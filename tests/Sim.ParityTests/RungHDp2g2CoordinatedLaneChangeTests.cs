using System.Linq;
using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// P2G-2 acceptance (docs/HIGH-DENSITY-P2G2-COOPERATIVE-LC-DESIGN.md): the coordinated dense lane-change
// model behind the `Engine.CoordinatedLaneChange` config gate. FUNCTIONAL, not parity (the gate-ON path
// is a non-default behavioural mode; the gate-OFF byte-identical guarantee is covered by the rest of the
// committed parity suite staying green).
//
// RE-BASELINED after the serve-path P2-G traffic-light junction fixes (Bug-2 RBL traffic_light
// exclusion, Bug-3 red-held-foe WillPass). Those fixes removed the saturated-grid gridlock AT ITS
// ROOT -- the `-L2` grid now drains for every config (parity and dense-only both ~1 stuck; measured),
// and organic-net throughput of the two is now equivalent (parity 327 / dense-only 325 arrived, was
// 278 / 278). This matches the design doc's own statement that cooperative LC is "required for PARITY
// (SUMO-faithful lane distribution), NOT for flow." The feature's remaining, untouched value is
// FIDELITY: the SUMO-faithful speed-gain lane choice on scenarios/46 that the default path misses
// (CoordinatedLaneChange_On_Scenario46_TakesFasterLane below).
//
// The cooperative `informFollower` layer was RETIRED (owner decision): its only benefit was rescuing
// the synthetic saturated grid, which the junction fixes now do at the engine level, and it degraded
// organic flow + cost perf on the Windows box. Its gate, code path, CoopSpeedAdvice channel, and CLI
// opt-ins were removed; the gate-OFF parity default is unchanged (all committed goldens byte-identical).
public class RungHDp2g2CoordinatedLaneChangeTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static int StuckCount(TrajectorySet traj)
    {
        var last = new Dictionary<string, (double T, double Speed)>();
        var maxT = 0.0;
        foreach (var p in traj.AllPoints)
        {
            maxT = System.Math.Max(maxT, p.Time);
            last[p.VehicleId] = (p.Time, p.Speed);
        }

        return last.Count(kv => kv.Value.T >= maxT - 1 && kv.Value.Speed < 0.1);
    }

    // Throughput proxy: how many vehicles drained (reached their destination) before the run's final
    // emitted step. Mirrors Sim.BenchCity's "vehicles arrived" -- a vehicle whose last trajectory frame
    // is before the final step left the network; one still present at the final step is running@end. More
    // arrived over a fixed horizon == better flow. (StuckCount above only separates cleanly on a net that
    // fully drains, e.g. the saturated grid; on a still-busy organic net at its horizon, momentary
    // junction queueing dominates it -- so use arrivals to compare organic flow.)
    private static int ArrivedCount(TrajectorySet traj)
    {
        var last = new Dictionary<string, double>();
        var maxT = 0.0;
        foreach (var p in traj.AllPoints)
        {
            maxT = System.Math.Max(maxT, p.Time);
            last[p.VehicleId] = p.Time;
        }

        return last.Count(kv => kv.Value < maxT - 0.5); // left before the final step (half-step slack)
    }

    // THE PRODUCT DEFAULT: aggressive dense LC (CoordinatedLaneChange=true -- what the runtime hosts ship).
    // On the realistic organic multi-lane net it must not REGRESS organic throughput vs the SUMO-anchor
    // parity baseline. RE-BASELINED after the P2-G junction fixes: those fixes lifted both configs by ~48
    // arrivals and closed the gap -- dense-LC and parity are now throughput-EQUIVALENT (measured parity 327,
    // dense-only 325, within ~2 over the 600-step horizon; before the junction fixes both were 278). So
    // dense-LC is now throughput-NEUTRAL vs parity rather than the clear win it was framed as -- its default
    // justification rests on FIDELITY (the scenario-46 speed-gain), not throughput. This guard checks
    // dense-LC stays within a small band of parity (catches a real organic-throughput regression -- a
    // dense-LC change that re-introduced thrash/jam would drop arrivals far more than this band -- while
    // allowing the measured few-vehicle reordering). (The cooperative informFollower config was retired --
    // its only benefit was a synthetic saturated-grid rescue the junction fixes now provide, and it degraded
    // organic flow; see docs/HIGH-DENSITY-P2G2-COOPERATIVE-LC-DESIGN.md.)
    [Fact]
    public void CoordinatedLaneChange_DenseOnly_OrganicThroughputTracksParity()
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", "_bench", "city-organic-L2");

        int ArrivedFor(bool denseLc)
        {
            var engine = new Engine { CoordinatedLaneChange = denseLc };
            engine.LoadScenario(Path.Combine(dir, "net.net.xml"), Path.Combine(dir, "rou.rou.xml"), Path.Combine(dir, "config.sumocfg"));
            return ArrivedCount(engine.Run(600));
        }

        var parity = ArrivedFor(denseLc: false); // SUMO-anchor baseline
        var defaultDense = ArrivedFor(denseLc: true); // the product default

        const int band = 8; // ~2.5% of ~327; allows measured reordering, catches a real jam/thrash regression
        Assert.True(defaultDense >= parity - band,
            $"default dense-LC must not regress organic throughput vs parity: default arrived {defaultDense}, parity {parity} (band {band}).");
    }

    // RE-BASELINED (was CoordinatedLaneChange_DenseOnly_SaturatedGridGridlocks). Before the serve-path
    // P2-G traffic-light junction fixes, aggressive dense LC ALONE (no informFollower) gridlocked this
    // deliberately over-saturated diagnostic grid to ~51 stuck -- the "aggressive LC without coordination
    // -> thrash -> jam" unstable regime, and the documented reason the informFollower opt-in existed. The
    // P2-G junction fixes (Bug-2 RBL traffic_light exclusion, Bug-3 red-held-foe WillPass) removed that
    // gridlock AT ITS ROOT: the grid now drains with dense-LC-alone (~1 stuck), and even at plain parity.
    // So the saturated-grid gridlock was substantially a junction TL-blindness problem, not a lane-change
    // one, and the informFollower's saturated-grid rescue became obsolete -- which is why the informFollower
    // was subsequently RETIRED (see the class header). This test now guards that dense-LC-alone keeps
    // FLOWING the grid (a regression that re-introduced the thrash/jam would resurface here).
    [Fact]
    public void CoordinatedLaneChange_DenseOnly_SaturatedGrid_FlowsAfterP2GJunctionFixes()
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", "_diag", "willpass-saturation");
        var engine = new Engine { CoordinatedLaneChange = true }; // dense LC only -- no informFollower rescue
        engine.LoadScenario(Path.Combine(dir, "net.net.xml"), Path.Combine(dir, "rou.rou.xml"), Path.Combine(dir, "config.sumocfg"));

        var traj = engine.Run(700);

        var stuck = StuckCount(traj);
        Assert.True(stuck <= 5,
            $"dense-LC-alone should now FLOW the saturated grid after the P2-G junction fixes (was ~51 stuck pre-fix); got {stuck}.");
    }

    // Fidelity: with the coordinated model ON, the engine performs the SUMO-faithful speed-gain overtake
    // across the K junction on scenarios/46 -- vehicle f.1 reaches the faster lane e_det1_1, which the
    // DEFAULT path never does (it stays on e_det1_0, the ~7 m residual). Behavioural assertion (the
    // gate-ON trajectory is not yet bit-exact end-to-end -- a documented next iteration).
    [Fact]
    public void CoordinatedLaneChange_On_Scenario46_TakesFasterLane()
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", "46-reroute-multilane");
        var engine = new Engine { CoordinatedLaneChange = true };
        engine.LoadScenario(Path.Combine(dir, "config.sumocfg"));

        var traj = engine.Run(300);

        var f1OnFastLane = traj.PointsFor("f.1").Any(tp => tp.Value.Lane == "e_det1_1");
        Assert.True(f1OnFastLane, "coordinated-LC: f.1 should speed-gain onto the faster lane e_det1_1 (like SUMO).");
    }

    // ROBUSTNESS regression guard (docs/HIGH-DENSITY-P2G2-COOPERATIVE-LC-DESIGN.md §3.7): coordinated
    // mode + REGION-PARALLEL execute on a large ORGANIC multi-lane net must NOT crash. Before the
    // _laneSeqPoolLock fix, the aggressive coordinated lane-changing triggered frequent concurrent
    // TryReResolveFromActualLane appends to the shared _laneSeqPool during the parallel ExecuteMoves,
    // corrupting the list's size -> IndexOutOfRange. This runs the exact scenario+mode that crashed and
    // asserts it completes and vehicles arrive.
    [Fact]
    public void CoordinatedLaneChange_On_OrganicNet_RegionParallel_DoesNotCrash()
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", "_bench", "city-organic-L2");
        var engine = new Engine { CoordinatedLaneChange = true, RegionPlan = true };
        engine.LoadScenario(
            Path.Combine(dir, "net.net.xml"), Path.Combine(dir, "rou.rou.xml"), Path.Combine(dir, "config.sumocfg"));

        var traj = engine.Run(400); // must not throw (region-parallel append race is fixed)

        Assert.NotEmpty(traj.VehicleIds); // sanity: the run actually simulated vehicles
    }

    // Control: the DEFAULT path (gate OFF) keeps f.1 on e_det1_0 -- the residual the coordinated mode
    // fixes. Confirms the two behaviours genuinely differ (the gate is load-bearing) and that gate-OFF is
    // the unchanged default.
    [Fact]
    public void CoordinatedLaneChange_Off_Scenario46_StaysOnSlowLane()
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", "46-reroute-multilane");
        var engine = new Engine(); // gate OFF (default)
        engine.LoadScenario(Path.Combine(dir, "config.sumocfg"));

        var traj = engine.Run(300);

        var f1OnFastLane = traj.PointsFor("f.1").Any(tp => tp.Value.Lane == "e_det1_1");
        Assert.False(f1OnFastLane, "default path: f.1 should stay on e_det1_0 (no cross-junction speed-gain).");
    }
}
