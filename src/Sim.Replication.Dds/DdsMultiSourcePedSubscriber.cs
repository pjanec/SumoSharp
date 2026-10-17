using System.Diagnostics;
using CycloneDDS.Runtime;
using CycloneDDS.Runtime.Tracking;
using Sim.Core;
using Sim.Pedestrians.Lod;
using Sim.Replication;

namespace Sim.Replication.Dds;

// docs/HROT-INTEGRATION-DESIGN.md H14 -- the pedestrian counterpart of H8's DdsMultiSourceSubscriber: reads the four
// ped topics (crowd, PathArc legs, ActivityTimeline legs, lifecycle) with DdsPedReplicationSource's QoS and
// DEMULTIPLEXES every sample by its `Source` key (DdsPedReplicationSink's `source`) into one DdsPedReplicationState per
// publisher (the SAME decode code DdsPedReplicationSource runs), each wrapped in its own PedRemoteReconstructor -- so a
// host reads each publisher's live peds, by name, at a sim time of its choosing.
//
// A source APPEARS on its first valid sample. It DISAPPEARS when
//   (a) one of its instances reports NotAliveNoWriters -- each publisher has one writer per topic, so that is "this
//       publisher's writers are gone". ⚠ Unlike the vehicle reader, NotAliveDisposed does NOT remove the source: a
//       ped sink DISPOSES a despawned ped's instances (H14 S2), so a disposed instance is one ped leaving, and the
//       Despawn sample written before the dispose is what drops it from the reconstructor;
//   (b) `sourceTimeoutSeconds > 0` and nothing has arrived from it for that long (off by default). ⚠ A ped stream can
//       be legitimately silent (low-power peds send nothing between legs), so a timeout must exceed the longest
//       quiet spell the host tolerates.
//
// Times: everything a ped record carries is SIM time (DdsPedReplicationState), and PedRemoteReconstructor.TryGetPoseAt
// / CopyLivePeds take the host's sim time explicitly -- there is no wall-clock stamp to opt out of (H3's
// `stampWithSimTime` exists for vehicles only because their history is stamped with the DDS SourceTimestamp).
// Each reconstructor is pumped with its source's newest record time (for its own RenderTime).
//
// Sender tracking: exactly H8's -- when the participant tracks senders, `SenderAppInstanceOf(source)` is the
// AppInstanceId of the participant that wrote that source's samples. Threading: Pump() and every read on ONE thread.
public sealed class DdsMultiSourcePedSubscriber : IDisposable
{
    private readonly DdsReader<DdsPedCrowdFrame> _crowdReader;
    private readonly DdsReader<DdsPedLeg> _pathArcReader;
    private readonly DdsReader<DdsPedActivityLeg> _activityReader;
    private readonly DdsReader<DdsPedLifecycle> _lifecycleReader;
    private readonly SenderRegistry? _senders;

    private readonly double _sourceTimeoutSeconds;
    private readonly Func<double> _clockSeconds;
    private readonly double _playoutDelaySeconds;

    private readonly Dictionary<uint, Entry> _entries = new();
    private readonly Dictionary<uint, PedRemoteReconstructor> _sources = new();
    private readonly Dictionary<uint, IPedReplicationSource> _rawSources = new();
    private readonly List<uint> _expiredScratch = new();

    private readonly byte[] _bytes = new byte[DdsWire.MaxPayload];

    private sealed class Entry
    {
        public Entry(DdsPedReplicationState state, PedRemoteReconstructor reconstructor)
        {
            State = state;
            Reconstructor = reconstructor;
        }

        public DdsPedReplicationState State { get; }
        public PedRemoteReconstructor Reconstructor { get; }
        public double LastSeenSeconds;
        public long PublicationHandle;
        public int? SenderAppInstanceId;
    }

    // `sourceTimeoutSeconds`: > 0 removes a source that has sent nothing for that long; <= 0 (default) never.
    // `clockSeconds`: the timeout's clock (monotonic wall clock by default); injectable for tests.
    // `playoutDelaySeconds`: each reconstructor's render delay (only TryGetRenderPose uses it).
    public DdsMultiSourcePedSubscriber(
        DdsParticipant participant,
        double sourceTimeoutSeconds = 0.0,
        Func<double>? clockSeconds = null,
        double playoutDelaySeconds = PedRemoteReconstructor.DefaultPlayoutDelaySeconds)
    {
        if (participant is null) throw new ArgumentNullException(nameof(participant));
        _sourceTimeoutSeconds = sourceTimeoutSeconds;
        _playoutDelaySeconds = playoutDelaySeconds;
        if (clockSeconds is null)
        {
            var sw = Stopwatch.StartNew();
            _clockSeconds = () => sw.Elapsed.TotalSeconds;
        }
        else
        {
            _clockSeconds = clockSeconds;
        }

        // Same topic types (names + QoS on the type, H11) as DdsPedReplicationSource / DdsPedReplicationSink.
        _crowdReader = new DdsReader<DdsPedCrowdFrame>(participant);
        _pathArcReader = new DdsReader<DdsPedLeg>(participant);
        _activityReader = new DdsReader<DdsPedActivityLeg>(participant);
        _lifecycleReader = new DdsReader<DdsPedLifecycle>(participant);

        _senders = participant.SenderRegistry;
        if (_senders is not null)
        {
            _crowdReader.EnableSenderTracking(_senders);
            _pathArcReader.EnableSenderTracking(_senders);
            _activityReader.EnableSenderTracking(_senders);
            _lifecycleReader.EnableSenderTracking(_senders);
        }
    }

    // One reconstructor per live publisher, keyed by its `Source` -- read names and poses through it
    // (TryGetName / TryGetId / TryGetPoseAt / CopyLivePeds). Pumped by this subscriber's Pump().
    public IReadOnlyDictionary<uint, PedRemoteReconstructor> Sources => _sources;

    // The decoded wire state behind each reconstructor (the same keys), for a caller that wants the raw records.
    public IReadOnlyDictionary<uint, IPedReplicationSource> RawSources => _rawSources;

    public long SourcesAppeared { get; private set; }

    public long SourcesRemoved { get; private set; }

    public bool Connected =>
        _crowdReader.CurrentStatus.CurrentCount > 0
        || _pathArcReader.CurrentStatus.CurrentCount > 0
        || _activityReader.CurrentStatus.CurrentCount > 0
        || _lifecycleReader.CurrentStatus.CurrentCount > 0;

    // H8's contract: the DDS-tracked sender of `source`'s samples, null when untracked/unknown/unresolved (retried).
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

    // Drain the four topics (legs before lifecycle, so a Despawn in the same batch wins), apply the inactivity
    // timeout, then pump every source's reconstructor.
    public void Pump()
    {
        var now = _clockSeconds();
        PumpPathArc(now);
        PumpActivity(now);
        PumpLifecycle(now);
        PumpCrowd(now);
        ExpireIdle(now);

        foreach (var e in _entries.Values)
        {
            e.Reconstructor.Pump(e.State.LatestSimTime ?? 0.0);
        }
    }

    private void PumpCrowd(double now)
    {
        using var loan = _crowdReader.Take(maxSamples: 64);
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
                if (f.Kind == FrameCodec.KindPedFreeKinematic)
                {
                    var e = Touch(source, now, info.PublicationHandle, loan, index);
                    var n = f.ReadPayload(_bytes);
                    e.State.ApplyCrowd(_bytes.AsSpan(0, n));
                }
            }
            else
            {
                if (info.InstanceState != DdsInstanceState.NotAliveNoWriters) continue;
                source = DdsTypeSupport.FromNative<DdsPedCrowdFrame>(sample.NativePtr).Source;
            }

            if (info.InstanceState == DdsInstanceState.NotAliveNoWriters) Remove(source);
        }
    }

    private void PumpPathArc(double now)
    {
        using var loan = _pathArcReader.Take(maxSamples: 256);
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
                var e = Touch(source, now, info.PublicationHandle, loan, index);
                var n = d.ReadPayload(_bytes);
                e.State.ApplyPathArc(_bytes.AsSpan(0, n));
            }
            else
            {
                if (info.InstanceState != DdsInstanceState.NotAliveNoWriters) continue;
                source = DdsTypeSupport.FromNative<DdsPedLeg>(sample.NativePtr).Source;
            }

            if (info.InstanceState == DdsInstanceState.NotAliveNoWriters) Remove(source);
        }
    }

    private void PumpActivity(double now)
    {
        using var loan = _activityReader.Take(maxSamples: 256);
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
                var e = Touch(source, now, info.PublicationHandle, loan, index);
                var n = d.ReadPayload(_bytes);
                e.State.ApplyActivity(new VehicleHandle(d.Index, d.Generation), _bytes.AsSpan(0, n));
            }
            else
            {
                if (info.InstanceState != DdsInstanceState.NotAliveNoWriters) continue;
                source = DdsTypeSupport.FromNative<DdsPedActivityLeg>(sample.NativePtr).Source;
            }

            if (info.InstanceState == DdsInstanceState.NotAliveNoWriters) Remove(source);
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
                var e = Touch(source, now, info.PublicationHandle, loan, index);
                e.State.ApplyLifecycle(new PedLifecycleRecord(
                    new VehicleHandle(d.Index, d.Generation), (PedLifecycleKind)d.Kind, d.Time, d.Name.ToString()));
            }
            else
            {
                if (info.InstanceState != DdsInstanceState.NotAliveNoWriters) continue;
                source = DdsTypeSupport.FromNative<DdsPedLifecycle>(sample.NativePtr).Source;
            }

            if (info.InstanceState == DdsInstanceState.NotAliveNoWriters) Remove(source);
        }
    }

    // The source's entry, created on its first valid sample, with its last-seen time refreshed and (when tracking)
    // its sender noted once per writer.
    private Entry Touch<T>(uint source, double now, long publicationHandle, DdsLoan<T> loan, int index)
        where T : struct
    {
        if (!_entries.TryGetValue(source, out var e))
        {
            var state = new DdsPedReplicationState(static () => { }); // this subscriber drains; the source's Pump is a no-op
            e = new Entry(state, new PedRemoteReconstructor(state, _playoutDelaySeconds));
            _entries[source] = e;
            _sources[source] = e.Reconstructor;
            _rawSources[source] = state;
            SourcesAppeared++;
        }

        e.LastSeenSeconds = now;
        if (_senders is not null && (e.SenderAppInstanceId is null || e.PublicationHandle != publicationHandle))
        {
            e.PublicationHandle = publicationHandle;
            e.SenderAppInstanceId = loan.GetSender(index) is { } who ? who.AppInstanceId : null;
        }

        return e;
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
        if (_entries.Remove(source))
        {
            _sources.Remove(source);
            _rawSources.Remove(source);
            SourcesRemoved++;
        }
    }

    public void Dispose()
    {
        _crowdReader.Dispose();
        _pathArcReader.Dispose();
        _activityReader.Dispose();
        _lifecycleReader.Dispose();
    }
}
