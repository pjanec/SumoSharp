using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Sim.LiveCity;
using Xunit;

namespace Sim.LiveCity.Tests;

// docs/LIVE-CITY-VISUALS-NOTES.md "Shared foundation" / deliverable 1: a non-vacuous test of
// LiveCityScene.Load against the committed scenarios/_ped/demo_city/box dataset -- asserts the exact
// record counts + a couple of per-record field invariants (not just "count > 0"), so a schema regression
// (e.g. a renamed JSON field silently defaulting to 0/null) is actually caught.
public class LiveCitySceneTests
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

    private static string DatasetDir() =>
        Path.Combine(RepoRoot(), "scenarios", "_ped", "demo_city", "box");

    [Fact]
    public void Load_DemoCityBox_ReturnsExpectedZones()
    {
        var scene = LiveCityScene.Load(DatasetDir());

        Assert.Equal(6, scene.Zones.Count);

        var expectedTypes = new[] { "downtown", "retail", "dining", "residential", "park", "arterial" };
        var actualTypes = scene.Zones.Select(z => z.Type).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedTypes.OrderBy(t => t, StringComparer.Ordinal).ToArray(), actualTypes);

        foreach (var z in scene.Zones)
        {
            Assert.True(z.Polygon.Count >= 3, $"zone {z.Id} has a degenerate polygon ({z.Polygon.Count} points)");
        }
    }

    [Fact]
    public void Load_DemoCityBox_ReturnsExpectedBuildings()
    {
        var scene = LiveCityScene.Load(DatasetDir());

        Assert.Equal(31, scene.Buildings.Count);
        foreach (var b in scene.Buildings)
        {
            Assert.True(b.HeightM > 0.0, $"building {b.Id} has non-positive HeightM {b.HeightM}");
            Assert.True(b.Footprint.Count >= 3, $"building {b.Id} has a degenerate footprint ({b.Footprint.Count} points)");
        }
    }

    [Fact]
    public void Load_DemoCityBox_ReturnsExpectedPoiCountsByKind()
    {
        var scene = LiveCityScene.Load(DatasetDir());

        var byKind = scene.Pois.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(51, byKind.GetValueOrDefault("building_entrance"));
        Assert.Equal(28, byKind.GetValueOrDefault("venue"));
        Assert.Equal(351, byKind.GetValueOrDefault("parking_access"));
        Assert.Equal(12, byKind.GetValueOrDefault("transit_stop"));
        Assert.Equal(25, byKind.GetValueOrDefault("dwell_spot"));

        // No area kinds should have leaked into the point-POI collection.
        Assert.DoesNotContain(scene.Pois, p => p.Kind is "parking_lot" or "park");

        // NOTE: unlike the task brief's assumption, NOT every building_entrance record in the committed
        // demo_city/box/pois.json carries a `building` back-ref (only 12 of 51 do -- confirmed by direct
        // inspection); ALL 51 carry `facing`. So Facing is asserted unconditionally, Building only when
        // the source record actually has it (parsed faithfully either way, never fabricated).
        var entrances = scene.Pois.Where(p => p.Kind == "building_entrance").ToList();
        Assert.Equal(51, entrances.Count);
        foreach (var e in entrances)
        {
            Assert.True(e.FacingX.HasValue && e.FacingY.HasValue, $"building_entrance {e.Id} missing Facing vector");
        }

        var withBuilding = entrances.Count(e => !string.IsNullOrEmpty(e.Building));
        Assert.Equal(12, withBuilding);

        // `facing` is exclusive to building_entrance in the committed data; `building` is NOT (a `venue`
        // and a `parking_lot` co-located with a building also carry it, e.g. poi_v2_venue_mall /
        // poi_v2_lot_mall -> bldg_mall_0) -- so only Facing is asserted absent off-kind, faithfully
        // matching what LoadPois actually parses (it reads `building` generically, not kind-gated).
        foreach (var p in scene.Pois.Where(p => p.Kind != "building_entrance"))
        {
            Assert.Null(p.FacingX);
            Assert.Null(p.FacingY);
        }
    }

    [Fact]
    public void Load_DemoCityBox_ReturnsExpectedAreas()
    {
        var scene = LiveCityScene.Load(DatasetDir());

        var byKind = scene.Areas.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(5, byKind.GetValueOrDefault("parking_lot"));
        Assert.Equal(1, byKind.GetValueOrDefault("park"));
        Assert.Equal(6, scene.Areas.Count);

        foreach (var a in scene.Areas)
        {
            Assert.True(a.Polygon.Count >= 3, $"area {a.Id} has a degenerate polygon ({a.Polygon.Count} points)");
        }
    }

    [Fact]
    public void Load_MissingDatasetDir_ReturnsAllEmptyCollections_NeverThrows()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "livecity-scene-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            var scene = LiveCityScene.Load(emptyDir);
            Assert.Empty(scene.Pois);
            Assert.Empty(scene.Areas);
            Assert.Empty(scene.Buildings);
            Assert.Empty(scene.Zones);
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    [Fact]
    public void LiveCitySim_ExposesTheSameScene_AsDirectLoad()
    {
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        using var sim = new LiveCitySim(cfg);

        Assert.Equal(6, sim.Scene.Zones.Count);
        Assert.Equal(31, sim.Scene.Buildings.Count);
    }
}
