using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Rung C4-vii-b (keep-right over-accumulation + final-edge arrival strand). scenarios/45-multilane-
// keepright-arrival isolates the keep-right/arrival interaction with a STRAIGHT-through vehicle (no
// turn, single internal lane) so it is free of the still-open cont-internal-lane / junction-timing
// gaps that keep scenarios/44 skip-gated. A off-ramp B->D fed only by the right lane forces the
// through-vehicle onto AB_1 (arrival lane BC_1); SUMO keeps it on AB_1 (stayOnBest suppresses keep-
// right on a must-avoid exit lane within TURN_LANE_DIST) and arrives it on BC_1 -- 0 lane changes
// (verified via SUMO --lanechange-output AND TraCI keepRightProbability == 0 on AB_1).
//
// The pre-fix engine (a) over-accumulated keep-right on AB_1 (it vetoed the COMMIT but not the
// accumulation), so v0 reached BC_1 past the fire threshold and keep-changed BC_1 -> BC_0, and (b)
// then FROZE at the BC_0 lane end (pos 60, speed 0) forever because final-edge arrival required the
// exact resolved pool lane. The fix ports SUMO's stayOnBest accumulation gate (Engine.
// KeepRightStrategicStay) and makes last-edge arrival lane-agnostic (Engine.ExecuteMoves). See
// scenarios/45/provenance.txt for the full derivation and the non-vacuousness check.
public class RungC4viibKeepRightArrivalParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "45-multilane-keepright-arrival");

    [Fact]
    public void Run30Steps_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(30);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"Rung-C4-vii-b keep-right/arrival parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
