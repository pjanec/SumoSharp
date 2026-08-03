using Sim.Core.Orca;
using Sim.Pedestrians.Navigation.Bake;

namespace Sim.Pedestrians.Navigation.RouteGraph;

// docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §3/§4: a second IPedNavigation provider that routes
// pedestrians on the SUMO pedestrian edge/connection graph directly (sidewalk lanes + crossings +
// walkingareas, stitched by PedConnection) -- no WalkablePolygonBaker.Bake, no polygon navmesh. This
// is the "arbitrary net" route-graph provider: cheap to build (one node per pedestrian lane element,
// O(lanes) construction) so a whole, unbaked road-net is routable. Kept entirely separate from
// SumoNavMesh/PolygonGraph (Bake/*) -- this class shares only PolygonGeometry's small deterministic
// point/segment helpers, never the polygon-graph machinery.

/// The kind of pedestrian-lane element a <see cref="RouteNode"/> was built from -- purely
/// informational (routing/width lookup treat the geometry uniformly per kind, see remarks on
/// <see cref="RouteNode"/>).
public enum RouteNodeKind
{
    Sidewalk,
    Crossing,
    WalkingArea,
}

/// One graph node per pedestrian-lane element (design §3.1). `Geometry` is the lane's centreline
/// polyline (Sidewalk/Crossing, an OPEN polyline -- walked end to end) or its polygon outline
/// (WalkingArea, a CLOSED ring per the <see cref="PolygonGeometry"/> convention: the edge from the
/// last vertex back to the first is part of the boundary though not stored twice). `HalfWidth` is
/// the lane's Width/2, or 0.5 m when the source width is unset/non-positive (same default the
/// existing WalkablePolygonBaker uses). `Centroid` is the vertex-average, used only as the A*
/// heuristic anchor (see SumoRouteGraphNav.Heuristic).
public sealed record RouteNode(
    int Index,
    string Id,
    RouteNodeKind Kind,
    IReadOnlyList<Vec2> Geometry,
    double HalfWidth,
    // C2: the source element's retained per-vertex elevation channel, index-aligned with `Geometry`
    // and null on a 2-D net. Output-only -- read by `ElevationsAlong` and by nothing else. Defaulted
    // so every existing `new RouteNode(...)` compiles unchanged.
    IReadOnlyList<double>? GeometryZ = null)
{
    public Vec2 Centroid { get; } = PolygonGeometry.VertexAverage(Geometry);
}

/// docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §3 (node/graph/spatial-index), §4 (FindPath), §4.1
/// (AssemblePolyline), §4.2 (HalfWidthsAlong). A route-graph IPedNavigation provider built directly
/// from a <see cref="PedNetwork"/> (no baking step): one node per sidewalk/crossing/walkingarea lane,
/// bidirectional adjacency + a portal point per <see cref="PedConnection"/>, and a uniform grid over
/// lane segments for nearest-lane lookup. Deterministic throughout (design §3.3/§10): construction
/// order is kind-group then Id ordinal, every tie-break is by lower node index, and no
/// <see cref="System.Random"/> is used anywhere in this class.
public sealed class SumoRouteGraphNav : IPedNavigation
{
    /// The result of a nearest-lane query: the owning node and the nearest point on its geometry.
    public readonly record struct NearestLaneResult(int NodeIndex, Vec2 Point);

    private const double DegenerateLengthSq = 1e-12;

    private readonly RouteNode[] _nodes;
    private readonly Dictionary<string, int> _idToIndex;
    private readonly int[][] _adjacency;
    private readonly Vec2[][] _portals;
    private readonly double _cellSize;
    private readonly Dictionary<(int Cx, int Cy), List<(int NodeIndex, int SegIndex)>> _grid;
    private readonly int _maxGridRadius;

    public SumoRouteGraphNav(PedNetwork network)
    {
        _nodes = BuildNodes(network);

        _idToIndex = new Dictionary<string, int>(_nodes.Length);
        foreach (var node in _nodes)
        {
            _idToIndex[node.Id] = node.Index;
        }

        (_adjacency, _portals) = BuildAdjacency(_nodes, _idToIndex, network.PedConnections);

        _cellSize = ComputeCellSize(_nodes);
        _grid = BuildGrid(_nodes, _cellSize, out _maxGridRadius);

        // PEDZ instrument (docs/TASKS-TODO.md ped z=0; print-only, LIVECITY_PEDZLOG=1): the graph-level
        // z census -- per node kind, how many nodes carry a non-null/non-flat GeometryZ channel and a
        // sample z range. Directly answers "does the RouteGraph the peds walk carry elevation AT ALL"
        // (the upstream half the per-ped bake tally in PedLodManager cannot see past).
        if (Environment.GetEnvironmentVariable("LIVECITY_PEDZLOG") == "1")
        {
            Span<int> total = stackalloc int[3];
            Span<int> withZ = stackalloc int[3];
            var zMin = double.PositiveInfinity;
            var zMax = double.NegativeInfinity;
            foreach (var node in _nodes)
            {
                var k = (int)node.Kind;
                total[k]++;
                if (node.GeometryZ is { Count: > 0 } zs)
                {
                    for (var i = 0; i < zs.Count; i++)
                    {
                        if (zs[i] != 0.0)
                        {
                            withZ[k]++;
                            if (zs[i] < zMin) zMin = zs[i];
                            if (zs[i] > zMax) zMax = zs[i];
                            break;
                        }
                    }
                }
            }

            Console.Error.WriteLine(
                $"[pedz] GRAPH walkAreas={withZ[(int)RouteNodeKind.WalkingArea]}/{total[(int)RouteNodeKind.WalkingArea]} "
                + $"crossings={withZ[(int)RouteNodeKind.Crossing]}/{total[(int)RouteNodeKind.Crossing]} "
                + $"sidewalks={withZ[(int)RouteNodeKind.Sidewalk]}/{total[(int)RouteNodeKind.Sidewalk]} carry non-flat z; "
                + $"zRange=[{zMin:F0},{zMax:F0}]");
        }
    }

    /// The graph's nodes, in construction order (also each node's stable `Index`).
    public IReadOnlyList<RouteNode> Nodes => _nodes;

    /// `Adjacency[i]` is the list of neighbour node indices node `i` connects to (both directions of
    /// every `PedConnection` are present -- see BuildAdjacency).
    public IReadOnlyList<IReadOnlyList<int>> Adjacency => _adjacency;

    // ---- B1: nearest-lane spatial index (design §3.3) -----------------------------------------

    /// The nearest pedestrian-lane node to `p`, plus the nearest point on that node's geometry, or
    /// `null` when the graph has no nodes at all. Queries the uniform grid at `p`'s cell + ring,
    /// widening the ring until a candidate segment is found (or the grid's extent is exhausted).
    /// Deterministic: ties broken by lower node index, then lower segment index (the iteration order
    /// within a cell's candidate list, itself built in node/segment order).
    public NearestLaneResult? NearestLane(Vec2 p)
    {
        if (_nodes.Length == 0)
        {
            return null;
        }

        var cx = (int)Math.Floor(p.X / _cellSize);
        var cy = (int)Math.Floor(p.Y / _cellSize);

        for (var r = 1; r <= _maxGridRadius; r++)
        {
            var bestNode = -1;
            var bestSeg = -1;
            var bestPoint = Vec2.Zero;
            var bestDistSq = double.MaxValue;

            for (var gx = cx - r; gx <= cx + r; gx++)
            {
                for (var gy = cy - r; gy <= cy + r; gy++)
                {
                    if (!_grid.TryGetValue((gx, gy), out var candidates))
                    {
                        continue;
                    }

                    foreach (var (nodeIndex, segIndex) in candidates)
                    {
                        var (a, b) = Segment(_nodes[nodeIndex], segIndex);
                        var candidate = PolygonGeometry.NearestPointOnSegment(a, b, p);
                        var distSq = (candidate - p).AbsSq;

                        var better = distSq < bestDistSq
                            || (distSq == bestDistSq
                                && (nodeIndex < bestNode || (nodeIndex == bestNode && segIndex < bestSeg)));
                        if (better)
                        {
                            bestDistSq = distSq;
                            bestNode = nodeIndex;
                            bestSeg = segIndex;
                            bestPoint = candidate;
                        }
                    }
                }
            }

            if (bestNode >= 0)
            {
                return new NearestLaneResult(bestNode, bestPoint);
            }
        }

        return null;
    }

    // ---- B2: FindPath (design §4, §4.1) --------------------------------------------------------

    /// The interface's single routing entry point (see IPedNavigation): routes, and additionally
    /// reports the NODE INDEX that produced each returned vertex. Recorded as the polyline is
    /// assembled -- the router already visits the nodes in order, so this costs one int per vertex and
    /// no extra work.
    ///
    /// There is deliberately NO 2-D `FindPath(start, goal)` sibling: the provenance is what lets
    /// `ElevationsAlong` tell stacked surfaces apart, so a caller that quietly took the shorter form
    /// would get a silently flat (or wrong-deck) route. A caller with genuinely no use for it discards
    /// it explicitly with `out _`.
    public IReadOnlyList<Vec2>? FindPath(Vec2 start, Vec2 goal, out IReadOnlyList<int>? vertexSurfaces)
    {
        vertexSurfaces = null;

        var startLane = NearestLane(start);
        var goalLane = NearestLane(goal);
        if (startLane is null || goalLane is null)
        {
            return null; // net has no ped lanes at all
        }

        if (startLane.Value.NodeIndex == goalLane.Value.NodeIndex)
        {
            var single = SameNodeSubPath(_nodes[startLane.Value.NodeIndex], start, goal);
            var ids = new int[single.Count];
            Array.Fill(ids, startLane.Value.NodeIndex);
            vertexSurfaces = ids;
            return single;
        }

        var nodePath = AStarNodePath(startLane.Value.NodeIndex, goalLane.Value.NodeIndex, start, goal);
        if (nodePath is null)
        {
            return null; // disconnected ped graph
        }

        var surfaces = new List<int>();
        var polyline = AssemblePolyline(start, goal, nodePath, surfaces);
        vertexSurfaces = surfaces;
        return polyline;
    }

    // Start and goal snap to the SAME node: walk (or cut straight, for a walkingarea) between the
    // two projected points on that single node's own geometry -- no graph search needed.
    private static IReadOnlyList<Vec2> SameNodeSubPath(RouteNode node, Vec2 start, Vec2 goal)
    {
        if (node.Kind == RouteNodeKind.WalkingArea || node.Geometry.Count < 2)
        {
            return new[] { start, goal };
        }

        var entryPos = NearestPositionOnPolyline(node.Geometry, start);
        var exitPos = NearestPositionOnPolyline(node.Geometry, goal);

        var waypoints = new List<Vec2> { start };
        AppendIntervening(waypoints, node.Geometry, entryPos, exitPos);
        waypoints.Add(goal);
        return waypoints;
    }

    // A* over the node-adjacency graph (design §4). The search state is the node index; the cost of
    // stepping from the current node to a neighbour is the Euclidean distance between the point the
    // walk ENTERED the current node at (start, for the very first node; otherwise the portal shared
    // with whichever node preceded it -- known once a node is settled, because A* only relaxes edges
    // out of a node after it is popped with a final `cameFrom`) and the portal point that connection
    // exits through -- i.e. "successive portal points along node geometry" (design §4), the length of
    // the sub-walk across the node's own centreline/area between its entry and this exit. The
    // heuristic is Euclidean distance from a node's vertex-average centroid to `goal` (the same
    // simple centroid metric SumoNavMesh's polygon-graph A* already uses). Priority is
    // (fScore, nodeIndex) -- ties broken by the lower node index, so expansion order is fully
    // deterministic regardless of insertion order.
    private List<int>? AStarNodePath(int startNode, int goalNode, Vec2 start, Vec2 goal)
    {
        var open = new List<int> { startNode };
        var inOpen = new HashSet<int> { startNode };
        var closed = new HashSet<int>();
        var cameFrom = new Dictionary<int, int>();
        var gScore = new Dictionary<int, double> { [startNode] = 0.0 };
        var entryPoint = new Dictionary<int, Vec2> { [startNode] = start };

        double FScore(int node) => gScore[node] + Heuristic(node, goal);

        while (open.Count > 0)
        {
            open.Sort((a, b) =>
            {
                var cmp = FScore(a).CompareTo(FScore(b));
                return cmp != 0 ? cmp : a.CompareTo(b);
            });

            var current = open[0];
            open.RemoveAt(0);
            inOpen.Remove(current);

            if (current == goalNode)
            {
                return ReconstructPath(cameFrom, current);
            }

            closed.Add(current);

            var neighbors = _adjacency[current];
            var portals = _portals[current];
            var currentEntry = entryPoint[current];
            for (var k = 0; k < neighbors.Length; k++)
            {
                var neighbor = neighbors[k];
                if (closed.Contains(neighbor))
                {
                    continue;
                }

                var portal = portals[k];
                var edgeCost = (portal - currentEntry).Abs;
                var tentativeG = gScore[current] + edgeCost;
                if (!gScore.TryGetValue(neighbor, out var existingG) || tentativeG < existingG)
                {
                    cameFrom[neighbor] = current;
                    gScore[neighbor] = tentativeG;
                    entryPoint[neighbor] = portal;
                    if (inOpen.Add(neighbor))
                    {
                        open.Add(neighbor);
                    }
                }
            }
        }

        return null;
    }

    private double Heuristic(int node, Vec2 goal) => (_nodes[node].Centroid - goal).Abs;

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

    // design §4.1: walk the node path, appending each node's contribution between its entry anchor
    // (the previous node's exit anchor -- `start` for the very first node) and its exit anchor (the
    // portal shared with the next node -- `goal` for the last node). Sidewalk/Crossing nodes append
    // the sub-slice of their own centreline between the two anchors' projections (oriented so the
    // near end matches the entry, exactly like SumoNavMesh's spine-threading); WalkingArea nodes
    // append a straight chord from entry to exit anchor (design §4.1's documented first
    // approximation -- see the report for how this was verified against the committed fixture).
    // `surfaces`, when supplied, receives the node index responsible for each appended waypoint --
    // index-aligned with the returned polyline. The first waypoint (`start`) is attributed to the first
    // node on the path, since that is the surface the ped stands on when it sets off.
    private IReadOnlyList<Vec2> AssemblePolyline(Vec2 start, Vec2 goal, List<int> nodePath, List<int>? surfaces = null)
    {
        var waypoints = new List<Vec2> { start };
        surfaces?.Add(nodePath[0]);

        for (var i = 0; i < nodePath.Count; i++)
        {
            var node = _nodes[nodePath[i]];
            var isLast = i == nodePath.Count - 1;
            var exitAnchor = isLast ? goal : PortalBetween(nodePath[i], nodePath[i + 1]);

            if (node.Kind == RouteNodeKind.WalkingArea || node.Geometry.Count < 2)
            {
                waypoints.Add(exitAnchor);
            }
            else
            {
                var entryAnchor = waypoints[^1];
                var entryPos = NearestPositionOnPolyline(node.Geometry, entryAnchor);
                var exitPos = NearestPositionOnPolyline(node.Geometry, exitAnchor);
                AppendIntervening(waypoints, node.Geometry, entryPos, exitPos);
                waypoints.Add(exitAnchor);
            }

            // Everything appended during this node's turn belongs to this node.
            while (surfaces is not null && surfaces.Count < waypoints.Count)
            {
                surfaces.Add(nodePath[i]);
            }
        }

        return waypoints;
    }

    // The precomputed portal point for the graph edge u -> v (u and v are known-adjacent, e.g. from
    // an A*-produced node path, so this always finds a match).
    private Vec2 PortalBetween(int u, int v)
    {
        var neighbors = _adjacency[u];
        var portals = _portals[u];
        for (var k = 0; k < neighbors.Length; k++)
        {
            if (neighbors[k] == v)
            {
                return portals[k];
            }
        }

        // Defensive fallback (should not happen for an adjacency-derived path): the neighbour's
        // centroid is still a deterministic, on-graph anchor point.
        return _nodes[v].Centroid;
    }

    // ---- B3: HalfWidthsAlong (design §4.2) ------------------------------------------------------

    /// Per-vertex half-width: each vertex is re-located to its nearest node (same grid `NearestLane`
    /// uses), and the owning node's `HalfWidth` is returned -- lane Width/2 for Sidewalk/Crossing
    /// vertices, the nominal WalkingArea half-width for those. Falls back to the interface's 0.5 m
    /// default only when the graph has no nodes at all (an empty network).
    public IReadOnlyList<double> HalfWidthsAlong(IReadOnlyList<Vec2> path)
    {
        var widths = new double[path.Count];
        for (var i = 0; i < path.Count; i++)
        {
            var nearest = NearestLane(path[i]);
            widths[i] = nearest is null ? 0.5 : _nodes[nearest.Value.NodeIndex].HalfWidth;
        }

        return widths;
    }

    // ---- C2: ElevationsAlong (design §3.4) ------------------------------------------------------

    /// Per-vertex surface elevation. `vertexSurfaces` is MANDATORY (pass an explicit `null` to ask for
    /// the plan-view fallback below) -- there is no one-argument sibling, so dropping the provenance is
    /// always a visible decision at the call site rather than an omission.
    ///
    /// Returns 0.0 for a vertex whose node has no elevation channel (a 2-D net), matching the
    /// interface's flat default exactly, so a 2-D net is bit-identical to before this existed.
    ///
    /// With PROVENANCE (`vertexSurfaces` from the routing overload above) the height is read off the
    /// node the router actually walked -- so a ped crossing a footbridge follows the bridge and one
    /// passing underneath follows the ground, even though the two are the same point in plan view.
    ///
    /// WITHOUT provenance it falls back to the nearest node in plan view. That is correct wherever
    /// surfaces do not overlap, and unavoidably ambiguous where they do: from directly beneath the
    /// bridge both candidates are equidistant and the tie-break decides. Prefer the provenance form for
    /// anything that will run on a real net.
    public IReadOnlyList<double> ElevationsAlong(IReadOnlyList<Vec2> path, IReadOnlyList<int>? vertexSurfaces)
    {
        var usable = vertexSurfaces is not null && vertexSurfaces.Count == path.Count;
        var elevations = new double[path.Count];

        for (var i = 0; i < path.Count; i++)
        {
            int nodeIndex;
            if (usable)
            {
                nodeIndex = vertexSurfaces![i];
                if (nodeIndex < 0 || nodeIndex >= _nodes.Length)
                {
                    continue; // a foreign/stale id -- stay flat rather than index into the wrong graph
                }
            }
            else
            {
                var nearest = NearestLane(path[i]);
                if (nearest is null)
                {
                    continue; // stays 0.0 -- the documented flat fallback
                }

                nodeIndex = nearest.Value.NodeIndex;
            }

            var node = _nodes[nodeIndex];
            elevations[i] = PolylineElevation.AtNearestPoint(node.Geometry, node.GeometryZ, path[i]);
        }

        return elevations;
    }

    /// The node index whose geometry is nearest `p` in plan view, or -1 when the graph is empty --
    /// the provenance a caller can attach to a point it derived itself (e.g. an interpolated split
    /// point on a sub-path). Exposed so such a caller re-uses the router's own lookup rather than
    /// inventing a second one.
    public int SurfaceAt(Vec2 p)
    {
        var nearest = NearestLane(p);
        return nearest?.NodeIndex ?? -1;
    }

    // ---- Construction helpers -------------------------------------------------------------------

    // design §3.1: one node per pedestrian lane element, in a fixed deterministic order --
    // WalkingAreas, then Crossings, then Sidewalks, each group sorted by Id (ordinal) -- so
    // construction order (and therefore every node Index) is stable across runs and hosts.
    private static RouteNode[] BuildNodes(PedNetwork network)
    {
        var nodes = new List<RouteNode>(
            network.WalkingAreas.Count + network.Crossings.Count + network.Sidewalks.Count);
        var index = 0;

        foreach (var wa in network.WalkingAreas.OrderBy(w => w.Id, StringComparer.Ordinal))
        {
            nodes.Add(new RouteNode(index++, wa.Id, RouteNodeKind.WalkingArea, wa.Polygon, HalfWidthOf(wa.Width), wa.PolygonZ));
        }

        foreach (var crossing in network.Crossings.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            nodes.Add(new RouteNode(index++, crossing.Id, RouteNodeKind.Crossing, crossing.Shape, HalfWidthOf(crossing.Width), crossing.ShapeZ));
        }

        foreach (var sidewalk in network.Sidewalks.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            nodes.Add(new RouteNode(index++, sidewalk.Id, RouteNodeKind.Sidewalk, sidewalk.Shape, HalfWidthOf(sidewalk.Width), sidewalk.ShapeZ));
        }

        return nodes.ToArray();
    }

    // Matches WalkablePolygonBaker's existing convention: Width/2 when a positive width is declared,
    // else a sane 0.5 m default.
    private static double HalfWidthOf(double width) => width > 0.0 ? width / 2.0 : 0.5;

    // design §3.2: bidirectional adjacency + a precomputed portal point per PedConnection. A
    // connection naming an id this graph has no node for (should not happen for a well-formed
    // PedNetwork -- lane ids share one id space, design §2 -- but defensive against a malformed net)
    // is silently ignored, never thrown: an unreachable/isolated node degrades FindPath to `null`,
    // never a crash.
    private static (int[][] Adjacency, Vec2[][] Portals) BuildAdjacency(
        RouteNode[] nodes, IReadOnlyDictionary<string, int> idToIndex, IReadOnlyList<PedConnection> connections)
    {
        var adjacency = new List<int>[nodes.Length];
        var portals = new List<Vec2>[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            adjacency[i] = new List<int>();
            portals[i] = new List<Vec2>();
        }

        foreach (var connection in connections)
        {
            if (!idToIndex.TryGetValue(connection.AId, out var a) || !idToIndex.TryGetValue(connection.BId, out var b))
            {
                continue;
            }

            var portal = ClosestVertexPairMidpoint(nodes[a].Geometry, nodes[b].Geometry);
            adjacency[a].Add(b);
            portals[a].Add(portal);
            adjacency[b].Add(a);
            portals[b].Add(portal);
        }

        var adjacencyArrays = new int[nodes.Length][];
        var portalArrays = new Vec2[nodes.Length][];
        for (var i = 0; i < nodes.Length; i++)
        {
            adjacencyArrays[i] = adjacency[i].ToArray();
            portalArrays[i] = portals[i].ToArray();
        }

        return (adjacencyArrays, portalArrays);
    }

    // design §3.2: the portal point for a connection is the midpoint of the closest vertex pair
    // between the two nodes' geometries -- a simple, deterministic stand-in for "the geometric
    // junction of the two lanes" that needs no continuous optimization. Deterministic tie-break: the
    // first (lowest i, then lowest j) pair achieving the minimum wins (only a strict `<` ever
    // updates the best-so-far).
    private static Vec2 ClosestVertexPairMidpoint(IReadOnlyList<Vec2> a, IReadOnlyList<Vec2> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return Vec2.Zero;
        }

        var bestDistSq = double.MaxValue;
        var bestMid = Vec2.Zero;
        for (var i = 0; i < a.Count; i++)
        {
            for (var j = 0; j < b.Count; j++)
            {
                var distSq = (a[i] - b[j]).AbsSq;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestMid = 0.5 * (a[i] + b[j]);
                }
            }
        }

        return bestMid;
    }

    // ---- Segment helpers (shared by grid construction, NearestLane, and cell-size estimation) ----

    // Sidewalk/Crossing geometry is an OPEN polyline (N-1 segments); WalkingArea geometry is a
    // CLOSED ring per the PolygonGeometry convention (N segments, wrapping). A single-vertex node
    // (degenerate, should not occur in real data) is treated as one zero-length "segment" (p, p) so
    // it is still indexable/locatable rather than silently dropped.
    private static int SegmentCount(RouteNode node)
    {
        if (node.Geometry.Count <= 1)
        {
            return node.Geometry.Count; // 0 or 1
        }

        return node.Kind == RouteNodeKind.WalkingArea ? node.Geometry.Count : node.Geometry.Count - 1;
    }

    private static (Vec2 A, Vec2 B) Segment(RouteNode node, int segIndex)
    {
        if (node.Geometry.Count == 1)
        {
            return (node.Geometry[0], node.Geometry[0]);
        }

        if (node.Kind == RouteNodeKind.WalkingArea)
        {
            return (node.Geometry[segIndex], node.Geometry[(segIndex + 1) % node.Geometry.Count]);
        }

        return (node.Geometry[segIndex], node.Geometry[segIndex + 1]);
    }

    // design §3.3: cell size ~ median segment length, with a sensible floor so degenerate/near-zero
    // segments (or a network with none at all) never collapse the grid to a pathological cell count.
    private static double ComputeCellSize(RouteNode[] nodes)
    {
        var lengths = new List<double>();
        foreach (var node in nodes)
        {
            var segCount = SegmentCount(node);
            for (var s = 0; s < segCount; s++)
            {
                var (a, b) = Segment(node, s);
                lengths.Add((b - a).Abs);
            }
        }

        if (lengths.Count == 0)
        {
            return 10.0;
        }

        lengths.Sort();
        var median = lengths[lengths.Count / 2];
        return median > 0.5 ? median : 1.0;
    }

    // design §3.3: a uniform grid over lane segment AABBs -- each segment is registered in every
    // cell its (axis-aligned) bounding box overlaps, so a query's cell + ring always finds any
    // segment that could be nearest, regardless of segment length relative to cell size.
    // `maxRadius` is the ring radius that is guaranteed to cover the whole populated grid extent
    // from any query cell, so NearestLane's widening loop always terminates.
    private static Dictionary<(int Cx, int Cy), List<(int NodeIndex, int SegIndex)>> BuildGrid(
        RouteNode[] nodes, double cellSize, out int maxRadius)
    {
        var grid = new Dictionary<(int Cx, int Cy), List<(int NodeIndex, int SegIndex)>>();
        var minCx = int.MaxValue;
        var maxCx = int.MinValue;
        var minCy = int.MaxValue;
        var maxCy = int.MinValue;

        for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            var segCount = SegmentCount(nodes[nodeIndex]);
            for (var segIndex = 0; segIndex < segCount; segIndex++)
            {
                var (a, b) = Segment(nodes[nodeIndex], segIndex);
                var xmin = Math.Min(a.X, b.X);
                var xmax = Math.Max(a.X, b.X);
                var ymin = Math.Min(a.Y, b.Y);
                var ymax = Math.Max(a.Y, b.Y);

                var cx0 = (int)Math.Floor(xmin / cellSize);
                var cx1 = (int)Math.Floor(xmax / cellSize);
                var cy0 = (int)Math.Floor(ymin / cellSize);
                var cy1 = (int)Math.Floor(ymax / cellSize);

                for (var cx = cx0; cx <= cx1; cx++)
                {
                    for (var cy = cy0; cy <= cy1; cy++)
                    {
                        var key = (cx, cy);
                        if (!grid.TryGetValue(key, out var list))
                        {
                            list = new List<(int, int)>();
                            grid[key] = list;
                        }

                        list.Add((nodeIndex, segIndex));

                        minCx = Math.Min(minCx, cx);
                        maxCx = Math.Max(maxCx, cx);
                        minCy = Math.Min(minCy, cy);
                        maxCy = Math.Max(maxCy, cy);
                    }
                }
            }
        }

        maxRadius = grid.Count == 0 ? 1 : Math.Max(maxCx - minCx, maxCy - minCy) + 2;
        return grid;
    }

    // ---- Polyline projection/slicing helpers (shared by SameNodeSubPath and AssemblePolyline) ----

    // The position of `p`'s nearest point on the OPEN polyline `shape`, expressed as
    // `segmentIndex + t` (t in [0,1]) -- monotonically increasing along the shape's own vertex
    // order, exactly like SumoNavMesh's NearestPositionOnSpine. A shape vertex `idx` sits at
    // position exactly `idx`, which is all AppendIntervening needs to select (and order) interior
    // vertices strictly between two projected positions.
    private static double NearestPositionOnPolyline(IReadOnlyList<Vec2> shape, Vec2 p)
    {
        if (shape.Count < 2)
        {
            return 0.0;
        }

        var bestDistSq = double.MaxValue;
        var bestPos = 0.0;
        for (var s = 0; s + 1 < shape.Count; s++)
        {
            var a = shape[s];
            var b = shape[s + 1];
            var candidate = PolygonGeometry.NearestPointOnSegment(a, b, p);
            var distSq = (candidate - p).AbsSq;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                var segLenSq = (b - a).AbsSq;
                var t = segLenSq > DegenerateLengthSq
                    ? Math.Clamp(Vec2.Dot(candidate - a, b - a) / segLenSq, 0.0, 1.0)
                    : 0.0;
                bestPos = s + t;
            }
        }

        return bestPos;
    }

    // Appends `shape`'s own interior vertices strictly between `entryPos` and `exitPos`, in the
    // direction from entry to exit (ascending shape order if entry <= exit, descending otherwise) --
    // the sub-slice of the centreline threaded between two projected positions (design §4.1).
    private static void AppendIntervening(List<Vec2> waypoints, IReadOnlyList<Vec2> shape, double entryPos, double exitPos)
    {
        if (shape.Count < 2)
        {
            return;
        }

        if (entryPos <= exitPos)
        {
            for (var idx = 1; idx < shape.Count - 1; idx++)
            {
                if (idx > entryPos && idx < exitPos)
                {
                    waypoints.Add(shape[idx]);
                }
            }
        }
        else
        {
            for (var idx = shape.Count - 2; idx >= 1; idx--)
            {
                if (idx < entryPos && idx > exitPos)
                {
                    waypoints.Add(shape[idx]);
                }
            }
        }
    }
}
