using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Cross-junction leader following: a follower approaching a junction must car-follow a slow leader
// that has ALREADY crossed onto the downstream lane, AND its insertion is delayed while that
// downstream leader is too close. scenarios/39-crossjunction-leader: AJ(200m) -> J -> JB(200m),
// single lane. `lead` (maxSpeed 3) sits on JB just past the junction; `foll` (13.89) approaches on
// AJ. SUMO golden: foll's insertion is DELAYED to t=2 (the downstream leader is too close at t=0/1
// to enter at departSpeed), then it does the cautious cross-junction follow -- 13.89 at t=2,3, then
// 10.403 at t=4 while STILL ON AJ (pos 199.293), 7.403 on JB at t=5, ... converging to the leader's
// 3.0. Two entangled mechanisms, both ported:
//   (1) Cross-junction LEADER following (Engine.CrossJunctionLeaderConstraint): the same-lane
//       LeaderFollowSpeedConstraint sees only ego's current lane, so the follow speed is Min'd
//       against the nearest leader on a downstream pool lane within the plan-move lookahead.
//   (2) Cross-junction INSERTION safety (Engine.TryInsertOnLane): the insertion follow-check also
//       considers a downstream leader; if the safe insertion speed is below departSpeed, insertion
//       fails that step and retries. Requires processing insertions in vehicle-definition order
//       (SUMO's MSInsertionControl order) so the downstream leader `lead` (defined first) is placed
//       before `foll` is checked.
//
// Runs 45 steps (t=0..44); neither vehicle arrives within the sim.
public class RungCrossJunctionLeaderParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "39-crossjunction-leader");

    [Fact]
    public void Run45Steps_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(45);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"Cross-junction-leader parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
