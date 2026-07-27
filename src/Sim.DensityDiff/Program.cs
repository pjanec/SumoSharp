using System;
using System.Globalization;
using System.Linq;
using System.IO;
using Sim.LiveCity;

// docs/DENSITY-DIFF-HARNESS-DESIGN.md §2/§5, -TASKS.md B1: runs the demo (Sim.LiveCity.LiveCitySim,
// the SAME coupled cars+peds host every viewer consumes) for --steps steps at --cars concurrent-car
// density with the B1 demand recorder ON, writing the exact procedural demand as a SUMO .rou.xml to
// --out. A driver only -- it does not run SUMO itself, does not compute the gap-decomposition report
// (Stage C), and is never a `dotnet test` dependency (design §5). Usage:
//   dotnet run --project src/Sim.DensityDiff -- --cars 480 --steps 200 --out /path/to/demand.rou.xml

var cars = 160;
var steps = 200;
string? outPath = null;
// A3 (design §1b): when set, run OPEN-LOOP at this many vehicles per simulated second, ignoring the
// occupancy cap. Required for any discharge/capacity measurement -- closed-loop inflow self-throttles.
double? inflow = null;
string? seriesPath = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cars" when i + 1 < args.Length:
            cars = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--steps" when i + 1 < args.Length:
            steps = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--out" when i + 1 < args.Length:
            outPath = args[++i];
            break;
        case "--inflow" when i + 1 < args.Length:
            inflow = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--series" when i + 1 < args.Length:
            seriesPath = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Sim.DensityDiff: unrecognized argument '{args[i]}'.");
            return 2;
    }
}

if (outPath is null)
{
    Console.Error.WriteLine("Sim.DensityDiff: --out FILE is required.");
    Console.Error.WriteLine("usage: --cars N --steps N --out FILE [--inflow VEH_PER_SEC] [--series CSV]");
    Console.Error.WriteLine("  --inflow  OPEN-LOOP mode: fixed inflow, occupancy cap IGNORED (design §1b).");
    Console.Error.WriteLine("            Required for discharge/capacity work; --cars is ignored when set.");
    Console.Error.WriteLine("  --series  write step,resident,arrived CSV (the runaway-vs-steady-state series).");
    return 2;
}

var repoRoot = FindRepoRoot();
var cfg = LiveCityConfig.ForRepoRoot(repoRoot);
cfg.CarTargetConcurrent = cars; // direct property set -- no LIVECITY_CARS env var touched, nothing leaks.
cfg.CarInflowVehPerSec = inflow; // null => unchanged closed-loop demo behaviour.

// G1 A/B: the driver sets the gate EXPLICITLY (never leaving it to the ambient shell), because these
// LIVECITY_* vars are process-global and an inherited value would silently contaminate the column this run
// claims to measure -- the failure that once produced a 392-vs-1295 "OFF" baseline.
var g1 = Environment.GetEnvironmentVariable("LIVECITY_KEEPCLEARHELD") == "1";
Environment.SetEnvironmentVariable("LIVECITY_KEEPCLEARHELD", g1 ? "1" : "0");
var minorArr = Environment.GetEnvironmentVariable("LIVECITY_MINORARRIVALSPEED") == "1";
Environment.SetEnvironmentVariable("LIVECITY_MINORARRIVALSPEED", minorArr ? "1" : "0");

var demandModel = inflow is null ? "closed-loop" : "open-loop";
Console.WriteLine(
    $"Sim.DensityDiff: dataset='{cfg.DatasetDir}' steps={steps} out='{outPath}'");
// Design §1b's standing rule: EVERY metric is labelled with the demand model that produced it. A capacity
// claim from closed-loop demand is invalid however carefully the rest was measured.
Console.WriteLine($"  G1 keepClear held-propagation = {(g1 ? "ON" : "OFF")}   minor-approach arrival speed = {(minorArr ? "ON" : "OFF")}");
Console.WriteLine(inflow is null
    ? $"  demand model = CLOSED-LOOP, cap={cars} concurrent  <-- CANNOT measure discharge (inflow self-throttles)"
    : $"  demand model = OPEN-LOOP, inflow={inflow.Value:F3} veh/s, occupancy cap IGNORED");

using var sink = new Sim.DensityDiff.DemandRouteFileSink(outPath);
using var sim = new LiveCitySim(cfg, recordDemandSink: sink);

// The demo's OWN insertion log (an existing LiveCitySim diagnostic, independent of the B1 recorder
// tee above) -- used purely as the SC3 ground truth: "vehicle count and depart times in the file
// match the demo's own insertion log exactly". Both are populated at the SAME call site
// (LiveCitySim.Step()'s spawn block), so any divergence between them is a bug in the recorder, not
// a measurement artifact.
sim.SpawnLog = new System.Collections.Generic.List<(double Depart, string From, string To)>();

// A3/SC1+SC3: resident-count-over-time. This series IS the discharge measurement: a level that holds is
// steady state (inflow == drain), a level that climbs to the horizon is runaway (inflow > drain) and means
// the network cannot sustain this inflow. Sampled every 60 simulated seconds.
var series = new System.Collections.Generic.List<(double Sec, int Resident, long Arrived, int Halting)>();
// "Moving but slow" is measured against EACH CAR'S OWN LANE LIMIT, never a single global reference.
// The first version of this probe used a flat 13.89 m/s, which was simply wrong for this net: its car
// lanes run 8.33 / 11.11 / 13.89 / 16.67 m/s, so on a 30 km/h lane a car driving the limit correctly
// looked "slow" and `freeFlow` dominated the histogram as a pure artefact. Same error class as the two
// other mislabels on this branch -- comparing a population against the wrong yardstick.
const double MovingSlowFraction = 0.8;
var movingSlow = 0;
var slowBinders = new System.Collections.Generic.Dictionary<byte, int>();
var sampleEvery = Math.Max(1, (int)Math.Round(60.0 / cfg.Dt));

for (var s = 0; s < steps; s++)
{
    sim.Step();
    if ((s + 1) % sampleEvery == 0)
    {
        // Also record how many of the resident cars are HALTING. The halting FRACTION is directly
        // comparable to SUMO's own `halting`/`running` in summary-output, and it splits the deficit
        // hypothesis space in half without any new engine surface: if our extra time in system is spent
        // STOPPED, the cause is queueing/yielding; if it is spent rolling slowly, the cause is
        // car-following / acceleration / speed limits. Threshold matches SUMO's SUMO_const_haltingSpeed.
        var halting = 0;
        foreach (var w in sim.WitnessAuthoritative())
        {
            if (w.Speed < 0.1) { halting++; continue; }

            // WHICH ARM IS COSTING US SPEED? The halting fraction came out identical to SUMO's (33.3% vs
            // 33.7%), so the extra ~37% of time in system is NOT spent stopped -- our cars ROLL slower
            // (~8.0 m/s while moving against SUMO's ~11.0). So the question is no longer "what stops our
            // cars" but "what holds a MOVING car below speed", and `BindingConstraint` answers it directly.
            // Counted only for cars that are moving yet well below the lane's allowed speed, so free-flow
            // cars (correctly bound by arm 3) do not swamp the histogram.
            if (!sim.Network.LanesById.TryGetValue(w.LaneId ?? string.Empty, out var wLane))
            {
                continue;
            }

            if (w.Speed < MovingSlowFraction * wLane.Speed)
            {
                movingSlow++;
                slowBinders.TryGetValue(w.Binder, out var n);
                slowBinders[w.Binder] = n + 1;
            }
        }
        series.Add(((s + 1) * cfg.Dt, sim.CurrentCars, sim.ArrivedTotal, halting));
    }
}

var recordedCount = sink.VehicleCount;
var recordedDeparts = sink.RecordedDeparts;
sink.Dispose(); // close the XML root element before anything reads the file.

var insertionLog = sim.SpawnLog!;
var logCount = insertionLog.Count;
var departsMatch = recordedCount == logCount;
if (departsMatch)
{
    // Both logs are populated at the SAME call site in the SAME order (see the ctor-log comment
    // above), so a positional, exact (not tolerance-fuzzed) compare is the real SC3 check, not a
    // count-only proxy.
    for (var k = 0; k < logCount; k++)
    {
        if (insertionLog[k].Depart != recordedDeparts[k])
        {
            departsMatch = false;
            Console.Error.WriteLine(
                $"SC3 MISMATCH at index {k}: demo-log depart={insertionLog[k].Depart} recorded depart={recordedDeparts[k]}");
        }
    }
}

// B1/SC4: the three fidelity caveats design §2 requires reported as NUMBERS, never silently omitted.
var currentCars = sim.CurrentCars;
var arrived = sim.ArrivedTotal;
// Proxy only -- Engine keeps NO cumulative counter for per-step insertion-gap refusals
// (InsertionFollowerGapCheck's `return false` path is retried next step, untallied) and
// DiscardedDepartureCount measures a DIFFERENT, unrelated thing (max-depart-delay eviction, which
// this config never enables, so it would misleadingly read 0). This proxy is everything spawned
// that is neither currently active nor arrived -- i.e. still Pending at the run's end.
var pendingProxy = recordedCount - currentCars - arrived;

Console.WriteLine();
Console.WriteLine("== B1 demand recorder report ==");
Console.WriteLine($"SC3 vehicle count: recorded={recordedCount} demo-insertion-log={logCount} match={recordedCount == logCount}");
Console.WriteLine($"SC3 depart times : positionally identical (same call site) = {departsMatch}");
Console.WriteLine();
Console.WriteLine("SC4 fidelity caveats (design §2):");
Console.WriteLine("  reroutes performed        : NOT MEASURED -- Engine exposes no cumulative counter for GAP-1 " +
    "dead-lane reroute or WrongLaneRerouteAtApproach (only a private per-vehicle cap dict); " +
    $"WrongLaneRerouteAtApproach={cfg.WrongLaneRerouteAtApproach}, DeadLaneDriveThrough={cfg.DeadLaneDriveThrough} " +
    "are ON in this config, so reroutes are plausible but uncounted.");
Console.WriteLine($"  insertions refused (proxy): {pendingProxy} (= recorded {recordedCount} - active {currentCars} - arrived {arrived}; " +
    "a 'still Pending at run end' proxy, NOT a per-step refusal-event tally -- none exists).");
Console.WriteLine($"  pedestrians present       : current={sim.CurrentPeds} peak={sim.PeakPeds}");
// ---- A3/SC3: steady state, or runaway? ----
// The test compares the mean resident count over the LAST quarter of the run against the quarter before it.
// Steady state means the level has stopped rising: the later window must not exceed the earlier one by more
// than SteadyStateTolerance. A simple "is the final value near the max" test would be fooled by a level that
// climbs steadily to the horizon, which is precisely the shape being detected -- hence two windows.
//
// The threshold is deliberately generous (5%): the question is "does this level RUN AWAY", not "is it
// perfectly flat". A queue growing 258 -> 2623 over an hour clears 5% by an enormous margin, and calling a
// genuinely-saturating network "runaway" over a few percent of warm-up drift would be the worse error.
const double SteadyStateTolerance = 0.05;
string verdict;
double earlierMean = 0, laterMean = 0;
if (series.Count >= 4)
{
    var q = series.Count / 4;
    var earlier = series.Skip(series.Count - 2 * q).Take(q).ToList();
    var later = series.Skip(series.Count - q).ToList();
    earlierMean = earlier.Average(x => (double)x.Resident);
    laterMean = later.Average(x => (double)x.Resident);
    var growth = earlierMean <= 0 ? 0 : (laterMean - earlierMean) / earlierMean;
    verdict = growth > SteadyStateTolerance
        ? $"RUNAWAY (resident count still climbing: +{growth * 100:F1}% between the last two quarters)"
        : $"STEADY STATE (plateau ~{laterMean:F0} cars; last-two-quarter drift {growth * 100:+0.0;-0.0}%)";
}
else
{
    verdict = "INDETERMINATE (run too short -- need at least 4 samples, i.e. 4 minutes of sim time)";
}

Console.WriteLine();
Console.WriteLine($"== A3 discharge measurement [{demandModel}] ==");
if (inflow is null)
{
    Console.WriteLine("  ⚠ CLOSED-LOOP: this verdict is MEANINGLESS as a capacity statement. Occupancy is");
    Console.WriteLine("    capped, so it reaches 'steady state' by construction regardless of the drain.");
    Console.WriteLine("    Re-run with --inflow to measure discharge (design §1b).");
}
Console.WriteLine($"  offered inflow      : {(inflow is null ? "self-throttled by our own drain" : inflow.Value.ToString("F3", CultureInfo.InvariantCulture) + " veh/s")}");
Console.WriteLine($"  resident at horizon : {sim.CurrentCars}");
Console.WriteLine($"  arrived total       : {sim.ArrivedTotal}");
Console.WriteLine($"  mean resident, prev quarter -> last quarter: {earlierMean:F0} -> {laterMean:F0}");
Console.WriteLine($"  VERDICT             : {verdict}");

// Halting fraction over everything after the first quarter (skip warm-up), the same window the SUMO side
// uses, so the two numbers are comparable as-is.
if (series.Count >= 4)
{
    var tail = series.Skip(series.Count / 4).Where(x => x.Resident > 0).ToList();
    if (tail.Count > 0)
    {
        var mr = tail.Average(x => (double)x.Resident);
        var mh = tail.Average(x => (double)x.Halting);
        Console.WriteLine($"  mean resident={mr:F0} mean halting={mh:F0}"
            + $" -> HALTING FRACTION {100 * mh / mr:F1}%   (SUMO emits the same pair in summary-output)");
        Console.WriteLine($"  MOVING-BUT-SLOW samples: {movingSlow} (moving, yet under {MovingSlowFraction:P0} of THEIR OWN lane's limit)");
        var names = new System.Collections.Generic.Dictionary<byte, string>
        {
            [0] = "none", [1] = "leaderFollow", [2] = "crossJxnLeader", [3] = "freeFlow",
            [4] = "successiveLaneSpeed", [5] = "deadLaneMerge", [6] = "stopLine", [7] = "redLight",
            [8] = "railSignal", [9] = "railCrossing", [10] = "junctionYield", [11] = "keepClear",
            [12] = "obstacle", [13] = "crowd", [14] = "internalJxnAdmission", [15] = "colocationBreak",
        };
        foreach (var kv in slowBinders.OrderByDescending(k => k.Value).Take(8))
        {
            var pct = movingSlow == 0 ? 0.0 : 100.0 * kv.Value / movingSlow;
            Console.WriteLine($"    binder {kv.Key,2} {names.GetValueOrDefault(kv.Key, "?"),-22} {kv.Value,9}  {pct,5:F1}%");
        }
    }
}

if (seriesPath is not null)
{
    using var sw = new StreamWriter(seriesPath);
    sw.WriteLine("# demand_model=" + demandModel
        + (inflow is null ? $" cap={cars}" : $" inflow_veh_per_s={inflow.Value.ToString("R", CultureInfo.InvariantCulture)}"));
    sw.WriteLine("sim_seconds,resident_cars,arrived_total,halting_cars");
    foreach (var (sec, res, arr, halt) in series)
    {
        sw.WriteLine($"{sec.ToString("F1", CultureInfo.InvariantCulture)},{res},{arr},{halt}");
    }
    Console.WriteLine($"  series -> '{seriesPath}'");
}

Console.WriteLine();
Console.WriteLine($"wrote '{outPath}'");

return 0;

// Walk up from the running assembly's directory to the directory containing Traffic.sln -- the same
// pattern DemoCatalog.RepoRoot()/Sim.Host.App's FindRepoRoot use. CLAUDE.md prime directive 1: never
// hardcode an absolute VM path -- resolve the repo root, don't assume it.
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName
        ?? throw new InvalidOperationException("Could not locate repo root (Traffic.sln not found above assembly).");
}
