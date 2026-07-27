using Sim.Harness;
using Sim.Sumo;
using Xunit;

namespace Sim.ParityTests;

// Low-density spurious-teleport regression guard (docs/SUMOSHARP-LOWDENSITY-TELEPORT-DESIGN.md).
//
// The committed repro scenarios/_repro/synthetic-junction2 (an irregular synthetic net with a handful
// of TLS junctions on short approaches) runs uncongested (~124 peak concurrent) yet SumoSharp used to
// fire far more jam/yield teleports than vanilla SUMO 1.20.0 (vanilla = 0). Root cause (mechanism A):
// JunctionYieldConstraint decided right-of-way from the static netconvert <request> matrix, which is
// TL-blind, so a vehicle holding a protected-green ('G') traffic signal still yielded to junction foes
// and froze at the stop line until it hit time-to-teleport. The havePriority-aware gate
// (Engine.EgoLinkHasSignalPriority, wired into the cautious-approach / crossing-yield / sameTarget
// arms) restores the signal's authority: a 'G' movement yields to no one. That cut this scenario's
// teleports from 10 to 5.
//
// This is an ENGINE-ONLY, offline check (no SUMO): it drives the committed scenario through the same
// in-process SumoShim path the SumoData serve pipeline uses and reads the produced <teleports> count.
// The bound guards the mechanism-A fix from regressing (back toward 10). The 5 remaining teleports are
// a SEPARATE, pre-existing priority-junction on-junction wedge (mechanism B, tracked as task T3); when
// that lands this bound tightens toward vanilla's 0.
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
public class LowDensityTeleportTests
{
    [Fact]
    public void SyntheticJunction2_TlPriorityVehiclesDoNotSpuriouslyTeleport()
    {
        var scenarioDir = Path.Combine(RepoRoot(), "scenarios", "_repro", "synthetic-junction2");
        var cfg = Path.Combine(scenarioDir, "scenario.sumocfg");
        Assert.True(File.Exists(cfg), $"repro scenario missing: {cfg}");

        var outDir = Path.Combine(Path.GetTempPath(), "sumosharp-lowdens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var statistic = Path.Combine(outDir, "stat.xml");
            var exit = SumoShim.Run(
                new[]
                {
                    "-c", cfg,
                    "--statistic-output", statistic,
                    "--end", "2000",
                    "--no-step-log", "true",
                },
                new StringWriter(), new StringWriter());

            Assert.Equal(0, exit);

            var stats = StatisticOutputParser.Parse(statistic);

            // History: the havePriority fix dropped this from 10 -> 5; the GAP-1 dead-lane fix
            // (merge brake + stuck-reroute, docs/HIGH-DENSITY-CALIBRATION-DESIGN.md §2.3.5) then dropped
            // it further to 1 -- the residual mechanism-B wedge cars now reroute off their dead lane
            // instead of teleporting. Guard at <= 2 (current is 1, tiny margin): any regression in
            // either the signal-priority gate or the dead-lane fix re-inflates the count. (Vanilla SUMO
            // fires 0 here.)
            Assert.True(
                stats.TeleportsTotal <= 2,
                $"synthetic-junction2 fired {stats.TeleportsTotal} teleports (jam={stats.TeleportsJam}, " +
                $"yield={stats.TeleportsYield}); the havePriority + dead-lane fixes should hold it at <= 2 " +
                "(pre-fix was 10, then 5; vanilla SUMO is 0).");
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
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
