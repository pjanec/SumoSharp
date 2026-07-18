using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation.Bake;

// P2-1 (docs/PEDESTRIAN-TASKS.md, PEDESTRIAN-DESIGN.md §4; POC-1a notes): buffers a WHOLE sidewalk
// polyline into ONE mitred strip polygon, replacing POC-1a's per-segment quad approximation
// (WalkablePolygonBaker's old per-segment loop -- see its history) that left a bent (multi-segment)
// sidewalk as several disconnected quads whenever consecutive segments were not collinear.
//
// Algorithm (standard polyline-offset with mitre joins): offset each segment's direction by +-half
// perpendicular to get a "left" and "right" rail; at each INTERIOR vertex, the two rails' consecutive
// offset segments are extended as infinite lines and intersected -- the mitre point -- so the strip
// stays one continuous, watertight polygon through a bend instead of a gap or an overlap. A mitre
// point can shoot arbitrarily far out on a very sharp turn (the classic mitre-join degeneracy), so
// it is clamped to a limit (MitreLimit * halfWidth) and falls back to a simple bevel (the straight
// average of the two rail endpoints) beyond that -- never NaN/near-infinite geometry regardless of
// input angle. End caps (the polyline's first and last vertex) are simple perpendicular caps, IDENTICAL
// to POC-1a's per-segment quad end caps -- so for a single-SEGMENT (2-point, i.e. straight) sidewalk
// this reduces to exactly the same rectangle POC-1a produced (see WalkablePolygonBakerTests /
// SumoBakeNavigationTests, whose fixture network only has straight sidewalks: their baked geometry,
// and hence every downstream assertion, is unaffected byte-for-byte).
internal static class PolylineBuffer
{
    // How far (in units of halfWidth) a mitre join may extend before falling back to a bevel. 4x is
    // a common default in 2D graphics/CAD polyline-offset implementations (e.g. SVG's stroke-miterlimit
    // default) -- generous enough to keep gentle bends sharp-cornered, tight enough to never produce
    // a wild spike on a near-reversal.
    private const double MitreLimit = 4.0;

    // Buffers `shape` (an ordered, distinct-vertex polyline -- NOT implicitly closed) by +-halfWidth
    // into one closed strip polygon ring: `left[0..]` followed by `right[..0]` (right reversed), so
    // the ring winds continuously around the strip's boundary. Returns an empty list if `shape` has
    // fewer than 2 (post length-0-segment filtering) distinct points -- caller treats that the same
    // way it already treats a degenerate/zero-length shape.
    public static List<Vec2> Buffer(IReadOnlyList<Vec2> shape, double halfWidth)
    {
        var pts = DropDegenerateSegments(shape);
        if (pts.Count < 2)
        {
            return new List<Vec2>();
        }

        var segCount = pts.Count - 1;
        var dir = new Vec2[segCount];
        var perp = new Vec2[segCount]; // perpendicular offset vector (already scaled by halfWidth)
        for (var s = 0; s < segCount; s++)
        {
            dir[s] = (pts[s + 1] - pts[s]).Normalized();
            perp[s] = dir[s].PerpCW * halfWidth;
        }

        var left = new List<Vec2>(pts.Count) { pts[0] + perp[0] };
        var right = new List<Vec2>(pts.Count) { pts[0] - perp[0] };

        for (var v = 1; v < segCount; v++)
        {
            // Interior vertex between segment (v-1) and segment v: join the two rails.
            left.Add(MitreJoin(pts[v], perp[v - 1], perp[v], dir[v - 1], dir[v], halfWidth));
            right.Add(MitreJoin(pts[v], -perp[v - 1], -perp[v], dir[v - 1], dir[v], halfWidth));
        }

        left.Add(pts[^1] + perp[^1]);
        right.Add(pts[^1] - perp[^1]);

        var ring = new List<Vec2>(left.Count + right.Count);
        ring.AddRange(left);
        for (var i = right.Count - 1; i >= 0; i--)
        {
            ring.Add(right[i]);
        }

        return ring;
    }

    // Mitre-joins the rail offset from segment (v-1) (endpoint `p + offsetA`, direction `dirA`) with
    // the rail offset from segment v (start point `p + offsetB`, direction `dirB`) by intersecting
    // the two as INFINITE lines. Falls back to the simple bevel point (average of the two rail
    // endpoints) when the lines are near-parallel (near-zero cross product -- a negligible bend, or a
    // near-180-degree reversal where a true mitre point is undefined/at infinity) or when the
    // resulting mitre point would exceed MitreLimit * halfWidth from `p` (an extreme, near-reversal
    // turn where a true mitre would spike out absurdly far).
    private static Vec2 MitreJoin(Vec2 p, Vec2 offsetA, Vec2 offsetB, Vec2 dirA, Vec2 dirB, double halfWidth)
    {
        var p1 = p + offsetA;
        var p2 = p + offsetB;
        var bevel = 0.5 * (p1 + p2);

        var cross = Vec2.Det(dirA, dirB);
        if (Math.Abs(cross) < 1e-9)
        {
            return bevel; // parallel-ish rails: no turn (or a degenerate reversal) -- bevel is exact/safe
        }

        var t = Vec2.Det(p2 - p1, dirB) / cross;
        var mitre = p1 + (dirA * t);

        var limit = MitreLimit * halfWidth;
        if ((mitre - p).AbsSq > limit * limit)
        {
            return bevel; // sharp near-reversal: true mitre point would spike out too far
        }

        return mitre;
    }

    // Filters `shape` down to a list of distinct points, dropping any point that would form a
    // zero-length segment with its predecessor (mirrors WalkablePolygonBaker's old per-segment
    // "skip degenerate zero-length shape segment" check, generalised across the whole polyline).
    private static List<Vec2> DropDegenerateSegments(IReadOnlyList<Vec2> shape)
    {
        var pts = new List<Vec2>(shape.Count);
        foreach (var p in shape)
        {
            if (pts.Count == 0 || (p - pts[^1]).AbsSq > PolygonGeometry.DegenerateLengthSq)
            {
                pts.Add(p);
            }
        }

        return pts;
    }
}
