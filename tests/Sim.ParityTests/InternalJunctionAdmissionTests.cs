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
    public void InternalJunctionAdmissionGate_DefaultIsFalse()
    {
        Assert.False(new Engine().InternalJunctionAdmissionGate);
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
        var engine = new Engine(); // InternalJunctionAdmissionGate defaults to false.
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
    // docs/NEED-internal-junction-second-stage-admission.md measured the deadlock under): veh 95 must
    // be HELD on its bay lane `:2336_18_0` while veh 102 occupies its unconditional foe lane
    // `:2336_3_0` (proving the gate actually engages for the measured pair, not merely that the
    // conflicting state never arises for some unrelated reason), and must NEVER reach cont stage-2
    // `:2336_42_0` while veh 102 is still on `:2336_3_0` (the direct behavioural assertion the task
    // requires -- NOT a teleport-count proxy).
    // ============================================================================================
    [Fact]
    public void FlagOn_Veh95IsHeldInTheBay_WhileVeh102Occupies3_0_AndNeverReachesStage2InThatState()
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
            .Where(p => p.VehicleId is "95" or "102")
            .GroupBy(p => p.Time)
            .OrderBy(g => g.Key);

        var everHeldInBayWhileFoeOccupies = false;
        var violationSteps = new List<double>();

        foreach (var g in byTime)
        {
            string? lane95 = null;
            string? lane102 = null;
            foreach (var p in g)
            {
                if (p.VehicleId == "95") lane95 = p.Lane;
                else if (p.VehicleId == "102") lane102 = p.Lane;
            }

            if (lane95 == BayLane && lane102 == ConflictLane)
            {
                everHeldInBayWhileFoeOccupies = true;
            }

            if (lane95 == Stage2Lane && lane102 == ConflictLane)
            {
                violationSteps.Add(g.Key);
            }
        }

        _out.WriteLine(
            $"veh 95 observed on bay lane {BayLane} while veh 102 occupied {ConflictLane}: "
            + $"{(everHeldInBayWhileFoeOccupies ? "YES (gate engaged for the measured pair)" : "NO")}");
        _out.WriteLine(
            $"veh 95 observed on stage-2 lane {Stage2Lane} while veh 102 occupied {ConflictLane}: "
            + $"{violationSteps.Count} step(s)"
            + (violationSteps.Count > 0 ? $" [{string.Join(",", violationSteps.Take(10))}]" : string.Empty));

        Assert.True(everHeldInBayWhileFoeOccupies,
            $"veh 95 was never observed on its bay lane {BayLane} while veh 102 occupied {ConflictLane} -- "
            + "cannot confirm the admission gate actually engaged for the measured pair (the test would be "
            + "vacuous otherwise).");

        Assert.True(violationSteps.Count == 0,
            $"veh 95 reached cont stage-2 ({Stage2Lane}) while veh 102 still occupied its unconditional foe "
            + $"lane {ConflictLane} at step(s) [{string.Join(",", violationSteps.Take(10))}] -- "
            + "InternalJunctionAdmissionGate should have held veh 95 in its bay; SUMO would never admit it "
            + "there. See docs/NEED-internal-junction-second-stage-admission.md.");
    }
}
