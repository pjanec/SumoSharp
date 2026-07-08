using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// C2-ii's real parity test: scenarios/18-strategic-turnlane. veh0 routes E1->E2 but departs on
// E1_0, a drop lane that does NOT connect to E2 (only E1_1 does) -- it must strategic-change
// left to E1_1 before reaching junction B. Ported from MSLCM_LC2013's STRATEGIC/URGENT block
// (`_wantsChange`, sumo/src/microsim/lcmodels/MSLCM_LC2013.cpp ~1216-1327) --
// see Engine.TryStrategicLaneChange's own header comment for the exact derivation. The golden
// (reverse-engineered, see TASKS.md C2-ii): pos/speed are pure free-flow (13.89 constant from
// t=6, a lanechange.duration=0 lateral snap never perturbs longitudinal motion); `lane` is
// E1_0 through t<=16, E1_1 from t=17, :B_0_0 at t=38, E2_0 at t=39.
public class RungC2ParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "18-strategic-turnlane");

    [Fact]
    public void Run90Steps_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(90);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"Rung-C2 parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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

    // Mirrors RungA2ParityTests.RepoRoot(): resolve the repo root by walking up from
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
