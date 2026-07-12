namespace Sim.Core;

// Coordinated with SUMOSHARP-API.md §4.1 (D4): a stable, zero-allocation reference to an obstacle slot
// in ObstacleStore, laid out to MIRROR the host game engine's 48-bit entity-handle convention -- a
// 32-bit slot Index plus a 16-bit Generation version counter. A stored handle whose Generation no
// longer matches the live slot's generation is stale (the slot was removed and its index recycled),
// and the store detects the mismatch in O(1) and treats the operation as an inert no-op -- the exact
// "inert-when-absent" contract the string-keyed UpdateObstacle already documented.
//
// This is a DISTINCT id space from the host ECS's own entity handles: same 32+16 shape, different
// registry -- a host maps between them (the same place any string->id cache lives). Never interchange
// a SumoSharp ObstacleHandle with a host EntityHandle.
//
// Generation 0 is never handed out for a live slot (ObstacleStore's first live generation is 1), so
// `default(ObstacleHandle)` (== None) never resolves to a live obstacle.
public readonly struct ObstacleHandle : IEquatable<ObstacleHandle>
{
    public readonly uint Index;
    public readonly ushort Generation;

    public ObstacleHandle(uint index, ushort generation)
    {
        Index = index;
        Generation = generation;
    }

    // The never-valid sentinel (Index 0, Generation 0). Usable by callers as "no obstacle".
    public static ObstacleHandle None => default;

    public bool Equals(ObstacleHandle other) => Index == other.Index && Generation == other.Generation;

    public override bool Equals(object? obj) => obj is ObstacleHandle h && Equals(h);

    public override int GetHashCode() => unchecked(((int)Index * 397) ^ Generation);

    public static bool operator ==(ObstacleHandle a, ObstacleHandle b) => a.Equals(b);

    public static bool operator !=(ObstacleHandle a, ObstacleHandle b) => !a.Equals(b);

    public override string ToString() => $"Obstacle#{Index}.{Generation}";
}
