using Sim.Ingest;

namespace Sim.Scene;

// docs/HROT-INTEGRATION-DESIGN.md H2 (NEW): the junction FILL surface. netconvert writes each junction's
// outline as `<junction shape="x,y x,y ...">` (Sim.Ingest.Junction.Shape, 2-D, either winding, sometimes
// closed with a repeated first point, sometimes degenerate). This triangulates that polygon by EAR
// CLIPPING into a SceneMesh of kind Junction, so a host can paint the area between the road ribbons.
//
// Z comes from the frame's ground (GroundToLocal with 0 above ground): a junction has no elevation of
// its own, so it follows the TerrainField (or the flat OriginZ datum). Triangles are CCW viewed from +z
// whatever the input winding, for a simple outline. Pure math, deterministic (fixed index order, no Random).
//
// SceneMesh.Area is the SIGNED sum of the triangle areas, i.e. exactly the polygon's shoelace area. For a
// simple outline every triangle is CCW and this is the covered area. netconvert occasionally emits a
// slightly SELF-INTERSECTING outline (a bowtie sliver, e.g. junction `ra_s_r3` of demo_city/box): no
// simple triangulation exists, so ear clipping may emit one CW triangle (culled by a single-sided
// renderer; it overlaps already-covered area), and its negative area is what keeps Area == shoelace.
public static class JunctionMeshBuilder
{
    private const double Eps = 1e-9;

    // One polygon -> one mesh. Fewer than 3 distinct points, or a zero-area (collinear) outline, yields
    // SceneMesh.Empty.
    public static SceneMesh Build(SceneFrame frame, IReadOnlyList<(double X, double Y)> polygon)
    {
        var pts = Clean(polygon);
        if (pts.Count < 3)
        {
            return SceneMesh.Empty;
        }

        // Normalise to CCW (positive signed area) so "convex" == cross > 0 below.
        if (SignedArea(pts) < 0.0)
        {
            pts.Reverse();
        }

        var tris = Triangulate(pts);
        if (tris.Count == 0)
        {
            return SceneMesh.Empty;
        }

        // Vertices: one per polygon point (shared by the fan of triangles that use it).
        var vertices = new float[pts.Count * 3];
        var normals = new float[pts.Count * 3];
        for (var i = 0; i < pts.Count; i++)
        {
            var (lx, ly, lz) = frame.GroundToLocal(pts[i].X, pts[i].Y, 0.0);
            vertices[i * 3 + 0] = lx;
            vertices[i * 3 + 1] = ly;
            vertices[i * 3 + 2] = lz;
            normals[i * 3 + 0] = 0f;
            normals[i * 3 + 1] = 0f;
            normals[i * 3 + 2] = 1f;
        }

        var indices = new int[tris.Count * 3];
        var area = 0.0;
        for (var t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            indices[t * 3 + 0] = a;
            indices[t * 3 + 1] = b;
            indices[t * 3 + 2] = c;
            area += Cross(pts[a], pts[b], pts[c]) * 0.5;
        }

        return new SceneMesh(vertices, indices, normals, area);
    }

    // Every junction of the network that has a usable polygon, in NetworkModel.Junctions order.
    public static IEnumerable<(string JunctionId, SceneMesh Mesh)> BuildAll(SceneFrame frame, NetworkModel network)
    {
        foreach (var junction in network.Junctions)
        {
            if (junction.Shape.Count < 3)
            {
                continue;
            }

            var mesh = Build(frame, junction.Shape);
            if (!mesh.IsEmpty)
            {
                yield return (junction.Id, mesh);
            }
        }
    }

    // Drops the closing duplicate and any consecutive duplicate points.
    private static List<(double X, double Y)> Clean(IReadOnlyList<(double X, double Y)> polygon)
    {
        var pts = new List<(double X, double Y)>(polygon.Count);
        foreach (var p in polygon)
        {
            if (pts.Count == 0 || !Same(pts[^1], p))
            {
                pts.Add(p);
            }
        }

        while (pts.Count > 1 && Same(pts[0], pts[^1]))
        {
            pts.RemoveAt(pts.Count - 1);
        }

        return pts;
    }

    private static bool Same((double X, double Y) a, (double X, double Y) b)
        => Math.Abs(a.X - b.X) <= Eps && Math.Abs(a.Y - b.Y) <= Eps;

    private static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static double SignedArea(List<(double X, double Y)> p)
    {
        var sum = 0.0;
        for (var i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return sum * 0.5;
    }

    // Ear clipping over a CCW polygon; returns triangles as indices into `pts`.
    private static List<(int A, int B, int C)> Triangulate(List<(double X, double Y)> pts)
    {
        var result = new List<(int, int, int)>();
        var idx = new List<int>(pts.Count);
        for (var i = 0; i < pts.Count; i++)
        {
            idx.Add(i);
        }

        // Scale-aware tolerance: "collinear" is relative to the polygon's own extent.
        var extent = 0.0;
        foreach (var p in pts)
        {
            extent = Math.Max(extent, Math.Max(Math.Abs(p.X - pts[0].X), Math.Abs(p.Y - pts[0].Y)));
        }

        var eps = Math.Max(extent * extent * 1e-12, 1e-12);

        var guard = idx.Count * idx.Count + 8;
        while (idx.Count > 3 && guard-- > 0)
        {
            var earAt = -1;
            var collinearAt = -1;
            var bestConvex = -1;
            var bestCross = 0.0;
            var n = idx.Count;
            for (var k = 0; k < n; k++)
            {
                var ia = idx[(k + n - 1) % n];
                var ib = idx[k];
                var ic = idx[(k + 1) % n];
                var cr = Cross(pts[ia], pts[ib], pts[ic]);
                if (Math.Abs(cr) <= eps)
                {
                    if (collinearAt < 0)
                    {
                        collinearAt = k;
                    }

                    continue;
                }

                if (cr < 0.0)
                {
                    continue; // reflex vertex
                }

                if (cr > bestCross)
                {
                    bestCross = cr;
                    bestConvex = k;
                }

                if (!AnyPointInside(pts, idx, ia, ib, ic))
                {
                    earAt = k;
                    break;
                }
            }

            if (earAt >= 0)
            {
                var ia = idx[(earAt + n - 1) % n];
                var ib = idx[earAt];
                var ic = idx[(earAt + 1) % n];
                result.Add((ia, ib, ic));
                idx.RemoveAt(earAt);
            }
            else if (collinearAt >= 0)
            {
                idx.RemoveAt(collinearAt); // zero-area spur/collinear vertex: no triangle, no area lost
            }
            else if (bestConvex >= 0)
            {
                // Self-intersecting / otherwise non-simple outline: no clean ear exists. Clip the most
                // convex vertex anyway so the loop always terminates.
                var ia = idx[(bestConvex + n - 1) % n];
                var ib = idx[bestConvex];
                var ic = idx[(bestConvex + 1) % n];
                result.Add((ia, ib, ic));
                idx.RemoveAt(bestConvex);
            }
            else
            {
                break; // nothing convex and nothing collinear left: degenerate remainder
            }
        }

        if (idx.Count == 3 && Math.Abs(Cross(pts[idx[0]], pts[idx[1]], pts[idx[2]])) > eps)
        {
            result.Add((idx[0], idx[1], idx[2]));
        }

        return result;
    }

    // True when any OTHER remaining polygon vertex lies inside (or on the boundary of) triangle abc. A
    // vertex that merely coincides with one of abc's corners (a duplicate point) does not block the ear.
    private static bool AnyPointInside(
        List<(double X, double Y)> pts, List<int> idx, int ia, int ib, int ic)
    {
        var a = pts[ia];
        var b = pts[ib];
        var c = pts[ic];
        foreach (var i in idx)
        {
            if (i == ia || i == ib || i == ic)
            {
                continue;
            }

            var p = pts[i];
            if (Same(p, a) || Same(p, b) || Same(p, c))
            {
                continue; // a coincident duplicate vertex does not block the ear
            }

            if (Cross(a, b, p) >= -Eps && Cross(b, c, p) >= -Eps && Cross(c, a, p) >= -Eps)
            {
                return true;
            }
        }

        return false;
    }
}
