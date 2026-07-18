using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Regression guard for the tripinfo routeLength device.rerouting fix (Engine.cs commit "Fix
// tripinfo routeLength across device.rerouting reroutes"): a device.rerouting periodic reroute
// REBUILDS the vehicle's lane-sequence pool starting at its CURRENT edge, discarding every
// earlier edge from that pool. The prior routeLength formula re-summed the CURRENT pool, so all
// distance travelled BEFORE a mid-trip reroute was silently lost (SumoData found rerouted trips
// reporting 0.33-0.49x their true routeLength). The fix replaced it with a running per-vehicle
// accumulator (VehicleRuntime.RouteDistanceTraveled) seeded at insertion and grown at the ONE
// lane-boundary-crossing site in ExecuteMoveVehicle, which survives a pool rebuild because it
// never re-reads the pool.
//
// scenarios/73-reroute-routelength (golden regenerated from real SUMO 1.20.0, sentinel-gated via
// `.wants-tripinfo`) is built so the bug is NOT masked -- and, critically, so the reroute is a
// genuine MID-TRIP one, not a pre-insertion one (SUMO's own device.rerouting -- and this engine's
// P1E-6 PreInsertionReroute -- also runs a ONE-TIME route computation at insertion, before the
// vehicle ever moves; if that pass alone had picked the detour, the vehicle's lane-sequence pool
// would never need a later rebuild and the bug this scenario guards would not be exercised. An
// earlier version of this scenario made that mistake -- see rou.rou.xml's header comment for the
// full account). Instead: a second vehicle "blocker" is inserted directly onto e_short and
// immediately stopped there for the whole run, so e_short looks like the genuinely faster path
// (150m @ 13.89 m/s, effort ~10.8s, vs the two-edge detour e_det1+e_det2's ~25.8s) at "rerouted"'s
// own insertion (t=0, before "blocker"'s presence has been sampled into the smoothed edge
// weights) -- so pre-insertion routing correctly leaves "rerouted" on e_short. Only over the next
// few seconds does "blocker"'s stopped-vehicle congestion (sampled by RerouteEdgeWeights.Update,
// device.rerouting.adaptation-steps=3 for fast convergence) drag e_short's smoothed effort far
// past the detour's, so "rerouted"'s first PERIODIC reroute (period=8s, jitter off, fires at
// exactly t=8s) is the one that switches -- confirmed by golden vehroute output showing
// replacedAtTime="8.00" replacedOnEdge="e_src2" (not t=0). The O->J prefix is deliberately split
// into TWO edges (e_src1, e_src2) so "rerouted" has already fully LEFT e_src1 -- folding its
// length into RouteDistanceTraveled -- before that t=8s reroute fires while it is still on e_src2
// (per golden.fcd.xml: e_src1 is left ~t=6-7s, J is not reached until ~t=15-16s). That periodic
// reroute rebuilds the lane-sequence pool starting at e_src2, dropping e_src1 out of it entirely:
// exactly the pool-rebuild the old formula mishandled (independently confirmed by temporarily
// reverting Engine.cs/VehicleRuntime.cs to the pre-fix pool-sum formula and re-running this
// scenario: it reports RouteLength=696.11, short of the golden's 756.21 by precisely e_src1 +
// its internal junction lane, 60.00 + 0.10 = 60.10 m).
public class RungHDgap2RerouteRouteLengthTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "73-reroute-routelength");
    private const int Steps = 120; // matches config.sumocfg's <end value="120"/> at step-length 1s.

    // golden.tripinfo.xml's own routeLength for "rerouted": 756.210000 m. This is
    // e_src1(60.00) + :P_0(0.10) + e_src2(127.49) + :J_0(8.56) + e_det1(176.02) + :K_0(1.97) +
    // e_det2(176.02) + :M_0(8.56) + arrivalPos(197.49) = 756.21 -- confirmed by hand-summing
    // net.net.xml's lane lengths along the ACTUAL driven path (e_src1 e_src2 e_det1 e_det2
    // e_dest), i.e. it counts e_src1 (pre-reroute) as well as the post-reroute detour.
    private const double GoldenRouteLength = 756.21;

    // What the OLD pool-sum-only formula would have reported for this exact trip: at the moment
    // the periodic reroute fires (t=8s, vehicle on e_src2), RegisterRerouted/RegisterPeriodicReroute
    // rebuilds the lane-sequence pool starting at the vehicle's CURRENT edge (e_src2) -- e_src1 and
    // its internal junction lane :P_0 are never in the rebuilt pool. Summing only from e_src2
    // onward: e_src2(127.49) + :J_0(8.56) + e_det1(176.02) + :K_0(1.97) + e_det2(176.02) +
    // :M_0(8.56) + arrivalPos(197.49) = 696.11 -- short of the true 756.21 by exactly e_src1 +
    // :P_0 (60.00 + 0.10 = 60.10). This is NOT a rounding-scale gap (60.10 m on a 756.21 m trip,
    // ~8%): it is the exact signature the bug fix guards against, so this constant is asserted
    // against below to prove the test would have failed pre-fix.
    private const double OldFormulaRouteLength = 696.11;

    [Fact]
    public void Run120Steps_ReroutedVehicle_MatchesGoldenFcdWithinTolerance()
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
    public void Run120Steps_ReroutedVehicle_ArrivesWithRouteLengthIncludingPreRerouteDistance()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        engine.Run(Steps);

        // The vehicle must have genuinely arrived (route-end), not stalled/vaporized -- exactly
        // one completed trip, this one.
        var arrivedIds = engine.CompletedTrips.Select(t => t.Id).ToList();
        Assert.Equal(new[] { "rerouted" }, arrivedIds);

        var trip = engine.CompletedTrips.Single(t => t.Id == "rerouted");

        var golden = TripInfoParser.Parse(Path.Combine(ScenarioDir, "golden.tripinfo.xml"));
        Assert.Single(golden);
        var goldenTrip = golden[0];

        // Pin the golden itself carries real, non-degenerate values (guards against a vacuous
        // pass if the golden were ever mis-regenerated).
        Assert.NotNull(goldenTrip.RouteLength);
        Assert.Equal(GoldenRouteLength, goldenTrip.RouteLength!.Value, precision: 2);

        // The core regression assertion: engine.CompletedTrips' RouteLength (the running
        // accumulator) must match vanilla SUMO's tripinfo routeLength within a small absolute
        // tolerance. Both engines pick the SAME reroute (the effort gap is decisive at pure
        // free-flow, not congestion-dependent, so there is no path-choice ambiguity to allow for)
        // and the shared e_src1/e_src2 prefix is traversed identically regardless of which engine
        // decided the switch when -- so a tight, non-loosened 1.0 m tolerance is appropriate here
        // (matching the task briefing's suggested scale), not the wider allowance that would be
        // needed if the two engines could plausibly diverge onto different reroute paths.
        Assert.Equal(goldenTrip.RouteLength!.Value, trip.RouteLength, tolerance: 1.0);

        // Duration/ArrivalLane/ArrivalPos must also match -- confirms the vehicle actually
        // completed the SAME physical trip as the golden, not just a coincidentally-close
        // routeLength number.
        Assert.NotNull(goldenTrip.ArrivalTime);
        Assert.Equal(goldenTrip.ArrivalTime!.Value, trip.Arrival, tolerance: 0.05);
        Assert.Equal(goldenTrip.ArrivalLane, trip.ArrivalLane);
        Assert.NotNull(goldenTrip.ArrivalPos);
        Assert.Equal(goldenTrip.ArrivalPos!.Value, trip.ArrivalPos, tolerance: 0.05);

        // The regression guard itself: the OLD pool-sum-only formula would have reported
        // OldFormulaRouteLength (696.11, short by e_src1's length + its internal junction lane --
        // see the constant's own header comment) for this exact trip. The asserted RouteLength
        // must be MATERIALLY larger than that -- proving this test actually exercises (and would
        // have failed under) the pre-fix formula, not just re-deriving a coincidentally-similar
        // number.
        Assert.True(
            trip.RouteLength > OldFormulaRouteLength + 30.0,
            $"routeLength={trip.RouteLength} is too close to the OLD pool-sum-only formula's " +
            $"value ({OldFormulaRouteLength}) to prove the pre-reroute e_src1 distance is " +
            "actually included -- this scenario would not have caught the bug.");

        // And the golden itself must exhibit the same signature (confirms the golden was
        // generated by a SUMO that counts the full route, and that our hand-derived
        // OldFormulaRouteLength isn't simply wrong).
        Assert.True(
            goldenTrip.RouteLength!.Value > OldFormulaRouteLength + 30.0,
            "golden.tripinfo.xml routeLength is unexpectedly close to the old-formula value -- " +
            "re-check the scenario's golden regeneration.");
    }

    // Independent confirmation (from vanilla SUMO's own --vehroute-output convention, mirrored
    // here via the tripinfo rerouteNo field) that a reroute actually fired for this vehicle -- not
    // just a routeLength number that happens to be large. golden.tripinfo.xml's own devices field
    // includes "routing_rerouted" (SUMO only attaches the routing device when device.rerouting
    // equips the vehicle) and rerouteNo="1".
    [Fact]
    public void GoldenTripinfo_RecordsExactlyOneReroute()
    {
        var goldenPath = Path.Combine(ScenarioDir, "golden.tripinfo.xml");
        var xml = File.ReadAllText(goldenPath);

        Assert.Contains("rerouteNo=\"1\"", xml);
        Assert.Contains("routing_rerouted", xml);

        // And the vehicle's ARRIVAL lane is on the detour's final edge, e_dest -- reached via
        // e_det1/e_det2, never e_short (the "blocker" vehicle sits stopped on e_short for the
        // whole run, so "rerouted" could not have driven through it without a collision/blocked
        // trajectory that would itself fail the FCD parity test above).
        Assert.Contains("arrivalLane=\"e_dest_0\"", xml);
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"scenario 73 reroute-routelength FCD parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
