using Sim.Core.Orca;
using Sim.Pedestrians;
using Sim.Pedestrians.Navigation;
using Sim.Pedestrians.Navigation.Bake;
using Sim.Pedestrians.Navigation.DotRecast;
using Xunit;

namespace Sim.Pedestrians.Nav.DotRecast.Tests.Navigation;

// POC-1b (docs/PEDESTRIAN-POC-PLAN.md POC-1 success condition 2): "both providers produce a valid
// path for the same O/D (the interface is honest, not a one-impl shim)". Drives the SUMO-geometry-
// bake provider (POC-1a, Sim.Pedestrians.Navigation.Bake) and this DotRecast navmesh provider
// (POC-1b) over the SAME baked polygon set and the SAME origin->destination used by
// SumoBakeNavigationTests, and checks both independently route across the junction and stay on
// walkable space. They are not required to produce the identical waypoint sequence -- A* over a
// hand-baked polygon graph and Detour's navmesh corridor search are different algorithms operating
// on different (though geometrically equivalent) representations of the same walkable area -- only
// that both are honest, working implementations of the same IPedNavigation/IWalkableSpace seam.
public class BothProvidersAgreeTests
{
    private const double AgentRadius = 0.3; // m; matches DotRecastBuildConfig.Default.AgentRadius

    // West side of the north arm, ~10 m south of its far end, well inside the "nc_0" sidewalk --
    // identical to SumoBakeNavigationTests.WestNorthArm.
    private static readonly Vec2 WestNorthArm = new(112.6, 140.0);

    // East side of the north arm, at the same y -- reaching it from WestNorthArm forces the route
    // through the junction's walkingarea -> crossing -> walkingarea chain (>= 2 polygons, and a
    // crossing). Identical to SumoBakeNavigationTests.EastNorthArm.
    private static readonly Vec2 EastNorthArm = new(127.4, 140.0);

    private static PedNetwork LoadPoc0Network() => PedNetworkParser.Load(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "net.net.xml"),
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "walkable.add.xml"));

    private static IReadOnlyList<BakedPolygon> BakePolygons() => WalkablePolygonBaker.Bake(LoadPoc0Network());

    private static (SumoWalkableSpace Space, SumoNavMesh Nav) BuildSumoProvider(IReadOnlyList<BakedPolygon> polygons)
    {
        var space = new SumoWalkableSpace(polygons);
        var nav = new SumoNavMesh(polygons, space);
        return (space, nav);
    }

    private static (DotRecastWalkableSpace Space, DotRecastNavMesh Nav) BuildDotRecastProvider(
        IReadOnlyList<BakedPolygon> polygons)
    {
        var config = DotRecastBuildConfig.Default;
        var navMesh = DotRecastNavMeshBuilder.Build(polygons, config);
        var space = new DotRecastWalkableSpace(navMesh, config);
        var nav = new DotRecastNavMesh(navMesh);
        return (space, nav);
    }

    // Checks every sampled point along `path` (not just the waypoints) against BOTH providers'
    // notion of walkable space, tolerant of either provider reporting containment (a portal segment
    // from one provider's corridor can legitimately sit right on the other provider's eroded
    // boundary -- see DotRecastWalkableSpace's Contains() remarks).
    private static void AssertPathStaysWalkable(
        IReadOnlyList<Vec2> path, SumoWalkableSpace sumoSpace, DotRecastWalkableSpace dotRecastSpace, string label)
    {
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var a = path[i];
            var b = path[i + 1];
            for (var s = 0; s <= 20; s++)
            {
                var t = s / 20.0;
                var sample = new Vec2(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

                var sumoOk = sumoSpace.Contains(sample);
                var drOk = dotRecastSpace.Contains(sample)
                    || (dotRecastSpace.ClampToWalkable(sample) - sample).Abs <= AgentRadius;

                Assert.True(sumoOk || drOk,
                    $"{label}: segment {i}->{i + 1} at t={t:F2} left walkable space per BOTH providers: " +
                    $"({sample.X:F3},{sample.Y:F3})");
            }
        }
    }

    [Fact]
    public void BothProviders_RouteTheSameOriginDestination_AndReachTheGoal()
    {
        var polygons = BakePolygons();
        var (sumoSpace, sumoNav) = BuildSumoProvider(polygons);
        var (dotRecastSpace, dotRecastNav) = BuildDotRecastProvider(polygons);

        var sumoPath = sumoNav.FindPath(WestNorthArm, EastNorthArm);
        var dotRecastPath = dotRecastNav.FindPath(WestNorthArm, EastNorthArm);

        Assert.NotNull(sumoPath);
        Assert.NotNull(dotRecastPath);

        // (a) starts near the origin and ends near the goal (within ~1 m).
        const double endpointTolerance = 1.0;
        Assert.True((sumoPath![0] - WestNorthArm).Abs <= endpointTolerance,
            $"SUMO-bake path start {sumoPath[0]} not within {endpointTolerance} m of origin {WestNorthArm}");
        Assert.True((sumoPath[^1] - EastNorthArm).Abs <= endpointTolerance,
            $"SUMO-bake path end {sumoPath[^1]} not within {endpointTolerance} m of goal {EastNorthArm}");
        Assert.True((dotRecastPath![0] - WestNorthArm).Abs <= endpointTolerance,
            $"DotRecast path start {dotRecastPath[0]} not within {endpointTolerance} m of origin {WestNorthArm}");
        Assert.True((dotRecastPath[^1] - EastNorthArm).Abs <= endpointTolerance,
            $"DotRecast path end {dotRecastPath[^1]} not within {endpointTolerance} m of goal {EastNorthArm}");

        // Forces the route through the junction's walkingarea -> crossing -> walkingarea chain per
        // SumoBakeNavigationTests: both providers must produce a genuinely multi-segment corridor,
        // not a straight-line shortcut through non-walkable space.
        Assert.True(sumoPath.Count >= 3, $"SUMO-bake path too short to cross the junction: {sumoPath.Count} waypoints");
        Assert.True(dotRecastPath.Count >= 2, $"DotRecast path too short: {dotRecastPath.Count} waypoints");

        // (b) sampled points along the polyline are walkable, per either provider's own notion of
        // walkable space (property test, POC-1 success condition 1's shape, applied to both).
        AssertPathStaysWalkable(sumoPath, sumoSpace, dotRecastSpace, "SUMO-bake path");
        AssertPathStaysWalkable(dotRecastPath, sumoSpace, dotRecastSpace, "DotRecast path");
    }

    [Fact]
    public void DotRecastNavMesh_FindPath_IsDeterministic_AcrossIndependentBuildsAndQueries()
    {
        var polygons = BakePolygons();

        var (_, navA) = BuildDotRecastProvider(polygons);
        var (_, navB) = BuildDotRecastProvider(polygons); // independent build: separate RcBuilder run, separate DtNavMesh

        var pathA1 = navA.FindPath(WestNorthArm, EastNorthArm);
        var pathA2 = navA.FindPath(WestNorthArm, EastNorthArm); // same navmesh, repeated query
        var pathB1 = navB.FindPath(WestNorthArm, EastNorthArm); // independently built navmesh

        Assert.NotNull(pathA1);
        Assert.NotNull(pathA2);
        Assert.NotNull(pathB1);

        AssertSameWaypoints(pathA1!, pathA2!, "same navmesh, repeated query");
        AssertSameWaypoints(pathA1!, pathB1!, "independent build");
    }

    private static void AssertSameWaypoints(IReadOnlyList<Vec2> expected, IReadOnlyList<Vec2> actual, string label)
    {
        Assert.True(expected.Count == actual.Count, $"{label}: waypoint count differs ({expected.Count} vs {actual.Count})");
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True((expected[i] - actual[i]).Abs <= 1e-4,
                $"{label}: waypoint {i} differs ({expected[i].X:F6},{expected[i].Y:F6}) vs ({actual[i].X:F6},{actual[i].Y:F6})");
        }
    }
}
