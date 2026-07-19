using System.Linq;
using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Issue 1 (docs/SUMOSHARP-SERVE-PATH-DROP-IN.md par 7, park-and-stay residency): the no-cheating
// sink pattern is a vehicle with `<stop parkingArea="pa0" duration="100000"/>` on its final edge --
// duration far outlasting the sim end means the vehicle must park and STAY resident, off-lane, for
// the whole run: it never "arrives", never appears in tripinfo, and stays in the FCD at its parked
// off-lane slot every step. scenarios/69-parkandstay-lc isolates the bug the acceptance run found:
// on a 2-lane edge with the parkingArea on lane 0, a park-and-stay car that departs on lane 1 must
// strategically lane-change toward the stop's lane in time to brake for it -- pre-fix, the engine
// never made that lane change (StopLineConstraint only brakes when the stop's lane already equals
// the vehicle's own lane, and the strategic lane-changer only ever targeted the route-continuation
// pool lane, never a same-edge stop's lane), so the car drove off the end of its final edge and was
// wrongly marked ARRIVED. The fix: TryStrategicLaneChange (Engine.cs) now overrides its target lane
// to a pending same-edge stop's lane, bound by distance-to-stop; a residency guard at the final-edge
// arrival site additionally refuses to arrive a vehicle with a pending unreached parkingArea stop.
//
// This scenario's short lead distance (stop at pos 40 on a 120m edge) is deliberate: it is far too
// short for the engine's independent keep-right accumulator (about 5-6s to fire, see scenarios/07)
// to have reached lane 0 in time on its own, so a pass here is real evidence of the STRATEGIC fix,
// not a keep-right coincidence -- confirmed by reverting the Engine.cs fix locally and observing
// park0 wrongly complete at t=11 on lane e0_1 (see this class's own PR description / session report
// for the before/after FCD dump).
public class RungHDgap3ParkAndStayResidencyTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "69-parkandstay-lc");
    private const int Steps = 30; // matches config.sumocfg's <end value="30"/> at step-length 1s.

    [Fact]
    public void ParkAndStay_LoadsViaCfg_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(Steps);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    // The core regression: park0 must reach lane e0_0 (the parkingArea's lane) and settle there --
    // matching the golden's own settled slot (pos=47.499, lane e0_0) rather than driving off the
    // end of e0_1 (its wrong departure lane).
    [Fact]
    public void ParkAndStay_Vehicle_ReachesParkingLaneAtGoldenSlot()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var traj = engine.Run(Steps);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));

        var lastTime = (double)(Steps - 1);
        var actualLast = traj.PointsFor("park0")[lastTime];
        var goldenLast = golden.PointsFor("park0")[lastTime];

        Assert.Equal("e0_0", actualLast.Lane);
        Assert.Equal(goldenLast.Lane, actualLast.Lane);
        Assert.Equal(goldenLast.Pos, actualLast.Pos, 3);
        Assert.Equal(0.0, actualLast.Speed, 3);
    }

    // RESIDENCY (the actual bug): park0 must never appear in engine.CompletedTrips (it never
    // arrives) and must be present in the trajectory at EVERY step from t=0 through the LAST step
    // (Steps-1) -- i.e. it never dropped out of the running set, exactly like the golden (which has
    // an empty <tripinfos/> for this scenario -- vanilla SUMO agrees it never arrives either).
    [Fact]
    public void ParkAndStay_Vehicle_NeverArrivesAndStaysInTrajectoryEveryStep()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var traj = engine.Run(Steps);

        // Not in CompletedTrips: the direct assertion that the engine never marked it Arrived.
        Assert.DoesNotContain(engine.CompletedTrips, t => t.Id == "park0");

        // Present at EVERY step 0..Steps-1 -- proves it stayed in the active/"running" set the
        // whole horizon rather than silently vanishing partway (the pre-fix bug: present through
        // t=10, then GONE for t=11..29 because it wrongly arrived and left the active set).
        var points = traj.PointsFor("park0");
        for (var step = 0; step < Steps; step++)
        {
            var t = (double)step;
            Assert.True(points.ContainsKey(t), $"park0 missing from trajectory at t={t} -- it dropped out of the running set.");
        }

        // Its own last-step presence (peak concurrent 'running' includes it through the final step):
        // parked, off-lane (nonzero lateral offset -> y != lane-centre -1.6), speed 0.
        var last = points[(double)(Steps - 1)];
        Assert.Equal(0.0, last.Speed, 3);
        Assert.NotEqual(-1.6, last.Y, 3);

        // Golden parity for "never arrives": vanilla SUMO's own tripinfo for this scenario is
        // empty (see golden.tripinfo.xml) -- park0 does not appear there either.
        var goldenTripinfo = TripInfoParser.Parse(Path.Combine(ScenarioDir, "golden.tripinfo.xml"));
        Assert.DoesNotContain(goldenTripinfo, t => t.Id == "park0");
        Assert.Empty(goldenTripinfo);
    }

    // Parked well before the stop's braking position is overrun: once settled, park0 stays fully
    // stopped and resident from the step it parks (t=8, see golden.fcd.xml) through the last step --
    // guards against a resume-then-arrive regression (a duration=100000 stop must never resume
    // early).
    [Fact]
    public void ParkAndStay_Vehicle_StaysStoppedFromParkUntilLastStep()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var traj = engine.Run(Steps);
        var points = traj.PointsFor("park0");

        for (var step = 8; step < Steps; step++)
        {
            var p = points[(double)step];
            Assert.Equal("e0_0", p.Lane);
            Assert.Equal(0.0, p.Speed, 3);
        }
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"scenario 69 park-and-stay parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
