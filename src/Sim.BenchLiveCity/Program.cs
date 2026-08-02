using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Sim.LiveCity;

// docs/LIVE-CITY-PERF-DESIGN.md (P0 in -TRACKER.md): a headless perf/GC/alloc MEASUREMENT INSTRUMENT
// for Sim.LiveCity.LiveCitySim -- the SAME coupled cars+peds host every viewer consumes (mirrors
// Sim.DensityDiff's driving pattern: LiveCityConfig.ForRepoRoot + a Step() loop; mirrors Sim.BenchCity's
// measurement conventions: Stopwatch, GC.GetTotalAllocatedBytes(precise:true) deltas,
// Process.PeakWorkingSet64, --profile printing Engine.PhaseTicks-shaped output).
//
// Adds ZERO behavioral risk: it never edits simulation logic, only drives LiveCitySim exactly as an
// existing caller would. NOT part of `dotnet test` (CLAUDE.md "two loops, kept strictly separate") --
// a CLI utility, like Sim.Bench/Sim.BenchCity/Sim.DensityDiff.
//
// Purpose (design §3): isolate car-only cost (--peds 0) from ped-only cost (--cars 0) from the coupled
// cost (both > 0), and -- because the reported problem is SPIKES, not a slow mean -- record every
// step's wall time and report the distribution (p50/p95/p99/max, count over 3x p50) plus
// GC.GetTotalPauseDuration() (the direct pause measurement; collection counts alone cannot show a
// spike). CLAUDE.md measurement rules 4 (label the demand model) and 10 (print every LIVECITY_*/
// SUMOSHARP_* gate value observed, since they are process-global) apply here just as they do to
// Sim.DensityDiff.
//
// Usage:
//   dotnet run -c Release --project src/Sim.BenchLiveCity -- [options]
//     --cars N          target concurrent cars (closed-loop cap; default 160)
//     --peds M          target concurrent peds (population cap; default 160)
//     --steps S         measured steps (default 400)
//     --warmup W        steps run BEFORE measurement starts, excluded from every statistic (default 40)
//     --hz H            sim Hz (LiveCityConfig.SimHz); default = config default (Dt=0.5 => 2 Hz)
//     --sweep "C:P,..." run several (cars,peds) configs in sequence, e.g. "0:0,160:0,0:1000,160:1000"
//                       (overrides --cars/--peds; one result per entry)
//     --repeats R       run each config R times (default 1) -- one printed block + one CSV row each
//     --csv PATH        append one row per (config, repeat) to PATH (header written once, if absent)
//     --profile         (LIVE-CITY-PERF-DESIGN.md P1) turn on LiveCitySim.ProfilePhases (+ the wrapped
//                       Engine's) for the MEASURED loop only, and print the phase breakdown (ms + % of
//                       measured wall, descending, with an explicit "unaccounted" remainder)
//     --hi-res-radius R (ADD1) call LiveCitySim.SetLcRealismZone at the pocket's default centre with
//                       radius R metres, resizing the high-realism (full-ORCA) ped pocket for the
//                       WHOLE run (set once, before warmup). R=0 (the default) leaves the ctor's
//                       static 70 m default pocket untouched.
//     --hi-res-centre x,y  override the pocket centre (default: LiveCitySim.HighRealismPocketX/Y);
//                       only meaningful together with --hi-res-radius > 0
//     --quiet           suppress the per-config human-readable block (CSV, if requested, still written)

// Locale trap (CLAUDE.md): this box is cs-CZ (comma decimal separator). Every number this tool prints
// or writes to CSV goes through CultureInfo.InvariantCulture explicitly -- never the ambient culture.
var inv = CultureInfo.InvariantCulture;

var cars = 160;
var peds = 160;
var steps = 400;
var warmup = 40;
double? hz = null;
string? sweep = null;
string? csvPath = null;
var repeats = 1;
var quiet = false;
var profile = false;
double hiResRadius = 0.0;
double? hiResCentreX = null;
double? hiResCentreY = null;
// T1 (docs/LIVE-CITY-PERF-SESSION-LOG.md REVISED PLAN, A8): big-net support. Mutually exclusive with
// each other; default (neither given) stays LiveCityConfig.ForRepoRoot, byte-identical to before.
string? sumocfgPath = null;
string? datasetDir = null;
// T2: prefill phase + spawn-rate overrides. 0 = fill phase disabled (byte-identical to before T2).
var fillSteps = 0;
int? carSpawnPerStepArg = null;
double? pedSpawnRateArg = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cars" when i + 1 < args.Length:
            cars = int.Parse(args[++i], inv);
            break;
        case "--peds" when i + 1 < args.Length:
            peds = int.Parse(args[++i], inv);
            break;
        case "--steps" when i + 1 < args.Length:
            steps = int.Parse(args[++i], inv);
            break;
        case "--warmup" when i + 1 < args.Length:
            warmup = int.Parse(args[++i], inv);
            break;
        case "--hz" when i + 1 < args.Length:
            hz = double.Parse(args[++i], inv);
            break;
        case "--sweep" when i + 1 < args.Length:
            sweep = args[++i];
            break;
        case "--csv" when i + 1 < args.Length:
            csvPath = args[++i];
            break;
        case "--repeats" when i + 1 < args.Length:
            repeats = int.Parse(args[++i], inv);
            break;
        case "--quiet":
            quiet = true;
            break;
        case "--profile":
            profile = true;
            break;
        case "--hi-res-radius" when i + 1 < args.Length:
            hiResRadius = double.Parse(args[++i], inv);
            break;
        case "--hi-res-centre" when i + 1 < args.Length:
        case "--hi-res-center" when i + 1 < args.Length:
        {
            var parts = args[++i].Split(',');
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, inv, out var cx)
                || !double.TryParse(parts[1], NumberStyles.Float, inv, out var cy))
            {
                Console.Error.WriteLine($"Sim.BenchLiveCity: bad --hi-res-centre value '{args[i]}' (want x,y).");
                return 2;
            }

            hiResCentreX = cx;
            hiResCentreY = cy;
            break;
        }

        case "--sumocfg" when i + 1 < args.Length:
            sumocfgPath = args[++i];
            break;
        case "--dataset" when i + 1 < args.Length:
            datasetDir = args[++i];
            break;
        case "--fill-steps" when i + 1 < args.Length:
            fillSteps = int.Parse(args[++i], inv);
            break;
        case "--car-spawn-per-step" when i + 1 < args.Length:
            carSpawnPerStepArg = int.Parse(args[++i], inv);
            break;
        case "--ped-spawn-rate" when i + 1 < args.Length:
            pedSpawnRateArg = double.Parse(args[++i], inv);
            break;

        default:
            Console.Error.WriteLine($"Sim.BenchLiveCity: unrecognized argument '{args[i]}'.");
            return 2;
    }
}

if (steps <= 0)
{
    Console.Error.WriteLine("Sim.BenchLiveCity: --steps must be > 0.");
    return 2;
}

if (warmup < 0)
{
    Console.Error.WriteLine("Sim.BenchLiveCity: --warmup must be >= 0.");
    return 2;
}

if (fillSteps < 0)
{
    Console.Error.WriteLine("Sim.BenchLiveCity: --fill-steps must be >= 0.");
    return 2;
}

if (sumocfgPath is not null && datasetDir is not null)
{
    Console.Error.WriteLine("Sim.BenchLiveCity: --sumocfg and --dataset are mutually exclusive.");
    return 2;
}

var configs = new List<(int Cars, int Peds)>();
if (sweep is not null)
{
    foreach (var part in sweep.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var kv = part.Split(':');
        if (kv.Length != 2
            || !int.TryParse(kv[0], NumberStyles.Integer, inv, out var c)
            || !int.TryParse(kv[1], NumberStyles.Integer, inv, out var p))
        {
            Console.Error.WriteLine($"Sim.BenchLiveCity: bad --sweep entry '{part}' (want C:P, e.g. \"160:1000\").");
            return 2;
        }

        configs.Add((c, p));
    }

    if (configs.Count == 0)
    {
        Console.Error.WriteLine("Sim.BenchLiveCity: --sweep produced zero configs.");
        return 2;
    }
}
else
{
    configs.Add((cars, peds));
}

var repoRoot = FindRepoRoot();

// T1: which scenario factory this run uses -- one choice for the whole invocation (every config in
// --sweep shares the same net). buildBaseConfig is passed into RunOne so each config still gets a
// FRESH LiveCityConfig (RunOne mutates CarTargetConcurrent/PedPopulationCap/etc. on it), but they all
// resolve to the SAME net.
LiveCityConfig BuildBaseConfig()
{
    if (sumocfgPath is not null)
    {
        return LiveCityConfig.ForSumocfg(sumocfgPath);
    }

    if (datasetDir is not null)
    {
        return LiveCityConfig.ForDataset(datasetDir);
    }

    return LiveCityConfig.ForRepoRoot(repoRoot);
}

var scenarioLabel = sumocfgPath is not null
    ? $"sumocfg:{sumocfgPath}"
    : datasetDir is not null
        ? $"dataset:{datasetDir}"
        : "ForRepoRoot (demo box)";

// T1 (docs/LIVE-CITY-PERF-SESSION-LOG.md REVISED PLAN, A8): report the scenario's physical size BEFORE
// any results. The original failed ladder's root cause (net far too small for the requested population)
// was invisible precisely because nothing printed the net's extent/lane count -- a config that could
// never physically host the request ran to completion and reported a fabricated REALTIME verdict.
// Parses the net directly (NetworkParser.Parse) rather than constructing a full LiveCitySim, which is
// cheaper and does not depend on cars/peds counts.
var probeCfg = BuildBaseConfig();
var netPath = probeCfg.ResolveNetPath();
var netModel = Sim.Ingest.NetworkParser.Parse(netPath);
var netLaneCount = netModel.LanesByHandle.Count;
var netMinX = double.MaxValue;
var netMinY = double.MaxValue;
var netMaxX = double.MinValue;
var netMaxY = double.MinValue;
foreach (var lane in netModel.LanesByHandle)
{
    foreach (var (x, y) in lane.Shape)
    {
        if (x < netMinX) netMinX = x;
        if (x > netMaxX) netMaxX = x;
        if (y < netMinY) netMinY = y;
        if (y > netMaxY) netMaxY = y;
    }
}

var netExtentX = netLaneCount > 0 ? netMaxX - netMinX : 0.0;
var netExtentY = netLaneCount > 0 ? netMaxY - netMinY : 0.0;

Console.WriteLine("== Scenario (T1) ==");
Console.WriteLine($"  source: {scenarioLabel}");
Console.WriteLine($"  net path: {netPath}");
Console.WriteLine(
    $"  lanes: {netLaneCount}   extent: x={netExtentX.ToString("F1", inv)} m  y={netExtentY.ToString("F1", inv)} m"
    + (netLaneCount > 0
        ? $"   bbox=[{netMinX.ToString("F1", inv)},{netMinY.ToString("F1", inv)}]..[{netMaxX.ToString("F1", inv)},{netMaxY.ToString("F1", inv)}]"
        : "   (no lanes parsed)"));
Console.WriteLine();

// CLAUDE.md rule 10: these gates are PROCESS-GLOBAL. Print every one this process observed, once,
// before any config runs (they cannot vary per-config -- they are read once by LiveCityConfig.
// WithEnvOverrides at construction time, same ambient value every time).
PrintEnvGates();

StreamWriter? csv = null;
if (csvPath is not null)
{
    var writeHeader = !File.Exists(csvPath);
    csv = new StreamWriter(csvPath, append: true);
    if (writeHeader)
    {
        csv.WriteLine(
            "cars_req,peds_req,cars_actual,peds_actual,hi_res_radius,ped_highpower_end,ped_highpower_max,"
            + "ped_lowpower_end,ped_lowpower_max,steps,warmup,dt,repeat,wall_s,steps_per_s,rtf,realtime,"
            + "mean_ms,p50_ms,p95_ms,p99_ms,max_ms,over_3xp50_count,gc0,gc1,gc2,alloc_mib,"
            + "alloc_bytes_per_step,gc_pause_ms,gc_pause_pct_wall,peak_ws_mib,arrived_total,"
            // T3: added columns.
            + "fill_ok,scenario,net_lanes,net_extent_x,net_extent_y,car_spawn_per_step,ped_spawn_rate,"
            + "fill_steps_used");
    }
}

foreach (var (carsReq, pedsReq) in configs)
{
    for (var rep = 0; rep < repeats; rep++)
    {
        RunOne(
            carsReq, pedsReq, steps, warmup, hz, BuildBaseConfig, quiet, profile, csv, rep, inv,
            hiResRadius, hiResCentreX, hiResCentreY,
            fillSteps, carSpawnPerStepArg, pedSpawnRateArg,
            scenarioLabel, netLaneCount, netExtentX, netExtentY);
    }
}

csv?.Dispose();
return 0;

// ---- helpers (local functions -- top-level statements file) ----

static void RunOne(
    int carsReq, int pedsReq, int steps, int warmup, double? hz, Func<LiveCityConfig> buildBaseConfig,
    bool quiet, bool profile, StreamWriter? csv, int repeatIndex, CultureInfo inv,
    double hiResRadius, double? hiResCentreX, double? hiResCentreY,
    int fillSteps, int? carSpawnPerStepArg, double? pedSpawnRateArg,
    string scenarioLabel, int netLaneCount, double netExtentX, double netExtentY)
{
    var cfg = buildBaseConfig();
    // Direct property sets -- no LIVECITY_CARS/LIVECITY_PEDS env var touched, nothing leaks (same
    // discipline as Sim.DensityDiff). --cars 0 / --peds 0 both simply mean "the spawn loop's own
    // `live < cap` guard is never true" -- Step() already handles a zero cap without special-casing.
    cfg.CarTargetConcurrent = carsReq < 0 ? 0 : carsReq;
    cfg.PedPopulationCap = pedsReq < 0 ? 0 : pedsReq;
    if (hz is { } h)
    {
        cfg.SimHz = h;
    }

    // T2: explicit overrides always win. Otherwise, when a fill phase is requested, auto-scale so the
    // requested population is actually reachable within --fill-steps instead of leaving the defaults
    // (LiveCityConfig.CarSpawnPerStep=5 / PedSpawnRatePerSecond=8.0) that produced the original
    // under-fill. Car formula: ceil(requested / fillSteps) floored at the existing default 5 (never
    // slower than today just because a caller asked for a fill phase). Ped formula: mirrors
    // LiveCityConfig.WithEnvOverrides' LIVECITY_PEDS scaling EXACTLY (LiveCityConfig.cs:427-431:
    // `8.0 * Math.Max(1.0, peds / 160.0)`), not an invented formula. Both are inert (config default
    // stays as-is) when --fill-steps is 0 (disabled) -- byte-identical to before T2 for every existing
    // caller.
    if (carSpawnPerStepArg is { } carSpawnOverride)
    {
        cfg.CarSpawnPerStep = carSpawnOverride;
    }
    else if (fillSteps > 0 && carsReq > 0)
    {
        cfg.CarSpawnPerStep = Math.Max(5, (int)Math.Ceiling(carsReq / (double)fillSteps));
    }

    if (pedSpawnRateArg is { } pedRateOverride)
    {
        cfg.PedSpawnRatePerSecond = pedRateOverride;
    }
    else if (fillSteps > 0 && pedsReq > 0)
    {
        cfg.PedSpawnRatePerSecond = 8.0 * Math.Max(1.0, pedsReq / 160.0);
    }

    using var sim = new LiveCitySim(cfg);

    // ADD1 (LIVE-CITY-PERF-DESIGN.md): resize the high-realism (full-ORCA) ped pocket for the WHOLE
    // run, set once before warmup so both warmup and the measured loop see the same zone. R<=0 (the
    // default) leaves the ctor's static 70 m pocket untouched -- SetLcRealismZone is only called when
    // the caller explicitly asked for a different radius.
    if (hiResRadius > 0.0)
    {
        var cx = hiResCentreX ?? sim.HighRealismPocketX;
        var cy = hiResCentreY ?? sim.HighRealismPocketY;
        sim.SetLcRealismZone(cx, cy, hiResRadius);
    }

    // T2: PREFILL phase -- runs BEFORE warmup and is excluded from every statistic below (the GC/alloc/
    // pause/step-time baselines are all captured strictly after this block, exactly like warmup).
    // Steps until BOTH achieved counts reach 95% of requested, or fillSteps elapses, whichever first.
    // fillSteps==0 (the default) means this loop runs zero iterations -- byte-identical to before T2.
    var fillStepsUsed = 0;
    if (fillSteps > 0)
    {
        while (fillStepsUsed < fillSteps)
        {
            var carsNow = sim.CurrentCars;
            var pedsNow = sim.CurrentPeds;
            var carsFillOk = carsReq <= 0 || carsNow >= 0.95 * carsReq;
            var pedsFillOk = pedsReq <= 0 || pedsNow >= 0.95 * pedsReq;
            if (carsFillOk && pedsFillOk)
            {
                break;
            }

            sim.Step();
            fillStepsUsed++;
        }
    }

    var fillCarsAchieved = sim.CurrentCars;
    var fillPedsAchieved = sim.CurrentPeds;
    var fillReachedTarget =
        (carsReq <= 0 || fillCarsAchieved >= 0.95 * carsReq)
        && (pedsReq <= 0 || fillPedsAchieved >= 0.95 * pedsReq);

    for (var s = 0; s < warmup; s++)
    {
        sim.Step();
    }

    // LIVE-CITY-PERF-DESIGN.md P1: turn phase profiling on only NOW (if requested), right before the
    // baselines below, so warmup's JIT/first-touch cost never pollutes the phase breakdown -- exactly
    // like the GC/alloc/pause baselines are read only after warmup.
    sim.ProfilePhases = profile;

    // ---- baselines, read AFTER warmup, immediately before the measured loop ----
    var process = Process.GetCurrentProcess();
    var gc0Before = GC.CollectionCount(0);
    var gc1Before = GC.CollectionCount(1);
    var gc2Before = GC.CollectionCount(2);
    var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
    var pauseBefore = GC.GetTotalPauseDuration();

    // Preallocated, fixed-size: no per-step allocation, no List growth during the measured loop.
    var stepMs = new double[steps];
    var toMs = 1000.0 / Stopwatch.Frequency;

    // ADD1: high/low-power ped high-water marks over the measured window (the pocket population
    // fluctuates as the crowd walks through it, so the end-of-run snapshot alone would understate the
    // peak workload). Plain int comparisons -- no allocation, negligible added per-step cost, same
    // class of cheap read as CurrentCars/CurrentPeds elsewhere in this loop.
    var maxHighPower = 0;
    var maxLowPower = 0;

    var sw = Stopwatch.StartNew();
    for (var s = 0; s < steps; s++)
    {
        var t0 = Stopwatch.GetTimestamp();
        sim.Step();
        var t1 = Stopwatch.GetTimestamp();
        stepMs[s] = (t1 - t0) * toMs;

        var hp = sim.PedHighPowerCount;
        if (hp > maxHighPower)
        {
            maxHighPower = hp;
        }

        var lp = sim.CurrentPeds - hp;
        if (lp > maxLowPower)
        {
            maxLowPower = lp;
        }
    }

    sw.Stop();

    var gc0After = GC.CollectionCount(0);
    var gc1After = GC.CollectionCount(1);
    var gc2After = GC.CollectionCount(2);
    var allocAfter = GC.GetTotalAllocatedBytes(precise: true);
    var pauseAfter = GC.GetTotalPauseDuration();
    process.Refresh();
    var peakWsBytes = process.PeakWorkingSet64;

    // Behavioral counters -- read AFTER the measured loop, for the "did the instrument perturb the sim"
    // determinism check (success condition 6) and for the achieved-vs-requested density check (design
    // §4: a config that never filled to its cap must be visible, not silently reported as if it hit it).
    var carsActual = sim.CurrentCars;
    var pedsActual = sim.CurrentPeds;
    var arrivedTotal = sim.ArrivedTotal;

    // ADD1: end-of-run high/low-power split (alongside the high-water marks captured during the loop
    // above). Ped cost is dominated by how many peds are high-power, not by the raw ped count, so
    // neither number alone is an interpretable workload label -- both are reported.
    var highPowerEnd = sim.PedHighPowerCount;
    var lowPowerEnd = pedsActual - highPowerEnd;
    if (highPowerEnd > maxHighPower)
    {
        maxHighPower = highPowerEnd;
    }

    if (lowPowerEnd > maxLowPower)
    {
        maxLowPower = lowPowerEnd;
    }

    var wallS = sw.Elapsed.TotalSeconds;
    var stepsPerSec = wallS > 0 ? steps / wallS : 0.0;
    var rtf = wallS > 0 ? (steps * cfg.Dt) / wallS : 0.0;

    // Percentiles: sort a copy-free in-place sort of stepMs. Post-measurement only (not in the timed
    // loop), so this does not count against the "no allocation during measurement" constraint above.
    var sorted = (double[])stepMs.Clone();
    Array.Sort(sorted);
    var mean = Mean(sorted);
    var p50 = Percentile(sorted, 0.50);
    var p95 = Percentile(sorted, 0.95);
    var p99 = Percentile(sorted, 0.99);
    var max = sorted.Length > 0 ? sorted[^1] : 0.0;
    var spikeThreshold = 3.0 * p50;
    var overCount = 0;
    foreach (var v in sorted)
    {
        if (v > spikeThreshold)
        {
            overCount++;
        }
    }

    // T3: the fill-adequacy gate. Uses the achieved counts AT THE END OF THE MEASURED WINDOW (the same
    // carsActual/pedsActual reported everywhere else) so the gate reflects what was actually measured,
    // not just what the (optional) prefill phase reached. A class with 0 requested trivially passes
    // (nothing to fill).
    var carsAtCap = carsReq <= 0 || carsActual >= 0.95 * carsReq;
    var pedsAtCap = pedsReq <= 0 || pedsActual >= 0.95 * pedsReq;
    var fillOk = carsAtCap && pedsAtCap;

    // ADD2/T3/T4: real-time verdict, against the EFFECTIVE dt's budget (cfg.Dt already reflects --hz).
    // The config is real-time only if BOTH the mean AND the tail (p99) step fit the budget -- smoothness
    // is a tail property, a mean that fits while p99 is 4x budget is not smooth, and a mean-only verdict
    // would hide exactly that. T3: a config that never reached 95% of its requested workload never ran
    // the target load at all, so its verdict is forced to "n/a" -- NEVER "yes" -- rather than reporting
    // real-time behaviour for a workload that was never actually measured.
    var budgetMs = cfg.Dt * 1000.0;
    var meanFitsBudget = mean <= budgetMs;
    var p99FitsBudget = p99 <= budgetMs;
    var realtime = !fillOk ? "n/a" : (meanFitsBudget && p99FitsBudget ? "yes" : "no");

    var gc0 = gc0After - gc0Before;
    var gc1 = gc1After - gc1Before;
    var gc2 = gc2After - gc2Before;
    var allocBytes = allocAfter - allocBefore;
    var allocMib = allocBytes / (1024.0 * 1024.0);
    var allocBytesPerStep = steps > 0 ? allocBytes / (double)steps : 0.0;
    var pauseMs = (pauseAfter - pauseBefore).TotalMilliseconds;
    var pausePctWall = wallS > 0 ? 100.0 * (pauseMs / 1000.0) / wallS : 0.0;
    var peakWsMib = peakWsBytes / (1024.0 * 1024.0);

    // The METRIC line is always printed (even under --quiet, which only suppresses the verbose
    // labelled block below) -- it is the one parseable line a script greps for.
    Console.WriteLine(
        $"METRIC cars_req={carsReq} peds_req={pedsReq} cars={carsActual} peds={pedsActual} "
        + $"hires_radius={hiResRadius.ToString("F1", inv)} ped_hi_end={highPowerEnd} ped_hi_max={maxHighPower} "
        + $"ped_lo_end={lowPowerEnd} ped_lo_max={maxLowPower} "
        + $"steps={steps} warmup={warmup} dt={cfg.Dt.ToString("R", inv)} repeat={repeatIndex} "
        + $"wall_s={wallS.ToString("F4", inv)} steps_per_s={stepsPerSec.ToString("F1", inv)} "
        + $"rtf={rtf.ToString("F2", inv)} realtime={realtime} fill_ok={(fillOk ? 1 : 0)} "
        + $"mean_ms={mean.ToString("F3", inv)} "
        + $"p50_ms={p50.ToString("F3", inv)} p95_ms={p95.ToString("F3", inv)} "
        + $"p99_ms={p99.ToString("F3", inv)} max_ms={max.ToString("F3", inv)} over3xp50={overCount} "
        + $"gc0={gc0} gc1={gc1} gc2={gc2} alloc_mib={allocMib.ToString("F2", inv)} "
        + $"alloc_bytes_per_step={allocBytesPerStep.ToString("F1", inv)} "
        + $"gc_pause_ms={pauseMs.ToString("F3", inv)} gc_pause_pct={pausePctWall.ToString("F3", inv)} "
        + $"peak_ws_mib={peakWsMib.ToString("F1", inv)} arrived={arrivedTotal}");

    // T3: loud, unconditional (not gated by --quiet) FILL-FAILED report -- the whole point of this task
    // is that an unfilled config must never be silently reported as if it had run the requested workload.
    if (!fillOk)
    {
        Console.WriteLine(
            $"  *** FILL-FAILED: requested cars={carsReq} peds={pedsReq} -- ACHIEVED cars={carsActual} "
            + $"({Pct(carsActual, carsReq, inv)}) peds={pedsActual} ({Pct(pedsActual, pedsReq, inv)}) "
            + "-- REALTIME verdict is n/a (never 'yes' for a workload that never actually ran) ***");
    }

    if (!quiet)
    {
        Console.WriteLine($"== Sim.BenchLiveCity: cars_req={carsReq} peds_req={pedsReq} repeat={repeatIndex} ==");
        Console.WriteLine($"  scenario: {scenarioLabel}   net lanes={netLaneCount}   "
            + $"extent x={netExtentX.ToString("F1", inv)} m y={netExtentY.ToString("F1", inv)} m");
        Console.WriteLine(
            $"  requested: cars={carsReq} peds={pedsReq}   ACTUAL at horizon: cars={carsActual} peds={pedsActual}"
            + (carsActual < carsReq || pedsActual < pedsReq
                ? "   <-- UNDER-FILLED, did not reach requested cap in this run"
                : string.Empty));
        if (fillSteps > 0)
        {
            Console.WriteLine(
                $"  PREFILL (T2): steps used={fillStepsUsed}/{fillSteps}   "
                + $"achieved cars={fillCarsAchieved}/{carsReq} ({Pct(fillCarsAchieved, carsReq, inv)})   "
                + $"peds={fillPedsAchieved}/{pedsReq} ({Pct(fillPedsAchieved, pedsReq, inv)})   "
                + (fillReachedTarget
                    ? "reached 95% target before fill-steps elapsed"
                    : "fill-steps EXHAUSTED before reaching 95% target"));
        }

        Console.WriteLine(
            $"  spawn rates (T2, effective): car_spawn_per_step={cfg.CarSpawnPerStep}   "
            + $"ped_spawn_rate={cfg.PedSpawnRatePerSecond.ToString("F2", inv)}/s");
        Console.WriteLine(
            $"  ped LOD split (ADD1): pocket radius={hiResRadius.ToString("F1", inv)} m "
            + $"({(hiResRadius > 0.0 ? "explicit" : "static 70 m default, unchanged")})   "
            + $"high-power end={highPowerEnd} max={maxHighPower}   low-power end={lowPowerEnd} max={maxLowPower}");
        Console.WriteLine(
            $"  demand model: CLOSED-LOOP (occupancy-capped; CLAUDE.md rule 4 -- not a capacity/discharge measurement)");
        Console.WriteLine($"  steps={steps} (measured) warmup={warmup} (excluded) dt={cfg.Dt.ToString("R", inv)}s "
            + $"({cfg.SimHz.ToString("F2", inv)} Hz)   step budget={budgetMs.ToString("F1", inv)} ms");
        Console.WriteLine(
            $"  wall={wallS.ToString("F3", inv)} s   steps/s={stepsPerSec.ToString("F1", inv)}   "
            + $"RTF(sim/wall)={rtf.ToString("F2", inv)}x   REALTIME: {realtime} "
            + (fillOk ? "(mean<=budget AND p99<=budget)" : "(n/a -- FILL-FAILED, see above)"));
        Console.WriteLine(
            $"  per-step wall time (ms): mean={mean.ToString("F3", inv)} p50={p50.ToString("F3", inv)} "
            + $"p95={p95.ToString("F3", inv)} p99={p99.ToString("F3", inv)} max={max.ToString("F3", inv)} "
            + $"  steps>3x p50 ({spikeThreshold.ToString("F3", inv)} ms): {overCount}/{steps}");
        Console.WriteLine(
            $"  GC: gen0={gc0} gen1={gc1} gen2={gc2}   alloc={allocMib.ToString("F2", inv)} MiB total "
            + $"({allocBytesPerStep.ToString("F1", inv)} bytes/step)");
        Console.WriteLine(
            $"  GC pause (GetTotalPauseDuration): {pauseMs.ToString("F3", inv)} ms total "
            + $"= {pausePctWall.ToString("F3", inv)}% of wall");
        Console.WriteLine($"  peak working set: {peakWsMib.ToString("F1", inv)} MiB");
        Console.WriteLine($"  arrived (behavioral counter): {arrivedTotal}");

        if (profile)
        {
            PrintPhaseBreakdown(sim, wallS, allocBytes, inv);
        }

        Console.WriteLine();
    }

    if (csv is not null)
    {
        csv.WriteLine(string.Join(",", new[]
        {
            carsReq.ToString(inv),
            pedsReq.ToString(inv),
            carsActual.ToString(inv),
            pedsActual.ToString(inv),
            hiResRadius.ToString("R", inv),
            highPowerEnd.ToString(inv),
            maxHighPower.ToString(inv),
            lowPowerEnd.ToString(inv),
            maxLowPower.ToString(inv),
            steps.ToString(inv),
            warmup.ToString(inv),
            cfg.Dt.ToString("R", inv),
            repeatIndex.ToString(inv),
            wallS.ToString("R", inv),
            stepsPerSec.ToString("R", inv),
            rtf.ToString("R", inv),
            realtime,
            mean.ToString("R", inv),
            p50.ToString("R", inv),
            p95.ToString("R", inv),
            p99.ToString("R", inv),
            max.ToString("R", inv),
            overCount.ToString(inv),
            gc0.ToString(inv),
            gc1.ToString(inv),
            gc2.ToString(inv),
            allocMib.ToString("R", inv),
            allocBytesPerStep.ToString("R", inv),
            pauseMs.ToString("R", inv),
            pausePctWall.ToString("R", inv),
            peakWsMib.ToString("R", inv),
            arrivedTotal.ToString(inv),
            // T3: added columns.
            fillOk ? "1" : "0",
            CsvEscape(scenarioLabel),
            netLaneCount.ToString(inv),
            netExtentX.ToString("R", inv),
            netExtentY.ToString("R", inv),
            cfg.CarSpawnPerStep.ToString(inv),
            cfg.PedSpawnRatePerSecond.ToString("R", inv),
            fillStepsUsed.ToString(inv),
        }));
        csv.Flush();
    }
}

// T3: "achieved/requested" as a percentage string, InvariantCulture. 0 requested is reported as "n/a"
// rather than a divide-by-zero-shaped 0.0%/Infinity% -- a class that was not requested at all trivially
// satisfies the fill gate (see the carsFillOk/pedsFillOk checks above), so a percentage would mislead.
static string Pct(int achieved, int requested, CultureInfo inv) =>
    requested <= 0 ? "n/a, 0 requested" : (100.0 * achieved / requested).ToString("F1", inv) + "%";

// T1: minimal CSV field escaping for the free-form scenario label (a --sumocfg/--dataset path could in
// principle contain a comma or a double quote) -- RFC4180-shaped, applied only when needed so the
// common case (no comma/quote) stays a plain unquoted field like every other column here.
static string CsvEscape(string s) =>
    s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

// Nearest-rank percentile over an ALREADY-SORTED (ascending) array.
static double Percentile(double[] sorted, double p)
{
    if (sorted.Length == 0)
    {
        return 0.0;
    }

    var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
    if (idx < 0)
    {
        idx = 0;
    }

    if (idx >= sorted.Length)
    {
        idx = sorted.Length - 1;
    }

    return sorted[idx];
}

static double Mean(double[] values)
{
    if (values.Length == 0)
    {
        return 0.0;
    }

    var sum = 0.0;
    foreach (var v in values)
    {
        sum += v;
    }

    return sum / values.Length;
}

// docs/LIVE-CITY-PERF-DESIGN.md P1: print LiveCitySim's own top-level Step() phases, sorted descending
// by BYTES (B3 -- the previously 95%-unexplained allocation residual is the more valuable axis; ms
// stays alongside it), with an explicit "unaccounted" remainder (measured wall minus the sum of
// phases; run-total allocation minus the sum of phases) so missing instrumentation is visible rather
// than hidden. The wrapped Engine's own phases (LiveCitySim.PhaseTicks/PhaseBytes merge them in,
// prefixed "engine.") are a SUB-DIVISION of the single "engineStep" entry above (the one call into
// _engine.Step()), not siblings of it -- summing both would double-count engineStep's wall time/bytes.
// They are printed separately, as a nested breakdown OF engineStep.
//
// B2: same treatment for PedLodManager.Step's/PedDemand.Step's sub-phases, merged in prefixed "ped."
// -- a breakdown OF the single "pedDemandStep" top-level entry, printed with its own explicit
// remainder line (sum of ped.* vs pedDemandStep itself) so a gap in the ped-side instrumentation is
// visible rather than silently absorbed into "unaccounted" above.
//
// B3: allocBytesTotal is the run's OWN GC.GetTotalAllocatedBytes(precise: true) delta (the number
// already printed as "alloc_mib"/"alloc_bytes_per_step"), used as the allocation reconciliation
// target -- independent of the per-phase profiler's own GC.GetTotalAllocatedBytes(precise: false)
// deltas (Engine/LiveCitySim/PedLodManager/PedDemand's TotalAllocatedBytes), so the two can disagree
// slightly (precise vs approximate, and any allocation on threads/paths outside the phase timers) --
// that residual disagreement IS the allocation "unaccounted" line, exactly mirroring the wall-time one.
static void PrintPhaseBreakdown(LiveCitySim sim, double wallS, long allocBytesTotal, CultureInfo inv)
{
    var toMs = 1000.0 / Stopwatch.Frequency;
    var wallMs = wallS * 1000.0;
    const double bytesToMib = 1.0 / (1024.0 * 1024.0);
    var allocMibTotal = allocBytesTotal * bytesToMib;
    Console.WriteLine("  phase breakdown (--profile; measured loop only, warmup excluded; sorted by allocated bytes):");
    if (sim.PhaseTicks.Count == 0)
    {
        Console.WriteLine("    (no phases recorded)");
        return;
    }

    var phaseBytes = sim.PhaseBytes;

    var own = new List<(string Key, long Ticks, long Bytes)>();
    var enginePhases = new List<(string Key, long Ticks, long Bytes)>();
    var pedPhases = new List<(string Key, long Ticks, long Bytes)>();
    foreach (var kv in sim.PhaseTicks)
    {
        phaseBytes.TryGetValue(kv.Key, out var bytes);
        var entry = (kv.Key, kv.Value, bytes);
        if (kv.Key.StartsWith("engine.", StringComparison.Ordinal)) enginePhases.Add(entry);
        else if (kv.Key.StartsWith("ped.", StringComparison.Ordinal)) pedPhases.Add(entry);
        else own.Add(entry);
    }

    static void PrintLine(string key, double ms, double pctWall, double mib, double pctAlloc, CultureInfo inv)
        => Console.WriteLine(
            $"    {key,-28} {ms.ToString("F1", inv),10} ms {pctWall.ToString("F1", inv),6}% wall   "
            + $"{mib.ToString("F2", inv),10} MiB {pctAlloc.ToString("F1", inv),6}% alloc");

    var sumMs = 0.0;
    var sumMib = 0.0;
    var pedDemandStepMs = 0.0;
    foreach (var (key, ticks, bytes) in own.OrderByDescending(e => e.Bytes))
    {
        var ms = ticks * toMs;
        var mib = bytes * bytesToMib;
        sumMs += ms;
        sumMib += mib;
        if (key == "pedDemandStep") pedDemandStepMs = ms;
        var pctWall = wallMs > 0 ? 100.0 * ms / wallMs : 0.0;
        var pctAlloc = allocMibTotal > 0 ? 100.0 * mib / allocMibTotal : 0.0;
        PrintLine(key, ms, pctWall, mib, pctAlloc, inv);
    }

    var unaccountedMs = wallMs - sumMs;
    var unaccountedMsPct = wallMs > 0 ? 100.0 * unaccountedMs / wallMs : 0.0;
    var unaccountedMib = allocMibTotal - sumMib;
    var unaccountedMibPct = allocMibTotal > 0 ? 100.0 * unaccountedMib / allocMibTotal : 0.0;
    PrintLine("unaccounted", unaccountedMs, unaccountedMsPct, unaccountedMib, unaccountedMibPct, inv);
    Console.WriteLine(
        "      (unaccounted ms = measured wall - sum of TOP-LEVEL phase ms; "
        + "unaccounted MiB = run's total precise alloc_mib - sum of TOP-LEVEL phase bytes)");

    if (enginePhases.Count > 0)
    {
        Console.WriteLine("  engine sub-phases (breakdown OF engineStep above, not additional wall time/bytes):");
        foreach (var (key, ticks, bytes) in enginePhases.OrderByDescending(e => e.Bytes))
        {
            var ms = ticks * toMs;
            var mib = bytes * bytesToMib;
            var pctWall = wallMs > 0 ? 100.0 * ms / wallMs : 0.0;
            var pctAlloc = allocMibTotal > 0 ? 100.0 * mib / allocMibTotal : 0.0;
            PrintLine(key, ms, pctWall, mib, pctAlloc, inv);
        }
    }

    if (pedPhases.Count > 0)
    {
        Console.WriteLine("  ped sub-phases (breakdown OF pedDemandStep above, not additional wall time/bytes):");
        var pedSumMs = 0.0;
        var pedSumMib = 0.0;
        foreach (var (key, ticks, bytes) in pedPhases.OrderByDescending(e => e.Bytes))
        {
            var ms = ticks * toMs;
            var mib = bytes * bytesToMib;
            pedSumMs += ms;
            pedSumMib += mib;
            var pctWall = wallMs > 0 ? 100.0 * ms / wallMs : 0.0;
            var pctAlloc = allocMibTotal > 0 ? 100.0 * mib / allocMibTotal : 0.0;
            PrintLine(key, ms, pctWall, mib, pctAlloc, inv);
        }

        var pedRemainderMs = pedDemandStepMs - pedSumMs;
        var pedRemainderMsPct = wallMs > 0 ? 100.0 * pedRemainderMs / wallMs : 0.0;
        // pedDemandStep's OWN bytes (the top-level entry, i.e. own.Bytes for "pedDemandStep") is the
        // reconciliation target for ped.* bytes -- fetch it back out of `own`.
        var pedDemandStepMib = 0.0;
        foreach (var (key, _, bytes) in own)
        {
            if (key == "pedDemandStep") pedDemandStepMib = bytes * bytesToMib;
        }

        var pedRemainderMib = pedDemandStepMib - pedSumMib;
        var pedRemainderMibPct = allocMibTotal > 0 ? 100.0 * pedRemainderMib / allocMibTotal : 0.0;
        PrintLine("remainder", pedRemainderMs, pedRemainderMsPct, pedRemainderMib, pedRemainderMibPct, inv);
        Console.WriteLine(
            "      (remainder ms/MiB = pedDemandStep's own ms/bytes - sum of the ped.* sub-phases above)");
    }
}

// CLAUDE.md measurement rule 10: LIVECITY_*/SUMOSHARP_* gates are PROCESS-GLOBAL; an inherited shell
// value is indistinguishable from a measured one, so print every one this process observed. The
// curated list is every name found by grepping src/ for these prefixes at the time this was written;
// the fallback loop below also catches anything added later that this list has not been updated for,
// so the run is never silently incomplete.
static void PrintEnvGates()
{
    var known = new[]
    {
        "LIVECITY_CARS", "LIVECITY_PEDS", "LIVECITY_PEDYIELD", "LIVECITY_LCMIN", "LIVECITY_MERGEGAP",
        "LIVECITY_MERGEDEFER", "LIVECITY_YIELD", "LIVECITY_YIELDTIMEOUT", "LIVECITY_TELEPORT",
        "LIVECITY_WRONGLANE", "LIVECITY_DRIVETHROUGH", "LIVECITY_COOP", "LIVECITY_HZ",
        "LIVECITY_KEEPCLEARHELD", "LIVECITY_MINORARRIVALSPEED", "LIVECITY_HELDSWERVE", "LIVECITY_F",
        "LIVECITY_CONTTURNFIX", "LIVECITY_ISLEADERFIX", "LIVECITY_INTERNALJUNCTIONFIX",
        "LIVECITY_INTERNALJUNCTIONENTRYORDER", "LIVECITY_INSERTIONFOLLOWERGAP",
        "LIVECITY_COLOCATIONSYMMETRYBREAK", "LIVECITY_LANECHANGEARBITRATION", "LIVECITY_SEQDESYNC",
        "LIVECITY_LCLOG", "LIVECITY_DUMP", "LIVECITY_DUMPROUTES", "LIVECITY_WITNESS",
        // Entry 46 refresh (the 3D session hit the staleness warning): the F3/junction-realism and
        // rerouting gate family, all live in LiveCitySim's ctor and all behavioural.
        "LIVECITY_F3OCCUPANCY", "LIVECITY_IGNOREBLOCKER", "LIVECITY_TRACEVEH",
        "LIVECITY_REROUTE", "LIVECITY_REROUTE_PERIOD", "LIVECITY_REROUTE_PROB",
        "LIVECITY_URGENTFOLLOW", "LIVECITY_RINGBREAK", "LIVECITY_PARTIALVEH",
        "SUMOSHARP_CONTTURNFIX", "SUMOSHARP_ISLEADERFIX", "SUMOSHARP_INTERNALJUNCTIONFIX",
        "SUMOSHARP_PHYSOCCUPANCY", "SUMOSHARP_IGNOREBLOCKER", "SUMOSHARP_TRACEVEH", "SUMOSHARP_URGENTFOLLOW",
    };

    var seen = new HashSet<string>(known, StringComparer.Ordinal);
    Console.WriteLine("== LIVECITY_*/SUMOSHARP_* env gates observed (process-global; CLAUDE.md rule 10) ==");
    foreach (var name in known)
    {
        var v = Environment.GetEnvironmentVariable(name);
        Console.WriteLine($"  {name} = {v ?? "<unset>"}");
    }

    foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
    {
        var key = (string)de.Key;
        if (seen.Contains(key))
        {
            continue;
        }

        if (key.StartsWith("LIVECITY_", StringComparison.Ordinal) || key.StartsWith("SUMOSHARP_", StringComparison.Ordinal))
        {
            Console.WriteLine($"  {key} = {de.Value} (NOT in the curated list above -- update Sim.BenchLiveCity/Program.cs)");
        }
    }

    Console.WriteLine();
}

// Walk up from the running assembly's directory to the directory containing Traffic.sln -- same
// pattern Sim.DensityDiff/Program.cs uses. CLAUDE.md prime directive 1: never hardcode an absolute VM
// path -- resolve the repo root, don't assume it.
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
