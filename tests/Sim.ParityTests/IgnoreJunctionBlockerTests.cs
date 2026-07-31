using Sim.Core;
using Sim.Harness;
using Sim.Sumo;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// Guards Engine.IgnoreJunctionBlockerSeconds -- the port of SUMO's `--ignore-junction-blocker TIME`
// (MSFrame.cpp:370-371, consumed at MSLink.cpp:1601). See
// docs/NEED-arm5-mutual-junction-deadlock.md.
//
// The mechanism exists because two cars on crossing internal lanes of one junction can car-follow EACH OTHER
// via JunctionYieldConstraint arm 5 (AdaptToJunctionLeader), which has no right-of-way notion and no escape.
// Measured on scenarios/_repro/synthetic-junction2 with ContTurnInsideJunctionGate ON: vehicles 95 and 102 sit
// at speed exactly 0.000 for 121 consecutive steps and are freed only by the 120 s teleport.
//
// HARNESS: this A/B runs entirely through Sim.Sumo.SumoShim.Run -- the SAME in-process CLI path
// LowDensityTeleportTests drives (`-c <cfg> --statistic-output ... --end 2000 --no-step-log true`) --
// so its teleport counts are directly comparable to that test's. An earlier version of this file drove
// engine.LoadScenario(...)+engine.Run(2000) directly, a DIFFERENT code path that reported 4 teleports
// with the knob off where the shim reports 2 for the identical scenario+end-time; the two numbers are
// not comparable and no conclusion can be drawn by mixing them (the direct path does not go through
// SumoShim's own StatisticWriter/engine wiring). The CLI flag is `--ignore-junction-blocker TIME`
// (SumoShim.cs); ContTurnInsideJunctionGate is not a SUMO option, so it is set via SumoShim's
// SUMOSHARP_CONTTURNFIX=1 env-var gate (mirrors LiveCitySim.cs's LIVECITY_CONTTURNFIX for the same
// property) -- see SumoShim.cs's own header for both.
//
// NOTE ON FAITHFULNESS: -1 (never ignore) is SUMO's OWN default, so the default path is byte-identical, and
// enabling the knob replicates a documented SUMO option -- but it is NOT what SUMO does by default. SUMO
// avoids the deadlock forming via isLeader() entry-time ordering, which is not ported. Enabling this is the
// pragmatic floor, not a substitute for that.
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
public class IgnoreJunctionBlockerTests
{
    private readonly ITestOutputHelper _out;

    public IgnoreJunctionBlockerTests(ITestOutputHelper output) => _out = output;

    private static string ScenarioDir()
        => Path.Combine(RepoRoot(), "scenarios", "_repro", "synthetic-junction2");

    // Drives the scenario through SumoShim.Run -- the exact harness LowDensityTeleportTests uses --
    // and reads back the <teleports> breakdown via StatisticOutputParser. `contTurnFix` is threaded
    // through SumoShim's SUMOSHARP_CONTTURNFIX env-var gate (not a real SUMO CLI flag).
    private StatisticRecord Run(double ignoreBlockerSeconds, bool contTurnFix)
    {
        var cfg = Path.Combine(ScenarioDir(), "scenario.sumocfg");
        var outDir = Path.Combine(Path.GetTempPath(), "sumosharp-ignoreblocker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        // This A/B varies SUMOSHARP_CONTTURNFIX deliberately, so the OTHER two junction gates must be
        // HELD FIXED at the engine defaults -- otherwise the four arms below differ in three variables
        // at once and none of them means what its label says (CLAUDE.md measurement-discipline #10).
        // Before SumoShim was switched to the safe EnvGate form they were silently OFF here; pinning
        // them makes that explicit and survives either shim behaviour.
        var prevGates = JunctionGateEnv.PinToEngineDefaults();
        var prevEnv = Environment.GetEnvironmentVariable("SUMOSHARP_CONTTURNFIX");
        try
        {
            Environment.SetEnvironmentVariable("SUMOSHARP_CONTTURNFIX", contTurnFix ? "1" : "0");

            var statistic = Path.Combine(outDir, "stat.xml");
            var args = ignoreBlockerSeconds >= 0.0
                ? new[]
                {
                    "-c", cfg,
                    "--statistic-output", statistic,
                    "--end", "2000",
                    "--no-step-log", "true",
                    "--ignore-junction-blocker", ignoreBlockerSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }
                : new[]
                {
                    "-c", cfg,
                    "--statistic-output", statistic,
                    "--end", "2000",
                    "--no-step-log", "true",
                };

            var exit = SumoShim.Run(args, new StringWriter(), new StringWriter());
            Assert.Equal(0, exit);

            return StatisticOutputParser.Parse(statistic);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUMOSHARP_CONTTURNFIX", prevEnv);
            JunctionGateEnv.Restore(prevGates);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // THE DEFAULT MUST BE INERT. -1 is SUMO's own default ("never ignore"), so it must change nothing.
    [Fact]
    public void DefaultIsMinusOne_AndIsNeverIgnore()
    {
        Assert.Equal(-1.0, new Engine().IgnoreJunctionBlockerSeconds);
    }

    // A/B on the scenario where the arm-5 deadlock is reproducible, driven entirely through the SumoShim
    // CLI path (see class header for why -- direct-engine and shim numbers are NOT comparable). Reports
    // rather than hard-asserting the exact teleport counts (they are a property of this scenario's
    // calibration, guarded separately by LowDensityTeleportTests); the load-bearing assertion is that
    // enabling the knob does not make teleports WORSE, since its entire purpose is to release stalled
    // vehicles.
    [Fact]
    public void EnablingTheKnob_DoesNotIncreaseTeleports_OnTheArm5DeadlockScenario()
    {
        var offOff = Run(-1.0, contTurnFix: false);
        var offOn = Run(-1.0, contTurnFix: true);
        var fiveOn = Run(5.0, contTurnFix: true);
        var fiveOff = Run(5.0, contTurnFix: false);

        _out.WriteLine(
            "[harness: Sim.Sumo.SumoShim.Run, same CLI path as LowDensityTeleportTests] "
            + "synthetic-junction2, 2000 s -- (ignoreBlocker, contTurnFix) -> total/jam/yield teleports\n"
            + $"  (-1, off) [today's default] : {offOff.TeleportsTotal,3} (jam={offOff.TeleportsJam}, yield={offOff.TeleportsYield})\n"
            + $"  (-1, ON)                    : {offOn.TeleportsTotal,3} (jam={offOn.TeleportsJam}, yield={offOn.TeleportsYield})\n"
            + $"  ( 5, ON)                    : {fiveOn.TeleportsTotal,3} (jam={fiveOn.TeleportsJam}, yield={fiveOn.TeleportsYield})\n"
            + $"  ( 5, off)                   : {fiveOff.TeleportsTotal,3} (jam={fiveOff.TeleportsJam}, yield={fiveOff.TeleportsYield})\n"
            + "  (real SUMO 1.20.0 fires 0 teleports here)");

        // THE LOAD-BEARING ASSERTION, restored to its original relative form. Entry 18 replaced it with
        // an absolute `== 0` because, with the other two junction gates silently OFF (the SumoShim bug,
        // fixed in Entry 19), the baseline had fallen to 0 and "must not make it worse" was satisfiable
        // only at exactly 0 -- it measured nothing. With those gates pinned to the engine defaults the
        // baseline is 1 again, so the relative form is meaningful and is the right assertion: the knob
        // exists to RELEASE stalled vehicles, so enabling it must never cost teleports.
        //
        // Measured in the shipped configuration: all four arms fire 1 (jam=0, yield=1) -- the knob is
        // fully inert on this scenario, which is the cleanest possible reading of its purpose.
        Assert.True(
            fiveOn.TeleportsTotal <= offOn.TeleportsTotal && fiveOff.TeleportsTotal <= offOff.TeleportsTotal,
            $"enabling IgnoreJunctionBlockerSeconds=5 must not INCREASE teleports: (-1) gave "
            + $"{offOn.TeleportsTotal}/{offOff.TeleportsTotal} (cont-turn on/off) and (5) gave "
            + $"{fiveOn.TeleportsTotal}/{fiveOff.TeleportsTotal}. The knob exists to release stalled "
            + "vehicles; if it makes matters worse the port is wrong. "
            + "See docs/NEED-arm5-mutual-junction-deadlock.md.");

        // ABSOLUTE ceiling alongside it, which the relative form alone cannot give: a mutual drift where
        // every arm regresses together would satisfy `fiveOn <= offOn` and still be a real regression.
        // 1 is the measured shipped-configuration value; vanilla SUMO 1.20.0 fires 0 here, and that
        // remaining 1 is the honest gap -- do not round it away.
        Assert.True(
            offOn.TeleportsTotal <= 1 && offOff.TeleportsTotal <= 1
                && fiveOn.TeleportsTotal <= 1 && fiveOff.TeleportsTotal <= 1,
            $"synthetic-junction2 teleports exceeded the measured allowance of 1 in at least one arm: "
            + $"(-1,on)={offOn.TeleportsTotal} (-1,off)={offOff.TeleportsTotal} "
            + $"(5,on)={fiveOn.TeleportsTotal} (5,off)={fiveOff.TeleportsTotal}. Vanilla SUMO fires 0.");
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "scenarios"))
                && File.Exists(Path.Combine(d.FullName, "Traffic.sln")))
            {
                return d.FullName;
            }

            d = d.Parent;
        }

        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }
}
