using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// Rung OV3 behavioral (property) tests: opposite-direction overtake EXECUTION. A held-up fast
// lcOpposite vehicle spills laterally toward the oncoming lane (reusing the B6 lateral drift), passes
// the slow leader (via the same-lane !FootprintsOverlap leader bypass), and returns to its lane once
// past it / when the gap acceptance drops the intent. Safety comes from OV2's gap acceptance (the
// overtake only commits with room to complete + return before the oncoming arrives). Asserted:
// the overtaker gets ahead of the leader, recenters, and NEVER physically overlaps another vehicle.
// Collisions are checked directly in the exported world (X, Y). No SUMO golden.
public class RungOV3OvertakeExecutionTests
{
    private const double LaneCentreYAB = -1.60; // edge AB lane centre
    private const double CarLen = 5.0;          // passenger length
    private const double CombinedHalfWidth = 1.8; // ~ (1.8+1.8)/2 body half-widths sum

    private static TrajectorySet Run(string rouFile, int steps)
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", "57-overtake-opposite");
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(dir, "net.net.xml"),
            Path.Combine(dir, rouFile),
            Path.Combine(dir, "config.sumocfg"));
        return engine.Run(steps);
    }

    private static void AssertNoCollisions(TrajectorySet traj)
    {
        // Two vehicles physically overlap only if their bodies overlap BOTH longitudinally (world-X)
        // AND laterally (world-Y). Check every pair at every shared timestep.
        var byTime = traj.AllPoints.GroupBy(p => p.Time);
        foreach (var frame in byTime)
        {
            var pts = frame.ToList();
            for (var i = 0; i < pts.Count; i++)
            {
                for (var j = i + 1; j < pts.Count; j++)
                {
                    var a = pts[i];
                    var b = pts[j];
                    var longOverlap = Math.Abs(a.X - b.X) < CarLen;
                    var latOverlap = Math.Abs(a.Y - b.Y) < CombinedHalfWidth;
                    Assert.False(longOverlap && latOverlap,
                        $"collision at t={a.Time}: {a.VehicleId}({a.X:F1},{a.Y:F1}) vs {b.VehicleId}({b.X:F1},{b.Y:F1})");
                }
            }
        }
    }

    [Fact]
    public void OvertakerSpillsPassesAndReturns_ClearLane()
    {
        var traj = Run("ov3-clear.rou.xml", 30);
        var ov = traj.PointsFor("overtaker").OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        var leader = traj.PointsFor("leader").OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

        // Spilled off its lane centre toward the oncoming lane (positive lateral) while overtaking.
        Assert.Contains(ov, p => p.Y - LaneCentreYAB > 1.5);
        // Got ahead of the leader.
        Assert.True(ov[^1].Pos > leader[^1].Pos, "overtaker never got ahead of the leader");
        // Returned to its lane centre after passing.
        Assert.True(Math.Abs(ov[^1].Y - LaneCentreYAB) < 1e-6, $"overtaker did not recenter (final y {ov[^1].Y:F2})");
        // Never overlapped the leader.
        AssertNoCollisions(traj);
    }

    [Fact]
    public void OvertakeCompletesAndReturns_BeforeApproachingOncoming_NoCollision()
    {
        var traj = Run("overtake.rou.xml", 30);
        var ov = traj.PointsFor("overtaker").OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        var leader = traj.PointsFor("leader").OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

        // The overtake still completes (gap acceptance found room) and the overtaker recenters ...
        Assert.Contains(ov, p => p.Y - LaneCentreYAB > 1.5);
        Assert.True(ov[^1].Pos > leader[^1].Pos, "overtaker never got ahead of the leader");
        Assert.True(Math.Abs(ov[^1].Y - LaneCentreYAB) < 1e-6, "overtaker did not recenter");
        // ... with no collision against EITHER the leader or the head-on oncoming vehicle.
        AssertNoCollisions(traj);
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
