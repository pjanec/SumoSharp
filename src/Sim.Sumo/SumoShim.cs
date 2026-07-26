using System.Globalization;
using Sim.Core;
using Sim.Harness;
using Sim.Ingest;

namespace Sim.Sumo;

// GAP-1 (docs/SUMOSHARP-SERVE-PATH-DROP-IN.md §1, docs/SERVE-PATH-PLAN.md): the `sumo`-compatible CLI
// shim -- the drop-in binary the SumoData serve/replay pipeline invokes (via `SUMO_BINARY`) instead
// of vanilla `sumo`. It parses the vanilla flag shape and drives the SAME engine wiring Sim.Run
// already proves out (LoadScenario(cfg) multi-file <input> + Fcd/Summary/Statistic writers); no
// engine change lives here. This class is DELIBERATELY separate from Sim.Run so the sumo-compatible
// contract stays clean and out of Sim.Run's dev/viz flags (--warmup/--fcd-out/--parity/...).
//
// The core is a pure `Run(args, stdout, stderr) -> exit code` so the parity test can drive it in-
// process (no shelling out) and compare the produced FCD against the committed golden -- proving the
// CLI path drives the engine identically. Program.Main is a one-line delegate over Console.Out/Error.
//
// The exact invocation shapes SumoData shells out (all list-form subprocess, so a differently-NAMED
// binary is fine as long as the flags match):
//   sumo -c <cfg> --summary-output S.xml --statistic-output T.xml --end <N> --no-step-log true
//   sumo -c <cfg> --tripinfo-output TI.xml [--summary-output S.xml --statistic-output T.xml] --end <N> --no-step-log true
//   sumo -c <cfg> --fcd-output F.xml --end <N> --no-step-log true
//
// Supported flags (SUMO spellings):
//   -c/--configuration <cfg>   the .sumocfg (its <input> resolves net/route/additional relative to it)
//   -b/--begin <t>             sim begin time  (optional; default = cfg <begin>)
//   -e/--end <t>               sim end   time  (optional; default = cfg <end>) -- run length is
//                              round((end-begin)/step-length) steps, exactly how the parity tests
//                              pick their step count. Vanilla `--end` overrides the cfg's <end>.
//   --fcd-output <path>        SUMO-schema FCD             (FcdWriterObserver)
//   --summary-output <path>    per-step summary            (SummaryWriterObserver)
//   --statistic-output <path>  <teleports total=.. jam=..> (StatisticWriter)
//   --tripinfo-output <path>   SUMO-schema <tripinfo> per ARRIVED vehicle (id/depart/arrival/
//                              arrivalLane/arrivalPos/arrivalSpeed/duration/routeLength/
//                              waitingTime/timeLoss -- GAP-2, docs/SUMOSHARP-SERVE-PATH-DROP-
//                              IN.md §2). Sourced from engine.CompletedTrips (Engine.cs's
//                              CaptureCompletedTrips), written via Sim.Harness.TripInfoWriter.
//   --no-step-log [bool]       accepted and ignored (we never print a per-step log)
//   --max-parallelism <N>      caps the engine's worker-thread degree (Engine.MaxParallelism). N<=0 =>
//                              all cores (the default, unchanged); N>=1 => at most N threads. A PERF
//                              knob only -- output is byte-identical regardless of N (the engine's
//                              plan/willPass/emit loops are order-independent), which is what lets the
//                              SumoData `--workers`×threads sweep trust its timing. Reuses the exact
//                              flag name the Sim.BenchCity/Crowd/PedLod bench tools already use, and
//                              (like every flag here) is parsed order-independently so it works before
//                              or after `-c` -- e.g. a `SUMO_BINARY="dotnet sumosharp.dll
//                              --max-parallelism 4"` prefix rides ahead of SumoData's `-c <cfg> ...`.
//   --ignore-junction-blocker <TIME>
//                              sets Engine.IgnoreJunctionBlockerSeconds (SUMO's own option,
//                              MSFrame.cpp:370-371 / consumed at MSLink.cpp:1601). Same "Processing"
//                              category as time-to-teleport, so the equivalent .sumocfg element is
//                              <processing><ignore-junction-blocker value="TIME"/></processing>
//                              (ScenarioConfig.IgnoreJunctionBlockerSeconds / ScenarioConfigParser).
//                              This CLI flag wins over the cfg element when both are given, mirroring
//                              how --end overrides the cfg's <end>. Default -1 (never ignore, SUMO's
//                              own default; any value < 0 means the same thing, per MSFrame.cpp:1043-
//                              1044 mapping it to SUMOTime::max()) -- byte-identical for every scenario
//                              that specifies neither.
//   SUMOSHARP_CONTTURNFIX=1 (env var, NOT a --flag)
//                              sets Engine.ContTurnInsideJunctionGate. Not a SUMO option (see the
//                              Engine property's own header comment); mirrors LiveCitySim.cs's
//                              identical LIVECITY_CONTTURNFIX env var. Unset/anything-but-"1" => false,
//                              the Engine default, so this is inert for every existing invocation.
// Any OTHER flag is TOLERATED (a warning to stderr, not an abort) so minor extra flags SumoData
// passes never break the run. Both `--flag value` and `--flag=value` forms are accepted.
public static class SumoShim
{
    // Thrown internally by value parsing; caught in Run so a bad numeric never escapes as a raw
    // FormatException (and so the testable Run never calls Environment.Exit).
    private sealed class CliError : Exception
    {
        public CliError(string message) : base(message) { }
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "-?")
        {
            stderr.WriteLine(
                "usage: sumosharp -c <cfg> [--begin t] [--end t] [--fcd-output F] " +
                "[--summary-output S] [--statistic-output T] [--tripinfo-output TI] [--no-step-log] " +
                "[--max-parallelism N]");
            stderr.WriteLine(
                "  --max-parallelism N   cap engine worker threads (N<=0 = all cores, the default); " +
                "perf knob only, output is identical for any N.");
            return args.Length == 0 ? 1 : 0;
        }

        string? cfgPath = null;
        double? beginOverride = null;
        double? endOverride = null;
        string? fcdOut = null;
        string? summaryOut = null;
        string? statisticOut = null;
        string? tripinfoOut = null;
        // Perf knob (see class header): -1 == engine default (all cores). Set from --max-parallelism.
        var maxParallelism = -1;
        // null == not given on the CLI => fall back to the cfg's <processing><ignore-junction-blocker>
        // (ScenarioConfig.IgnoreJunctionBlockerSeconds, itself defaulting to -1 = never ignore).
        double? ignoreJunctionBlockerOverride = null;

        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                var (flag, inlineValue) = SplitInline(args[i]);

                // Read the value for a value-taking flag: the inline `--flag=value` form if present,
                // else the next token. Throws a CliError when neither is available.
                string TakeValue()
                {
                    if (inlineValue is not null)
                    {
                        return inlineValue;
                    }

                    if (i + 1 < args.Length)
                    {
                        return args[++i];
                    }

                    throw new CliError($"{flag} requires a value");
                }

                switch (flag)
                {
                    case "-c":
                    case "--configuration":
                    case "--config-file":
                        cfgPath = TakeValue();
                        break;
                    case "-b":
                    case "--begin":
                        beginOverride = ParseTime(TakeValue(), flag);
                        break;
                    case "-e":
                    case "--end":
                        endOverride = ParseTime(TakeValue(), flag);
                        break;
                    case "--fcd-output":
                        fcdOut = TakeValue();
                        break;
                    case "--summary-output":
                        summaryOut = TakeValue();
                        break;
                    case "--statistic-output":
                        statisticOut = TakeValue();
                        break;
                    case "--tripinfo-output":
                        tripinfoOut = TakeValue();
                        break;
                    case "--max-parallelism":
                        // Perf-only: caps Engine.MaxParallelism. Parsed here (order-independently, like
                        // every flag) so it works before OR after -c, letting SumoData carry it as a
                        // SUMO_BINARY prefix with no SumoData-side change. N<=0 keeps the all-cores
                        // default; the Engine setter maps any non-positive value back to -1.
                        maxParallelism = ParseInt(TakeValue(), flag);
                        break;
                    case "--ignore-junction-blocker":
                        // SUMO's own option (see class header); ParseTime accepts a leading '-' so
                        // "-1" (and any other negative TIME) round-trips through NumberStyles.Float.
                        ignoreJunctionBlockerOverride = ParseTime(TakeValue(), flag);
                        break;
                    case "--no-step-log":
                        // Accept and ignore. SUMO passes `--no-step-log true`; the value (only when it
                        // is a bare boolean token, not the next real flag) is consumed and dropped.
                        if (inlineValue is null && i + 1 < args.Length && IsBooleanLiteral(args[i + 1]))
                        {
                            i++;
                        }

                        break;
                    default:
                        // Tolerate unknown flags: warn, don't abort (the doc's explicit requirement). If
                        // the unknown flag is followed by a non-flag token, treat that as its value and
                        // skip it too, so a `--some-extra value` pair does not desync the parser.
                        stderr.WriteLine($"warning: ignoring unrecognized argument '{args[i]}'");
                        if (inlineValue is null && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        {
                            i++;
                        }

                        break;
                }
            }
        }
        catch (CliError ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return 1;
        }

        if (cfgPath is null)
        {
            stderr.WriteLine("error: no configuration given (-c <cfg>)");
            return 1;
        }

        if (!File.Exists(cfgPath))
        {
            stderr.WriteLine($"error: configuration file not found: {cfgPath}");
            return 1;
        }

        ScenarioConfig config;
        try
        {
            config = ScenarioConfigParser.Parse(cfgPath);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: failed to parse '{cfgPath}': {ex.Message}");
            return 1;
        }

        if (config.RouteFiles.Count == 0)
        {
            stderr.WriteLine(
                $"error: '{cfgPath}' has no <input><net-file>/<route-files>; the sumo shim requires a " +
                "cfg with an <input> section (every SUMO .sumocfg has one).");
            return 1;
        }

        var beginTime = beginOverride ?? config.Begin;
        var endTime = endOverride ?? config.End;
        if (endTime <= beginTime)
        {
            stderr.WriteLine($"error: end ({endTime}) must be greater than begin ({beginTime}).");
            return 1;
        }

        var steps = (int)Math.Round((endTime - beginTime) / config.StepLength);

        var engine = new Engine();
        // Perf knob only -- does NOT change results (the engine's parallel loops are order-independent;
        // the parallelism-invariance parity test asserts byte-identical output across values). Set
        // before the run; <=0 leaves the all-cores default (the setter maps non-positive to -1).
        engine.MaxParallelism = maxParallelism;
        // CLI flag wins over the cfg's <processing><ignore-junction-blocker> element (same override
        // precedence as --end over <end>); absent either way, Engine's own -1 default applies.
        engine.IgnoreJunctionBlockerSeconds = ignoreJunctionBlockerOverride ?? config.IgnoreJunctionBlockerSeconds;
        // Env-var test/measurement gate for Engine.ContTurnInsideJunctionGate -- NOT a SUMO option, so
        // (like MaxParallelism above) it is deliberately NOT a `--flag` in the parsed-args table; it
        // mirrors LiveCitySim.cs's own LIVECITY_CONTTURNFIX env var for the identical property, kept
        // permanently (not a throwaway hack) so IgnoreJunctionBlockerTests can drive BOTH knobs through
        // this one shim path and stay directly comparable to LowDensityTeleportTests. Unset/non-"1" =>
        // false, the Engine default, so every existing shim invocation that never sets this env var is
        // byte-identical to before. See docs/NEED-arm5-mutual-junction-deadlock.md.
        engine.ContTurnInsideJunctionGate = Environment.GetEnvironmentVariable("SUMOSHARP_CONTTURNFIX") == "1";
        try
        {
            engine.LoadScenario(cfgPath);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: failed to load scenario: {ex.Message}");
            return 1;
        }

        // Register only the writers the caller asked for; each is additive and reads the same per-frame
        // export snapshot (see the observer classes' own comments). No flag -> no observer -> no file.
        using (var fcdWriter = fcdOut is not null ? new FcdWriterObserver(fcdOut) : null)
        using (var summaryWriter = summaryOut is not null ? new SummaryWriterObserver(summaryOut) : null)
        {
            if (fcdWriter is not null)
            {
                engine.AddExportObserver(fcdWriter);
            }

            if (summaryWriter is not null)
            {
                engine.AddExportObserver(summaryWriter);
            }

            engine.Run(steps);
        }

        if (statisticOut is not null)
        {
            StatisticWriter.Write(statisticOut, engine.TeleportCount,
                teleportsJam: engine.TeleportCountJam,
                teleportsYield: engine.TeleportCountYield,
                teleportsWrongLane: engine.TeleportCountWrongLane);
        }

        if (fcdOut is not null)
        {
            stdout.WriteLine($"wrote {fcdOut}");
        }

        if (summaryOut is not null)
        {
            stdout.WriteLine($"wrote {summaryOut}");
        }

        if (statisticOut is not null)
        {
            stdout.WriteLine($"wrote {statisticOut}");
        }

        if (tripinfoOut is not null)
        {
            // GAP-2 (docs/SUMOSHARP-SERVE-PATH-DROP-IN.md §2): engine.CompletedTrips is the real per-
            // vehicle arrival record (Sim.Core.CompletedTripInfo, captured at the route-end arrival
            // seam). Adapt it to Sim.Harness.TripInfoRecord here -- Sim.Core cannot reference
            // Sim.Harness (Sim.Harness already depends on Sim.Core; see CompletedTripInfo's own header
            // comment for why), so this shim, which references both, is where the two meet.
            var trips = new List<TripInfoRecord>(engine.CompletedTrips.Count);
            foreach (var trip in engine.CompletedTrips)
            {
                trips.Add(new TripInfoRecord(
                    trip.Id, trip.Depart, trip.Duration, trip.ArrivalSpeed,
                    ArrivalLane: trip.ArrivalLane,
                    ArrivalPos: trip.ArrivalPos,
                    ArrivalTime: trip.Arrival,
                    RouteLength: trip.RouteLength,
                    WaitingTime: trip.WaitingTime,
                    TimeLoss: trip.TimeLoss));
            }

            TripInfoWriter.Write(tripinfoOut, trips);
            stdout.WriteLine($"wrote {tripinfoOut}");
        }

        stdout.WriteLine($"ran {steps} steps over [{beginTime}, {endTime}] @ {config.StepLength}s");
        return 0;
    }

    // "--flag=value" -> ("--flag", "value"); "--flag" -> ("--flag", null). Only the FIRST '=' splits,
    // so a value containing '=' survives intact.
    private static (string Flag, string? InlineValue) SplitInline(string arg)
    {
        var eq = arg.IndexOf('=');
        return eq < 0 ? (arg, null) : (arg[..eq], arg[(eq + 1)..]);
    }

    private static double ParseTime(string value, string flag)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
        {
            throw new CliError($"{flag} value '{value}' is not a number");
        }

        return t;
    }

    private static int ParseInt(string value, string flag)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            throw new CliError($"{flag} value '{value}' is not an integer");
        }

        return n;
    }

    private static bool IsBooleanLiteral(string s) =>
        s is "true" or "false" or "1" or "0" or "True" or "False";
}
