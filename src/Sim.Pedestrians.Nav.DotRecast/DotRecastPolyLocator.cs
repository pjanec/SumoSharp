using DotRecast.Core.Numerics;
using DotRecast.Detour;
using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation.DotRecast;

// Shared "nearest polygon to a 2D point" lookup used by both DotRecastWalkableSpace and
// DotRecastNavMesh (docs/PEDESTRIAN-POC-PLAN.md POC-1b). DtNavMeshQuery.FindNearestPoly needs a
// bounded search box (halfExtents) rather than a true "nearest anywhere" query, so a caller-supplied
// point that lands outside the first box would otherwise silently fail to locate a polygon. This
// grows the box geometrically until a polygon is found (or a hard cap is hit), which is a
// deliberate, documented POC simplification of what a production query would do (e.g. a real
// nearest-anywhere spatial index) -- adequate at POC-0's net scale.
//
// Deterministic: DtNavMeshQuery.FindNearestPoly is a pure function of (navmesh, center, extents,
// filter) with no RNG or iteration-order dependence (DotRecast's BV-tree walk is a fixed traversal).
internal static class DotRecastPolyLocator
{
    private const float InitialHalfExtent = 1.0f;
    private const float MaxHalfExtent = 256.0f;
    private const float VerticalHalfExtent = 50.0f; // generous: the POC walkable plane is flat at Y=0

    public static bool TryFindNearestPoly(
        DtNavMeshQuery query,
        IDtQueryFilter filter,
        Vec2 p,
        out long polyRef,
        out RcVec3f point)
    {
        var center = new RcVec3f((float)p.X, 0f, (float)p.Y);

        for (var halfExtent = InitialHalfExtent; halfExtent <= MaxHalfExtent; halfExtent *= 2f)
        {
            var halfExtents = new RcVec3f(halfExtent, VerticalHalfExtent, halfExtent);
            var status = query.FindNearestPoly(center, halfExtents, filter, out polyRef, out point, out _);
            if (status.Succeeded() && polyRef != 0)
            {
                return true;
            }
        }

        polyRef = 0;
        point = center;
        return false;
    }
}
