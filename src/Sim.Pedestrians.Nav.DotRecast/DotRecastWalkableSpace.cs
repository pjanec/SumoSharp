using DotRecast.Detour;
using Sim.Core.Orca;
using Sim.Pedestrians.Navigation.Bake;

namespace Sim.Pedestrians.Navigation.DotRecast;

// IWalkableSpace over a DtNavMesh built by DotRecastNavMeshBuilder from a WalkablePolygonBaker.Bake()
// polygon set (docs/PEDESTRIAN-POC-PLAN.md POC-1b task step 3).
public sealed class DotRecastWalkableSpace : IWalkableSpace
{
    private readonly DtNavMeshQuery _query;
    private readonly DtQueryDefaultFilter _filter = new();

    // Contains() tolerance: the navmesh is eroded inward by AgentRadius during the build (Recast's
    // "erode walkable area by agent radius" step), so a point up to ~AgentRadius outside the eroded
    // boundary can still be legitimately "inside" the true (unedoded) walkable polygon it came from.
    // Treating "nearest poly found within AgentRadius" as contained is the documented POC
    // approximation the task calls for, rather than tracking the pre-erosion polygon boundary
    // separately.
    private readonly float _containsTolerance;

    public DotRecastWalkableSpace(DtNavMesh navMesh, DotRecastBuildConfig config)
    {
        _query = new DtNavMeshQuery(navMesh);
        _containsTolerance = config.AgentRadius;
    }

    public DotRecastWalkableSpace(IReadOnlyList<BakedPolygon> polygons, DotRecastBuildConfig? config = null)
        : this(DotRecastNavMeshBuilder.Build(polygons, config ?? DotRecastBuildConfig.Default), config ?? DotRecastBuildConfig.Default)
    {
    }

    public bool Contains(Vec2 p)
    {
        if (!DotRecastPolyLocator.TryFindNearestPoly(_query, _filter, p, out _, out var nearestPt))
        {
            return false;
        }

        var dx = nearestPt.X - p.X;
        var dz = nearestPt.Z - p.Y;
        var distSq = (dx * dx) + (dz * dz);
        return distSq <= _containsTolerance * _containsTolerance;
    }

    public Vec2 ClampToWalkable(Vec2 p)
    {
        // "Identity if already inside" (interface doc): FindNearestPoly's nearest point IS p itself
        // whenever p already lies over a polygon, so no separate Contains() branch is needed here.
        if (!DotRecastPolyLocator.TryFindNearestPoly(_query, _filter, p, out _, out var nearestPt))
        {
            return p; // empty/unreachable navmesh: nothing to clamp onto, degrade to identity
        }

        return new Vec2(nearestPt.X, nearestPt.Z);
    }

    // Empty by design (documented, POC-1b task): this provider does not compute a union outer
    // boundary of its polygons. The interface permits an empty boundary set for a provider that
    // confines agents by other means -- here, confinement is either the SUMO-bake provider's
    // BoundarySegments (POC-1a) or ORCA obstacles supplied some other way; callers must (and, per
    // Bake/PedRouteController, already do) tolerate an empty list.
    public IReadOnlyList<WallSegment> BoundarySegments => Array.Empty<WallSegment>();
}
