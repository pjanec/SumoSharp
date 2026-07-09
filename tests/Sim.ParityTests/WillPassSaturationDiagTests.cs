using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// C4-viii (willPass pre-pass) SATURATION anchor -- diagnostic, gauged by stuck-count/arrival (a
// gridlocked state is not a per-step FCD golden, matching the established gridlock-anchor convention;
// exact @1e-3 is unreachable for a saturated -L2 grid where keep-right/lane-change/junction ordering
// all diverge from SUMO). scenarios/_diag/willpass-saturation is a small 3x3 -L2 TLS grid
// (netgenerate --grid.number=3 --grid.length=250 -L2 --tls.guess --seed 11 + randomTrips -e 350
// -p 0.85 --min-distance 400 --seed 11 + duarouter --named-routes), 412 fixed-route trips, tuned to
// the SATURATION point where root blockers mutually brake-to-stop -- the willPass ordering the
// C4-vii-b/c anchors do NOT exercise.
//
// THE BUG this pins (pre-C4-viii): the crossing-yield decision read a foe's raw START-OF-STEP speed.
// A root blocker yields to a foe that is close AND moving (speed > 0) but is BRAKING TO A STOP this
// step (it is itself yielding), so its planned vNext ~ 0 and SUMO's willPass for it is false -- SUMO
// does NOT yield to it. The engine yielded -> both sit -> the gridlock cascades as traffic queues
// behind. At saturation this fires everywhere: WITHOUT the willPass pre-pass this net leaves ~50
// vehicles PERMANENTLY stuck at junction stop lines (verified persistent to t=899); WITH it the grid
// FLOWS -- 0 stuck, fully drained, matching SUMO (0 stuck, 0 teleports). FIX: Engine.ComputeWillPass
// caches each vehicle's planned vNext-at-link from the frozen start-of-step snapshot BEFORE
// PlanMovements (SUMO's MSLink::setApproaching-before-opened()); JunctionYieldConstraint then blocks
// ego only on a foe whose WillPass is true. Inert wherever no foe is braking-to-stop at a crossing
// (the whole committed suite stays green at 166 + 1 skip, Sim.Bench hash unchanged).
public class WillPassSaturationDiagTests
{
    private static readonly string Dir = System.IO.Path.Combine(
        RepoRoot(), "scenarios", "_diag", "willpass-saturation");

    [Fact]
    public void SaturatedGrid_Flows_NotGridlocked()
    {
        var engine = new Engine();
        engine.LoadScenario(
            System.IO.Path.Combine(Dir, "net.net.xml"),
            System.IO.Path.Combine(Dir, "rou.rou.xml"),
            System.IO.Path.Combine(Dir, "config.sumocfg"));

        var traj = engine.Run(700);

        var last = new Dictionary<string, (double T, double Speed)>();
        var maxT = 0.0;
        foreach (var p in traj.AllPoints)
        {
            maxT = System.Math.Max(maxT, p.Time);
            last[p.VehicleId] = (p.Time, p.Speed);
        }

        var stuck = last.Count(kv => kv.Value.T >= maxT - 1 && kv.Value.Speed < 0.1);

        // SUMO: 0 stuck / 412. Engine WITH the willPass pre-pass: 0 (drains by ~t=605). WITHOUT it: ~50
        // permanently stuck (this assertion FAILS if the pre-pass / gate is removed -- the
        // non-vacuousness guard). A small margin absorbs residual symmetric mutual-yield corners
        // (arrival-time RoW, out of willPass scope) without letting a real gridlock regression through.
        Assert.True(stuck <= 5, $"willPass saturation regression: {stuck} stuck (post-fix ~0, pre-fix ~50, SUMO 0).");
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
