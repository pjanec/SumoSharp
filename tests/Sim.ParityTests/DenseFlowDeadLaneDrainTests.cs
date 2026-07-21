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

            // Vanilla SUMO 1.20.0 on this exact committed cfg: 0 teleports / 290 arrivals (drains
            // fully). Pre-fix SumoSharp gridlocked (10 teleports / 275 arrivals / ~45 stuck). The
            // dead-lane merge-brake + stuck-reroute fix restores drainage to vanilla parity. Guard both
            // halves; a regression toward the gridlock re-inflates teleports and drops arrivals.
            Assert.True(
                stats.TeleportsTotal == 0,
                $"dense synthetic fired {stats.TeleportsTotal} teleports (jam={stats.TeleportsJam}, " +
                $"yield={stats.TeleportsYield}); the dead-lane fix should drain it with 0 (vanilla is 0, " +
                "pre-fix was 10).");

            Assert.True(
                arrivals >= 290,
                $"dense synthetic arrived {arrivals} vehicles; the dead-lane fix should reach vanilla's " +
                "290 (pre-fix was 275, gridlocked).");
        }
        finally
        {
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
