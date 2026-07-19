using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Regression guard for P2-G Bug-1 (ScenarioConfigParser <routing>-section fallback). SUMO's
// canonical sumocfg layout puts the rerouting-device / routing options in a dedicated <routing>
// section; SumoData's serve-path configs do too. Before the fix, ScenarioConfigParser read
// device.rerouting.* / routing-algorithm ONLY from <processing>, so those keys were silently
// ignored for a canonical config and device.rerouting never equipped the vehicle -- rerouting
// was inert and vehicles could not route around jams vanilla routes around.
//
// scenarios/74-reroute-routing-section is scenarios/73-reroute-routelength's exact net + demand,
// with the ONLY difference being that the four keys (device.rerouting.probability / .period /
// .adaptation-steps / routing-algorithm) live under <routing> instead of <processing>. Vanilla
// SUMO 1.20.0 reads options by NAME regardless of the sumocfg section, so this scenario's golden
// trajectory body is byte-identical to 73's (verified at regeneration) and the rerouted vehicle
// takes the same detour with routeLength 756.21.
//
// The test is NON-VACUOUS: the scenario is built (via 73's "blocker" vehicle stopped on e_short
// for the whole run) so that WITHOUT rerouting the vehicle drives straight into the stopped
// blocker on e_short and never reaches e_dest -- a gross FCD divergence from the golden's detour.
// So if the parser ever regresses to <processing>-only, device.rerouting goes inert here and this
// test fails hard. Confirmed by temporarily reverting the parser fix: the rerouted vehicle then
// stalls behind the blocker and FCD parity fails at the first junction.
public class RungP2gBug1RoutingSectionRerouteTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "74-reroute-routing-section");
    private const int Steps = 120; // matches config.sumocfg's <end value="120"/> at step-length 1s.

    // Same value as scenarios/73-reroute-routelength: the full driven path
    // e_src1 e_src2 e_det1 e_det2 e_dest (rerouting active, detour taken).
    private const double GoldenRouteLength = 756.21;

    [Fact]
    public void Run120Steps_RoutingSectionReroute_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(Steps);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    [Fact]
    public void Run120Steps_RoutingSectionReroute_ActivatesRerouting_AndTakesDetour()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        engine.Run(Steps);

        // Exactly one completed trip, and it genuinely arrived (route-end) -- not stalled behind
        // the blocker (which is where an inert-rerouting regression lands it).
        var arrivedIds = engine.CompletedTrips.Select(t => t.Id).ToList();
        Assert.Equal(new[] { "rerouted" }, arrivedIds);

        var trip = engine.CompletedTrips.Single(t => t.Id == "rerouted");

        var golden = TripInfoParser.Parse(Path.Combine(ScenarioDir, "golden.tripinfo.xml"));
        Assert.Single(golden);
        var goldenTrip = golden[0];

        // The golden itself must carry the rerouting signature (guards against a vacuous pass if
        // the golden were ever mis-regenerated without device.rerouting active).
        Assert.NotNull(goldenTrip.RouteLength);
        Assert.Equal(GoldenRouteLength, goldenTrip.RouteLength!.Value, precision: 2);

        // The engine must reach the SAME detour arrival as vanilla: only possible if the <routing>
        // section was honored and the vehicle rerouted off e_short onto e_det1/e_det2/e_dest.
        Assert.Equal(goldenTrip.RouteLength!.Value, trip.RouteLength, tolerance: 1.0);
        Assert.NotNull(goldenTrip.ArrivalPos);
        Assert.Equal(goldenTrip.ArrivalPos!.Value, trip.ArrivalPos, tolerance: 0.05);
        Assert.Equal(goldenTrip.ArrivalLane, trip.ArrivalLane);
        Assert.Equal("e_dest_0", trip.ArrivalLane);
    }

    // The golden must record the reroute (SUMO only attaches routing_rerouted when device.rerouting
    // equips the vehicle -- i.e. only if it read the <routing> section) and arrival on the detour.
    [Fact]
    public void GoldenTripinfo_RecordsRerouteFromRoutingSection()
    {
        var xml = File.ReadAllText(Path.Combine(ScenarioDir, "golden.tripinfo.xml"));
        Assert.Contains("rerouteNo=\"1\"", xml);
        Assert.Contains("routing_rerouted", xml);
        Assert.Contains("arrivalLane=\"e_dest_0\"", xml);
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"scenario 74 routing-section-reroute FCD parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
