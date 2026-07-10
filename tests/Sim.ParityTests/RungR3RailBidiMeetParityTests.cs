using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Rung R3 (rail support) trajectory parity test: single-track BIDIRECTIONAL meet. Two rail trains
// depart at t=0 from opposite ends of one physical track (edges AB and BA, each the other's bidi=).
// With no rail signal, SUMO's no-signal deadlock guard (MSLane::isInsertionSuccess bidi check,
// MSLane.cpp:843/999) holds the second train at INSERTION until the first has cleared the shared
// track: trainBA cannot enter BA while its bidi partner AB carries trainAB, so it inserts only
// once trainAB exits (~t=89). This proves (a) the net.xml `bidi` attribute is ingested
// (Edge.BidiEdgeId / NetworkModel.TryGetBidiLaneId) and (b) the rail bidi insertion hold
// reproduces SUMO exactly. NON-VACUOUS: the pre-port engine ignores bidi entirely and would insert
// both trains at t=0, running them head-on THROUGH each other -- a presence mismatch at t=0..88 and
// a position collision. Exact @1e-3 on lane,pos,speed. Mirrors RungA1ParityTests, pointed at
// scenarios/49-rail-bidi-meet.
public class RungR3RailBidiMeetParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "49-rail-bidi-meet");

    [Fact]
    public void Run180Steps_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(180);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"Rung-R3 bidi-meet parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
