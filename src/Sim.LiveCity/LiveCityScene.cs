using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Sim.LiveCity;

// docs/LIVE-CITY-VISUALS-NOTES.md "Shared foundation": one parser for the static world-overlay companion
// files in a scenario's dataset dir (scenarios/_ped/demo_city/box/{zones,buildings,pois}.json), consumed
// by BOTH viewers (Raylib's src/Sim.Viewer and City3D's CityLib) so the JSON schema is read exactly once.
// Plain, render-neutral records -- no Godot/raylib types leak in here. Pure load (System.Text.Json), no
// RNG, no mutation of the input files. Every file is OPTIONAL: a missing file (or a missing top-level
// array key) yields an empty list for that layer, mirroring the reference viz's "every optional layer key
// is omitted when its source is absent" guarantee (DESIGN-live-city-2d-viz.md §8) -- so a dataset that
// only ships net.xml + scenario.rou.xml (no box companion files) still loads a scene with all four
// collections empty rather than throwing.

// A point POI (docs/reference/live-city-viz's `pois[]`, kinds venue/transit_stop/building_entrance/
// dwell_spot/parking_access -- i.e. every pois.json record WITHOUT a `polygon`). `Label` defaults to the
// POI's own `id` (the reference renderer's glyph label is the POI id, DESIGN-live-city-2d-viz.md §2:
// "text label when zoomed in" over the POI's `id`). `Building`/`FacingX`/`FacingY` are populated only for
// `building_entrance` records (the one kind that carries a `building` back-ref + a `facing` unit vector,
// per the pois.json schema) and are otherwise null.
public sealed record ScenePoi(
    string Id,
    string Kind,
    double X,
    double Y,
    string? Label = null,
    string? Building = null,
    double? FacingX = null,
    double? FacingY = null);

// A polygon-anchored POI (pois.json records that DO carry a `polygon`: `parking_lot`, `park`).
public sealed record SceneArea(
    string Id,
    string Kind,
    IReadOnlyList<(double X, double Y)> Polygon);

// One building.json record: footprint polygon extruded to `HeightM` (real data, not a synthetic default --
// docs/LIVE-CITY-VISUALS-NOTES.md's "data over defaults" standing directive), coloured by `Type`
// (mall/garage/office/residential/restaurant), optionally back-referencing its `Zone` id.
public sealed record SceneBuilding(
    string Id,
    string Type,
    IReadOnlyList<(double X, double Y)> Footprint,
    double HeightM,
    string? Zone = null);

// One zones.json district: `Type` (downtown/retail/dining/residential/park/arterial) drives the ground
// tint palette (DESIGN-live-city-2d-viz.md §2 row 4 / template.js ZONE_FILL); `Polygon` is the district
// boundary in SUMO world (x,y) metres.
public sealed record SceneZone(
    string Id,
    string Type,
    IReadOnlyList<(double X, double Y)> Polygon);

// The loaded, render-neutral static-world overlay for one dataset directory. Both viewers construct this
// once (directly via `Load`, or indirectly via `LiveCitySim.Scene`) and read the four collections from it
// every frame -- no per-frame re-parsing, no per-viewer JSON code.
public sealed class LiveCityScene
{
    public static readonly LiveCityScene Empty = new(
        Array.Empty<ScenePoi>(), Array.Empty<SceneArea>(), Array.Empty<SceneBuilding>(), Array.Empty<SceneZone>());

    private LiveCityScene(
        IReadOnlyList<ScenePoi> pois,
        IReadOnlyList<SceneArea> areas,
        IReadOnlyList<SceneBuilding> buildings,
        IReadOnlyList<SceneZone> zones)
    {
        Pois = pois;
        Areas = areas;
        Buildings = buildings;
        Zones = zones;
    }

    public IReadOnlyList<ScenePoi> Pois { get; }

    public IReadOnlyList<SceneArea> Areas { get; }

    public IReadOnlyList<SceneBuilding> Buildings { get; }

    public IReadOnlyList<SceneZone> Zones { get; }

    // Loads every optional companion file under `datasetDir` (zones.json, buildings.json, pois.json).
    // Each is independently optional: a missing file simply contributes an empty list to that layer,
    // never throws -- every layer is additive/optional per the reference viz's guarantee.
    public static LiveCityScene Load(string datasetDir)
    {
        if (datasetDir is null)
        {
            throw new ArgumentNullException(nameof(datasetDir));
        }

        var zones = LoadZones(Path.Combine(datasetDir, "zones.json"));
        var buildings = LoadBuildings(Path.Combine(datasetDir, "buildings.json"));
        var (pois, areas) = LoadPois(Path.Combine(datasetDir, "pois.json"));

        return new LiveCityScene(pois, areas, buildings, zones);
    }

    private static IReadOnlyList<SceneZone> LoadZones(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<SceneZone>();
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("zones", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SceneZone>();
        }

        var result = new List<SceneZone>(arr.GetArrayLength());
        foreach (var z in arr.EnumerateArray())
        {
            result.Add(new SceneZone(
                Id: RequireString(z, "id", "zone"),
                Type: RequireString(z, "type", "zone"),
                Polygon: ReadPolygon(z, "polygon")));
        }

        return result;
    }

    private static IReadOnlyList<SceneBuilding> LoadBuildings(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<SceneBuilding>();
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("buildings", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SceneBuilding>();
        }

        var result = new List<SceneBuilding>(arr.GetArrayLength());
        foreach (var b in arr.EnumerateArray())
        {
            result.Add(new SceneBuilding(
                Id: RequireString(b, "id", "building"),
                Type: RequireString(b, "type", "building"),
                Footprint: ReadPolygon(b, "footprint"),
                HeightM: b.TryGetProperty("height_m", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : 0.0,
                Zone: b.TryGetProperty("zone", out var z) && z.ValueKind == JsonValueKind.String ? z.GetString() : null));
        }

        return result;
    }

    // pois.json mixes point records (venue/transit_stop/building_entrance/dwell_spot/parking_access, keyed
    // by `pos`) with polygon-anchored area records (parking_lot/park, which additionally carry `polygon`).
    // The presence of a `polygon` property is the clean discriminator (confirmed against the committed
    // demo_city/box/pois.json: exactly parking_lot + park carry it) -- so a single pass over `pois[]`
    // routes each record to `ScenePoi` or `SceneArea` accordingly.
    private static (IReadOnlyList<ScenePoi> Pois, IReadOnlyList<SceneArea> Areas) LoadPois(string path)
    {
        if (!File.Exists(path))
        {
            return (Array.Empty<ScenePoi>(), Array.Empty<SceneArea>());
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("pois", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return (Array.Empty<ScenePoi>(), Array.Empty<SceneArea>());
        }

        var pois = new List<ScenePoi>();
        var areas = new List<SceneArea>();

        foreach (var p in arr.EnumerateArray())
        {
            var id = RequireString(p, "id", "poi");
            var kind = RequireString(p, "kind", "poi");

            if (p.TryGetProperty("polygon", out var polyEl) && polyEl.ValueKind == JsonValueKind.Array)
            {
                areas.Add(new SceneArea(id, kind, ReadPolygon(p, "polygon")));
                continue;
            }

            var pos = p.GetProperty("pos");
            double? facingX = null, facingY = null;
            if (p.TryGetProperty("facing", out var f) && f.ValueKind == JsonValueKind.Array && f.GetArrayLength() >= 2)
            {
                facingX = f[0].GetDouble();
                facingY = f[1].GetDouble();
            }

            pois.Add(new ScenePoi(
                Id: id,
                Kind: kind,
                X: pos[0].GetDouble(),
                Y: pos[1].GetDouble(),
                Label: id,
                Building: p.TryGetProperty("building", out var bld) && bld.ValueKind == JsonValueKind.String ? bld.GetString() : null,
                FacingX: facingX,
                FacingY: facingY));
        }

        return (pois, areas);
    }

    private static IReadOnlyList<(double X, double Y)> ReadPolygon(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<(double, double)>();
        }

        var pts = new List<(double X, double Y)>(arr.GetArrayLength());
        foreach (var pt in arr.EnumerateArray())
        {
            pts.Add((pt[0].GetDouble(), pt[1].GetDouble()));
        }

        return pts;
    }

    private static string RequireString(JsonElement element, string propertyName, string recordKind)
    {
        return element.TryGetProperty(propertyName, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s
            ? s
            : throw new FormatException($"{recordKind} record missing string '{propertyName}'");
    }
}
