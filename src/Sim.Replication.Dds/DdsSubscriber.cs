using CycloneDDS.Runtime;
using Sim.Core;
using Sim.Replication;

namespace Sim.Replication.Dds;

// docs/DEMO-CITY3D-DESIGN.md "DRY rewire (parity-neutral)": the DDS binding's own IReplicationSource,
// moved here verbatim from src/Sim.Viewer.Raylib/DdsSubscriber.cs -- the read-side counterpart of
// DdsReplicationSink (this file's sibling), so both halves of the DDS transport live in the one binding
// package instead of the read half sitting in the heavy raylib viewer package. Nothing about topics, QoS,
// decode logic, or public surface changed by this move.
//
// docs/SUMOSHARP-NATIVE-VIEWER.md P2 — the read side of the native viewer's DDS data path. Decodes the four
// topics DdsPublisher writes into plain in-memory state a renderer (P2b) or this phase's LoopbackSelfTest
// can inspect: static lane geometry (once), a short per-vehicle history (newest last, for later
// interpolation/DR), TL state, and lifecycle dims. No rendering, no DrClock/extrapolation here.
//
// CRITICAL (see Sim.Replication.Dds/README.md): every reader below is constructed with the SAME explicit
// DdsTopicNames.* string the matching DdsPublisher writer used.
// docs/SUMOSHARP-PACKAGING-DESIGN.md P1 (D8): implements the transport-neutral IReplicationSource so a
// consumer can be coded against Sim.Replication alone; the DDS-specific members below are unchanged.
public sealed class DdsSubscriber : IReplicationSource
{
    // Per-vehicle sample timestamped with the DDS sample's own source timestamp when the reader exposes one
    // (CycloneDDS.NET's DdsSampleInfo.SourceTimestamp, nanoseconds since the Unix epoch); falls back to the
    // frame's own sim-time field on the (untested-in-practice) chance the timestamp comes back <= 0. This is
    // what a later DrClock paces its render clock off (§8 of SUMOSHARP-DEADRECKONING.md): wall-clock arrival
    // time, not sim time, is what a REMOTE subscriber actually has to reconstruct a rate from. Packaging P2-B
    // (docs/SUMOSHARP-PACKAGING-DESIGN.md §5): this is now the transport-neutral Sim.Replication.
    // TimestampedSample (identical field-for-field, same ctor order) rather than a DDS-local type, so DrClock
    // no longer needs to know this class exists.
    private readonly DdsReader<DdsWireFrame> _vehicleReader;
    private readonly DdsReader<DdsNetworkGeometry> _geometryReader;
    private readonly DdsReader<DdsVehicleLifecycle> _lifecycleReader;
    private readonly DdsReader<DdsTlState> _tlReader;

    // docs/HROT-INTEGRATION-DESIGN.md H8: the decode/accumulate logic (geometry, history, dims, names, tags,
    // TL) lives in DdsReplicationState so DdsMultiSourceSubscriber can keep one per publisher. This reader
    // keeps exactly ONE and feeds it every sample regardless of its `Source` key -- unchanged behaviour.
    private readonly DdsReplicationState _state;

    // Must mirror DdsPublisher's per-topic QoS profile exactly (a RELIABLE reader will not match a
    // BEST_EFFORT writer) -- see DdsQos's doc comment.
    public DdsSubscriber(DdsParticipant participant) : this(participant, stampWithSimTime: false)
    {
    }

    // docs/HROT-INTEGRATION-DESIGN.md H3: `stampWithSimTime` stamps every vehicle sample with the frame's own
    // SIM time instead of the DDS SourceTimestamp (wall clock). A host whose render clock IS the sim clock
    // (pausable, scalable, seekable -- e.g. HROT's cluster time) resolves poses with DrClock.ResolveAt at
    // that sim time, so wall-clock arrival stamps would keep extrapolating through a pause. Default false
    // keeps the existing wall-clock behaviour byte-for-byte.
    public DdsSubscriber(DdsParticipant participant, bool stampWithSimTime)
    {
        _vehicleReader = new DdsReader<DdsWireFrame>(participant, DdsTopicNames.Vehicles, DdsQos.VolatileLatest(DdsQos.SparseStreamDepth));
        _geometryReader = new DdsReader<DdsNetworkGeometry>(participant, DdsTopicNames.Geometry, DdsQos.DurableLatest());
        _lifecycleReader = new DdsReader<DdsVehicleLifecycle>(participant, DdsTopicNames.Lifecycle, DdsQos.DurableLatest());
        _tlReader = new DdsReader<DdsTlState>(participant, DdsTopicNames.Tl, DdsQos.VolatileLatest());
        _state = new DdsReplicationState(stampWithSimTime, Pump, () => Connected);
    }

    // Drop all per-vehicle state (history + dims + latest-sample-time) so it rebuilds from the live stream.
    // Called on a publisher RESTART: the publisher rebuilds at t=0 and REUSES vehicle handle indices, so a
    // reused handle's buffer would otherwise mix old-timeline samples (large Pos) with new ones (small Pos)
    // -> the DR flings that vehicle backward instead of the old one vanishing. Geometry + TL are kept (the
    // network didn't change).
    public void ResetVehicles() => _state.ResetVehicles();

    // Decoded static lane geometry, keyed by lane handle. Empty until PublishGeometryOnce's chunk(s) have
    // arrived; see GeometryComplete.
    public IReadOnlyDictionary<int, GeometryCodec.LaneGeo> Geometry => _state.Geometry;

    // True once every announced chunk (0..TotalChunks-1) has been seen at least once.
    public bool GeometryComplete => _state.GeometryComplete;

    // Per-vehicle history, newest sample last, capped at 8 -- the raw material a later DR/interpolation
    // consumer (P2b/DrClock) walks. Exposed as the transport-neutral IVehicleSampleHistory (Sim.Replication)
    // so a consumer never needs this class's concrete type.
    public IReadOnlyDictionary<VehicleHandle, IVehicleSampleHistory> History => _state.History;

    // Dims announced over the lifecycle topic (spawn), removed on despawn.
    public IReadOnlyDictionary<VehicleHandle, (float Length, float Width)> Dims => _state.Dims;

    // SUMO ids announced over the lifecycle topic (spawn), removed on despawn.
    public IReadOnlyDictionary<VehicleHandle, string> Names => _state.Names;

    // H7: external type + colour announced over the lifecycle topic (spawn, non-empty only), removed on despawn.
    public IReadOnlyDictionary<VehicleHandle, VehicleTag> Tags => _state.Tags;

    // Latest TL signal char per controlled lane handle.
    public IReadOnlyDictionary<int, byte> TlStateByLane => _state.TlStateByLane;

    // P2b (DrClock) — the newest per-vehicle sample timestamp seen so far across every decoded vehicle
    // frame (max of TimestampedSample.TimestampSeconds); null until the first vehicle frame arrives. All
    // vehicle records decoded from the SAME DDS sample share one timestamp, so this is exactly the
    // "current frame's sim-axis time" DrClock.Pump paces its render clock off.
    public double? LatestVehicleSampleTime => _state.LatestVehicleSampleTime;

    // P2b diagnostics — total vehicle records decoded across every Pump call so far (a monotonically
    // increasing counter; divide by elapsed wall time for a "DDS samples/s" HUD readout).
    public long TotalVehicleSamplesReceived => _state.TotalVehicleSamplesReceived;

    // P3 ("remote mode + QoS") — true once THIS reader is currently matched with at least one live writer
    // on the Vehicles topic (DDS's own publication-matched bookkeeping, not an inference from whether data
    // has arrived yet), so a remote viewer's "connected: yes/no" indicator reflects the transport itself,
    // not just "have I decoded a frame". A publisher that starts, publishes once, then is killed will show
    // CurrentCount drop back to 0 here even though TotalVehicleSamplesReceived stays nonzero.
    public bool Connected => _vehicleReader.CurrentStatus.CurrentCount > 0;

    public bool TryGetLatest(VehicleHandle handle, out TimestampedSample sample) => _state.TryGetLatest(handle, out sample);

    // Pump every topic once. Call this at whatever cadence the host polls DDS (the loopback self-test polls
    // once per publish step).
    public void Pump()
    {
        PumpGeometry();
        PumpLifecycle();
        PumpVehicles();
        PumpTl();
    }

    private void PumpGeometry()
    {
        using var loan = _geometryReader.Take(maxSamples: 16);
        foreach (var sample in loan)
        {
            if (!sample.IsValid)
            {
                continue;
            }

            var f = sample.Data;
            _state.ApplyGeometry(in f);
        }
    }

    private void PumpLifecycle()
    {
        using var loan = _lifecycleReader.Take(maxSamples: 256);
        foreach (var sample in loan)
        {
            if (!sample.IsValid)
            {
                continue;
            }

            var d = sample.Data;
            _state.ApplyLifecycle(in d);
        }
    }

    private void PumpVehicles()
    {
        using var loan = _vehicleReader.Take(maxSamples: 64);
        foreach (var sample in loan)
        {
            if (!sample.IsValid)
            {
                continue;
            }

            var f = sample.Data;
            _state.ApplyVehicleFrame(in f, sample.Info.SourceTimestamp);
        }
    }

    private void PumpTl()
    {
        using var loan = _tlReader.Take(maxSamples: 8);
        foreach (var sample in loan)
        {
            if (!sample.IsValid)
            {
                continue;
            }

            var f = sample.Data;
            _state.ApplyTl(in f);
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
