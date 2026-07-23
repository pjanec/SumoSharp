using Sim.LiveCity;

namespace CityLib;

// docs/LIVE-CITY-VISUALS-NOTES.md "Zones/districts" row / docs/reference/live-city-viz/DESIGN-live-city-
// 2d-viz.md §7 "Ground plane / districts": `zones[].polygon` -> a flat tinted ground region. Pure polygon
// -> mesh math, no Godot type anywhere (mirrors RoadMeshBuilder's own "CityLib stays engine-agnostic"
// split -- Main.cs turns this into an ArrayMesh/MeshInstance3D).
public readonly struct FlatGroundMesh
{
    public FlatGroundMesh(float[] vertices, int[] indices, float[] normals, double area)
    {
        Vertices = vertices;
        Indices = indices;
        Normals = normals;
        Area = area;
    }

    // xyz triples, already in GODOT space (CoordinateTransform.SumoToGodot applied).
    public float[] Vertices { get; }
    public int[] Indices { get; }
    public float[] Normals { get; }

    // The polygon's planar (SUMO x/y ground-plane) area in square metres -- callers (Main.cs) sort zones
    // largest-area-first before building/adding their MeshInstance3D nodes so a big district's tint never
    // paints over a small one nested/adjacent to it (docs/LIVE-CITY-VISUALS-NOTES.md deliverable 2:
    // "Draw largest-area-first").
    public double Area { get; }
}

public static class ZoneGroundBuilder
{
    // Fan-triangulates a district polygon (SceneZone.Polygon, SUMO x/y metres) into ONE flat mesh, sitting
    // `groundOffsetSumoZ` metres below the road surface (SUMO z=0, the same elevation RoadMeshBuilder emits
    // for a flat lane) to avoid z-fighting with the road ribbons drawn on top -- a small negative default
    // (-0.05m) is imperceptible from the live-city overview camera's altitude but reliably wins the depth
    // test in the road's favour. Fan triangulation (vertex 0 as the shared apex) is exact for the convex
    // (or near-convex, e.g. the arterial ring's collinear mid-edge points) polygons the demo_city/box
    // dataset actually ships -- see docs/reference/live-city-viz/DESIGN-live-city-2d-viz.md §7's own "fan
    // or ear-clip for convex-ish district rects" guidance. Winding order is deliberately NOT normalized
    // here (the caller's material renders both sides -- CullMode.Disabled -- exactly because a flat ground
    // tint has no meaningful "back face", so getting the fan's winding backwards for a clockwise-authored
    // polygon costs nothing).
    public static FlatGroundMesh Build(IReadOnlyList<(double X, double Y)> polygon, double groundOffsetSumoZ = -0.05)
    {
        var n = polygon.Count;
        if (n < 3)
        {
            return new FlatGroundMesh(Array.Empty<float>(), Array.Empty<int>(), Array.Empty<float>(), 0.0);
        }

        var vertices = new float[n * 3];
        var normals = new float[n * 3];
        for (var i = 0; i < n; i++)
        {
            var (gx, gy, gz) = CoordinateTransform.SumoToGodot(polygon[i].X, polygon[i].Y, groundOffsetSumoZ);
            var b = i * 3;
            vertices[b + 0] = gx;
            vertices[b + 1] = gy;
            vertices[b + 2] = gz;
            normals[b + 0] = 0f;
            normals[b + 1] = 1f;
            normals[b + 2] = 0f;
        }

        var triCount = n - 2;
        var indices = new int[triCount * 3];
        for (var i = 0; i < triCount; i++)
        {
            var b = i * 3;
            indices[b + 0] = 0;
            indices[b + 1] = i + 1;
            indices[b + 2] = i + 2;
        }

        return new FlatGroundMesh(vertices, indices, normals, PlanarArea(polygon));
    }

    // Shoelace formula, SUMO (x,y) plane -- same technique RoadMeshBuilder.QuadArea uses per-quad, just
    // over the whole polygon in one pass. Absolute value: callers only need magnitude for area-sort.
    private static double PlanarArea(IReadOnlyList<(double X, double Y)> polygon)
    {
        var n = polygon.Count;
        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            var (x1, y1) = polygon[i];
            var (x2, y2) = polygon[(i + 1) % n];
            sum += (x1 * y2) - (x2 * y1);
        }

        return Math.Abs(sum) * 0.5;
    }
}
