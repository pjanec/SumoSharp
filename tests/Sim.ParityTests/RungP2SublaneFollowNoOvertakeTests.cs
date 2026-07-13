using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Phase 2 (sublane following, too-narrow -> NO lateral overtake -- exact @1e-3). scenarios/
// 62-sublane-overtake: lateral-resolution=0.8, ONE 4.8 m lane, both vehicles CENTERED. A fast
// follower catches a slow leader (maxSpeed 5) and settles behind it at 5; posLat stays 0 the whole
// run. A 4.8 m lane is too narrow for two 1.8 m cars to pass with minGapLat clearance (the max
// sublane offset ±1.5 still overlaps the centered leader by 0.3 m), so SUMO's sublane model makes NO
// lateral change -- plain Krauss following. This is a COVERAGE anchor: it validates that the sublane
// drift code (P2.3/P2.2a) is inert for a centered-following pair (target latAlignment "center" -> 0,
// held by the small-latDist threshold), i.e. sublane mode does not perturb longitudinal car-following
// and does not induce a spurious lateral change when there is no room to pass. Reproduced with NO new
// leader/decision code -- the actual lateral overtake (a wider lane) is the P2.4 _wantsChangeSublane
// rung. Runs 40 steps.
public class RungP2SublaneFollowNoOvertakeTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "62-sublane-overtake");

    [Fact]
    public void Run40Steps_MatchesGoldenFcdWithinTolerance_NoLateralChangeWhenTooNarrow()
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
            $"Sublane-follow-no-overtake parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
