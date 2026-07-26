using System;
using System.Globalization;
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
        default:
            Console.Error.WriteLine($"Sim.DensityDiff: unrecognized argument '{args[i]}'.");
            return 2;
    }
}

if (outPath is null)
{
    Console.Error.WriteLine("Sim.DensityDiff: --out FILE is required.");
    Console.Error.WriteLine("usage: --cars N --steps N --out FILE");
    return 2;
}

var repoRoot = FindRepoRoot();
var cfg = LiveCityConfig.ForRepoRoot(repoRoot);
cfg.CarTargetConcurrent = cars; // direct property set -- no LIVECITY_CARS env var touched, nothing leaks.

Console.WriteLine(
    $"Sim.DensityDiff: dataset='{cfg.DatasetDir}' cars={cars} steps={steps} out='{outPath}'");

using var sink = new Sim.DensityDiff.DemandRouteFileSink(outPath);
using var sim = new LiveCitySim(cfg, recordDemandSink: sink);

// The demo's OWN insertion log (an existing LiveCitySim diagnostic, independent of the B1 recorder
// tee above) -- used purely as the SC3 ground truth: "vehicle count and depart times in the file
// match the demo's own insertion log exactly". Both are populated at the SAME call site
// (LiveCitySim.Step()'s spawn block), so any divergence between them is a bug in the recorder, not
// a measurement artifact.
sim.SpawnLog = new System.Collections.Generic.List<(double Depart, string From, string To)>();

for (var s = 0; s < steps; s++)
{
    sim.Step();
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
