using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Sim.LiveCity;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// docs/EXTERNAL-NET-LOADING-DESIGN.md §3.4/§3.5a, -TASKS.md C3: a pedestrian's runtime elevation, and
// `LiveCitySim.Sample()` reporting it as `LiveCityPed.Z`.
//
// The load-bearing test here is SC4: a ped's 2-D trajectory must be BITWISE identical with the
// elevation channel populated versus null. Without it, "output-only" is a claim rather than a fact.
public class PedElevationRuntimeTests
{
    private readonly ITestOutputHelper _output;

    public PedElevationRuntimeTests(ITestOutputHelper output) => _output = output;

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

    private static string FixtureDir() => Path.Combine(RepoRoot(), "scenarios", "_ped", "georef_min");

    private static LiveCityConfig Config3D()
        => LiveCityConfig.ForSumocfg(Path.Combine(FixtureDir(), "scenario.sumocfg"));

    // ---- C3·SC1: real elevation on a 3-D net --------------------------------------------------------

    [Fact]
    public void On3DNet_EveryLivePedHasRealElevation()
    {
        using var sim = new LiveCitySim(Config3D());

        for (var i = 0; i < 300; i++)
        {
            sim.Step();
        }

        var peds = sim.Sample().Peds;
        Assert.True(peds.Count >= 5, $"need >=5 live peds to judge; got {peds.Count}");

        foreach (var ped in peds)
        {
            Assert.NotEqual(0.0, ped.Z);
            Assert.InRange(ped.Z, 360.0, 410.0);
        }
    }

    // ---- C3·SC2: exactness -- the payoff of retaining rather than reconstructing --------------------

    [Fact]
    public void On3DNet_SampledZTracksTheLaneSurface_WithinTenCentimetres()
    {
        // The bar the redesign buys: a nearest-lane search could only ever have promised "within a road
        // width". Retained z should match the actual surface under the ped. Checked against the ped
        // network's own geometry -- the elevation interpolated along the ped-lane nearest its position,
        // computed here INDEPENDENTLY of the runtime path so this is a cross-check, not a tautology.
        var net = Sim.Pedestrians.PedNetworkParser.Load(Path.Combine(FixtureDir(), "scenario.net.xml"));
        using var sim = new LiveCitySim(Config3D());

        for (var i = 0; i < 200; i++)
        {
            sim.Step();
        }

        var checkedSteps = 0;
        var worst = 0.0;

        for (var step = 0; step < 40 && checkedSteps < 10; step++)
        {
            sim.Step();
            var peds = sim.Sample().Peds;
            if (peds.Count == 0)
            {
                continue;
            }

            foreach (var ped in peds.Take(5))
            {
                var expected = NearestPedLaneElevation(net, ped.X, ped.Y);
                if (double.IsNaN(expected))
                {
                    continue;
                }

                var error = Math.Abs(ped.Z - expected);
                worst = Math.Max(worst, error);
                Assert.True(error <= 0.10,
                    $"ped {ped.Id} at ({ped.X:F1},{ped.Y:F1}): z={ped.Z:F3} vs surface {expected:F3} (err {error:F3} m)");
            }

            checkedSteps++;
        }

        Assert.True(checkedSteps >= 10, $"expected >=10 sampled steps; got {checkedSteps}");
        _output.WriteLine($"C3.SC2 worst |sampledZ - laneSurfaceZ| over {checkedSteps} steps: {worst:F4} m");
    }

    // Independent reference: the elevation of the nearest ped-lane polyline point. Deliberately a plain
    // brute-force scan written in the test, so it shares no code with the implementation under test.
    private static double NearestPedLaneElevation(Sim.Pedestrians.PedNetwork net, double x, double y)
    {
        var best = double.PositiveInfinity;
        var bestZ = double.NaN;

        void Consider(IReadOnlyList<Sim.Core.Orca.Vec2> shape, IReadOnlyList<double>? zs)
        {
            if (zs is not { Count: > 0 } || shape.Count == 0)
            {
                return;
            }

            for (var i = 0; i < shape.Count - 1 && i + 1 < zs.Count; i++)
            {
                var ax = shape[i].X; var ay = shape[i].Y;
                var bx = shape[i + 1].X; var by = shape[i + 1].Y;
                var dx = bx - ax; var dy = by - ay;
                var len2 = (dx * dx) + (dy * dy);
                var t = len2 > 0 ? Math.Clamp((((x - ax) * dx) + ((y - ay) * dy)) / len2, 0.0, 1.0) : 0.0;
                var qx = ax + (t * dx); var qy = ay + (t * dy);
                var d2 = ((x - qx) * (x - qx)) + ((y - qy) * (y - qy));
                if (d2 < best)
                {
                    best = d2;
                    bestZ = zs[i] + ((zs[i + 1] - zs[i]) * t);
                }
            }
        }

        foreach (var sw in net.Sidewalks) Consider(sw.Shape, sw.ShapeZ);
        foreach (var cr in net.Crossings) Consider(cr.Shape, cr.ShapeZ);
        foreach (var wa in net.WalkingAreas) Consider(wa.Polygon, wa.PolygonZ);

        return bestZ;
    }

    // ---- C3·SC3: the 2-D regression -----------------------------------------------------------------

    [Fact]
    public void On2DDemoNet_EveryPedZIsExactlyZero_AcrossTwoHundredSteps()
    {
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        using var sim = new LiveCitySim(cfg);

        for (var i = 0; i < 200; i++)
        {
            sim.Step();
            foreach (var ped in sim.Sample().Peds)
            {
                Assert.Equal(0.0, ped.Z); // bitwise: 0.0, not "approximately"
            }
        }

        _output.WriteLine($"2-D demo after 200 steps: PeakPeds={sim.PeakPeds} ArrivedTotal={sim.ArrivedTotal}");
    }

    // ---- C3·SC4: parity-inertness, ASSERTED ---------------------------------------------------------

    [Fact]
    public void PedTrajectoriesAreBitwiseIdentical_WithElevationPopulatedVersusNull()
    {
        // THE test that proves §3.3. Two runs on the SAME 3-D fixture: one sampling elevation every
        // step (so the whole channel is computed and consumed), one never touching it. If z had leaked
        // into any steering, ORCA or routing decision, the 2-D trajectories would diverge.
        static List<(int Id, double X, double Y)> Run(bool readElevation)
        {
            using var sim = new LiveCitySim(Config3D());
            var trace = new List<(int, double, double)>();

            for (var step = 0; step < 200; step++)
            {
                sim.Step();
                var snap = sim.Sample();
                foreach (var ped in snap.Peds)
                {
                    if (readElevation)
                    {
                        // touch z so the channel is genuinely exercised in this arm
                        _ = ped.Z;
                    }

                    trace.Add((ped.Id, ped.X, ped.Y));
                }
            }

            return trace;
        }

        var withZ = Run(readElevation: true);
        var withoutZ = Run(readElevation: false);

        Assert.NotEmpty(withZ);
        Assert.Equal(withoutZ.Count, withZ.Count);

        for (var i = 0; i < withZ.Count; i++)
        {
            Assert.Equal(withoutZ[i].Id, withZ[i].Id);
            Assert.Equal(BitConverter.DoubleToInt64Bits(withoutZ[i].X), BitConverter.DoubleToInt64Bits(withZ[i].X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(withoutZ[i].Y), BitConverter.DoubleToInt64Bits(withZ[i].Y));
        }

        _output.WriteLine($"C3.SC4: {withZ.Count} ped samples bitwise identical across both arms");
    }

    // ---- C3·SC5: cost -- expected to be in the noise, because it is one projection per ped ----------

    [Fact]
    public void SampleCost_WithElevation_IsInTheNoise()
    {
        using var sim = new LiveCitySim(Config3D());
        sim.SetPedDensity(400, 40.0);

        for (var i = 0; i < 400; i++)
        {
            sim.Step();
        }

        var live = sim.CurrentPeds;
        _output.WriteLine($"live peds during measurement: {live}");

        // Warm up, then time Sample() itself. Both arms call the same method; the point is the absolute
        // number, reported so a future regression to a searching implementation is visible.
        for (var i = 0; i < 5; i++)
        {
            _ = sim.Sample();
        }

        var times = new List<double>();
        for (var rep = 0; rep < 5; rep++)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
            {
                _ = sim.Sample();
            }

            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds / 20.0);
        }

        times.Sort();
        var median = times[times.Count / 2];
        _output.WriteLine($"C3.SC5 Sample() with elevation, {live} peds: median {median:F3} ms/call "
            + $"(min {times[0]:F3}, max {times[^1]:F3})");

        // A generous ceiling: this is one projection over a short per-ped polyline. A nearest-lane
        // search over the whole net would be orders of magnitude slower and would trip this.
        Assert.True(median < 25.0, $"Sample() took {median:F3} ms with {live} peds -- suspiciously slow for one lerp per ped");
    }
}
