using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Rung C5 follow-on (willPass): a crossing vehicle must NOT yield to a foe that will not enter the
// junction. scenarios/38-keepclear-crosstraffic is scenario 34's keepClear box WITH the crossing
// vehicle nCross restored: mBlock sits stopped on exit JE, mThrough (W->E major) keepClear-stops at
// the junction entry (WJ@91.8), and nCross (N->S minor) crosses freely -- because mThrough is
// keepClear-held, SUMO clears its request (setRequest=false) so blockedByFoe's !willPass
// short-circuit (MSLink.cpp:935) lets nCross proceed. The pre-fix engine had nCross over-yield
// (stop at NJ@95.9) to the stopped mThrough.
//
// Port = Engine.FoeKeepClearBlocked, gating the approaching-foe yield in JunctionYieldConstraint's
// crossing branch: when the approaching foe is keepClear-blocked (its own checkRewindLinkLanes
// removal would fire), ego does not yield to it. Inert for every scenario without a downstream jam
// (suite stays green), so only this scenario is affected.
//
// Runs the golden's full 40 steps: nCross clears by t=18; mThrough + mBlock stay put (JE blocked).
public class RungC5WillPassParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "38-keepclear-crosstraffic");

    [Fact]
    public void Run40Steps_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(40);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"Rung-C5-willPass parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
