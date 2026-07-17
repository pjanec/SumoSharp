using Sim.Core.Orca;
using Sim.Pedestrians.Navigation;
using Sim.Pedestrians.Navigation.Bake;
using Xunit;

namespace Sim.Pedestrians.Tests.Navigation;

// POC-1a (docs/PEDESTRIAN-POC-PLAN.md POC-1; docs/PEDESTRIAN-DESIGN.md §4): the SUMO-geometry-bake
// navigation provider (WalkablePolygonBaker / SumoWalkableSpace / SumoNavMesh / WaypointFollower /
// PedRouteController), driving a pedestrian through the operational OrcaCrowd entirely via the
// committed IWalkableSpace / IPedNavigation / ILocalSteering seam. Asserts POC-1 success
// conditions 1, 3, 4 (condition 2, "both providers agree", is POC-1b's job, not this provider's).
//
// All scenarios route across POC-0's four-way junction: the ped network has two sidewalks per arm
// (SUMO's usual "one lane each direction" convention) that only connect to each other through the
// junction's walkingarea/crossing/walkingarea chain -- there is no direct link between them at the
// far (network-boundary) end. Start/goal points are placed close to the junction so the walkable
// route length stays commensurate with the straight-line distance (a route timing/slack assertion
// otherwise has to absorb the whole detour to the junction and back).
public class SumoBakeNavigationTests
{
    private const double MaxSpeed = 1.4;   // m/s, a typical adult walking speed
    private const double Radius = 0.3;     // m
    private const double ArriveRadius = 0.3; // m
    private const double Dt = 0.1;         // s
    private const int MaxSteps = 2000;     // simulation safety cap -- large enough for every scenario below

    // West side of the north arm, ~10 m south of its far end, well inside the "nc_0" sidewalk.
    private static readonly Vec2 WestNorthArm = new(112.6, 140.0);

    // East side of the north arm, at the same y -- reaching it from WestNorthArm forces the route
    // through the junction's walkingarea -> crossing -> walkingarea chain (>= 2 polygons, and a
    // crossing).
    private static readonly Vec2 EastNorthArm = new(127.4, 140.0);

    private static PedNetwork LoadPoc0Network() => PedNetworkParser.Load(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "net.net.xml"),
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "walkable.add.xml"));

    private static (IReadOnlyList<BakedPolygon> Polygons, SumoWalkableSpace Space, SumoNavMesh Nav) BuildProvider()
    {
        var polygons = WalkablePolygonBaker.Bake(LoadPoc0Network());
        var space = new SumoWalkableSpace(polygons);
        var nav = new SumoNavMesh(polygons, space);
        return (polygons, space, nav);
    }

    // Drives a single ped along `path` through a fresh OrcaCrowd via PedRouteController until it
    // arrives at `goal` (or MaxSteps is hit). Returns every stepped position (post-Step, in order)
    // so callers can assert containment/arrival/determinism against the same run.
    private static List<Vec2> RunSinglePed(Vec2 start, IReadOnlyList<Vec2> path, Vec2 goal, out int steps)
    {
        var crowd = new OrcaCrowd();
        var index = crowd.Add(start, Radius, MaxSpeed, goal: path[0]);
        var controller = new PedRouteController(crowd, new WaypointFollower(), ArriveRadius);
        controller.AddRoute(index, path, MaxSpeed);

        var trajectory = new List<Vec2>();
        steps = 0;
        while (steps < MaxSteps)
        {
            controller.Update();
            crowd.Step(Dt);
            steps++;
            var position = crowd.Position(index);
            trajectory.Add(position);

            if ((position - goal).Abs <= ArriveRadius && controller.IsRouteComplete(index))
            {
                break;
            }
        }

        return trajectory;
    }

    [Fact]
    public void FindPath_RoutesThroughJunctionPolygons_AndStaysInsideWalkableSpace()
    {
        var (_, space, nav) = BuildProvider();

        var path = nav.FindPath(WestNorthArm, EastNorthArm);

        Assert.NotNull(path);
        // Forces >= 2 polygons per the task: sidewalk -> walkingarea -> crossing -> walkingarea ->
        // sidewalk is 5 polygons / 4 portal waypoints beyond start, i.e. >= 3 waypoints total.
        Assert.True(path!.Count >= 3, $"expected a multi-portal path, got {path.Count} waypoints");

        // Every point ALONG each waypoint-to-waypoint segment (not just the waypoints themselves)
        // must lie in walkable space: portal midpoints sit on a shared boundary, so the segment
        // between consecutive portals stays inside the polygon they both bound (verified here,
        // empirically, against POC-0's real junction geometry -- see SumoNavMesh remarks).
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var a = path[i];
            var b = path[i + 1];
            for (var s = 0; s <= 20; s++)
            {
                var t = s / 20.0;
                var sample = new Vec2(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
                Assert.True(space.Contains(sample),
                    $"segment {i}->{i + 1} at t={t:F2} left walkable space: ({sample.X:F3},{sample.Y:F3})");
            }
        }
    }

    [Fact]
    public void SinglePed_RoutedThroughOrcaCrowd_ArrivesWithinSlack_EveryStepInsideWalkableSpace()
    {
        var (_, space, nav) = BuildProvider();
        var path = nav.FindPath(WestNorthArm, EastNorthArm);
        Assert.NotNull(path);

        var trajectory = RunSinglePed(WestNorthArm, path!, EastNorthArm, out var steps);

        // POC-1 success condition 1: arrives within dist/maxSpeed * slack seconds.
        const double slack = 3.0;
        var straightLineDist = (EastNorthArm - WestNorthArm).Abs;
        var maxAllowedSeconds = (straightLineDist / MaxSpeed) * slack;
        var actualSeconds = steps * Dt;
        Assert.True(actualSeconds <= maxAllowedSeconds,
            $"took {actualSeconds:F1}s, allowed {maxAllowedSeconds:F1}s (straight dist {straightLineDist:F1}m)");

        var finalPosition = trajectory[^1];
        Assert.True((finalPosition - EastNorthArm).Abs <= ArriveRadius,
            $"final position ({finalPosition.X:F3},{finalPosition.Y:F3}) is not within arrive radius of goal");

        // POC-1 success condition 1 (property test): every stepped position -- not just the
        // waypoints -- is inside walkable space.
        foreach (var position in trajectory)
        {
            Assert.True(space.Contains(position),
                $"stepped position ({position.X:F3},{position.Y:F3}) left walkable space");
        }
    }

    [Fact]
    public void TwoPeds_NegotiateSharedWalkingArea_NoOverlap_BothArrive()
    {
        var (_, space, nav) = BuildProvider();

        // Ped A: west arm -> east arm. Ped B: the reverse, same junction -- they meet head-on in
        // the walkingarea/crossing chain, forcing ORCA to negotiate (POC-1 success condition 3).
        var pathA = nav.FindPath(WestNorthArm, EastNorthArm);
        var pathB = nav.FindPath(EastNorthArm, WestNorthArm);
        Assert.NotNull(pathA);
        Assert.NotNull(pathB);

        var crowd = new OrcaCrowd();
        var indexA = crowd.Add(WestNorthArm, Radius, MaxSpeed, goal: pathA![0]);
        var indexB = crowd.Add(EastNorthArm, Radius, MaxSpeed, goal: pathB![0]);

        var controller = new PedRouteController(crowd, new WaypointFollower(), ArriveRadius);
        controller.AddRoute(indexA, pathA, MaxSpeed);
        controller.AddRoute(indexB, pathB, MaxSpeed);

        var sumOfRadii = Radius + Radius;
        const double overlapEps = 1e-3; // "small eps" per the task's no-overlap wording
        var minSeparation = double.MaxValue;
        var arrivedA = false;
        var arrivedB = false;

        var steps = 0;
        while (steps < MaxSteps)
        {
            controller.Update();
            crowd.Step(Dt);
            steps++;

            var posA = crowd.Position(indexA);
            var posB = crowd.Position(indexB);

            Assert.True(space.Contains(posA), $"ped A left walkable space at step {steps}: ({posA.X:F3},{posA.Y:F3})");
            Assert.True(space.Contains(posB), $"ped B left walkable space at step {steps}: ({posB.X:F3},{posB.Y:F3})");

            var separation = (posA - posB).Abs;
            minSeparation = Math.Min(minSeparation, separation);
            Assert.True(separation >= sumOfRadii - overlapEps,
                $"step {steps}: separation {separation:F4} < sum-of-radii {sumOfRadii:F4} - eps");

            arrivedA = arrivedA || ((posA - EastNorthArm).Abs <= ArriveRadius && controller.IsRouteComplete(indexA));
            arrivedB = arrivedB || ((posB - WestNorthArm).Abs <= ArriveRadius && controller.IsRouteComplete(indexB));
            if (arrivedA && arrivedB)
            {
                break;
            }
        }

        Assert.True(arrivedA, "ped A never arrived");
        Assert.True(arrivedB, "ped B never arrived");
        Assert.True(minSeparation >= sumOfRadii - overlapEps,
            $"minimum separation over the run ({minSeparation:F4}) violated sum-of-radii ({sumOfRadii:F4})");
    }

    [Fact]
    public void Determinism_FindPathAndTrajectory_AreIdenticalAcrossIndependentRuns()
    {
        var (_, _, nav) = BuildProvider();

        var pathRun1 = nav.FindPath(WestNorthArm, EastNorthArm);
        var pathRun2 = nav.FindPath(WestNorthArm, EastNorthArm);
        Assert.NotNull(pathRun1);
        Assert.NotNull(pathRun2);
        Assert.Equal(pathRun1!.Count, pathRun2!.Count);
        for (var i = 0; i < pathRun1.Count; i++)
        {
            Assert.Equal(pathRun1[i].X, pathRun2[i].X, precision: 12);
            Assert.Equal(pathRun1[i].Y, pathRun2[i].Y, precision: 12);
        }

        var trajectory1 = RunSinglePed(WestNorthArm, pathRun1, EastNorthArm, out var steps1);
        var trajectory2 = RunSinglePed(WestNorthArm, pathRun2, EastNorthArm, out var steps2);

        Assert.Equal(steps1, steps2);
        Assert.Equal(trajectory1.Count, trajectory2.Count);
        for (var i = 0; i < trajectory1.Count; i++)
        {
            Assert.Equal(trajectory1[i].X, trajectory2[i].X, precision: 12);
            Assert.Equal(trajectory1[i].Y, trajectory2[i].Y, precision: 12);
        }
    }
}
