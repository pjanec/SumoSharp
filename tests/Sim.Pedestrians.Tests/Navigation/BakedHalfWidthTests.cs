using Sim.Core.Orca;
using Sim.Pedestrians.Navigation.Bake;
using Xunit;

namespace Sim.Pedestrians.Tests.Navigation;

// W2a (docs/PEDESTRIAN-WEAVE-PRODUCTION-DESIGN.md): exposes the per-polygon sidewalk half-width
// from the baked navmesh so a route can be sampled for the deterministic pedestrian weave's clamp
// width. Hand-builds a small PedNetwork with a single straight sidewalk lane of known Width (no
// netconvert/SUMO toolchain needed -- same pattern as BentSidewalkBakeTests) and asserts:
//  1. Width propagates: a sidewalk baked with Width=W gets BakedPolygon.HalfWidth == W/2 exactly.
//  2. Fallback: a sidewalk with Width=0 (unset) bakes to the 0.5 m default half-width.
//  3. Sampling along a route: SumoNavMesh.HalfWidthsAlong(path) returns one width per path vertex,
//     every one of them the lane's own W/2 (the whole route lies on this one sidewalk).
//  4. Clamp safety (the design's success condition): no sampled half-width ever exceeds the baked
//     walkable strip's own half-width -- the weave can never be told it has more room than the
//     baked walkable strip actually provides.
public class BakedHalfWidthTests
{
    private const double LaneWidth = 3.0; // half-width 1.5

    private static readonly Vec2[] StraightShape = { new(0, 0), new(20, 0) };

    private static PedNetwork SingleLaneNetwork(double width) => new(
        Sidewalks: new[] { new PedLane("sw", "e_sw", width, StraightShape) },
        Crossings: Array.Empty<PedCrossing>(),
        WalkingAreas: Array.Empty<PedWalkingArea>(),
        WalkablePolygons: Array.Empty<WalkablePolygon>(),
        AccessPoints: Array.Empty<WalkableAccessPoint>());

    [Fact]
    public void Sidewalk_WithKnownWidth_BakesToExactHalfWidth()
    {
        var polygons = WalkablePolygonBaker.Bake(SingleLaneNetwork(LaneWidth));

        Assert.Single(polygons);
        Assert.Equal(LaneWidth / 2.0, polygons[0].HalfWidth);
    }

    [Fact]
    public void Sidewalk_WithZeroWidth_FallsBackToDefaultHalfWidth()
    {
        var polygons = WalkablePolygonBaker.Bake(SingleLaneNetwork(0.0));

        Assert.Single(polygons);
        Assert.Equal(0.5, polygons[0].HalfWidth);
    }

    [Fact]
    public void HalfWidthsAlong_SampledAlongRoute_MatchesLaneHalfWidth_EveryVertex()
    {
        var polygons = WalkablePolygonBaker.Bake(SingleLaneNetwork(LaneWidth));
        var nav = new SumoNavMesh(polygons);

        var start = new Vec2(1.0, 0.0);
        var goal = new Vec2(19.0, 0.0);
        var path = nav.FindPath(start, goal);
        Assert.NotNull(path);

        var widths = nav.HalfWidthsAlong(path!);

        Assert.Equal(path!.Count, widths.Count);
        foreach (var w in widths)
        {
            Assert.Equal(LaneWidth / 2.0, w);
        }
    }

    [Fact]
    public void HalfWidthsAlong_NeverExceedsBakedStripHalfWidth_ClampSafety()
    {
        var polygons = WalkablePolygonBaker.Bake(SingleLaneNetwork(LaneWidth));
        var nav = new SumoNavMesh(polygons);

        var start = new Vec2(1.0, 0.0);
        var goal = new Vec2(19.0, 0.0);
        var path = nav.FindPath(start, goal);
        Assert.NotNull(path);

        var widths = nav.HalfWidthsAlong(path!);

        var maxAllowed = (LaneWidth / 2.0) + 1e-9;
        foreach (var w in widths)
        {
            Assert.True(w <= maxAllowed, $"sampled half-width {w:F6} exceeds baked strip half-width {maxAllowed:F6}");
        }
    }
}
