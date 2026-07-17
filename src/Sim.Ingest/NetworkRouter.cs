namespace Sim.Ingest;

// Rung B2 -- a minimum-travel-time edge-graph router, standalone infrastructure that is a
// prerequisite for B3's live reroute-around-blockage feature. Ported conceptually from
// sumo/src/utils/router/DijkstraRouter.h (DijkstraRouter<E,V>::compute) as driven by
// sumo/src/microsim/devices/MSDevice_Routing.h -- SUMO's live-rerouting device recomputes a
// vehicle's remaining route with a DijkstraRouter whose default effort operation is free-flow
// edge travel time (edge length / edge speed) when no dynamic edge-weight measurements are in
// use yet (MSDevice_Routing::buildVehicleDevices seeds the weight container with each edge's
// speed limit). DESIGN.md "Two futures" flags this as a deliberate scope addition: the project
// does not reimplement offline routing IMPORT (duarouter), but a *live* re-router reacting to
// external obstacles is new, intended infrastructure -- this class is the pathfinding core that
// a later live re-router (B3) will call, decoupled here so it can be validated alone against a
// duarouter golden before anything wires it to a reroute trigger.
//
// Graph construction: nodes are NORMAL edges only (ids not starting with ':' -- internal/
// junction-interior edges are skipped, matching how a route's edge list is itself expressed in
// normal-edge ids only). A directed arc A -> B exists iff there is a top-level <connection
// from="A" to="B"> in the parsed network AND both A and B are normal edges -- this is exactly
// SUMO's turn-permission graph (MSEdge::getViaSuccessors, ultimately backed by netconvert's
// <connection> list): an arc exists only where a movement is actually legal, not merely where
// two edges happen to meet at the same junction.
public sealed class NetworkRouter
{
    private readonly NetworkModel _network;
    private readonly IReadOnlyDictionary<string, List<string>> _successors;

    // P1E-3 (HIGH-DENSITY-P1E-DESIGN.md §1D, §9): per-edge downstream-end coordinate (the last
    // point of the edge's representative lane 0 shape) and the network-wide max lane speed --
    // both precomputed once at construction, purely for A*'s euclidean-distance/max-speed
    // heuristic. Nothing about Dijkstra's own behavior changes; these are additive fields.
    private readonly IReadOnlyDictionary<string, (double X, double Y)> _downstreamEnd;
    private readonly double _networkMaxSpeed;

    public NetworkRouter(NetworkModel network)
    {
        _network = network;

        // Build the adjacency map once, from the (from, to) pairs of every <connection> whose
        // endpoints are both normal (non-internal) edges known to the network.
        var successors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var connection in network.Connections)
        {
            if (IsInternal(connection.From) || IsInternal(connection.To))
            {
                continue;
            }

            if (!network.EdgesById.ContainsKey(connection.From) || !network.EdgesById.ContainsKey(connection.To))
            {
                continue;
            }

            if (!successors.TryGetValue(connection.From, out var list))
            {
                list = new List<string>();
                successors[connection.From] = list;
            }

            // Guard against a duplicate <connection from=A to=B> (e.g. two lanes both offering
            // the same A->B turn) producing a duplicate arc -- harmless for Dijkstra either way,
            // but keeps the adjacency list minimal.
            if (!list.Contains(connection.To))
            {
                list.Add(connection.To);
            }
        }

        _successors = successors;

        // P1E-3: downstream-end coordinate per edge (any edge -- an A* query's destination edge
        // need not be "normal" any more strictly than Route's own IsInternal guard already
        // requires) and the network-wide max lane speed (AStarRouter.h's heuristic denominator),
        // taken over EVERY edge in the network (internal included), matching the task's literal
        // "max over all edges of max lane speed" -- this can only be >= any normal edge's own max
        // speed, which is what admissibility actually requires.
        var downstreamEnd = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var maxSpeed = 0.0;
        foreach (var edge in network.Edges)
        {
            var lane0 = edge.Lanes[0];
            downstreamEnd[edge.Id] = lane0.Shape[^1];

            foreach (var lane in edge.Lanes)
            {
                if (lane.Speed > maxSpeed)
                {
                    maxSpeed = lane.Speed;
                }
            }
        }

        _downstreamEnd = downstreamEnd;
        _networkMaxSpeed = maxSpeed;
    }

    // Minimum-travel-time edge-id sequence from fromEdge to toEdge, inclusive of both
    // endpoints. Returns null when either edge id is unknown/internal, or when toEdge is
    // unreachable from fromEdge in the turn-permission graph.
    //
    // Ported from DijkstraRouter<E,V>::compute (sumo/src/utils/router/DijkstraRouter.h): a
    // classic Dijkstra over edges-as-nodes, effort = free-flow travel time
    // (length / max-lane-speed), with a deterministic frontier tie-break so the result does not
    // depend on iteration/dictionary order (SUMO's own tie-break is by edge numerical id, which
    // is assignment order; we use lexicographic edge id instead -- the golden net has no ties,
    // so this difference is never exercised, but the tie-break itself is still deterministic).
    public IReadOnlyList<string>? Route(string fromEdge, string toEdge) =>
        Route(fromEdge, toEdge, EmptyAvoidSet);

    private static readonly HashSet<string> EmptyAvoidSet = new(StringComparer.Ordinal);

    // B3: same Dijkstra as above, but never relaxes into (or starts/ends at) any edge in
    // `avoidEdges` -- the live reroute-around-blockage trigger's core query ("find me a path to
    // my destination that does not use this blocked edge"). SUMO's own live-rerouting device
    // achieves the same effect by giving a closed edge's effort +infinity for the closure's
    // duration (MSDevice_Routing / <rerouter> closingReroute) rather than removing it from the
    // graph -- behaviorally equivalent (a closed/avoided edge can never be the argmin of any
    // relaxation), and simpler to express directly as a skip here.
    // `avoidEdges` is `ISet<string>` (not `IReadOnlySet<string>`) so the signature compiles on
    // netstandard2.1, which predates IReadOnlySet<T>; only `.Contains` is called, so widening the
    // parameter is behavior-identical and every caller already passes a HashSet<string>.
    public IReadOnlyList<string>? Route(string fromEdge, string toEdge, ISet<string> avoidEdges) =>
        Route(fromEdge, toEdge, EdgeCost, avoidEdges);

    // P1E-3 (§1C/§9): same Dijkstra, but the per-edge cost is INJECTED rather than fixed to the
    // free-flow `EdgeCost` below -- the seam MSRoutingEngine's live/smoothed weights (P1E-2's
    // RerouteEdgeWeights.Effort) plug into. The two overloads above are a special case of this one
    // (`edgeCost: EdgeCost`), so they stay byte-identical to before -- nothing about their
    // observable behavior changed, only the implementation is now shared.
    public IReadOnlyList<string>? Route(string fromEdge, string toEdge, Func<string, double> edgeCost) =>
        Route(fromEdge, toEdge, edgeCost, EmptyAvoidSet);

    // P1E-3: the fully general Dijkstra entry point -- injected cost AND an avoid set.
    public IReadOnlyList<string>? Route(
        string fromEdge, string toEdge, Func<string, double> edgeCost, ISet<string> avoidEdges)
    {
        if (IsInternal(fromEdge) || IsInternal(toEdge))
        {
            return null;
        }

        if (!_network.EdgesById.ContainsKey(fromEdge) || !_network.EdgesById.ContainsKey(toEdge))
        {
            return null;
        }

        if (avoidEdges.Contains(fromEdge) || avoidEdges.Contains(toEdge))
        {
            return null;
        }

        if (fromEdge == toEdge)
        {
            return new[] { fromEdge };
        }

        var dist = new Dictionary<string, double> { [fromEdge] = 0.0 };
        var prev = new Dictionary<string, string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        // (dist, edgeId) frontier ordered by dist ascending, then edgeId lexicographically --
        // a SortedSet gives O(log n) pop-min/insert and a deterministic pop order for ties.
        var comparer = Comparer<(double Dist, string Edge)>.Create((a, b) =>
        {
            var cmp = a.Dist.CompareTo(b.Dist);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Edge, b.Edge);
        });
        var frontier = new SortedSet<(double Dist, string Edge)>(comparer) { (0.0, fromEdge) };

        while (frontier.Count > 0)
        {
            var (curDist, cur) = frontier.Min;
            frontier.Remove((curDist, cur));

            if (!visited.Add(cur))
            {
                continue;
            }

            if (cur == toEdge)
            {
                return ReconstructPath(prev, fromEdge, toEdge);
            }

            if (!_successors.TryGetValue(cur, out var successors))
            {
                continue;
            }

            foreach (var next in successors)
            {
                if (visited.Contains(next) || avoidEdges.Contains(next))
                {
                    continue;
                }

                var candidate = curDist + edgeCost(next);
                var hasExisting = dist.TryGetValue(next, out var existingDist);
                if (!hasExisting || candidate < existingDist)
                {
                    if (hasExisting)
                    {
                        frontier.Remove((existingDist, next));
                    }

                    dist[next] = candidate;
                    prev[next] = cur;
                    frontier.Add((candidate, next));
                }
            }
        }

        return null;
    }

    // Free-flow travel time for one edge = length / speed, using its representative lane
    // (length from lane 0, speed from the fastest lane -- all lanes are equal in the routing
    // test nets, matching MSEdge::getLength()/MSEdge::getSpeedLimit() for a single/uniform-lane
    // edge). Guards against a zero/negative speed producing infinite or negative cost. This is
    // the DEFAULT cost function every plain (no injected-cost) `Route` overload above uses.
    private double EdgeCost(string edgeId)
    {
        var edge = _network.EdgesById[edgeId];
        var length = edge.Lanes[0].Length;
        var speed = edge.Lanes.Max(l => l.Speed);
        return speed > 0.0 ? length / speed : double.PositiveInfinity;
    }

    // P1E-3 (HIGH-DENSITY-P1E-DESIGN.md §1D): A* over the same edges-as-nodes graph and the same
    // injected cost function as Route above, using an admissible + consistent heuristic --
    // euclidean straight-line distance from an edge's downstream end to the destination edge's
    // downstream end, divided by the network-wide max lane speed (AStarRouter.h:128-278's own
    // heuristic, minus the landmark table our config doesn't use). Because the heuristic never
    // overestimates the true remaining cost (no edge can be traversed faster than
    // networkMaxSpeed, and the straight-line distance never exceeds the actual remaining route
    // length) AND is consistent (the triangle inequality holds for euclidean distance), A* never
    // needs to re-expand a settled node -- it returns the IDENTICAL optimal-cost path Dijkstra
    // returns on the same `edgeCost`. This is a pure optimization over Route, not a behavior
    // change; it exists so the periodic reroute device (P1E-4) can use a cheaper search without
    // altering which route gets installed.
    public IReadOnlyList<string>? RouteAStar(string fromEdge, string toEdge) =>
        RouteAStar(fromEdge, toEdge, EdgeCost, EmptyAvoidSet);

    public IReadOnlyList<string>? RouteAStar(string fromEdge, string toEdge, Func<string, double> edgeCost) =>
        RouteAStar(fromEdge, toEdge, edgeCost, EmptyAvoidSet);

    public IReadOnlyList<string>? RouteAStar(
        string fromEdge, string toEdge, Func<string, double> edgeCost, ISet<string> avoidEdges)
    {
        if (IsInternal(fromEdge) || IsInternal(toEdge))
        {
            return null;
        }

        if (!_network.EdgesById.ContainsKey(fromEdge) || !_network.EdgesById.ContainsKey(toEdge))
        {
            return null;
        }

        if (avoidEdges.Contains(fromEdge) || avoidEdges.Contains(toEdge))
        {
            return null;
        }

        if (fromEdge == toEdge)
        {
            return new[] { fromEdge };
        }

        var targetPoint = _downstreamEnd[toEdge];
        double Heuristic(string edgeId)
        {
            var point = _downstreamEnd[edgeId];
            var dx = point.X - targetPoint.X;
            var dy = point.Y - targetPoint.Y;
            return Math.Sqrt(dx * dx + dy * dy) / _networkMaxSpeed;
        }

        // gScore = best known true cost from fromEdge; fScore = gScore + heuristic, the frontier
        // priority. Same (value, edgeId) tie-break as Dijkstra's `dist`-ordered frontier above --
        // string.CompareOrdinal on the edge id -- so equal-f frontier pops resolve identically.
        var gScore = new Dictionary<string, double> { [fromEdge] = 0.0 };
        var fScore = new Dictionary<string, double> { [fromEdge] = Heuristic(fromEdge) };
        var prev = new Dictionary<string, string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        var comparer = Comparer<(double F, string Edge)>.Create((a, b) =>
        {
            var cmp = a.F.CompareTo(b.F);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Edge, b.Edge);
        });
        var frontier = new SortedSet<(double F, string Edge)>(comparer) { (fScore[fromEdge], fromEdge) };

        while (frontier.Count > 0)
        {
            var (curF, cur) = frontier.Min;
            frontier.Remove((curF, cur));

            if (!visited.Add(cur))
            {
                continue;
            }

            if (cur == toEdge)
            {
                return ReconstructPath(prev, fromEdge, toEdge);
            }

            if (!_successors.TryGetValue(cur, out var successors))
            {
                continue;
            }

            var curG = gScore[cur];
            foreach (var next in successors)
            {
                if (visited.Contains(next) || avoidEdges.Contains(next))
                {
                    continue;
                }

                var candidateG = curG + edgeCost(next);
                var hasExisting = gScore.TryGetValue(next, out var existingG);
                if (!hasExisting || candidateG < existingG)
                {
                    if (hasExisting)
                    {
                        frontier.Remove((fScore[next], next));
                    }

                    gScore[next] = candidateG;
                    var f = candidateG + Heuristic(next);
                    fScore[next] = f;
                    prev[next] = cur;
                    frontier.Add((f, next));
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReconstructPath(
        IReadOnlyDictionary<string, string> prev, string fromEdge, string toEdge)
    {
        var path = new List<string> { toEdge };
        var cur = toEdge;
        while (cur != fromEdge)
        {
            cur = prev[cur];
            path.Add(cur);
        }

        path.Reverse();
        return path;
    }

    private static bool IsInternal(string edgeId) => edgeId.StartsWith(':');
}
