using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// Laneless direction (docs/LANELESS-DIRECTION.md), Stage 3: external-agent unification. A SUMO
// vehicle in RVO mode (Engine.LanelessRvo) avoids an injected EXTERNAL agent via the SAME reciprocal
// footprint solve it uses for other vehicles -- the "SUMO traffic respects non-SUMO navmesh/RVO
// agents" property, for free, because a vehicle and an agent are the same RvoNeighbor. One centred
// vehicle on a 7.2 m lane; a finite-width static agent is dropped ahead in its path. Asserted
// (behaviourally, no golden): the vehicle drifts laterally around the agent (peak |posLat| off
// centre), never overlaps it (footprints disjoint whenever they overlap longitudinally), passes it,
// and recentres. share=1.0 (one-sided: the engine does all the avoiding for an external agent).
//
// Byte-identity: LanelessRvo defaults false AND the agent is injected only in this test, so every
// committed golden + the determinism hash are unaffected (the full suite proves it).
public class RungRvoAgentUnificationTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "_fixtures", "rvo-agent");

    // The injected agent: static, finite lateral width, centred, in the vehicle's path.
    private const string Lane = "e0_0";
    private const double AgentFront = 60.0;
    private const double AgentLength = 1.0;
    private const double AgentLatPos = 0.0;
    private const double AgentHalfWidth = 0.4;   // width 0.8
    private const double VehHalfWidth = 0.9;     // passenger width 1.8
    private const double VehLength = 5.0;

    [Fact]
    public void RvoVehicle_AvoidsInjectedExternalAgent_NoOverlap_Passes_Recenters()
    {
        var engine = new Engine { LanelessRvo = true };
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        // Drop a finite-width external agent ahead in the lane (Stage-3: a footprint agent, not a
        // full-lane block -- width > 0 so the vehicle can go around it rather than stop dead).
        engine.AddObstacle(engine.GetLane(Lane), frontPos: AgentFront, length: AgentLength,
            latPos: AgentLatPos, width: 2 * AgentHalfWidth);

        var traj = engine.Run(40);

        double peakLat = 0.0, lastLat = 0.0, lastPos = 0.0;
        for (var t = 0; t <= 39; t++)
        {
            if (!traj.TryGet("v0", t, out var p))
            {
                continue;
            }

            peakLat = Math.Max(peakLat, Math.Abs(p.PosLat));
            lastLat = p.PosLat;
            lastPos = p.Pos;

            // No-overlap: whenever the vehicle overlaps the agent longitudinally, their lateral
            // footprints must be disjoint (separation >= sum of half-widths).
            var longOverlap = p.Pos - VehLength < AgentFront && AgentFront - AgentLength < p.Pos;
            if (longOverlap)
            {
                var latSeparation = Math.Abs(p.PosLat - AgentLatPos);
                Assert.True(latSeparation >= VehHalfWidth + AgentHalfWidth - 1e-6,
                    $"vehicle overlapped the external agent at t={t}: lateral separation {latSeparation:F3} " +
                    $"< {VehHalfWidth + AgentHalfWidth} (pos={p.Pos:F2}, posLat={p.PosLat:F3})");
            }
        }

        Assert.True(peakLat > 1.0, $"vehicle did not manoeuvre around the agent (peak |posLat| = {peakLat:F3})");
        Assert.True(lastPos > AgentFront, $"vehicle did not get past the agent (ended at pos {lastPos:F1}, agent at {AgentFront})");
        Assert.True(Math.Abs(lastLat) < 0.2, $"vehicle did not recentre after passing (ended at posLat {lastLat:F3})");
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
