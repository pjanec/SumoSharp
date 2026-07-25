using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Sim.LiveCity;
using Xunit;

namespace Sim.LiveCity.Tests;

// docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md / -TASKS.md, Stage A (A1/A2/A3): dataset-factory + NavMode
// data flag (A1), the net.xml drivable-edges fallback (A2), and the ped-capability probe + graceful
// degrade (A3). All fixtures here are tiny, hand-written net.xml strings written to a scratch temp
// dir at test time -- SUMO-free, nothing committed.
public class ArbitraryNetStageATests
{
    // Same repo-root resolution as LiveCitySimTests.RepoRoot (git rev-parse, walk-up fallback).
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

    private static string CreateTempDataset(string netXml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "livecity-stageA-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "net.xml"), netXml);
        return dir;
    }

    // ---- A1: LiveCityConfig.ForDataset factory + NavMode (design §5.1) ----

    [Fact]
    public void ForRepoRoot_ReturnsFieldForFieldDemoConfig_IncludingNavModeNavmesh()
    {
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());

        Assert.Equal(Path.Combine(RepoRoot(), "scenarios", "_ped", "demo_city", "box"), cfg.DatasetDir);
        Assert.Equal(PedNavMode.Navmesh, cfg.NavMode);

        // The PINNED downtown-HERO crop (SUMOSHARP-LIVE-CITY-DECISIONS.md Q7) -- unchanged by the refactor.
        Assert.Equal(2055, cfg.X0);
        Assert.Equal(2055, cfg.Y0);
        Assert.Equal(2895, cfg.X1);
        Assert.Equal(2895, cfg.Y1);

        Assert.Equal(160, cfg.CarTargetConcurrent);
        Assert.Equal(1.5, cfg.LaneChangeMinSpeed);
        Assert.Equal(5.0, cfg.MergeStoppedMinGap);
        Assert.Equal(15.0, cfg.MergeStoppedStrategicDeferDist);
        Assert.True(cfg.YieldEnabled);
        Assert.Equal(5.0, cfg.JunctionYieldTimeoutSeconds);
        Assert.Equal(0.0, cfg.TimeToTeleportSeconds);
        Assert.True(cfg.DeadLaneDriveThrough);
        Assert.True(cfg.WrongLaneRerouteAtApproach);
        Assert.True(cfg.CooperativeLaneChange);
        Assert.Equal(5, cfg.CarSpawnPerStep);
        Assert.Equal(0.5, cfg.Dt);
        Assert.Equal(20260721UL, cfg.PedSeed);
        Assert.Equal(160, cfg.PedPopulationCap);
        Assert.Equal(8.0, cfg.PedSpawnRatePerSecond);
        Assert.Equal(0x243F6A8885A308D3UL, cfg.CarRngSeed);
    }

    [Fact]
    public void ForDataset_ReturnsDatasetDirAndRouteGraphNavMode()
    {
        const string dir = "/some/arbitrary/dataset/dir";
        var cfg = LiveCityConfig.ForDataset(dir);

        Assert.Equal(dir, cfg.DatasetDir);
        Assert.Equal(PedNavMode.RouteGraph, cfg.NavMode);

        // "leaves crop fields at values the road-net path ignores" -- same pinned-crop defaults as
        // ForRepoRoot's shared builder; road-net mode does not consult them (this stage doesn't wire
        // that bypass yet, but the values themselves are untouched by ForDataset).
        Assert.Equal(2055, cfg.X0);
        Assert.Equal(2055, cfg.Y0);
        Assert.Equal(2895, cfg.X1);
        Assert.Equal(2895, cfg.Y1);
    }

    [Fact]
    public void EnvOverride_AppliesIdenticallyToBothFactories()
    {
        Environment.SetEnvironmentVariable("LIVECITY_CARS", "77");
        try
        {
            var repoCfg = LiveCityConfig.ForRepoRoot(RepoRoot());
            var dataCfg = LiveCityConfig.ForDataset("/some/dataset/dir");

            Assert.Equal(77, repoCfg.CarTargetConcurrent);
            Assert.Equal(77, dataCfg.CarTargetConcurrent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVECITY_CARS", null);
        }
    }

    // ---- A2: drivable edges from net.xml fallback (design §5.6) ----

    [Fact]
    public void ForRepoRoot_Demo_CropEdgesMatchTheScenarioRouScrape_FallbackNeverRuns()
    {
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        using var sim = new LiveCitySim(cfg);

        // Independently reproduce the ORIGINAL scenario.rou.xml scrape + the ctor's crop filter (the
        // pre-A2 behaviour) so this test catches the net.xml-derived fallback wrongly running even
        // though scenario.rou.xml is present and non-empty (it must not -- the scrape wins).
        var rouPath = Path.Combine(cfg.DatasetDir, "scenario.rou.xml");
        var scraped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(File.ReadAllText(rouPath), "edges=\"([^\"]*)\""))
        {
            foreach (var tok in m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (seen.Add(tok)) scraped.Add(tok);
            }
        }

        Assert.True(scraped.Count > 0, "expected the demo's scenario.rou.xml scrape to be non-empty");

        var expected = new List<(string Id, int Lane)>();
        foreach (var eid in scraped)
        {
            if (!sim.Network.EdgesById.TryGetValue(eid, out var edge) || edge.Lanes.Count == 0) continue;
            var carLane = edge.Lanes[^1];
            if (carLane.Shape.Count == 0) continue;
            var mid = carLane.Shape[carLane.Shape.Count / 2];
            if (mid.X >= cfg.X0 && mid.X <= cfg.X1 && mid.Y >= cfg.Y0 && mid.Y <= cfg.Y1)
            {
                expected.Add((eid, carLane.Index));
            }
        }

        Assert.Equal(expected, sim.CropEdges);
    }

    private const string NoRouFallbackNetXml = """
        <net>
          <edge id="e1" from="A" to="B">
            <lane id="e1_0" index="0" speed="13.9" length="100.0" shape="100.00,100.00 200.00,100.00"/>
          </edge>
          <edge id="e2" from="B" to="C">
            <lane id="e2_0" index="0" speed="13.9" length="100.0" shape="200.00,100.00 300.00,100.00"/>
          </edge>
          <connection from="e1" to="e2" fromLane="0" toLane="0"/>
          <edge id="sidewalk_only" from="A" to="D">
            <lane id="sidewalk_only_0" index="0" allow="pedestrian" speed="1.5" length="50.0" shape="100.00,200.00 150.00,200.00"/>
          </edge>
          <edge id=":J_0">
            <lane id=":J_0_0" index="0" speed="13.9" length="5.0" shape="200.00,100.00 200.00,105.00"/>
          </edge>
        </net>
        """;

    [Fact]
    public void ForDataset_NoRouFile_FallsBackToNetXmlDrivableEdges()
    {
        var dir = CreateTempDataset(NoRouFallbackNetXml);
        try
        {
            var cfg = LiveCityConfig.ForDataset(dir);
            cfg.X0 = 0; cfg.Y0 = 0; cfg.X1 = 1000; cfg.Y1 = 1000; // cover the whole tiny fixture

            Assert.False(File.Exists(Path.Combine(dir, "scenario.rou.xml")));

            using var sim = new LiveCitySim(cfg);

            Assert.True(sim.CropEdges.Count > 0, "expected a non-empty fallback edge set");
            foreach (var (id, _) in sim.CropEdges)
            {
                Assert.False(id.StartsWith(":", StringComparison.Ordinal), $"internal edge '{id}' leaked into the fallback");
                var edge = sim.Network.EdgesById[id];
                Assert.Contains(edge.Lanes, l => l.AllowsRoadVehicle);
            }

            // The sidewalk-only edge (no vehicle-allowed lane) and the internal edge must both be excluded.
            Assert.DoesNotContain(sim.CropEdges, e => e.Id == "sidewalk_only");
            Assert.DoesNotContain(sim.CropEdges, e => e.Id == ":J_0");
            Assert.Contains(sim.CropEdges, e => e.Id == "e1");
            Assert.Contains(sim.CropEdges, e => e.Id == "e2");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- A3: capability probe + graceful degrade (design §6) ----

    private const string BareVehicleOnlyNetXml = """
        <net>
          <edge id="v1" from="A" to="B">
            <lane id="v1_0" index="0" speed="13.9" length="100.0" shape="100.00,100.00 200.00,100.00"/>
          </edge>
          <edge id="v2" from="B" to="C">
            <lane id="v2_0" index="0" speed="13.9" length="100.0" shape="200.00,100.00 300.00,100.00"/>
          </edge>
          <connection from="v1" to="v2" fromLane="0" toLane="0"/>
        </net>
        """;

    [Fact]
    public void ForDataset_BareVehicleOnlyNet_DegradesGracefully_CarsStepWithoutThrow()
    {
        var dir = CreateTempDataset(BareVehicleOnlyNetXml);
        try
        {
            var cfg = LiveCityConfig.ForDataset(dir);
            cfg.X0 = 0; cfg.Y0 = 0; cfg.X1 = 1000; cfg.Y1 = 1000;
            cfg.CarTargetConcurrent = 5;

            using var sim = new LiveCitySim(cfg);

            Assert.False(sim.PedestriansEnabled);
            Assert.False(sim.CrossingsEnabled);
            Assert.NotNull(sim.PedSource);

            var ex = Record.Exception(() =>
            {
                for (var i = 0; i < 20; i++) sim.Step();
            });
            Assert.Null(ex);

            var snap = sim.Sample();
            Assert.Empty(snap.Peds);
            Assert.Equal(0, sim.OccupiedCrossings);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A crossing edge (function="crossing") whose only lane has NO allow="pedestrian" -- trips
    // PedNetworkParser.Load's "Crossing edge '...' has no pedestrian lane." InvalidOperationException
    // (src/Sim.Pedestrians/PedNetworkParser.cs:65-66), which the ctor's try/catch must degrade from
    // instead of letting it escape.
    private const string MalformedCrossingNetXml = """
        <net>
          <edge id="v1" from="A" to="B">
            <lane id="v1_0" index="0" speed="13.9" length="100.0" shape="100.00,100.00 200.00,100.00"/>
          </edge>
          <edge id="v2" from="B" to="C">
            <lane id="v2_0" index="0" speed="13.9" length="100.0" shape="200.00,100.00 300.00,100.00"/>
          </edge>
          <connection from="v1" to="v2" fromLane="0" toLane="0"/>
          <edge id=":J_c0" function="crossing">
            <lane id=":J_c0_0" index="0" speed="1.5" length="10.0" shape="200.00,90.00 200.00,110.00"/>
          </edge>
        </net>
        """;

    [Fact]
    public void ForDataset_MalformedCrossingLackingPedLane_DegradesToPedestriansDisabled_NoThrow()
    {
        var dir = CreateTempDataset(MalformedCrossingNetXml);
        try
        {
            var cfg = LiveCityConfig.ForDataset(dir);
            cfg.X0 = 0; cfg.Y0 = 0; cfg.X1 = 1000; cfg.Y1 = 1000;

            LiveCitySim? sim = null;
            var ctorEx = Record.Exception(() => sim = new LiveCitySim(cfg));
            Assert.Null(ctorEx);
            using var disposable = sim;

            Assert.NotNull(sim);
            Assert.False(sim!.PedestriansEnabled);
            Assert.False(sim.CrossingsEnabled);

            var stepEx = Record.Exception(() => sim.Step());
            Assert.Null(stepEx);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Sidewalks present, no crossings at all -- CrossingsEnabled must degrade independently of
    // PedestriansEnabled ("walk-only": peds route, no crossing-occupancy gate / crosswalk signals).
    private const string SidewalksNoCrossingsNetXml = """
        <net>
          <edge id="v1" from="A" to="B">
            <lane id="v1_0" index="0" speed="13.9" length="100.0" shape="100.00,100.00 200.00,100.00"/>
          </edge>
          <edge id="v2" from="B" to="C">
            <lane id="v2_0" index="0" speed="13.9" length="100.0" shape="200.00,100.00 300.00,100.00"/>
          </edge>
          <connection from="v1" to="v2" fromLane="0" toLane="0"/>
          <edge id="sidewalk1" from="A" to="D">
            <lane id="sidewalk1_0" index="0" allow="pedestrian" speed="1.5" length="100.0" shape="100.00,150.00 200.00,150.00"/>
          </edge>
        </net>
        """;

    [Fact]
    public void ForDataset_SidewalksButNoCrossings_WalkOnlyDegrade_NoThrow()
    {
        var dir = CreateTempDataset(SidewalksNoCrossingsNetXml);
        try
        {
            var cfg = LiveCityConfig.ForDataset(dir);
            cfg.X0 = 0; cfg.Y0 = 0; cfg.X1 = 1000; cfg.Y1 = 1000;

            using var sim = new LiveCitySim(cfg);

            Assert.True(sim.PedestriansEnabled);
            Assert.False(sim.CrossingsEnabled);

            var ex = Record.Exception(() =>
            {
                for (var i = 0; i < 5; i++) sim.Step();
            });
            Assert.Null(ex);
            Assert.Equal(0, sim.OccupiedCrossings);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
