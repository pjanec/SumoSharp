using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// P2-G acceptance (docs/HIGH-DENSITY-P2G-DESIGN.md §6): the SUMO-faithful keep-right target-lane
// LEADER safety veto anchor. scenarios/49-multilane-keepright is a straight 2-lane edge (AB, 2500 m,
// speed 13.89, sigma=0), 24 vehicles departing period 1s with NUMERIC pinned alternating depart
// lanes (0,1,0,1,...), departSpeed="max". Vanilla SUMO 1.20.0 keeps every vehicle on its departure
// lane for the whole 100 s run (no lane changes at all) because vehicle v1 (departLane=1) would land
// too close behind the lane-0 leader if it kept right, so SUMO's target-lane leader secure-gap check
// blocks the change. Before the P2-G fix, `Engine.ApplyKeepRightDecision` had NO safety/blocker veto
// on the keep-right (right-lane) path, so the engine spuriously moved v1 from AB_1 to AB_0 at t=7 s,
// cascading into a large divergence (verified during authoring: pre-fix run on this exact scenario
// first diverges at t=7 with a LANE-MISMATCH on v1, max pos err 15.00 m by t=99). With the fix
// (`IsTargetLaneSafe(v, neighLead, null, dt)` gating the keep-right swap), the engine matches SUMO
// bit-exactly: v1 never leaves lane AB_1.
public class RungHDp2gKeepRightVetoParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "49-multilane-keepright");
    private const int End = 100;

    [Fact]
    public void KeepRightVeto_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(End);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    // Load-bearing keep-right-veto assertion (docs/HIGH-DENSITY-P2G-DESIGN.md §2/§4/§6): without the
    // fix, the engine spuriously keep-rights vehicle v1 from lane AB_1 to AB_0 at t=7 s because
    // ApplyKeepRightDecision applied the lane swap with no target-lane leader safety check. With the
    // fix, the swap is gated on IsTargetLaneSafe(v, neighLead, null, dt) and is correctly vetoed
    // (matching SUMO, which never moves v1 off lane AB_1 for the whole run). This assertion is
    // non-vacuous: it fails against the pre-fix engine (confirmed during authoring) and passes only
    // because the veto is in place.
    [Fact]
    public void KeepRightVeto_LeftLaneVehicleV1StaysOnLane1ForWholeRun()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(End);

        var points = actual.PointsFor("v1");
        Assert.NotEmpty(points);

        foreach (var (time, point) in points)
        {
            Assert.True(
                point.Lane == "AB_1",
                $"v1 left lane AB_1 at t={time} (lane={point.Lane}) -- the keep-right leader veto did not hold.");
        }
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"P2-G keep-right-veto parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
        };
        foreach (var attribute in result.Attributes)
        {
            lines.Add($"  attribute={attribute.Attribute} maxAbsError={attribute.MaxAbsError} rmse={attribute.Rmse} withinTolerance={attribute.WithinTolerance}");
        }
        if (result.PresenceMismatches.Count > 0)
        {
            lines.Add("  presence mismatches (first 10):");
            foreach (var mismatch in result.PresenceMismatches.Take(10))
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
