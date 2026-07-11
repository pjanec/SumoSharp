using Sim.Core;
using Sim.Harness;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// Rung T1: ENSEMBLE statistical parity of the probabilistic-flow INSERTION-COUNT distribution vs
// SUMO. scenarios/58-flow-probability: a single <flow probability="0.15"> over [0,60) on a 500 m
// single lane; each second a Bernoulli hit tries to insert a car at pos 0 / speed 0, thinned by the
// entry-lane occupancy gate. Our per-flow seeded RNG (SplitMix64) is deliberately NOT SUMO's flow
// RNG, so a specific run's insertion TIMES do not match a specific SUMO run -- the parity bar is the
// ensemble insertion-COUNT distribution (mean/std), the same statistical bar as C1/C7.
//
// The compared statistic per run is the number of DISTINCT vehicle ids that appear in the FCD =
// the number of vehicles the flow actually inserted (a vehicle appears in the FCD from its insertion
// step onward, so this counts insertions even for cars that never complete the edge before end=60 --
// verified equal to the extended-run tripinfo trip count when the golden was generated).
// Golden = 50 committed SUMO runs (seeds 1..50).
public class RungT1ProbabilisticFlowEnsembleTests
{
    private const int N = 50;
    private const int Steps = 60;
    private readonly ITestOutputHelper _o;
    public RungT1ProbabilisticFlowEnsembleTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void ProbabilisticFlow_EngineEnsembleInsertionCount_MatchesSumoEnsembleStatistically()
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", "58-flow-probability");
        var tol = ToleranceConfig.Load(Path.Combine(dir, "tolerance.json"));

        // Golden: distinct-vehicle-id count in each committed SUMO FCD run.
        var goldenCounts = Directory
            .EnumerateFiles(Path.Combine(dir, "golden.ensemble"), "*.fcd.xml")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => FcdParser.Parse(p).VehicleIds.Count)
            .ToList();
        Assert.Equal(N, goldenCounts.Count);

        // Engine: run seeds 1..N, count inserted flow vehicles per run.
        var engineCounts = new List<int>(N);
        for (ulong seed = 1; seed <= N; seed++)
        {
            var e = new Engine { Seed = seed };
            e.LoadScenario(
                Path.Combine(dir, "net.net.xml"),
                Path.Combine(dir, "rou.rou.xml"),
                Path.Combine(dir, "config.sumocfg"));
            engineCounts.Add(e.Run(Steps).VehicleIds.Count);
        }

        var (meanG, stdG) = MeanStd(goldenCounts);
        var (meanE, stdE) = MeanStd(engineCounts);
        var meanTol = tol.MeanToleranceFor("insertionCount");
        var stdTol = tol.StdToleranceFor("insertionCount");
        _o.WriteLine($"insertionCount mean: engine={meanE:F3} sumo={meanG:F3} |d|={Math.Abs(meanE - meanG):F3} (tol {meanTol})");
        _o.WriteLine($"insertionCount std : engine={stdE:F3} sumo={stdG:F3} |d|={Math.Abs(stdE - stdG):F3} (tol {stdTol})");

        // Non-vacuous: the flow really inserts several cars per run and the count varies across seeds
        // (a broken flow that inserted 0 or a fixed number would pass a mean check but fail this).
        Assert.True(meanG > 3.0, "golden ensemble should insert several cars per run");
        Assert.True(stdG > 0.5, "golden insertion count should vary across seeds");

        Assert.True(Math.Abs(meanE - meanG) <= meanTol,
            $"insertion-count mean out of tolerance: engine={meanE:F3} sumo={meanG:F3} (tol {meanTol})");
        Assert.True(Math.Abs(stdE - stdG) <= stdTol,
            $"insertion-count std out of tolerance: engine={stdE:F3} sumo={stdG:F3} (tol {stdTol})");
    }

    private static (double Mean, double Std) MeanStd(IReadOnlyCollection<int> xs)
    {
        var mean = xs.Average();
        var std = Math.Sqrt(xs.Select(x => (x - mean) * (x - mean)).Sum() / xs.Count);
        return (mean, std);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Traffic.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
