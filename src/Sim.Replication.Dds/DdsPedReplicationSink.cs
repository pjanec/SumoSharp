using CycloneDDS.Runtime;
using Sim.Core;
using Sim.Replication;

namespace Sim.Replication.Dds;

// docs/PEDESTRIAN-DDS-TRANSPORT-DESIGN.md §4/§5 -- the live CycloneDDS binding of IPedReplicationSink, the
// ped mirror of DdsReplicationSink (the vehicle SEND side). Encode + DDS-write ONLY: every FrameCodec /
// ActivityTimelineWire byte here is byte-identical to InMemoryPedReplicationBus.SinkImpl -- the DDS layer
// transports the exact bytes the InMemory bus round-trips, adding no new codec or quantization.
//
// QoS (docs §4): the crowd frame is perishable per-step state -> VolatileLatest (BEST_EFFORT); the three
// durable per-ped topics (patharc, activity, lifecycle) -> DurableLatest (RELIABLE/TRANSIENT_LOCAL) so a
// late-joining IG gets the latest leg / DR-model per ped. A reader MUST use the matching profile (see
// DdsPedReplicationSource) -- a RELIABLE reader will not match a BEST_EFFORT writer.
//
// CRITICAL (see README.md): every writer is constructed with an EXPLICIT DdsTopicNames.Ped* string.
public sealed class DdsPedReplicationSink : IPedReplicationSink
{
    // Durable per-ped topics keep a small history depth so a rare burst of >1 leg per ped between a
    // subscriber's Pump() calls is not collapsed to a single sample (docs §4 "accumulation semantics").
    private const int DurableDepth = 16;

    private readonly DdsWriter<DdsPedCrowdFrame> _crowdWriter;
    private readonly DdsWriter<DdsPedLeg> _pathArcWriter;
    private readonly DdsWriter<DdsPedActivityLeg> _activityWriter;
    private readonly DdsWriter<DdsPedLifecycle> _lifecycleWriter;

    private byte[] _crowdScratch = new byte[DdsWire.MaxPayload];
    private byte[] _pathArcScratch = new byte[DdsWire.MaxPayload];

    // Cumulative bytes written per topic (valid payload prefixes only) -- a pure diagnostic for the
    // live-bandwidth readout (docs §6, P6-1 SC3), never read on the reconstruction path.
    public long CrowdBytesPublished { get; private set; }
    public long PathArcBytesPublished { get; private set; }
    public long ActivityBytesPublished { get; private set; }
    public long LifecycleBytesPublished { get; private set; }

    private readonly uint _source;
    private readonly bool _float64StartTimes;

    // H14: the ped indices with a live leg instance on each durable topic, so a despawn disposes exactly the
    // instances this writer registered (never an instance it never wrote).
    private readonly HashSet<uint> _pathArcInstances = new();
    private readonly HashSet<uint> _activityInstances = new();

    public DdsPedReplicationSink(DdsParticipant participant) : this(participant, source: 0, float64StartTimes: false)
    {
    }

    // HROT H14 (docs/HROT-INTEGRATION-DESIGN.md): `source` is stamped on EVERY sample (a key member of all four ped
    // topics, H8's vehicle pattern) so DdsMultiSourcePedSubscriber can demultiplex several publishers on one domain;
    // `float64StartTimes` encodes PathArc legs as FrameCodec kinds 6/7 (startTime as float64). Defaults 0 / false =
    // the 1-arg ctor, byte-identical payloads.
    public DdsPedReplicationSink(DdsParticipant participant, uint source, bool float64StartTimes = false)
    {
        _source = source;
        _float64StartTimes = float64StartTimes;
        _crowdWriter = new DdsWriter<DdsPedCrowdFrame>(participant);
        _pathArcWriter = new DdsWriter<DdsPedLeg>(participant);
        _activityWriter = new DdsWriter<DdsPedActivityLeg>(participant);
        _lifecycleWriter = new DdsWriter<DdsPedLifecycle>(participant);
    }

    // High-rate crowd frame: chunk by the shared byte budget (3640 peds / 64 KiB chunk at 18 B/record) and
    // DDS-write each chunk as a DdsWireFrame (Kind = KindPedFreeKinematic). Same chunk planning as the
    // vehicle sink; same FrameCodec bytes as the InMemory bus.
    public void PublishCrowdFrame(uint step, float time, ReadOnlySpan<PedFreeKinematicRecord> records)
    {
        if (records.Length == 0)
        {
            return;
        }

        var maxPerChunk = Math.Max(1, FrameChunker.MaxRecordsForPayload(DdsWire.MaxPayload, FrameCodec.PedFreeKinematicRecordSize));
        var totalChunks = FrameChunker.ChunkCount(records.Length, maxPerChunk);
        for (var c = 0; c < totalChunks; c++)
        {
            var (start, count) = FrameChunker.ChunkRange(c, records.Length, maxPerChunk);
            var chunk = records.Slice(start, count);
            var size = FrameCodec.WritePedFreeKinematicFrame(_crowdScratch, step, time, chunk);
            var frame = new DdsPedCrowdFrame();
            frame.SetPayload(FrameCodec.KindPedFreeKinematic, step, time, _crowdScratch.AsSpan(0, size), c);
            frame.Source = _source;
            _crowdWriter.Write(frame);
            CrowdBytesPublished += size;
        }
    }

    // Durable, once per PathArc leg: serialize the single record via FrameCodec (same bytes as the InMemory
    // bus) and DDS-write it keyed by the ped handle Index (latest leg per ped for late joiners).
    public void PublishPathArc(in PathArcRecord record)
    {
        var recs = new[] { record };
        var size = FrameCodec.WritePathArcFrame(_pathArcScratch, step: 0, time: 0f, recs, _float64StartTimes);
        var leg = new DdsPedLeg();
        leg.SetPayload(record.Handle.Index, record.Handle.Generation, _pathArcScratch.AsSpan(0, size));
        leg.Source = _source;
        _pathArcWriter.Write(leg);
        _pathArcInstances.Add(record.Handle.Index);
        PathArcBytesPublished += size;
    }

    // Durable, once per ActivityTimeline leg: the payload is ALREADY the opaque ActivityTimelineWire blob
    // (Sim.Replication never interprets it) -- just tag it with the owning ped's handle and DDS-write.
    public void PublishActivityTimeline(VehicleHandle handle, ReadOnlySpan<byte> timelineBytes)
    {
        var leg = new DdsPedActivityLeg();
        leg.SetPayload(handle.Index, handle.Generation, timelineBytes);
        leg.Source = _source;
        _activityWriter.Write(leg);
        _activityInstances.Add(handle.Index);
        ActivityBytesPublished += timelineBytes.Length;
    }

    // Durable, keyed: spawn/despawn + DR-model-switch lifecycle event, the ped analog of the vehicle
    // lifecycle write.
    public void PublishPedLifecycle(in PedLifecycleRecord record)
    {
        var lc = new DdsPedLifecycle
        {
            Index = record.Handle.Index,
            Generation = record.Handle.Generation,
            Kind = (byte)record.Kind,
            Time = record.Time,
            Name = DdsReplicationSink.ToFixedName(record.Name),
            Source = _source,
        };
        _lifecycleWriter.Write(lc);
        LifecycleBytesPublished += 15; // index(4)+gen(2)+kind(1)+time(8), matching the InMemory bus record

        // H14 S2: a despawned ped's durable instances are DISPOSED -- its PathArc / ActivityTimeline legs and, after
        // the Despawn sample above, its lifecycle instance -- so a late joiner is not handed a ped that is gone and the
        // writers' TRANSIENT_LOCAL history does not grow with every ped that ever lived. A live reader still takes the
        // Despawn sample first (written before the dispose).
        if (record.Kind == PedLifecycleKind.Despawn)
        {
            if (_pathArcInstances.Remove(record.Handle.Index))
            {
                _pathArcWriter.DisposeInstance(new DdsPedLeg { Index = record.Handle.Index, Generation = record.Handle.Generation, Source = _source });
            }

            if (_activityInstances.Remove(record.Handle.Index))
            {
                _activityWriter.DisposeInstance(new DdsPedActivityLeg { Index = record.Handle.Index, Generation = record.Handle.Generation, Source = _source });
            }

            _lifecycleWriter.DisposeInstance(lc);
        }
    }

    public uint Source => _source;

    public void Dispose()
    {
        _crowdWriter.Dispose();
        _pathArcWriter.Dispose();
        _activityWriter.Dispose();
        _lifecycleWriter.Dispose();
    }
}
