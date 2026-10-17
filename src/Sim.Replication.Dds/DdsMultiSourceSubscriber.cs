using System.Diagnostics;
using CycloneDDS.Runtime;
using CycloneDDS.Runtime.Tracking;
using Sim.Replication;

namespace Sim.Replication.Dds;

// docs/HROT-INTEGRATION-DESIGN.md H8 — the read side for SEVERAL publishers on one domain. Reads the same four
// SUMO topics DdsSubscriber reads, with the same QoS, and DEMULTIPLEXES every sample by its `Source` key
// (DdsReplicationSink's `source`) into one IReplicationSource per publisher: each with its own geometry,
// per-vehicle history, dims, names, tags and TL table (one DdsReplicationState each -- the SAME decode code
// DdsSubscriber runs, never a copy).
//
// A source APPEARS on its first valid sample. It DISAPPEARS when
//   (a) one of its instances reports NotAliveNoWriters / NotAliveDisposed -- every instance is keyed by
//       Source and each publisher has one writer per topic, so that is "this publisher's writers are gone";
//   (b) `sourceTimeoutSeconds > 0` and nothing has arrived from it for that long (OFF by default: an idle
//       sim publishes no frames, and a source that came back would not get its durable geometry again).
//
// Sender tracking (CycloneDDS.NET 0.3.2): when the participant has it enabled (`participant.SenderRegistry`
// non-null -- the HOST calls `participant.EnableSenderTracking(...)` before constructing this), every reader
// tracks senders and each source records the AppInstanceId of the participant that wrote its samples
// (`SenderAppInstanceOf`). The host uses that as the cars' OWNER; `Source` exists only so instances do not
// collide. Because the library resolves an arrived identity from an async loop, an early sample may carry no
// sender yet; the last publication handle is kept and retried on every query until it resolves.
//
// Threading: like DdsSubscriber -- Pump() and every read on ONE thread.
public sealed class DdsMultiSourceSubscriber : IDisposable
{
    private readonly DdsReader<DdsWireFrame> _vehicleReader;
    private readonly DdsReader<DdsNetworkGeometry> _geometryReader;
    private readonly DdsReader<DdsVehicleLifecycle> _lifecycleReader;
    private readonly DdsReader<DdsTlState> _tlReader;
    private readonly SenderRegistry? _senders;

    private readonly bool _stampWithSimTime;
    private readonly double _sourceTimeoutSeconds;
    private readonly Func<double> _clockSeconds;

    private readonly Dictionary<uint, Entry> _entries = new();
    private readonly Dictionary<uint, IReplicationSource> _sources = new();
    private readonly List<uint> _expiredScratch = new();

    private sealed class Entry
    {
        public Entry(DdsReplicationState state) => State = state;

        public DdsReplicationState State { get; }
        public double LastSeenSeconds;
        public long PublicationHandle;
        public int? SenderAppInstanceId;
        public bool Gone;
    }

    public DdsMultiSourceSubscriber(DdsParticipant participant)
        : this(participant, stampWithSimTime: false, sourceTimeoutSeconds: 0.0, clockSeconds: null)
    {
    }

    // `stampWithSimTime`: H3, applied per source exactly as DdsSubscriber applies it.
    // `sourceTimeoutSeconds`: > 0 removes a source that has sent nothing for that long; <= 0 (default) never.
    // `clockSeconds`: the timeout's clock (monotonic wall clock by default); injectable for tests.
    public DdsMultiSourceSubscriber(
        DdsParticipant participant, bool stampWithSimTime, double sourceTimeoutSeconds = 0.0, Func<double>? clockSeconds = null)
    {
        if (participant is null) throw new ArgumentNullException(nameof(participant));
        _stampWithSimTime = stampWithSimTime;
        _sourceTimeoutSeconds = sourceTimeoutSeconds;
        if (clockSeconds is null)
        {
            var sw = Stopwatch.StartNew();
            _clockSeconds = () => sw.Elapsed.TotalSeconds;
        }
        else
        {
            _clockSeconds = clockSeconds;
        }

        // Same topic names and QoS profiles as DdsSubscriber / DdsReplicationSink (a RELIABLE reader will not
        // match a BEST_EFFORT writer -- DdsQos's remark).
        _vehicleReader = new DdsReader<DdsWireFrame>(participant, DdsTopicNames.Vehicles, DdsQos.VolatileLatest());
        _geometryReader = new DdsReader<DdsNetworkGeometry>(participant, DdsTopicNames.Geometry, DdsQos.DurableLatest());
        _lifecycleReader = new DdsReader<DdsVehicleLifecycle>(participant, DdsTopicNames.Lifecycle, DdsQos.DurableLatest());
        _tlReader = new DdsReader<DdsTlState>(participant, DdsTopicNames.Tl, DdsQos.VolatileLatest());

        _senders = participant.SenderRegistry;
        if (_senders is not null)
        {
            _vehicleReader.EnableSenderTracking(_senders);
            _geometryReader.EnableSenderTracking(_senders);
            _lifecycleReader.EnableSenderTracking(_senders);
            _tlReader.EnableSenderTracking(_senders);
        }
    }

    // One IReplicationSource per live publisher, keyed by its `Source`. A source's Pump() pumps ALL of them.
    public IReadOnlyDictionary<uint, IReplicationSource> Sources => _sources;

    // Diagnostics: how many sources have appeared / been removed over this reader's life.
    public long SourcesAppeared { get; private set; }

    public long SourcesRemoved { get; private set; }

    // True while any of the four readers is matched with at least one live writer.
    public bool Connected =>
        _vehicleReader.CurrentStatus.CurrentCount > 0
        || _lifecycleReader.CurrentStatus.CurrentCount > 0
        || _geometryReader.CurrentStatus.CurrentCount > 0
        || _tlReader.CurrentStatus.CurrentCount > 0;

    // The DDS-tracked sender of `source`'s samples: the AppInstanceId its participant announced through
    // EnableSenderTracking. Null when this participant does not track senders, the source is unknown, or
    // the sender's identity has not resolved yet (retried on every call).
    public int? SenderAppInstanceOf(uint source)
    {
        if (!_entries.TryGetValue(source, out var e))
        {
            return null;
        }

        if (e.SenderAppInstanceId is null && _senders is not null && e.PublicationHandle != 0
            && _senders.TryGetIdentity(e.PublicationHandle, out var id))
        {
            e.SenderAppInstanceId = id.AppInstanceId;
        }

        return e.SenderAppInstanceId;
    }

    // Drain all four topics once (same order as DdsSubscriber: geometry, lifecycle, vehicles, TL), then
    // apply the inactivity timeout.
    public void Pump()
    {
        var now = _clockSeconds();
        PumpGeometry(now);
        PumpLifecycle(now);
        PumpVehicles(now);
        PumpTl(now);
        ExpireIdle(now);
    }

    // Each pump: a valid sample is routed to its source's state; then, valid or not, an instance whose state
    // says its writers are gone (NotAliveNoWriters) or it was disposed removes that source. Cyclone carries an
    // instance-state change on the next unread sample when there is one, and generates an invalid (key-only)
    // sample only when there is none -- so both cases are checked. An invalid sample's key is read off the
    // native sample, the same way HROT's EntityMaster ingress reads a disposed key.
    private void PumpGeometry(double now)
    {
        using var loan = _geometryReader.Take(maxSamples: 16);
        var index = -1;
        foreach (var sample in loan)
        {
            index++;
            var info = sample.Info;
            uint source;
            if (sample.IsValid)
            {
                var f = sample.Data;
                source = f.Source;
                var e = Touch(source, now);
                if (NeedsSender(e, info.PublicationHandle)) NoteSender(e, info.PublicationHandle, loan.GetSender(index));
                e.State.ApplyGeometry(in f);
            }
            else
            {
                if (!IsGone(info.InstanceState)) continue;
                source = DdsTypeSupport.FromNative<DdsNetworkGeometry>(sample.NativePtr).Source;
            }

            if (IsGone(info.InstanceState)) Remove(source);
        }
    }

    private void PumpLifecycle(double now)
    {
        using var loan = _lifecycleReader.Take(maxSamples: 256);
        var index = -1;
        foreach (var sample in loan)
        {
            index++;
            var info = sample.Info;
            uint source;
            if (sample.IsValid)
            {
                var d = sample.Data;
                source = d.Source;
                var e = Touch(source, now);
                if (NeedsSender(e, info.PublicationHandle)) NoteSender(e, info.PublicationHandle, loan.GetSender(index));
                e.State.ApplyLifecycle(in d);
            }
            else
            {
                if (!IsGone(info.InstanceState)) continue;
                source = DdsTypeSupport.FromNative<DdsVehicleLifecycle>(sample.NativePtr).Source;
            }

            if (IsGone(info.InstanceState)) Remove(source);
        }
    }

    private void PumpVehicles(double now)
    {
        using var loan = _vehicleReader.Take(maxSamples: 64);
        var index = -1;
        foreach (var sample in loan)
        {
            index++;
            var info = sample.Info;
            uint source;
            if (sample.IsValid)
            {
                var f = sample.Data;
                source = f.Source;
                var e = Touch(source, now);
                if (NeedsSender(e, info.PublicationHandle)) NoteSender(e, info.PublicationHandle, loan.GetSender(index));
                e.State.ApplyVehicleFrame(in f, info.SourceTimestamp);
            }
            else
            {
                if (!IsGone(info.InstanceState)) continue;
                source = DdsTypeSupport.FromNative<DdsWireFrame>(sample.NativePtr).Source;
            }

            if (IsGone(info.InstanceState)) Remove(source);
        }
    }

    private void PumpTl(double now)
    {
        using var loan = _tlReader.Take(maxSamples: 8);
        var index = -1;
        foreach (var sample in loan)
        {
            index++;
            var info = sample.Info;
            uint source;
            if (sample.IsValid)
            {
                var f = sample.Data;
                source = f.Source;
                var e = Touch(source, now);
                if (NeedsSender(e, info.PublicationHandle)) NoteSender(e, info.PublicationHandle, loan.GetSender(index));
                e.State.ApplyTl(in f);
            }
            else
            {
                if (!IsGone(info.InstanceState)) continue;
                source = DdsTypeSupport.FromNative<DdsTlState>(sample.NativePtr).Source;
            }

            if (IsGone(info.InstanceState)) Remove(source);
        }
    }

    private static bool IsGone(DdsInstanceState state) =>
        state == DdsInstanceState.NotAliveNoWriters || state == DdsInstanceState.NotAliveDisposed;

    // The source's entry, created on its first valid sample, with its last-seen time refreshed.
    private Entry Touch(uint source, double now)
    {
        if (!_entries.TryGetValue(source, out var e))
        {
            Entry? created = null;
            var state = new DdsReplicationState(_stampWithSimTime, Pump, () => created is { Gone: false });
            created = new Entry(state);
            e = created;
            _entries[source] = e;
            _sources[source] = state;
            SourcesAppeared++;
        }

        e.LastSeenSeconds = now;
        return e;
    }

    // Resolve the sender only while tracking is on and it is unknown or the writer changed (one lookup per
    // writer, not per sample).
    private bool NeedsSender(Entry e, long publicationHandle) =>
        _senders is not null && (e.SenderAppInstanceId is null || e.PublicationHandle != publicationHandle);

    private static void NoteSender(Entry e, long publicationHandle, SenderIdentity? who)
    {
        e.PublicationHandle = publicationHandle;
        e.SenderAppInstanceId = who is { } id ? id.AppInstanceId : null;
    }

    private void ExpireIdle(double now)
    {
        if (_sourceTimeoutSeconds <= 0.0 || _entries.Count == 0)
        {
            return;
        }

        _expiredScratch.Clear();
        foreach (var kv in _entries)
        {
            if (now - kv.Value.LastSeenSeconds > _sourceTimeoutSeconds)
            {
                _expiredScratch.Add(kv.Key);
            }
        }

        foreach (var source in _expiredScratch)
        {
            Remove(source);
        }
    }

    private void Remove(uint source)
    {
        if (_entries.TryGetValue(source, out var e))
        {
            e.Gone = true;
            _entries.Remove(source);
            _sources.Remove(source);
            SourcesRemoved++;
        }
    }

    public void Dispose()
    {
        _vehicleReader.Dispose();
        _geometryReader.Dispose();
        _lifecycleReader.Dispose();
        _tlReader.Dispose();
    }
}
