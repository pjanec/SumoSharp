using System.Linq;
using Sim.Core;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// F3/isLeader T2.2 -- docs/F3-ISLEADER-PORT-DESIGN.md §2, §2b, §4; docs/F3-ISLEADER-PORT-TASKS.md T2.2.
//
// STAGE 1 IS PARITY-INERT BY CONSTRUCTION: VehicleRuntime's three new junction timestamps
// (JunctionEntryTime/JunctionEntryTimeNeverYield/JunctionConflictEntryTime) are written at the
// lane-advance seam but consumed by NOTHING (IsLeader is T2.3). These tests observe the write
// directly through Engine.TryGetJunctionEntryTimesForTest -- a diagnostic-only accessor that is
// itself never read by any simulation logic (see that method's own header comment) -- so they pin
// Stage 1's correctness without widening any real read surface.
//
// PROBE-ONLY diagnostic dump (kept, disabled by default via ITestOutputHelper) to make failures
// legible: run with `dotnet test --filter Dump -v n` to see the raw per-step trace.
public class JunctionEntryTimeTests
{
    private readonly ITestOutputHelper _out;

    public JunctionEntryTimeTests(ITestOutputHelper output) => _out = output;

    private static string ScenarioCfg()
        => Path.Combine(RepoRoot(), "scenarios", "_repro", "synthetic-junction2", "scenario.sumocfg");

    private readonly record struct Sample(int Step, string LaneId, long Et, long Etn, long Cet);

    // Drives the engine directly (no SumoShim), one step at a time, recording every sample where
    // `vehicleId` is active -- exactly the per-step trace the design's worked example (§2b) needs.
    private static List<Sample> Trace(string vehicleId, int steps)
    {
        var engine = new Engine();
        engine.LoadScenario(ScenarioCfg());
        var samples = new List<Sample>();
        for (var i = 0; i < steps; i++)
        {
            engine.Run(1);
            if (engine.TryGetJunctionEntryTimesForTest(vehicleId, out var laneId, out var et, out var etn, out var cet))
            {
                samples.Add(new Sample(i, laneId, et, etn, cet));
            }
        }

        return samples;
    }

    // Success condition 1: a vehicle taking NON-CONT link 3 (veh 102, design doc §0/§2b's worked
    // example: "-2437 -> :2336_3_0: entry link, Cont=0 => all three set to the entry step. On exit to
    // "-2417", all three back to MAX."). All three timestamps equal the SAME entry step while veh 102
    // sits on `:2336_3_0`, and all three are MaxValue immediately before entry and immediately after
    // exit.
    [Fact]
    public void NonContLink3_AllThreeTimestampsMatchEntryStep_AndResetOnExit()
    {
        const string vehicleId = "102";
        const string junctionLane = ":2336_3_0";

        var samples = Trace(vehicleId, 700);
        Assert.True(samples.Count > 0, $"vehicle {vehicleId} was never observed active -- depart time/route assumption is stale.");

        var onJunctionLane = samples.Where(s => s.LaneId == junctionLane).ToList();
        Assert.True(onJunctionLane.Count > 0, $"vehicle {vehicleId} never occupied {junctionLane} -- fixture assumption (design doc §0) is stale.");

        // Non-cont entry: isEntryLink AND isConflictEntryLink both fire on the SAME hop (design §2b),
        // so ET == ETN == CET == the step it entered, for EVERY step it sits on this lane (SUMO does
        // not re-timestamp on subsequent steps -- these are lane-ENTRY events, not per-step values).
        var entryStep = onJunctionLane[0].Et;
        Assert.NotEqual(long.MaxValue, entryStep);
        foreach (var s in onJunctionLane)
        {
            Assert.Equal(entryStep, s.Et);
            Assert.Equal(entryStep, s.Etn);
            Assert.Equal(entryStep, s.Cet);
        }

        // The WHOLE-TRACE invariant, which is both stronger and actually true: a vehicle on a NORMAL
        // lane is between junctions, so all three timestamps must read MaxValue; a vehicle on an
        // INTERNAL lane has entered some junction, so ET/ETN must NOT.
        //
        // (An earlier version of this asserted "every sample BEFORE the first `:2336_3_0` sample is
        // MaxValue". That is false and it is why this test failed: veh 102 traverses several EARLIER
        // junctions on its way to 2336, and those samples legitimately carry those junctions' entry
        // stamps. The invariant below is what that assertion was reaching for.)
        var normalSamples = 0;
        var internalSamples = 0;
        foreach (var s in samples)
        {
            if (s.LaneId.Length > 0 && s.LaneId[0] == ':')
            {
                internalSamples++;
                Assert.True(
                    s.Et != long.MaxValue && s.Etn != long.MaxValue,
                    $"step {s.Step}: on internal lane {s.LaneId} but ET/ETN is MaxValue -- an entry link "
                    + "was traversed without stamping the entry time.");
            }
            else
            {
                normalSamples++;
                Assert.True(
                    s.Et == long.MaxValue && s.Etn == long.MaxValue && s.Cet == long.MaxValue,
                    $"step {s.Step}: on normal lane {s.LaneId} but timestamps are "
                    + $"ET={s.Et} ETN={s.Etn} CET={s.Cet} -- an exit link failed to reset them.");
            }
        }

        // Non-vacuity: the invariant above is only meaningful if the trace actually covers both cases.
        Assert.True(normalSamples > 0 && internalSamples > 0,
            $"trace covered normal={normalSamples} internal={internalSamples} samples; both must be non-zero.");

        // After exit: MSLink::isExitLink (MSVehicle.cpp:4363-4368) resets all three. As in the cont
        // test below, the sample immediately after the junction lane is NOT necessarily on a normal
        // lane -- a vehicle can cross two junction boundaries in one step, in which case that sample
        // legitimately carries a FRESH entry stamp for the next junction. Asserting MaxValue there
        // would assert something about step granularity, not about this port. So: prove the reset
        // fired (the entry step is no longer the one recorded on this junction lane), then check
        // MaxValue at the first subsequent NORMAL-lane sample.
        var lastOnLaneIndex = samples.IndexOf(onJunctionLane[^1]);
        Assert.True(lastOnLaneIndex + 1 < samples.Count, $"vehicle {vehicleId} was not observed after leaving {junctionLane} -- extend the step budget.");
        var afterExit = samples[lastOnLaneIndex + 1];
        Assert.NotEqual(junctionLane, afterExit.LaneId);
        Assert.True(
            afterExit.Et != entryStep,
            $"after leaving {junctionLane} the entry time is still the entry step ({entryStep}), so the "
            + "exit reset did not fire.");

        var normalIndex = samples.FindIndex(lastOnLaneIndex + 1, s => s.LaneId.Length > 0 && s.LaneId[0] != ':');
        Assert.True(
            normalIndex >= 0,
            $"vehicle {vehicleId} was never observed on a normal lane after leaving {junctionLane} -- extend the step budget.");
        Assert.Equal(long.MaxValue, samples[normalIndex].Et);
        Assert.Equal(long.MaxValue, samples[normalIndex].Etn);
        Assert.Equal(long.MaxValue, samples[normalIndex].Cet);
    }

    // Success condition 2: a vehicle taking CONT link 18 (veh 95, design doc §0/§2b's worked example:
    // "2417 -> :2336_18_0: entry link, Cont=1 => ET = ETN = t_enter, and CET stays MAX ... Then
    // :2336_18_0 -> :2336_42_0: internal->internal => CET = t_stage2, and ET is restored to ETN".
    //
    // THE LOAD-BEARING ASSERTION is `CET == long.MaxValue` while veh 95 sits on the STAGE-1 bay lane
    // `:2336_18_0` -- it is what distinguishes a correct cont port (conflict-entry does NOT fire on a
    // cont link, MSLink.cpp:1292-1296's `!myAmCont` guard) from a plausible WRONG port that stamps
    // CET at ordinary junction entry regardless of cont-ness.
    [Fact]
    public void ContLink18_StageOneNeverSetsConflictEntry_StageTwoSetsItAndRenewsEntryTime()
    {
        const string vehicleId = "95";
        const string stage1Lane = ":2336_18_0";
        const string stage2Lane = ":2336_42_0";

        var samples = Trace(vehicleId, 700);
        Assert.True(samples.Count > 0, $"vehicle {vehicleId} was never observed active -- depart time/route assumption is stale.");

        var onStage1 = samples.Where(s => s.LaneId == stage1Lane).ToList();
        var onStage2 = samples.Where(s => s.LaneId == stage2Lane).ToList();
        Assert.True(onStage1.Count > 0, $"vehicle {vehicleId} never occupied {stage1Lane} -- fixture assumption (design doc §0) is stale.");
        Assert.True(onStage2.Count > 0, $"vehicle {vehicleId} never occupied {stage2Lane} -- fixture assumption (design doc §0) is stale.");

        // Stage 1 (the waiting bay): ET == ETN == t1 (the entry step), and -- THE LOAD-BEARING CHECK --
        // CET stays long.MaxValue for EVERY step spent in the bay (isConflictEntryLink is false for a
        // cont link's entry hop).
        var t1 = onStage1[0].Et;
        Assert.NotEqual(long.MaxValue, t1);
        foreach (var s in onStage1)
        {
            Assert.Equal(t1, s.Et);
            Assert.Equal(t1, s.Etn);
            Assert.Equal(long.MaxValue, s.Cet);
        }

        // Stage 2 (the actual conflict area): CET == t2 > t1 (set on THIS hop), and ET == ETN == t1
        // STILL (MSVehicle.cpp:4361's "renew yielded request": ET is restored from ETN, which was never
        // touched, so the ORIGINAL entry step survives across the internal->internal hop).
        var t2 = onStage2[0].Cet;
        Assert.NotEqual(long.MaxValue, t2);
        Assert.True(t2 > t1, $"stage-2 CET ({t2}) must be strictly later than stage-1 entry ET ({t1}).");
        foreach (var s in onStage2)
        {
            Assert.Equal(t1, s.Et);
            Assert.Equal(t1, s.Etn);
            Assert.Equal(t2, s.Cet);
        }

        // After exit: MSLink::isExitLink resets all three. But the sample immediately after stage 2
        // is NOT necessarily on a normal lane -- measured here, veh 95 leaves `:2336_42_0` and enters
        // junction 444's internal lane `:444_0_0` in the SAME step, so that sample legitimately shows
        // a fresh entry stamp (ET == ETN == CET == that step) rather than MaxValue. Asserting
        // MaxValue there would be asserting that a vehicle cannot cross two junction boundaries in one
        // step, which is not a property of the engine and has nothing to do with this port.
        //
        // So check the two things that ARE the port's contract:
        //   (a) the exit reset genuinely fired -- ET is no longer the stage-1 entry step t1. Without
        //       the reset ET would still read t1, so this is the load-bearing half.
        //   (b) at the first subsequent sample on a NORMAL (non-':') lane, all three are MaxValue.
        var lastOnStage2Index = samples.IndexOf(onStage2[^1]);
        Assert.True(lastOnStage2Index + 1 < samples.Count, $"vehicle {vehicleId} was not observed after leaving {stage2Lane} -- extend the step budget.");
        var afterExit = samples[lastOnStage2Index + 1];
        Assert.NotEqual(stage2Lane, afterExit.LaneId);
        Assert.True(
            afterExit.Et != t1,
            $"after leaving {stage2Lane} the entry time is still the stage-1 entry step ({t1}), so the "
            + "exit reset (MSLink::isExitLink, MSVehicle.cpp:4365-4367) did not fire.");

        var normalIndex = samples.FindIndex(lastOnStage2Index + 1, s => s.LaneId.Length > 0 && s.LaneId[0] != ':');
        Assert.True(
            normalIndex >= 0,
            $"vehicle {vehicleId} was never observed on a normal lane after leaving {stage2Lane} -- extend the step budget.");
        var normalAfterExit = samples[normalIndex];
        Assert.Equal(long.MaxValue, normalAfterExit.Et);
        Assert.Equal(long.MaxValue, normalAfterExit.Etn);
        Assert.Equal(long.MaxValue, normalAfterExit.Cet);
    }

    // PROBE: dumps the raw per-step trace for both vehicles (skipped by default -- Xunit has no
    // built-in "diagnostic only" trait, so this stays a normal Fact but asserts nothing beyond
    // non-emptiness; its purpose is `dotnet test --filter Dump -v n` output for a future debugging
    // session, not a parity guard).
    [Fact]
    public void Dump_RawTraceForBothVehicles()
    {
        foreach (var vehicleId in new[] { "95", "102" })
        {
            var samples = Trace(vehicleId, 700);
            _out.WriteLine($"--- vehicle {vehicleId} ({samples.Count} samples) ---");
            foreach (var s in samples)
            {
                _out.WriteLine($"step={s.Step,4} lane={s.LaneId,-16} ET={s.Et,10} ETN={s.Etn,10} CET={s.Cet,10}");
            }

            Assert.NotEmpty(samples);
        }
    }

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
}
