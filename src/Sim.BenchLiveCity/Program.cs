using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
            "cars_req,peds_req,cars_actual,peds_actual,steps,warmup,dt,repeat,wall_s,steps_per_s,rtf,"
            + "mean_ms,p50_ms,p95_ms,p99_ms,max_ms,over_3xp50_count,gc0,gc1,gc2,alloc_mib,"
            + "alloc_bytes_per_step,gc_pause_ms,gc_pause_pct_wall,peak_ws_mib,arrived_total");
    }
}

foreach (var (carsReq, pedsReq) in configs)
{
    for (var rep = 0; rep < repeats; rep++)
    {
        RunOne(carsReq, pedsReq, steps, warmup, hz, repoRoot, quiet, profile, csv, rep, inv);
    }
}

csv?.Dispose();
return 0;

// ---- helpers (local functions -- top-level statements file) ----

static void RunOne(
    int carsReq, int pedsReq, int steps, int warmup, double? hz, string repoRoot,
    bool quiet, bool profile, StreamWriter? csv, int repeatIndex, CultureInfo inv)
{
    var cfg = LiveCityConfig.ForRepoRoot(repoRoot);
    // Direct property sets -- no LIVECITY_CARS/LIVECITY_PEDS env var touched, nothing leaks (same
    // discipline as Sim.DensityDiff). --cars 0 / --peds 0 both simply mean "the spawn loop's own
    // `live < cap` guard is never true" -- Step() already handles a zero cap without special-casing.
    cfg.CarTargetConcurrent = carsReq < 0 ? 0 : carsReq;
    cfg.PedPopulationCap = pedsReq < 0 ? 0 : pedsReq;
    if (hz is { } h)
    {
        cfg.SimHz = h;
    }

    using var sim = new LiveCitySim(cfg);

    for (var s = 0; s < warmup; s++)
    {
        sim.Step();
    }

    // TASK 2 (LIVE-CITY-PERF-DESIGN.md P1) wires `--profile` here: `sim.ProfilePhases = profile;` right
    // before the baselines below, once LiveCitySim grows the ProfilePhases/PhaseTicks scaffolding. Task
    // 1 (this commit) never references that API -- LiveCitySim does not have it yet.
    if (profile)
    {
        Console.Error.WriteLine("Sim.BenchLiveCity: --profile is not yet wired (LIVE-CITY-PERF-DESIGN.md P1 pending).");
    }

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

    var sw = Stopwatch.StartNew();
    for (var s = 0; s < steps; s++)
    {
        var t0 = Stopwatch.GetTimestamp();
        sim.Step();
        var t1 = Stopwatch.GetTimestamp();
        stepMs[s] = (t1 - t0) * toMs;
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
        + $"steps={steps} warmup={warmup} dt={cfg.Dt.ToString("R", inv)} repeat={repeatIndex} "
        + $"wall_s={wallS.ToString("F4", inv)} steps_per_s={stepsPerSec.ToString("F1", inv)} "
        + $"rtf={rtf.ToString("F2", inv)} mean_ms={mean.ToString("F3", inv)} "
        + $"p50_ms={p50.ToString("F3", inv)} p95_ms={p95.ToString("F3", inv)} "
        + $"p99_ms={p99.ToString("F3", inv)} max_ms={max.ToString("F3", inv)} over3xp50={overCount} "
        + $"gc0={gc0} gc1={gc1} gc2={gc2} alloc_mib={allocMib.ToString("F2", inv)} "
        + $"alloc_bytes_per_step={allocBytesPerStep.ToString("F1", inv)} "
        + $"gc_pause_ms={pauseMs.ToString("F3", inv)} gc_pause_pct={pausePctWall.ToString("F3", inv)} "
        + $"peak_ws_mib={peakWsMib.ToString("F1", inv)} arrived={arrivedTotal}");

    if (!quiet)
    {
        Console.WriteLine($"== Sim.BenchLiveCity: cars_req={carsReq} peds_req={pedsReq} repeat={repeatIndex} ==");
        Console.WriteLine(
            $"  requested: cars={carsReq} peds={pedsReq}   ACTUAL at horizon: cars={carsActual} peds={pedsActual}"
            + (carsActual < carsReq || pedsActual < pedsReq
                ? "   <-- UNDER-FILLED, did not reach requested cap in this run"
                : string.Empty));
        Console.WriteLine(
            $"  demand model: CLOSED-LOOP (occupancy-capped; CLAUDE.md rule 4 -- not a capacity/discharge measurement)");
        Console.WriteLine($"  steps={steps} (measured) warmup={warmup} (excluded) dt={cfg.Dt.ToString("R", inv)}s "
            + $"({cfg.SimHz.ToString("F2", inv)} Hz)");
        Console.WriteLine(
            $"  wall={wallS.ToString("F3", inv)} s   steps/s={stepsPerSec.ToString("F1", inv)}   "
            + $"RTF(sim/wall)={rtf.ToString("F2", inv)}x");
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
            steps.ToString(inv),
            warmup.ToString(inv),
            cfg.Dt.ToString("R", inv),
            repeatIndex.ToString(inv),
            wallS.ToString("R", inv),
            stepsPerSec.ToString("R", inv),
            rtf.ToString("R", inv),
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
        }));
        csv.Flush();
    }
}

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
        "SUMOSHARP_CONTTURNFIX", "SUMOSHARP_ISLEADERFIX", "SUMOSHARP_INTERNALJUNCTIONFIX",
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
