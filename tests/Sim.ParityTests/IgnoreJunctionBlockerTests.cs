using System.Collections.Generic;
using System.Linq;
using Sim.Core;
using Sim.Harness;
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
// NOTE ON FAITHFULNESS: -1 (never ignore) is SUMO's OWN default, so the default path is byte-identical, and
// enabling the knob replicates a documented SUMO option -- but it is NOT what SUMO does by default. SUMO
// avoids the deadlock forming via isLeader() entry-time ordering, which is not ported. Enabling this is the
// pragmatic floor, not a substitute for that.
public class IgnoreJunctionBlockerTests
{
    private readonly ITestOutputHelper _out;

    public IgnoreJunctionBlockerTests(ITestOutputHelper output) => _out = output;

    private static string ScenarioDir()
        => Path.Combine(RepoRoot(), "scenarios", "_repro", "synthetic-junction2");

    private (int Teleports, int Arrived, int StillRunning) Run(double ignoreBlockerSeconds, bool contTurnFix)
    {
        var dir = ScenarioDir();
        var engine = new Engine
        {
            ContTurnInsideJunctionGate = contTurnFix,
            IgnoreJunctionBlockerSeconds = ignoreBlockerSeconds,
        };
        engine.LoadScenario(
            Path.Combine(dir, "grid.net.xml"),
            Path.Combine(dir, "scenario.rou.xml"),
            Path.Combine(dir, "scenario.sumocfg"));

        var traj = engine.Run(2000);

        var last = new Dictionary<string, double>();
        var maxT = 0.0;
        foreach (var p in traj.AllPoints)
        {
            maxT = System.Math.Max(maxT, p.Time);
            last[p.VehicleId] = p.Time;
        }

        return (engine.TeleportCountYield + engine.TeleportCountJam,
                last.Count(kv => kv.Value < maxT),
                last.Count(kv => kv.Value >= maxT));
    }

    // THE DEFAULT MUST BE INERT. -1 is SUMO's own default ("never ignore"), so it must change nothing.
    [Fact]
    public void DefaultIsMinusOne_AndIsNeverIgnore()
    {
        Assert.Equal(-1.0, new Engine().IgnoreJunctionBlockerSeconds);
    }

    // A/B on the scenario where the arm-5 deadlock is reproducible. Reports rather than hard-asserting the
    // exact teleport counts (they are a property of this scenario's calibration, guarded separately by
    // LowDensityTeleportTests); the load-bearing assertion is that enabling the knob does not make teleports
    // WORSE, since its entire purpose is to release stalled vehicles.
    [Fact]
    public void EnablingTheKnob_DoesNotIncreaseTeleports_OnTheArm5DeadlockScenario()
    {
        var offOff = Run(-1.0, contTurnFix: false);
        var offOn = Run(-1.0, contTurnFix: true);
        var fiveOn = Run(5.0, contTurnFix: true);
        var fiveOff = Run(5.0, contTurnFix: false);

        _out.WriteLine(
            "synthetic-junction2, 2000 s -- (ignoreBlocker, contTurnFix) -> teleports / arrived / running@end\n"
            + $"  (-1, off) [today's default] : {offOff.Teleports,3} / {offOff.Arrived,4} / {offOff.StillRunning,3}\n"
            + $"  (-1, ON)                    : {offOn.Teleports,3} / {offOn.Arrived,4} / {offOn.StillRunning,3}\n"
            + $"  ( 5, ON)                    : {fiveOn.Teleports,3} / {fiveOn.Arrived,4} / {fiveOn.StillRunning,3}\n"
            + $"  ( 5, off)                   : {fiveOff.Teleports,3} / {fiveOff.Arrived,4} / {fiveOff.StillRunning,3}\n"
            + "  (real SUMO 1.20.0 fires 0 teleports here)");

        Assert.True(
            fiveOn.Teleports <= offOn.Teleports,
            $"enabling IgnoreJunctionBlockerSeconds=5 must not INCREASE teleports: with the cont-turn fix on, "
            + $"(-1) gave {offOn.Teleports} and (5) gave {fiveOn.Teleports}. The knob exists to release stalled "
            + "vehicles; if it makes matters worse the port is wrong. See docs/NEED-arm5-mutual-junction-deadlock.md.");

        Assert.True(
            fiveOn.Arrived >= offOn.Arrived,
            $"enabling the knob must not reduce arrivals: (-1) {offOn.Arrived} vs (5) {fiveOn.Arrived}.");
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
