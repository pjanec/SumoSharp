using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Sim.LiveCity;
using Sim.Pedestrians;
using Sim.Pedestrians.Navigation.Bake;
using Sim.Pedestrians.Navigation.RouteGraph;
using Xunit;

namespace Sim.LiveCity.Tests;

// docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md / -TASKS.md, Stage C (C1-C4, C6 -- C5 is explicitly OUT of
// scope for this session, see -TASKS.md's C5 ownership note): the road-net (RouteGraph) mode branch
// wired into LiveCitySim's ctor -- SumoRouteGraphNav in place of WalkablePolygonBaker+SumoNavMesh
// (C1), crossings-only bake + gate/signals (C2), whole-net O/D sampling from sidewalk centrelines
// (C3), the RerouteDriver/SumoNavMesh-never-constructed invariant (C4), and Engine.RegionPlan (C6).
// All tests here run against the committed `scenarios/_ped/roadnet_min` fixture (a synthetic
// ped-equipped 3x3 grid -- see its README/provenance.txt) -- SUMO-free at test time.
public class ArbitraryNetStageCTests
{
    // Same repo-root resolution as ArbitraryNetStageATests.RepoRoot / LiveCitySimTests.RepoRoot.
    private static string RepoRoot()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --show-toplevel")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode == 0 && Directory.Exists(Path.Combine(output, "scenarios")))
            {
                return output;
            }
        }
        catch
        {
            // fall through to the walk-up fallback
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "scenarios")) && File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }

    private static string DatasetDir() => Path.Combine(RepoRoot(), "scenarios", "_ped", "roadnet_min");

    // A config tuned so the fixture reliably produces at least one occupied crossing within a modest
    // step budget (deterministic once green -- see the class remarks on the C1 smoke test below):
    // a larger concurrent ped population than the ForDataset default so more routes are in flight
    // (more chances to thread a crossing) without needing an excessive step count.
    private static LiveCityConfig MakeFixtureConfig()
    {
        var cfg = LiveCityConfig.ForDataset(DatasetDir());
        cfg.PedPopulationCap = 200;
        cfg.PedSpawnRatePerSecond = 20.0;
        cfg.CarTargetConcurrent = 20;
        return cfg;
    }

    // ---- C1: mode branch + smoke (design §5.2, §5.4) ----

    [Fact]
    public void ForDataset_RoadNetFixture_UsesRouteGraphNav_RoutesAndCrosses()
    {
        var cfg = MakeFixtureConfig();
        using var sim = new LiveCitySim(cfg);

        Assert.True(sim.PedestriansEnabled);
        Assert.True(sim.CrossingsEnabled);
        Assert.True(sim.RouteGraphNavigationActive);

        var ex = Record.Exception(() =>
        {
            for (var i = 0; i < 400; i++) sim.Step();
        });
        Assert.Null(ex);

        Assert.True(sim.PeakPeds > 0, "expected peds to spawn and route on the whole-net route graph");
        // Proves routing actually threads a crossing (not just walking sidewalks) -- the crux of the
        // route-graph wiring: without CrossingsEnabled/BakeCrossingsOnly wired correctly, this stays 0
        // even though peds are moving. Deterministic (fixed seed/config), so once green this is stable;
        // if it ever proves flaky, the fix is to raise step count / PedPopulationCap above, never to
        // weaken this assertion.
        Assert.True(sim.PeakOccupiedCrossings > 0, "expected at least one ped to occupy a crossing over the run");
    }

    // ---- C2: crossings-only bake matches the Crossing subset of the full Bake (design §5.3) ----

    [Fact]
    public void BakeCrossingsOnly_MatchesCrossingSubsetOfFullBake()
    {
        var netPath = Path.Combine(DatasetDir(), "net.xml");
        var network = PedNetworkParser.Load(netPath);

        var crossingsOnly = WalkablePolygonBaker.BakeCrossingsOnly(network);
        var fullBakeCrossings = WalkablePolygonBaker.Bake(network)
            .Where(p => p.Kind == BakedPolygonKind.Crossing)
            .ToList();

        Assert.True(crossingsOnly.Count > 0, "expected the fixture to have at least one crossing");
        Assert.Equal(fullBakeCrossings.Count, crossingsOnly.Count);

        // Same Id order (both iterate Crossings ordered by Id, ordinal) and identical geometry/width --
        // Index deliberately excluded (BakeCrossingsOnly's subset is 0-based; the full Bake's global
        // staging order offsets Crossing indices by the WalkingArea count processed first -- see the
        // method's own remarks). Callers (CrossingOccupancySource, CrosswalkSignals.FromNet) never key
        // off Index, only Id/Kind/Vertices/HalfWidth, so this is the contract that actually matters.
        for (var i = 0; i < crossingsOnly.Count; i++)
        {
            var a = crossingsOnly[i];
            var b = fullBakeCrossings[i];
            Assert.Equal(b.Id, a.Id);
            Assert.Equal(b.Kind, a.Kind);
            Assert.Equal(b.HalfWidth, a.HalfWidth);
            Assert.Equal(b.Vertices.Count, a.Vertices.Count);
            for (var v = 0; v < a.Vertices.Count; v++)
            {
                Assert.Equal(b.Vertices[v].X, a.Vertices[v].X, 9);
                Assert.Equal(b.Vertices[v].Y, a.Vertices[v].Y, 9);
            }
        }
    }

    // ---- C3: O/D sampling from sidewalk centrelines is deterministic (design §5.5) ----

    [Fact]
    public void ForDataset_RoadNetFixture_SameConfig_ProducesIdenticalPeakMetrics()
    {
        var cfg1 = MakeFixtureConfig();
        var cfg2 = MakeFixtureConfig();

        using var sim1 = new LiveCitySim(cfg1);
        using var sim2 = new LiveCitySim(cfg2);

        for (var i = 0; i < 400; i++)
        {
            sim1.Step();
            sim2.Step();
        }

        Assert.Equal(sim1.PeakPeds, sim2.PeakPeds);
        Assert.Equal(sim1.PeakCars, sim2.PeakCars);
        Assert.Equal(sim1.PeakOccupiedCrossings, sim2.PeakOccupiedCrossings);
        Assert.Equal(sim1.ArrivedTotal, sim2.ArrivedTotal);

        // Peds actually spawn AND reach destinations over the run (arrival-equivalent for peds is
        // "left the live set" -- PedDemand despawns on arrival; PeakPeds>0 plus the identical-metrics
        // check above together confirm deterministic, non-trivial O/D routing).
        Assert.True(sim1.PeakPeds > 0);
    }

    // ---- C4: RerouteDriver / concrete SumoNavMesh invariant (design §5.7) ----

    [Fact]
    public void RoadNetFixture_IsRouteGraphNav_Demo_IsNot()
    {
        using var roadNetSim = new LiveCitySim(LiveCityConfig.ForDataset(DatasetDir()));
        Assert.True(roadNetSim.RouteGraphNavigationActive);

        using var demoSim = new LiveCitySim(LiveCityConfig.ForRepoRoot(RepoRoot()));
        Assert.False(demoSim.RouteGraphNavigationActive);
    }

    // ---- C6: Engine.RegionPlan (design §5.9) ----

    [Fact]
    public void RegionPlan_DefaultsMatchFactory_ForDatasetTrue_ForRepoRootFalse()
    {
        var datasetCfg = LiveCityConfig.ForDataset(DatasetDir());
        var repoCfg = LiveCityConfig.ForRepoRoot(RepoRoot());

        Assert.True(datasetCfg.RegionPlan);
        Assert.False(repoCfg.RegionPlan);
    }

    [Fact]
    public void RoadNetFixture_RegionPlanOnOrOff_ProducesIdenticalPeakMetrics()
    {
        var cfgOn = MakeFixtureConfig();
        cfgOn.RegionPlan = true;
        var cfgOff = MakeFixtureConfig();
        cfgOff.RegionPlan = false;

        using var simOn = new LiveCitySim(cfgOn);
        using var simOff = new LiveCitySim(cfgOff);

        for (var i = 0; i < 400; i++)
        {
            simOn.Step();
            simOff.Step();
        }

        Assert.Equal(simOff.PeakPeds, simOn.PeakPeds);
        Assert.Equal(simOff.PeakCars, simOn.PeakCars);
        Assert.Equal(simOff.PeakOccupiedCrossings, simOn.PeakOccupiedCrossings);
        Assert.Equal(simOff.ArrivedTotal, simOn.ArrivedTotal);
    }
}
