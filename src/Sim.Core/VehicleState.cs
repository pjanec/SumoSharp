namespace Sim.Core;

// SUMOSHARP-API.md §5: the random-access read record returned by Engine.TryGetVehicle -- the
// array-of-structures view over the same per-step published snapshot the columnar spans expose. A
// `readonly struct` (no heap allocation) carrying BOTH the render-facing float geometry and the
// parity-exact `double` lane-relative values, per the precision split (D7): float for "where to draw
// it", double for "what the sim actually computed".
public readonly struct VehicleState
{
    public readonly VehicleHandle Handle;
    public readonly int EntityIndex;
    public readonly string VehicleId;
    public readonly string VehicleType;

    // Lane-relative source of truth (DESIGN.md seam 2), parity-exact doubles.
    public readonly int LaneHandle;
    public readonly string LaneId;
    public readonly double Pos;      // longitudinal arc-length along the lane centreline
    public readonly double Speed;    // m/s
    public readonly double PosLat;   // lateral offset from centreline (+ = LEFT of travel), 0 in lane mode

    // Derived, render-facing (float). Z is present from day one -- 0 on today's 2-D nets, real when
    // geometry-3D lands (SUMOSHARP-API.md §6), so this record never breaks when elevation arrives.
    public readonly float X;
    public readonly float Y;
    public readonly float Z;
    public readonly float Angle;     // heading / yaw, degrees

    public VehicleState(
        VehicleHandle handle, int entityIndex, string vehicleId, string vehicleType,
        int laneHandle, string laneId, double pos, double speed, double posLat,
        float x, float y, float z, float angle)
    {
        Handle = handle;
        EntityIndex = entityIndex;
        VehicleId = vehicleId;
        VehicleType = vehicleType;
        LaneHandle = laneHandle;
        LaneId = laneId;
        Pos = pos;
        Speed = speed;
        PosLat = posLat;
        X = x;
        Y = y;
        Z = z;
        Angle = angle;
    }
}
