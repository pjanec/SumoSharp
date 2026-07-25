using System;
using System.IO;
using Sim.LiveCity;
using Xunit;

namespace Sim.LiveCity.Tests;

// docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §9, -TASKS.md E4: coordinate robustness through the WHOLE
// road-net-import pipeline (net.xml parse -> PedNetwork -> SumoRouteGraphNav -> whole-net O/D sample
// -> LiveCitySim.Step). The demo net and `roadnet_min` are both small, all-positive, 2-D. This fixture
// instead uses a Geneva/CH1903-style frame -- large-magnitude NEGATIVE x/y (~-108000,-136900) -- and
// every shape point carries a 3rd (z / elevation) component, e.g. "-108030.00,-136900.00,-374.50",
// exercising:
//   - src/Sim.Ingest/NetworkParser.cs's ParseShape/ParseShapeZ (vehicle side, already z-aware)
//   - src/Sim.Pedestrians/PedNetworkParser.cs's ParseShape (ped side; Vec2 is 2-D, so it takes only
//     the first two comma-separated tokens and silently drops z -- that is the CORRECT, intended
//     behaviour; this test's job is to prove the drop happens cleanly, with no exception/NaN, not
//     that z is preserved)
//   - SumoRouteGraphNav construction/FindPath on that large-negative-coordinate PedNetwork
//   - LiveCitySim's whole-net sidewalk-centreline O/D sampling and net-AABB pocket centre
// No scenario.rou.xml is provided, so this also exercises the A2 net.xml-derived drivable-edges
// fallback on the same large-negative-coordinate vehicle edges.
public class ArbitraryNetStageE4Tests
{
    private const double OriginX = -108000.0;
    private const double OriginY = -136900.0;
    private const double ElevationZ = -374.5;

    // Same "sw_a -- wa_0 -- crossing -- wa_1 -- sw_b" topology as SumoRouteGraphNavTests'
    // ConnectedFixture, expressed as a real net.xml: two one-lane vehicle edges (e1/e2, meeting at
    // junction B) plus a parallel sidewalk/walkingarea/crossing chain, ALL translated onto the large
    // negative origin above and carrying a z on every shape point.
    private static string LargeNegative3DNetXml()
    {
        // p(x,y) formats one absolute, z-carrying shape point: (OriginX+x, OriginY+y, ElevationZ).
        string P(double x, double y) =>
            FormattableString.Invariant(
                $"{OriginX + x:F2},{OriginY + y:F2},{ElevationZ:F2}");

        var e1Shape = $"{P(-30, 0)} {P(0, 0)}";
        var e2Shape = $"{P(0, 0)} {P(30, 0)}";
        var swAShape = $"{P(-30, 5)} {P(-5, 5)}";
        var swBShape = $"{P(5, 5)} {P(30, 5)}";
        var wa0Shape = $"{P(-5, 4)} {P(-3, 4)} {P(-3, 6)} {P(-5, 6)}";
        var wa1Shape = $"{P(3, 4)} {P(5, 4)} {P(5, 6)} {P(3, 6)}";
        var crossingShape = $"{P(-3, 5)} {P(0, 5)} {P(3, 5)}";
        var crossingOutline = $"{P(-3, 3.5)} {P(3, 3.5)} {P(3, 6.5)} {P(-3, 6.5)}";

        return "<net>\n"
            + $"  <edge id=\"e1\" from=\"A\" to=\"B\">\n"
            + $"    <lane id=\"e1_0\" index=\"0\" speed=\"13.9\" length=\"30.0\" shape=\"{e1Shape}\"/>\n"
            + "  </edge>\n"
            + $"  <edge id=\"e2\" from=\"B\" to=\"C\">\n"
            + $"    <lane id=\"e2_0\" index=\"0\" speed=\"13.9\" length=\"30.0\" shape=\"{e2Shape}\"/>\n"
            + "  </edge>\n"
            + "  <connection from=\"e1\" to=\"e2\" fromLane=\"0\" toLane=\"0\"/>\n"
            + $"  <edge id=\"sw_a\" from=\"A\" to=\"B\">\n"
            + $"    <lane id=\"sw_a_0\" index=\"0\" allow=\"pedestrian\" width=\"2.0\" speed=\"1.5\" length=\"25.0\" shape=\"{swAShape}\"/>\n"
            + "  </edge>\n"
            + $"  <edge id=\"sw_b\" from=\"B\" to=\"C\">\n"
            + $"    <lane id=\"sw_b_0\" index=\"0\" allow=\"pedestrian\" width=\"2.0\" speed=\"1.5\" length=\"25.0\" shape=\"{swBShape}\"/>\n"
            + "  </edge>\n"
            + "  <edge id=\":B_w0\" function=\"walkingarea\">\n"
            + $"    <lane id=\":B_w0_0\" index=\"0\" allow=\"pedestrian\" width=\"2.4\" speed=\"1.5\" length=\"2.0\" shape=\"{wa0Shape}\"/>\n"
            + "  </edge>\n"
            + "  <edge id=\":B_c0\" function=\"crossing\" crossingEdges=\"e1 e2\">\n"
            + $"    <lane id=\":B_c0_0\" index=\"0\" allow=\"pedestrian\" width=\"3.0\" speed=\"1.5\" length=\"6.0\" shape=\"{crossingShape}\" outlineShape=\"{crossingOutline}\"/>\n"
            + "  </edge>\n"
            + "  <edge id=\":B_w1\" function=\"walkingarea\">\n"
            + $"    <lane id=\":B_w1_0\" index=\"0\" allow=\"pedestrian\" width=\"2.4\" speed=\"1.5\" length=\"2.0\" shape=\"{wa1Shape}\"/>\n"
            + "  </edge>\n"
            + "  <connection from=\"sw_a\" fromLane=\"0\" to=\":B_w0\" toLane=\"0\"/>\n"
            + "  <connection from=\":B_w0\" fromLane=\"0\" to=\":B_c0\" toLane=\"0\"/>\n"
            + "  <connection from=\":B_c0\" fromLane=\"0\" to=\":B_w1\" toLane=\"0\"/>\n"
            + "  <connection from=\":B_w1\" fromLane=\"0\" to=\"sw_b\" toLane=\"0\"/>\n"
            + "</net>\n";
    }

    private static string CreateTempDataset(string netXml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "livecity-stageE4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "net.xml"), netXml);
        return dir;
    }

    private static bool IsFiniteNumber(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

    [Fact]
    public void LargeNegative3DCoordNet_ParsesRoutesAndSteps_WithNoThrowAndNoNaN()
    {
        var dir = CreateTempDataset(LargeNegative3DNetXml());
        try
        {
            // Parse (E4 leg 1): both ingests must tolerate the z-carrying, large-negative shapes.
            var pedNetwork = Sim.Pedestrians.PedNetworkParser.Load(Path.Combine(dir, "net.xml"));
            Assert.Equal(2, pedNetwork.Sidewalks.Count);
            Assert.Single(pedNetwork.Crossings);
            Assert.Equal(2, pedNetwork.WalkingAreas.Count);
            Assert.Equal(4, pedNetwork.PedConnections.Count);

            foreach (var sw in pedNetwork.Sidewalks)
            {
                foreach (var v in sw.Shape)
                {
                    Assert.True(IsFiniteNumber(v.X));
                    Assert.True(IsFiniteNumber(v.Y));
                    // z is NOT carried into Vec2 -- confirms the intended 2-D drop, not an accidental
                    // z leaking into X/Y (both coordinates stay near the -108000/-136900 origin, not
                    // anywhere near ElevationZ's magnitude).
                    Assert.True(Math.Abs(v.X - OriginX) < 100.0);
                    Assert.True(Math.Abs(v.Y - OriginY) < 100.0);
                }
            }

            // FindPath (E4 leg 2): route directly on SumoRouteGraphNav built from this PedNetwork.
            var nav = new Sim.Pedestrians.Navigation.RouteGraph.SumoRouteGraphNav(pedNetwork);
            var start = new Sim.Core.Orca.Vec2(OriginX - 20, OriginY + 5);
            var goal = new Sim.Core.Orca.Vec2(OriginX + 20, OriginY + 5);
            var path = nav.FindPath(start, goal);

            Assert.NotNull(path);
            Assert.True(path!.Count >= 2);
            foreach (var v in path)
            {
                Assert.True(IsFiniteNumber(v.X), $"path vertex X non-finite: {v.X}");
                Assert.True(IsFiniteNumber(v.Y), $"path vertex Y non-finite: {v.Y}");
            }

            // O/D sampling + full pipeline (E4 leg 3): LiveCitySim.ForDataset on this net -- no
            // scenario.rou.xml, so the A2 net.xml-derived drivable-edges fallback also runs on the
            // same large-negative vehicle edges.
            var cfg = LiveCityConfig.ForDataset(dir);
            using var sim = new LiveCitySim(cfg);

            Assert.True(sim.PedestriansEnabled);
            Assert.True(sim.CrossingsEnabled);
            Assert.True(sim.RouteGraphNavigationActive);
            Assert.True(sim.CropEdges.Count > 0, "expected the net.xml drivable-edges fallback to find e1/e2");

            var ex = Record.Exception(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    sim.Step();
                    var snap = sim.Sample();
                    foreach (var ped in snap.Peds)
                    {
                        Assert.True(IsFiniteNumber(ped.X), $"ped {ped.Id} has non-finite X");
                        Assert.True(IsFiniteNumber(ped.Y), $"ped {ped.Id} has non-finite Y");
                    }

                    foreach (var car in snap.Cars)
                    {
                        Assert.True(IsFiniteNumber(car.X), $"car {car.Handle} has non-finite X");
                        Assert.True(IsFiniteNumber(car.Y), $"car {car.Handle} has non-finite Y");
                    }
                }
            });
            Assert.Null(ex);

            Assert.True(sim.PeakPeds > 0, "expected peds to spawn and route on the large-negative-coord net");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LargeNegative3DCoordNet_TwoRuns_AreDeterministic()
    {
        var dir = CreateTempDataset(LargeNegative3DNetXml());
        try
        {
            LiveCityConfig MakeCfg() => LiveCityConfig.ForDataset(dir);

            using var simA = new LiveCitySim(MakeCfg());
            using var simB = new LiveCitySim(MakeCfg());

            for (var i = 0; i < 200; i++)
            {
                simA.Step();
                simB.Step();
            }

            Assert.Equal(simA.PeakPeds, simB.PeakPeds);
            Assert.Equal(simA.PeakCars, simB.PeakCars);
            Assert.Equal(simA.ArrivedTotal, simB.ArrivedTotal);

            var snapA = simA.Sample();
            var snapB = simB.Sample();
            Assert.Equal(snapA.Peds.Count, snapB.Peds.Count);
            for (var i = 0; i < snapA.Peds.Count; i++)
            {
                Assert.Equal(snapA.Peds[i].Id, snapB.Peds[i].Id);
                Assert.Equal(snapA.Peds[i].X, snapB.Peds[i].X);
                Assert.Equal(snapA.Peds[i].Y, snapB.Peds[i].Y);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
