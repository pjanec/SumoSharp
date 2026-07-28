using Sim.Core;
using Sim.Core.Orca;

namespace Sim.Replication;

// SUMOSHARP-DEADRECKONING.md §4.2 / §4.4 — the per-mover records carried in a replication frame. Handle-
// based (no strings). Scalars are kept as-is here (the codec does the wire packing/quantization); a
// renderer feeds these straight into PoseResolver.Resolve.

// Up to K upcoming lane handles ahead of a LaneArc vehicle (current lane first), for the resolver's forward
// walk. Fixed K keeps the wire record uniform (DDS InlineArray-friendly); -1 pads unused slots. K=4 covers
// the short render-prediction horizon (a vehicle rarely crosses >4 lanes within one update interval).
public readonly struct UpcomingLanes
{
    public const int Count = 4;

    private readonly int _l0, _l1, _l2, _l3;

    public UpcomingLanes(ReadOnlySpan<int> lanes)
    {
        _l0 = lanes.Length > 0 ? lanes[0] : -1;
        _l1 = lanes.Length > 1 ? lanes[1] : -1;
        _l2 = lanes.Length > 2 ? lanes[2] : -1;
        _l3 = lanes.Length > 3 ? lanes[3] : -1;
    }

    public int this[int i] => i switch { 0 => _l0, 1 => _l1, 2 => _l2, 3 => _l3, _ => -1 };

    // Copy the non-(-1) handles into `dst`; returns the count (for PoseResolver's ReadOnlySpan<int>).
    public int CopyTo(Span<int> dst)
    {
        var n = 0;
        for (var i = 0; i < Count && n < dst.Length; i++)
        {
            var h = this[i];
            if (h < 0) break;
            dst[n++] = h;
        }

        return n;
    }
}

// A LaneArc vehicle's prediction record. `latSpeed` is 0 for a lane-centred vehicle (non-zero only under
// sublane/laneless, from Kinematics.LatSpeed at the laneless merge). Physical dims travel once in the
// lifecycle/registry topic, not per frame.
public readonly struct VehicleRecord
{
    public VehicleRecord(
        VehicleHandle handle, DrModel model, int laneHandle,
        double pos, double posLat, double speed, double accel, double latSpeed, UpcomingLanes upcoming)
    {
        Handle = handle; Model = model; LaneHandle = laneHandle;
        Pos = pos; PosLat = posLat; Speed = speed; Accel = accel; LatSpeed = latSpeed; Upcoming = upcoming;
    }

    public VehicleHandle Handle { get; }
    public DrModel Model { get; }
    public int LaneHandle { get; }
    public double Pos { get; }
    public double PosLat { get; }
    public double Speed { get; }
    public double Accel { get; }
    public double LatSpeed { get; }
    public UpcomingLanes Upcoming { get; }
}

// A FreeKinematic (crowd / ORCA / holonomic) mover's record (§4.4, confirmed with the laneless branch on
// issue #3): position + velocity + footprint radius; z reserved (0 in 2-D). Heading is NOT on the wire —
// a renderer derives it from atan2(vy, vx).
public readonly struct CrowdRecord
{
    public CrowdRecord(VehicleHandle handle, double x, double y, double z, double vx, double vy, double radius)
    {
        Handle = handle; X = x; Y = y; Z = z; Vx = vx; Vy = vy; Radius = radius;
    }

    public VehicleHandle Handle { get; }
    public double X { get; }
    public double Y { get; }
    public double Z { get; }
    public double Vx { get; }
    public double Vy { get; }
    public double Radius { get; }
}

// POC-7b (docs/PEDESTRIAN-POC-PLAN.md POC-7; docs/PEDESTRIAN-DESIGN.md §7) — the QUANTIZED pedestrian
// analog of `CrowdRecord`, for the high-power (`OrcaCrowd`/`FreeKinematic`) population. This is a
// SEPARATE record kind from `CrowdRecord` on purpose: `CrowdRecord` is unquantized float32 and other
// consumers may already depend on its 32 B layout (CLAUDE.md additive-only rule), so it is left byte-
// for-byte unchanged. `PedFreeKinematicRecord` is the logical (unquantized, double) view; the codec
// (`FrameCodec.WritePedFreeKinematicFrame`/`ReadPedFreeKinematicFrame`) does the cm-precision packing
// into the compact wire form (see that file's header comment for the exact 18 B layout and the
// int32-cm-absolute-vs-int16-cm-relative tradeoff).
//
// No `Z`: unlike `CrowdRecord`, pedestrians are assumed to ride the walkable ground plane; elevation is
// resolved on the IG from the static walkable-surface geometry (navmesh bake), exactly as `VehicleRecord`
// carries no `Z` either (resolved from lane geometry). This is a deliberate scope cut for POC-7b, not a
// claim that multi-level (bridge/underpass) peds are unsupported forever.
public readonly struct PedFreeKinematicRecord
{
    public PedFreeKinematicRecord(VehicleHandle handle, double x, double y, double vx, double vy, double radius)
    {
        Handle = handle; X = x; Y = y; Vx = vx; Vy = vy; Radius = radius;
    }

    public VehicleHandle Handle { get; }
    public double X { get; }
    public double Y { get; }
    public double Vx { get; }
    public double Vy { get; }
    public double Radius { get; }
}

// POC-7b — the wire counterpart of the low-power ped's one-time payload (docs/PEDESTRIAN-DESIGN.md §7
// "PathArc DR model"; the logical/event-model analog is `Sim.Pedestrians.Lod.PathArcRecord`, which this
// mirrors but does not depend on -- `Sim.Replication` stays free of a `Sim.Pedestrians` reference, so
// this type uses `Sim.Core.Orca.Vec2` directly, same as `Sim.Pedestrians` does). Sent ONCE per ped (on
// spawn, and again on each demotion with a fresh re-routed path) on the low-rate/durable lifecycle
// topic -- never repeated per step -- so it must not be counted as per-step bandwidth (only amortized
// over the ped's path lifetime, plus the separate low-rate heartbeat).
public readonly struct PathArcRecord
{
    public PathArcRecord(VehicleHandle handle, double speed, double startTime, IReadOnlyList<Vec2> path)
        : this(handle, speed, startTime, path, pathZ: null)
    {
    }

    // C4 (docs/EXTERNAL-NET-LOADING-DESIGN.md §3.6): the ADDITIVE overload carrying the path's
    // per-vertex elevation. The 4-argument ctor above stays and simply passes null, so every existing
    // construction site is untouched.
    public PathArcRecord(
        VehicleHandle handle, double speed, double startTime, IReadOnlyList<Vec2> path, IReadOnlyList<double>? pathZ)
    {
        Handle = handle; Speed = speed; StartTime = startTime; Path = path; PathZ = pathZ;
    }

    public VehicleHandle Handle { get; }
    public double Speed { get; }
    public double StartTime { get; }
    public IReadOnlyList<Vec2> Path { get; }

    // Per-vertex elevation (metres), index-aligned with `Path`, or null when the source net is 2-D.
    //
    // NULL IS THE WIRE DECISION, not just a data one: the publisher emits the ORIGINAL frame kind 4
    // whenever this is null, so a 2-D net's bytes are byte-for-byte what they were before elevation
    // existed. Only a 3-D net pays the extra 4 B/point, on a record sent once per ped path lifetime on
    // the durable topic -- never on the per-step hot path.
    public IReadOnlyList<double>? PathZ { get; }
}
