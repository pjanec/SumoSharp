using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation.Bake;

// A portal-adjacency edge from a polygon to one of its neighbours, carrying the shared-boundary
// portal point SumoNavMesh.FindPath threads the path through.
internal readonly record struct PolygonPortal(int Neighbor, Vec2 Point);

// Builds and holds the polygon-adjacency graph over a baked polygon set (docs/PEDESTRIAN-DESIGN.md
// §4, POC-1a). Two polygons are adjacent when their boundaries share a segment (an edge with the
// same two endpoints, in either order, within AdjacencyEpsilon) -- that segment's midpoint is the
// portal. Where more than one boundary segment is shared between the same pair (SUMO's corner
// geometry commonly abuts along a short multi-segment "staircase" chain, not a single edge), the
// portal point is the average of every shared segment's midpoint, giving one representative
// doorway point roughly centred on the shared chain.
//
// DELIBERATELY NO vertex-only fallback: an earlier version of this connected any pair sharing just
// one endpoint (no whole matching edge), meant to be the interface doc's "or overlap (endpoints
// within a small epsilon)" case for same-lane sidewalk quads meeting at a bend. In POC-0's fixture
// that fallback instead created a WRONG shortcut: a crossing polygon, its walkingarea, and the far
// sidewalk quad all meet at one shared CORNER VERTEX (three polygons, one point), and the fallback
// connected the crossing directly to the sidewalk there -- skipping the walkingarea whose actual
// AREA is the only real path between them. A* then routed a straight line across that corner that
// briefly left the walkable union (verified empirically against POC-0). A single shared vertex is
// not evidence of a walkable doorway when 3+ polygons touch it; only a shared EDGE is. POC-0 has no
// bent sidewalks, so dropping the fallback costs nothing here -- see WalkablePolygonBaker's bend
// note for the resulting (accepted) POC limitation.
internal sealed class PolygonGraph
{
    // A "small epsilon" for SUMO net coordinates (meters): generous enough to tolerate minor
    // floating-point drift in shared boundary geometry, tight enough that unrelated polygons
    // several metres apart never falsely connect.
    private const double AdjacencyEpsilon = 1e-3;

    private readonly List<PolygonPortal>[] _adjacency;

    public PolygonGraph(IReadOnlyList<BakedPolygon> polygons)
    {
        _adjacency = BuildAdjacency(polygons);
    }

    public IReadOnlyList<PolygonPortal> Neighbors(int polygonIndex) => _adjacency[polygonIndex];

    private static List<PolygonPortal>[] BuildAdjacency(IReadOnlyList<BakedPolygon> polygons)
    {
        var n = polygons.Count;
        var adjacency = new List<PolygonPortal>[n];
        for (var i = 0; i < n; i++)
        {
            adjacency[i] = new List<PolygonPortal>();
        }

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                var portal = FindPortal(polygons[i].Vertices, polygons[j].Vertices);
                if (portal is { } point)
                {
                    adjacency[i].Add(new PolygonPortal(j, point));
                    adjacency[j].Add(new PolygonPortal(i, point));
                }
            }
        }

        // Fixed iteration order per node: sort each neighbour list ascending by neighbour index,
        // so graph traversal (and hence A*) never depends on the O(n^2) build order above.
        for (var i = 0; i < n; i++)
        {
            adjacency[i].Sort((a, b) => a.Neighbor.CompareTo(b.Neighbor));
        }

        return adjacency;
    }

    private static Vec2? FindPortal(IReadOnlyList<Vec2> a, IReadOnlyList<Vec2> b)
    {
        var edgeMidpoints = new List<Vec2>();
        for (var i = 0; i < a.Count; i++)
        {
            var a0 = a[i];
            var a1 = a[(i + 1) % a.Count];
            for (var j = 0; j < b.Count; j++)
            {
                var b0 = b[j];
                var b1 = b[(j + 1) % b.Count];
                var sameOrder = PolygonGeometry.NearlyEqual(a0, b0, AdjacencyEpsilon)
                    && PolygonGeometry.NearlyEqual(a1, b1, AdjacencyEpsilon);
                var reversed = PolygonGeometry.NearlyEqual(a0, b1, AdjacencyEpsilon)
                    && PolygonGeometry.NearlyEqual(a1, b0, AdjacencyEpsilon);
                if (sameOrder || reversed)
                {
                    edgeMidpoints.Add(0.5 * (a0 + a1));
                }
            }
        }

        return edgeMidpoints.Count > 0 ? Average(edgeMidpoints) : null;
    }

    private static Vec2 Average(List<Vec2> points)
    {
        double sx = 0.0, sy = 0.0;
        foreach (var p in points)
        {
            sx += p.X;
            sy += p.Y;
        }

        return new Vec2(sx / points.Count, sy / points.Count);
    }
}
