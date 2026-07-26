using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Sim.Core;
using Sim.Ingest;
using Sim.LiveCity;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// TEMPORARY MEASUREMENT INSTRUMENT -- to be deleted before this task finishes (git status must be clean).
//
// Long-horizon (~1 simulated hour) A/B measurement of the live-city demo, gates OFF vs the three new F3
// gates (LIVECITY_CONTTURNFIX, LIVECITY_ISLEADERFIX, LIVECITY_INTERNALJUNCTIONFIX) ALL ON, to quantify:
//   (A) terminal gridlock: long-run stopped vehicles ("blocked forever"), throughput collapse, teleport.
//   (B) same-direction interpenetration: same-normal-lane overlaps and same-target-merge overlaps.
//
// Reuses the EXACT harness construction from F3JunctionOverlapDiagTests/DemoCarOverlapInvariantTests
// (LiveCityConfig.ForRepoRoot(RepoRoot()) + new LiveCitySim(cfg) + sim.Step()/Sample()/WitnessAuthoritative())
// so this run is comparable to the existing 200-step diagnostics, just over a much longer horizon.
public class LongHorizonGridlockDiagTests
{
    private readonly ITestOutputHelper _out;
    public LongHorizonGridlockDiagTests(ITestOutputHelper output) { _out = output; }

    private const string ScratchDir = "/tmp/claude-0/-home-user-SumoSharp/e21d49f3-f27d-5fd7-845f-7d5806744c6e/scratchpad";

    // ---- copied verbatim from the existing diagnostics ----
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
            if (proc.ExitCode == 0 && Directory.Exists(Path.Combine(output, "scenarios"))) return output;
        }
        catch { }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "scenarios")) && File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }

    private static bool IsInternal(string laneId) => laneId.Length > 0 && laneId[0] == ':';

    private const double NoiseThreshold = 0.05;
    private const double StoppedThreshold = 0.5;
    private const double FullyCoLocatedThreshold = 1.79; // width is 1.8 -> essentially fully co-located

    private sealed record OverlapEvent(
        int Step, string NameA, string LaneA, double PosA, double SpdA,
        string NameB, string LaneB, double PosB, double SpdB, double Penetration,
        // World positions of BOTH cars, carried so an event can be classified against the
        // high-realism pocket (docs/CONSTRAINT-high-realism-artefact-ladder.md). These are the SAME
        // coordinates VehicleObb.Penetration was given, so the classification cannot drift from the
        // measurement it classifies.
        double Ax, double Ay, double Bx, double By)
    {
        // Inside the high-realism pocket if EITHER car is within `radius` of the pocket centre --
        // deliberately the permissive test: an overlap is visible in the zone if any part of it is.
        public bool InZone(double cx, double cy, double radius)
        {
            var da = (Ax - cx) * (Ax - cx) + (Ay - cy) * (Ay - cy);
            var db = (Bx - cx) * (Bx - cx) + (By - cy) * (By - cy);
            var r2 = radius * radius;
            return da <= r2 || db <= r2;
        }
    }

    private sealed record RunRecord(string Name, string Lane, int Start, int End, bool StillActiveAtEnd);

    private sealed class ConfigResult
    {
        public string Label = "";
        public int StepsRun;
        // High-realism pocket geometry, captured from the run's own sim instance.
        public double PocketX;
        public double PocketY;
        public double PocketPromoteRadius;
        public double PocketDemoteRadius;
        public double SimSecondsReached;
        public long ArrivedTotal;
        public List<(int BucketStartMin, int Completed)> ArrivalsPerBucket = new();
        public List<(int Step, int Moving, int Stopped, int Total)> MovingStoppedSamples = new();
        public List<RunRecord> StoppedRunsOver300 = new();
        public List<RunRecord> BlockedForever = new();
        public Dictionary<string, int> RunLengthHistogram = new(); // bucket label -> count
        public int TotalStoppedRuns;
        public int TeleportCount, TeleportJam, TeleportYield, TeleportWrongLane;
        public List<OverlapEvent> SameNormalLaneEvents = new();
        public List<OverlapEvent> SameTargetMergeEvents = new();
        public List<OverlapEvent> FullyCoLocatedEvents = new();
        public int TotalOverlapEventsAll;
    }

    // Build the set of (laneA,laneB) internal-lane pairs that are a SUMO "same-target merge" -- two
    // links converging on a common downstream lane without crossing (NetworkModel.MergeConflict,
    // Sim.Ingest/NetworkModel.cs:186-188). Canonicalised (ordinal-sorted) so lookup is order-independent.
    private static HashSet<(string, string)> BuildMergeLanePairs(string netPath)
    {
        var model = NetworkParser.Parse(netPath);
        var pairs = new HashSet<(string, string)>();
        foreach (var junction in model.Junctions)
        {
            foreach (var merge in junction.Merges)
            {
                if (merge.EgoLink < 0 || merge.EgoLink >= junction.Links.Count) continue;
                if (merge.FoeLink < 0 || merge.FoeLink >= junction.Links.Count) continue;
                var laneA = junction.Links[merge.EgoLink].InternalLaneId;
                var laneB = junction.Links[merge.FoeLink].InternalLaneId;
                if (string.IsNullOrEmpty(laneA) || string.IsNullOrEmpty(laneB)) continue;
                var key = string.CompareOrdinal(laneA, laneB) <= 0 ? (laneA, laneB) : (laneB, laneA);
                pairs.Add(key);
            }
        }
        return pairs;
    }

    private static string RunLenBucket(int len)
    {
        if (len <= 10) return "1-10";
        if (len <= 50) return "11-50";
        if (len <= 100) return "51-100";
        if (len <= 300) return "101-300";
        if (len <= 600) return "301-600";
        if (len <= 1200) return "601-1200";
        return "1201+";
    }

    private ConfigResult RunConfig(
        string label, bool gatesOn, string repoRoot, HashSet<(string, string)> mergeLanePairs,
        int maxSteps, TimeSpan wallClockBudget, StreamWriter log)
    {
        // Gates are read directly from the environment INSIDE LiveCitySim's constructor (not routed
        // through LiveCityConfig), so set/clear them before constructing the sim.
        Environment.SetEnvironmentVariable("LIVECITY_CONTTURNFIX", gatesOn ? "1" : null);
        Environment.SetEnvironmentVariable("LIVECITY_ISLEADERFIX", gatesOn ? "1" : null);
        Environment.SetEnvironmentVariable("LIVECITY_INTERNALJUNCTIONFIX", gatesOn ? "1" : null);

        var cfg = LiveCityConfig.ForRepoRoot(repoRoot);
        using var sim = new LiveCitySim(cfg);

        var result = new ConfigResult { Label = label };

        // Vehicle name lookup (best-effort, from the crop-filtered Sample(); a handle not seen there
        // yet falls back to its handle's ToString()).
        var nameByHandle = new Dictionary<VehicleHandle, string>();

        // Stopped-run tracking (engine-wide, via WitnessAuthoritative -- NOT crop-filtered, so this
        // captures gridlock anywhere the engine simulates, not just the demo's rendered crop).
        var activeRuns = new Dictionary<VehicleHandle, (string Lane, int Start, int Last)>();

        const int bucketSteps = 1200; // 10 minutes of sim time @ dt=0.5
        long arrivedAtBucketStart = 0;
        var bucketIdx = 0;

        const int sampleEvery = 50; // ~25s of sim time between moving/stopped samples

        var sw = Stopwatch.StartNew();
        var st = 0;
        for (; st < maxSteps; st++)
        {
            if (sw.Elapsed > wallClockBudget)
            {
                log.WriteLine($"[{label}] WALL-CLOCK BUDGET REACHED at step {st} ({sw.Elapsed.TotalSeconds:F1}s) -- stopping early.");
                break;
            }

            sim.Step();
            var cars = sim.Sample().Cars;
            foreach (var c in cars) nameByHandle[c.Handle] = c.Name;
            var witnesses = sim.WitnessAuthoritative();

            // ---- A: stopped-run tracking (engine-wide) ----
            var presentSpeed = new Dictionary<VehicleHandle, (string Lane, double Speed)>(witnesses.Count);
            foreach (var w in witnesses) presentSpeed[w.Handle] = (w.LaneId ?? string.Empty, w.Speed);

            // Finalize runs for handles no longer present or no longer stopped.
            foreach (var h in activeRuns.Keys.ToList())
            {
                var stillStopped = presentSpeed.TryGetValue(h, out var ps) && ps.Speed < StoppedThreshold;
                if (!stillStopped)
                {
                    var (lane, start, last) = activeRuns[h];
                    RecordCompletedRun(result, nameByHandle, h, lane, start, last, stillActiveAtEnd: false);
                    activeRuns.Remove(h);
                }
            }

            var stoppedCount = 0;
            foreach (var w in witnesses)
            {
                if (w.Speed < StoppedThreshold)
                {
                    stoppedCount++;
                    if (activeRuns.TryGetValue(w.Handle, out var run) && run.Lane == (w.LaneId ?? string.Empty))
                    {
                        activeRuns[w.Handle] = (run.Lane, run.Start, st);
                    }
                    else
                    {
                        activeRuns[w.Handle] = (w.LaneId ?? string.Empty, st, st);
                    }
                }
            }

            if (st % sampleEvery == 0)
            {
                result.MovingStoppedSamples.Add((st, witnesses.Count - stoppedCount, stoppedCount, witnesses.Count));
            }

            // ---- A: throughput bucket (10-minute slices of sim time) ----
            while (st >= (bucketIdx + 1) * bucketSteps)
            {
                var completedThisBucket = sim.ArrivedTotal - arrivedAtBucketStart;
                result.ArrivalsPerBucket.Add((bucketIdx * 10, (int)completedThisBucket));
                arrivedAtBucketStart = sim.ArrivedTotal;
                bucketIdx++;
            }

            // ---- B: overlap detection (crop-filtered Sample(), same math as the existing diagnostics) ----
            var byHandle = new Dictionary<VehicleHandle, LiveCitySim.CarAuthWitness>(witnesses.Count);
            foreach (var w in witnesses) byHandle[w.Handle] = w;

            for (var i = 0; i < cars.Count; i++)
            {
                for (var j = i + 1; j < cars.Count; j++)
                {
                    var pen = VehicleObb.Penetration(
                        cars[i].X, cars[i].Y, cars[i].AngleDeg, cars[i].Length, cars[i].Width,
                        cars[j].X, cars[j].Y, cars[j].AngleDeg, cars[j].Length, cars[j].Width);
                    if (pen <= NoiseThreshold) continue;

                    result.TotalOverlapEventsAll++;
                    byHandle.TryGetValue(cars[i].Handle, out var wa);
                    byHandle.TryGetValue(cars[j].Handle, out var wb);
                    var laneA = wa.LaneId ?? string.Empty;
                    var laneB = wb.LaneId ?? string.Empty;

                    var ev = new OverlapEvent(st, cars[i].Name, laneA, wa.Pos, wa.Speed, cars[j].Name, laneB, wb.Pos, wb.Speed, pen,
                        cars[i].X, cars[i].Y, cars[j].X, cars[j].Y);
                    log.WriteLine($"[{label}] OVERLAP step={st} A={cars[i].Name} lane={laneA} pos={wa.Pos:F2} spd={wa.Speed:F2} | "
                        + $"B={cars[j].Name} lane={laneB} pos={wb.Pos:F2} spd={wb.Speed:F2} | pen={pen:F3}");

                    if (!IsInternal(laneA) && !IsInternal(laneB) && laneA == laneB && laneA.Length > 0)
                    {
                        result.SameNormalLaneEvents.Add(ev);
                    }

                    if (laneA != laneB && laneA.Length > 0 && laneB.Length > 0)
                    {
                        var key = string.CompareOrdinal(laneA, laneB) <= 0 ? (laneA, laneB) : (laneB, laneA);
                        if (mergeLanePairs.Contains(key))
                        {
                            result.SameTargetMergeEvents.Add(ev);
                        }
                    }

                    if (pen >= FullyCoLocatedThreshold)
                    {
                        result.FullyCoLocatedEvents.Add(ev);
                    }
                }
            }
        }

        // Flush remaining active runs as "blocked forever" (still stopped at the horizon).
        foreach (var kv in activeRuns)
        {
            var (lane, start, last) = kv.Value;
            RecordCompletedRun(result, nameByHandle, kv.Key, lane, start, last, stillActiveAtEnd: true);
        }

        // Flush the final partial throughput bucket.
        {
            var completedThisBucket = sim.ArrivedTotal - arrivedAtBucketStart;
            result.ArrivalsPerBucket.Add((bucketIdx * 10, (int)completedThisBucket));
        }

        result.StepsRun = st;
        result.SimSecondsReached = st * cfg.Dt;
        result.ArrivedTotal = sim.ArrivedTotal;
        // High-realism pocket geometry (docs/CONSTRAINT-high-realism-artefact-ladder.md): the camera-driven
        // circle inside which the artefact ladder is binding. Captured from the SAME sim instance that
        // produced the events, so the classification cannot be applied against a stale/default pocket.
        result.PocketX = sim.HighRealismPocketX;
        result.PocketY = sim.HighRealismPocketY;
        result.PocketPromoteRadius = sim.HighRealismPromoteRadius;
        result.PocketDemoteRadius = sim.HighRealismDemoteRadius;

        // Teleport counters: no public accessor on LiveCitySim, so read the private Engine field via
        // reflection purely for this measurement (read-only; nothing is mutated).
        var engineField = typeof(LiveCitySim).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance);
        var engine = (Engine)engineField!.GetValue(sim)!;
        result.TeleportCount = engine.TeleportCount;
        result.TeleportJam = engine.TeleportCountJam;
        result.TeleportYield = engine.TeleportCountYield;
        result.TeleportWrongLane = engine.TeleportCountWrongLane;

        Environment.SetEnvironmentVariable("LIVECITY_CONTTURNFIX", null);
        Environment.SetEnvironmentVariable("LIVECITY_ISLEADERFIX", null);
        Environment.SetEnvironmentVariable("LIVECITY_INTERNALJUNCTIONFIX", null);

        return result;
    }

    private static void RecordCompletedRun(
        ConfigResult result, Dictionary<VehicleHandle, string> nameByHandle, VehicleHandle h,
        string lane, int start, int last, bool stillActiveAtEnd)
    {
        var len = last - start + 1;
        result.TotalStoppedRuns++;
        var bucket = RunLenBucket(len);
        result.RunLengthHistogram[bucket] = result.RunLengthHistogram.GetValueOrDefault(bucket) + 1;

        if (len > 300 || stillActiveAtEnd)
        {
            var name = nameByHandle.GetValueOrDefault(h, h.ToString());
            var rec = new RunRecord(name, lane, start, last, stillActiveAtEnd);
            if (stillActiveAtEnd) result.BlockedForever.Add(rec);
            if (len > 300) result.StoppedRunsOver300.Add(rec);
        }
    }

    [Fact]
    public void LongHorizon_GridlockAndInterpenetration_OffVsOn()
    {
        var repoRoot = RepoRoot();
        var netPath = Path.Combine(repoRoot, "scenarios", "_ped", "demo_city", "box", "net.xml");
        var mergeLanePairs = BuildMergeLanePairs(netPath);
        _out.WriteLine($"Merge-conflict lane pairs discovered in the net: {mergeLanePairs.Count}");

        const int oneHourSteps = 7200; // dt=0.5s -> 3600s / 0.5 = 7200 steps
        var wallBudget = TimeSpan.FromMinutes(15);

        var logPath = Path.Combine(ScratchDir, "longhorizon-trace.log");
        Directory.CreateDirectory(ScratchDir);

        ConfigResult off, on;
        using (var log = new StreamWriter(logPath, append: false))
        {
            log.AutoFlush = false;
            off = RunConfig("OFF", gatesOn: false, repoRoot, mergeLanePairs, oneHourSteps, wallBudget, log);
            on = RunConfig("ON", gatesOn: true, repoRoot, mergeLanePairs, oneHourSteps, wallBudget, log);
            log.Flush();
        }

        _out.WriteLine($"Full per-step overlap trace written to: {logPath}");
        _out.WriteLine("");

        void PrintReport(ConfigResult r)
        {
            _out.WriteLine(new string('=', 100));
            _out.WriteLine($"CONFIG: {r.Label} -- horizon reached: {r.StepsRun} steps ({r.SimSecondsReached:F0} sim-s = {r.SimSecondsReached / 60.0:F1} sim-min)");
            _out.WriteLine(new string('=', 100));

            _out.WriteLine($"ArrivedTotal (cumulative completions) = {r.ArrivedTotal}");
            _out.WriteLine("Arrivals per 10-min sim-time bucket:");
            foreach (var (startMin, completed) in r.ArrivalsPerBucket)
            {
                _out.WriteLine($"  [{startMin,4}-{startMin + 10,4} min) : {completed} completions");
            }

            _out.WriteLine("");
            _out.WriteLine("Moving vs stopped vehicle count (sampled every 50 steps = 25 sim-s):");
            foreach (var (step, moving, stopped, total) in r.MovingStoppedSamples)
            {
                _out.WriteLine($"  step={step,5} (t={step * 0.5,7:F0}s) moving={moving,4} stopped={stopped,4} total={total,4}");
            }

            _out.WriteLine("");
            _out.WriteLine("Stopped-run length distribution (all completed + still-active runs):");
            foreach (var kv in r.RunLengthHistogram.OrderBy(kv => kv.Key))
            {
                _out.WriteLine($"  {kv.Key,-10}: {kv.Value}");
            }
            _out.WriteLine($"Total stopped runs observed = {r.TotalStoppedRuns}");

            _out.WriteLine("");
            _out.WriteLine($"Runs stopped for >300 consecutive steps: {r.StoppedRunsOver300.Count}");
            foreach (var rec in r.StoppedRunsOver300.OrderByDescending(x => x.End - x.Start))
            {
                _out.WriteLine($"  {rec.Name,-14} lane={rec.Lane,-16} steps={rec.Start}-{rec.End} (len={rec.End - rec.Start + 1}){(rec.StillActiveAtEnd ? "  <-- STILL STOPPED AT HORIZON (blocked forever)" : "")}");
            }

            _out.WriteLine("");
            _out.WriteLine($"BLOCKED FOREVER (stopped from some step through the END of the run): {r.BlockedForever.Count}");
            foreach (var rec in r.BlockedForever.OrderBy(x => x.Start))
            {
                _out.WriteLine($"  {rec.Name,-14} lane={rec.Lane,-16} stopped since step={rec.Start} (t={rec.Start * 0.5:F0}s), {rec.End - rec.Start + 1} consecutive steps to horizon");
            }

            _out.WriteLine("");
            _out.WriteLine($"Teleports fired: total={r.TeleportCount} (jam={r.TeleportJam}, yield={r.TeleportYield}, wrongLane={r.TeleportWrongLane})");

            _out.WriteLine("");
            _out.WriteLine($"B. Overlap events -- total (all pairs, pen>{NoiseThreshold:F2}m) = {r.TotalOverlapEventsAll}");
            _out.WriteLine($"   SAME-NORMAL-LANE (both non-':' lane, same lane id): {r.SameNormalLaneEvents.Count} events"
                + (r.SameNormalLaneEvents.Count > 0 ? $", worst penetration {r.SameNormalLaneEvents.Max(e => e.Penetration):F3} m" : ""));
            foreach (var e in r.SameNormalLaneEvents.OrderByDescending(e => e.Penetration).Take(5))
            {
                _out.WriteLine($"     step={e.Step,5} A={e.NameA,-10} lane={e.LaneA,-14} pos={e.PosA,7:F2} spd={e.SpdA,6:F2} | B={e.NameB,-10} lane={e.LaneB,-14} pos={e.PosB,7:F2} spd={e.SpdB,6:F2} | pen={e.Penetration:F3} m");
            }

            _out.WriteLine("");
            _out.WriteLine($"   SAME-TARGET-MERGE (different lanes converging on a common downstream lane, MergeConflict): {r.SameTargetMergeEvents.Count} events"
                + (r.SameTargetMergeEvents.Count > 0 ? $", worst penetration {r.SameTargetMergeEvents.Max(e => e.Penetration):F3} m" : ""));
            foreach (var e in r.SameTargetMergeEvents.OrderByDescending(e => e.Penetration).Take(5))
            {
                _out.WriteLine($"     step={e.Step,5} A={e.NameA,-10} lane={e.LaneA,-14} pos={e.PosA,7:F2} spd={e.SpdA,6:F2} | B={e.NameB,-10} lane={e.LaneB,-14} pos={e.PosB,7:F2} spd={e.SpdB,6:F2} | pen={e.Penetration:F3} m");
            }

            _out.WriteLine("");
            _out.WriteLine($"   PEN >= {FullyCoLocatedThreshold:F2} m (essentially fully co-located): {r.FullyCoLocatedEvents.Count} events");
            foreach (var e in r.FullyCoLocatedEvents.OrderByDescending(e => e.Penetration).Take(10))
            {
                _out.WriteLine($"     step={e.Step,5} A={e.NameA,-10} lane={e.LaneA,-14} pos={e.PosA,7:F2} spd={e.SpdA,6:F2} | B={e.NameB,-10} lane={e.LaneB,-14} pos={e.PosB,7:F2} spd={e.SpdB,6:F2} | pen={e.Penetration:F3} m");
            }
            _out.WriteLine("");
        }

        PrintReport(off);
        PrintReport(on);

        _out.WriteLine(new string('=', 100));
        _out.WriteLine("DELTA TABLE (OFF -> ON)");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine($"Horizon reached (steps)               : {off.StepsRun} -> {on.StepsRun}");
        _out.WriteLine($"ArrivedTotal                            : {off.ArrivedTotal} -> {on.ArrivedTotal}");
        _out.WriteLine($"Total stopped runs                      : {off.TotalStoppedRuns} -> {on.TotalStoppedRuns}");
        _out.WriteLine($"Runs > 300 consecutive steps             : {off.StoppedRunsOver300.Count} -> {on.StoppedRunsOver300.Count}");
        _out.WriteLine($"BLOCKED FOREVER (stopped to horizon)     : {off.BlockedForever.Count} -> {on.BlockedForever.Count}");
        _out.WriteLine($"Teleports fired                          : {off.TeleportCount} -> {on.TeleportCount}");
        _out.WriteLine($"SAME-NORMAL-LANE overlap events           : {off.SameNormalLaneEvents.Count} -> {on.SameNormalLaneEvents.Count}");
        _out.WriteLine($"SAME-NORMAL-LANE worst penetration (m)    : {(off.SameNormalLaneEvents.Count > 0 ? off.SameNormalLaneEvents.Max(e => e.Penetration) : 0):F3} -> {(on.SameNormalLaneEvents.Count > 0 ? on.SameNormalLaneEvents.Max(e => e.Penetration) : 0):F3}");
        _out.WriteLine($"SAME-TARGET-MERGE overlap events           : {off.SameTargetMergeEvents.Count} -> {on.SameTargetMergeEvents.Count}");
        _out.WriteLine($"PEN >= {FullyCoLocatedThreshold:F2} m events                  : {off.FullyCoLocatedEvents.Count} -> {on.FullyCoLocatedEvents.Count}");
        _out.WriteLine($"Total overlap events (all pairs)          : {off.TotalOverlapEventsAll} -> {on.TotalOverlapEventsAll}");
        _out.WriteLine(new string('=', 100));

        // ------------------------------------------------------------------------------------------
        // LADDER VIOLATION QUANTIFICATION (docs/CONSTRAINT-high-realism-artefact-ladder.md).
        // The ladder is binding INSIDE the high-realism pocket, so every overlap class is split
        // in-zone vs out-of-zone. Rung 2 (pass-through) is permitted ONLY as recovery from an
        // already-crashed blocking pair; rung 3 (overlap during normal driving) is never permitted.
        // Since tiers 2 and 3 are the SAME GEOMETRY distinguished only by CAUSE, this report cannot
        // by itself separate them -- it reports the geometry and flags that limitation explicitly
        // rather than silently attributing intent.
        // ------------------------------------------------------------------------------------------
        void ReportViolations(ConfigResult r)
        {
            var cx = r.PocketX; var cy = r.PocketY; var rad = r.PocketPromoteRadius;
            _out.WriteLine("");
            _out.WriteLine(new string('-', 100));
            _out.WriteLine($"LADDER VIOLATIONS [{r.Label}] -- high-realism pocket centre=({cx:F1},{cy:F1}) "
                + $"promoteRadius={rad:F1} m (demote={r.PocketDemoteRadius:F1} m)");
            _out.WriteLine(new string('-', 100));

            void Line(string rung, string what, List<OverlapEvent> evs)
            {
                var inZone = evs.Count(e => e.InZone(cx, cy, rad));
                var worstIn = evs.Where(e => e.InZone(cx, cy, rad)).Select(e => e.Penetration).DefaultIfEmpty(0).Max();
                var worstAll = evs.Select(e => e.Penetration).DefaultIfEmpty(0).Max();
                _out.WriteLine($"  rung {rung} | {what,-46} total={evs.Count,6}  IN-ZONE={inZone,6}"
                    + $"  ({(evs.Count == 0 ? 0 : 100.0 * inZone / evs.Count),5:F1}%)"
                    + $"  worstAll={worstAll,6:F3} m  worstInZone={worstIn,6:F3} m");
            }

            Line("3", "same-lane overlap (normal driving)", r.SameNormalLaneEvents);
            Line("3", "same-target merge (2 dirs -> 1 exit lane)", r.SameTargetMergeEvents);
            Line("2/3", "fully co-located (pen >= vehicle width)", r.FullyCoLocatedEvents);
            _out.WriteLine($"  rung 4   | teleports                                      total={r.TeleportCount,6}"
                + "   (ANY non-zero is a violation -- teleport is never permitted in high realism)");
            _out.WriteLine($"  rung 5   | stopped runs > 300 consecutive steps           total={r.StoppedRunsOver300.Count,6}");
            _out.WriteLine($"  rung 5   | stopped from some step through to horizon      total={r.BlockedForever.Count,6}");
            _out.WriteLine("  NOTE: rung 2 vs rung 3 cannot be separated from geometry alone -- both are two cars");
            _out.WriteLine("        overlapping, distinguished only by WHETHER IT WAS A DELIBERATE UNBLOCK. This engine");
            _out.WriteLine("        has no unblock-by-overlap mechanism enabled (IgnoreJunctionBlockerSeconds = -1), so");
            _out.WriteLine("        every overlap counted here is rung 3 by construction: NOT an unblock, therefore");
            _out.WriteLine("        NOT permitted in the high-realism pocket.");
        }

        ReportViolations(off);
        ReportViolations(on);

        // ASSERTIONS. This began as a pure measurement probe and was promoted to a guard only after it
        // was shown to DISCRIMINATE: on the first full-hour run, OFF produced 161 stopped runs longer
        // than 300 steps and 156 vehicles stopped through to the horizon, while ON produced 0 and 59
        // respectively -- and ON's 59 all began in the final few hundred steps (7066-7169 of 7200, runs of
        // 31-134 steps), i.e. ordinary queueing at the cut-off rather than wedges. A test that cannot tell
        // the two configurations apart is not worth committing, so the thresholds below are anchored to
        // that measured separation rather than chosen to pass.
        //
        // The bar is deliberately on the GATES-ON configuration only: the OFF numbers are today's shipped
        // default and are NOT asserted, because the gates are default-OFF and this must not fail on an
        // unrelated change to the default path.

        // 1. THE BELIEVABILITY INVARIANT: no vehicle is wedged for a long unbroken stretch. Measured 0
        //    with the gates on, against 161 without. A generous ceiling, so ordinary heavy queueing does
        //    not trip it, while the 300+-step wedges that made the demo unbelievable do.
        Assert.True(on.StoppedRunsOver300.Count <= 20,
            $"gates ON left {on.StoppedRunsOver300.Count} stopped runs longer than 300 consecutive steps "
            + $"(measured 0 when this guard was written; gates OFF gave {off.StoppedRunsOver300.Count}). "
            + "A long unbroken stall is the failure the owner reported as 'blocked forever'. "
            + "See docs/NEED-livecity-teleport-safety-net-disabled.md and F3-SESSION-LOG.md.");

        // 2. THE CITY STILL FLOWS at the end of the hour -- throughput must not collapse toward zero.
        //    Measured 2709 arrivals with the gates on, 1295 without.
        Assert.True(on.ArrivedTotal >= 1500,
            $"gates ON completed only {on.ArrivedTotal} trips in {on.StepsRun} steps "
            + $"(measured 2709 when this guard was written; gates OFF gave {off.ArrivedTotal}). "
            + "A collapse here means the city gridlocked before the horizon.");

        // 3. NO same-target-merge interpenetration -- two vehicles arriving from different directions into
        //    the SAME downstream lane and ending up inside each other. Measured 4374 events with the gates
        //    off and EXACTLY 0 with them on, which is why this one is asserted at zero.
        Assert.True(on.SameTargetMergeEvents.Count == 0,
            $"gates ON produced {on.SameTargetMergeEvents.Count} same-target-merge overlap events "
            + $"(measured 0 when this guard was written; gates OFF gave {off.SameTargetMergeEvents.Count}). "
            + "This is the owner-reported 'cars from two directions meeting in the same exit lane'.");

        // 4. Fully co-located vehicles (penetration >= vehicle width) must stay rare. Measured 868 with the
        //    gates on against 83015 without -- a 99% reduction, but NOT zero, so this is a calibrated
        //    tripwire, not an invariant. The residue is the unfixed same-lane defect
        //    (docs/NEED-colocated-vehicles.md), which this guard deliberately does not claim to have fixed.
        Assert.True(on.FullyCoLocatedEvents.Count <= 2000,
            $"gates ON produced {on.FullyCoLocatedEvents.Count} essentially-fully-co-located overlap events "
            + $"(measured 868 when this guard was written; gates OFF gave {off.FullyCoLocatedEvents.Count}).");
    }
}
