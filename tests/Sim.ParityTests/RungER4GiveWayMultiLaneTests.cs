using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// Rung ER4 behavioral (property) tests: give-way EXECUTION on a multi-lane road. A slow regular
// car (car0) drives in the right lane e0_0 of a 2-lane edge; a blue-light emergency vehicle (ev)
// catches up in the same lane. car0 vacates its lane by changing to e0_1 so the EV can pass in
// e0_0 (Engine.TryGiveWayLaneChange, reusing the ordinary lane-change machinery). car0 starts in
// the right lane precisely so that ordinary keep-right/speed-gain would never move it -- making the
// give-way lane change the only possible cause (non-vacuous). There is NO SUMO golden.
public class RungER4GiveWayMultiLaneTests
{
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
    public void RegularCar_VacatesItsLane_ForApproachingEmergencyVehicle_ThenEmergencyPasses()
    {
        var traj = Run("54-giveway-multi", 40);
        var car0 = traj.PointsFor("car0").OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        var ev = traj.PointsFor("ev").OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        Assert.NotEmpty(car0);
        Assert.NotEmpty(ev);

        // car0 starts in the right lane e0_0 ...
        Assert.Equal("e0_0", car0[0].Lane);
        // ... and vacates it (changes to the left lane e0_1) at some point to clear the way.
        Assert.Contains(car0, p => p.Lane == "e0_1");

        // The EV starts behind car0 and ends up ahead of it (it got past).
        Assert.True(ev[0].Pos < car0[0].Pos, "EV should start behind car0");
        Assert.True(ev[^1].Pos > car0[^1].Pos, "EV should end ahead of car0 (it passed)");

        // No collision: whenever the two share a lane at the same timestep, they are separated
        // longitudinally by at least a vehicle length (they are in DIFFERENT lanes during the pass).
        foreach (var c in car0)
        {
            if (traj.TryGet("ev", c.Time, out var e) && e.Lane == c.Lane)
            {
                Assert.True(Math.Abs(e.Pos - c.Pos) > 6.5,
                    $"collision at t={c.Time}: car0 and ev both on {c.Lane} at {c.Pos:F1}/{e.Pos:F1}");
            }
        }
    }

    [Fact]
    public void EmergencyVehicle_HoldsItsLane_WhilePassing()
    {
        // The bluelight EV makes no overtaking lane change of its own -- it stays in e0_0 the whole
        // run and relies on car0 clearing (so the give-way, not the EV's own speed-gain, is what
        // opens the path).
        var traj = Run("54-giveway-multi", 40);
        var ev = traj.PointsFor("ev").Select(kv => kv.Value).ToList();
        Assert.NotEmpty(ev);
        Assert.All(ev, p => Assert.Equal("e0_0", p.Lane));
    }
}
