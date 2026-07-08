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
//   dotnet run --project src/Sim.Run -- <scenarioDir> [--steps N] [--fcd-out PATH]
//
// Defaults: steps = round((end-begin)/step-length) from the scenario's *.sumocfg (matches how
// the parity tests pick their step count); fcd-out = <scenarioDir>/engine.fcd.xml.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine(
                "usage: Sim.Run <scenarioDir> [--steps N] [--fcd-out PATH]");
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
                default:
                    Console.Error.WriteLine($"error: unrecognized argument: {args[i]}");
                    return 2;
            }
        }

        var net = SingleFile(scenarioDir, "*.net.xml");
        var rou = SingleFile(scenarioDir, "*.rou.xml");
        var cfg = SingleFile(scenarioDir, "*.sumocfg");
        if (net is null || rou is null || cfg is null)
        {
            Console.Error.WriteLine(
                $"error: scenario dir must contain exactly one each of *.net.xml, *.rou.xml, " +
                $"*.sumocfg (found net={net}, rou={rou}, cfg={cfg})");
            return 2;
        }

        var config = ScenarioConfigParser.Parse(cfg);
        var steps = stepsOverride ?? (int)Math.Round((config.End - config.Begin) / config.StepLength);
        fcdOut ??= Path.Combine(scenarioDir, "engine.fcd.xml");

        var engine = new Engine();
        engine.LoadScenario(net, rou, cfg);

        using (var writer = new FcdWriterObserver(fcdOut))
        {
            engine.AddExportObserver(writer);
            engine.Run(steps);
        }

        Console.WriteLine($"wrote {fcdOut}  ({steps} steps, [{config.Begin}, {config.End}] @ {config.StepLength}s)");
        return 0;
    }

    // A scenario dir has exactly one of each input; more than one is ambiguous, so refuse.
    private static string? SingleFile(string dir, string pattern)
    {
        var matches = Directory.GetFiles(dir, pattern);
        return matches.Length == 1 ? matches[0] : null;
    }
}
