using System.Linq;
using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// GAP-3 off-lane exclusion follow-up (docs/ISSUE2-JUNCTION-KEEPCLEAR-DESIGN.md): the diagnosed root
// cause of the synthetic-grid "Issue 2" jam-teleports was NOT a junction right-of-way defect -- it
// was a park-and-stay car still acting as an on-lane blocker in several ActiveVehicles()-based scans
// that bypass the parked-excluding LaneNeighborQuery (RearmostOnLaneAmongActive/ActiveRearmost,
// BuildFoeApproachIndex's foe registration, FindRearmostOnLane's merge-target-lane leader, and a
// few occupancy/getFreeLane-style tallies). scenarios/70-parked-passable is a focused regression: a
// park-and-stay car parks in a roadside parkingArea (pa_JE) on the MAJOR through exit lane JE, just
// past priority junction J (the SAME W/E-major, N/S-minor cross geometry as the already-committed
// keepClear golden, scenario 38-keepclear-crosstraffic). 15 through-vehicles (W->J->E) must drive
// straight past the parked car without braking, and two isolated minor cross-vehicles (N->J->S,
// timed around the busy period so they meet an otherwise-empty junction -- see rou.rou.xml's own
// comment on why they are NOT interleaved with the through traffic) prove the minor movement is not
// permanently denied either. Golden from vanilla SUMO 1.20.0 confirms 0 jam-teleports and every
// through-vehicle at cruise speed (13.89 m/s) through the parked car's zone.
public class RungHDgap3ParkedPassableTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "70-parked-passable");
    private const int RunSteps = 220;

    // The primary parity gate: the full FCD trajectory (every through/cross vehicle, and the
    // park-and-stay blocker's own off-lane lateral offset while parked) must match the golden
    // within tolerance.json (pos/speed at 0.001).
    [Fact]
    public void ParkedPassable_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(RunSteps);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    // FUNCTIONAL (the design doc's decisive proof): every through-vehicle (W->J->E) must drive
    // straight past the parked car's position on JE_0 (~pos 15.6, inside the pa_JE bay 10-25)
    // WITHOUT its speed ever dropping to 0 there -- the pre-fix bug had it queue behind the
    // "phantom" on-lane blocker instead. Checked against the ACTUAL parked-car position read from
    // this same run (not hardcoded), inside a generous +-10m window around it.
    [Fact]
    public void ThroughTraffic_NeverStopsAtParkedCarsPosition()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var traj = engine.Run(RunSteps);

        var parkStayPoints = traj.PointsFor("parkStay");
        Assert.True(parkStayPoints.Count > 0, "parkStay never appeared in the trajectory.");
        var parkedPos = parkStayPoints.Values.First(p => p.Lane == "JE_0" && p.Speed < 0.01).Pos;
        Assert.InRange(parkedPos, 10.0, 25.0); // inside the pa_JE bay (startPos=10, endPos=25)

        for (var i = 1; i <= 15; i++)
        {
            var id = $"through{i}";
            var points = traj.PointsFor(id);
            Assert.True(points.Count > 0, $"{id} never appeared in the trajectory.");

            foreach (var (time, p) in points)
            {
                if (p.Lane != "JE_0" || Math.Abs(p.Pos - parkedPos) > 10.0)
                {
                    continue;
                }

                Assert.True(p.Speed > 1.0,
                    $"{id} stopped (speed={p.Speed}) at pos={p.Pos} time={time} near the parked car " +
                    $"(pos={parkedPos}) on JE_0 -- it was wrongly blocked by an off-lane parked vehicle.");
            }
        }
    }

    // FUNCTIONAL: the park-and-stay blocker is RESIDENT -- present in the trajectory every step from
    // the moment it parks (well before the run ends) through the final frame, matching FCD/residency
    // semantics -- and it never genuinely ARRIVES (it stops with duration=100000, far past the run's
    // end), so it must be absent from CompletedTrips. This guards against ever over-excluding a
    // parked vehicle from the export/"running" side of the engine while fixing the blocking side.
    [Fact]
    public void ParkedCar_IsResidentEveryStep_AndNeverCompletesATrip()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var traj = engine.Run(RunSteps);
        var points = traj.PointsFor("parkStay");

        for (var t = 5; t <= RunSteps - 1; t++)
        {
            Assert.True(points.ContainsKey((double)t), $"parkStay missing from the trajectory at t={t} -- a parked vehicle must stay resident (visible in FCD) every step, never dropped wholesale.");
        }

        Assert.DoesNotContain(engine.CompletedTrips, trip => trip.Id == "parkStay");
    }

    // The teleport-count gate: 0 jam-teleports over the run, matching golden.statistic.xml -- the
    // design doc's "no deadlock formed" bar. A pre-fix engine wedges the minor N->J->S movement
    // behind a phantom queue on J's internal lane once several through-vehicles pile up unable to
    // pass the (wrongly on-lane) parked car, eventually jam-teleporting.
    [Fact]
    public void JamTeleportCount_MatchesGoldenStatistic_NoDeadlockFormed()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        engine.Run(RunSteps);

        var golden = StatisticOutputParser.Parse(Path.Combine(ScenarioDir, "golden.statistic.xml"));

        Assert.Equal(0, golden.TeleportsJam);
        Assert.Equal(golden.TeleportsTotal, engine.TeleportCount);
        Assert.Equal(golden.TeleportsJam, engine.TeleportCountJam);
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"scenario 70-parked-passable FCD parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
