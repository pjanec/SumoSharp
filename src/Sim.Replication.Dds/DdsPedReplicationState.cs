using Sim.Core;
using Sim.Replication;

namespace Sim.Replication.Dds;

// docs/HROT-INTEGRATION-DESIGN.md H14 -- the ped decode/accumulate logic (crowd frame, PathArc legs, ActivityTimeline
// legs, lifecycle) behind an IPedReplicationSource, factored out of DdsPedReplicationSource exactly as H8 factored
// DdsReplicationState out of DdsSubscriber: DdsPedReplicationSource keeps ONE and feeds it every sample;
// DdsMultiSourcePedSubscriber keeps one per publishing `Source`. The same decode code either way, never a copy.
//
// Times are SIM times end to end: the crowd frame carries its step's sim time, a leg carries its start time, a
// lifecycle record its event time. Nothing here reads a DDS SourceTimestamp, so (unlike the vehicle stream, where H3
// made sim-time stamping an opt-in) a ped receiver is ALWAYS on the publisher's sim clock.
public sealed class DdsPedReplicationState : IPedReplicationSource
{
    private readonly Action _pump;

    private uint _latestCrowdStep;
    private float _latestCrowdTime;
    private bool _haveCrowd;
    private readonly List<PedFreeKinematicRecord> _latestCrowdFrame = new();
    private readonly List<PathArcRecord> _pathArcs = new();
    private readonly List<(VehicleHandle Handle, byte[] TimelineBytes)> _timelines = new();
    private readonly List<PedLifecycleRecord> _lifecycles = new();

    private PedFreeKinematicRecord[] _crowdRecs = new PedFreeKinematicRecord[4096];

    // `pump` is what this source's Pump() does -- the owning reader's drain (a no-op for a per-source state the
    // owner drains itself).
    public DdsPedReplicationState(Action pump) => _pump = pump ?? throw new ArgumentNullException(nameof(pump));

    public void Pump() => _pump();

    public uint LatestCrowdStep => _latestCrowdStep;
    public float LatestCrowdTime => _latestCrowdTime;
    public IReadOnlyList<PedFreeKinematicRecord> LatestCrowdFrame => _latestCrowdFrame;
    public IReadOnlyList<PathArcRecord> PathArcs => _pathArcs;
    public IReadOnlyList<(VehicleHandle Handle, byte[] TimelineBytes)> ActivityTimelines => _timelines;
    public IReadOnlyList<PedLifecycleRecord> Lifecycles => _lifecycles;

    // H14: the newest sim time any record of this source carried (crowd frame, leg start, lifecycle event); null
    // before the first. A reconstructor's "latest server time".
    public double? LatestSimTime { get; private set; }

    private void Note(double t)
    {
        if (LatestSimTime is null || t > LatestSimTime.Value)
        {
            LatestSimTime = t;
        }
    }

    // Keep the newest step's records. Rule per decoded sample: step > latest -> fresh frame; step == latest ->
    // append (this step's other chunks); step < latest -> stale, skip.
    public void ApplyCrowd(ReadOnlySpan<byte> payload)
    {
        var header = FrameCodec.ReadHeader(payload);
        if (_crowdRecs.Length < header.Count)
        {
            _crowdRecs = new PedFreeKinematicRecord[Math.Max(header.Count, _crowdRecs.Length * 2)];
        }

        var count = FrameCodec.ReadPedFreeKinematicFrame(payload, _crowdRecs);
        if (!_haveCrowd || header.Step > _latestCrowdStep)
        {
            _haveCrowd = true;
            _latestCrowdStep = header.Step;
            _latestCrowdTime = header.Time;
            _latestCrowdFrame.Clear();
        }
        else if (header.Step < _latestCrowdStep)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            _latestCrowdFrame.Add(_crowdRecs[i]);
        }

        Note(header.Time);
    }

    public void ApplyPathArc(ReadOnlySpan<byte> payload)
    {
        var recs = FrameCodec.ReadPathArcFrame(payload);
        _pathArcs.AddRange(recs);
        foreach (var r in recs)
        {
            Note(r.StartTime);
        }
    }

    // Opaque bytes: only the Sim.Pedestrians bridge (which owns ActivityTimelineWire) decodes them.
    public void ApplyActivity(VehicleHandle handle, ReadOnlySpan<byte> payload) =>
        _timelines.Add((handle, payload.ToArray()));

    public void ApplyLifecycle(in PedLifecycleRecord record)
    {
        _lifecycles.Add(record);
        Note(record.Time);
    }

    public void Dispose()
    {
    }
}
