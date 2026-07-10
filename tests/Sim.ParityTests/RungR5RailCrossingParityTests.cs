using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Rung R5 (rail support) trajectory parity test: a LEVEL CROSSING (MSRailCrossing). A road car
// approaches a rail_crossing junction where a rail line crosses the road. A train arrives first and
// occupies the crossing; the car, facing a crossing that is already closed (red) because the train
// is physically on it, brakes to a stop ~1 m before the crossing (pos 295.899 on SC) and waits.
// Once the train clears and the crossing's opening sequence completes, the road link goes green and
// the car proceeds (~t=44). This is "road traffic yields to trains".
//
// NON-VACUOUS: the pre-port engine has no rail crossing -- it would throw KeyNotFoundException on the
// tl="C" connection (a rail_crossing junction has no <tlLogic>), and even guarded it would drive the
// car straight across in front of the train. Exact @1e-3 on lane,pos,speed. Mirrors
// RungA1ParityTests, pointed at scenarios/51-rail-crossing.
public class RungR5RailCrossingParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "51-rail-crossing");

    [Fact]
    public void Run80Steps_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(80);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"Rung-R5 rail-crossing parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
