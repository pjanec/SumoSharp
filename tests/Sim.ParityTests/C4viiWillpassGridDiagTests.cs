using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// C4-vii DIAGNOSTIC + regression guard. scenarios/_diag/c4vii-willpass-grid is a committed 6x6
// -L2 priority grid (netgenerate --grid.number=6 --grid.length=250 -L2, priority junctions; 75
// duarouter trips). SUMO runs it at free flow (0 of 75 stuck); the pre-C4-vii-c engine gridlocked
// (~38-40 stuck). The gridlock was root-caused (NOT the willPass signal the scenario name and the
// original C4-VII-REMAINING.md bootstrap assumed): 29 of the 38 stuck were clamped by the C2-ii
// boundary-convergence guard -- stranded at a lane end because their actual lane != the route pool's
// resolved exit lane, even though that lane connects onward (a route->lane OVER-CONSTRAINT; 208 of
// this net's 224 two-lane edges have both lanes connecting to a common downstream edge, so the pool's
// single-exit choice is routinely ambiguous). The remaining 9 stuck all transitively yielded to those
// clamp victims, so the willPass gate had zero independent effect here (verified: gate on/off gave
// identical stuck counts). See scenarios/46-convergence-lane for the minimal single-vehicle anchor of
// the fix (Engine.TryReResolveFromActualLane), and C4-VII-REMAINING.md for the corrected diagnosis.
//
// This test guards two properties:
//   1. Runs 600 steps WITHOUT THROWING -- the crash regression (parking vehicles on 2-lane internal
//      lanes once tripped ComputeBestLanes on an internal edge; guarded by the internal-lane guard in
//      ApplyKeepRightDecision + DecideSpeedGainChanges).
//   2. The grid now FLOWS like SUMO: 0 stuck (was ~38 pre-fix). Asserted tightly below.
public class C4viiWillpassGridDiagTests
{
    private static readonly string Dir = System.IO.Path.Combine(RepoRoot(), "scenarios", "_diag", "c4vii-willpass-grid");

    [Fact]
    public void Grid_RunsWithoutThrowing_CrashRegression()
    {
        var engine = new Engine();
        engine.LoadScenario(
            System.IO.Path.Combine(Dir, "net.net.xml"),
            System.IO.Path.Combine(Dir, "rou.rou.xml"),
            System.IO.Path.Combine(Dir, "config.sumocfg"));

        // Must not throw. Before the internal-lane keep-right guard (main eac0a5b) this threw
        // "edge ':C2_16' is not part of the given route" on the first junction crossing.
        var traj = engine.Run(600);

        var last = new Dictionary<string, (double T, double Speed)>();
        var maxT = 0.0;
        foreach (var p in traj.AllPoints)
        {
            maxT = System.Math.Max(maxT, p.Time);
            last[p.VehicleId] = (p.Time, p.Speed);
        }

        var stuck = last.Count(kv => kv.Value.T >= maxT - 1 && kv.Value.Speed < 0.1);

        // SUMO: 0 stuck / 75. Engine after the C4-vii-c convergence-clamp fix: 0 stuck (was ~38
        // pre-fix). A tiny margin (<= 2) absorbs a vehicle legitimately still in free-flow transit at
        // the run end without letting a real gridlock regression slip through.
        Assert.True(stuck <= 2, $"C4-vii-c regression: {stuck} stuck (post-fix baseline 0, SUMO 0). See scenarios/46-convergence-lane + C4-VII-REMAINING.md.");
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
