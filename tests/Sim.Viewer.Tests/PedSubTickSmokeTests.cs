using System;
using System.Diagnostics;
using System.IO;
using Sim.LiveCity;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Viewer.Tests;

// Ped-smoothing fix (docs/LIVE-CITY-VISUALS-NOTES.md-adjacent task) -- the "sub-tick smoke" the task
// requires: the OBJECTIVE proof that a ped's render position no longer steps at the sim tick rate. At a
// fixed sim state (two consecutive real LiveCitySim.Step() snapshots, default Dt=0.5s => 2 Hz), samples the
// SAME ped's PedInterpolator-resolved render position at several render times strictly WITHIN that one
// tick and asserts the position changes continuously/monotonically between them -- the old raw-snapshot
// path would have returned the IDENTICAL position for every one of these until the tick boundary.
public sealed class PedSubTickSmokeTests
{
    private readonly ITestOutputHelper _output;

    public PedSubTickSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Mirrors LiveCitySimTests.RepoRoot / SimRecRoundTripTests.RepoRoot.
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
            // fall through to the walk-up fallback
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

    [Fact]
    public void PedInterpolator_SubTickSamples_ChangeContinuously_NotStepwise()
    {
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        using var sim = new LiveCitySim(cfg);

        // Warm up (spawn/settle some peds) before capturing the two bracketing ticks under test.
        for (var i = 0; i < 20; i++)
        {
            sim.Step();
        }

        var interp = new PedInterpolator();

        sim.Step();
        var tA = sim.Time;
        var snapA = sim.Sample().Peds;
        interp.Push(tA, ToFrames(snapA));

        sim.Step();
        var tB = sim.Time;
        var snapB = sim.Sample().Peds;
        interp.Push(tB, ToFrames(snapB));

        Assert.True(tB - tA > 0.0, "expected a real tick gap between the two captured snapshots");

        // Find a ped id present (and moving) in both snapshots -- a walking ped, not a paused/dwelling one.
        int? movingId = null;
        foreach (var pa in snapA)
        {
            foreach (var pb in snapB)
            {
                if (pb.Id != pa.Id)
                {
                    continue;
                }

                var dx = pb.X - pa.X;
                var dy = pb.Y - pa.Y;
                if (Math.Sqrt(dx * dx + dy * dy) > 0.02)
                {
                    movingId = pa.Id;
                }

                break;
            }

            if (movingId is not null)
            {
                break;
            }
        }

        Assert.True(movingId.HasValue, "expected at least one ped id present and moving across both captured ticks");
        var id = movingId!.Value;

        // Sample at several render times STRICTLY within [tA, tB) -- the sub-tick proof.
        const int SubSamples = 6;
        var times = new double[SubSamples];
        var xs = new double[SubSamples];
        var ys = new double[SubSamples];
        for (var i = 0; i < SubSamples; i++)
        {
            var t = tA + (tB - tA) * (i / (double)SubSamples); // 0/6, 1/6, ..., 5/6 through the tick.
            times[i] = t;
            var sample = interp.Sample(t);
            var found = false;
            foreach (var p in sample)
            {
                if (p.Id == id)
                {
                    xs[i] = p.X;
                    ys[i] = p.Y;
                    found = true;
                    break;
                }
            }

            Assert.True(found, $"ped {id} missing from Sample({t})");
        }

        _output.WriteLine($"PED SUB-TICK SMOKE: ped id={id} tick=[{tA:F4},{tB:F4}]");
        for (var i = 0; i < SubSamples; i++)
        {
            _output.WriteLine($"  t={times[i]:F4}  X={xs[i]:F6}  Y={ys[i]:F6}");
        }

        // Continuity/monotonicity: the raw (pre-fix) path would report the IDENTICAL (tA) position for
        // EVERY one of these sub-tick times (a step function) until the tick boundary at tB. The fixed path
        // must instead move smoothly toward the tB position -- assert no two consecutive samples are
        // identical (continuity) and that each step moves monotonically toward the endpoint on each axis
        // that actually changes.
        var dxTotal = (snapBOf(snapB, id).X - snapAOf(snapA, id).X);
        var dyTotal = (snapBOf(snapB, id).Y - snapAOf(snapA, id).Y);

        for (var i = 1; i < SubSamples; i++)
        {
            var moved = Math.Abs(xs[i] - xs[i - 1]) > 1e-9 || Math.Abs(ys[i] - ys[i - 1]) > 1e-9;
            Assert.True(moved, $"expected samples at t={times[i - 1]:F4} and t={times[i]:F4} to differ (no stepping)");

            if (Math.Abs(dxTotal) > 1e-6)
            {
                var sign = Math.Sign(dxTotal);
                Assert.True(sign * (xs[i] - xs[i - 1]) >= -1e-9, $"X should move monotonically toward the tB position (i={i})");
            }

            if (Math.Abs(dyTotal) > 1e-6)
            {
                var sign = Math.Sign(dyTotal);
                Assert.True(sign * (ys[i] - ys[i - 1]) >= -1e-9, $"Y should move monotonically toward the tB position (i={i})");
            }
        }
    }

    private static LiveCityPed snapAOf(System.Collections.Generic.IReadOnlyList<LiveCityPed> peds, int id)
    {
        foreach (var p in peds)
        {
            if (p.Id == id)
            {
                return p;
            }
        }

        throw new InvalidOperationException("id not found");
    }

    private static LiveCityPed snapBOf(System.Collections.Generic.IReadOnlyList<LiveCityPed> peds, int id) => snapAOf(peds, id);

    private static PedInterpFrame[] ToFrames(System.Collections.Generic.IReadOnlyList<LiveCityPed> peds)
    {
        var arr = new PedInterpFrame[peds.Count];
        for (var i = 0; i < peds.Count; i++)
        {
            var p = peds[i];
            arr[i] = new PedInterpFrame(p.Id, p.X, p.Y, p.Regime, p.AnimTag);
        }

        return arr;
    }
}
