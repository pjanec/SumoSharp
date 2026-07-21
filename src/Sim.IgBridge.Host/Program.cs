using System.Text;
using Sim.IgBridge;

// IgBridge verification host (docs/IGBRIDGE-TASKS.md). T0.3+T1.2 smoke: drive the fixed-10 Hz runner over
// the box scenario, resample the reused reconstruction stack at the emit cadence, write an IG-native
// new/upd/del trace, and prove the emitted trace is byte-deterministic across two runs. FakeIg replay +
// side-by-side render + metrics land in T1.3-T1.5.
var repoRoot = FindRepoRoot();
var boxDir = Path.Combine(repoRoot, "scenarios", "_ped", "demo_city", "box");
var cfg = new IgBridgeConfig(Path.Combine(boxDir, "net.xml"), Path.Combine(boxDir, "scenario.rou.xml"))
{
    StepLength = 0.1,
    Seed = 42,
};
var emit = new IgEmitConfig { EmitHz = 20.0, LookaheadSeconds = 0.1 };

const int Steps = 1200; // 120 s @ 10 Hz

var outDir = Path.Combine(repoRoot, "artifacts", "igbridge");
Directory.CreateDirectory(outDir);
var tracePath = Path.Combine(outDir, "trace.jsonl");

Console.WriteLine($"box={boxDir}");

// Run 1: write the trace to disk (the replayable artifact) and capture its text for the determinism check.
string traceText1;
using (var sw = new StringWriter())
{
    var stats = RunSession(cfg, emit, Steps, new IgTraceWriter(sw));
    traceText1 = sw.ToString();
    File.WriteAllText(tracePath, traceText1);
    Console.WriteLine(stats);
    Console.WriteLine($"trace written: {tracePath} ({traceText1.Length:N0} chars)");
}

// Run 2: emit-stream determinism.
string traceText2;
using (var sw = new StringWriter())
{
    RunSession(cfg, emit, Steps, new IgTraceWriter(sw));
    traceText2 = sw.ToString();
}

Console.WriteLine(traceText1 == traceText2
    ? "EMIT DETERMINISM OK: two runs produced a byte-identical trace"
    : "EMIT DETERMINISM FAILED: traces diverged");

return 0;

static string RunSession(IgBridgeConfig cfg, IgEmitConfig emit, int steps, IgTraceWriter trace)
{
    var runner = new IgBridgeRunner(cfg);
    var session = new IgBridgeSession(runner, emit, trace);

    for (var step = 0; step < steps; step++)
    {
        runner.Tick();
        session.Advance();
    }

    session.Finish();

    var sb = new StringBuilder();
    sb.AppendLine($"ticks={steps} simTime={runner.SimTime:F3} "
        + $"veh(distinct={runner.VehicleHistories.Count}, maxLive={runner.LiveVehicles.Count}) "
        + $"ped(distinct={runner.PedHistories.Count}, live={runner.LivePeds.Count})");
    sb.Append($"emitted records={session.EmittedCount} (EmitHz={emit.EmitHz}, lookahead={emit.LookaheadSeconds}s)");
    return sb.ToString();
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "Traffic.sln"))) d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("repo root not found");
}
