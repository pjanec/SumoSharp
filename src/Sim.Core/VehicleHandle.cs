namespace Sim.Core;

// SUMOSHARP-API.md §5 (D4/D6): a stable, zero-allocation reference to a vehicle, laid out to MIRROR the
// host game engine's 48-bit entity-handle convention -- a 32-bit Index (== VehicleRuntime.EntityIndex,
// the stable slot the engine's own side tables already key off) plus a 16-bit Generation.
//
// This is a FACADE over an int that already exists (EntityIndex), NOT a reshape of the vehicle storage:
// the read surface just wraps it. The Generation is sourced from a parallel side array that is presently
// a constant (no vehicle slot is recycled yet) and gets bumped per-slot when runtime despawn/spawn lands
// -- at which point a handle held across a despawn goes stale and TryGetVehicle rejects it in O(1). Same
// 32+16 shape as ObstacleHandle, but a DISTINCT id space (vehicles vs obstacles); never interchange them.
public readonly struct VehicleHandle : IEquatable<VehicleHandle>
{
    public readonly uint Index;
    public readonly ushort Generation;

    public VehicleHandle(uint index, ushort generation)
    {
        Index = index;
        Generation = generation;
    }

    public static VehicleHandle None => default;

    public bool Equals(VehicleHandle other) => Index == other.Index && Generation == other.Generation;

    public override bool Equals(object? obj) => obj is VehicleHandle h && Equals(h);

    public override int GetHashCode() => unchecked(((int)Index * 397) ^ Generation);

    public static bool operator ==(VehicleHandle a, VehicleHandle b) => a.Equals(b);

    public static bool operator !=(VehicleHandle a, VehicleHandle b) => !a.Equals(b);

    public override string ToString() => $"Vehicle#{Index}.{Generation}";
}
