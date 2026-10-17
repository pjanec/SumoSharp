using CycloneDDS.Runtime;
using Sim.Core;
using Sim.Replication;

namespace Sim.Replication.Dds;

// docs/PEDESTRIAN-DDS-TRANSPORT-DESIGN.md §4/§5 -- the live CycloneDDS binding of IPedReplicationSource, the
// ped mirror of DdsSubscriber (the vehicle RECEIVE side). Pump()-then-read: every topic is drained on
// Pump(), decoded with the SAME FrameCodec the InMemory bus uses, into the transport-neutral state a
// PedReplicationReceiver (HeadlessIg) reconstructs from. The DDS layer interprets no ActivityTimeline bytes
// -- it hands them back opaque, exactly as InMemoryPedReplicationBus does.
//
// Reader QoS MUST mirror the sink's per-topic profile exactly (a RELIABLE reader will not match a
// BEST_EFFORT writer): crowd = VolatileLatest; patharc/activity/lifecycle = DurableLatest.
//
// CRITICAL (see README.md): every reader is constructed with the SAME explicit DdsTopicNames.Ped* string
// the matching sink writer used.
public sealed class DdsPedReplicationSource : IPedReplicationSource
{
    private const int DurableDepth = 16;

    private readonly DdsReader<DdsPedCrowdFrame> _crowdReader;
    private readonly DdsReader<DdsPedLeg> _pathArcReader;
    private readonly DdsReader<DdsPedActivityLeg> _activityReader;
    private readonly DdsReader<DdsPedLifecycle> _lifecycleReader;

    // H14: the decode/accumulate state lives in DdsPedReplicationState (shared with DdsMultiSourcePedSubscriber); this
    // reader keeps exactly ONE and feeds it every sample regardless of its `Source` key -- unchanged behaviour.
    private readonly DdsPedReplicationState _state;

    // Decode scratch (sized to the max payload; the ped frames are far smaller in practice).
    private byte[] _crowdBytes = new byte[DdsWire.MaxPayload];
    private byte[] _legBytes = new byte[DdsWire.MaxPayload];

    public DdsPedReplicationSource(DdsParticipant participant)
    {
        _crowdReader = new DdsReader<DdsPedCrowdFrame>(participant);
        _pathArcReader = new DdsReader<DdsPedLeg>(participant);
        _activityReader = new DdsReader<DdsPedActivityLeg>(participant);
        _lifecycleReader = new DdsReader<DdsPedLifecycle>(participant);
        _state = new DdsPedReplicationState(Pump);
    }

    public uint LatestCrowdStep => _state.LatestCrowdStep;
    public float LatestCrowdTime => _state.LatestCrowdTime;
    public IReadOnlyList<PedFreeKinematicRecord> LatestCrowdFrame => _state.LatestCrowdFrame;
    public IReadOnlyList<PathArcRecord> PathArcs => _state.PathArcs;
    public IReadOnlyList<(VehicleHandle Handle, byte[] TimelineBytes)> ActivityTimelines => _state.ActivityTimelines;
    public IReadOnlyList<PedLifecycleRecord> Lifecycles => _state.Lifecycles;

    // H14: the newest sim time any ped record carried (see DdsPedReplicationState.LatestSimTime).
    public double? LatestSimTime => _state.LatestSimTime;

    // P6-1/remote-view diagnostic (mirrors DdsSubscriber.Connected): true once this crowd reader is matched
    // with at least one live writer -- the transport-level "connected", not "have I decoded a frame".
    public bool Connected => _crowdReader.CurrentStatus.CurrentCount > 0;

    public void Pump()
    {
        PumpCrowd();
        PumpPathArc();
        PumpActivity();
        PumpLifecycle();
    }

    // Keep the newest step's records. Rule per decoded sample (docs §4): step > latest -> fresh frame;
    // step == latest -> append (this step's other chunks); step < latest -> stale, skip. Handles chunks
    // arriving in any order within a Take and split across Pump() calls.
    private void PumpCrowd()
    {
        using var loan = _crowdReader.Take(maxSamples: 64);
        foreach (var sample in loan)
        {
            if (!sample.IsValid)
            {
                continue;
            }

            var f = sample.Data;
            if (f.Kind != FrameCodec.KindPedFreeKinematic)
            {
                continue; // defensive: this topic only carries ped crowd frames.
            }

            var n = f.ReadPayload(_crowdBytes);
            _state.ApplyCrowd(_crowdBytes.AsSpan(0, n));
        }
    }

    private void PumpPathArc()
    {
        using var loan = _pathArcReader.Take(maxSamples: 256);
        foreach (var sample in loan)
        {
            if (!sample.IsValid)
            {
                continue;
            }

            var d = sample.Data;
            var n = d.ReadPayload(_legBytes);
            _state.ApplyPathArc(_legBytes.AsSpan(0, n));
        }
    }

    private void PumpActivity()
    {
        using var loan = _activityReader.Take(maxSamples: 256);
        foreach (var sample in loan)
        {
            if (!sample.IsValid)
            {
                continue;
            }

            var d = sample.Data;
            var n = d.ReadPayload(_legBytes);
            _state.ApplyActivity(new VehicleHandle(d.Index, d.Generation), _legBytes.AsSpan(0, n));
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
            _state.ApplyLifecycle(new PedLifecycleRecord(
                new VehicleHandle(d.Index, d.Generation), (PedLifecycleKind)d.Kind, d.Time, d.Name.ToString()));
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
