using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Phase 2 / P2.3 (single-vehicle lateral drift -- the "rung 1" of the sublane lateral axis, exact
// @1e-3 on posLat). scenarios/60-sublane-drift: lateral-resolution=0.8 (sublane model ON), one
// passenger (width 1.8) on ONE wide lane (width 4.8, centre y=-2.40), latAlignment="right". SUMO's
// MSLCM_SL2015 steers it from the centred insertion toward the right lane edge at maxSpeedLat
// (default 1.0 m/s):
//     posLat: 0 -> -1.0 (step 1) -> -1.4995 (step 2) -> holds.
// Target 1.4995 = halfLaneWidth - halfVehWidth where halfVehWidth uses MSLCM_SL2015::getWidth() =
// vType.Width + NUMERICAL_EPS (the 1.4995-vs-1.5 detail). Longitudinal (pos/speed) is identical to
// free-flow -- the drift does not perturb car-following here, isolating the lateral integration.
//
// Port = Engine.ComputeSublaneLateral (gated on _sublane == lateral-resolution > 0) + the vType
// maxSpeedLat/latAlignment resolution; the LatOffset carrier (MoveIntent -> Kinematics) and posLat
// emit already existed (Seam 4 / P2.0). NON-VACUOUS: the golden's posLat is nonzero at t>=1, so the
// engine must actually drive the lateral offset (a lane-centred posLat==0 fails at step 1). INERT:
// gated on lateral-resolution>0, so every phase-1 scenario keeps posLat==0 and is byte-identical.
// Deferred to P2.2+: the per-sublane neighbour query (multi-vehicle safe-lat clamp) and the
// non-fixed alignments (nice/compact/arbitrary). Runs 30 steps (t=0..29).
public class RungP23SublaneDriftTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "60-sublane-drift");

    [Fact]
    public void Run30Steps_MatchesGoldenFcdWithinTolerance_IncludingPosLat()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(30);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        // tolerance.json lists posLat in comparedAttributes, so the lateral axis is checked at 1e-3.
        Assert.Contains("posLat", tolerance.ComparedAttributes);

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"Rung-P2.3 sublane-drift parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
