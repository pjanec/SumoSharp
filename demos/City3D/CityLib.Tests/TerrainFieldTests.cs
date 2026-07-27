using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CityLib;
using Sim.Ingest;
using Sim.LiveCity;
using Xunit;
using Xunit.Abstractions;

namespace CityLib.Tests;

// docs/EXTERNAL-NET-VIEWER-DESIGN.md §7.2: the baked ground field that replaced the viewer's single flat
// elevation datum.
//
// The load-bearing tests here are (a) the field passes through the ROAD heights, not near them -- that is
// what makes it a terrain and not a smoothing -- and (b) a 2-D net bakes to Flat, which is what keeps the
// demo's overlays bit-identical.
public class TerrainFieldTests
{
    private readonly ITestOutputHelper _output;

    public TerrainFieldTests(ITestOutputHelper output) => _output = output;

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

    private static string FixtureCfg()
        => Path.Combine(RepoRoot(), "scenarios", "_ped", "georef_min", "scenario.sumocfg");

    // A synthetic east-west ramp plus a north-south one crossing it: enough real 3-D structure to check
    // interpolation without needing an external dataset (and small enough to reason about by hand).
    private static List<(IReadOnlyList<(double X, double Y)> Shape, IReadOnlyList<double>? ShapeZ)> RampLanes()
    {
        var ew = new List<(double, double)>();
        var ewZ = new List<double>();
        for (var i = 0; i <= 10; i++)
        {
            ew.Add((i * 100.0, 0.0));
            ewZ.Add(100.0 + (i * 10.0)); // 100 -> 200 m over 1 km
        }

        var ns = new List<(double, double)>();
        var nsZ = new List<double>();
        for (var i = 0; i <= 10; i++)
        {
            ns.Add((500.0, (i - 5) * 100.0));
            nsZ.Add(150.0); // level, crossing the ramp exactly where the ramp is also 150
        }

        return new List<(IReadOnlyList<(double X, double Y)>, IReadOnlyList<double>?)>
        {
            (ew, ewZ),
            (ns, nsZ),
        };
    }

    // ---- the flat cases: what keeps the 2-D demo bit-identical ---------------------------------------

    [Fact]
    public void ALaneSetWithNoElevation_BakesToFlatZero()
    {
        var lanes = new List<(IReadOnlyList<(double X, double Y)>, IReadOnlyList<double>?)>
        {
            (new[] { (0.0, 0.0), (100.0, 0.0) }, null),
            (new[] { (0.0, 50.0), (100.0, 50.0) }, null),
        };

        var field = TerrainField.FromLaneGeometry(lanes);

        Assert.True(field.IsFlat);
        Assert.Equal(0.0, field.HeightAt(0.0, 0.0));
        Assert.Equal(0.0, field.HeightAt(1e6, -1e6)); // defined everywhere, including far outside
    }

    [Fact]
    public void ALaneSetAtOneConstantHeight_BakesToFlatAtThatHeight()
    {
        // A net that is 3-D in form but level in fact takes the cheap path -- and, importantly, gives the
        // exact constant back rather than an interpolation of it.
        var lanes = new List<(IReadOnlyList<(double X, double Y)>, IReadOnlyList<double>?)>
        {
            (new[] { (0.0, 0.0), (100.0, 0.0) }, new[] { 372.5, 372.5 }),
        };

        var field = TerrainField.FromLaneGeometry(lanes);

        Assert.True(field.IsFlat);
        Assert.Equal(372.5, field.HeightAt(50.0, 25.0));
    }

    [Fact]
    public void AnEmptyLaneSet_BakesToFlatZero()
    {
        var field = TerrainField.FromLaneGeometry(Array.Empty<(IReadOnlyList<(double X, double Y)>, IReadOnlyList<double>?)>());
        Assert.True(field.IsFlat);
        Assert.Equal(0.0, field.HeightAt(123.0, 456.0));
    }

    [Fact]
    public void Flat_IsExactlyConstant()
    {
        var field = TerrainField.Flat(385.25);
        Assert.True(field.IsFlat);
        Assert.Equal(385.25, field.HeightAt(0.0, 0.0));
        Assert.Equal(385.25, field.HeightAt(-1e7, 1e7));
        Assert.Equal(385.25, field.MinHeight);
        Assert.Equal(385.25, field.MaxHeight);
    }

    // ---- THE property: the field passes through the road heights -------------------------------------

    [Fact]
    public void OnTheRampNet_HeightAtALaneVertex_MatchesThatVertexsOwnZ()
    {
        var field = TerrainField.FromLaneGeometry(RampLanes(), cellSize: 25.0);

        Assert.False(field.IsFlat);

        var worst = 0.0;
        for (var i = 0; i <= 10; i++)
        {
            var x = i * 100.0;
            var expected = 100.0 + (i * 10.0);
            var got = field.HeightAt(x, 0.0);
            worst = Math.Max(worst, Math.Abs(got - expected));
        }

        _output.WriteLine($"ramp: worst |HeightAt(laneVertex) - laneZ| = {worst:F3} m over 11 vertices");

        // A metre on a 100 m/10 m ramp: the lattice is a 25 m bilinear grid, so the field is an
        // approximation of the polyline, not a copy of it. What matters is that it TRACKS -- a nearest-
        // sample or flat-datum answer would be out by tens of metres at the ends.
        Assert.True(worst <= 2.0, $"field should track the ramp within a couple of metres; worst {worst:F3} m");
    }

    [Fact]
    public void OnTheRampNet_HeightIsMonotonicAlongTheRamp()
    {
        var field = TerrainField.FromLaneGeometry(RampLanes(), cellSize: 25.0);

        var prev = double.NegativeInfinity;
        for (var x = 0.0; x <= 1000.0; x += 25.0)
        {
            var h = field.HeightAt(x, 0.0);
            Assert.True(h >= prev - 1e-9, $"height dipped at x={x}: {h:F3} after {prev:F3}");
            prev = h;
        }

        // ...and it really does climb, rather than being flat-but-monotonic.
        Assert.True(field.HeightAt(1000.0, 0.0) - field.HeightAt(0.0, 0.0) > 80.0);
    }

    [Fact]
    public void AwayFromAnyRoad_TheHeightIsBetweenTheNearbyRoadHeights_NotZero()
    {
        // The whole reason the fill exists: a point in the middle of a block has no lane vertex, and the
        // honest answer is the surrounding roads' height -- never 0, and never the global datum.
        var field = TerrainField.FromLaneGeometry(RampLanes(), cellSize: 25.0);

        var h = field.HeightAt(250.0, 300.0); // 300 m north of the ramp, 250 m along it
        Assert.InRange(h, 100.0, 200.0);
    }

    [Fact]
    public void OutsideTheBbox_TheHeightIsClampedToTheNearestEdge_NotZero()
    {
        var field = TerrainField.FromLaneGeometry(RampLanes(), cellSize: 25.0);

        var west = field.HeightAt(-5000.0, 0.0);
        var east = field.HeightAt(5000.0, 0.0);

        Assert.True(Math.Abs(west - field.HeightAt(0.0, 0.0)) < 1e-9, $"west clamp {west:F3}");
        Assert.True(Math.Abs(east - field.HeightAt(1000.0, 0.0)) < 1e-9, $"east clamp {east:F3}");
    }

    // ---- determinism (§7.2.4) ------------------------------------------------------------------------

    [Fact]
    public void BakingTheSameGeometryTwice_IsBitwiseIdentical()
    {
        var a = TerrainField.FromLaneGeometry(RampLanes(), cellSize: 25.0);
        var b = TerrainField.FromLaneGeometry(RampLanes(), cellSize: 25.0);

        Assert.Equal(a.CountX, b.CountX);
        Assert.Equal(a.CountY, b.CountY);
        Assert.Equal(a.MeasuredCorners, b.MeasuredCorners);

        var samples = 0;
        for (var x = -100.0; x <= 1100.0; x += 37.0)
        {
            for (var y = -600.0; y <= 600.0; y += 41.0)
            {
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(a.HeightAt(x, y)),
                    BitConverter.DoubleToInt64Bits(b.HeightAt(x, y)));
                samples++;
            }
        }

        Assert.True(samples >= 300, $"expected a dense comparison; got {samples} samples");
        _output.WriteLine($"§7.2.4: {samples} sample points bitwise identical across two bakes");
    }

    // ---- the lattice cap: a country-sized net must not blow up ---------------------------------------

    [Fact]
    public void AHugeExtent_GrowsTheCellRatherThanTheLattice()
    {
        // 300 km of net at a 40 m nominal cell would be 7500 corners per axis. The cap grows the cell.
        var lanes = new List<(IReadOnlyList<(double X, double Y)>, IReadOnlyList<double>?)>
        {
            (new[] { (0.0, 0.0), (300000.0, 300000.0) }, new[] { 200.0, 4000.0 }),
        };

        var field = TerrainField.FromLaneGeometry(lanes);

        Assert.True(field.CountX <= TerrainField.MaxCornersPerAxis, $"countX={field.CountX}");
        Assert.True(field.CountY <= TerrainField.MaxCornersPerAxis, $"countY={field.CountY}");
        Assert.True(field.CellSize > TerrainField.DefaultCellSizeMeters,
            $"cell should have grown past the default; got {field.CellSize:F1} m");
    }

    // ---- against the committed 3-D fixture -----------------------------------------------------------

    [Fact]
    public void OnTheGeorefFixture_TheFieldTracksTheRealLaneSurface()
    {
        var cfg = LiveCityConfig.ForSumocfg(FixtureCfg());
        using var source = new LiveCitySource(cfg);

        var field = TerrainField.FromNetwork(source.Network);
        Assert.False(field.IsFlat);
        Assert.InRange(field.MinHeight, 350.0, 420.0);
        Assert.InRange(field.MaxHeight, 350.0, 420.0);

        var worst = 0.0;
        var checkedVertices = 0;
        foreach (var lane in source.Network.LanesById.Values)
        {
            if (lane.ShapeZ is not { Count: > 0 } zs)
            {
                continue;
            }

            for (var i = 0; i < lane.Shape.Count && i < zs.Count; i++)
            {
                var (x, y) = lane.Shape[i];
                worst = Math.Max(worst, Math.Abs(field.HeightAt(x, y) - zs[i]));
                checkedVertices++;
            }
        }

        Assert.True(checkedVertices > 100, $"expected a real net; checked {checkedVertices} vertices");
        _output.WriteLine(
            $"georef fixture: {field}; worst |HeightAt(laneVertex) - laneZ| = {worst:F3} m over {checkedVertices} vertices");

        // The fixture spans ~30 m of relief. A flat datum would be out by up to half of that at the
        // extremes; the field has to do materially better than that to be worth baking.
        var relief = field.MaxHeight - field.MinHeight;
        Assert.True(worst < relief * 0.5,
            $"field error {worst:F3} m should be well under half the net's own relief {relief:F3} m");
    }

    [Fact]
    public void ForNetwork_OnTheGeorefFixture_PutsTheFieldOnTheFrame()
    {
        var cfg = LiveCityConfig.ForSumocfg(FixtureCfg());
        using var source = new LiveCitySource(cfg);

        var frame = SumoGodotFrame.ForNetwork(source.Network);

        Assert.True(frame.HasTerrain);
        Assert.False(frame.Terrain.IsFlat);
    }

    [Fact]
    public void AFrameWithNoField_IsFlatAtItsOwnDatum_SoGroundToGodotIsUnchanged()
    {
        // The 2-D regression, at the seam every overlay goes through. `default` and `Identity` must both
        // behave as they did before the field existed.
        Assert.False(SumoGodotFrame.Identity.HasTerrain);
        Assert.True(SumoGodotFrame.Identity.Terrain.IsFlat);
        Assert.True(default(SumoGodotFrame).Terrain.IsFlat);

        var hilly = new SumoGodotFrame(91850.0, 73960.0, 385.0);
        Assert.False(hilly.HasTerrain);
        Assert.Equal(385.0, hilly.Terrain.HeightAt(1.0, 2.0));

        var (_, y, _) = hilly.GroundToGodot(91900.0, 74000.0, -0.05);
        Assert.Equal(-0.05, (double)y, 4);
    }

    [Fact]
    public void AFrameWITHAField_MovesGroundOverlaysOntoTheSurface()
    {
        var field = TerrainField.FromLaneGeometry(RampLanes(), cellSize: 25.0);
        var frame = new SumoGodotFrame(500.0, 0.0, 150.0, field);

        Assert.True(frame.HasTerrain);

        // At the ramp's low end the ground is ~100 m, i.e. ~50 m BELOW the 150 m datum -- so a ground
        // overlay there must render ~50 m below Y=0, not at it.
        var (_, lowY, _) = frame.GroundToGodot(0.0, 0.0, 0.0);
        var (_, highY, _) = frame.GroundToGodot(1000.0, 0.0, 0.0);

        Assert.True(lowY < -40f, $"low end should sit well below the datum; got {lowY}");
        Assert.True(highY > 40f, $"high end should sit well above the datum; got {highY}");

        // ...and the offset argument still means "this far above the ground", wherever the ground is.
        var (_, lowY2, _) = frame.GroundToGodot(0.0, 0.0, 2.0);
        Assert.Equal(2.0, (double)(lowY2 - lowY), 3);
    }
}
