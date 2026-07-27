using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sim.Core.Orca;
using Sim.Pedestrians;
using Sim.Pedestrians.Navigation.RouteGraph;
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
        + $" shape=\"0.00,0.00,{GroundZ:F2} 50.00,0.00,{GroundZ:F2} 100.00,0.00,{GroundZ:F2}\"/>\n"
        + "  </edge>\n"
        + "  <edge id=\"bridge\" from=\"S\" to=\"N\">\n"
        + $"    <lane id=\"bridge_0\" index=\"0\" allow=\"pedestrian\" width=\"3.0\" speed=\"1.5\" length=\"100.0\""
        + $" shape=\"50.00,-50.00,{BridgeZ:F2} 50.00,0.00,{BridgeZ:F2} 50.00,50.00,{BridgeZ:F2}\"/>\n"
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

    // KNOWN LIMITATION -- see the class remarks and the report to the design owner. `ElevationsAlong`
    // receives only POINTS, so it must locate each one by plan-view proximity; `FindPath` knows which
    // node produced each vertex but discards that before returning. Directly under the bridge the two
    // surfaces are equidistant and the tie-break decides, so today BOTH queries return the bridge.
    //
    // This test pins the CURRENT behaviour so the limitation is visible rather than folklore. When the
    // provider learns to carry path provenance, it fails and should be replaced by the two assertions
    // below it (a ped on the ground gets 400, a ped on the bridge gets 412.5).
    [Fact]
    public void AtTheCrossingPoint_BothSurfacesResolveToOne_TheProvenanceLimitation()
    {
        var net = LoadStacked();
        var nav = new SumoRouteGraphNav(net);
        var crossing = new Vec2(50.0, 0.0);

        var viaBridge = nav.ElevationsAlong(new[] { crossing, new Vec2(50.0, 25.0) })[0];
        var viaGround = nav.ElevationsAlong(new[] { crossing, new Vec2(75.0, 0.0) })[0];

        Assert.Equal(viaBridge, viaGround, 6);
        Assert.True(Math.Abs(viaBridge - BridgeZ) <= 0.05 || Math.Abs(viaBridge - GroundZ) <= 0.05,
            "the shared answer should at least be one of the two real surfaces");
    }

    [Fact]
    public void APedOnTheBridge_GetsTheBridgeHeight_NotTheGroundBelowIt()
    {
        // Walking the BRIDGE's own polyline must yield the bridge's height at every vertex -- including
        // the crossing point, where the ground lane is exactly as near in plan view.
        var net = LoadStacked();
        var nav = new SumoRouteGraphNav(net);
        var bridge = net.Sidewalks.Single(s => s.Id == "bridge_0");

        var elevations = nav.ElevationsAlong(bridge.Shape);

        Assert.Equal(bridge.Shape.Count, elevations.Count);
        for (var i = 0; i < elevations.Count; i++)
        {
            Assert.True(Math.Abs(elevations[i] - BridgeZ) <= 0.05,
                $"bridge vertex {i} resolved to {elevations[i]:F2}, expected {BridgeZ:F2} "
                + "-- it picked up the surface underneath instead of the one it is on");
        }
    }

    [Fact]
    public void APedOnTheGround_GetsTheGroundHeight_NotTheBridgeAboveIt()
    {
        var net = LoadStacked();
        var nav = new SumoRouteGraphNav(net);
        var ground = net.Sidewalks.Single(s => s.Id == "ground_0");

        // Sampled AWAY from the overlap, where the ground lane is unambiguously nearest. At the crossing
        // point itself the answer is currently the bridge's -- see the provenance-limitation test above.
        var away = new[] { new Vec2(10.0, 0.0), new Vec2(25.0, 0.0), new Vec2(80.0, 0.0), new Vec2(95.0, 0.0) };
        var elevations = nav.ElevationsAlong(away);

        for (var i = 0; i < elevations.Count; i++)
        {
            Assert.True(Math.Abs(elevations[i] - GroundZ) <= 0.05,
                $"ground sample {i} resolved to {elevations[i]:F2}, expected {GroundZ:F2}");
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
            Assert.All(nav.ElevationsAlong(net.Sidewalks.First().Shape), z => Assert.Equal(0.0, z));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
