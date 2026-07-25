using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Sim.Core;
using Sim.LiveCity;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// docs/F3-JUNCTION-OVERLAP-HANDOFF.md -- DIAGNOSTIC instrument, NOT a regression guard. This test
// ALWAYS PASSES BY DESIGN: it makes no assertions that can fail.
//
// This variant of the diagnostic tests the FRONT-BUMPER-VS-CENTRE hypothesis: the sampled (X,Y) that
// every OBB overlap computation in this repo treats as the box CENTRE is actually SUMO's front-bumper
// arc-length position (LiveCitySim.Sample() copies _lastSnapshot.PosX/PosY, filled in Engine.cs from
// LaneGeometry.PositionAtOffset(lane.Shape, v.Kinematics.Pos, ...), and Kinematics.Pos is the
// front-bumper offset -- PositionAtOffset never subtracts half a vehicle length). So every OBB drawn
// from (X,Y) directly is shifted forward by Length/2 along the vehicle's own forward axis.
//
// It runs the SAME 200-step live-city simulation ONCE (single LiveCitySim, single Step() loop, cars
// sampled once per step) and computes car-car OBB overlaps TWICE per step from that single sampled
// frame:
//   - "FRONT-ANCHOR (current, as committed)": (X,Y) passed straight through as the OBB centre, exactly
//     as DemoCarOverlapInvariantTests and the original single-variant version of this file did.
//   - "CENTRE-CORRECTED": (X,Y) back-shifted along forward = (-sin th, cos th) by Length/2 before the
//     same ObbOverlap math is applied.
// Running both variants from the same sampled frames guarantees byte-identical trajectories between
// the two -- only the OBB anchor differs.
//
// A separate sub-investigation characterises exact same-lane/same-pos co-location events (two cars
// reporting the identical engine-authoritative lane + pos + speed) without attempting to fix them.
//
// Pure diagnosis: does not touch src/Sim.Core/**, src/Sim.Ingest/**, or src/Sim.LiveCity/**. See
// DemoCarOverlapInvariantTests for the pass/fail bounded-regression guard on the same phenomenon.
public class F3JunctionOverlapDiagTests
{
    private readonly ITestOutputHelper _out;

    public F3JunctionOverlapDiagTests(ITestOutputHelper output)
    {
        _out = output;
    }

    // ---------------------------------------------------------------------------------------------
    // Copied VERBATIM from DemoCarOverlapInvariantTests (tests/Sim.LiveCity.Tests/DemoCarOverlapInvariantTests.cs).
    // ---------------------------------------------------------------------------------------------

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

    // ---------------------------------------------------------------------------------------------
    // Diagnostic-only additions below this line.
    // ---------------------------------------------------------------------------------------------

    private const double NoiseThreshold = 0.05; // same as DemoCarOverlapInvariantTests
    private const double CoLocationThreshold = 0.01; // same-lane, same-pos co-location threshold

    private const string BothInternalSameLane = "BOTH-INTERNAL-SAME-LANE";
    private const string BothInternalDifferentLane = "BOTH-INTERNAL-DIFFERENT-LANE";
    private const string OneInternalOneNormal = "ONE-INTERNAL-ONE-NORMAL";
    private const string BothNormalSameLane = "BOTH-NORMAL-SAME-LANE";
    private const string BothNormalDifferentLane = "BOTH-NORMAL-DIFFERENT-LANE";

    private static readonly string[] Buckets =
    {
        BothInternalSameLane, BothInternalDifferentLane, OneInternalOneNormal, BothNormalSameLane, BothNormalDifferentLane,
    };

    // One car-car overlap EVENT (a single (step, unordered pair) with penetration > NoiseThreshold),
    // fully joined to its WitnessAuthoritative() record by VehicleHandle.
    private sealed record EventRow(
        int Step,
        string NameA, string LaneA, double PosA, double LatA, double SpdA, char TlA,
        string NameB, string LaneB, double PosB, double LatB, double SpdB, char TlB,
        double Penetration)
    {
        public string Bucket => Classify(LaneA, LaneB);
    }

    // One exact same-lane/same-pos co-location row (sub-investigation, variant-independent -- driven
    // by the engine-authoritative witness, not by either OBB anchor).
    private sealed record CoLocatedRow(
        int Step,
        string NameA, VehicleHandle HandleA, string NameB, VehicleHandle HandleB,
        string Lane, double PosA, double PosB, double SpdA, double SpdB, double LatA, double LatB,
        double XA, double YA, double AngA, double XB, double YB, double AngB);

    private static bool IsInternal(string laneId) => laneId.Length > 0 && laneId[0] == ':';

    private static string Classify(string laneA, string laneB)
    {
        var aInt = IsInternal(laneA);
        var bInt = IsInternal(laneB);
        if (aInt && bInt) return laneA == laneB ? BothInternalSameLane : BothInternalDifferentLane;
        if (aInt != bInt) return OneInternalOneNormal;
        return laneA == laneB ? BothNormalSameLane : BothNormalDifferentLane;
    }

    private static string FmtTl(char c) => c == '\0' ? "-" : c.ToString();

    private static string DetailLine(EventRow e) =>
        $"step={e.Step,4} | A={e.NameA,-10} lane={e.LaneA,-14} pos={e.PosA,7:F2} lat={e.LatA,6:F2} spd={e.SpdA,6:F2} tl={FmtTl(e.TlA),1} "
        + $"| B={e.NameB,-10} lane={e.LaneB,-14} pos={e.PosB,7:F2} lat={e.LatB,6:F2} spd={e.SpdB,6:F2} tl={FmtTl(e.TlB),1} "
        + $"| pen={e.Penetration,6:F3} m";

    // Prints the full per-variant report (totals, bucket classification, distinct lane-pair table) and
    // returns the per-bucket (count, worst-penetration) stats so the caller can build the DELTA section.
    private Dictionary<string, (int Count, double Worst)> PrintVariantReport(
        string label, List<EventRow> events, double worstPenOverall, string worstPairOverall, int worstStepOverall, int maxPairsInAnyFrame)
    {
        _out.WriteLine(new string('=', 100));
        _out.WriteLine($"§F3 DIAGNOSTIC -- VARIANT: {label}");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine(
            $"Totals: {events.Count} overlap events, worst penetration {worstPenOverall:F3} m on pair "
            + $"[{worstPairOverall}] at step {worstStepOverall}, max overlapping pairs/frame {maxPairsInAnyFrame}");

        _out.WriteLine("");
        _out.WriteLine("Bucket classification:");
        var bucketStats = new Dictionary<string, (int Count, double Worst)>();
        foreach (var bucket in Buckets)
        {
            var inBucket = events.Where(e => e.Bucket == bucket).ToList();
            if (inBucket.Count == 0)
            {
                _out.WriteLine($"  {bucket}: 0 events");
                bucketStats[bucket] = (0, 0.0);
                continue;
            }

            var worst = inBucket.OrderByDescending(e => e.Penetration).First();
            _out.WriteLine($"  {bucket}: {inBucket.Count} events, worst penetration {worst.Penetration:F3} m");
            _out.WriteLine($"      worst -> {DetailLine(worst)}");
            bucketStats[bucket] = (inBucket.Count, worst.Penetration);
        }

        _out.WriteLine("");
        _out.WriteLine("Distinct lane-pair table (sorted by worst penetration desc):");
        var laneGroups = new Dictionary<(string, string), (string Bucket, int Count, EventRow Worst)>();
        foreach (var e in events)
        {
            var key = string.CompareOrdinal(e.LaneA, e.LaneB) <= 0 ? (e.LaneA, e.LaneB) : (e.LaneB, e.LaneA);
            if (laneGroups.TryGetValue(key, out var existing))
            {
                var worst = e.Penetration > existing.Worst.Penetration ? e : existing.Worst;
                laneGroups[key] = (existing.Bucket, existing.Count + 1, worst);
            }
            else
            {
                laneGroups[key] = (e.Bucket, 1, e);
            }
        }

        var sortedLanePairs = laneGroups.OrderByDescending(kv => kv.Value.Worst.Penetration).ToList();
        if (sortedLanePairs.Count == 0)
        {
            _out.WriteLine("  (none)");
        }
        foreach (var kv in sortedLanePairs)
        {
            var (lane1, lane2) = kv.Key;
            var (bucket, count, worst) = kv.Value;
            _out.WriteLine($"  laneA=[{lane1}] laneB=[{lane2}] bucket={bucket} events={count} worstPenetration={worst.Penetration:F3} m");
            _out.WriteLine($"      worst -> {DetailLine(worst)}");
        }

        _out.WriteLine("");
        return bucketStats;
    }

    [Fact]
    public void F3_ClassifyAuthoritativeOverlaps()
    {
        const int steps = 200;

        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        using var sim = new LiveCitySim(cfg);

        var frontEvents = new List<EventRow>();
        var centreEvents = new List<EventRow>();

        double frontWorstPen = 0.0, centreWorstPen = 0.0;
        string frontWorstPair = "(none)", centreWorstPair = "(none)";
        int frontWorstStep = -1, centreWorstStep = -1;
        int frontMaxPairsInAnyFrame = 0, centreMaxPairsInAnyFrame = 0;

        var coLocated = new List<CoLocatedRow>();
        var distinctLengthWidth = new SortedSet<(double Length, double Width)>();

        // Throughput/congestion sub-investigation (question 4 of the F3 A/B handoff): does the flag
        // visibly reduce throughput or increase stopped cars? Variant-independent (driven by the
        // engine-authoritative sample, not either OBB anchor) -- computed once per step.
        var distinctVehicleNamesEverObserved = new HashSet<string>();
        var stoppedCountByFinalStep = new List<(int Step, int Stopped, int Total)>();
        var stoppedVehicleNamesInFinalWindow = new HashSet<string>();
        const int finalWindow = 10;
        const double stoppedSpeedThreshold = 0.5;

        for (var st = 0; st < steps; st++)
        {
            sim.Step();
            var cars = sim.Sample().Cars;
            var witnesses = sim.WitnessAuthoritative();

            foreach (var c in cars)
            {
                distinctVehicleNamesEverObserved.Add(c.Name);
            }

            if (st >= steps - finalWindow)
            {
                var speedByHandle = new Dictionary<VehicleHandle, double>(witnesses.Count);
                foreach (var w in witnesses)
                {
                    speedByHandle[w.Handle] = w.Speed;
                }

                var stoppedThisStep = 0;
                foreach (var c in cars)
                {
                    if (speedByHandle.TryGetValue(c.Handle, out var spd) && spd < stoppedSpeedThreshold)
                    {
                        stoppedThisStep++;
                        stoppedVehicleNamesInFinalWindow.Add(c.Name);
                    }
                }
                stoppedCountByFinalStep.Add((st, stoppedThisStep, cars.Count));
            }

            // Exact join key: VehicleHandle is shared between LiveCityCar (Sample()) and
            // CarAuthWitness (WitnessAuthoritative()) -- no name/nearest-position matching needed.
            var byHandle = new Dictionary<VehicleHandle, LiveCitySim.CarAuthWitness>(witnesses.Count);
            foreach (var w in witnesses)
            {
                byHandle[w.Handle] = w;
            }

            foreach (var c in cars)
            {
                distinctLengthWidth.Add((c.Length, c.Width));
            }

            // CENTRE-CORRECTED anchor per car this step: back-shift the sampled front-bumper (X,Y) by
            // Length/2 along forward = (-sin th, cos th) to recover the true OBB centre. Computed from
            // the SAME sampled frame as the front-anchor variant below -- identical trajectories.
            var centreXY = new (double X, double Y)[cars.Count];
            for (var i = 0; i < cars.Count; i++)
            {
                var th = cars[i].AngleDeg * Math.PI / 180.0;
                var fx = -Math.Sin(th); var fy = Math.Cos(th);
                var halfLen = cars[i].Length / 2.0;
                centreXY[i] = (cars[i].X - halfLen * fx, cars[i].Y - halfLen * fy);
            }

            var pairsThisFrameFront = 0;
            var pairsThisFrameCentre = 0;

            for (var i = 0; i < cars.Count; i++)
            {
                for (var j = i + 1; j < cars.Count; j++)
                {
                    byHandle.TryGetValue(cars[i].Handle, out var wa);
                    byHandle.TryGetValue(cars[j].Handle, out var wb);

                    // FRONT-ANCHOR (current, as committed): (X,Y) passed straight through.
                    var penFront = ObbOverlap(
                        new[] { cars[i].X, cars[i].Y, cars[i].AngleDeg, cars[i].Length, cars[i].Width },
                        new[] { cars[j].X, cars[j].Y, cars[j].AngleDeg, cars[j].Length, cars[j].Width });
                    if (penFront > NoiseThreshold)
                    {
                        pairsThisFrameFront++;
                        frontEvents.Add(new EventRow(
                            st,
                            cars[i].Name, wa.LaneId ?? string.Empty, wa.Pos, wa.PosLat, wa.Speed, wa.Tl,
                            cars[j].Name, wb.LaneId ?? string.Empty, wb.Pos, wb.PosLat, wb.Speed, wb.Tl,
                            penFront));

                        if (penFront > frontWorstPen)
                        {
                            frontWorstPen = penFront;
                            var a = cars[i].Name; var b = cars[j].Name;
                            frontWorstPair = string.CompareOrdinal(a, b) < 0 ? a + " / " + b : b + " / " + a;
                            frontWorstStep = st;
                        }
                    }

                    // CENTRE-CORRECTED: back-shifted (X,Y) as the OBB centre; everything else identical.
                    var penCentre = ObbOverlap(
                        new[] { centreXY[i].X, centreXY[i].Y, cars[i].AngleDeg, cars[i].Length, cars[i].Width },
                        new[] { centreXY[j].X, centreXY[j].Y, cars[j].AngleDeg, cars[j].Length, cars[j].Width });
                    if (penCentre > NoiseThreshold)
                    {
                        pairsThisFrameCentre++;
                        centreEvents.Add(new EventRow(
                            st,
                            cars[i].Name, wa.LaneId ?? string.Empty, wa.Pos, wa.PosLat, wa.Speed, wa.Tl,
                            cars[j].Name, wb.LaneId ?? string.Empty, wb.Pos, wb.PosLat, wb.Speed, wb.Tl,
                            penCentre));

                        if (penCentre > centreWorstPen)
                        {
                            centreWorstPen = penCentre;
                            var a = cars[i].Name; var b = cars[j].Name;
                            centreWorstPair = string.CompareOrdinal(a, b) < 0 ? a + " / " + b : b + " / " + a;
                            centreWorstStep = st;
                        }
                    }
                }
            }

            if (pairsThisFrameFront > frontMaxPairsInAnyFrame) frontMaxPairsInAnyFrame = pairsThisFrameFront;
            if (pairsThisFrameCentre > centreMaxPairsInAnyFrame) centreMaxPairsInAnyFrame = pairsThisFrameCentre;

            // Co-located-pair sub-investigation: same lane id, |posA - posB| < CoLocationThreshold, at
            // this step -- driven purely by the engine-authoritative witness (Pos/PosLat/Speed), so it
            // is identical for both OBB variants. Characterisation only -- not a fix.
            for (var i = 0; i < witnesses.Count; i++)
            {
                for (var j = i + 1; j < witnesses.Count; j++)
                {
                    var wa = witnesses[i]; var wb = witnesses[j];
                    if (string.IsNullOrEmpty(wa.LaneId) || wa.LaneId != wb.LaneId) continue;
                    if (Math.Abs(wa.Pos - wb.Pos) >= CoLocationThreshold) continue;

                    LiveCityCar ca = default, cb = default;
                    var foundA = false; var foundB = false;
                    foreach (var c in cars)
                    {
                        if (c.Handle == wa.Handle) { ca = c; foundA = true; }
                        else if (c.Handle == wb.Handle) { cb = c; foundB = true; }
                    }
                    if (!foundA || !foundB) continue;

                    coLocated.Add(new CoLocatedRow(
                        st, ca.Name, wa.Handle, cb.Name, wb.Handle, wa.LaneId,
                        wa.Pos, wb.Pos, wa.Speed, wb.Speed, wa.PosLat, wb.PosLat,
                        ca.X, ca.Y, ca.AngleDeg, cb.X, cb.Y, cb.AngleDeg));
                }
            }
        }

        // ============================================================================================
        // Per-variant reports.
        // ============================================================================================
        var frontStats = PrintVariantReport(
            "FRONT-ANCHOR (current, as committed)", frontEvents, frontWorstPen, frontWorstPair, frontWorstStep, frontMaxPairsInAnyFrame);
        var centreStats = PrintVariantReport(
            "CENTRE-CORRECTED", centreEvents, centreWorstPen, centreWorstPair, centreWorstStep, centreMaxPairsInAnyFrame);

        // ============================================================================================
        // DELTA section.
        // ============================================================================================
        _out.WriteLine(new string('=', 100));
        _out.WriteLine("§F3 DIAGNOSTIC -- DELTA (FRONT-ANCHOR -> CENTRE-CORRECTED)");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine(
            $"totals: {frontEvents.Count} -> {centreEvents.Count} events; "
            + $"worst penetration {frontWorstPen:F3} m -> {centreWorstPen:F3} m; "
            + $"max pairs/frame {frontMaxPairsInAnyFrame} -> {centreMaxPairsInAnyFrame}");
        foreach (var bucket in Buckets)
        {
            var f = frontStats[bucket];
            var c = centreStats[bucket];
            _out.WriteLine($"  {bucket}: {f.Count} -> {c.Count} events; worst {f.Worst:F3} m -> {c.Worst:F3} m");
        }

        // ============================================================================================
        // Co-located pair sub-investigation.
        // ============================================================================================
        _out.WriteLine("");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine("§F3 DIAGNOSTIC -- co-located pair investigation (same lane, |posA-posB| < "
            + $"{CoLocationThreshold:F2} m; characterisation only, NOT fixed)");
        _out.WriteLine(new string('=', 100));

        var byPair = coLocated
            .GroupBy(r => string.CompareOrdinal(r.NameA, r.NameB) <= 0 ? (r.NameA, r.NameB) : (r.NameB, r.NameA))
            .OrderBy(g => g.Key.Item1, StringComparer.Ordinal).ThenBy(g => g.Key.Item2, StringComparer.Ordinal)
            .ToList();

        _out.WriteLine($"total co-located rows (step,pair) = {coLocated.Count}; distinct co-located pairs over {steps} steps = {byPair.Count}");
        _out.WriteLine("");

        foreach (var g in byPair)
        {
            var rows = g.OrderBy(r => r.Step).ToList();
            var stepList = rows.Select(r => r.Step).ToList();

            // Consecutive-run detection over the sorted step list.
            var runs = new List<(int Start, int End)>();
            var runStart = stepList[0]; var prev = stepList[0];
            for (var k = 1; k < stepList.Count; k++)
            {
                if (stepList[k] == prev + 1)
                {
                    prev = stepList[k];
                }
                else
                {
                    runs.Add((runStart, prev));
                    runStart = stepList[k]; prev = stepList[k];
                }
            }
            runs.Add((runStart, prev));

            var runsFmt = string.Join(", ", runs.Select(r => r.Start == r.End ? r.Start.ToString() : $"{r.Start}-{r.End}"));
            var persistence = runs.Count == 1 && runs[0].Start != runs[0].End
                ? $"PERSISTS across {runs[0].End - runs[0].Start + 1} consecutive steps"
                : runs.Count == 1
                    ? "single-step blip"
                    : $"{runs.Count} separate runs (blips/re-occurrences)";

            _out.WriteLine($"Pair [{g.Key.Item1} / {g.Key.Item2}]: {stepList.Count} row(s), steps=[{string.Join(",", stepList)}], runs=[{runsFmt}] -- {persistence}");
            foreach (var r in rows)
            {
                _out.WriteLine(
                    $"    step={r.Step,4} A={r.NameA,-10}(handle={r.HandleA}) B={r.NameB,-10}(handle={r.HandleB}) lane={r.Lane} "
                    + $"posA={r.PosA:F3} posB={r.PosB:F3} spdA={r.SpdA:F3} spdB={r.SpdB:F3} latA={r.LatA:F3} latB={r.LatB:F3} "
                    + $"| A: X={r.XA:F3} Y={r.YA:F3} angle={r.AngA:F2} | B: X={r.XB:F3} Y={r.YB:F3} angle={r.AngB:F2}");
            }
        }

        // ============================================================================================
        // Vehicle Length/Width in use (from the sampled cars).
        // ============================================================================================
        _out.WriteLine("");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine("§F3 DIAGNOSTIC -- vehicle Length/Width in use (distinct combos observed across all sampled cars)");
        _out.WriteLine(new string('=', 100));
        foreach (var (length, width) in distinctLengthWidth)
        {
            _out.WriteLine($"  Length={length:F3} m  Width={width:F3} m");
        }

        // ============================================================================================
        // Throughput/congestion sub-investigation (question 4 of the F3 A/B handoff).
        // ============================================================================================
        _out.WriteLine("");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine("§F3 DIAGNOSTIC -- throughput/congestion (does the flag visibly cause congestion?)");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine($"ArrivedTotal (cumulative trip completions over {steps} steps) = {sim.ArrivedTotal}");
        _out.WriteLine($"Distinct vehicle names ever observed (sampled) over {steps} steps = {distinctVehicleNamesEverObserved.Count}");
        _out.WriteLine($"Stopped-car count (speed < {stoppedSpeedThreshold:F1}) per step, final {finalWindow} steps:");
        foreach (var (stStep, stopped, total) in stoppedCountByFinalStep)
        {
            _out.WriteLine($"  step={stStep,4}: stopped={stopped,3} / total={total,3}");
        }
        _out.WriteLine(
            $"Distinct vehicles stopped at least once in the final {finalWindow} steps = {stoppedVehicleNamesInFinalWindow.Count}");

        // ============================================================================================
        // Baseline sanity echo (no assertion -- diagnostic only).
        // ============================================================================================
        _out.WriteLine("");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine(
            $"§F3 DIAGNOSTIC totals -- FRONT-ANCHOR: {frontEvents.Count} events, worst {frontWorstPen:F3} m "
            + $"on [{frontWorstPair}] @ step {frontWorstStep}, max pairs/frame {frontMaxPairsInAnyFrame}. "
            + $"CENTRE-CORRECTED: {centreEvents.Count} events, worst {centreWorstPen:F3} m on [{centreWorstPair}] "
            + $"@ step {centreWorstStep}, max pairs/frame {centreMaxPairsInAnyFrame}. "
            + "This test makes NO assertions -- it always passes by design.");
        _out.WriteLine(new string('=', 100));

        // No assertions. This is a diagnostic instrument, not a regression guard -- see
        // DemoCarOverlapInvariantTests for the pass/fail guard on this same phenomenon.
        Assert.True(true);
    }

    // ---------------------------------------------------------------------------------------------
    // STOPPED-IN-JUNCTION hypothesis quantification (below this line).
    //
    // Hypothesis under test: the dominant cause of car-car overlap on crossing junction internal
    // lanes is NOT a missing junction-admission gate, but that vehicles come to a STOP while sitting
    // ON an internal (':'-prefixed) lane -- they block the junction interior and everything crossing
    // then overlaps them (SUMO prevents this upstream via keepClear / MSVehicle::checkRewindLinkLanes).
    //
    // Pure diagnosis: reads WitnessAuthoritative() and Sample() only, never mutates the engine. Makes
    // no assertions that can fail -- see the pass/fail guards elsewhere for the bounded-regression gate.
    // ---------------------------------------------------------------------------------------------

    // Mutable per-(vehicle) run-in-progress state: the vehicle is stopped (speed < threshold) on the
    // SAME internal lane for a maximal span of consecutive steps. Samples carries GapAhead/NextMouthGap
    // per step in the run for the blocked-exit check (question C).
    private sealed class RunState
    {
        public string Lane = string.Empty;
        public int StartStep;
        public int LastStep;
        public double MinSpeed;
        public readonly List<(int Step, double GapAhead, double NextMouthGap)> Samples = new();
    }

    [Fact]
    public void F3_JunctionStoppingAttribution()
    {
        const int steps = 200;
        const double stoppedThreshold = 0.5;

        Assert.True(
            Environment.GetEnvironmentVariable("LIVECITY_F3OCCUPANCY") is null or "0",
            "LIVECITY_F3OCCUPANCY must be unset/0 for this diagnostic -- it must measure the DEFAULT configuration.");

        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        using var sim = new LiveCitySim(cfg);

        // ---- A. bookkeeping ------------------------------------------------------------------
        var stoppedInternalPairs = new HashSet<(VehicleHandle Handle, string Lane)>();
        long vehicleStepsObservedTotal = 0;
        long vehicleStepsStoppedAny = 0;
        long vehicleStepsStoppedInternal = 0;
        var distinctInternalLanesStopped = new HashSet<string>();
        var distinctVehiclesStoppedInternal = new HashSet<VehicleHandle>();
        var carNameByHandle = new Dictionary<VehicleHandle, string>();

        var activeRuns = new Dictionary<VehicleHandle, RunState>();
        var completedRuns = new List<(VehicleHandle Handle, RunState Run)>();

        // ---- B. F3-target bucket, CENTRE-CORRECTED anchor only (the hypothesis's own preferred
        // anchor, per the F3 handoff) --------------------------------------------------------------
        var centreEvents = new List<EventRow>();

        for (var st = 0; st < steps; st++)
        {
            sim.Step();
            var cars = sim.Sample().Cars;
            var witnesses = sim.WitnessAuthoritative();

            foreach (var c in cars)
            {
                carNameByHandle[c.Handle] = c.Name;
            }

            var byHandle = new Dictionary<VehicleHandle, LiveCitySim.CarAuthWitness>(witnesses.Count);
            foreach (var w in witnesses)
            {
                byHandle[w.Handle] = w;
            }

            // ---- A: per-witness accounting + run tracking ----
            foreach (var w in witnesses)
            {
                vehicleStepsObservedTotal++;
                var stoppedAny = w.Speed < stoppedThreshold;
                if (stoppedAny) vehicleStepsStoppedAny++;

                var onInternal = IsInternal(w.LaneId);
                var stoppedInternal = stoppedAny && onInternal;

                if (stoppedInternal)
                {
                    vehicleStepsStoppedInternal++;
                    stoppedInternalPairs.Add((w.Handle, w.LaneId));
                    distinctInternalLanesStopped.Add(w.LaneId);
                    distinctVehiclesStoppedInternal.Add(w.Handle);

                    if (activeRuns.TryGetValue(w.Handle, out var run) && run.Lane == w.LaneId && run.LastStep == st - 1)
                    {
                        run.LastStep = st;
                        if (w.Speed < run.MinSpeed) run.MinSpeed = w.Speed;
                        run.Samples.Add((st, w.GapAhead, w.NextMouthGap));
                    }
                    else
                    {
                        if (activeRuns.TryGetValue(w.Handle, out var oldRun))
                        {
                            completedRuns.Add((w.Handle, oldRun));
                        }

                        var newRun = new RunState { Lane = w.LaneId, StartStep = st, LastStep = st, MinSpeed = w.Speed };
                        newRun.Samples.Add((st, w.GapAhead, w.NextMouthGap));
                        activeRuns[w.Handle] = newRun;
                    }
                }
                else if (activeRuns.TryGetValue(w.Handle, out var oldRun2))
                {
                    completedRuns.Add((w.Handle, oldRun2));
                    activeRuns.Remove(w.Handle);
                }
            }

            // ---- B: CENTRE-CORRECTED overlap events (identical math to F3_ClassifyAuthoritativeOverlaps) ----
            var centreXY = new (double X, double Y)[cars.Count];
            for (var i = 0; i < cars.Count; i++)
            {
                var th = cars[i].AngleDeg * Math.PI / 180.0;
                var fx = -Math.Sin(th); var fy = Math.Cos(th);
                var halfLen = cars[i].Length / 2.0;
                centreXY[i] = (cars[i].X - halfLen * fx, cars[i].Y - halfLen * fy);
            }

            for (var i = 0; i < cars.Count; i++)
            {
                for (var j = i + 1; j < cars.Count; j++)
                {
                    byHandle.TryGetValue(cars[i].Handle, out var wa);
                    byHandle.TryGetValue(cars[j].Handle, out var wb);

                    var pen = ObbOverlap(
                        new[] { centreXY[i].X, centreXY[i].Y, cars[i].AngleDeg, cars[i].Length, cars[i].Width },
                        new[] { centreXY[j].X, centreXY[j].Y, cars[j].AngleDeg, cars[j].Length, cars[j].Width });
                    if (pen > NoiseThreshold)
                    {
                        centreEvents.Add(new EventRow(
                            st,
                            cars[i].Name, wa.LaneId ?? string.Empty, wa.Pos, wa.PosLat, wa.Speed, wa.Tl,
                            cars[j].Name, wb.LaneId ?? string.Empty, wb.Pos, wb.PosLat, wb.Speed, wb.Tl,
                            pen));
                    }
                }
            }
        }

        // Flush any runs still in progress at the final step.
        foreach (var kv in activeRuns)
        {
            completedRuns.Add((kv.Key, kv.Value));
        }

        // ============================================================================================
        // A. Prevalence of stopping inside a junction.
        // ============================================================================================
        _out.WriteLine(new string('=', 100));
        _out.WriteLine("§F3-STOP DIAGNOSTIC -- A. Prevalence of stopping inside a junction");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine($"A1. Distinct (vehicle, internal-lane) pairs with speed < {stoppedThreshold:F1} on a ':' lane = {stoppedInternalPairs.Count}");
        _out.WriteLine($"A2. Total vehicle-steps with speed < {stoppedThreshold:F1} on a ':' lane           = {vehicleStepsStoppedInternal}");

        _out.WriteLine("");
        _out.WriteLine("A3. Top 10 longest consecutive stopped-on-same-internal-lane runs:");
        _out.WriteLine($"    {"vehicle",-14} {"lane",-16} {"runSteps",8} {"minSpeed",9} {"stepRange",-14}");
        var top10Runs = completedRuns
            .OrderByDescending(r => r.Run.LastStep - r.Run.StartStep + 1)
            .Take(10)
            .ToList();
        if (top10Runs.Count == 0)
        {
            _out.WriteLine("    (no stopped-on-internal-lane runs observed)");
        }
        foreach (var (handle, run) in top10Runs)
        {
            var name = carNameByHandle.GetValueOrDefault(handle, handle.ToString());
            var len = run.LastStep - run.StartStep + 1;
            _out.WriteLine($"    {name,-14} {run.Lane,-16} {len,8} {run.MinSpeed,9:F3} {$"{run.StartStep}-{run.LastStep}",-14}");
        }

        _out.WriteLine("");
        _out.WriteLine($"A4. Distinct internal lanes that ever host a stopped vehicle = {distinctInternalLanesStopped.Count}");
        _out.WriteLine($"    Distinct vehicles that ever stop on an internal lane      = {distinctVehiclesStoppedInternal.Count}");

        _out.WriteLine("");
        _out.WriteLine($"A5. Total vehicle-steps observed (all lanes)                       = {vehicleStepsObservedTotal}");
        _out.WriteLine($"    Total vehicle-steps with speed < {stoppedThreshold:F1} on ANY lane            = {vehicleStepsStoppedAny}");
        _out.WriteLine($"    Total vehicle-steps with speed < {stoppedThreshold:F1} on an internal (':') lane = {vehicleStepsStoppedInternal}");
        var fractionOfStopsInJunction = vehicleStepsStoppedAny > 0 ? (double)vehicleStepsStoppedInternal / vehicleStepsStoppedAny : 0.0;
        _out.WriteLine($"    => fraction of ALL stopped vehicle-steps that occur on an internal lane = {fractionOfStopsInJunction:P1}");

        // ============================================================================================
        // B. Attribution of F3 overlaps to stopped-in-junction cars.
        // ============================================================================================
        _out.WriteLine("");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine("§F3-STOP DIAGNOSTIC -- B. Attribution of F3 overlaps (CENTRE-CORRECTED anchor)");
        _out.WriteLine(new string('=', 100));

        void ReportBucketSplit(string bucketName, bool withLanePairTable)
        {
            var inBucket = centreEvents.Where(e => e.Bucket == bucketName).ToList();
            var stoppedFoe = inBucket.Where(e => e.SpdA < stoppedThreshold || e.SpdB < stoppedThreshold).ToList();
            var bothMoving = inBucket.Where(e => e.SpdA >= stoppedThreshold && e.SpdB >= stoppedThreshold).ToList();

            _out.WriteLine($"Bucket {bucketName}: {inBucket.Count} total events");
            var stoppedWorst = stoppedFoe.Count > 0 ? stoppedFoe.OrderByDescending(e => e.Penetration).First() : (EventRow?)null;
            var movingWorst = bothMoving.Count > 0 ? bothMoving.OrderByDescending(e => e.Penetration).First() : (EventRow?)null;
            _out.WriteLine($"  STOPPED-FOE (>=1 car < {stoppedThreshold:F1}): {stoppedFoe.Count} events, worst penetration {(stoppedWorst is null ? 0.0 : stoppedWorst.Penetration):F3} m");
            if (stoppedWorst is not null) _out.WriteLine($"      worst -> {DetailLine(stoppedWorst)}");
            _out.WriteLine($"  BOTH-MOVING (both cars >= {stoppedThreshold:F1}):  {bothMoving.Count} events, worst penetration {(movingWorst is null ? 0.0 : movingWorst.Penetration):F3} m");
            if (movingWorst is not null) _out.WriteLine($"      worst -> {DetailLine(movingWorst)}");

            if (!withLanePairTable) return;

            _out.WriteLine("  Distinct lane-pairs in this bucket (class = class of the worst-penetration event for that pair):");
            var laneGroups = new Dictionary<(string, string), (int Count, EventRow Worst)>();
            foreach (var e in inBucket)
            {
                var key = string.CompareOrdinal(e.LaneA, e.LaneB) <= 0 ? (e.LaneA, e.LaneB) : (e.LaneB, e.LaneA);
                if (laneGroups.TryGetValue(key, out var existing))
                {
                    var worst = e.Penetration > existing.Worst.Penetration ? e : existing.Worst;
                    laneGroups[key] = (existing.Count + 1, worst);
                }
                else
                {
                    laneGroups[key] = (1, e);
                }
            }

            if (laneGroups.Count == 0)
            {
                _out.WriteLine("    (none)");
            }
            foreach (var kv in laneGroups.OrderByDescending(kv => kv.Value.Worst.Penetration))
            {
                var (lane1, lane2) = kv.Key;
                var (count, worst) = kv.Value;
                var cls = worst.SpdA < stoppedThreshold || worst.SpdB < stoppedThreshold ? "STOPPED-FOE" : "BOTH-MOVING";
                _out.WriteLine(
                    $"    laneA=[{lane1}] laneB=[{lane2}] class={cls} events={count} worstPenetration={worst.Penetration:F3} m "
                    + $"spdA={worst.SpdA:F3} spdB={worst.SpdB:F3} (at worst: A={worst.NameA} B={worst.NameB} step={worst.Step})");
            }
        }

        _out.WriteLine("B6/B7. BOTH-INTERNAL-DIFFERENT-LANE bucket (the F3 target):");
        ReportBucketSplit(BothInternalDifferentLane, withLanePairTable: true);

        _out.WriteLine("");
        _out.WriteLine("B8. ONE-INTERNAL-ONE-NORMAL bucket (counts + worst per class only):");
        ReportBucketSplit(OneInternalOneNormal, withLanePairTable: false);

        // ============================================================================================
        // C. Blocked-exit check for the top stop events from A3.
        // ============================================================================================
        _out.WriteLine("");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine("§F3-STOP DIAGNOSTIC -- C. Blocked-exit check (GapAhead / NextMouthGap for the longest stop runs)");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine(
            "NOTE: CarAuthWitness does not expose the NEXT lane's string id (only NextLaneHandles internally, "
            + "which is not part of the public WitnessAuthoritative() surface) -- so the actual next-lane id "
            + "cannot be determined through existing public API. GapAhead (same-lane leader gap) and "
            + "NextMouthGap (distance to the nearest car on the NEXT lane, measured from that lane's start; "
            + "+Inf if the exit is clear/unknown) are reported instead -- a small NextMouthGap while GapAhead "
            + "is +Inf (own lane clear ahead) is the fingerprint of a blocked-exit/keepClear-type stop.");
        _out.WriteLine("");

        var topFewForC = completedRuns
            .OrderByDescending(r => r.Run.LastStep - r.Run.StartStep + 1)
            .Take(5)
            .ToList();
        if (topFewForC.Count == 0)
        {
            _out.WriteLine("(no stopped-on-internal-lane runs to inspect)");
        }
        foreach (var (handle, run) in topFewForC)
        {
            var name = carNameByHandle.GetValueOrDefault(handle, handle.ToString());
            _out.WriteLine($"Run: vehicle={name} lane={run.Lane} steps={run.StartStep}-{run.LastStep} minSpeed={run.MinSpeed:F3}");
            foreach (var (stepNo, gapAhead, nextMouthGap) in run.Samples)
            {
                var gapStr = double.IsPositiveInfinity(gapAhead) ? "+Inf" : gapAhead.ToString("F3");
                var mouthStr = double.IsPositiveInfinity(nextMouthGap) ? "+Inf" : nextMouthGap.ToString("F3");
                _out.WriteLine($"    step={stepNo,4} GapAhead={gapStr,8} NextMouthGap={mouthStr,8}");
            }
        }

        // ============================================================================================
        // Verdict.
        // ============================================================================================
        _out.WriteLine("");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine("§F3-STOP DIAGNOSTIC -- VERDICT");
        _out.WriteLine(new string('=', 100));

        var f3Bucket = centreEvents.Where(e => e.Bucket == BothInternalDifferentLane).ToList();
        var f3StoppedFoe = f3Bucket.Count(e => e.SpdA < stoppedThreshold || e.SpdB < stoppedThreshold);
        var f3BothMoving = f3Bucket.Count - f3StoppedFoe;
        var majorityStoppedFoe = f3Bucket.Count > 0 && f3StoppedFoe > f3BothMoving;

        _out.WriteLine(
            $"BOTH-INTERNAL-DIFFERENT-LANE: {f3Bucket.Count} events total, {f3StoppedFoe} STOPPED-FOE, {f3BothMoving} BOTH-MOVING.");
        _out.WriteLine(
            $"Stopping inside a junction: {stoppedInternalPairs.Count} distinct (vehicle,lane) pairs, "
            + $"{vehicleStepsStoppedInternal} vehicle-steps, {fractionOfStopsInJunction:P1} of all stopped vehicle-steps.");

        if (f3Bucket.Count == 0)
        {
            _out.WriteLine(
                "VERDICT: INCONCLUSIVE -- no BOTH-INTERNAL-DIFFERENT-LANE overlap events occurred in this 200-step run "
                + "under the default configuration, so the hypothesis cannot be scored against this bucket.");
        }
        else if (majorityStoppedFoe && stoppedInternalPairs.Count > 0)
        {
            _out.WriteLine(
                $"VERDICT: SUPPORTED -- {f3StoppedFoe}/{f3Bucket.Count} ({(double)f3StoppedFoe / f3Bucket.Count:P1}) of "
                + $"BOTH-INTERNAL-DIFFERENT-LANE overlap events involve at least one car stopped (< {stoppedThreshold:F1} m/s), "
                + $"and stopping inside a junction is not rare ({stoppedInternalPairs.Count} distinct (vehicle,lane) pairs, "
                + $"{fractionOfStopsInJunction:P1} of all stopped vehicle-steps happen on an internal lane).");
        }
        else
        {
            _out.WriteLine(
                $"VERDICT: REFUTED -- only {f3StoppedFoe}/{f3Bucket.Count} ({(double)f3StoppedFoe / f3Bucket.Count:P1}) of "
                + "BOTH-INTERNAL-DIFFERENT-LANE overlap events involve a stopped car; most F3-target overlaps are "
                + "BOTH-MOVING, so stopping-in-junction is not the dominant cause under this run.");
        }

        _out.WriteLine(new string('=', 100));

        // No assertions on the hypothesis outcome -- this is a diagnostic instrument, not a regression
        // guard. The only assertion is the harness precondition (default config, gate above).
        Assert.True(true);
    }

    // ---------------------------------------------------------------------------------------------
    // BindingConstraint / JunctionYieldArm attribution (below this line).
    //
    // Follow-on to F3_JunctionStoppingAttribution's finding (docs/F3-JUNCTION-OVERLAP-DESIGN.md
    // "(1) HIGHEST VALUE"): __veh127 and __veh140 sit stopped on an internal lane for ~100 steps with
    // GapAhead=+Inf and NextMouthGap=+Inf (no leader, no blocked exit). This test reads the engine's own
    // per-vehicle diagnostic fields -- WitnessAuthoritative()'s Binder/JyArm/JyFoeSpeed, which already
    // surface Engine.cs's BindingConstraint/JunctionYieldArm/JunctionYieldFoeSpeed (VehicleRuntime,
    // written unconditionally by ComputeMoveIntent's diagnostic argmin fold, see Engine.cs:5067-5183 and
    // :6756-7239) -- to identify WHICH speed constraint is pinning each vehicle at ~0.
    //
    // Pure diagnosis: reads WitnessAuthoritative()/Sample() only, never mutates the engine, makes no
    // assertions that can fail beyond the harness precondition below.
    // ---------------------------------------------------------------------------------------------

    // BindingConstraint id -> name, per Engine.cs:5071-5073.
    private static readonly string[] BinderNames =
    {
        "0:none(unconstrained)", "1:leaderFollow", "2:crossJxnLeader", "3:freeFlow", "4:successiveLane",
        "5:deadLaneMerge", "6:stopLine", "7:redLight", "8:railSignal", "9:railCrossing",
        "10:junctionYield", "11:keepClear", "12:obstacle", "13:crowd",
    };

    private static string BinderName(byte b) => b < BinderNames.Length ? BinderNames[b] : $"{b}:UNKNOWN";

    // JunctionYieldArm low bits -> arm name, per the `jyArm` assignments in JunctionYieldConstraint
    // (Engine.cs:6805 cycleHold, :6886 cautiousApproach, :6964 sameTargetMerge, :7001 externalAgent,
    // :7016 adaptToJunctionLeader, :7201 approachingCross). High bit (0x80) is egoHasSignalPriority
    // (Engine.cs:7238), decoded separately.
    private static readonly string[] JyArmNames =
    {
        "0:none", "1:cycleHold", "2:cautiousApproach", "3:sameTargetMerge",
        "4:externalAgent", "5:adaptToJunctionLeader", "6:approachingCross",
    };

    private static string JyArmDecoded(byte raw)
    {
        var arm = raw & 0x7F;
        var priority = (raw & 0x80) != 0;
        var name = arm < JyArmNames.Length ? JyArmNames[arm] : $"{arm}:UNKNOWN";
        return $"raw={raw,3} arm={arm,2} ({name}) signalPriority={(priority ? "YES" : "no")}";
    }

    // One step's full diagnostic row for a single tracked vehicle.
    private sealed record BindRow(
        int Step, string LaneId, double Pos, double Speed, byte Binder, byte JyArmRaw, float JyFoeSpeed,
        double GapAhead, char Tl);

    private static string FmtGap(double g) => double.IsPositiveInfinity(g) ? "+Inf" : g.ToString("F3");

    private static string BindRowLine(BindRow r) =>
        $"step={r.Step,4} | lane={r.LaneId,-16} | pos={r.Pos,7:F2} | spd={r.Speed,6:F3} "
        + $"| Binder={r.Binder,2} ({BinderName(r.Binder),-22}) | JyArm[{JyArmDecoded(r.JyArmRaw)}] "
        + $"| JyFoeSpeed={r.JyFoeSpeed,7:F3} | GapAhead={FmtGap(r.GapAhead),8} | tl={FmtTl(r.Tl)}";

    // Longest consecutive run of speed < stoppedThreshold ON THE SAME INTERNAL (':') LANE within `rows`
    // (rows assumed sorted by Step) -- matches F3_JunctionStoppingAttribution's RunState semantics
    // exactly (same Handle + same Lane + LastStep == st-1), not just "speed below threshold anywhere":
    // a vehicle merely driving slowly while changing internal lanes must NOT count as one long run.
    // Returns (-1,-1,"") if the vehicle is never observed stopped on one internal lane for 2+ steps.
    private static (int Start, int End) LongestStoppedRun(List<BindRow> rows, double stoppedThreshold)
    {
        var bestStart = -1; var bestEnd = -1; var bestLen = 0;
        var curStart = -1; var curLen = 0; var curLane = string.Empty; var prevStep = int.MinValue;
        foreach (var r in rows)
        {
            var contiguous = r.Step == prevStep + 1;
            var stoppedOnInternal = r.Speed < stoppedThreshold && r.LaneId.Length > 0 && r.LaneId[0] == ':';
            if (stoppedOnInternal)
            {
                if (curLen > 0 && contiguous && r.LaneId == curLane)
                {
                    curLen++;
                }
                else
                {
                    curStart = r.Step; curLen = 1; curLane = r.LaneId;
                }

                if (curLen > bestLen)
                {
                    bestLen = curLen; bestStart = curStart; bestEnd = r.Step;
                }
            }
            else
            {
                curLen = 0;
            }

            prevStep = r.Step;
        }

        return bestLen == 0 ? (-1, -1) : (bestStart, bestEnd);
    }

    // Print every step within 5 of the run's start or end boundary, plus every 5th step from
    // runStart through runEnd otherwise -- per the requested "10 steps around each boundary, every
    // 5th step through the middle" sampling.
    private void PrintRunTable(string name, List<BindRow> rows, int runStart, int runEnd)
    {
        _out.WriteLine($"--- {name}: stuck run steps {runStart}-{runEnd} ({runEnd - runStart + 1} steps), "
            + $"printing window [{runStart - 5},{runEnd + 5}] ---");
        foreach (var r in rows)
        {
            if (r.Step < runStart - 5 || r.Step > runEnd + 5) continue;
            var nearStartBoundary = r.Step >= runStart - 5 && r.Step <= runStart + 5;
            var nearEndBoundary = r.Step >= runEnd - 5 && r.Step <= runEnd + 5;
            var everyFifth = (r.Step - runStart) % 5 == 0;
            if (!nearStartBoundary && !nearEndBoundary && !everyFifth) continue;
            _out.WriteLine("    " + BindRowLine(r));
        }
    }

    [Fact]
    public void F3_BindingConstraintAttribution()
    {
        const int steps = 200;
        const double stoppedThreshold = 0.5;

        Assert.True(
            Environment.GetEnvironmentVariable("LIVECITY_F3OCCUPANCY") is null or "0",
            "LIVECITY_F3OCCUPANCY must be unset/0 for this diagnostic -- it must measure the DEFAULT configuration.");

        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        using var sim = new LiveCitySim(cfg);

        var primaryTargets = new[] { "__veh127", "__veh140" };
        var cheapTargets = new[] { "__veh79", "__veh133", "__veh163" };
        var allTargets = primaryTargets.Concat(cheapTargets).ToArray();

        var handleByName = new Dictionary<string, VehicleHandle>();
        var rowsByName = allTargets.ToDictionary(n => n, n => new List<BindRow>());

        for (var st = 0; st < steps; st++)
        {
            sim.Step();
            var cars = sim.Sample().Cars;
            var witnesses = sim.WitnessAuthoritative();

            foreach (var c in cars)
            {
                if (!handleByName.ContainsKey(c.Name) && Array.IndexOf(allTargets, c.Name) >= 0)
                {
                    handleByName[c.Name] = c.Handle;
                }
            }

            // WitnessAuthoritative() is engine-authoritative over ALL active vehicles (no crop filter,
            // unlike Sample().Cars) -- once a target's handle is known, read it here every step so a
            // vehicle that happens to be cropped out of Sample() this frame is still tracked.
            foreach (var name in allTargets)
            {
                if (!handleByName.TryGetValue(name, out var h)) continue;
                foreach (var w in witnesses)
                {
                    if (!w.Handle.Equals(h)) continue;
                    rowsByName[name].Add(new BindRow(st, w.LaneId ?? string.Empty, w.Pos, w.Speed, w.Binder, w.JyArm, w.JyFoeSpeed, w.GapAhead, w.Tl));
                    break;
                }
            }
        }

        _out.WriteLine(new string('=', 100));
        _out.WriteLine("§F3-BIND DIAGNOSTIC -- BindingConstraint / JunctionYieldArm attribution for the stuck-in-junction vehicles");
        _out.WriteLine(new string('=', 100));

        foreach (var name in primaryTargets)
        {
            var rows = rowsByName[name];
            _out.WriteLine("");
            _out.WriteLine(new string('-', 100));
            if (rows.Count == 0)
            {
                _out.WriteLine($"{name}: NEVER OBSERVED (not found in any step's WitnessAuthoritative()).");
                continue;
            }

            var (runStart, runEnd) = LongestStoppedRun(rows, stoppedThreshold);
            var lastRow = rows[^1];
            _out.WriteLine(
                $"{name}: observed steps {rows[0].Step}-{lastRow.Step} ({rows.Count} rows); "
                + $"longest stopped(<{stoppedThreshold:F1})-run = "
                + (runStart < 0 ? "NONE" : $"steps {runStart}-{runEnd} ({runEnd - runStart + 1} steps)"));
            _out.WriteLine($"    last observed: step={lastRow.Step} lane={lastRow.LaneId} pos={lastRow.Pos:F2} speed={lastRow.Speed:F3}"
                + (lastRow.Step < steps - 1 ? "  <-- disappears from WitnessAuthoritative() before the final step (arrived/teleported/removed)" : "  <-- still present at the final observed step"));
            _out.WriteLine("");

            if (runStart >= 0)
            {
                PrintRunTable(name, rows, runStart, runEnd);

                // Binder/JyArm histogram over the run, for the "dominant constraint" answer.
                var binderCounts = new Dictionary<byte, int>();
                var jyArmCounts = new Dictionary<byte, int>();
                foreach (var r in rows)
                {
                    if (r.Step < runStart || r.Step > runEnd) continue;
                    binderCounts[r.Binder] = binderCounts.GetValueOrDefault(r.Binder) + 1;
                    if (r.Binder == 10) jyArmCounts[r.JyArmRaw] = jyArmCounts.GetValueOrDefault(r.JyArmRaw) + 1;
                }

                _out.WriteLine("");
                _out.WriteLine($"    BindingConstraint histogram over the run [{runStart}-{runEnd}]:");
                foreach (var kv in binderCounts.OrderByDescending(kv => kv.Value))
                {
                    _out.WriteLine($"        {BinderName(kv.Key),-24} : {kv.Value} steps");
                }

                if (jyArmCounts.Count > 0)
                {
                    _out.WriteLine($"    JunctionYieldArm histogram (rows where Binder==10) over the run [{runStart}-{runEnd}]:");
                    foreach (var kv in jyArmCounts.OrderByDescending(kv => kv.Value))
                    {
                        _out.WriteLine($"        {JyArmDecoded(kv.Key),-60} : {kv.Value} steps");
                    }
                }

                // Transition: the row(s) immediately before the run began.
                _out.WriteLine("");
                _out.WriteLine("    Transition into the stuck state (rows immediately before runStart):");
                foreach (var r in rows)
                {
                    if (r.Step < runStart - 3 || r.Step >= runStart) continue;
                    _out.WriteLine("        " + BindRowLine(r));
                }
            }
        }

        _out.WriteLine("");
        _out.WriteLine(new string('-', 100));
        _out.WriteLine("Cheap dominant-constraint check for the remaining top-10 stuck vehicles:");
        foreach (var name in cheapTargets)
        {
            var rows = rowsByName[name];
            if (rows.Count == 0)
            {
                _out.WriteLine($"    {name}: NEVER OBSERVED.");
                continue;
            }

            var (runStart, runEnd) = LongestStoppedRun(rows, stoppedThreshold);
            if (runStart < 0)
            {
                _out.WriteLine($"    {name}: observed {rows.Count} rows, no stopped run found.");
                continue;
            }

            var binderCounts = new Dictionary<byte, int>();
            var jyArmCounts = new Dictionary<byte, int>();
            foreach (var r in rows)
            {
                if (r.Step < runStart || r.Step > runEnd) continue;
                binderCounts[r.Binder] = binderCounts.GetValueOrDefault(r.Binder) + 1;
                if (r.Binder == 10) jyArmCounts[r.JyArmRaw] = jyArmCounts.GetValueOrDefault(r.JyArmRaw) + 1;
            }

            var domBinder = binderCounts.OrderByDescending(kv => kv.Value).First();
            var domJyArm = jyArmCounts.Count > 0 ? jyArmCounts.OrderByDescending(kv => kv.Value).First().Key : (byte?)null;
            _out.WriteLine(
                $"    {name,-10} run={runStart}-{runEnd} ({runEnd - runStart + 1} steps) "
                + $"dominantBinder={domBinder.Key} ({BinderName(domBinder.Key)}) [{domBinder.Value}/{runEnd - runStart + 1} steps]"
                + (domJyArm is null ? string.Empty : $" dominantJyArm=[{JyArmDecoded(domJyArm.Value)}]"));
        }

        _out.WriteLine("");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine("§F3-BIND DIAGNOSTIC -- end. No assertions made -- diagnostic only.");
        _out.WriteLine(new string('=', 100));

        Assert.True(true);
    }
}
