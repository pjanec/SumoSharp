using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Phase 2 / P2.2 (sublane side-by-side coexistence -- exact @1e-3 on pos/speed/posLat).
// scenarios/61-sublane-sidebyside: lateral-resolution=0.8, ONE wide lane (width 4.8). A slow leader
// v0 (maxSpeed 5) hugging the LEFT sublane (departPosLat "left" -> posLat +1.5) and a fast follower
// v1 hugging the RIGHT sublane (departPosLat "right" -> posLat -1.5). Their lateral footprints are
// DISJOINT, so the sublane model lets them coexist: v1 free-flows to 13.89 and PASSES v0 with no
// braking -- the defining sublane-vs-lane-mode difference (in lane mode a same-lane leader always
// binds and v1 would settle behind v0 forever, as rung 4). Both hold posLat = ±1.5 exactly.
//
// Port (on top of P2.3): departPosLat initial placement (Engine.InitialLatOffset) + the small-
// latDist drift threshold (MSLCM_SL2015.cpp:1924, so the 0.0005 gap between the ±1.5 departPosLat
// edge and the ±1.4995 alignment target is SKIPPED, matching SUMO's hold at ±1.5). The coexistence
// itself needs NO new leader logic -- the pre-existing FootprintsOverlap same-lane leader bypass
// (LeaderFollowSpeedConstraint) already returns +inf for a non-overlapping leader; there is only ONE
// leader ahead of the follower, so the per-sublane multi-leader query (MSLeaderInfo) is a LATER rung.
// NON-VACUOUS: if the follower treated the disjoint leader as binding it would settle at maxSpeed 5
// behind v0 instead of reaching 13.89 and passing (pos ~733 vs v0 ~292 by t=59). INERT: gated on
// lateral-resolution>0 (byte-identical for phase 1). Runs 60 steps (t=0..59).
public class RungP22SublaneSideBySideTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "61-sublane-sidebyside");

    [Fact]
    public void Run60Steps_MatchesGoldenFcdWithinTolerance_FollowerPassesLaterallySeparatedLeader()
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
            $"Rung-P2.2 sublane side-by-side parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
