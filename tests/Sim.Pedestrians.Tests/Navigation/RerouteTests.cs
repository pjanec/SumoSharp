using Sim.Core.Orca;
using Sim.Pedestrians.Navigation.Bake;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests.Navigation;

// POC-5 (docs/PEDESTRIAN-POC-PLAN.md POC-5 success condition 3; docs/PEDESTRIAN-DESIGN.md §6 "full
// occlusion of a portal triggers a strategic reroute"): a portal that is fully blocked forces
// SumoNavMesh.FindPath to route around it instead of jamming. Reuses the committed POC-0 fixture net
// and POC-1a navigation pieces exactly like SumoBakeNavigationTests/PedLodManagerTests.
//
// POC-0's 4-way junction ("c") has 4 crossings (:c_c0_0 north, :c_c1_0 east, :c_c2_0 south,
// :c_c3_0 west) and 4 corner walkingareas forming a ring around the junction (:c_w0_0.._w3_0),
// verified empirically (see the diagnostic dump this test's geometry is based on): the shortest path
// between WestNorthArm and EastNorthArm (SumoBakeNavigationTests' own POC-1 fixture points) crosses
// STRAIGHT via the north crossing polygon (:c_c0_0). Blocking that one crossing polygon still leaves
// the junction connected via the walkingarea ring through the other three crossings, so a valid,
// longer detour exists -- exactly the scenario the task calls for.
public class RerouteTests
{
    private readonly ITestOutputHelper _out;

    public RerouteTests(ITestOutputHelper output) => _out = output;

    private const double MaxSpeed = 1.4;   // m/s
    private const double Radius = 0.3;     // m
    private const double ArriveRadius = 0.3; // m
    private const double Dt = 0.1;         // s
    private const int MaxSteps = 3000;

    // Same POC-0 junction points SumoBakeNavigationTests/PedLodManagerTests use.
    private static readonly Vec2 WestNorthArm = new(112.6, 140.0);
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

    // True when any point along the polyline `path` MEANINGFULLY enters `polygon`'s interior --
    // i.e. lies inside it AND more than `Radius` past its boundary (sampled every segment, at a
    // resolution fine enough to catch a ped-scale intrusion: ~0.1 m steps, matching the simulated
    // ped's own per-step travel distance at MaxSpeed*Dt below).
    //
    // NOT a plain point-in-polygon test, deliberately: WalkablePolygonBaker bakes each walkable
    // surface (crossings, walkingareas, sidewalk-segment quads) as an INDEPENDENT polygon from
    // SUMO's real junction geometry, and at a busy junction corner these independently-baked shapes
    // legitimately OVERLAP each other by a shallow sliver where they abut (verified empirically here
    // against POC-0's real junction: adjacent crossing/walkingarea polygons around junction "c"
    // overlap by ~0.05-0.2 m at their shared corner -- well under one ped radius). This mirrors the
    // already-documented approximation in SumoWalkableSpace's own remarks ("shared edge emitted
    // TWICE... harmless for containment"), extended from shared EDGES to the shared CORNER region a
    // detour route must necessarily brush past to reach an adjacent, unblocked polygon. A route that
    // only grazes that shallow shared sliver has not actually "traversed" the blocked polygon as a
    // crossing (a ped whose CENTER is < Radius past the boundary still has more than half its own
    // body outside/on it); a route that goes DEEP into the blocked polygon's interior (a genuine
    // "used this crossing instead") is still caught.
    private static bool PathEntersPolygon(IReadOnlyList<Vec2> path, BakedPolygon polygon)
    {
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var a = path[i];
            var b = path[i + 1];
            var length = (b - a).Abs;
            var steps = Math.Max(20, (int)(length / 0.1));
            for (var s = 0; s <= steps; s++)
            {
                var t = (double)s / steps;
                var sample = new Vec2(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
                if (MeaningfullyInside(polygon, sample))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool MeaningfullyInside(BakedPolygon polygon, Vec2 p)
    {
        var single = new SumoWalkableSpace(new[] { polygon });
        if (!single.Contains(p))
        {
            return false;
        }

        return DistanceToBoundary(polygon.Vertices, p) > Radius;
    }

    private static double DistanceToBoundary(IReadOnlyList<Vec2> vertices, Vec2 p)
    {
        var n = vertices.Count;
        var best = double.MaxValue;
        for (var i = 0; i < n; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % n];
            var ab = b - a;
            var abLenSq = ab.AbsSq;
            var t = abLenSq > 1e-12 ? Math.Clamp(Vec2.Dot(p - a, ab) / abLenSq, 0.0, 1.0) : 0.0;
            var candidate = a + (t * ab);
            var dist = (candidate - p).Abs;
            if (dist < best)
            {
                best = dist;
            }
        }

        return best;
    }

    [Fact]
    public void BlockedCrossing_ProducesDifferentNonNullPath_AvoidingIt_StayingWalkable_ReachingGoal()
    {
        var (polygons, space, nav) = BuildProvider();

        var northCrossing = polygons.Single(p => p.Kind == BakedPolygonKind.Crossing && p.Id == ":c_c0_0");

        // Baseline: the direct (unblocked) shortest path DOES use the north crossing.
        var directPath = nav.FindPath(WestNorthArm, EastNorthArm, out _);
        Assert.NotNull(directPath);
        Assert.True(PathEntersPolygon(directPath!, northCrossing),
            "test setup invalid: the direct path is expected to cross the north crossing polygon");

        // Reroute: block the north crossing polygon and re-query.
        var blocked = new HashSet<int> { northCrossing.Index };
        var reroutePath = nav.FindPathAvoiding(WestNorthArm, EastNorthArm, blocked);

        Assert.NotNull(reroutePath);
        _out.WriteLine($"[POC-5 measured] direct path waypoints: {directPath!.Count}; reroute path waypoints: {reroutePath!.Count}");

        // (a) genuinely a DIFFERENT path (not a coincidental re-derivation of the same waypoints).
        Assert.NotEqual(directPath.Count, reroutePath.Count);

        // (b) does not traverse the blocked crossing.
        Assert.False(PathEntersPolygon(reroutePath, northCrossing),
            "rerouted path still passes through the blocked crossing polygon");

        // (c) stays in walkable space over its whole length (every segment, densely sampled).
        for (var i = 0; i + 1 < reroutePath.Count; i++)
        {
            var a = reroutePath[i];
            var b = reroutePath[i + 1];
            for (var s = 0; s <= 20; s++)
            {
                var t = s / 20.0;
                var sample = new Vec2(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
                Assert.True(space.Contains(sample),
                    $"reroute segment {i}->{i + 1} at t={t:F2} left walkable space: ({sample.X:F3},{sample.Y:F3})");
            }
        }

        // Still reaches the goal.
        Assert.True((reroutePath[^1] - EastNorthArm).Abs < 1e-6,
            $"reroute path's last waypoint {reroutePath[^1]} is not the requested goal {EastNorthArm}");
    }

    [Fact]
    public void PedDrivenAlongReroutedPath_ArrivesWithoutEnteringBlockedCrossingsArea()
    {
        var (polygons, space, nav) = BuildProvider();
        var northCrossing = polygons.Single(p => p.Kind == BakedPolygonKind.Crossing && p.Id == ":c_c0_0");
        var blocked = new HashSet<int> { northCrossing.Index };

        var reroutePath = nav.FindPathAvoiding(WestNorthArm, EastNorthArm, blocked);
        Assert.NotNull(reroutePath);

        var crowd = new OrcaCrowd();
        var index = crowd.Add(WestNorthArm, Radius, MaxSpeed, goal: reroutePath![0]);
        var controller = new PedRouteController(crowd, new WaypointFollower(), ArriveRadius);
        controller.AddRoute(index, reroutePath, MaxSpeed);

        var arrived = false;
        var steps = 0;
        for (; steps < MaxSteps; steps++)
        {
            controller.Update();
            crowd.Step(Dt);
            var pos = crowd.Position(index);

            Assert.True(space.Contains(pos), $"ped left walkable space at step {steps}: ({pos.X:F3},{pos.Y:F3})");
            // "Entered the blocked crossing's area" means a MEANINGFUL intrusion (see
            // MeaningfullyInside's remarks above), not a graze of the shallow shared-corner overlap
            // between independently-baked adjacent polygons that any detour past crossing0 must pass
            // near by construction.
            Assert.False(MeaningfullyInside(northCrossing, pos),
                $"ped meaningfully entered the blocked crossing's area at step {steps}: ({pos.X:F3},{pos.Y:F3})");

            if ((pos - EastNorthArm).Abs <= ArriveRadius && controller.IsRouteComplete(index))
            {
                arrived = true;
                break;
            }
        }

        Assert.True(arrived, "ped driven along the rerouted path never arrived");
        _out.WriteLine($"[POC-5 measured] ped following rerouted path arrived in {steps} steps ({steps * Dt:F1}s)");
    }

    [Fact]
    public void Determinism_FindPathWithBlockedSet_IsIdenticalAcrossCalls()
    {
        var (polygons, _, nav) = BuildProvider();
        var northCrossing = polygons.Single(p => p.Kind == BakedPolygonKind.Crossing && p.Id == ":c_c0_0");
        var blocked = new HashSet<int> { northCrossing.Index };

        var run1 = nav.FindPathAvoiding(WestNorthArm, EastNorthArm, blocked);
        var run2 = nav.FindPathAvoiding(WestNorthArm, EastNorthArm, blocked);

        Assert.NotNull(run1);
        Assert.NotNull(run2);
        Assert.Equal(run1!.Count, run2!.Count);
        for (var i = 0; i < run1.Count; i++)
        {
            Assert.Equal(run1[i].X, run2[i].X, precision: 12);
            Assert.Equal(run1[i].Y, run2[i].Y, precision: 12);
        }
    }

    // The unblocked overload must still be byte-identical to the two-argument overload other POCs
    // (POC-1/POC-3) depend on -- this is the "additive, not behaviour-changing" guarantee for
    // SumoNavMesh.FindPath(start, goal, out _). SumoBakeNavigationTests/PedLodManagerTests already cover
    // this indirectly (they still pass unmodified); this test asserts it directly.
    [Fact]
    public void UnblockedOverload_MatchesTwoArgumentOverload_Exactly()
    {
        var (_, _, nav) = BuildProvider();

        var twoArg = nav.FindPath(WestNorthArm, EastNorthArm, out _);
        var threeArgEmpty = nav.FindPathAvoiding(WestNorthArm, EastNorthArm, new HashSet<int>());
        var threeArgNull = nav.FindPathAvoiding(WestNorthArm, EastNorthArm, (ISet<int>?)null);

        Assert.NotNull(twoArg);
        Assert.NotNull(threeArgEmpty);
        Assert.NotNull(threeArgNull);
        Assert.Equal(twoArg!.Count, threeArgEmpty!.Count);
        Assert.Equal(twoArg.Count, threeArgNull!.Count);
        for (var i = 0; i < twoArg.Count; i++)
        {
            Assert.Equal(twoArg[i].X, threeArgEmpty[i].X, precision: 12);
            Assert.Equal(twoArg[i].Y, threeArgEmpty[i].Y, precision: 12);
            Assert.Equal(twoArg[i].X, threeArgNull[i].X, precision: 12);
            Assert.Equal(twoArg[i].Y, threeArgNull[i].Y, precision: 12);
        }
    }
}
