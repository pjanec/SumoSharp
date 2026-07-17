using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation.Bake;

// IPedNavigation over a WalkablePolygonBaker.Bake() polygon set (docs/PEDESTRIAN-DESIGN.md §4,
// PEDESTRIAN-POC-PLAN.md POC-1a "SUMO-geometry bake" provider). Strategic routing is A* over the
// polygon-adjacency graph (PolygonGraph): each polygon is a node, each shared boundary a portal
// edge, edge cost and the search heuristic are both the Euclidean distance between polygon
// VERTEX-AVERAGE centroids (BakedPolygon.Centroid) -- using the same metric for cost and heuristic
// keeps the heuristic consistent (never overestimates), so the search is optimal over this graph.
// Deterministic: fixed adjacency iteration order (PolygonGraph), ties broken by polygon Index (the
// task's "deterministic tie-break by polygon index").
public sealed class SumoNavMesh : IPedNavigation
{
    private readonly IReadOnlyList<BakedPolygon> _polygons;
    private readonly PolygonGraph _graph;
    private readonly SumoWalkableSpace _space;

    public SumoNavMesh(IReadOnlyList<BakedPolygon> polygons)
        : this(polygons, new SumoWalkableSpace(polygons))
    {
    }

    // Overload for callers that already built (and want to share) a SumoWalkableSpace over the
    // same polygon set, instead of this constructing its own.
    public SumoNavMesh(IReadOnlyList<BakedPolygon> polygons, SumoWalkableSpace space)
    {
        _polygons = polygons;
        _graph = new PolygonGraph(polygons);
        _space = space;
    }

    public IReadOnlyList<Vec2>? FindPath(Vec2 start, Vec2 goal)
    {
        var startPolygon = LocatePolygon(start);
        var goalPolygon = LocatePolygon(goal);
        if (startPolygon < 0 || goalPolygon < 0)
        {
            return null; // walkable space is empty, or the point cannot be located/clamped at all
        }

        if (startPolygon == goalPolygon)
        {
            // Same convex-ish local region: a direct segment is a valid (if not string-pulled)
            // corridor path.
            return new[] { start, goal };
        }

        var nodePath = FindNodePath(startPolygon, goalPolygon);
        if (nodePath is null)
        {
            return null; // disconnected in the adjacency graph -> genuinely unreachable
        }

        var waypoints = new List<Vec2> { start };
        for (var i = 0; i + 1 < nodePath.Count; i++)
        {
            var from = nodePath[i];
            var to = nodePath[i + 1];
            var portal = _graph.Neighbors(from).First(p => p.Neighbor == to).Point;
            waypoints.Add(portal);
        }

        waypoints.Add(goal);
        return waypoints;
    }

    // Locates the polygon containing `p`; if none does, snaps `p` onto walkable space first (the
    // interface's documented off-mesh spawn/goal case) and locates from there, falling back to the
    // nearest polygon by boundary distance if even the clamped point lands exactly on a seam.
    private int LocatePolygon(Vec2 p)
    {
        var direct = IndexOfContaining(p);
        if (direct >= 0)
        {
            return direct;
        }

        if (_polygons.Count == 0)
        {
            return -1;
        }

        var clamped = _space.ClampToWalkable(p);
        var afterClamp = IndexOfContaining(clamped);
        if (afterClamp >= 0)
        {
            return afterClamp;
        }

        var best = -1;
        var bestDistSq = double.MaxValue;
        for (var i = 0; i < _polygons.Count; i++)
        {
            PolygonGeometry.NearestPointOnBoundary(_polygons[i].Vertices, clamped, out var distSq);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = i;
            }
        }

        return best;
    }

    private int IndexOfContaining(Vec2 p)
    {
        for (var i = 0; i < _polygons.Count; i++)
        {
            if (PolygonGeometry.Contains(_polygons[i].Vertices, p))
            {
                return i;
            }
        }

        return -1;
    }

    private List<int>? FindNodePath(int start, int goal)
    {
        var open = new List<int> { start };
        var inOpen = new HashSet<int> { start };
        var closed = new HashSet<int>();
        var cameFrom = new Dictionary<int, int>();
        var gScore = new Dictionary<int, double> { [start] = 0.0 };

        double FScore(int node) => gScore[node] + Heuristic(node, goal);

        while (open.Count > 0)
        {
            // Deterministic tie-break: lowest f-score, then lowest polygon index.
            open.Sort((a, b) =>
            {
                var cmp = FScore(a).CompareTo(FScore(b));
                return cmp != 0 ? cmp : a.CompareTo(b);
            });

            var current = open[0];
            open.RemoveAt(0);
            inOpen.Remove(current);

            if (current == goal)
            {
                return ReconstructPath(cameFrom, current);
            }

            closed.Add(current);

            foreach (var portal in _graph.Neighbors(current))
            {
                if (closed.Contains(portal.Neighbor))
                {
                    continue;
                }

                var tentativeG = gScore[current] + CentroidDistance(current, portal.Neighbor);
                if (!gScore.TryGetValue(portal.Neighbor, out var existingG) || tentativeG < existingG)
                {
                    cameFrom[portal.Neighbor] = current;
                    gScore[portal.Neighbor] = tentativeG;
                    if (inOpen.Add(portal.Neighbor))
                    {
                        open.Add(portal.Neighbor);
                    }
                }
            }
        }

        return null;
    }

    private double Heuristic(int node, int goal) => CentroidDistance(node, goal);

    private double CentroidDistance(int a, int b) => (_polygons[a].Centroid - _polygons[b].Centroid).Abs;

    private static List<int> ReconstructPath(Dictionary<int, int> cameFrom, int current)
    {
        var path = new List<int> { current };
        while (cameFrom.TryGetValue(current, out var prev))
        {
            current = prev;
            path.Add(current);
        }

        path.Reverse();
        return path;
    }
}
