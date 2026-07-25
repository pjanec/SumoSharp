using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Sim.LiveCity;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// docs/LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md §F4 (no car-car-overlap invariant) -- FIRST part: the
// AUTHORITATIVE car-car overlap regression guard for the live-city demo. It runs the coupled
// LiveCitySim headless and OBB-checks the engine's OWN Sample() positions every frame (raw
// authoritative poses -- NOT the DR-reconstructed render; that is a separate later step that pulls in
// Sim.Viz/VizReplayBuilder).
//
// This is a FAIL-FIRST characterization test: it documents the KNOWN §F3 pre-existing junction-overlap
// engine bug (cars on crossing internal junction lanes occupying the same space, worst ~3 m -- e.g.
// veh58 drives through stopped veh159). Today it asserts the overlap is PRESENT (proving the check is
// live, not vacuous) and BOUNDED (a regression tripwire in the family of §F2/Task A, which blew car-car
// overlaps far past the §F3 baseline). When §F3 is FIXED, both assertions flip to assert ZERO overlap.
public class DemoCarOverlapInvariantTests
{
    private readonly ITestOutputHelper _out;

    public DemoCarOverlapInvariantTests(ITestOutputHelper output)
    {
        _out = output;
    }

    // Resolve the repo root the same way LiveCitySimTests does (git rev-parse, walk-up fallback).
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

    // Oriented-box overlap depth (metres) between two vehicles, each encoded as the 5-tuple
    // [cx, cy, headingDeg, length, width]. COPIED verbatim (same math) from ObbOverlap in
    // RunLiveCityDrCheck (src/Sim.Viz/Program.cs). Heading maps to forward = (-sinθ, cosθ) -- the
    // convention validated in §F4 (NOT (cosθ, sinθ), which rotates every box 90°); right axis is
    // perpendicular. Separating-axis test over the 4 box axes; returns the minimum penetration across
    // axes, or 0 if any axis separates (a separating axis => disjoint).
    private static double ObbOverlap(double[] a, double[] b)
    {
        double PenOnAxis(double axX, double axY)
        {
            double Half(double[] v, double ax, double ay)
            {
                var th = v[2] * Math.PI / 180.0;
                var fx = -Math.Sin(th); var fy = Math.Cos(th);   // forward (length) axis
                var rx = -fy; var ry = fx;                       // right (width) axis
                var hl = v[3] * 0.5; var hw = v[4] * 0.5;
                return Math.Abs((fx * ax + fy * ay) * hl) + Math.Abs((rx * ax + ry * ay) * hw);
            }
            var centerGap = Math.Abs((b[0] - a[0]) * axX + (b[1] - a[1]) * axY);
            return Half(a, axX, axY) + Half(b, axX, axY) - centerGap; // >0 => overlap on this axis
        }
        double minPen = double.PositiveInfinity;
        foreach (var v in new[] { a, b })
        {
            var th = v[2] * Math.PI / 180.0;
            var fx = Math.Cos(th); var fy = Math.Sin(th);
            foreach (var (ax, ay) in new[] { (fx, fy), (-fy, fx) })
            {
                var p = PenOnAxis(ax, ay);
                if (p <= 0) return 0; // separating axis found -> disjoint
                if (p < minPen) minPen = p;
            }
        }
        return minPen;
    }

    [Fact]
    public void DemoAuthoritative_CarFootprints_DoNotOverlapBeyondKnownF3Baseline()
    {
        const int steps = 200;
        const double noiseThreshold = 0.05; // ignore sub-5cm grazes (numeric noise), same as --live-city-drcheck

        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        using var sim = new LiveCitySim(cfg);

        double worstPenetration = 0.0;
        string worstPair = "(none)";
        int worstStep = -1;
        int maxOverlappingPairsInAnyFrame = 0;
        long totalOverlappingPairEvents = 0;

        for (var st = 0; st < steps; st++)
        {
            sim.Step();
            var cars = sim.Sample().Cars;

            var pairsThisFrame = 0;
            for (var i = 0; i < cars.Count; i++)
            {
                for (var j = i + 1; j < cars.Count; j++)
                {
                    var pen = ObbOverlap(
                        new[] { cars[i].X, cars[i].Y, cars[i].AngleDeg, cars[i].Length, cars[i].Width },
                        new[] { cars[j].X, cars[j].Y, cars[j].AngleDeg, cars[j].Length, cars[j].Width });
                    if (pen <= noiseThreshold) continue;

                    pairsThisFrame++;
                    totalOverlappingPairEvents++;
                    if (pen > worstPenetration)
                    {
                        worstPenetration = pen;
                        var a = cars[i].Name; var b = cars[j].Name;
                        worstPair = string.CompareOrdinal(a, b) < 0 ? a + " / " + b : b + " / " + a;
                        worstStep = st;
                    }
                }
            }

            if (pairsThisFrame > maxOverlappingPairsInAnyFrame)
            {
                maxOverlappingPairsInAnyFrame = pairsThisFrame;
            }
        }

        _out.WriteLine(
            $"AUTHORITATIVE car-overlap invariant ({steps} steps): worst penetration {worstPenetration:F3} m "
            + $"on pair [{worstPair}] at step {worstStep}; max overlapping pairs/frame {maxOverlappingPairsInAnyFrame}; "
            + $"total overlapping-pair events {totalOverlappingPairEvents}.");

        // (A) LIVE + NON-VACUOUS: the check actually detects the real §F3 junction overlap (worst ~3 m).
        //     If this ever drops <= 0.5 m the bug is either fixed (flip this whole test to assert ZERO) or
        //     the check has gone dead (footprints/heading convention broke) -- either way, investigate.
        Assert.True(worstPenetration > 0.5,
            $"expected the live §F3 junction overlap to be detected (worst > 0.5 m), got {worstPenetration:F3} m "
            + $"on pair [{worstPair}] at step {worstStep}. If §F3 is FIXED, flip this test to assert ZERO overlap.");

        // (B) BOUNDED (regression tripwire): §F3 overlaps stay in the known band. A §F2/Task-A-style
        //     regression (laterally-invisible straddling cars, followers creeping in) blows far past this.
        //     Ceilings encode the MEASURED §F3 baseline on this branch (200 steps, default density):
        //       worst penetration = 3.035 m (pair __veh134/__veh38, step 197); max overlapping pairs in
        //       any single frame = 4; total overlapping-pair events = 116.
        //     Set just above the measured baseline (worst < 4.0 m; pairs ceiling = 4 + 3 margin = 7).
        Assert.True(worstPenetration < 4.0,
            $"REGRESSION: worst car-car penetration {worstPenetration:F3} m exceeded the §F3 bound (4.0 m) "
            + $"on pair [{worstPair}] at step {worstStep}.");
        Assert.True(maxOverlappingPairsInAnyFrame <= MAX_OVERLAPPING_PAIRS_CEILING,
            $"REGRESSION: {maxOverlappingPairsInAnyFrame} overlapping pairs in a single frame exceeded the "
            + $"§F3 baseline ceiling ({MAX_OVERLAPPING_PAIRS_CEILING} = measured baseline + 3). A Task-A-style "
            + "lateral-freeze regression would blow past this.");
    }

    // Measured §F3 baseline (4 pairs in the worst frame) + 3 margin. Encodes the known §F3 overlap band;
    // a Task-A-style lateral-freeze regression would blow well past this. See comment on assertion (B).
    private const int MAX_OVERLAPPING_PAIRS_CEILING = 7;
}
