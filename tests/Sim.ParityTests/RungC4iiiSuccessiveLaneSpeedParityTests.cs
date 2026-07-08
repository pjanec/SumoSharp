using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Rung C4-iii (partial -- the SUCCESSIVE-LANE SPEED-LIMIT half): MSVehicle::planMoveInternal caps
// the free-flow speed so a vehicle never enters an UPCOMING lane on its route faster than that lane
// permits (MSVehicle.cpp:2894-2900 per-lane `va = MAX2(freeSpeed(getSpeed(), seen, laneMaxV),
// vMinComfortable); v = MIN2(va, v)`). Every pre-C4-iii scenario left this unexercised because no
// tested vehicle's ON-ROUTE lanes dropped the speed limit ahead of it (junction turn lanes that do
// drop it were always OFF the straight-through path). A single-lane priority roundabout is the first
// geometry whose on-route internal ring lanes are speed-limited by curvature.
//
// scenarios/33-roundabout-solo is that roundabout with a SINGLE circulating vehicle vWest
// (WIn->RW->RS->RE->EOut), so the ONLY mechanism under test is the successive-lane cap on the curved
// ring internal lanes (:RW_1 at 9.11, :RS_1 at 5.58) -- there is no second vehicle, hence no junction
// yield or sameTarget merge (the two-vehicle roundabout's entry-yield needs the junction
// arrival-time right-of-way subsystem, still unported -- see TASKS.md scenario 32). vWest brakes
// approaching each ring node exactly as SUMO does (e.g. 11.48 -> 6.98 -> lands EXACTLY on the RWtoRS
// lane end at 7.947333, then 5.58 onto :RS_1), which also exercises the strict `>` lane-end boundary
// (MSVehicle.cpp:4282: a vehicle that lands at pos == laneLength stays on its lane one more step).
// Golden regenerated in-session from SUMO 1.20.0 (Euler, sigma=0, teleport off, seed 42).
//
// Runs 36 steps: vWest clears to the east exit by t=35 in the golden.
public class RungC4iiiSuccessiveLaneSpeedParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "33-roundabout-solo");

    [Fact]
    public void Run36Steps_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(36);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"Rung-C4-iii (successive-lane speed) parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
