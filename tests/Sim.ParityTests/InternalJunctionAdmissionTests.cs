using System.Collections.Generic;
using System.Linq;
using Sim.Core;
using Sim.Harness;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// F3/internal-junction-foes T3.2 -- docs/F3-INTERNAL-JUNCTION-DESIGN.md §3/§3a/§6/§7 T3.2.
//
// Guards Engine.InternalJunctionAdmissionGate + InternalJunctionAdmissionConstraint, the cont-turn
// stage-1 BAY admission arm ported from MSInternalJunction::postloadInit's `myFoeLanes` half (the
// physical-occupancy foe set T3.1 built into NetworkModel.InternalLaneFoes). See
// docs/NEED-internal-junction-second-stage-admission.md for the measured veh 95 / 102 deadlock this
// fixes, and Engine.cs's InternalJunctionAdmissionGate property comment for the full derivation and
// the deliberate omissions (myInternalLinkFoes/addBlockedLink, indirectBicycleTurn, exit-link foe
// lanes, walking-area foe exits).
//
// This class drives a PLAIN engine.Run(...) (never Sim.Sumo.SumoShim.Run), so it does NOT touch the
// process-global SUMOSHARP_* env vars and does not need SumoShimEnvCollection -- see
// InternalJunctionAdmissionEndToEndTests (a separate file) for the SumoShim-driven success
// condition 4, which does join that collection.
public class InternalJunctionAdmissionTests
{
    private readonly ITestOutputHelper _out;

    public InternalJunctionAdmissionTests(ITestOutputHelper output) => _out = output;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "scenarios"))
                && File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }

    private static string ScenarioCfg()
        => Path.Combine(RepoRoot(), "scenarios", "_repro", "synthetic-junction2", "scenario.sumocfg");

    private const string BayLane = ":2336_18_0";       // veh 95's cont stage-1 bay.
    private const string ConflictLane = ":2336_3_0";   // veh 102's lane -- an UNCONDITIONAL foe (design §1).
    private const string Stage2Lane = ":2336_42_0";    // veh 95's cont stage-2 lane (the internal junction itself).

    // ============================================================================================
    // Success condition 1 (design §7 T3.2.1): default is false.
    // ============================================================================================
    [Fact]
    // ⚠ THE DEFAULT MOVED (session 4). It was false so that every committed golden stayed untouched until
    // the flag-ON behaviour was measured. That measurement is now in and the answer was unambiguous: with
    // all seven gates ON, **all 661 goldens are byte-identical** and `Sim.Bench` still hashes
    // `D96213B7BB4021A7` with par == single. The only tests that changed were these default assertions --
    // i.e. nothing observable moved, so keeping the gate off was costing believability for no parity gain.
    // Gate-OFF behaviour remains reachable (and is still exercised by the flag-OFF tests below and by the
    // LIVECITY_* / SUMOSHARP_* env overrides), so this is a default change, not a removal.
    public void InternalJunctionAdmissionGate_DefaultIsTrue()
    {
        Assert.True(new Engine().InternalJunctionAdmissionGate);

        // AND its ordering sub-gate, which must never be on without this one being on too: bare-occupancy
        // admission (this gate alone) is symmetric and wedges a cycle of cont bays permanently -- measured
        // at 4890 steps, four cars, junction d_5_4. The pairing is the whole point, so assert it here.
        Assert.True(new Engine().InternalJunctionAdmissionEntryOrder);
    }

    // ============================================================================================
    // Success condition 2 (design §7 T3.2.2), the DIRECT (not merely golden-inferred) half: with the
    // flag OFF, BindingConstraint (arm id 14, Engine.cs's ComputeMoveIntent) must NEVER be the binder
    // for ANY vehicle at ANY step of this scenario -- i.e. the arm is a true no-op, not just "the
    // goldens happen not to move". The full 661-golden/bench-hash/five-diagnostics byte-identical
    // claim itself is verified by the surrounding `dotnet test` run (see the task's done conditions),
    // not re-asserted here.
    // ============================================================================================
    [Fact]
    public void FlagOff_InternalJunctionAdmissionArmNeverBinds_OnSyntheticJunction2()
    {
        // Session 4: the gate now defaults to TRUE, so this flag-OFF test must turn it off EXPLICITLY.
        // Keeping the test is the point -- it is what proves the arm is still an unconditional no-op when
        // disabled, which is what makes the default a *default* rather than a one-way door.
        var engine = new Engine { InternalJunctionAdmissionGate = false };
        Assert.False(engine.InternalJunctionAdmissionGate);
        engine.LoadScenario(ScenarioCfg());

        const int steps = 2000;
        for (var st = 0; st < steps; st++)
        {
            engine.Run(1);
            var binders = engine.BindingConstraints;
            for (var i = 0; i < binders.Length; i++)
            {
                Assert.True(binders[i] != 14,
                    $"BindingConstraint == 14 (internalJunctionAdmission) observed at step {st} for vehicle "
                    + $"index {i} with InternalJunctionAdmissionGate OFF -- the arm must be an unconditional "
                    + "+infinity no-op when the flag is off.");
            }
        }
    }

    // ============================================================================================
    // Success condition 3 (design §7 T3.2.3) -- THE LOAD-BEARING ONE. Flag ON (together with
    // ContTurnInsideJunctionGate + JunctionIsLeaderGate, the same three-flag configuration
    // docs/NEED-internal-junction-second-stage-admission.md measured the deadlock under): a bay
    // occupant must be HELD on its bay lane `:2336_18_0` while a conflict-lane occupant occupies the
    // unconditional foe lane `:2336_3_0` (proving the gate actually engages for the measured pair, not
    // merely that the conflicting state never arises for some unrelated reason), and must NEVER reach
    // cont stage-2 `:2336_42_0` while the foe is still on `:2336_3_0` (the direct behavioural assertion
    // the task requires -- NOT a teleport-count proxy).
    //
    // RE-ANCHORED a SECOND time after two engine insertion fixes landed (absent `departPos` now
    // resolves to SUMO's `base` instead of 0; a vType's own `speedDev` attribute is now honoured
    // instead of only the cfg-wide `default.speeddev`). Both shift where and how fast vehicles enter,
    // moving trajectories on this net again: the previous witness pair (veh 89 bay / veh 102 conflict)
    // no longer co-occurred, and the anchor moved to veh 78 / veh 156.
    //
    // RE-ANCHORED a THIRD time when `Engine.UrgentStrategicLeaderFollow` flipped default-ON (journal
    // Entry 31): earlier strategic merges shift this net's trajectories yet again, 78/156 no longer
    // co-occur, and the longest co-occurrence is BACK to veh 89 (bay) / veh 102 (conflict) -- steps
    // [321, 326] inclusive (6 steps), 0 violation steps, measured over all (bay, conflict) pairs of
    // the 2000-step run on the shipped defaults. The mechanism under test is unchanged; only the
    // witness moved. (A trajectory-anchored witness is inherently re-anchor-prone; the vacuity guard
    // below is what makes that safe -- it fails loudly instead of going silently vacuous.)
    // ============================================================================================
    [Fact]
    public void FlagOn_Veh89IsHeldInTheBay_WhileVeh102Occupies3_0_AndNeverReachesStage2InThatState()
    {
        var engine = new Engine
        {
            InternalJunctionAdmissionGate = true,
            ContTurnInsideJunctionGate = true,
            JunctionIsLeaderGate = true,
        };
        Assert.Equal(-1.0, engine.IgnoreJunctionBlockerSeconds); // SUMO's own default, unchanged.
        engine.LoadScenario(ScenarioCfg());

        const int steps = 2000;
        var traj = engine.Run(steps);

        var byTime = traj.AllPoints
            .Where(p => p.VehicleId is "89" or "102")
            .GroupBy(p => p.Time)
            .OrderBy(g => g.Key);

        var everHeldInBayWhileFoeOccupies = false;
        var violationSteps = new List<double>();

        foreach (var g in byTime)
        {
            string? lane78 = null;
            string? lane156 = null;
            foreach (var p in g)
            {
                if (p.VehicleId == "89") lane78 = p.Lane;
                else if (p.VehicleId == "102") lane156 = p.Lane;
            }

            if (lane78 == BayLane && lane156 == ConflictLane)
            {
                everHeldInBayWhileFoeOccupies = true;
            }

            if (lane78 == Stage2Lane && lane156 == ConflictLane)
            {
                violationSteps.Add(g.Key);
            }
        }

        _out.WriteLine(
            $"veh 89 observed on bay lane {BayLane} while veh 102 occupied {ConflictLane}: "
            + $"{(everHeldInBayWhileFoeOccupies ? "YES (gate engaged for the measured pair)" : "NO")}");
        _out.WriteLine(
            $"veh 89 observed on stage-2 lane {Stage2Lane} while veh 102 occupied {ConflictLane}: "
            + $"{violationSteps.Count} step(s)"
            + (violationSteps.Count > 0 ? $" [{string.Join(",", violationSteps.Take(10))}]" : string.Empty));

        Assert.True(everHeldInBayWhileFoeOccupies,
            $"veh 89 was never observed on its bay lane {BayLane} while veh 102 occupied {ConflictLane} -- "
            + "cannot confirm the admission gate actually engaged for the measured pair (the test would be "
            + "vacuous otherwise).");

        Assert.True(violationSteps.Count == 0,
            $"veh 89 reached cont stage-2 ({Stage2Lane}) while veh 102 still occupied its unconditional foe "
            + $"lane {ConflictLane} at step(s) [{string.Join(",", violationSteps.Take(10))}] -- "
            + "InternalJunctionAdmissionGate should have held veh 78 in its bay; SUMO would never admit it "
            + "there. See docs/NEED-internal-junction-second-stage-admission.md.");
    }
}
