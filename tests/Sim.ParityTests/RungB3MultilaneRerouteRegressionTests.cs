using Sim.Core;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// Pure-CORE regression for the reroute / strategic-lane-change bug surfaced by
// PANIC-EVAC-PHASE5 on a 2-lanes-per-edge net (fixed in Engine.cs via _effectiveRouteIdByEntity /
// EffectiveRouteId / RegisterRerouted). No Sim.Evac here -- just Engine.SetDestination + Step, so
// the guard lives at the layer that broke.
//
// The bug: RerouteActive/UpdateReroutes replace an ACTIVE vehicle's remaining LANE sequence, but the
// two strategic-lane-change reads (KeepRightStrategicStay / TryStrategicLaneChange) kept resolving
// `_routesById[v.Def.RouteId]` -- the ORIGINAL (pre-reroute) route. Once a rerouted vehicle drove
// onto a MULTI-LANE edge that its original route never contained, BestLanesCached walked the stale
// original edge list looking for the vehicle's current edge, did not find it, and threw
// `InvalidDataException: Edge 'X' is not part of the given route`. RungB3RerouteTests never caught it
// because its diamond net is single-lane, so the strategic-lane-change path never runs.
//
// Fixture: the committed organic town (scenarios/_bench/city-organic-L2 -- 2 lanes per edge, rich
// branching), loaded offline (no SUMO). We build up active traffic, then reroute the whole active
// set toward a far corner (forcing paths that diverge from each vehicle's original route onto fresh
// multi-lane edges) and keep stepping. Pre-fix this throws within a few steps; post-fix it runs clean.
public class RungB3MultilaneRerouteRegressionTests
{
    private readonly ITestOutputHelper _out;
    public RungB3MultilaneRerouteRegressionTests(ITestOutputHelper output) => _out = output;

    private static readonly string ScenarioDir =
        Path.Combine(RepoRoot(), "scenarios", "_bench", "city-organic-L2");

    // A far-corner dead-end edge (due east, x=2077.51) -- routing every active vehicle here forces a
    // path that leaves its original boundary-to-boundary route and traverses new interior 2-lane edges.
    private const string FarEdge = "1484";

    [Fact]
    public void ActiveRerouteOntoDivergentMultilanePath_DoesNotReadStaleRoute()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        // Let the loaded demand build up real traffic on the multi-lane mesh.
        for (var i = 0; i < 80; i++)
        {
            engine.Step();
        }

        // Reroute the entire active set toward the far corner. Deterministic: VehicleHandles is the
        // published read surface in fixed (ascending EntityIndex) order. Each success repoints an
        // ACTIVE vehicle onto a route that diverges from its original one.
        var rerouted = 0;
        foreach (var h in engine.VehicleHandles.ToArray())
        {
            if (engine.SetDestination(h, FarEdge))
            {
                rerouted++;
            }
        }

        _out.WriteLine($"activeAtReroute={engine.VehicleHandles.Length} rerouted={rerouted}");
        Assert.True(rerouted > 0,
            "expected at least one active vehicle to reroute onto a path diverging from its original route");

        // Pre-fix, the strategic-lane-change read hit the stale original route for a rerouted vehicle
        // now on a fresh multi-lane edge and threw InvalidDataException. Post-fix, the run completes.
        var ex = Record.Exception(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                engine.Step();
            }
        });

        Assert.Null(ex);
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
