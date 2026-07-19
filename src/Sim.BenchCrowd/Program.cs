using System.Diagnostics;
using System.Globalization;
using Sim.Core.Orca;

// POC-7a (docs/PEDESTRIAN-POC-PLAN.md POC-7; docs/PEDESTRIAN-DESIGN.md §3c/§9): measures
// OrcaCrowd.Step throughput SERIAL vs the new opt-in PARALLEL plan (OrcaCrowd.UseParallelStep) across
// a range of crowd sizes, and reports the speedup and where it plateaus -- the POC-7a scale
// deliverable. See docs/PEDESTRIAN-POC7A-FINDINGS.md for the recorded numbers from a run of this tool.
//
// Deliberately a SEPARATE project, NOT part of `dotnet test` (same convention as Sim.Bench /
// Sim.BenchCity / Sim.Run -- see their own header comments): a wall-clock benchmark's numbers are
// machine-dependent and must never gate the offline parity loop, which stays constant-time and
// network-free. Run manually:
//   dotnet run -c Release --project src/Sim.BenchCrowd -- [options]
//     --sizes N,N,...      crowd sizes to measure (default 1000,10000,50000,100000)
//     --steps N            timed steps per measurement (default 20)
//     --warmup N            untimed JIT/cache warmup steps before each timed run (default 3)
//     --dt SECONDS          integration step (default 0.2)
//     --neighbour-dist M    ORCA neighbour cutoff / spatial-hash cell size (default 3.0)
//     --max-neighbours N    RVO2 nearest-k cap, 0 = unlimited (default 8)
//     --max-parallelism N   caps OrcaCrowd.MaxParallelism on the parallel run (default -1 = runtime auto)
internal static class Program
{
    private static int Main(string[] args)
    {
        // Invariant-culture output so "12.345" never prints as "12,345" and breaks a downstream parser.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        var sizes = new[] { 1_000, 10_000, 50_000, 100_000 };
        var steps = 20;
        var warmup = 3;
        var dt = 0.2;
        var neighbourDist = 3.0;
        var maxNeighbours = 8;
        var maxParallelism = -1;
        // P6-2-4 (docs/PEDESTRIAN-P6-2-REGION-DESIGN.md): when set, the "parallel" column uses the region-
        // decomposed plan (OrcaCrowd.UseRegionDecomposition) instead of the flat Parallel.For, so a run with
        // vs without --region-decomp measures P6-2's per-core uplift. --region-mult is the region cell size
        // in multiples of neighbourDist.
        var regionDecomp = false;
        var regionMult = 4.0;

        for (var a = 0; a < args.Length; a++)
        {
            switch (args[a])
            {
                case "--sizes":
                    sizes = args[++a].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(int.Parse).ToArray();
                    break;
                case "--steps":
                    steps = int.Parse(args[++a]);
                    break;
                case "--warmup":
                    warmup = int.Parse(args[++a]);
                    break;
                case "--dt":
                    dt = double.Parse(args[++a], CultureInfo.InvariantCulture);
                    break;
                case "--neighbour-dist":
                    neighbourDist = double.Parse(args[++a], CultureInfo.InvariantCulture);
                    break;
                case "--max-neighbours":
                    maxNeighbours = int.Parse(args[++a]);
                    break;
                case "--max-parallelism":
                    maxParallelism = int.Parse(args[++a]);
                    break;
                case "--region-decomp":
                    regionDecomp = true;
                    break;
                case "--region-mult":
                    regionMult = double.Parse(args[++a], CultureInfo.InvariantCulture);
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown option: {args[a]}");
                    PrintUsage();
                    return 2;
            }
        }

        Console.WriteLine($"logical processors : {Environment.ProcessorCount}");
        Console.WriteLine($"max-parallelism arg : {(maxParallelism > 0 ? maxParallelism.ToString(CultureInfo.InvariantCulture) : "-1 (runtime auto)")}");
        Console.WriteLine($"steps/warmup        : {steps}/{warmup}   dt={dt}   neighbourDist={neighbourDist}   maxNeighbours={maxNeighbours}");
        Console.WriteLine($"parallel column     : {(regionDecomp ? $"REGION-DECOMP (mult={regionMult})" : "flat Parallel.For")}");
        Console.WriteLine();
        Console.WriteLine($"{"N",10} | {"serial ms/step",15} | {"parallel ms/step",17} | {"speedup",8} | {"serial steps/s",15} | {"parallel steps/s",17}");
        Console.WriteLine(new string('-', 96));

        foreach (var n in sizes)
        {
            var serial = BuildCrowd(n, useParallel: false, maxParallelism, neighbourDist, maxNeighbours, regionDecomp: false, regionMult);
            var parallel = BuildCrowd(n, useParallel: true, maxParallelism, neighbourDist, maxNeighbours, regionDecomp, regionMult);

            for (var w = 0; w < warmup; w++)
            {
                serial.Step(dt);
                parallel.Step(dt);
            }

            var swSerial = Stopwatch.StartNew();
            for (var s = 0; s < steps; s++)
            {
                serial.Step(dt);
            }

            swSerial.Stop();

            var swParallel = Stopwatch.StartNew();
            for (var s = 0; s < steps; s++)
            {
                parallel.Step(dt);
            }

            swParallel.Stop();

            var serialMsPerStep = swSerial.Elapsed.TotalMilliseconds / steps;
            var parallelMsPerStep = swParallel.Elapsed.TotalMilliseconds / steps;
            var speedup = serialMsPerStep / parallelMsPerStep;

            Console.WriteLine(
                $"{n,10} | {serialMsPerStep,15:F3} | {parallelMsPerStep,17:F3} | {speedup,7:F2}x | " +
                $"{1000.0 / serialMsPerStep,15:F1} | {1000.0 / parallelMsPerStep,17:F1}");
        }

        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "usage: Sim.BenchCrowd [--sizes N,N,...] [--steps N] [--warmup N] [--dt SECONDS] " +
            "[--neighbour-dist M] [--max-neighbours N] [--max-parallelism N] [--region-decomp] [--region-mult K]");
    }

    // Deterministic crowd of exactly `n` agents: a near-square grid, each agent routed to the point
    // mirrored through the grid centre (dense crossing traffic -- continuous plan churn, not a crowd
    // that instantly settles), spatial hash ON (mandatory at this scale -- brute force is O(n^2)),
    // MaxNeighbours capping the per-agent LP size the way a real deployment would, SymmetryBreak so
    // the antipodal-style crossings don't deadlock. UseSpatialHash/MaxNeighbours/SymmetryBreak are all
    // scratch-touching features -- exercising them here means the benchmark's parallel path takes the
    // exact same code path OrcaParallelStepTests proves bit-identical, not a simplified stand-in.
    private static OrcaCrowd BuildCrowd(
        int n, bool useParallel, int maxParallelism, double neighbourDist, int maxNeighbours,
        bool regionDecomp, double regionMult)
    {
        const double radius = 0.35;
        const double maxSpeed = 1.4;
        const double spacing = 1.5;

        var gx = (int)Math.Ceiling(Math.Sqrt(n));
        var gy = (int)Math.Ceiling((double)n / gx);

        var crowd = new OrcaCrowd(n)
        {
            UseSpatialHash = true,
            NeighbourDist = neighbourDist,
            MaxNeighbours = maxNeighbours,
            SymmetryBreak = 0.05,
            UseParallelStep = useParallel,
            // P6-2: the parallel variant uses region decomposition when requested (it takes precedence over
            // the flat parallel plan on OrcaCrowd). The serial variant always passes regionDecomp:false.
            UseRegionDecomposition = useParallel && regionDecomp,
            RegionCellSizeMultiplier = regionMult,
        };

        if (maxParallelism > 0)
        {
            crowd.MaxParallelism = maxParallelism;
        }

        var originX = -(gx - 1) * spacing / 2.0;
        var originY = -(gy - 1) * spacing / 2.0;

        var placed = 0;
        for (var iy = 0; iy < gy && placed < n; iy++)
        {
            for (var ix = 0; ix < gx && placed < n; ix++)
            {
                var p = new Vec2(originX + ix * spacing, originY + iy * spacing);
                crowd.Add(p, radius, maxSpeed, goal: -p);
                placed++;
            }
        }

        return crowd;
    }
}
