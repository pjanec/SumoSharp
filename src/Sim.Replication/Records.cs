using Sim.Core;

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
