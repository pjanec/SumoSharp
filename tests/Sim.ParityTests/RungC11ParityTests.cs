using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// C11-i: IDM (Intelligent Driver Model) car-following model + carFollowModel dispatch --
// scenarios/22-idm-carfollow, both vTypes carFollowModel="IDM" on scenario 01's 1000m single
// lane (net = scenario 01's net). "lead" (maxSpeed=6) free-accelerates via IDM.freeSpeed to its
// low desired speed; "follow" (default desired ~13.9) free-accelerates via IDM.freeSpeed to
// ~13.7, then brakes via IDM.followSpeed's gap term and settles following "lead" at 6.0 m/s at
// the IDM equilibrium gap. sigma=0 (irrelevant for IDM -- it never dawdles), Euler,
// actionStepLength=1. Runs the full 60s (scenario end=60) to also cover the long IDM-equilibrium
// tail (t=53..59 in golden.fcd.xml) where the follower's speed converges to 6.0 to 1e-6.
public class RungC11ParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "22-idm-carfollow");

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
            $"RungC11 (IDM) parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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

    // Mirrors EngineRung1PlumbingTests.RepoRoot(): resolve the repo root by walking up from
    // the test assembly's location until Traffic.sln is found.
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
