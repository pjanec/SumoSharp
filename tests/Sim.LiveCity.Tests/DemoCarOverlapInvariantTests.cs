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
        //       any single frame = 4.
        //     Set just above the measured baseline (worst < 4.0 m; pairs ceiling = 4 + 3 margin = 7).
        //     NOTE (Task A redo, SuppressHeldCrowdSwerve now ON by default): total overlapping-pair EVENTS
        //     rose 116 -> 178 because cars that used to swerve THROUGH a crosswalk ped now correctly STOP
        //     and queue, so the SAME pre-existing §F3 junction overlaps are exposed across more frames. The
        //     two SEVERITY ceilings above are UNCHANGED (worst still 3.035 m, max pairs/frame still 4). Lane-
        //     classified diff (fix on vs off): junction pairs 30->30 (worst 3.035 m both), normal-lane pairs
        //     7->8 (worst 1.800 m both) -- the fix adds only shallow normal-lane overlaps (0.74 m, 0.09 m),
        //     both shallower than 6 pre-existing normal-lane overlaps, and never a straddle (see §F4a).
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

    // ---------------------------------------------------------------------------------------------------
    // §F4a -- TARGETED regression guard for the reverted Task-A §F2 bug.
    // docs/LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md §F2/§F4a.
    //
    // NOTE: the flag that CAUSED §F2, Engine.FreezeLateralWhenStopped (blanket lateral freeze), was reverted
    // and REMOVED; the Task A redo (Engine.SuppressHeldCrowdSwerve, on by default in the demo) only recentres
    // a held car and so structurally cannot straddle -- this guard passes with the redo on. It remains as a
    // GENERAL straddle tripwire: any future change that pins a stopped car past its lane edge trips it. The
    // calibration numbers below were measured against the (now-removed) FreezeLateralWhenStopped flag, which
    // is why they can no longer be reproduced via LIVECITY_FREEZELAT -- they are kept as the record that the
    // guard is non-vacuous (it demonstrably caught a real straddle).
    //
    // §F2 mechanism (historical): with Engine.FreezeLateralWhenStopped ON, a car that dropped below
    // LaneChangeMinSpeed (1.5 m/s) *mid-lane-change* had its lateral offset (PosLat) FROZEN at whatever large
    // value it held -- leaving it pinned straddling two lanes / jutting past its lane edge. A stopped straddler
    // reports gap=Infinity to followers (laterally invisible to car-following), so followers creep into it ->
    // the §F2 overlaps. The invariant we want: NO stopped/slow car may sit straddling past its lane edge.
    //
    // Why a raw peak-|PosLat| guard does NOT work here (measured, 200 steps, this demo):
    //   The demo's crowd-swerve (Engine.ComputeLateralEvasion) legitimately pushes a slow car far
    //   sideways to dodge a pedestrian disc, so the peak stopped |PosLat| is ~5.1 m with the freeze OFF
    //   (transient swerve) AND ~5.2 m with it ON -- the peaks OVERLAP and cannot separate the bug. (This
    //   demo is denser/wider-laned than the 1.45-1.88 m straddle originally logged for §F2.)
    //
    // What DOES separate them is the §F2 fingerprint: the straddle is FROZEN -- PosLat pinned at a value
    // far past the lane edge and held *unchanged* across many consecutive stopped ticks. A legitimate
    // crowd-swerve (freeze OFF) evolves/oscillates every tick and resolves; only the freeze SUSTAINS a
    // deep offset. So the guard counts, per car, the longest run of consecutive stopped ticks (speed <
    // 1.5) in which |PosLat| stays past STRADDLE_EDGE AND does not move (frozen, |ΔPosLat| <= 1e-6).
    //
    // Empirical calibration (200 steps; ran with/without LIVECITY_FREEZELAT and LIVECITY_PEDS):
    //   STRADDLE_EDGE = 1.2 m -- past the ~0.7 m legal within-lane offset and above the ~0.92 m a car
    //     legitimately parks at against a lane edge; inside the §F2 straddle band.
    //   max frozen-straddle run (consecutive frozen ticks with |PosLat| > 1.2 m):
    //       freeze OFF, default density : 0 ticks   (guard PASSES)
    //       freeze OFF, LIVECITY_PEDS=800: 1 tick    (a single transient swerve tick; never sustains)
    //       freeze ON,  default density : 58 ticks  (guard FAILS -- Vehicle#19.1 pinned at 3.178 m)
    //       freeze ON,  LIVECITY_PEDS=800: 74 ticks  (guard FAILS -- pinned at 1.274 m)
    //   OFF never exceeds 1; ON is always >= 58. MAX_FROZEN_STRADDLE_TICKS = 10 sits cleanly between
    //   (10x the OFF transient-noise ceiling, < 1/5 of the smallest ON run) -- PASSES freeze-off, FAILS
    //   freeze-on, on both densities.
    private const double STRADDLE_EDGE = 1.2;             // m past lane centre; between legal (~0.92) and §F2 band
    private const int MAX_FROZEN_STRADDLE_TICKS = 10;     // OFF max observed = 1; ON min observed = 58

    [Fact]
    public void DemoAuthoritative_NoStoppedCarStraddlesPastItsLane()
    {
        const int steps = 200;
        const double stoppedSpeed = 1.5; // == LaneChangeMinSpeed floor; §F2 froze PosLat below this.

        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        using var sim = new LiveCitySim(cfg);

        // Per car: the previous step it was seen on and its PosLat then, plus the current frozen-straddle
        // run length. A run advances only when the car is (a) stopped, (b) past the straddle edge, and
        // (c) at the SAME PosLat as the immediately-preceding tick (the §F2 freeze).
        var prevStep = new Dictionary<string, int>();
        var prevLat = new Dictionary<string, double>();
        var run = new Dictionary<string, int>();

        int maxFrozenRun = 0;
        double maxRunLat = 0.0;
        string worstHandle = "(none)";
        string worstLane = "(none)";
        int worstStep = -1;

        // Also report the raw peak stopped |PosLat| for context (this is what a naive guard would use --
        // it does NOT separate the bug here; see the calibration comment above).
        double maxStoppedAbsPosLat = 0.0;

        for (var st = 0; st < steps; st++)
        {
            sim.Step();
            foreach (var w in sim.WitnessAuthoritative())
            {
                if (w.Speed >= stoppedSpeed) continue;

                var h = w.Handle.ToString();
                var absLat = Math.Abs(w.PosLat);
                if (absLat > maxStoppedAbsPosLat) maxStoppedAbsPosLat = absLat;

                bool frozenFromPrev =
                    prevStep.TryGetValue(h, out var ps) && ps == st - 1 &&
                    prevLat.TryGetValue(h, out var pl) && Math.Abs(w.PosLat - pl) <= 1e-6;
                bool pastEdge = absLat > STRADDLE_EDGE;

                if (pastEdge && frozenFromPrev)
                {
                    var r = run.TryGetValue(h, out var rv) ? rv + 1 : 2; // this tick + the frozen predecessor
                    run[h] = r;
                    if (r > maxFrozenRun)
                    {
                        maxFrozenRun = r;
                        maxRunLat = absLat;
                        worstHandle = h;
                        worstLane = w.LaneId;
                        worstStep = st;
                    }
                }
                else
                {
                    run[h] = pastEdge ? 1 : 0;
                }

                prevStep[h] = st;
                prevLat[h] = w.PosLat;
            }
        }

        _out.WriteLine(
            $"§F4a stopped-car straddle guard ({steps} steps, heldSwerveSuppress="
            + $"{(Environment.GetEnvironmentVariable("LIVECITY_HELDSWERVE") == "0" ? "off" : "ON(default)")}, peds="
            + $"{Environment.GetEnvironmentVariable("LIVECITY_PEDS") ?? "default"}): "
            + $"longest frozen straddle (|PosLat| > {STRADDLE_EDGE:F1} m, stopped, unchanged) = {maxFrozenRun} ticks "
            + $"(handle {worstHandle} on lane [{worstLane}] at |PosLat| {maxRunLat:F3} m, step {worstStep}); "
            + $"ceiling {MAX_FROZEN_STRADDLE_TICKS} ticks. Raw peak stopped |PosLat| = {maxStoppedAbsPosLat:F3} m "
            + "(reported for context; does NOT separate the bug -- crowd-swerve reaches the same peak both ways).");

        Assert.True(maxFrozenRun < MAX_FROZEN_STRADDLE_TICKS,
            $"§F2 STRADDLE REGRESSION: a stopped/slow car (speed < {stoppedSpeed} m/s) stayed FROZEN with "
            + $"|PosLat| > {STRADDLE_EDGE:F1} m for {maxFrozenRun} consecutive ticks (>= ceiling "
            + $"{MAX_FROZEN_STRADDLE_TICKS}) -- pinned straddling past its lane edge (handle {worstHandle}, "
            + $"lane [{worstLane}], |PosLat| {maxRunLat:F3} m, ending step {worstStep}). A car pinned mid-lane-change "
            + "straddling two lanes -- the signature of the reverted Task-A Engine.FreezeLateralWhenStopped bug (§F2). "
            + "A legitimate crowd-swerve resolves within a tick or two and never sustains a deep frozen offset.");
    }
}
