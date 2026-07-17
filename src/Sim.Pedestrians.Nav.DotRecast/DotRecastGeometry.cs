using DotRecast.Recast.Geom;
using Sim.Core.Orca;
using Sim.Pedestrians.Navigation.Bake;

namespace Sim.Pedestrians.Navigation.DotRecast;

// Lifts WalkablePolygonBaker.Bake()'s 2D BakedPolygon set into a Recast Y-up triangle soup
// (docs/PEDESTRIAN-POC-PLAN.md POC-1b task step 1). Fan-triangulates each polygon from vertex 0
// (valid for POC-0's convex quads and junction shapes, per the task) and maps 2D Vec2(x,y) to 3D
// (x, 0, y): the walkable plane sits at Recast height 0 and our 2D Y becomes Recast Z.
//
// WINDING: a fan triangle (v0, vi, vi+1) taken directly under the (x, 0, y) mapping faces DOWN
// (-Y) whenever the source 2D polygon is wound counter-clockwise in the standard math convention
// (positive shoelace signed area) -- Recast would then classify it unwalkable regardless of slope,
// because the cross-product normal's Y component works out to -2*SignedArea(v0,vi,vi+1) for that
// mapping (derivation: edges lie in the Y=0 plane, so normal.Y = edge1.z*edge2.x - edge1.x*edge2.z,
// which is exactly the negated 2D cross product of the same two edges). Baked polygons are not
// guaranteed consistently wound (WalkingAreas/Crossings come straight from SUMO's shapes; the
// per-segment sidewalk quads are buffered locally) -- so each polygon's own signed area is checked
// and the fan direction flipped when it is positive (CCW), giving every emitted triangle a CW 2D
// order and therefore a correct upward (+Y) Recast normal.
internal static class DotRecastGeometry
{
    // Triangles this thin under the shoelace formula are treated as degenerate (collinear or
    // zero-length fan legs, e.g. a polygon vertex sitting exactly on the v0-vi line) and skipped
    // rather than handed to Recast, which chokes on zero-area input triangles.
    private const double DegenerateTriangleArea = 1e-9;

    public static SimpleInputGeomProvider Triangulate(IReadOnlyList<BakedPolygon> polygons)
    {
        var vertices = new List<float>();
        var faces = new List<int>();

        foreach (var polygon in polygons)
        {
            var verts = polygon.Vertices;
            if (verts.Count < 3)
            {
                continue;
            }

            var baseIndex = vertices.Count / 3;
            foreach (var v in verts)
            {
                vertices.Add((float)v.X);
                vertices.Add(0f);
                vertices.Add((float)v.Y);
            }

            // CCW (positive signed area) -> reverse the fan direction (see WINDING remarks above).
            var reverse = SignedArea(verts) > 0.0;

            for (var i = 1; i + 1 < verts.Count; i++)
            {
                var vb = reverse ? i + 1 : i;
                var vc = reverse ? i : i + 1;

                if (Math.Abs(TriangleArea(verts[0], verts[vb], verts[vc])) <= DegenerateTriangleArea)
                {
                    continue;
                }

                faces.Add(baseIndex);
                faces.Add(baseIndex + vb);
                faces.Add(baseIndex + vc);
            }
        }

        return new SimpleInputGeomProvider(vertices.ToArray(), faces.ToArray());
    }

    // Shoelace formula over the implicitly-closed ring (same convention as
    // Sim.Pedestrians.Navigation.Bake.PolygonGeometry.SignedArea, reimplemented here because that
    // helper is internal to Sim.Pedestrians).
    private static double SignedArea(IReadOnlyList<Vec2> vertices)
    {
        double area = 0.0;
        var n = vertices.Count;
        for (var i = 0; i < n; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % n];
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        return area / 2.0;
    }

    private static double TriangleArea(Vec2 a, Vec2 b, Vec2 c) =>
        ((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / 2.0;
}
