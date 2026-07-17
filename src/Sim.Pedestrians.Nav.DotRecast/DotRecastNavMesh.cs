using DotRecast.Detour;
using Sim.Core.Orca;
using Sim.Pedestrians.Navigation.Bake;

namespace Sim.Pedestrians.Navigation.DotRecast;

// IPedNavigation over a DtNavMesh built by DotRecastNavMeshBuilder from a WalkablePolygonBaker.Bake()
// polygon set (docs/PEDESTRIAN-POC-PLAN.md POC-1b task step 4). Strategic routing is DotRecast's own
// Detour pipeline: FindNearestPoly locates the start/goal polygons, FindPath produces the polygon
// corridor (A* over the navmesh's polygon-adjacency graph), FindStraightPath funnels that corridor
// down to a taut waypoint polyline (Detour's built-in string-pull) which is mapped back to 2D by
// dropping the (always-zero) Recast height.
//
// Deterministic: DtNavMeshQuery's search is a pure function of (navmesh, start, goal, filter) with
// fixed tie-breaks and no RNG (see DotRecastNavMeshBuilder remarks) -- repeated calls with the same
// inputs return the same corridor and the same straight path.
public sealed class DotRecastNavMesh : IPedNavigation
{
    // Detour's FindStraightPath needs a caller-supplied output cap; POC-0's net is small enough that
    // no route ever approaches this, so it is generous headroom rather than a tuned limit.
    private const int MaxStraightPathPoints = 256;

    private readonly DtNavMeshQuery _query;
    private readonly DtQueryDefaultFilter _filter = new();

    public DotRecastNavMesh(DtNavMesh navMesh)
    {
        _query = new DtNavMeshQuery(navMesh);
    }

    public DotRecastNavMesh(IReadOnlyList<BakedPolygon> polygons, DotRecastBuildConfig? config = null)
        : this(DotRecastNavMeshBuilder.Build(polygons, config ?? DotRecastBuildConfig.Default))
    {
    }

    public IReadOnlyList<Vec2>? FindPath(Vec2 start, Vec2 goal)
    {
        if (!DotRecastPolyLocator.TryFindNearestPoly(_query, _filter, start, out var startRef, out var startPt))
        {
            return null;
        }

        if (!DotRecastPolyLocator.TryFindNearestPoly(_query, _filter, goal, out var goalRef, out var goalPt))
        {
            return null;
        }

        var corridor = new List<long>();
        var pathStatus = _query.FindPath(startRef, goalRef, startPt, goalPt, _filter, ref corridor, DtFindPathOption.NoOption);
        if (pathStatus.Failed() || corridor.Count == 0)
        {
            return null; // disconnected in the navmesh's polygon graph -> genuinely unreachable
        }

        Span<DtStraightPath> straightPath = new DtStraightPath[MaxStraightPathPoints];
        var straightStatus = _query.FindStraightPath(
            startPt, goalPt, corridor, corridor.Count, straightPath, out var straightPathCount, MaxStraightPathPoints, 0);
        if (straightStatus.Failed() || straightPathCount == 0)
        {
            return null;
        }

        var waypoints = new List<Vec2>(straightPathCount);
        for (var i = 0; i < straightPathCount; i++)
        {
            waypoints.Add(new Vec2(straightPath[i].pos.X, straightPath[i].pos.Z));
        }

        return waypoints;
    }
}
