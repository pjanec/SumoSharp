using System.Globalization;
using Sim.Core;
using Sim.Harness;
using Sim.Ingest;

// VB-0 (VIZ_BENCH_TASKS.md Phase 0): run the engine on a scenario directory and dump a
// SUMO-schema FCD file via the D9 export seam (FcdWriterObserver). This is the "wire the
// engine to emit FCD" path VIZ_SPEC.md asks for: Sim.Viz and the benchmark consume the emitted
// engine.fcd.xml through the exact same FcdParser they already use for golden.fcd.xml.
//
// It is NOT part of `dotnet test` -- a deliberate CLI utility, like Sim.Bench.
//
// Usage:
//   dotnet run --project src/Sim.Run -- <scenarioDir> [--steps N] [--fcd-out PATH] [--warmup N]
//                                        [--summary-output PATH] [--statistic-output PATH]
//
// Defaults: steps = round((end-begin)/step-length) from the scenario's *.sumocfg (matches how
// the parity tests pick their step count); fcd-out = <scenarioDir>/engine.fcd.xml; warmup = 0
// (today's behavior -- the recorded run starts from the scenario's fresh t=Begin state, exactly
// as before this flag existed).
//
// --warmup N (additive, CLI-only; does not touch the engine/parity path): calls the existing
// Engine.WarmUp(N) BEFORE the recorded Run, advancing the simulation N steps with no FCD export
// (see Engine.cs's WarmUp doc comment -- W1). The recorded FCD then starts from that already-
// populated state instead of ramping up from empty, e.g. for a demo that wants frame 0 to already
// show a busy network. Omitting the flag (or passing 0) reproduces prior behavior byte-for-byte.
//
// P0-D (docs/HIGH-DENSITY-P0-DESIGN.md "P0-D"): --summary-output PATH / --statistic-output PATH
// are ADDITIVE and absent by default -- when omitted, no SummaryWriterObserver is registered and
// no statistic file is written, so every pre-P0-D invocation of this CLI is unaffected. When
// given, --summary-output registers a SummaryWriterObserver alongside the FCD writer (both read
// the SAME per-frame export snapshot, see that class's own comment) and --statistic-output writes
// engine.TeleportCount (0 in phase 1) via StatisticWriter once the run completes.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine(
                "usage: Sim.Run <scenarioDir> [--steps N] [--fcd-out PATH] [--warmup N]\n"
                + "                            [--summary-output PATH] [--statistic-output PATH]\n"
                + "                            [--binder-log PATH] [--parity|--coordinated-lc]");
            return args.Length == 0 ? 2 : 0;
        }

        var scenarioDir = args[0];
        if (!Directory.Exists(scenarioDir))
        {
            Console.Error.WriteLine($"error: scenario directory not found: {scenarioDir}");
            return 2;
        }

        int? stepsOverride = null;
        string? fcdOut = null;
        var warmupSteps = 0;
        string? summaryOut = null;
        string? statisticOut = null;
        string? binderLog = null;
        // P2G-2: the dense lane-change model (aggressive multi-lane overtaking/merging) is the PRODUCT
        // DEFAULT -- believable, and it flows the realistic organic net about as well as parity. `--parity`
        // selects the deterministic SUMO-anchor mode (byte-identical to the committed goldens, the mode the
        // offline `dotnet test` suite runs). (The cooperative informFollower layer was retired -- its only
        // benefit was a synthetic saturated-grid rescue that the P2-G traffic-light junction fixes now
        // provide at the engine level, and it degraded organic flow + cost perf.)
        var coordinatedLc = true;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--steps" when i + 1 < args.Length:
                    stepsOverride = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--fcd-out" when i + 1 < args.Length:
                    fcdOut = args[++i];
                    break;
                case "--warmup" when i + 1 < args.Length:
                    warmupSteps = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--summary-output" when i + 1 < args.Length:
                    summaryOut = args[++i];
                    break;
                case "--statistic-output" when i + 1 < args.Length:
                    statisticOut = args[++i];
                    break;
                // Additive diagnostic (docs/JUNCTION-REALISM-SESSION-JOURNAL.md Entry 3): per-vehicle,
                // per-step binding constraint as CSV. Absent => no observer registered => no behaviour
                // change, exactly like --summary-output above.
                case "--binder-log" when i + 1 < args.Length:
                    binderLog = args[++i];
                    break;
                case "--parity":
                    coordinatedLc = false; // deterministic SUMO-anchor mode (matches the committed goldens)
                    break;
                case "--coordinated-lc":
                    coordinatedLc = true; // explicit (already the default: aggressive dense LC)
                    break;
                default:
                    Console.Error.WriteLine($"error: unrecognized argument: {args[i]}");
                    return 2;
            }
        }

        var cfg = SingleFile(scenarioDir, "*.sumocfg");
        if (cfg is null)
        {
            Console.Error.WriteLine(
                "error: scenario dir must contain exactly one *.sumocfg");
            return 2;
        }

        var config = ScenarioConfigParser.Parse(cfg);
        var steps = stepsOverride ?? (int)Math.Round((config.End - config.Begin) / config.StepLength);
        fcdOut ??= Path.Combine(scenarioDir, "engine.fcd.xml");

        var engine = new Engine { CoordinatedLaneChange = coordinatedLc };
        // JUNCTION-APPROACH-ARM: an A/B toggle for Engine.InternalJunctionApproachArm, so the arm's
        // before/after can be measured through ONE binary and one code path (CLAUDE.md measurement
        // discipline #8/#13 -- cross-instrument comparisons are invalid, and rebuilding with a flipped
        // default would be exactly that).
        //
        // Semantics are `EnvGate(name, engineDefault)` -- UNSET means the ENGINE DEFAULT, not `false`.
        // docs/ENV-GATES.md "Adding a gate" mandates exactly this and forbids the bare `== "1"` form,
        // because `== "1"` makes an unset variable silently force the gate OFF and override the engine
        // default. That is not hypothetical: it is what `SumoShim` does to the three junction gates
        // today, so every drop-in shim invocation runs with gates the engine and the goldens have ON
        // (already documented in ENV-GATES.md and tracked in TASKS-TODO.md -- not a new finding, and
        // deliberately not fixed here).
        engine.InternalJunctionApproachArm = EnvGate("SUMOSHARP_APPROACHARM", engine.InternalJunctionApproachArm);
        // The CONVERSE half, for the paired experiment (docs/JUNCTION-REALISM-TRACE-FINDINGS.md §8):
        // the approach arm stops ego entering into an approaching foe's path, while this one stops ego
        // driving through a foe ALREADY STOPPED inside the junction. Measured separately they look like
        // two unrelated gates; the §8 measurement says they are two halves of one mechanism, which is
        // why this toggle exists next to the one above -- both must be settable in BOTH arms of the same
        // A/B, per CLAUDE.md measurement discipline #10 (an inherited value is indistinguishable from a
        // measured one).
        engine.JunctionPhysicalOccupancyGate = EnvGate("SUMOSHARP_PHYSOCC", engine.JunctionPhysicalOccupancyGate);
        // P0-A: a cfg with an <input> section (net-file/route-files) is SUMO-faithful and self-
        // describing -- drive it off the new 1-arg LoadScenario(cfgPath) overload, which resolves
        // <input> paths against the cfg's own directory. Otherwise (every pre-P0-A scenario dir)
        // fall back to the original glob-based single-file discovery for back-compat.
        if (config.RouteFiles.Count > 0)
        {
            engine.LoadScenario(cfg);
        }
        else
        {
            var net = SingleFile(scenarioDir, "*.net.xml");
            var rou = SingleFile(scenarioDir, "*.rou.xml");
            if (net is null || rou is null)
            {
                Console.Error.WriteLine(
                    $"error: scenario dir must contain exactly one each of *.net.xml, *.rou.xml " +
                    $"(found net={net}, rou={rou})");
                return 2;
            }

            engine.LoadScenario(net, rou, cfg);
        }

        if (warmupSteps > 0)
        {
            engine.WarmUp(warmupSteps);
        }

        // P0-D: --summary-output is additive -- summaryWriter stays null (no observer registered,
        // no behavior change) unless the flag was passed.
        using (var writer = new FcdWriterObserver(fcdOut))
        using (var summaryWriter = summaryOut is not null ? new SummaryWriterObserver(summaryOut) : null)
        using (var binderWriter = binderLog is not null ? new BinderLogObserver(binderLog) : null)
        {
            engine.AddExportObserver(writer);
            if (summaryWriter is not null)
            {
                engine.AddExportObserver(summaryWriter);
            }

            if (binderWriter is not null)
            {
                engine.AddExportObserver(binderWriter);
            }

            engine.Run(steps);
        }

        if (statisticOut is not null)
        {
            StatisticWriter.Write(statisticOut, engine.TeleportCount, teleportsJam: engine.TeleportCountJam);
        }

        Console.WriteLine(
            $"wrote {fcdOut}  ({steps} steps, [{config.Begin}, {config.End}] @ {config.StepLength}s" +
            (warmupSteps > 0 ? $", warmup={warmupSteps} steps" : string.Empty) + ")");
        if (summaryOut is not null)
        {
            Console.WriteLine($"wrote {summaryOut}");
        }

        if (statisticOut is not null)
        {
            Console.WriteLine($"wrote {statisticOut}");
        }

        return 0;
    }

    // A scenario dir has exactly one of each input; more than one is ambiguous, so refuse.
    private static string? SingleFile(string dir, string pattern)
    {
        var matches = Directory.GetFiles(dir, pattern);
        return matches.Length == 1 ? matches[0] : null;
    }

    // docs/ENV-GATES.md "Adding a gate" rule 1: read a gate as (unset => engine default), never as a
    // bare `== "1"`, which silently forces an unset gate OFF regardless of what the engine ships.
    private static bool EnvGate(string name, bool engineDefault)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? engineDefault : v == "1";
    }

}
