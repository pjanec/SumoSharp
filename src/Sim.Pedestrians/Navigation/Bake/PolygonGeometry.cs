using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation.Bake;

// Small deterministic 2D polygon helpers shared by the SUMO-geometry-bake navigation provider
// (docs/PEDESTRIAN-DESIGN.md §4, POC-1a). A "polygon" here is always an ORDERED vertex list
// treated as an IMPLICITLY CLOSED ring -- the edge from Vertices[^1] back to Vertices[0] is part
// of the boundary even though it is not stored twice. This matches the convention
// OrcaCrowd.AddObstacle already uses for its wall loops, so a baked polygon's vertex list can be
// hand^ed straight to AddObstacle with no re-closing.
internal static class PolygonGeometry
{
    // Used only for degenerate-length checks (zero-length segments), not for equality tolerance.
    public const double DegenerateLengthSq = 1e-12;

    // Point-in-polygon via the standard even-odd ray-casting test (Sedgewick). Runs on the
    // implicitly-closed ring described above.
    public static bool Contains(IReadOnlyList<Vec2> vertices, Vec2 p)
    {
        var inside = false;
        var n = vertices.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var vi = vertices[i];
            var vj = vertices[j];
            var crosses = (vi.Y > p.Y) != (vj.Y > p.Y);
            if (crosses && p.X < ((vj.X - vi.X) * (p.Y - vi.Y) / (vj.Y - vi.Y)) + vi.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    // Nearest point to `p` lying ON the polygon's boundary (its edge set), plus the squared
    // distance to it. Used to clamp an off-mesh point onto walkable space.
    public static Vec2 NearestPointOnBoundary(IReadOnlyList<Vec2> vertices, Vec2 p, out double distSq)
    {
        var n = vertices.Count;
        var best = vertices[0];
        var bestDistSq = double.MaxValue;

        for (var i = 0; i < n; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % n];
            var candidate = NearestPointOnSegment(a, b, p);
            var dSq = (candidate - p).AbsSq;
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                best = candidate;
            }
        }

        distSq = bestDistSq;
        return best;
    }

    public static Vec2 NearestPointOnSegment(Vec2 a, Vec2 b, Vec2 p)
    {
        var ab = b - a;
        var abLenSq = ab.AbsSq;
        if (abLenSq <= DegenerateLengthSq)
        {
            return a;
        }

        var t = Vec2.Dot(p - a, ab) / abLenSq;
        t = Math.Clamp(t, 0.0, 1.0);
        return a + (t * ab);
    }

    // Vertex-average "centroid" (not the true area centroid) -- adequate as an A* distance
    // heuristic (PEDESTRIAN-POC-PLAN.md POC-1 task: "Euclidean centroid heuristic") without the
    // extra complexity of a signed-area centroid computation.
    public static Vec2 VertexAverage(IReadOnlyList<Vec2> vertices)
    {
        double sx = 0.0, sy = 0.0;
        foreach (var v in vertices)
        {
            sx += v.X;
            sy += v.Y;
        }

        return new Vec2(sx / vertices.Count, sy / vertices.Count);
    }

    public static bool NearlyEqual(Vec2 a, Vec2 b, double epsilon) => (a - b).AbsSq <= epsilon * epsilon;

    // Signed area via the shoelace formula, over the implicitly-closed ring. Used to detect and
    // drop DEGENERATE polygons: SUMO emits a zero-area "walkingarea" at a network-boundary dead
    // end (all its shape points collinear, e.g. POC-0's ":n_w0_0" / ":e_w0_0" / ":s_w0_0" /
    // ":w_w0_0") to keep its lane-adjacency bookkeeping uniform, even though there is no real
    // walkable AREA there. Left in the baked set, such a polygon has no interior (Contains() is
    // false almost everywhere on it) yet still gets adjacency portals to its neighbours -- which
    // would let A* route a path "through" it as an illegitimate zero-width teleport. Baking must
    // filter these out.
    public static double SignedArea(IReadOnlyList<Vec2> vertices)
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
}
