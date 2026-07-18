using System.Linq;
using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// P2G-2 acceptance (docs/HIGH-DENSITY-P2G2-COOPERATIVE-LC-DESIGN.md): the coordinated dense lane-change
// model behind the `Engine.CoordinatedLaneChange` config gate. FUNCTIONAL, not parity (the gate-ON path
// is a non-default behavioural mode; the gate-OFF byte-identical guarantee is covered by the rest of the
// committed parity suite staying green). These tests pin the headline finding of the informFollower
// spike: the cooperative speed-advice coordination lets the faithful aggressive lane-changing (P2G-3
// cross-junction speed-gain) FLOW a saturated -L2 grid instead of gridlocking it, and makes the
// SUMO-faithful speed-gain lane choice on scenarios/46 that the default path misses.
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

    // THE HEADLINE: full coordination (aggressive dense LC + the cooperative informFollower yield) flows the
    // saturated grid (0 stuck), NOT gridlocks it. This is what the `--inform-follower` opt-in buys. Note the
    // informFollower is REQUIRED here: aggressive LC alone gridlocks this deliberately over-saturated grid
    // to ~51 stuck (see CoordinatedLaneChange_DenseOnly_SaturatedGridGridlocks). That is exactly why the
    // informFollower exists -- and exactly why it is NOT the product default (it degrades the realistic
    // organic net; see CoordinatedLaneChange_DenseOnly_FlowsOrganicNet). This is the proof the cooperative
    // coordination architecture is correct for the saturated-grid case.
    [Fact]
    public void CoordinatedLaneChange_WithInformFollower_SaturatedGridStillFlows()
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", "_diag", "willpass-saturation");
        var engine = new Engine { CoordinatedLaneChange = true, CooperativeInformFollower = true };
        engine.LoadScenario(Path.Combine(dir, "net.net.xml"), Path.Combine(dir, "rou.rou.xml"), Path.Combine(dir, "config.sumocfg"));

        var traj = engine.Run(700);

        var stuck = StuckCount(traj);
        Assert.True(stuck <= 5, $"full-coordination saturated grid gridlocked: {stuck} stuck (expected <=5; SUMO 0).");
    }

    // THE PRODUCT DEFAULT: aggressive dense LC WITHOUT the informFollower (CoordinatedLaneChange=true,
    // CooperativeInformFollower=false -- what the runtime hosts ship). On the realistic organic multi-lane
    // net it must flow AT LEAST as well as parity -- that is the entire reason it is the default. Compared
    // by throughput (vehicles drained over a fixed 600-step horizon): the default must arrive no fewer than
    // parity. Also asserts the default arrives no fewer than FULL coordination (dense LC + informFollower),
    // pinning the headline finding that the informFollower HURTS organic throughput (measured: default and
    // parity both drain 278, full coordination only 268). This is the load-bearing guard for the default
    // choice: if a change made the default flow worse than parity, the default would no longer be justified.
    [Fact]
    public void CoordinatedLaneChange_DenseOnly_FlowsOrganicNetAtLeastAsWellAsParity()
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", "_bench", "city-organic-L2");

        int ArrivedFor(bool denseLc, bool informFollower)
        {
            var engine = new Engine { CoordinatedLaneChange = denseLc, CooperativeInformFollower = informFollower };
            engine.LoadScenario(Path.Combine(dir, "net.net.xml"), Path.Combine(dir, "rou.rou.xml"), Path.Combine(dir, "config.sumocfg"));
            return ArrivedCount(engine.Run(600));
        }

        var parity = ArrivedFor(denseLc: false, informFollower: false); // SUMO-anchor baseline
        var defaultDense = ArrivedFor(denseLc: true, informFollower: false); // the product default
        var fullCoord = ArrivedFor(denseLc: true, informFollower: true); // the --inform-follower opt-in

        Assert.True(defaultDense >= parity,
            $"default dense-LC must flow the organic net >= parity: default arrived {defaultDense}, parity {parity}.");
        Assert.True(defaultDense >= fullCoord,
            $"informFollower should not improve organic throughput: default arrived {defaultDense}, full-coordination {fullCoord}.");
    }

    // CONTROL for the headline: aggressive dense LC ALONE (no informFollower -- the product default) DOES
    // gridlock the deliberately over-saturated diagnostic grid. This is the documented reason the
    // informFollower opt-in exists, and the reason the saturated-grid A/B is a diagnostic, not a realistic
    // product net. Asserts the two configs genuinely differ (the informFollower flag is load-bearing).
    [Fact]
    public void CoordinatedLaneChange_DenseOnly_SaturatedGridGridlocks()
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", "_diag", "willpass-saturation");
        var engine = new Engine { CoordinatedLaneChange = true }; // dense LC only -- no informFollower rescue
        engine.LoadScenario(Path.Combine(dir, "net.net.xml"), Path.Combine(dir, "rou.rou.xml"), Path.Combine(dir, "config.sumocfg"));

        var traj = engine.Run(700);

        var stuck = StuckCount(traj);
        Assert.True(stuck >= 20, $"expected dense-LC-alone to gridlock the saturated grid (~51 stuck); got {stuck}.");
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
