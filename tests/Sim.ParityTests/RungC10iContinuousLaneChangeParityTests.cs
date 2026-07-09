using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Rung C10-i (continuous lane change): with lanechange.duration > 0, a lane change is not an instant
// lane-index snap -- it slides over `duration` steps, and SUMO emits the SOURCE lane label until the
// vehicle center crosses the lane midpoint (halfway through the maneuver), then the target. This is
// the foundation of the lateral axis (the LatOffset seam). scenarios/43-continuous-lanechange:
// E0(2 lanes)->E1(1 lane), E0_0 does NOT connect to E1 so v0 must strategic-change E0_0->E0_1 on a
// CLEAR road (no leader) -- isolating the change TIMING from car-following. SUMO golden
// (lanechange.duration=3): E0_0 (t=0,1) then E0_1 (t=2+); the change is decided at t=0 (as the engine
// already does) but the label flips at t=2, not t=1. Pre-C10 the engine snapped instantly (E0_1 at
// t=1). pos/speed stay free-flow throughout.
//
// Port = Engine.AdvanceLaneChanges + the StartLaneChangeManeuver command, gated on
// lanechange.duration > 0 so every duration-0 scenario keeps the instant snap (byte-identical).
// Deferred to follow-ons: the lateral POSITION (y/LatOffset comparison) and shadow-lane car-following
// during the straddle. Runs 40 steps (t=0..39).
public class RungC10iContinuousLaneChangeParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "43-continuous-lanechange");

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
            $"Rung-C10-i continuous-lane-change parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
