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
    //
    // §7.2.3 -- TERRAIN. On a frame carrying a baked TerrainField, sampling the height only at the
    // polygon's own vertices would leave each fan triangle a PLANE THROUGH THREE TERRAIN POINTS: over a
    // 400 m district on a slope the middle of the tint ends up metres under the road it is supposed to
    // sit beneath. So each fan triangle is recursively split at its edge midpoints until every edge is
    // <= the field's cell size, and every generated vertex -- interior midpoints included -- is placed
    // through GroundToGodot. The fan topology is unchanged, so the COVERED AREA is exactly what it was:
    // no clipping, no coverage change, just more vertices on the same surface.
    //
    // On a flat field the subdivision is skipped entirely (it would produce the same planar surface with
    // more triangles), so a 2-D net's tint mesh is vertex-for-vertex what it was.
    public static FlatGroundMesh Build(SumoGodotFrame frame, IReadOnlyList<(double X, double Y)> polygon, double groundOffsetSumoZ = -0.05)
    {
        var n = polygon.Count;
        if (n < 3)
        {
            return new FlatGroundMesh(Array.Empty<float>(), Array.Empty<int>(), Array.Empty<float>(), 0.0);
        }

        var area = PlanarArea(polygon);

        if (!frame.HasTerrain)
        {
            return BuildFlatFan(frame, polygon, groundOffsetSumoZ, area);
        }

        // Deduplicating on the SUMO (x, y) key rather than the Godot triple: two sub-triangles sharing a
        // split edge must share the vertex, or the tint shows hairline cracks where floating-point
        // rounding puts the two copies a ULP apart.
        var maxEdge = Math.Max(frame.Terrain.CellSize, 1.0);
        var verts = new List<float>(n * 12);
        var indices = new List<int>(n * 12);
        var seen = new Dictionary<(double X, double Y), int>();

        int VertexFor((double X, double Y) p)
        {
            if (seen.TryGetValue(p, out var existing))
            {
                return existing;
            }

            var (gx, gy, gz) = frame.GroundToGodot(p.X, p.Y, groundOffsetSumoZ);
            var index = verts.Count / 3;
            verts.Add(gx);
            verts.Add(gy);
            verts.Add(gz);
            seen[p] = index;
            return index;
        }

        for (var i = 0; i < n - 2; i++)
        {
            Subdivide(polygon[0], polygon[i + 1], polygon[i + 2], maxEdge, 0, VertexFor, indices);
        }

        // Flat-up normals: the tint is an unlit-ish translucent wash whose material culls nothing, so a
        // per-triangle normal would buy nothing but the cost of computing it.
        var normals = new float[verts.Count];
        for (var i = 1; i < normals.Length; i += 3)
        {
            normals[i] = 1f;
        }

        return new FlatGroundMesh(verts.ToArray(), indices.ToArray(), normals, area);
    }

    /// Deepest recursion allowed, so a pathologically large polygon against a small cell size cannot
    /// blow up: 5 levels is 4^5 = 1024 sub-triangles per fan triangle, which covers a ~1 km district at
    /// a 40 m cell with room to spare.
    public const int MaxSubdivisionDepth = 5;

    // Split at edge midpoints (1 triangle -> 4) while any edge is longer than `maxEdge`. Midpoints are
    // computed in SUMO space so the shared-edge dedup key above is exact between sibling triangles.
    private static void Subdivide(
        (double X, double Y) a, (double X, double Y) b, (double X, double Y) c,
        double maxEdge, int depth,
        Func<(double X, double Y), int> vertexFor,
        List<int> indices)
    {
        var longest = Math.Max(Distance(a, b), Math.Max(Distance(b, c), Distance(c, a)));
        if (depth >= MaxSubdivisionDepth || longest <= maxEdge)
        {
            indices.Add(vertexFor(a));
            indices.Add(vertexFor(b));
            indices.Add(vertexFor(c));
            return;
        }

        var ab = Midpoint(a, b);
        var bc = Midpoint(b, c);
        var ca = Midpoint(c, a);

        Subdivide(a, ab, ca, maxEdge, depth + 1, vertexFor, indices);
        Subdivide(ab, b, bc, maxEdge, depth + 1, vertexFor, indices);
        Subdivide(ca, bc, c, maxEdge, depth + 1, vertexFor, indices);
        Subdivide(ab, bc, ca, maxEdge, depth + 1, vertexFor, indices);
    }

    private static (double X, double Y) Midpoint((double X, double Y) p, (double X, double Y) q)
        => ((p.X + q.X) * 0.5, (p.Y + q.Y) * 0.5);

    private static double Distance((double X, double Y) p, (double X, double Y) q)
        => Math.Sqrt(((p.X - q.X) * (p.X - q.X)) + ((p.Y - q.Y) * (p.Y - q.Y)));

    // The pre-terrain path, kept verbatim: one vertex per polygon point, a fan of n-2 triangles.
    private static FlatGroundMesh BuildFlatFan(
        SumoGodotFrame frame, IReadOnlyList<(double X, double Y)> polygon, double groundOffsetSumoZ, double area)
    {
        var n = polygon.Count;
        var vertices = new float[n * 3];
        var normals = new float[n * 3];
        for (var i = 0; i < n; i++)
        {
            var (gx, gy, gz) = frame.GroundToGodot(polygon[i].X, polygon[i].Y, groundOffsetSumoZ);
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

        return new FlatGroundMesh(vertices, indices, normals, area);
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
