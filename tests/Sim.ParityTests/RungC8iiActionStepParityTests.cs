using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Rung C8-ii (TASKS.md "actionStepLength > 1 (reaction time)"). A vehicle re-plans its
// car-following speed only every `actionStepLength` seconds; between those action steps it
// continues with the acceleration decided at the last one (its reaction time -- it does NOT react
// to anything until the next action step). scenarios/28-actionstep is scenario 21's single 1000m
// lane with `default.action-step-length=2` and one vehicle accelerating 0 -> 13.89.
//
// The discriminating behavior (and why every-step re-planning gets it wrong): at the action step
// t=4 (speed 10.4) SUMO plans an acceleration of 1.745 to reach the 13.89 cap over the 2-second
// action interval, then HOLDS that acceleration through the NON-action step t=5 (-> 12.145) into
// t=6 (-> 13.89). A vehicle re-planning every step would instead recompute a smaller acceleration
// at t=5 and only reach ~13.02 at t=6. The port (Engine.ComputeMoveIntent action-step gate +
// VehicleRuntime.LastActionTime, from MSVehicle.cpp:4443-4462 / MSVehicle.h:638) is gated on
// actionStepLength > dt, so every prior scenario (all action-step-length=1) is byte-identical.
//
// Runs 20 steps (config end=20): covers the accel ramp, the held-accel cap approach, and steady
// free-flow cruise.
public class RungC8iiActionStepParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "28-actionstep");

    [Fact]
    public void Run20Steps_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(20);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"Rung-C8-ii parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
