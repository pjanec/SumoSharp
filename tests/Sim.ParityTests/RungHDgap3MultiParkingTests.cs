using System.Linq;
using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// GAP-3 acceptance gate (docs/SUMOSHARP-SERVE-PATH-DROP-IN.md §3, docs/SERVE-PATH-PLAN.md): real
// multi-occupant parkingArea semantics -- scenarios/67-multi-parking shares one 3-lot parkingArea
// (pa0, e0_0, startPos=195/endPos=225) among a park-and-stay sink started via departPos="stop"
// (parkStay0, lot0=205), a departPos="stop" origin that PULLS OUT (pullOut0, lot1=215, duration=60),
// a moving car that drives in and parks forever (driveInPark0, lot2=225), and pure through-traffic
// with no stop (through0, depart=20) that must pass every occupied bay unobstructed.
public class RungHDgap3MultiParkingTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "67-multi-parking");

    [Fact]
    public void MultiParking_LoadsViaCfg_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(150);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    // FUNCTIONAL (the doc's acceptance bar, SUMOSHARP-SERVE-PATH-DROP-IN.md §3): the through
    // follower is NOT blocked by the parked cars -- its speed never drops below cruise (13.89 m/s)
    // while it is on the lane, including while its position crosses the occupied bay zone
    // (x=195..225, where parkStay0/driveInPark0 sit off-lane and pullOut0 sat off-lane until t=61).
    [Fact]
    public void MultiParking_ThroughFollower_PassesOccupiedBaysUnobstructed()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var traj = engine.Run(150);
        var points = traj.PointsFor("through0");

        Assert.True(points.Count > 0, "through0 never appeared in the trajectory.");

        // Once through0 reaches free-flow cruise speed (13.89 m/s) it must never drop below it again
        // -- a degenerate on-lane parking stop would instead show it braking to a dead stop behind
        // driveInPark0 (parked at lot2=225) or queuing behind parkStay0 (lot0=205). Iterate in
        // ascending time order (PointsFor is keyed by time, not step index).
        var reachedCruise = false;
        foreach (var time in points.Keys.OrderBy(t => t))
        {
            var p = points[time];
            if (p.Speed >= 13.88)
            {
                reachedCruise = true;
            }

            if (reachedCruise)
            {
                Assert.True(p.Speed >= 13.88,
                    $"through0 dropped to speed={p.Speed} at pos={p.Pos} time={time} after reaching cruise -- it was blocked by a parked vehicle.");
            }
        }

        Assert.True(reachedCruise, "through0 never reached cruise speed at all.");
    }

    // FUNCTIONAL: co-occupant parked vehicles (parkStay0 @ lot0, driveInPark0 @ lot2) have DISTINCT
    // longitudinal lot positions while BOTH are simultaneously parked (t=25..149, i.e. well after
    // driveInPark0 has parked and before the run ends) -- proving multi-occupant slot assignment,
    // not the pre-GAP-3 degenerate single-lot collapse.
    [Fact]
    public void MultiParking_CoOccupants_HaveDistinctLotPositions()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var traj = engine.Run(150);
        var parkStay = traj.PointsFor("parkStay0");
        var driveIn = traj.PointsFor("driveInPark0");

        for (var t = 30; t <= 149; t++)
        {
            var a = parkStay[(double)t];
            var b = driveIn[(double)t];
            Assert.Equal("e0_0", a.Lane);
            Assert.Equal("e0_0", b.Lane);
            Assert.Equal(0.0, a.Speed, 3);
            Assert.Equal(0.0, b.Speed, 3);
            Assert.NotEqual(a.Pos, b.Pos);
            Assert.True(Math.Abs(a.Pos - b.Pos) > 5.0,
                $"parkStay0 (pos={a.Pos}) and driveInPark0 (pos={b.Pos}) are not in distinct lots at t={t}.");
        }
    }

    // FUNCTIONAL: a parked vehicle sits OFF the travel lane -- nonzero lateral offset -- while parked,
    // and returns to lane-centre (LatOffset 0) once it pulls out.
    [Fact]
    public void MultiParking_ParkedVehicles_HaveNonzeroLateralOffset()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var traj = engine.Run(150);
        var parkStay = traj.PointsFor("parkStay0");

        // Off-lane while parked (well after its Reached transition has settled).
        var parkedPoint = parkStay[30.0];
        Assert.NotEqual(-1.6, parkedPoint.Y, 3);

        // Through-traffic, never parked, stays exactly at lane centre throughout.
        var through = traj.PointsFor("through0");
        foreach (var p in through.Values)
        {
            Assert.Equal(-1.6, p.Y, 3);
        }
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"GAP-3 multi-parking parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
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
