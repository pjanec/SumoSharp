using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// Rung ER5 behavioral (property) tests: give-way EXECUTION on a SINGLE wide lane (the fallback when
// no lane change is possible). A slow regular car (car0) drives on a 6 m single lane; a blue-light
// emergency vehicle (ev) catches up behind it. With no lane to change into, car0 pulls to the lane
// edge (a lateral drift reusing the B6 primitive, Engine.ComputeLateralEvasion's give-way arm), and
// the EV passes it within the lane (their lateral footprints no longer overlap -- the same
// FootprintsOverlap test B6 uses). car0 recenters after the EV clears. There is NO SUMO golden
// (SUMO's lane-based rescue cannot form a corridor on a single lane at all -- this is our
// enhancement). The lane centre is y = -3.00; positive drift is toward one edge.
public class RungER5GiveWayDriftTests
{
    private const double LaneCentreY = -3.00;
    private const double CarWidth = 1.80;   // passenger vClass default
    private const double EvWidth = 2.16;    // emergency vClass default
    private const double CarLength = 5.00;
    private const double EvLength = 6.50;

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

    private static TrajectorySet Run(string scenario, int steps)
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", scenario);
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(dir, "net.net.xml"),
            Path.Combine(dir, "rou.rou.xml"),
            Path.Combine(dir, "config.sumocfg"));
        return engine.Run(steps);
    }

    [Fact]
    public void CarPullsToLaneEdge_EmergencyPasses_NoCollision_ThenRecenters()
    {
        var traj = Run("55-giveway-drift", 45);
        var car0 = traj.PointsFor("car0").OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        var ev = traj.PointsFor("ev").OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        Assert.NotEmpty(car0);
        Assert.NotEmpty(ev);

        // car0 starts lane-centred, then pulls to the lane edge (a clear lateral drift) to give way.
        Assert.True(Math.Abs(car0[0].Y - LaneCentreY) < 1e-6, "car0 should start lane-centred");
        var maxDrift = car0.Max(p => Math.Abs(p.Y - LaneCentreY));
        Assert.True(maxDrift > 1.5, $"car0 never pulled meaningfully aside (max drift {maxDrift:F2} m)");

        // The EV starts behind car0 and ends up ahead (it got past on the single lane).
        Assert.True(ev[0].Pos < car0[0].Pos, "EV should start behind car0");
        Assert.True(ev[^1].Pos > car0[^1].Pos, "EV should end ahead of car0 (it passed within the lane)");

        // No physical overlap: at no timestep do the two footprints overlap BOTH longitudinally and
        // laterally at once (they share the single lane during the pass, kept apart by the drift).
        foreach (var c in car0)
        {
            if (!traj.TryGet("ev", c.Time, out var e))
            {
                continue;
            }

            var longitudinalOverlap = c.Pos - CarLength < e.Pos && e.Pos - EvLength < c.Pos;
            var lateralOverlap = Math.Abs(c.Y - e.Y) < (CarWidth + EvWidth) / 2.0;
            Assert.False(longitudinalOverlap && lateralOverlap,
                $"collision at t={c.Time}: car0 pos={c.Pos:F1} y={c.Y:F2}, ev pos={e.Pos:F1} y={e.Y:F2}");
        }

        // car0 returns to the lane centre after the EV has passed (recenters).
        Assert.True(Math.Abs(car0[^1].Y - LaneCentreY) < 1e-6,
            $"car0 did not recenter (final y {car0[^1].Y:F2}, expected {LaneCentreY})");
    }

    [Fact]
    public void CarNeverHardBrakes_WhileGivingWay()
    {
        // The give-way drift + lateral pass must not make car0 slam on the brakes as the EV draws
        // alongside (an early bug: car0 saw the passing EV as a same-lane leader with negative gap).
        // car0's maxSpeed is 6; once up to speed it should stay there, never dropping to a stop.
        var traj = Run("55-giveway-drift", 45);
        var car0 = traj.PointsFor("car0").OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        var cruising = car0.Where(p => p.Time >= 5).ToList();
        Assert.NotEmpty(cruising);
        Assert.All(cruising, p => Assert.True(p.Speed > 1.0,
            $"car0 hard-braked at t={p.Time} (speed {p.Speed:F2}) while giving way"));
    }
}
