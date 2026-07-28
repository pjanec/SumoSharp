using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sim.Core.Orca;
using Sim.Pedestrians;
using Sim.Pedestrians.Navigation.RouteGraph;
using Sim.Pedestrians.Navigation.Bake;
using Xunit;

namespace Sim.Pedestrians.Tests;

// docs/EXTERNAL-NET-LOADING-DESIGN.md §3.1: THE case that separates retaining elevation from
// reconstructing it -- two pedestrian surfaces stacked vertically, a footbridge over the path it
// crosses. In plan view they overlap; only their z differs.
//
// A nearest-surface search (the superseded design) cannot get this right in principle: from directly
// underneath, the bridge and the path are the same 2-D point, so the answer is a coin toss. Retaining z
// from ingest gets it right by construction, because a ped's height comes from the lane it is walking on,
// not from whatever lane happens to be nearest in plan.
//
// Deliberately SYNTHETIC and hand-written here rather than cut from real data: it is a handful of lines,
// it is always present, it needs no external dataset, and it isolates exactly one property.
public class PedElevationMultiLevelTests
{
    private const double GroundZ = 400.0;
    private const double BridgeZ = 412.5; // 12.5 m of clearance -- a real overpass, not a kerb

    // A ground sidewalk running west-east, and a footbridge running south-north directly over it. They
    // cross at (50, 0) in plan view and are 12.5 m apart vertically. Both are pedestrian lanes on
    // ordinary (non-internal) edges, so both parse as sidewalks.
    private static string StackedNetXml() =>
        "<net>\n"
        + "  <edge id=\"ground\" from=\"W\" to=\"E\">\n"
        + $"    <lane id=\"ground_0\" index=\"0\" allow=\"pedestrian\" width=\"3.0\" speed=\"1.5\" length=\"100.0\""
        + $" shape=\"0.00,0.00,{GroundZ.ToString("F2", CultureInfo.InvariantCulture)} 50.00,0.00,{GroundZ.ToString("F2", CultureInfo.InvariantCulture)} 100.00,0.00,{GroundZ.ToString("F2", CultureInfo.InvariantCulture)}\"/>\n"
        + "  </edge>\n"
        + "  <edge id=\"bridge\" from=\"S\" to=\"N\">\n"
        + $"    <lane id=\"bridge_0\" index=\"0\" allow=\"pedestrian\" width=\"3.0\" speed=\"1.5\" length=\"100.0\""
        + $" shape=\"50.00,-50.00,{BridgeZ.ToString("F2", CultureInfo.InvariantCulture)} 50.00,0.00,{BridgeZ.ToString("F2", CultureInfo.InvariantCulture)} 50.00,50.00,{BridgeZ.ToString("F2", CultureInfo.InvariantCulture)}\"/>\n"
        + "  </edge>\n"
        + "</net>\n";

    private static PedNetwork LoadStacked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ped-stacked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var netPath = Path.Combine(dir, "net.xml");
            File.WriteAllText(netPath, StackedNetXml());
            return PedNetworkParser.Load(netPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BothStackedSurfaces_RetainTheirOwnElevation()
    {
        var net = LoadStacked();

        var ground = net.Sidewalks.Single(s => s.Id == "ground_0");
        var bridge = net.Sidewalks.Single(s => s.Id == "bridge_0");

        Assert.All(ground.ShapeZ!, z => Assert.Equal(GroundZ, z, 6));
        Assert.All(bridge.ShapeZ!, z => Assert.Equal(BridgeZ, z, 6));

        // They really do overlap in plan view -- otherwise this fixture would not be testing anything.
        Assert.Contains(ground.Shape, p => Math.Abs(p.X - 50.0) < 1e-9 && Math.Abs(p.Y) < 1e-9);
        Assert.Contains(bridge.Shape, p => Math.Abs(p.X - 50.0) < 1e-9 && Math.Abs(p.Y) < 1e-9);
    }

    // THE discriminating test. At the SAME plan-view point the two stacked surfaces must give two
    // different heights -- which is only possible if the query knows which surface the ped is ON.
    //
    // Without provenance this collapsed: both queries returned the bridge, because from directly
    // beneath it the two candidates are equidistant and the tie-break decided. Any position-only
    // mechanism -- nearest-lane, ground-clamp, heightmap probe -- fails here no matter which it picks.
    [Fact]
    public void AtTheCrossingPoint_ProvenanceKeepsTheTwoSurfacesApart()
    {
        var net = LoadStacked();
        var nav = new SumoRouteGraphNav(net);

        // Route ALONG each surface and read the height at the shared crossing point from that route.
        var bridgeZ = ElevationAtCrossingWalking(nav, new Vec2(50.0, -40.0), new Vec2(50.0, 40.0));
        var groundZ = ElevationAtCrossingWalking(nav, new Vec2(10.0, 0.0), new Vec2(90.0, 0.0));

        Assert.True(Math.Abs(bridgeZ - BridgeZ) <= 0.05,
            $"a ped routed over the bridge read {bridgeZ:F2}, expected {BridgeZ.ToString("F2", CultureInfo.InvariantCulture)}");
        Assert.True(Math.Abs(groundZ - GroundZ) <= 0.05,
            $"a ped routed under the bridge read {groundZ:F2}, expected {GroundZ.ToString("F2", CultureInfo.InvariantCulture)} "
            + "-- it was lifted onto the bridge");
        Assert.True(Math.Abs(bridgeZ - groundZ) > 1.0);
    }

    // Route start->goal, then report the elevation the router itself attributes to the vertex nearest
    // the crossing point -- i.e. exactly what the runtime stores as that ped's elevation channel.
    private static double ElevationAtCrossingWalking(SumoRouteGraphNav nav, Vec2 start, Vec2 goal)
    {
        var path = nav.FindPath(start, goal, out var surfaces);
        Assert.NotNull(path);
        Assert.NotNull(surfaces);
        Assert.Equal(path!.Count, surfaces!.Count);

        var elevations = nav.ElevationsAlong(path, surfaces);

        var crossing = new Vec2(50.0, 0.0);
        var best = 0;
        var bestD2 = double.PositiveInfinity;
        for (var i = 0; i < path.Count; i++)
        {
            var d = path[i] - crossing;
            var d2 = (d.X * d.X) + (d.Y * d.Y);
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = i;
            }
        }

        return elevations[best];
    }

    [Fact]
    public void ProvenanceIsIndexAlignedWithThePath_AndIdsAreRealNodes()
    {
        var net = LoadStacked();
        var nav = new SumoRouteGraphNav(net);

        var path = nav.FindPath(new Vec2(10.0, 0.0), new Vec2(90.0, 0.0), out var surfaces);

        Assert.NotNull(path);
        Assert.NotNull(surfaces);
        Assert.Equal(path!.Count, surfaces!.Count);
        Assert.All(surfaces, s => Assert.InRange(s, 0, nav.Nodes.Count - 1));
    }

    [Fact]
    public void WithoutProvenance_TheQueryStillAnswers_ButCannotSeparateStackedSurfaces()
    {
        // The documented fallback: correct away from overlaps, ambiguous under them. Kept as a test so
        // the difference the provenance channel makes is visible rather than asserted in prose.
        var net = LoadStacked();
        var nav = new SumoRouteGraphNav(net);
        var crossing = new Vec2(50.0, 0.0);

        var a = nav.ElevationsAlong(new[] { crossing }, vertexSurfaces: null)[0];
        var b = nav.ElevationsAlong(new[] { crossing }, vertexSurfaces: null)[0];

        Assert.Equal(a, b, 9); // deterministic, just not disambiguating
        Assert.True(Math.Abs(a - BridgeZ) <= 0.05 || Math.Abs(a - GroundZ) <= 0.05);
    }

    [Fact]
    public void APedOnTheBridge_GetsTheBridgeHeight_NotTheGroundBelowIt()
    {
        // Walking the BRIDGE's own polyline must yield the bridge's height at every vertex -- including
        // the crossing point, where the ground lane is exactly as near in plan view.
        var net = LoadStacked();
        var nav = new SumoRouteGraphNav(net);
        var bridge = net.Sidewalks.Single(s => s.Id == "bridge_0");

        var elevations = nav.ElevationsAlong(bridge.Shape, vertexSurfaces: null);

        Assert.Equal(bridge.Shape.Count, elevations.Count);
        for (var i = 0; i < elevations.Count; i++)
        {
            Assert.True(Math.Abs(elevations[i] - BridgeZ) <= 0.05,
                $"bridge vertex {i} resolved to {elevations[i]:F2}, expected {BridgeZ.ToString("F2", CultureInfo.InvariantCulture)} "
                + "-- it picked up the surface underneath instead of the one it is on");
        }
    }

    [Fact]
    public void APedOnTheGround_GetsTheGroundHeight_NotTheBridgeAboveIt()
    {
        var net = LoadStacked();
        var nav = new SumoRouteGraphNav(net);
        var ground = net.Sidewalks.Single(s => s.Id == "ground_0");

        // Sampled away from the overlap, where even the proximity fallback is unambiguous.
        var away = new[] { new Vec2(10.0, 0.0), new Vec2(25.0, 0.0), new Vec2(80.0, 0.0), new Vec2(95.0, 0.0) };
        var elevations = nav.ElevationsAlong(away, vertexSurfaces: null);

        for (var i = 0; i < elevations.Count; i++)
        {
            Assert.True(Math.Abs(elevations[i] - GroundZ) <= 0.05,
                $"ground sample {i} resolved to {elevations[i]:F2}, expected {GroundZ.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        Assert.NotEmpty(ground.ShapeZ!);
    }

    [Fact]
    public void TheRuntimeSurfaceDoesDistinguishThem_BecauseItProjectsOntoThePedsOwnPath()
    {
        // The saving grace, and the reason this limitation is narrower than it looks: the RUNTIME path
        // (PedLodManager.ElevationOf / HeadlessIg) does not ask "what is under this point?" -- it
        // interpolates along the channel attached to the ped's OWN path. So a ped whose route is the
        // bridge carries the bridge's elevations with it and reads 412.5 under the overlap, while one
        // routed along the ground carries 400. Demonstrated here directly on the shared evaluator both
        // surfaces use.
        var net = LoadStacked();
        var bridge = net.Sidewalks.Single(s => s.Id == "bridge_0");
        var ground = net.Sidewalks.Single(s => s.Id == "ground_0");
        var crossing = new Vec2(50.0, 0.0);

        var onBridge = Sim.Pedestrians.Navigation.PolylineElevation.AtNearestPoint(
            bridge.Shape, bridge.ShapeZ, crossing);
        var onGround = Sim.Pedestrians.Navigation.PolylineElevation.AtNearestPoint(
            ground.Shape, ground.ShapeZ, crossing);

        Assert.Equal(BridgeZ, onBridge, 3);
        Assert.Equal(GroundZ, onGround, 3);
        Assert.True(Math.Abs(onBridge - onGround) > 1.0);
    }

    // ---- the SAME discrimination, on the OTHER provider ----------------------------------------------
    //
    // SumoNavMesh used to answer `ElevationsAlong` from a plan-view polygon lookup with no notion of
    // which deck the ped was on, so it failed this fixture outright: both routes read the same height.
    // It now records the BakedPolygon index behind each waypoint (the funnel's own corridor) and reads
    // the height off that polygon's retained channel. These tests exist so the two providers are held to
    // one standard rather than the mesh one being "the 2-D one".

    private static SumoNavMesh StackedNavMesh(PedNetwork net)
    {
        var polygons = WalkablePolygonBaker.Bake(net);
        return new SumoNavMesh(polygons, new SumoWalkableSpace(polygons));
    }

    [Fact]
    public void OnTheNavMesh_ProvenanceAlsoKeepsTheTwoStackedSurfacesApart()
    {
        var net = LoadStacked();
        var nav = StackedNavMesh(net);

        var bridgeZ = NavMeshElevationAtCrossing(nav, new Vec2(50.0, -40.0), new Vec2(50.0, 40.0));
        var groundZ = NavMeshElevationAtCrossing(nav, new Vec2(10.0, 0.0), new Vec2(90.0, 0.0));

        Assert.True(Math.Abs(bridgeZ - BridgeZ) <= 0.05,
            $"navmesh: a ped routed over the bridge read {bridgeZ:F2}, expected {BridgeZ.ToString("F2", CultureInfo.InvariantCulture)}");
        Assert.True(Math.Abs(groundZ - GroundZ) <= 0.05,
            $"navmesh: a ped routed under the bridge read {groundZ:F2}, expected {GroundZ.ToString("F2", CultureInfo.InvariantCulture)} "
            + "-- it was lifted onto the bridge");
        Assert.True(Math.Abs(bridgeZ - groundZ) > 1.0);
    }

    private static double NavMeshElevationAtCrossing(SumoNavMesh nav, Vec2 start, Vec2 goal)
    {
        var path = nav.FindPath(start, goal, out var surfaces);
        Assert.NotNull(path);
        Assert.NotNull(surfaces);
        Assert.Equal(path!.Count, surfaces!.Count);

        var elevations = nav.ElevationsAlong(path, surfaces);
        Assert.Equal(path.Count, elevations.Count);

        var crossing = new Vec2(50.0, 0.0);
        var best = 0;
        var bestD2 = double.PositiveInfinity;
        for (var i = 0; i < path.Count; i++)
        {
            var d = path[i] - crossing;
            var d2 = (d.X * d.X) + (d.Y * d.Y);
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = i;
            }
        }

        return elevations[best];
    }

    [Fact]
    public void OnTheNavMesh_ProvenanceIsIndexAlignedAndNamesRealPolygons()
    {
        var net = LoadStacked();
        var polygons = WalkablePolygonBaker.Bake(net);
        var nav = new SumoNavMesh(polygons, new SumoWalkableSpace(polygons));

        var path = nav.FindPath(new Vec2(10.0, 0.0), new Vec2(90.0, 0.0), out var surfaces);

        Assert.NotNull(path);
        Assert.NotNull(surfaces);
        Assert.Equal(path!.Count, surfaces!.Count);
        Assert.All(surfaces, s => Assert.InRange(s, 0, polygons.Count - 1));

        // ...and every named polygon really is the ground deck, not the bridge overhead.
        Assert.All(surfaces, s => Assert.Equal("ground_0", polygons[s].Id));
    }

    [Fact]
    public void OnTheNavMesh_A2DNetStillReadsFlat()
    {
        // The mesh provider's own 2-D regression: no channel on the baked polygons => 0.0 everywhere,
        // provenance or not.
        var dir = Path.Combine(Path.GetTempPath(), "ped-navmesh2d-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var netPath = Path.Combine(dir, "net.xml");
            File.WriteAllText(netPath,
                "<net>\n"
                + "  <edge id=\"ground\" from=\"W\" to=\"E\">\n"
                + "    <lane id=\"ground_0\" index=\"0\" allow=\"pedestrian\" width=\"3.0\" speed=\"1.5\" length=\"100.0\""
                + " shape=\"0,0 50,0 100,0\"/>\n"
                + "  </edge>\n"
                + "</net>\n");

            var net = PedNetworkParser.Load(netPath);
            var nav = StackedNavMesh(net);

            var path = nav.FindPath(new Vec2(10.0, 0.0), new Vec2(90.0, 0.0), out var surfaces);
            Assert.NotNull(path);
            Assert.All(nav.ElevationsAlong(path!, surfaces), z => Assert.Equal(0.0, z));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A2DStackedNet_StillYieldsNullChannels()
    {
        // The same topology with no third coordinate: null everywhere, so the 2-D contract holds even
        // for geometry that overlaps in plan view.
        var dir = Path.Combine(Path.GetTempPath(), "ped-stacked2d-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var netPath = Path.Combine(dir, "net.xml");
            File.WriteAllText(netPath,
                "<net>\n"
                + "  <edge id=\"ground\" from=\"W\" to=\"E\">\n"
                + "    <lane id=\"ground_0\" index=\"0\" allow=\"pedestrian\" width=\"3.0\" speed=\"1.5\" length=\"100.0\""
                + " shape=\"0,0 50,0 100,0\"/>\n"
                + "  </edge>\n"
                + "  <edge id=\"bridge\" from=\"S\" to=\"N\">\n"
                + "    <lane id=\"bridge_0\" index=\"0\" allow=\"pedestrian\" width=\"3.0\" speed=\"1.5\" length=\"100.0\""
                + " shape=\"50,-50 50,0 50,50\"/>\n"
                + "  </edge>\n"
                + "</net>\n");

            var net = PedNetworkParser.Load(netPath);
            Assert.All(net.Sidewalks, s => Assert.Null(s.ShapeZ));

            var nav = new SumoRouteGraphNav(net);
            Assert.All(nav.ElevationsAlong(net.Sidewalks.First().Shape, vertexSurfaces: null), z => Assert.Equal(0.0, z));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
