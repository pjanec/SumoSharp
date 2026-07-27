using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Sim.Core.Orca;
using Sim.Pedestrians;
using Sim.Pedestrians.Navigation;
using Sim.Pedestrians.Navigation.Bake;
using Sim.Pedestrians.Navigation.RouteGraph;
using Xunit;

namespace Sim.Pedestrians.Tests;

// docs/EXTERNAL-NET-LOADING-DESIGN.md §3.4, -TASKS.md C2: `IPedNavigation.ElevationsAlong` -- a DEFAULT
// interface method returning all zeros, overridden by the two SUMO-geometry providers from the channels
// C1 retains. Follows the `HalfWidthsAlong` precedent exactly, which is what lets DotRecast and every
// test double stay untouched.
public class PedElevationsAlongTests
{
    private static string RepoRoot()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --show-toplevel")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode == 0 && Directory.Exists(Path.Combine(output, "scenarios")))
            {
                return output;
            }
        }
        catch
        {
            // fall through
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "scenarios")) && File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }

    private static string Net3D() => Path.Combine(RepoRoot(), "scenarios", "_ped", "georef_min", "scenario.net.xml");
    private static string Net2D() => Path.Combine(RepoRoot(), "scenarios", "_ped", "demo_city", "box", "net.xml");

    // A provider that overrides NOTHING -- stands in for DotRecast and the test doubles, proving the
    // default is what they inherit.
    private sealed class BareNav : IPedNavigation
    {
        public IReadOnlyList<Vec2>? FindPath(Vec2 start, Vec2 goal) => new[] { start, goal };
    }

    // ---- C2·SC2: the default is flat, the right length, and does not throw -------------------------

    [Fact]
    public void Default_ReturnsExactlyPathCountZeros()
    {
        IPedNavigation nav = new BareNav();
        var path = new[] { new Vec2(0, 0), new Vec2(5, 5), new Vec2(9, 1), new Vec2(12, 4) };

        var elevations = nav.ElevationsAlong(path);

        Assert.Equal(path.Length, elevations.Count);
        Assert.All(elevations, z => Assert.Equal(0.0, z));
    }

    [Fact]
    public void Default_OnAnEmptyPath_IsEmptyNotNull()
    {
        IPedNavigation nav = new BareNav();
        Assert.Empty(nav.ElevationsAlong(Array.Empty<Vec2>()));
    }

    // ---- C2·SC3: every override returns exactly path.Count, over many real paths -------------------

    [Fact]
    public void RouteGraphNav_ElevationsAlong_IsIndexAligned_OverManyGeneratedPaths()
    {
        var net = PedNetworkParser.Load(Net3D());
        var nav = new SumoRouteGraphNav(net);

        // Deterministic O/D sweep over sidewalk endpoints -- no RNG, so this is reproducible.
        var anchors = net.Sidewalks
            .Where(s => s.Shape.Count > 0)
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => s.Shape[s.Shape.Count / 2])
            .ToList();

        Assert.True(anchors.Count >= 10, $"fixture should offer plenty of anchors; got {anchors.Count}");

        var checkedPaths = 0;
        for (var i = 0; i < anchors.Count && checkedPaths < 80; i++)
        {
            for (var j = i + 1; j < anchors.Count && checkedPaths < 80; j += 3)
            {
                var path = nav.FindPath(anchors[i], anchors[j]);
                if (path is null || path.Count == 0)
                {
                    continue;
                }

                var elevations = nav.ElevationsAlong(path);
                Assert.Equal(path.Count, elevations.Count);
                Assert.All(elevations, z => Assert.False(double.IsNaN(z) || double.IsInfinity(z)));
                Assert.All(elevations, z => Assert.InRange(z, 360.0, 410.0));
                checkedPaths++;
            }
        }

        Assert.True(checkedPaths >= 50, $"expected >=50 routable paths to check; got {checkedPaths}");
    }

    // ---- C2·SC4: correctness against the fixture's known elevation field ---------------------------

    [Fact]
    public void RouteGraphNav_ElevationsMatchTheSourceGeometry_WithinFiveCentimetres()
    {
        // The fixture's elevations are engineered per node (370 + 4i + 7j + 1.5ij metres) and the
        // netconvert output interpolates between them along each lane. Rather than re-deriving that,
        // assert against the SOURCE geometry the parser retained: at a vertex that IS a shape point of
        // its own lane, the returned elevation must be that point's own z.
        var net = PedNetworkParser.Load(Net3D());
        var nav = new SumoRouteGraphNav(net);

        // Sampled at points strictly INSIDE the lane, not at its endpoints: an endpoint sits exactly on
        // the boundary it shares with the adjoining walkingarea, so attributing it to either element is
        // correct and their heights can differ by a few centimetres. Interior points belong to exactly
        // one element, which is what makes this an assertion about accuracy rather than about tie-breaks.
        var checkedSamples = 0;
        foreach (var sw in net.Sidewalks.Where(s => s.ShapeZ is { Count: > 1 }).Take(30))
        {
            var a = sw.Shape[0];
            var b = sw.Shape[^1];
            var za = sw.ShapeZ![0];
            var zb = sw.ShapeZ![^1];

            foreach (var f in new[] { 0.25, 0.5, 0.75 })
            {
                var probe = new Vec2(a.X + ((b.X - a.X) * f), a.Y + ((b.Y - a.Y) * f));
                var expected = za + ((zb - za) * f);

                var elevations = nav.ElevationsAlong(new[] { probe });
                Assert.Single(elevations);
                Assert.True(Math.Abs(elevations[0] - expected) <= 0.05,
                    $"{sw.Id} at f={f}: expected {expected:F3}, got {elevations[0]:F3}");
                checkedSamples++;
            }
        }

        Assert.True(checkedSamples >= 50, $"expected to check >=50 samples; got {checkedSamples}");
    }

    [Fact]
    public void RouteGraphNav_AlongARamp_ElevationsRiseMonotonicallyAndMatchTheGrade()
    {
        // C2·SC4. Every sidewalk in this fixture is a straight two-point grid edge, so walking its two
        // vertices would assert almost nothing -- the interesting behaviour is the INTERPOLATION between
        // them. So this walks the steepest lane in 20 steps and checks the height climbs smoothly and
        // linearly, which is what a ped actually experiences as it crosses it.
        var net = PedNetworkParser.Load(Net3D());
        var nav = new SumoRouteGraphNav(net);

        var ramp = net.Sidewalks
            .Where(s => s.ShapeZ is { Count: >= 2 })
            .OrderByDescending(s => s.ShapeZ![^1] - s.ShapeZ![0])
            .First();

        var za = ramp.ShapeZ![0];
        var zb = ramp.ShapeZ![^1];
        var span = zb - za;
        Assert.True(span > 1.0, $"expected a real grade to test; steepest rise was {span:F3} m");

        var a = ramp.Shape[0];
        var b = ramp.Shape[^1];

        const int Steps = 20;
        var walk = new List<Vec2>(Steps + 1);
        for (var i = 0; i <= Steps; i++)
        {
            var f = (double)i / Steps;
            walk.Add(new Vec2(a.X + ((b.X - a.X) * f), a.Y + ((b.Y - a.Y) * f)));
        }

        var elevations = nav.ElevationsAlong(walk);
        Assert.Equal(walk.Count, elevations.Count);

        for (var i = 1; i < elevations.Count; i++)
        {
            Assert.True(elevations[i] >= elevations[i - 1] - 1e-9,
                $"{ramp.Id}: elevation fell at step {i} ({elevations[i - 1]:F4} -> {elevations[i]:F4})");
        }

        // ...and matches the analytic linear grade at every interior sample, within the stated 0.05 m.
        for (var i = 1; i < Steps; i++)
        {
            var expected = za + (span * i / Steps);
            Assert.True(Math.Abs(elevations[i] - expected) <= 0.05,
                $"{ramp.Id} step {i}: expected {expected:F3}, got {elevations[i]:F3}");
        }
    }

    // ---- C2·SC5: determinism ------------------------------------------------------------------------

    [Fact]
    public void RouteGraphNav_TwoIndependentProviders_ProduceBitwiseIdenticalElevations()
    {
        var netA = PedNetworkParser.Load(Net3D());
        var netB = PedNetworkParser.Load(Net3D());
        var navA = new SumoRouteGraphNav(netA);
        var navB = new SumoRouteGraphNav(netB);

        var path = netA.Sidewalks.OrderBy(s => s.Id, StringComparer.Ordinal).First().Shape;

        var a = navA.ElevationsAlong(path);
        var b = navB.ElevationsAlong(path);

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i]), BitConverter.DoubleToInt64Bits(b[i]));
        }
    }

    // ---- the 2-D regression: both overrides stay flat ----------------------------------------------

    [Fact]
    public void On2DNet_BothOverridesReturnExactlyZero()
    {
        var net = PedNetworkParser.Load(Net2D());

        var routeGraph = new SumoRouteGraphNav(net);
        var polygons = WalkablePolygonBaker.Bake(net);
        var navmesh = new SumoNavMesh(polygons, new SumoWalkableSpace(polygons), net.PedConnections);

        var path = net.Sidewalks.OrderBy(s => s.Id, StringComparer.Ordinal).First().Shape;
        Assert.NotEmpty(path);

        Assert.All(routeGraph.ElevationsAlong(path), z => Assert.Equal(0.0, z));
        Assert.All(navmesh.ElevationsAlong(path), z => Assert.Equal(0.0, z));
    }

    // ---- the navmesh override on 3-D geometry -------------------------------------------------------

    [Fact]
    public void NavMesh_On3DGeometry_ReturnsTheSourceElevation()
    {
        // SumoNavMesh is the demo's (2-D) provider, so exercise its override on a 3-D bake directly
        // rather than only through the flat demo net -- otherwise the override is never actually run.
        var net = PedNetworkParser.Load(Net3D());
        var polygons = WalkablePolygonBaker.Bake(net);
        var navmesh = new SumoNavMesh(polygons, new SumoWalkableSpace(polygons), net.PedConnections);

        var sidewalk = net.Sidewalks.First(s => s.ShapeZ is { Count: > 1 });
        var elevations = navmesh.ElevationsAlong(sidewalk.Shape);

        Assert.Equal(sidewalk.Shape.Count, elevations.Count);
        Assert.Contains(elevations, z => z != 0.0);
        Assert.All(elevations, z => Assert.InRange(z, 360.0, 410.0));
    }
}
