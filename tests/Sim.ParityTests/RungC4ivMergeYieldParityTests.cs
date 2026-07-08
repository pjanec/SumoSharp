using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Rung C4-iv (TASKS.md "sameTarget-merge yield") -- the gap-acceptance MERGE half of C3 (scenario
// 19 never exercised it because its mainline vehicle was far). Two junction links whose
// <connection>s feed the SAME downstream lane converge rather than cross, so no JunctionConflict is
// recorded; a vehicle entering the merge must still follow-yield to a vehicle already traversing
// the other merging lane. scenarios/31-merge-yield-sym is a SYMMETRIC Y-merge (A->J major +
// B->J minor into JC) whose two internal lanes are mirror images. A slow major mA crosses the merge
// exactly as minor vB arrives, so vB yields then follows mA onto JC.
//
// The mechanism (Engine.SameTargetMergeConstraint), verified per-step against the vendored v1_20_0
// getLeaderInfo/adaptToJunctionLeader DEBUG trace: while vB is still on its approach lane and beyond
// the link's foe-visibility distance (4.5), the cautious approach governs and the merge is
// non-binding (SUMO's MAX2(vSafeLeader, vLinkWait) relaxation); once within visibility / on the
// internal lane it car-follows mA -- phase 1 (mA on its internal lane, t=8) then phase 2 (mA on the
// shared lane JC, cross-lane follow) -- converging to mA's 6.0. The SYMMETRIC geometry makes the
// merge conflict's lengthBehindCrossing terms cancel exactly (the asymmetric anchor 29-merge-yield
// carries a residual geometry term not yet ported -- see TASKS.md), which is why THIS scenario is
// the exact anchor.
//
// Runs 60 steps: mA clears by t=57 and vB (delayed by the yield) by t=59 in the golden.
public class RungC4ivMergeYieldParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "31-merge-yield-sym");

    [Fact]
    public void Run60Steps_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(60);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"Rung-C4-iv parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
        };

        foreach (var attribute in result.Attributes)
        {
            lines.Add(
                $"  attribute={attribute.Attribute} maxAbsError={attribute.MaxAbsError} rmse={attribute.Rmse} withinTolerance={attribute.WithinTolerance}");
        }

        if (result.PresenceMismatches.Count > 0)
        {
            lines.Add("  presence mismatches:");
            foreach (var mismatch in result.PresenceMismatches)
            {
                lines.Add($"    {mismatch.Kind} vehicle={mismatch.VehicleId} time={mismatch.Time?.ToString() ?? "n/a"}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Traffic.sln not found above test assembly).");
    }
}
