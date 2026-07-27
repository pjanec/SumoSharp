using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Sim.LiveCity;
using Xunit;

namespace Sim.LiveCity.Tests;

// docs/EXTERNAL-NET-LOADING-DESIGN.md / -TASKS.md, Stage C (C1/C2/C3): loading a net by explicit path
// or from a .sumocfg, pedestrian ground elevation on a 3-D net, and live density setters.
//
// The fixture is scenarios/_ped/georef_min -- the committed synthetic stand-in for a SumoData
// preprocess.py Geneva cut (design §6, produced by scripts/gen-georef-fixture.sh): georeferenced
// UTM32N, 3-D lane shapes at ~370-400 m, coordinates ~(91850, 73960) far from the origin, and named
// scenario.net.xml + scenario.sumocfg rather than net.xml. It is committed XML, so this test needs no
// SUMO -- the offline loop stays SUMO-free (CLAUDE.md).
public class ExternalNetLoadingTests
{
    // Same repo-root resolution as the other suites here (git rev-parse, walk-up fallback).
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

    private static string FixtureDir() => Path.Combine(RepoRoot(), "scenarios", "_ped", "georef_min");
    private static string FixtureNet() => Path.Combine(FixtureDir(), "scenario.net.xml");
    private static string FixtureRoutes() => Path.Combine(FixtureDir(), "scenario.rou.xml");
    private static string FixtureCfg() => Path.Combine(FixtureDir(), "scenario.sumocfg");

    private static LiveCityConfig FixtureConfigByNetPath()
    {
        var cfg = LiveCityConfig.ForDataset(FixtureDir());
        cfg.NetPath = FixtureNet();
        cfg.RoutePath = FixtureRoutes();
        return cfg;
    }

    // ---- F1: the fixture itself is what the design says it is ------------------------------------
    // Guards the PROPERTIES the rest of this file relies on. If someone regenerates the fixture
    // without the anchor-and-crop step, its coordinates collapse to ~0..400 and the float-precision
    // tests below would pass vacuously -- so assert the far-from-origin frame explicitly.

    [Fact]
    public void Fixture_IsGeoreferenced3DAndFarFromOrigin()
    {
        var netText = File.ReadAllText(FixtureNet());

        Assert.Contains("+proj=utm +zone=32", netText);
        Assert.Contains("netOffset=", netText);

        var cfg = FixtureConfigByNetPath();
        using var sim = new LiveCitySim(cfg);

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var anyZ = false;
        var zMin = double.PositiveInfinity;
        var zMax = double.NegativeInfinity;

        foreach (var lane in sim.Network.LanesById.Values)
        {
            foreach (var (x, y) in lane.Shape)
            {
                if (x < minX) minX = x;
                if (y < minY) minY = y;
            }

            if (lane.ShapeZ is { Count: > 0 } zs)
            {
                anyZ = true;
                foreach (var z in zs)
                {
                    if (z < zMin) zMin = z;
                    if (z > zMax) zMax = z;
                }
            }
        }

        Assert.True(minX > 50000.0, $"fixture must be far from the origin to exercise the recenter; minX={minX}");
        Assert.True(minY > 50000.0, $"fixture must be far from the origin to exercise the recenter; minY={minY}");
        Assert.True(anyZ, "fixture must be 3-D (Lane.ShapeZ non-null)");
        Assert.InRange(zMin, 360.0, 410.0);
        Assert.InRange(zMax, 360.0, 410.0);
    }

    // ---- C1: NetPath -----------------------------------------------------------------------------

    [Fact]
    public void NetPath_LoadsANetNotNamedNetXml_AndSpawnsCarsAndPeds()
    {
        // The fixture dir deliberately contains NO net.xml, so this only works via NetPath.
        Assert.False(File.Exists(Path.Combine(FixtureDir(), "net.xml")));

        using var sim = new LiveCitySim(FixtureConfigByNetPath());

        Assert.True(sim.Network.EdgesById.Count > 0);
        Assert.True(sim.PedestriansEnabled, "the fixture has guessed sidewalks");
        Assert.True(sim.CrossingsEnabled, "the fixture has guessed crossings");
        Assert.True(sim.RouteGraphNavigationActive, "ForDataset => arbitrary-net RouteGraph mode");
        Assert.True(sim.CropEdges.Count > 0, "expected the route-file scrape to find drivable edges");

        for (var i = 0; i < 200; i++)
        {
            sim.Step();
        }

        // Not merely "did not throw": cars and peds must actually be on the arbitrary graph.
        Assert.True(sim.CurrentCars > 0, $"expected live cars after 200 steps, got {sim.CurrentCars}");
        var snap = sim.Sample();
        Assert.True(snap.Cars.Count > 0, "expected cars in the sampled frame");
        Assert.True(sim.PeakPeds > 0, "expected peds to spawn on the fixture's sidewalk graph");
        Assert.True(snap.Peds.Count > 0, "expected peds in the sampled frame");
    }

    [Fact]
    public void ResolveNetPath_UnsetFallsBackToDatasetDirNetXml()
    {
        var cfg = new LiveCityConfig { DatasetDir = "/some/dataset" };
        Assert.Equal(Path.Combine("/some/dataset", "net.xml"), cfg.ResolveNetPath());

        cfg.NetPath = "/elsewhere/scenario.net.xml";
        Assert.Equal("/elsewhere/scenario.net.xml", cfg.ResolveNetPath());
    }

    [Fact]
    public void ResolveRoutePaths_PrecedenceIsPathsThenPathThenConvention()
    {
        var cfg = new LiveCityConfig { DatasetDir = "/ds" };
        Assert.Equal(new[] { Path.Combine("/ds", "scenario.rou.xml") }, cfg.ResolveRoutePaths());

        cfg.RoutePath = "/r/one.rou.xml";
        Assert.Equal(new[] { "/r/one.rou.xml" }, cfg.ResolveRoutePaths());

        cfg.RoutePaths = new[] { "/r/a.xml", "/r/b.xml" };
        Assert.Equal(new[] { "/r/a.xml", "/r/b.xml" }, cfg.ResolveRoutePaths());
    }

    // ---- C1: ForSumocfg --------------------------------------------------------------------------

    [Fact]
    public void ForSumocfg_ResolvesRelativePathsAgainstTheCfgDir_AndAppliesArbitraryNetDefaults()
    {
        var cfg = LiveCityConfig.ForSumocfg(FixtureCfg());

        Assert.Equal(Path.GetFullPath(FixtureNet()), cfg.NetPath);
        Assert.NotNull(cfg.RoutePaths);
        Assert.Contains(Path.GetFullPath(FixtureRoutes()), cfg.RoutePaths!);
        Assert.Equal(Path.GetFullPath(FixtureDir()), Path.GetFullPath(cfg.DatasetDir));
        Assert.Equal(PedNavMode.RouteGraph, cfg.NavMode);
        Assert.True(cfg.RegionPlan);
    }

    [Fact]
    public void ForSumocfg_BuiltSim_StepsAndCarriesCarsAndPeds()
    {
        using var sim = new LiveCitySim(LiveCityConfig.ForSumocfg(FixtureCfg()));

        Assert.True(sim.CropEdges.Count > 0);
        for (var i = 0; i < 200; i++)
        {
            sim.Step();
        }

        Assert.True(sim.CurrentCars > 0);
        Assert.True(sim.PeakPeds > 0);
    }

    [Fact]
    public void ForSumocfg_AbsoluteInputPaths_AreTakenAsIs_ThePreprocessPyForm()
    {
        // SumoData's preprocess.py emits ABSOLUTE net/route paths (SUBAREA-METHOD.md §8) while
        // demo-city emits relative ones. Write the absolute form to a temp dir far from the fixture:
        // if the resolver naively combined, the result would point inside the temp dir and not exist.
        var tempDir = Path.Combine(Path.GetTempPath(), "livecity-abs-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgPath = Path.Combine(tempDir, "absolute.sumocfg");
            File.WriteAllText(cfgPath,
                "<configuration><input>"
                + $"<net-file value=\"{Path.GetFullPath(FixtureNet())}\"/>"
                + $"<route-files value=\"{Path.GetFullPath(FixtureRoutes())}\"/>"
                + "</input></configuration>");

            var cfg = LiveCityConfig.ForSumocfg(cfgPath);

            Assert.Equal(Path.GetFullPath(FixtureNet()), cfg.NetPath);
            Assert.Equal(new[] { Path.GetFullPath(FixtureRoutes()) }, cfg.RoutePaths);

            // And it really loads -- the point of the exercise.
            using var sim = new LiveCitySim(cfg);
            Assert.True(sim.Network.EdgesById.Count > 0);
            Assert.True(sim.CropEdges.Count > 0);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ForSumocfg_MultipleRouteFiles_ScrapesTheUnion_NotJustTheFirst()
    {
        // A real cut's <route-files> leads with vType files and puts the demand LAST
        // (scenarios/_ped/subarea-box/scenario.sumocfg). Scraping only the first entry would find zero
        // edges and then silently fall through to the net-derived fallback -- a wrong-but-plausible
        // result. Assert the union equals what the routes file alone yields.
        var tempDir = Path.Combine(Path.GetTempPath(), "livecity-multi-rou-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var vTypePath = Path.Combine(tempDir, "vType.config.xml");
            File.WriteAllText(vTypePath, "<routes><vType id=\"car\" vClass=\"passenger\"/></routes>");

            var cfgPath = Path.Combine(tempDir, "multi.sumocfg");
            File.WriteAllText(cfgPath,
                "<configuration><input>"
                + $"<net-file value=\"{Path.GetFullPath(FixtureNet())}\"/>"
                + $"<route-files value=\"{vTypePath},{Path.GetFullPath(FixtureRoutes())}\"/>"
                + "</input></configuration>");

            var cfg = LiveCityConfig.ForSumocfg(cfgPath);
            Assert.Equal(2, cfg.RoutePaths!.Count);

            using var multi = new LiveCitySim(cfg);

            var single = FixtureConfigByNetPath();
            using var routesOnly = new LiveCitySim(single);

            Assert.True(multi.CropEdges.Count > 0);
            Assert.Equal(
                routesOnly.CropEdges.Select(e => e.Id).ToArray(),
                multi.CropEdges.Select(e => e.Id).ToArray());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ForSumocfg_MissingNetFile_ThrowsNamingTheCfg()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "livecity-nonet-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgPath = Path.Combine(tempDir, "nonet.sumocfg");
            File.WriteAllText(cfgPath, "<configuration><time><begin value=\"0\"/></time></configuration>");

            var ex = Assert.Throws<InvalidDataException>(() => LiveCityConfig.ForSumocfg(cfgPath));
            Assert.Contains("nonet.sumocfg", ex.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ForSumocfg_MissingRouteFiles_IsNotAnError_TheNetFallbackCovers()
    {
        // Unlike Engine.LoadScenario, LiveCitySim generates its own demand and only scrapes routes for
        // a spawn-edge set -- so a cfg with no <route-files> must load, using the net-derived fallback.
        var tempDir = Path.Combine(Path.GetTempPath(), "livecity-norou-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgPath = Path.Combine(tempDir, "norou.sumocfg");
            File.WriteAllText(cfgPath,
                $"<configuration><input><net-file value=\"{Path.GetFullPath(FixtureNet())}\"/></input></configuration>");

            var cfg = LiveCityConfig.ForSumocfg(cfgPath);
            Assert.Null(cfg.RoutePaths);

            using var sim = new LiveCitySim(cfg);
            Assert.True(sim.CropEdges.Count > 0, "expected the net.xml drivable-edge fallback");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---- C1: the demo must not move --------------------------------------------------------------

    [Fact]
    public void ForRepoRoot_LeavesThePathOverridesUnset_AndResolvesTheHistoricalPaths()
    {
        var repoRoot = RepoRoot();
        var cfg = LiveCityConfig.ForRepoRoot(repoRoot);

        Assert.Null(cfg.NetPath);
        Assert.Null(cfg.RoutePath);
        Assert.Null(cfg.RoutePaths);

        var box = Path.Combine(repoRoot, "scenarios", "_ped", "demo_city", "box");
        Assert.Equal(Path.Combine(box, "net.xml"), cfg.ResolveNetPath());
        Assert.Equal(new[] { Path.Combine(box, "scenario.rou.xml") }, cfg.ResolveRoutePaths());
    }

    // ---- C2: pedestrian elevation ----------------------------------------------------------------

    [Fact]
    public void PedElevation_ResolvesPedLaneIdsAgainstTheVehicleNetwork()
    {
        // Proves design §4.2's load-bearing claim -- ped lane ids share NetworkModel.LanesById's id
        // space -- rather than assuming it. If it broke, ElevationAt would silently return 0 forever.
        using var sim = new LiveCitySim(FixtureConfigByNetPath());

        Assert.True(sim.PedElevation.PedLaneIdCount > 0);
        var ratio = (double)sim.PedElevation.ResolvedLaneCount / sim.PedElevation.PedLaneIdCount;
        Assert.True(ratio >= 0.9,
            $"expected >=90% of ped lane ids to resolve to 3-D vehicle lanes, got {ratio:P0} "
            + $"({sim.PedElevation.ResolvedLaneCount}/{sim.PedElevation.PedLaneIdCount})");
        Assert.True(sim.PedElevation.HasElevation);
    }

    [Fact]
    public void PedElevation_On3DNet_GivesRealElevation_NotZero()
    {
        using var sim = new LiveCitySim(FixtureConfigByNetPath());
        for (var i = 0; i < 200; i++)
        {
            sim.Step();
        }

        var peds = sim.Sample().Peds;
        Assert.True(peds.Count > 0, "need live peds to assert their elevation");

        foreach (var ped in peds)
        {
            Assert.NotEqual(0.0, ped.Z);
            Assert.InRange(ped.Z, 360.0, 410.0);
        }
    }

    [Fact]
    public void PedElevation_AgreesWithNearbyCarElevation_WithinTwoMetres()
    {
        // The handoff's own bar: a ped's z must match the nearby car z within a metre or two. Judged on
        // the MEDIAN over the population, with the single worst pair also reported, so one ped on a
        // steep kerb cannot fail the suite and a systemic offset cannot hide behind an outlier filter.
        using var sim = new LiveCitySim(FixtureConfigByNetPath());
        for (var i = 0; i < 400; i++)
        {
            sim.Step();
        }

        var snap = sim.Sample();
        Assert.True(snap.Peds.Count > 0);
        Assert.True(snap.Cars.Count > 0);

        var diffs = new List<double>();
        foreach (var ped in snap.Peds)
        {
            var bestD2 = double.PositiveInfinity;
            var bestCarZ = double.NaN;
            foreach (var car in snap.Cars)
            {
                var d2 = ((car.X - ped.X) * (car.X - ped.X)) + ((car.Y - ped.Y) * (car.Y - ped.Y));
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    bestCarZ = car.Z;
                }
            }

            if (bestD2 <= 30.0 * 30.0)
            {
                diffs.Add(Math.Abs(ped.Z - bestCarZ));
            }
        }

        Assert.True(diffs.Count > 0, "expected at least one ped within 30 m of a car");
        diffs.Sort();
        var median = diffs[diffs.Count / 2];
        Assert.True(median <= 2.0,
            $"median |pedZ - nearest carZ| = {median:F3} m over {diffs.Count} pairs (max {diffs[^1]:F3})");
    }

    [Fact]
    public void PedElevation_OnThe2DDemoNet_IsExactlyZero()
    {
        // The byte-identical-demo guarantee for C2: a 2-D net indexes nothing, so Sample() still writes
        // the literal 0.0 it wrote before the sampler existed.
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        using var sim = new LiveCitySim(cfg);

        Assert.False(sim.PedElevation.HasElevation);

        for (var i = 0; i < 120; i++)
        {
            sim.Step();
            if (i % 40 != 0)
            {
                continue;
            }

            foreach (var ped in sim.Sample().Peds)
            {
                Assert.Equal(0.0, ped.Z);
            }
        }
    }

    [Fact]
    public void PedElevation_QueryFarOutsideTheNet_ReturnsTheNearestSurface_NotGarbage()
    {
        using var sim = new LiveCitySim(FixtureConfigByNetPath());

        // A point 5 km away still resolves to the nearest indexed lane (the ring search widens); the
        // contract is "never NaN, never an unjustifiable guess".
        var far = sim.PedElevation.ElevationAt(new Sim.Core.Orca.Vec2(91850.0 + 5000.0, 73960.0 + 5000.0));
        Assert.False(double.IsNaN(far));
        Assert.InRange(far, 360.0, 410.0);
    }

    // ---- C3: live density ------------------------------------------------------------------------

    [Fact]
    public void SetPedDensity_RaisingTheCap_ConvergesUpward_WithNoRebuild()
    {
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        cfg.PedPopulationCap = 40;
        cfg.PedSpawnRatePerSecond = 8.0;
        using var sim = new LiveCitySim(cfg);

        // Fill to the initial cap.
        for (var i = 0; i < 200; i++)
        {
            sim.Step();
        }

        var before = sim.CurrentPeds;
        Assert.InRange(before, 1, 40);

        sim.SetPedDensity(120, 12.0);
        Assert.Equal(120, sim.PedDemand!.PopulationCap);
        Assert.Equal(12.0, sim.PedDemand!.SpawnRatePerSecond);

        // 20 simulated seconds at Dt=0.5 => 40 steps.
        for (var i = 0; i < 40; i++)
        {
            sim.Step();
        }

        Assert.True(sim.CurrentPeds > 40, $"expected the crowd to grow past the old cap; got {sim.CurrentPeds}");

        for (var i = 0; i < 200; i++)
        {
            sim.Step();
        }

        Assert.True(sim.CurrentPeds >= 100, $"expected convergence toward 120; got {sim.CurrentPeds}");
    }

    [Fact]
    public void SetPedDensity_LoweringTheCap_StopsSpawning_AndDrainsByAttrition()
    {
        // The documented (design §3.2) semantics: lowering does NOT delete anybody, it stops new
        // spawns. Assert exactly that -- no new spawn events while over the cap, count non-increasing.
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        cfg.PedPopulationCap = 120;
        cfg.PedSpawnRatePerSecond = 12.0;
        using var sim = new LiveCitySim(cfg);

        for (var i = 0; i < 300; i++)
        {
            sim.Step();
        }

        var filled = sim.CurrentPeds;
        Assert.True(filled > 20, $"expected a filled crowd before lowering; got {filled}");

        sim.SetPedDensity(10, 1.0);
        var spawnsAtLower = sim.PedDemand!.SpawnEvents.Count;
        var previous = sim.CurrentPeds;

        for (var i = 0; i < 40; i++)
        {
            sim.Step();
            Assert.True(sim.CurrentPeds <= previous,
                $"live count rose after lowering the cap: {previous} -> {sim.CurrentPeds}");
            previous = sim.CurrentPeds;

            if (sim.CurrentPeds > 10)
            {
                Assert.Equal(spawnsAtLower, sim.PedDemand!.SpawnEvents.Count);
            }
        }
    }

    [Fact]
    public void SetSpawnRate_ToZeroAndBack_IsReversible()
    {
        // The +Infinity pending-wait trap: parking the rate at 0 must not be a one-way door.
        //
        // The cap is deliberately far above anything the run can reach, and that headroom is ASSERTED
        // below: with a binding cap, "no spawns while parked" and "spawns after unparking" would both
        // be explained by the cap rather than by the rate, and the test would prove nothing about the
        // knob it names.
        const int RoomyCap = 5000;
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        cfg.PedPopulationCap = RoomyCap;
        using var sim = new LiveCitySim(cfg);

        for (var i = 0; i < 60; i++)
        {
            sim.Step();
        }

        sim.SetPedDensity(RoomyCap, 0.0);
        for (var i = 0; i < 20; i++)
        {
            sim.Step();
        }

        var parked = sim.PedDemand!.SpawnEvents.Count;
        for (var i = 0; i < 40; i++)
        {
            sim.Step();
        }

        Assert.True(sim.CurrentPeds < RoomyCap, "the cap must not bind, or this test measures the cap");
        Assert.Equal(parked, sim.PedDemand!.SpawnEvents.Count);

        sim.SetPedDensity(RoomyCap, 10.0);
        for (var i = 0; i < 40; i++)
        {
            sim.Step();
        }

        Assert.True(sim.CurrentPeds < RoomyCap, "the cap must not bind, or this test measures the cap");
        Assert.True(sim.PedDemand!.SpawnEvents.Count > parked,
            "spawning must resume after the rate is raised again");
    }

    [Fact]
    public void SetPedDensity_OnANetWithNoPedestrians_IsASilentNoOp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "livecity-noped-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "net.xml"),
                "<net>\n"
                + "  <edge id=\"e1\" from=\"A\" to=\"B\">\n"
                + "    <lane id=\"e1_0\" index=\"0\" speed=\"13.9\" length=\"30.0\" shape=\"0,0 30,0\"/>\n"
                + "  </edge>\n"
                + "  <edge id=\"e2\" from=\"B\" to=\"C\">\n"
                + "    <lane id=\"e2_0\" index=\"0\" speed=\"13.9\" length=\"30.0\" shape=\"30,0 60,0\"/>\n"
                + "  </edge>\n"
                + "  <connection from=\"e1\" to=\"e2\" fromLane=\"0\" toLane=\"0\"/>\n"
                + "</net>\n");

            using var sim = new LiveCitySim(LiveCityConfig.ForDataset(dir));
            Assert.False(sim.PedestriansEnabled);
            Assert.Null(sim.PedDemand);

            var ex = Record.Exception(() => sim.SetPedDensity(50, 5.0));
            Assert.Null(ex);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SetCarDensity_MovesTheLiveCarCount_WithNoRebuild()
    {
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        cfg.CarTargetConcurrent = 20;
        using var sim = new LiveCitySim(cfg);

        for (var i = 0; i < 120; i++)
        {
            sim.Step();
        }

        Assert.True(sim.CurrentCars <= 20 + 5, $"expected the initial cap to hold; got {sim.CurrentCars}");

        sim.SetCarDensity(90);
        for (var i = 0; i < 120; i++)
        {
            sim.Step();
        }

        Assert.True(sim.CurrentCars > 30, $"expected the car count to climb toward 90; got {sim.CurrentCars}");
    }

    [Fact]
    public void DensitySetters_WithTheSameCallSequence_AreDeterministic()
    {
        static List<(int Id, double Time)> Run()
        {
            var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
            cfg.PedPopulationCap = 30;
            using var sim = new LiveCitySim(cfg);

            for (var i = 0; i < 60; i++)
            {
                sim.Step();
            }

            sim.SetPedDensity(90, 11.0);
            for (var i = 0; i < 60; i++)
            {
                sim.Step();
            }

            sim.SetPedDensity(20, 2.0);
            for (var i = 0; i < 60; i++)
            {
                sim.Step();
            }

            return sim.PedDemand!.SpawnEvents.Select(e => (e.Id, e.Time)).ToList();
        }

        Assert.Equal(Run(), Run());
    }
}
