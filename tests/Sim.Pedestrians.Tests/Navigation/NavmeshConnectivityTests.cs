using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sim.Core.Orca;
using Sim.Pedestrians.Navigation.Bake;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests.Navigation;

// P8-1b (docs/PEDESTRIAN-P8-1B-NAVMESH-CONNECTIVITY-DESIGN.md): the walkable bake must connect REAL
// netconvert geometry, where independently-buffered sidewalk/crossing strips OVERLAP the junction
// walkingArea by a ~0.05-0.2 m sliver instead of sharing exact edges/vertices. The prior bake connected
// only exact shared edges / 2-polygon shared corners, so real crops fragmented into ~1000 components and
// O/D routing failed (SUMOSHARP-P8-1-REAL-NET-NAVMESH.md). These tests reproduce the fragmentation from
// HAND-BUILT polygons (no SUMO/real geometry needed) and pin the area-overlap fix + its invariants.
public class NavmeshConnectivityTests
{
    private readonly ITestOutputHelper _out;

    public NavmeshConnectivityTests(ITestOutputHelper output) => _out = output;

    // Axis-aligned rectangle (CCW), as one baked polygon.
    private static BakedPolygon Rect(int index, string id, BakedPolygonKind kind, double x0, double y0, double x1, double y1)
        => new(index, id, kind, new[] { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) });

    // A mini-junction the way netconvert emits it: a central walkingArea, two sidewalk strips and a
    // crossing that each OVERLAP the walkingArea by 0.1 m (vertices NON-coincident, no shared exact edge),
    // and do NOT overlap one another (they meet the area from different sides). WA = [0,4]x[0,4].
    private static List<BakedPolygon> MiniJunction(bool includeWalkingArea)
    {
        var polys = new List<BakedPolygon>();
        var i = 0;
        if (includeWalkingArea)
        {
            polys.Add(Rect(i++, "wa", BakedPolygonKind.WalkingArea, 0, 0, 4, 4));
        }

        polys.Add(Rect(i++, "sa", BakedPolygonKind.SidewalkSegment, -10, 1, 0.1, 3));   // left, overlaps WA x in [0,0.1]
        polys.Add(Rect(i++, "sb", BakedPolygonKind.SidewalkSegment, 3.9, 1, 14, 3));    // right, overlaps WA x in [3.9,4]
        polys.Add(Rect(i++, "xc", BakedPolygonKind.Crossing, 1, -10, 3, 0.1));          // below, overlaps WA y in [0,0.1]
        return polys;
    }

    [Fact]
    public void AreaOverlap_ConnectsAbuttingStrips_ThroughTheWalkingArea_OneComponent()
    {
        var polys = MiniJunction(includeWalkingArea: true);
        var nav = new SumoNavMesh(polys);

        // The whole junction is one connected component (was 4 before the area-overlap pass: no strip shares
        // an exact edge/vertex with the walkingArea, so the shared-edge/vertex passes connected nothing).
        Assert.Equal(1, nav.ConnectedComponentCount());

        // A ped can route from the crossing across the junction to the far sidewalk...
        var path = nav.FindPath(new Vec2(2, -5), new Vec2(-5, 2), out _);
        Assert.NotNull(path);

        // ...and the route goes THROUGH the walkingArea (a waypoint lands inside WA=[0,4]x[0,4]), never a
        // direct crossing->sidewalk shortcut -- the POC-0 no-shortcut invariant, preserved by construction.
        Assert.Contains(path!, wp => wp.X is > 0.0 and < 4.0 && wp.Y is > 0.0 and < 4.0);

        _out.WriteLine($"[P8-1b] mini-junction: 1 component, cross-junction path = {path!.Count} waypoints via the area");
    }

    [Fact]
    public void AreaOverlap_NeverBridgesTwoNonAreaPolygons()
    {
        // Same layout but with the walkingArea REMOVED: the crossing and the two sidewalks overlap only the
        // (now absent) area, never each other, and all three are non-area kinds -- so the area-anchored
        // overlap pass connects nothing. They stay 3 separate components. This is the invariant that keeps a
        // crossing from being bridged directly to a sidewalk (the POC-0 shortcut bug).
        var polys = MiniJunction(includeWalkingArea: false);
        var nav = new SumoNavMesh(polys);

        Assert.Equal(3, nav.ConnectedComponentCount());
        Assert.Null(nav.FindPath(new Vec2(2, -5), new Vec2(-5, 2), out _)); // crossing -> far sidewalk unreachable
        _out.WriteLine("[P8-1b] non-area polygons are never overlap-bridged (crossing<->sidewalk stays disconnected)");
    }

    [Fact]
    public void SeparatedNonTouchingPolygons_AreNotOverlapConnected()
    {
        // A walkingArea and a crossing that neither touch nor overlap (a 0.5 m gap) must stay two components:
        // the overlap pass connects only genuine area overlaps, never a near-miss. (Guards against the pass
        // over-connecting once it stopped requiring an exact shared edge/vertex.)
        var wa = Rect(0, "wa", BakedPolygonKind.WalkingArea, 0, 0, 4, 4);
        var xc = Rect(1, "xc", BakedPolygonKind.Crossing, 4.5, 0, 8, 4); // 0.5 m gap from WA
        var nav = new SumoNavMesh(new List<BakedPolygon> { wa, xc });

        Assert.Equal(2, nav.ConnectedComponentCount());
        _out.WriteLine("[P8-1b] a non-touching gap is not an overlap -> not bridged");
    }

    [Fact]
    public void ConnectedComponentCount_CountsDisjointIslands()
    {
        var far = new List<BakedPolygon>
        {
            Rect(0, "a", BakedPolygonKind.WalkablePolygon, 0, 0, 2, 2),
            Rect(1, "b", BakedPolygonKind.WalkablePolygon, 100, 100, 102, 102),
        };
        Assert.Equal(2, new SumoNavMesh(far).ConnectedComponentCount());
    }

    [Fact]
    public void IrregularWitnessBox_ConnectsToOneComponent()
    {
        // P8-1b-5 real-net acceptance: the committed irregular witness (netgenerate --rand, sub-area session's
        // pedfrag repro) bakes to 222 disconnected components with the pre-fix graph (crowd peak 0). With the
        // area-overlap/abutment pass it must connect to ONE component and be routable end-to-end -- the exact
        // failure the fix targets, pinned against genuine netconvert geometry.
        var dir = RepoRoot();
        var net = Path.Combine(dir, "scenarios", "_ped", "subarea-irregular", "net.xml");
        var polygons = WalkablePolygonBaker.Bake(PedNetworkParser.Load(net));
        var nav = new SumoNavMesh(polygons);

        Assert.Equal(1, nav.ConnectedComponentCount());

        // Routable across the whole box (nearest polygons to opposite corners).
        var lo = polygons.OrderBy(p => p.Centroid.X + p.Centroid.Y).First().Centroid;
        var hi = polygons.OrderByDescending(p => p.Centroid.X + p.Centroid.Y).First().Centroid;
        Assert.NotNull(nav.FindPath(lo, hi, out _));

        _out.WriteLine($"[P8-1b] irregular witness box: {polygons.Count} polygons, {nav.ConnectedComponentCount()} component (was 222)");
    }

    // -- P8-1c: sidewalk<->sidewalk continuation bridging (docs/PEDESTRIAN-P8-1C-NAVMESH-CONTINUATION-DESIGN.md) --

    // A horizontal sidewalk strip (x0..x1 at height yc, half-width hw) carrying its centreline Spine -- the
    // travel axis the P8-1c continuation gate reads. (The plain Rect() helper leaves Spine null, which is why
    // the P8-1b MiniJunction tests are untouched by the continuation pass.)
    private static BakedPolygon HStrip(int index, string id, double x0, double x1, double yc, double hw)
        => new(index, id, BakedPolygonKind.SidewalkSegment,
            new[] { new Vec2(x0, yc - hw), new Vec2(x1, yc - hw), new Vec2(x1, yc + hw), new Vec2(x0, yc + hw) },
            new[] { new Vec2(x0, yc), new Vec2(x1, yc) });

    // A vertical sidewalk strip (y0..y1 at abscissa xc, half-width hw) + centreline Spine.
    private static BakedPolygon VStrip(int index, string id, double y0, double y1, double xc, double hw)
        => new(index, id, BakedPolygonKind.SidewalkSegment,
            new[] { new Vec2(xc - hw, y0), new Vec2(xc + hw, y0), new Vec2(xc + hw, y1), new Vec2(xc - hw, y1) },
            new[] { new Vec2(xc, y0), new Vec2(xc, y1) });

    [Fact]
    public void SidewalkContinuation_BridgesCollinearStrips_NoWalkingArea()
    {
        // Two collinear sidewalk strips meeting end-to-end with a 3 cm gap and NO walkingArea between them --
        // the witness's Mode-1 residual (a dropped/absent continuation walkingArea). The area-anchored P8-1b
        // pass leaves these two islands; the P8-1c continuation pass bridges them (outward end-tangents +x and
        // -x -> Dot -1 -> continuation) into ONE routable component.
        var polys = new List<BakedPolygon>
        {
            HStrip(0, "a", -10.0, 0.0, yc: 2.0, hw: 1.0),
            HStrip(1, "b", 0.03, 10.0, yc: 2.0, hw: 1.0), // 3 cm gap, collinear
        };
        var nav = new SumoNavMesh(polys);

        Assert.Equal(1, nav.ConnectedComponentCount());
        Assert.NotNull(nav.FindPath(new Vec2(-9.0, 2.0), new Vec2(9.0, 2.0), out _));
        _out.WriteLine("[P8-1c] collinear sidewalk continuation (3 cm gap, no WA): 2 islands -> 1 component");
    }

    [Fact]
    public void SidewalkCorner_StaysUnbridged_NoWalkingArea()
    {
        // THE NO-SHORTCUT GUARD. A horizontal strip and a vertical strip that abut within 3 cm at a right-angle
        // CORNER (no walkingArea between them). The continuation gate rejects it (outward end-tangents +x and
        // -y -> Dot 0, well above the -cos(135 deg) threshold), so the two strips stay SEPARATE components --
        // a ped must route around through a real walkingArea, never cut the corner across the (non-walkable)
        // junction interior. This is the POC-0 invariant the whole gate exists to preserve; if the continuation
        // pass ever bridged this, it would be exactly the blanket "connect anything close" shortcut the witness
        // README warns against.
        var polys = new List<BakedPolygon>
        {
            HStrip(0, "h", -10.0, 0.0, yc: 2.0, hw: 1.0),  // top edge y = 3, x in [-10,0]
            VStrip(1, "v", 3.03, 13.0, xc: 0.0, hw: 1.0),  // bottom edge y = 3.03, x in [-1,1] -> 3 cm gap, 90 deg
        };
        var nav = new SumoNavMesh(polys);

        Assert.Equal(2, nav.ConnectedComponentCount());
        _out.WriteLine("[P8-1c] perpendicular sidewalk corner (3 cm gap, no WA): stays 2 components (no shortcut)");
    }

    [Fact]
    public void PedFrag2WitnessBox_ConnectsToOneComponent()
    {
        // P8-1c real-net acceptance: the committed pedfrag2 witness (the sub-area session's geometry-free
        // residual repro) bakes to 83 components under the P8-1b (area-anchored) graph -- all sidewalk<->
        // sidewalk <=5 cm collinear seams with no walkingArea. The P8-1c continuation pass must connect it to
        // ONE routable component (the exact residual the fix targets), pinned against genuine netgenerate
        // geometry.
        var dir = RepoRoot();
        var net = Path.Combine(dir, "scenarios", "_ped", "subarea-pedfrag2", "net.xml");
        var polygons = WalkablePolygonBaker.Bake(PedNetworkParser.Load(net));
        var nav = new SumoNavMesh(polygons);

        Assert.Equal(1, nav.ConnectedComponentCount());

        var lo = polygons.OrderBy(p => p.Centroid.X + p.Centroid.Y).First().Centroid;
        var hi = polygons.OrderByDescending(p => p.Centroid.X + p.Centroid.Y).First().Centroid;
        Assert.NotNull(nav.FindPath(lo, hi, out _));

        _out.WriteLine($"[P8-1c] pedfrag2 witness box: {polygons.Count} polygons, {nav.ConnectedComponentCount()} component (was 83)");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Traffic.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }

    [Fact]
    public void SyntheticBox_StaysOneComponent()
    {
        // The committed synthetic box already connected (shared exact edges) -> 1 component; the additive
        // overlap pass must not change that. Anchors the report's "synthetic grid -> 1 component" baseline.
        var boxNet = Path.Combine(RepoRoot(), "scenarios", "_ped", "subarea-box", "net.xml");
        var network = PedNetworkParser.Load(boxNet);
        var polygons = WalkablePolygonBaker.Bake(network);
        var nav = new SumoNavMesh(polygons);

        Assert.Equal(1, nav.ConnectedComponentCount());
        _out.WriteLine($"[P8-1b] synthetic box: {polygons.Count} polygons, {nav.ConnectedComponentCount()} component");
    }
}
