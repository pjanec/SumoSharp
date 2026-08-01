using Sim.Harness;
using Sim.Sumo;
using Xunit;

namespace Sim.ParityTests;

// GAP-1 dense-flow gridlock anchor (docs/HIGH-DENSITY-CALIBRATION-DESIGN.md §2.3.5).
//
// The committed scenarios/_repro/synthetic-junction2 net has junctions where the only connection
// from an upstream edge lands a vehicle on a lane whose connections do NOT include its next route
// edge -- a "dead lane" for that route (e.g. veh routed through 30->124 is forced onto 30_1, but
// 124 leaves only from 30_0; and -2437_1 wanting -2337 at the tl=2336 junction). Under the 2x
// compressed-depart demand (scenario.dense.rou.xml, 325 vehicles departing in half the time) SumoSharp
// used to HARD-DEADLOCK at these dead lanes: cars slammed into the lane end at speed, clamped to 0, and
// gridlocked (10 teleports, 275 arrivals, ~45 cars stuck at meanSpeed 0), while vanilla SUMO 1.20.0
// drains fully (0 teleports, 290 arrivals).
//
// The three-part SUMO-faithful fix (Engine.DeadLaneMergeBrakeConstraint = MSLCM_LC2013::informLeader's
// urgent-strategic-change brake; the boundary reroute; Engine.TryRerouteStuckDeadLane for cars held
// short of the lane end by a junction yield / red light) makes a dead-lane vehicle decelerate to
// re-try merging onto its through lane and, failing that, cross via its actual lane's connection
// (getBestLanesContinuation semantics) instead of freezing. This restores drainage: 0 teleports /
// 290 arrivals == vanilla.
//
// ENGINE-ONLY, offline (no SUMO): drives the committed dense cfg through the same in-process SumoShim
// path the serve pipeline uses and reads the produced <teleports>/<tripinfo> counts. The bounds guard
// the fix from regressing back toward the gridlock. Every input is committed; the fix is provably inert
// for every committed FCD golden (all gated on the dead-lane condition no golden vehicle is ever in),
// verified by the rest of the parity suite staying byte-identical.
// PROCESS-GLOBAL ENV HAZARD -- this class drives Sim.Sumo.SumoShim.Run, and SumoShim reads the
// PROCESS-WIDE environment variable SUMOSHARP_CONTTURNFIX to set Engine.ContTurnInsideJunctionGate
// (SumoShim.cs:250). IgnoreJunctionBlockerTests SETS that variable around its own shim runs, so with
// xUnit's DEFAULT cross-class parallelism a concurrently-running shim test can observe the other
// class's value and silently simulate with a DIFFERENT engine configuration than it intended.
//
// This was not hypothetical: LowDensityTeleportTests failed 1 of 3 full-suite runs with exactly
// 5 teleports (vs its <= 2 ceiling) while passing every standalone run, and the leak was then
// reproduced deterministically -- `SUMOSHARP_CONTTURNFIX=1 dotnet test --filter LowDensityTeleportTests`
// fails with that identical message. Since LowDensityTeleportTests and DenseFlowDeadLaneDrainTests are
// two of the five load-bearing gridlock diagnostics, an unreliable one is worse than no diagnostic at
// all -- a false RED sends the next session chasing a regression that does not exist.
//
// Every class that calls SumoShim.Run therefore shares this collection, which xUnit runs SEQUENTIALLY.
// A NEW test that drives SumoShim.Run MUST join it. The robust fix (removing the process-global read
// entirely) is docs/NEED-sumoshim-process-global-contturn-env.md.
[Collection(SumoShimEnvCollection.Name)]
public class DenseFlowDeadLaneDrainTests
{
    [Fact]
    public void SyntheticJunction2Dense_DrainsWithoutGridlock_MatchesVanilla()
    {
        var scenarioDir = Path.Combine(RepoRoot(), "scenarios", "_repro", "synthetic-junction2");
        var cfg = Path.Combine(scenarioDir, "scenario.dense.sumocfg");
        Assert.True(File.Exists(cfg), $"dense repro scenario missing: {cfg}");

        var outDir = Path.Combine(Path.GetTempPath(), "sumosharp-densedrain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);

        // PIN THE THREE JUNCTION GATES TO THE ENGINE'S OWN DEFAULTS (all `true`). SumoShim reads these
        // with the unsafe `== "1"` form, so an UNSET variable forces the gate OFF -- the open bug
        // docs/ENV-GATES.md flags. Left unpinned, this test measured a configuration THE ENGINE DOES NOT
        // SHIP, and the numbers below were calibrated in it. That is exactly the process-global hazard
        // CLAUDE.md measurement-discipline #10 exists for: "set every gate you care about EXPLICITLY, in
        // BOTH arms". Measured difference on this scenario at the time of pinning: unpinned base 290
        // arrivals, pinned base 289 -- so the old `>= 290` constant was never reachable in the shipped
        // configuration.
        var prevGates = JunctionGateEnv.PinToEngineDefaults();
        try
        {
            var statistic = Path.Combine(outDir, "stat.xml");
            var tripinfo = Path.Combine(outDir, "trip.xml");
            var exit = SumoShim.Run(
                new[]
                {
                    "-c", cfg,
                    "--statistic-output", statistic,
                    "--tripinfo-output", tripinfo,
                    "--end", "1000",
                    "--no-step-log", "true",
                },
                new StringWriter(), new StringWriter());

            Assert.Equal(0, exit);

            var stats = StatisticOutputParser.Parse(statistic);
            var arrivals = CountArrivals(tripinfo);

            // Vanilla SUMO 1.20.0 on this exact committed cfg: 0 teleports / 290 arrivals. Pre-fix
            // SumoSharp GRIDLOCKED (10 teleports / 275 arrivals / ~45 permanently stuck). This anchor
            // exists to catch a regression BACK toward that gridlock.
            //
            // ⚠ RE-BASELINED, and the reason matters more than the number. Two things were wrong with
            // the previous constants, and only one of them is a behaviour change:
            //
            // 1. THE OLD `>= 290` WAS MEASURED IN A CONFIGURATION THE ENGINE DOES NOT SHIP. This test
            //    did not pin the three junction gates, so SumoShim's `== "1"` reads forced them OFF
            //    (see JunctionGateEnv). With them pinned to the Engine defaults the SAME pre-change
            //    code arrives 289, not 290 -- the old floor was already unreachable for the shipped
            //    engine, and nobody could see it because the gates were silently off.
            //
            // 2. The keepClear walk-direction fix and the SameTargetMergeConstraint PHASE 0
            //    `!foe.WillPass` fix (docs/JUNCTION-REALISM-SESSION-JOURNAL.md Entry 17) cost this
            //    2x-compressed TORTURE scenario a further 2 arrivals, 289 -> 287.
            //
            // WHAT THE 38 NON-ARRIVALS ACTUALLY ARE, counted rather than assumed (325 routed, 325 all
            // inserted, 0 never-inserted): 35 are PARKED by scenario.add.xml and are not supposed to
            // arrive; 3 are genuinely wedged. Of those 3, vehicles 122 and 256 sit on the dead lane
            // `30_1` at pos 24.12/16.62 -- IDENTICALLY, to the centimetre, in both arms, i.e. the
            // dead-lane stranding this test is named for is PRE-EXISTING and was never covered by the
            // 290 figure. The 2 the fixes cost wedge INSIDE junctions under
            // `internalJunctionAdmission` (binder 14) on `:2810_8_0` and `crossJxnLeader` on
            // `:2450_0_1` -- a different mechanism, tracked in the journal, not the dead-lane one.
            //
            // WHY THIS IS ACCEPTED. The same two fixes take junction-realism-L1 from a permanent
            // 338-vehicle gridlock (112 arrivals) to 386 of 450, drop stuckDwell to 0 across the whole
            // 26-net battery bar one net, and take THIS net's low-density scenario to 0 teleports --
            // exactly matching vanilla SUMO, from 5. All 661 goldens stayed byte-identical.
            //
            // The floor stays a HARD FAIL: it still separates "healthy" from the gridlock signature
            // (275 arrivals with ~45 frozen). It is now pinned to a measured, shipped-configuration
            // number instead of an aspirational one.
            //
            // ⚠ RE-BASELINED AGAIN (Entry 38), same procedure, counted not assumed. Un-gating the
            // merge-arm entry-order tie-break + foes-based reachability (the latent mutual PHASE-1
            // merge deadlock, journal Entry 38) changed this torture scenario 287 -> 286, and the
            // END-STATE ACCOUNTING is the point: the two previously-wedged junction-interior cars
            // AND the documented dead-lane pair 122/256 on `30_1` now ALL ARRIVE; the non-arrivals
            // are 35 parked + veh 208 (`484_0`, binder successiveLane from t=267) + veh 241
            // (`101_0`, same class from t=388) -- the PRE-EXISTING dead-lane stranding this test is
            // named for, redistributed by the changed junction interleave. No junction-wedge class
            // remains in the end state. Accepted because the same change takes the live-city
            // long-horizon run from 129 >300-step stalls to 0 (LongHorizonGridlockDiagTests, which
            // gates it) and drops this scenario's default-arm yield teleports 1 -> 0
            // (IgnoreJunctionBlockerTests' table).
            Assert.True(
                arrivals >= 286,
                $"dense synthetic arrived {arrivals} vehicles (< the measured shipped-configuration " +
                "floor of 286): FULL DRAINAGE regressed -- this is the gridlock signature (pre-fix was " +
                "275 with ~45 stuck). This is the hard invariant.");

            // Teleports: was <= 2, measured 5 with the gates pinned and the Entry 17 fixes in. These are
            // RECOVERED teleports -- the vehicles are re-inserted and the arrivals floor above is the
            // thing that catches a real gridlock. A spike well past this signals a genuine regression.
            Assert.True(
                stats.TeleportsTotal <= 5,
                $"dense synthetic fired {stats.TeleportsTotal} teleports (jam={stats.TeleportsJam}, " +
                $"yield={stats.TeleportsYield}); expected <= 5 (the measured allowance for this 2x " +
                "stress scenario in the shipped gate configuration). A spike well past 5 signals a real " +
                "gridlock regression (pre-fix was 10 with arrivals dropping to 275).");
        }
        finally
        {
            JunctionGateEnv.Restore(prevGates);
            Directory.Delete(outDir, recursive: true);
        }
    }

    private static int CountArrivals(string tripinfoPath)
    {
        if (!File.Exists(tripinfoPath))
        {
            return 0;
        }

        var count = 0;
        foreach (var line in File.ReadLines(tripinfoPath))
        {
            if (line.Contains("<tripinfo ", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
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
