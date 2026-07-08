using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Rung C6-ii (actuated / detector-driven TLS): the first STATEFUL traffic-light program. Junction
// J runs an actuated program (2 green phases Gr/rG, minDur 5 / maxDur 50, 3s yellows). Four N-S
// vehicles (ns0..ns3) stream over the SJ induction loop, so phase 0 (Gr) EXTENDS past its minDur
// of 5 -- the gap-based algorithm keeps re-detecting a vehicle within max-gap 3.0s until the
// stream thins, ending phase 0 at t=13 (vs the static duration=42). ew0 waits at the WJ red and is
// released when phase 2 (rG) turns green at t=16. scenarios/35-actuated-tls's golden.fcd.xml (from
// SUMO 1.20.0) is the ground truth; scenarios/35-actuated-tls/phase-timeline.txt records the
// golden phase sequence that this port must reproduce (0->1 at 13, 1->2 at 16, 2->3 at 21, ...).
//
// Port = Sim.Core.ActuatedTrafficLightLogic (MSActuatedTrafficLightLogic gap-based algorithm +
// MSInductLoop): a per-TLS phase machine advanced each step before PlanMovements, reading
// induction loops fed from ExecuteMoves. A static program is completely unaffected (RedLightConstraint
// keeps the pure-function TrafficLightState path), so every pre-C6 scenario stays byte-identical.
public class RungC6iiActuatedTlsParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "35-actuated-tls");

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
            $"Rung-C6-ii actuated-TLS parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
