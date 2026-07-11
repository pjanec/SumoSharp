using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// Cont-turn (U-turn) MERGE-distance freeze anchor -- diagnostic, gauged by "does the U-turn vehicle
// complete its turn" (a frozen/gridlocked state is not a per-step FCD golden; same convention as the
// willpass-saturation / sym-rbl-straight anchors). scenarios/_diag/uturn-contturn-freeze is a small
// 4x4 grid (netgenerate --grid.number=4 --grid.length=500 -L 1 --tls.guess --seed 42), one U-turn
// vehicle (veh 0: A0A1 -> A1A0, a CONT turn split across two internal lanes :A1_8_0 -> :A1_11_0) plus
// a period-6 flow on A2A1 -> A1A0 that provides the sameTarget MERGE foe veh 0 yields to at junction A1.
//
// THE BUG this pins: for a cont turn, JunctionYieldConstraint's `approachLane` is the intermediate
// INTERNAL lane (:A1_8_0, ~1 m), but the vehicle's `pos` is on the normal lane far upstream. The
// SameTargetMergeConstraint measured its distance-to-merge as `approachLane.Length - pos` (= 1 - pos =
// large negative), so its stop-line gate never relaxed and its merge follow returned ~0 -- freezing
// the vehicle HUNDREDS of metres before its turn. This was the dominant city-3000 gridlock seed (~47%
// of that demand is U-turn routes; pre-fix 2231 vehicles stuck, post-fix 0, matching SUMO). SUMO flows
// veh 0 through the U-turn onto A1A0. FIX: compute the true distance to the junction-link internal lane
// by walking the route pool (the same C4-vii-a cont-turn distance the cautious-approach arm already
// used), reducing to `approachLane.Length - pos` for every ordinary (non-cont) merge -- so the whole
// committed suite stays byte-identical (227 + 1 skip, Sim.Bench hash 909605E965BFFE59 unchanged) and
// only the cont-turn merge path changes.
public class UturnContTurnFreezeDiagTests
{
    private static readonly string Dir = System.IO.Path.Combine(
        RepoRoot(), "scenarios", "_diag", "uturn-contturn-freeze");

    [Fact]
    public void UturnVehicle_CompletesTheContTurn_NotFrozenOnApproach()
    {
        var engine = new Engine();
        engine.LoadScenario(
            System.IO.Path.Combine(Dir, "net.net.xml"),
            System.IO.Path.Combine(Dir, "rou.rou.xml"),
            System.IO.Path.Combine(Dir, "config.sumocfg"));

        var traj = engine.Run(120);

        // veh 0 is the U-turn vehicle A0A1 -> A1A0. WITH the fix it crosses junction A1 and reaches its
        // SECOND edge A1A0 (SUMO: on A1A0 well before t=120). WITHOUT the fix the spurious merge stop
        // strands it on its FIRST edge A0A1 -- it never reaches A1A0. Asserting it reaches A1A0 is the
        // non-vacuous regression guard (fails if the cont-turn distance fix is removed).
        var reachedA1A0 = traj.AllPoints.Any(p => p.VehicleId == "0" && p.Lane.StartsWith("A1A0"));

        Assert.True(reachedA1A0,
            "cont-turn merge freeze regression: U-turn veh 0 never reached its post-turn edge A1A0 "
            + "(pre-fix it freezes on the A0A1 approach; SUMO and the fixed engine flow it through).");
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
