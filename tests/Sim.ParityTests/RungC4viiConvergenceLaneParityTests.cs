using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Rung C4-vii-c (route->lane over-constraint / convergence-clamp fix). scenarios/46-convergence-lane
// is the minimal, single-vehicle, deterministic anchor for the -L2 grid FLOW blocker. A straight
// 2-lane corridor A->B->C->D; one vehicle v0 departs on AB lane 1 and keep-rights onto AB lane 0
// (Rechtsfahrgebot, empty road). Its route pool resolves AB's exit to lane 1 (from its depart lane),
// but lane 0 ALSO connects straight to BC -- so at the AB lane end v0 sits on an equally-valid
// connecting lane that is not the pool lane.
//
// SUMO reference: v0 keep-rights AB_1->AB_0 (t=6), crosses AB_0->BC_0 (t=24) and BC_0->CD_0 (t=46)
// and is still in free-flow transit on CD at the run end (t=59, pos 188.9) -- both junction
// crossings exercised, no arrival-frame timing to reconcile. ENGINE BUG this anchor pins
// (baseline 8bd4beb): the C2-ii boundary-convergence guard clamped v0 at the AB lane end (AB_0,
// pos 300, speed 0) FOREVER because its lane != the pool's resolved exit lane -- even though that
// lane connects onward. This is the dominant cause of the committed -L2 diag grid's gridlock (29 of
// 38 stuck vehicles were this exact clamp). FIX (Engine.TryReResolveFromActualLane): when a vehicle
// reaches a lane end on a non-pool lane that STILL connects to the next route edge, re-resolve the
// remaining route from that lane (pinning the edge's exit to it) and proceed, matching SUMO's "follow
// whatever connection leaves the current lane, lane-change toward bestLaneOffset opportunistically".
//
// PARITY SCOPE: compared attributes are ["pos","speed"] (see tolerance.json) -- pos/speed match SUMO
// EXACTLY (max-abs error < 1e-12 across all 60 frames). "lane" is deliberately excluded: v0's
// longitudinal motion is identical, but its keep-right AB_1->AB_0 TIMING differs (SUMO t=6, engine
// t=19) -- a pre-existing keep-right-fidelity gap (see C4-vii-b) orthogonal to this fix. The pre-fix
// engine strands v0 at AB_0 pos 300 speed 0 from ~t=23, so its pos diverges from the golden by
// ~100 m for the rest of the run -- a decisive, non-vacuous failure of this exact anchor.
public class RungC4viiConvergenceLaneParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "46-convergence-lane");

    [Fact]
    public void Run60Steps_MatchesGoldenPosSpeed()
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
            $"Rung-C4-vii-c convergence-lane parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
