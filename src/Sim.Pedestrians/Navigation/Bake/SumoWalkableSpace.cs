using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation.Bake;

// IWalkableSpace over a WalkablePolygonBaker.Bake() polygon set (docs/PEDESTRIAN-DESIGN.md §4,
// PEDESTRIAN-POC-PLAN.md POC-1a "SUMO-geometry bake" provider).
public sealed class SumoWalkableSpace : IWalkableSpace
{
    private readonly IReadOnlyList<BakedPolygon> _polygons;
    private readonly IReadOnlyList<WallSegment> _boundarySegments;

    public SumoWalkableSpace(IReadOnlyList<BakedPolygon> polygons)
    {
        _polygons = polygons;
        _boundarySegments = BuildBoundarySegments(polygons);
    }

    // Union containment: inside ANY baked polygon. Fixed iteration order (bake order) -> pure
    // function of (polygons, p), so repeated calls are deterministic.
    public bool Contains(Vec2 p)
    {
        foreach (var polygon in _polygons)
        {
            if (PolygonGeometry.Contains(polygon.Vertices, p))
            {
                return true;
            }
        }

        return false;
    }

    public Vec2 ClampToWalkable(Vec2 p)
    {
        if (Contains(p))
        {
            return p;
        }

        var best = p;
        var bestDistSq = double.MaxValue;
        foreach (var polygon in _polygons)
        {
            var candidate = PolygonGeometry.NearestPointOnBoundary(polygon.Vertices, p, out var distSq);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = candidate;
            }
        }

        return best;
    }

    public IReadOnlyList<WallSegment> BoundarySegments => _boundarySegments;

    // APPROXIMATION (documented, POC-1a, per the task): this returns each baked polygon's OWN
    // boundary edges independently rather than computing the true outer boundary of the polygon
    // UNION. Where two walkable polygons abut (e.g. a sidewalk quad and the walkingarea it feeds
    // into), the shared edge is emitted TWICE -- once from each side -- as if it were an interior
    // wall. That is harmless for a caller that only wants to confine agents to the union's outer
    // edge and filters accordingly, but WRONG to feed wholesale into OrcaCrowd.AddObstacle: a
    // shared edge is exactly a navigation PORTAL (see PolygonGraph), and walling it off would block
    // agents from ever crossing it. PedRouteController therefore does NOT consume
    // BoundarySegments in this POC. A full union-boundary computation (dropping/merging shared
    // interior edges) is future work if a caller needs a single confinement wall set.
    private static IReadOnlyList<WallSegment> BuildBoundarySegments(IReadOnlyList<BakedPolygon> polygons)
    {
        var segments = new List<WallSegment>();
        foreach (var polygon in polygons)
        {
            var vertices = polygon.Vertices;
            var n = vertices.Count;
            for (var i = 0; i < n; i++)
            {
                segments.Add(new WallSegment(vertices[i], vertices[(i + 1) % n]));
            }
        }

        return segments;
    }
}
