using System.Diagnostics;
using System.Globalization;
using Sim.Core;
using Sim.Evac;

// PANIC-EVAC-PHASE5-TASKS.md T4.2 (design §4/§6): the Tier-1 cost-profile deliverable. Runs the
// organic-town evac demo (EvacOrganicScenario -- the same fixture EvacOrganicDemoTests and
// SceneGen.BuildEvacOrganic use: scenarios/_bench/city-organic-L2, incident at junction 415, auto-
// track working region) with EvacDirector's opt-in profiler turned on, and reports a per-phase
// wall-time breakdown -- fear update, disc feeds, pedestrian step, pusher step, engine.Step (the
// parity core, context only), and "other" -- so the Tier-2 optimization list (design §6 candidates:
// FearField grid, spatial disc feeds, OrcaCrowd.UseSpatialHash, etc.) targets the MEASURED dominant
// hotspot rather than a guessed one.
//
// NOT part of `dotnet test` -- a deliberate CLI utility (like Sim.Bench / Sim.BenchCity / Sim.Viz).
// Never touches the parity engine's committed inputs/goldens; EvacDirector's profiler is a pure
// opt-in observability seam (null unless EnableProfiling() is called), so running this tool has zero
// effect on any other demo/test's behaviour or the determinism hash.
internal static class Program
{
    private const int Ticks = 300;

    private static int Main()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        var repoRoot = RepoRoot();
        var (engine, director) = EvacOrganicScenario.Build(repoRoot);
        director.EnableProfiling();

        var peakActive = 0;
        var everActive = new HashSet<VehicleHandle>();

        var sw = Stopwatch.StartNew();
        for (var step = 0; step < Ticks; step++)
        {
            director.Tick();

            var handles = engine.VehicleHandles;
            peakActive = Math.Max(peakActive, handles.Length);
            foreach (var h in handles)
            {
                everActive.Add(h);
            }
        }

        sw.Stop();

        var profile = director.Profile;
        var totalMs = sw.Elapsed.TotalMilliseconds;

        var fearMs = profile.FearUpdate.TotalMilliseconds;
        var discFeedsMs = profile.DiscFeeds.TotalMilliseconds;
        var pedStepMs = profile.PedestrianStep.TotalMilliseconds;
        var pusherStepMs = profile.PusherStep.TotalMilliseconds;
        var engineStepMs = profile.EngineStep.TotalMilliseconds;
        var accountedMs = fearMs + discFeedsMs + pedStepMs + pusherStepMs + engineStepMs;
        var otherMs = Math.Max(0.0, profile.TotalTick.TotalMilliseconds - accountedMs);

        Console.WriteLine("=== T4.2 evac cost profile: organic town (EvacOrganicScenario) ===");
        Console.WriteLine($"scenario           : scenarios/_bench/city-organic-L2 (274 junctions, 1186 edges, 618 trips)");
        Console.WriteLine($"ticks              : {Ticks}  (stepLength=1.0s)");
        Console.WriteLine($"peak concurrent    : {peakActive}  (vehicles active in the SAME tick, parity-engine-wide)");
        Console.WriteLine($"ever active        : {everActive.Count}  (distinct vehicles seen over the whole run)");
        Console.WriteLine($"panicked           : {director.PanickedCount}");
        Console.WriteLine($"converted          : {director.ConvertedCount}");
        Console.WriteLine($"pedestrians        : {director.PedestrianCount}");
        Console.WriteLine();
        Console.WriteLine($"total generation wall time : {totalMs:F1} ms  ({sw.Elapsed.TotalSeconds:F3} s)");
        Console.WriteLine();
        Console.WriteLine("per-phase breakdown (of EvacDirector.Tick() wall time):");

        void PrintPhase(string name, double ms) =>
            Console.WriteLine($"  {name,-18} {ms,9:F1} ms   {(totalMs > 0 ? ms / totalMs : 0.0),6:P1}");

        PrintPhase("fear update", fearMs);
        PrintPhase("disc feeds", discFeedsMs);
        PrintPhase("pedestrian step", pedStepMs);
        PrintPhase("pusher step", pusherStepMs);
        PrintPhase("engine.Step", engineStepMs);
        PrintPhase("other", otherMs);

        // The dominant EVAC hotspot -- deliberately excludes engine.Step (the parity core; it is
        // reported as context, not a Tier-2 optimization candidate per design §6) and "other" (not
        // one specific named phase to optimize). This is the input that scopes the Tier-2 task list.
        var evacPhases = new (string Name, double Ms)[]
        {
            ("fear update", fearMs),
            ("disc feeds", discFeedsMs),
            ("pedestrian step", pedStepMs),
            ("pusher step", pusherStepMs),
        };
        var dominant = evacPhases.OrderByDescending(p => p.Ms).First();

        Console.WriteLine();
        Console.WriteLine(
            $"DOMINANT EVAC HOTSPOT: {dominant.Name}  ({dominant.Ms:F1} ms, " +
            $"{(totalMs > 0 ? dominant.Ms / totalMs : 0.0):P1} of total tick time) " +
            "-- this is what Tier 2 should optimize first.");

        return 0;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Traffic.sln not found above the exe).");
    }
}
