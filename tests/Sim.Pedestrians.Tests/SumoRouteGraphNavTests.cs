using Sim.Core.Orca;
using Sim.Pedestrians.Navigation.RouteGraph;
using Xunit;

namespace Sim.Pedestrians.Tests;

// Stage B (docs/LIVE-CITY-ARBITRARY-NET-TASKS.md B1-B4; docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §3/§4):
// SumoRouteGraphNav, the route-graph IPedNavigation provider that routes directly on the SUMO
// pedestrian edge/connection graph (no WalkablePolygonBaker bake). Fixtures are built in-code
// (no file I/O) as tiny hand-written PedNetwork records -- a "connected" fixture chaining
// sidewalk -> walkingarea -> crossing -> walkingarea -> sidewalk (so a route must thread all three
// node kinds and cross a crossing), and a "disconnected" fixture of two sidewalk islands sharing no
// PedConnection.
public class SumoRouteGraphNavTests
{
    private const double Eps = 1e-9;

    // ---- Connected fixture: sw_a -- wa_1 -- c_ab -- wa_2 -- sw_b ------------------------------
    //
    //   sw_a_0 (width 2, half 1.0): x in [-20,-5],  y = 0
    //   wa_1_0 (width 2.4, half 1.2, square):        x in [-5,-3], y in [-1,1]
    //   c_ab_0 (width 3, half 1.5, crossing):        x in [-3,3],  y = 0
    //   wa_2_0 (width 2.4, half 1.2, square):        x in [3,5],   y in [-1,1]
    //   sw_b_0 (width 2, half 1.0):                  x in [5,20],  y = 0
    //
    // Connections declared in the natural chain order; walking is bidirectional over them.
    private static PedNetwork ConnectedFixture()
    {
        var swA = new PedLane("sw_a_0", "e_a", 2.0, new[] { new Vec2(-20, 0), new Vec2(-5, 0) });
        var swB = new PedLane("sw_b_0", "e_b", 2.0, new[] { new Vec2(5, 0), new Vec2(20, 0) });

        // Shape carries a genuine interior vertex (0,0) -- not just the two endpoints -- so a route
        // that must cross the road visits an actual on-centreline crossing vertex (distance 0 from
        // the crossing, unambiguously nearer to it than to either flanking walkingarea corner) and
        // AssemblePolyline's slice-between-projections logic (design §4.1) has something to thread.
        var crossing = new PedCrossing(
            "c_ab_0", "j0", 3.0,
            Shape: new[] { new Vec2(-3, 0), new Vec2(0, 0), new Vec2(3, 0) },
            Outline: new[] { new Vec2(-3, -1.5), new Vec2(3, -1.5), new Vec2(3, 1.5), new Vec2(-3, 1.5) },
            CrossingEdges: Array.Empty<string>(),
            TlLogicId: null);

        var wa1 = new PedWalkingArea(
            "wa_1_0", "j0", 2.4,
            Polygon: new[] { new Vec2(-5, -1), new Vec2(-3, -1), new Vec2(-3, 1), new Vec2(-5, 1) });
        var wa2 = new PedWalkingArea(
            "wa_2_0", "j0", 2.4,
            Polygon: new[] { new Vec2(3, -1), new Vec2(5, -1), new Vec2(5, 1), new Vec2(3, 1) });

        return new PedNetwork(
            Sidewalks: new[] { swA, swB },
            Crossings: new[] { crossing },
            WalkingAreas: new[] { wa1, wa2 },
            WalkablePolygons: Array.Empty<WalkablePolygon>(),
            AccessPoints: Array.Empty<WalkableAccessPoint>())
        {
            PedConnections = new[]
            {
                new PedConnection(swA.Id, wa1.Id),
                new PedConnection(wa1.Id, crossing.Id),
                new PedConnection(crossing.Id, wa2.Id),
                new PedConnection(wa2.Id, swB.Id),
            },
        };
    }

    // Two sidewalk islands, far apart, sharing NO PedConnection -- geometrically and topologically
    // disconnected.
    private static PedNetwork DisconnectedFixture()
    {
        var islandX = new PedLane("isl_x_0", "e_x", 2.0, new[] { new Vec2(-100, 0), new Vec2(-90, 0) });
        var islandY = new PedLane("isl_y_0", "e_y", 2.0, new[] { new Vec2(90, 0), new Vec2(100, 0) });

        return new PedNetwork(
            Sidewalks: new[] { islandX, islandY },
            Crossings: Array.Empty<PedCrossing>(),
            WalkingAreas: Array.Empty<PedWalkingArea>(),
            WalkablePolygons: Array.Empty<WalkablePolygon>(),
            AccessPoints: Array.Empty<WalkableAccessPoint>());
        // PedConnections defaults to empty (PedNetwork's init-only default).
    }

    // ======================================================================================
    // B1 -- node/edge graph + spatial index (design §3)
    // ======================================================================================

    [Fact]
    public void Construction_DoesNotThrow_AndNodeCountMatchesLaneCounts()
    {
        var network = ConnectedFixture();
        var nav = new SumoRouteGraphNav(network);

        var expectedCount = network.Sidewalks.Count + network.Crossings.Count + network.WalkingAreas.Count;
        Assert.Equal(5, expectedCount);
        Assert.Equal(expectedCount, nav.Nodes.Count);
    }

    [Fact]
    public void NearestLane_ReturnsCorrectLane_ForPointsOnAndNearAKnownLane()
    {
        var nav = new SumoRouteGraphNav(ConnectedFixture());

        // Exactly on sw_a_0's centreline.
        var onLane = nav.NearestLane(new Vec2(-10, 0));
        Assert.NotNull(onLane);
        Assert.Equal("sw_a_0", nav.Nodes[onLane!.Value.NodeIndex].Id);

        // Near sw_a_0 (within its half-width of 1.0 m) but off the centreline -- still nearer to
        // sw_a_0 than to anything else in the fixture.
        var nearLane = nav.NearestLane(new Vec2(-10, 0.6));
        Assert.NotNull(nearLane);
        Assert.Equal("sw_a_0", nav.Nodes[nearLane!.Value.NodeIndex].Id);

        // On the crossing's centreline.
        var onCrossing = nav.NearestLane(new Vec2(0, 0));
        Assert.NotNull(onCrossing);
        Assert.Equal("c_ab_0", nav.Nodes[onCrossing!.Value.NodeIndex].Id);
    }

    [Fact]
    public void Adjacency_IsSymmetric_AndMatchesPedConnectionCount()
    {
        var network = ConnectedFixture();
        var nav = new SumoRouteGraphNav(network);

        var idOf = new Func<int, string>(idx => nav.Nodes[idx].Id);
        int IndexOf(string id)
        {
            for (var i = 0; i < nav.Nodes.Count; i++)
            {
                if (nav.Nodes[i].Id == id)
                {
                    return i;
                }
            }

            throw new InvalidOperationException($"node '{id}' not found");
        }

        // Each PedConnection produces both directions.
        var totalNeighborEntries = 0;
        for (var i = 0; i < nav.Adjacency.Count; i++)
        {
            totalNeighborEntries += nav.Adjacency[i].Count;
        }

        Assert.Equal(network.PedConnections.Count * 2, totalNeighborEntries);

        // Spot-check one declared connection is present in both directions.
        var swAIdx = IndexOf("sw_a_0");
        var wa1Idx = IndexOf("wa_1_0");
        Assert.Contains(wa1Idx, nav.Adjacency[swAIdx]);
        Assert.Contains(swAIdx, nav.Adjacency[wa1Idx]);
        _ = idOf; // (kept for readability of the helper above; avoids an unused-lambda warning)
    }

    // ======================================================================================
    // B2 -- FindPath (A* + polyline assembly, design §4/§4.1)
    // ======================================================================================

    private static readonly Vec2 StartOnSwA = new(-15, 0.2);
    private static readonly Vec2 GoalOnSwB = new(15, -0.2);

    [Fact]
    public void FindPath_OnConnectedFixture_ReturnsOnNetworkPolyline()
    {
        var nav = new SumoRouteGraphNav(ConnectedFixture());

        var path = nav.FindPath(StartOnSwA, GoalOnSwB);

        Assert.NotNull(path);
        Assert.True(path!.Count >= 2);

        // Every vertex lies within its OWNING lane's half-width (+ a small epsilon) of a ped lane.
        foreach (var vertex in path)
        {
            var nearest = nav.NearestLane(vertex);
            Assert.NotNull(nearest);
            var node = nav.Nodes[nearest!.Value.NodeIndex];
            var dist = (vertex - nearest.Value.Point).Abs;
            Assert.True(dist <= node.HalfWidth + 1e-6,
                $"vertex ({vertex.X:F3},{vertex.Y:F3}) is {dist:F3} m from node '{node.Id}' " +
                $"(half-width {node.HalfWidth:F3})");
        }
    }

    [Fact]
    public void FindPath_RouteThatMustCrossARoad_PassesThroughACrossingOwnedVertex()
    {
        var nav = new SumoRouteGraphNav(ConnectedFixture());

        var path = nav.FindPath(StartOnSwA, GoalOnSwB);
        Assert.NotNull(path);

        var sawCrossingVertex = false;
        foreach (var vertex in path!)
        {
            var nearest = nav.NearestLane(vertex);
            if (nearest is not null && nav.Nodes[nearest.Value.NodeIndex].Kind == RouteNodeKind.Crossing)
            {
                sawCrossingVertex = true;
                break;
            }
        }

        Assert.True(sawCrossingVertex, "expected at least one vertex owned by the crossing node");
    }

    [Fact]
    public void FindPath_OnDisconnectedFixture_ReturnsNull()
    {
        var nav = new SumoRouteGraphNav(DisconnectedFixture());

        var path = nav.FindPath(new Vec2(-95, 0), new Vec2(95, 0));

        Assert.Null(path);
    }

    [Fact]
    public void FindPath_StartAndGoalOnSameLane_ReturnsMultiPointSubPath()
    {
        var nav = new SumoRouteGraphNav(ConnectedFixture());

        var start = new Vec2(-15, 0);
        var goal = new Vec2(-10, 0);
        var path = nav.FindPath(start, goal);

        Assert.NotNull(path);
        Assert.True(path!.Count >= 2);
        Assert.Equal(start.X, path[0].X, precision: 9);
        Assert.Equal(start.Y, path[0].Y, precision: 9);
        Assert.Equal(goal.X, path[^1].X, precision: 9);
        Assert.Equal(goal.Y, path[^1].Y, precision: 9);
    }

    // ======================================================================================
    // B3 -- HalfWidthsAlong (design §4.2)
    // ======================================================================================

    [Fact]
    public void HalfWidthsAlong_MatchesSourceLaneWidths_ForSidewalkAndCrossingVertices()
    {
        var nav = new SumoRouteGraphNav(ConnectedFixture());

        var path = nav.FindPath(StartOnSwA, GoalOnSwB);
        Assert.NotNull(path);

        var widths = nav.HalfWidthsAlong(path!);
        Assert.Equal(path!.Count, widths.Count);

        var sawNonDefaultSidewalkOrCrossingWidth = false;
        for (var i = 0; i < path.Count; i++)
        {
            var nearest = nav.NearestLane(path[i]);
            Assert.NotNull(nearest);
            var node = nav.Nodes[nearest!.Value.NodeIndex];

            Assert.Equal(node.HalfWidth, widths[i], precision: 9);

            if (node.Kind is RouteNodeKind.Sidewalk or RouteNodeKind.Crossing)
            {
                // sw_a/sw_b half-width is 1.0, the crossing's is 1.5 -- both distinct from the bare
                // 0.5 m interface default -- so this proves the provider is not just falling back.
                Assert.NotEqual(0.5, node.HalfWidth);
                sawNonDefaultSidewalkOrCrossingWidth = true;
            }
        }

        Assert.True(sawNonDefaultSidewalkOrCrossingWidth,
            "expected the assembled path to visit at least one sidewalk/crossing vertex");
    }

    // ======================================================================================
    // B4 -- determinism (design §3.3/§10)
    // ======================================================================================

    [Fact]
    public void FindPath_CalledTwice_ReturnsSequenceIdenticalResults()
    {
        var nav = new SumoRouteGraphNav(ConnectedFixture());

        var first = nav.FindPath(StartOnSwA, GoalOnSwB);
        var second = nav.FindPath(StartOnSwA, GoalOnSwB);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Count, second!.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].X, second[i].X, precision: 12);
            Assert.Equal(first[i].Y, second[i].Y, precision: 12);
        }
    }

    [Fact]
    public void FindPath_AcrossManySeededODPairs_IsStableAcrossRepeatedCalls()
    {
        var nav = new SumoRouteGraphNav(ConnectedFixture());

        // A small deterministic sweep of O/D pairs along sw_a/sw_b (no System.Random -- a fixed
        // seeded stride over the lane range), each queried twice and compared.
        for (var i = 0; i <= 5; i++)
        {
            var t = i / 5.0;
            var start = new Vec2(-19 + (t * 13.0), 0.0); // sweeps across sw_a's span
            var goal = new Vec2(6 + (t * 13.0), 0.0);    // sweeps across sw_b's span

            var run1 = nav.FindPath(start, goal);
            var run2 = nav.FindPath(start, goal);

            Assert.NotNull(run1);
            Assert.NotNull(run2);
            Assert.Equal(run1!.Count, run2!.Count);
            for (var v = 0; v < run1.Count; v++)
            {
                Assert.Equal(run1[v].X, run2[v].X, precision: 12);
                Assert.Equal(run1[v].Y, run2[v].Y, precision: 12);
            }
        }
    }
}
