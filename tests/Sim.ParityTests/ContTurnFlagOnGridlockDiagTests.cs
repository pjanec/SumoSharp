using System.Collections.Generic;
using System.Linq;
using Sim.Core;
using Sim.Harness;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// TEMPORARY MEASUREMENT INSTRUMENT (always passes) -- docs/F3-SESSION-LOG.md T1.7/T1.9.
//
// The five gridlock diagnostics all construct their own Engine, so they exercise the DEFAULT
// (ContTurnInsideJunctionGate == false) configuration and cannot tell us whether the cont-turn fix is
// still blocked. This runs the same saturated-grid scenarios with the flag ON and prints the numbers,
// so the decision to enable the flag by default is made on measurement rather than assumption.
public class ContTurnFlagOnGridlockDiagTests
{
    private readonly ITestOutputHelper _out;

    public ContTurnFlagOnGridlockDiagTests(ITestOutputHelper output) => _out = output;

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

    private static int Arrived(TrajectorySet traj)
    {
        var last = new Dictionary<string, double>();
        var maxT = 0.0;
        foreach (var p in traj.AllPoints)
        {
            maxT = System.Math.Max(maxT, p.Time);
            last[p.VehicleId] = p.Time;
        }

        return last.Count(kv => kv.Value < maxT);
    }

    private (int Stuck, int Arrived) Run(string scenarioRelDir, int steps, bool contTurnFix, bool coordinatedLc)
    {
        var dir = Path.Combine(RepoRoot(), scenarioRelDir);
        var engine = new Engine
        {
            CoordinatedLaneChange = coordinatedLc,
            ContTurnInsideJunctionGate = contTurnFix,
        };
        engine.LoadScenario(
            Path.Combine(dir, "net.net.xml"),
            Path.Combine(dir, "rou.rou.xml"),
            Path.Combine(dir, "config.sumocfg"));

        var traj = engine.Run(steps);
        return (StuckCount(traj), Arrived(traj));
    }

    [Fact]
    public void ContTurnFlag_OnVsOff_SaturatedGridComparison()
    {
        // Mirrors RungHDp2g2CoordinatedLaneChangeTests (ceiling: stuck <= 5) and
        // WillPassSaturationDiagTests / DenseFlowDeadLaneDrainTests on the same family of nets.
        var cases = new (string Dir, int Steps, bool Lc, string Label, string Ceiling)[]
        {
            ("scenarios/_diag/willpass-saturation", 700, true,  "willpass-saturation (dense LC)", "stuck <= 5"),
            ("scenarios/_diag/willpass-saturation", 700, false, "willpass-saturation (plain)",    "stuck ~0"),
        };

        foreach (var c in cases)
        {
            var off = Run(c.Dir, c.Steps, contTurnFix: false, coordinatedLc: c.Lc);
            var on = Run(c.Dir, c.Steps, contTurnFix: true, coordinatedLc: c.Lc);
            _out.WriteLine(
                $"{c.Label,-34} [{c.Ceiling}]  stuck: OFF={off.Stuck,4} -> ON={on.Stuck,4}   "
                + $"arrived: OFF={off.Arrived,4} -> ON={on.Arrived,4}");
        }

        Assert.True(true, "diagnostic-only; see printed comparison.");
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
