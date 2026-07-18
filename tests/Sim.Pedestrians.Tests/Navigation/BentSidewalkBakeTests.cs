using Sim.Core.Orca;
using Sim.Pedestrians.Navigation.Bake;
using Xunit;

namespace Sim.Pedestrians.Tests.Navigation;

// P2-1 (docs/PEDESTRIAN-TASKS.md, PEDESTRIAN-DESIGN.md §4; POC-1a notes): hardens the SUMO-geometry
// bake for BENT (multi-segment) sidewalks. POC-1a's per-segment quad approximation left a bent
// sidewalk as several independently-buffered quads that could fail PolygonGraph's shared-edge
// adjacency test right at the bend (a real disconnection, not just a rounding error, whenever the
// two segments were not collinear). This hand-builds a small PedNetwork with a genuinely bent
// (L-shaped, 3-point) sidewalk lane -- no netconvert/SUMO toolchain needed -- and asserts:
//  1. The bent lane bakes to exactly ONE connected polygon (PolylineBuffer.Buffer), not fragments.
//  2. SumoNavMesh.FindPath across the bend stays inside walkable space end-to-end (no false
//     disconnection, no straight-line shortcut cutting outside the L-shape's actual footprint --
//     the naive "same polygon -> direct segment" fast path would fail this for a bent polygon;
//     SumoNavMesh threads through the lane's Spine instead, see its remarks).
//  3. A second, separate sidewalk lane feeding into the bent one via a shared end-cap edge still
//     connects and routes correctly (the pre-existing shared-edge adjacency, unaffected by P2-1).
public class BentSidewalkBakeTests
{
    private const double LaneWidth = 2.0; // half-width 1.0

    // An L-shaped sidewalk: (0,0) -> (10,0) -> (10,10) -- a 90-degree bend at (10,0).
    private static readonly Vec2[] BentShape = { new(0, 0), new(10, 0), new(10, 10) };

    private static PedNetwork SingleBentLaneNetwork() => new(
        Sidewalks: new[] { new PedLane("bent", "e_bent", LaneWidth, BentShape) },
        Crossings: Array.Empty<PedCrossing>(),
        WalkingAreas: Array.Empty<PedWalkingArea>(),
        WalkablePolygons: Array.Empty<WalkablePolygon>(),
        AccessPoints: Array.Empty<WalkableAccessPoint>());

    private static PedNetwork ApproachAndBentLaneNetwork() => new(
        Sidewalks: new[]
        {
            new PedLane("approach", "e_approach", LaneWidth, new[] { new Vec2(-10, 0), new Vec2(0, 0) }),
            new PedLane("bent", "e_bent", LaneWidth, BentShape),
        },
        Crossings: Array.Empty<PedCrossing>(),
        WalkingAreas: Array.Empty<PedWalkingArea>(),
        WalkablePolygons: Array.Empty<WalkablePolygon>(),
        AccessPoints: Array.Empty<WalkableAccessPoint>());

    [Fact]
    public void BentSidewalk_BakesToExactlyOneConnectedPolygon()
    {
        var polygons = WalkablePolygonBaker.Bake(SingleBentLaneNetwork());

        Assert.Single(polygons);
        var polygon = polygons[0];
        Assert.Equal(BakedPolygonKind.SidewalkSegment, polygon.Kind);
        Assert.Equal("bent", polygon.Id);
        Assert.NotNull(polygon.Spine);
        Assert.Equal(3, polygon.Spine!.Count);

        // A genuine 90-degree mitred strip has a fillable elbow -- expect more than the 4 corners a
        // simple straight quad would have (proves the bend actually produced extra elbow geometry,
        // not a degenerate/duplicate-point collapse).
        Assert.True(polygon.Vertices.Count >= 5,
            $"expected extra elbow geometry from the bend, got only {polygon.Vertices.Count} vertices");
    }

    [Fact]
    public void BentSidewalk_FindPath_AcrossTheBend_StaysInsideWalkableSpace_NoFalseShortcut()
    {
        var polygons = WalkablePolygonBaker.Bake(SingleBentLaneNetwork());
        var space = new SumoWalkableSpace(polygons);
        var nav = new SumoNavMesh(polygons, space);

        // Well inside each arm, straddling the bend -- a naive straight line between these two
        // points cuts diagonally across the L's concave inside corner, well outside the 1m-wide
        // strip (verified below), so this only passes if the bend is actually routed around.
        var start = new Vec2(1.0, 0.0);   // near the start of the horizontal arm
        var goal = new Vec2(10.0, 9.0);   // near the end of the vertical arm

        // Sanity check that this is a MEANINGFUL test, not a vacuous one: confirm the naive direct
        // segment the pre-fix code would have returned really does leave walkable space somewhere
        // along its length (otherwise a "no shortcut" assertion on the real path would prove
        // nothing).
        var naiveLeavesSpace = false;
        for (var s = 0; s <= 20; s++)
        {
            var t = s / 20.0;
            var sample = new Vec2(start.X + ((goal.X - start.X) * t), start.Y + ((goal.Y - start.Y) * t));
            if (!space.Contains(sample))
            {
                naiveLeavesSpace = true;
                break;
            }
        }

        Assert.True(naiveLeavesSpace, "test setup is not meaningful: the naive direct segment never left walkable space");

        var path = nav.FindPath(start, goal);
        Assert.NotNull(path);

        // Must actually route THROUGH the bend (at least one interior waypoint beyond start/goal),
        // not just report itself connected while still cutting the corner.
        Assert.True(path!.Count >= 3, $"expected a path threaded through the bend, got {path.Count} waypoints");

        for (var i = 0; i + 1 < path.Count; i++)
        {
            var a = path[i];
            var b = path[i + 1];
            for (var s = 0; s <= 20; s++)
            {
                var t = s / 20.0;
                var sample = new Vec2(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
                Assert.True(space.Contains(sample),
                    $"segment {i}->{i + 1} at t={t:F2} left walkable space at the bend: ({sample.X:F3},{sample.Y:F3})");
            }
        }
    }

    [Fact]
    public void TwoLanes_ApproachFeedsIntoBentSidewalk_ConnectsViaSharedEdge_RoutesCorrectly()
    {
        var polygons = WalkablePolygonBaker.Bake(ApproachAndBentLaneNetwork());
        Assert.Equal(2, polygons.Count); // "approach" and "bent" -- two lanes, two polygons

        var space = new SumoWalkableSpace(polygons);
        var nav = new SumoNavMesh(polygons, space);

        var start = new Vec2(-5.0, 0.0);   // well inside "approach"
        var goal = new Vec2(5.0, 0.0);     // inside "bent", before its bend -- stays on the safe
                                            // (locally convex) horizontal arm so this test isolates
                                            // the shared-edge ADJACENCY between the two lanes,
                                            // independent of the elbow-threading behaviour the
                                            // previous test already covers.

        var path = nav.FindPath(start, goal);
        Assert.NotNull(path);

        for (var i = 0; i + 1 < path!.Count; i++)
        {
            var a = path[i];
            var b = path[i + 1];
            for (var s = 0; s <= 20; s++)
            {
                var t = s / 20.0;
                var sample = new Vec2(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
                Assert.True(space.Contains(sample),
                    $"segment {i}->{i + 1} at t={t:F2} left walkable space crossing lane boundary: " +
                    $"({sample.X:F3},{sample.Y:F3})");
            }
        }
    }
}
