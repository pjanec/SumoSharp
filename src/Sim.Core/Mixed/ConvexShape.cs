using Sim.Core.Orca;

namespace Sim.Core.Mixed;

// A centered convex polygon footprint for the believable mixed-traffic (Indian) module
// (docs/INDIA-TRAFFIC.md). Vehicles are oriented boxes / hexagons / capsule-polygons rather than
// discs, so that avoidance is ANISOTROPIC: a long vehicle blocks a long swath broadside but is
// narrow end-on, and a small motorcycle threads gaps a bus cannot. This type is the geometry the
// shaped velocity-obstacle solver (ShapedVoSolver) consumes.
//
// Vertices are stored in COUNTER-CLOCKWISE order (matching the OrcaObstacle / RVO2 winding
// convention: polygon interior on the LEFT of each directed edge). Shapes are defined in a LOCAL
// frame with +X = forward (length axis) and +Y = left (width axis), centered on the vehicle
// reference point; Rotated() maps them to a heading. All math is double precision for determinism.
//
// Deliberately independent of the lane-parity core (like the rest of Sim.Core.Orca / Sim.Core.Mixed):
// it is reachable only from the Mixed crowd, never from the lane Engine, so it cannot move the
// determinism hash.
public sealed class ConvexShape
{
    // CCW, centered. Small (typically 4 for a rectangle, 6 for a hexagon, ~12 for a capsule).
    public readonly Vec2[] Verts;

    private ConvexShape(Vec2[] verts)
    {
        Verts = verts;
    }

    // Build from vertices already known to be CCW and convex (e.g. a translated existing shape).
    // Skips the hull pass -- the caller guarantees winding/convexity. Used by the VO solver's
    // translate step, which only shifts an existing convex polygon.
    internal static ConvexShape FromVertsUnchecked(Vec2[] verts) => new(verts);

    public int Count => Verts.Length;

    // Oriented box: full length x full width, +X forward. Cars, buses, auto-rickshaws.
    public static ConvexShape Rectangle(double length, double width)
    {
        var hl = length * 0.5;
        var hw = width * 0.5;
        // CCW starting bottom-right: (+hl,-hw) -> (+hl,+hw) -> (-hl,+hw) -> (-hl,-hw).
        return new ConvexShape(new[]
        {
            new Vec2(hl, -hw),
            new Vec2(hl, hw),
            new Vec2(-hl, hw),
            new Vec2(-hl, -hw),
        });
    }

    // Regular n-gon of the given circum-radius, first vertex on +X. Hexagon (n=6) is the motorcycle
    // footprint the viz already draws; a compact near-round shape that still filters through gaps.
    public static ConvexShape RegularPolygon(int n, double radius)
    {
        if (n < 3)
        {
            throw new ArgumentException("A polygon needs at least 3 vertices.", nameof(n));
        }

        var verts = new Vec2[n];
        for (var i = 0; i < n; i++)
        {
            var a = 2.0 * Math.PI * i / n;   // CCW
            verts[i] = new Vec2(radius * Math.Cos(a), radius * Math.Sin(a));
        }

        return new ConvexShape(verts);
    }

    // Capsule (stadium) approximated as a convex polygon: an oriented box of the straight length with
    // semicircular end-caps of the given radius, capSegs segments per cap. A good long-vehicle shape
    // (rounded ends avoid corner-snagging) while staying a convex polygon the Minkowski/VO path can
    // consume directly.
    public static ConvexShape Capsule(double straightLength, double radius, int capSegs = 4)
    {
        var hl = straightLength * 0.5;
        var verts = new List<Vec2>((capSegs + 1) * 2);
        // Right cap: sweep from -90deg to +90deg around (+hl, 0).
        for (var i = 0; i <= capSegs; i++)
        {
            var a = -Math.PI / 2 + Math.PI * i / capSegs;
            verts.Add(new Vec2(hl + radius * Math.Cos(a), radius * Math.Sin(a)));
        }

        // Left cap: sweep from +90deg to +270deg around (-hl, 0).
        for (var i = 0; i <= capSegs; i++)
        {
            var a = Math.PI / 2 + Math.PI * i / capSegs;
            verts.Add(new Vec2(-hl + radius * Math.Cos(a), radius * Math.Sin(a)));
        }

        // The two shared seam points (a=+90 of right cap == a=+90 start of left cap, etc.) can double
        // up; the convex hull cleans that and guarantees CCW.
        return FromHull(verts);
    }

    // Rotate every vertex by the heading given as (cos, sin). Rotation preserves CCW winding.
    public ConvexShape Rotated(double cos, double sin)
    {
        var r = new Vec2[Verts.Length];
        for (var i = 0; i < Verts.Length; i++)
        {
            var v = Verts[i];
            r[i] = new Vec2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
        }

        return new ConvexShape(r);
    }

    public ConvexShape RotatedTo(double headingRad) => Rotated(Math.Cos(headingRad), Math.Sin(headingRad));

    // Point reflection through the origin (v -> -v). Used to build the Minkowski collision set
    // A (+) (-B). A 180deg rotation, so it preserves CCW winding.
    public ConvexShape Reflected()
    {
        var r = new Vec2[Verts.Length];
        for (var i = 0; i < Verts.Length; i++)
        {
            r[i] = -Verts[i];
        }

        return new ConvexShape(r);
    }

    // Minkowski sum A (+) B: { a + b : a in A, b in B }. For two convex polygons the sum is convex;
    // computed robustly as the convex hull of all pairwise vertex sums (both are tiny -- <= ~12
    // vertices -- so the O(nm) hull is trivial and avoids the edge-merge's angle-sorting pitfalls).
    // The collision set for two centered footprints in relative-position space is A (+) (-B); for
    // centrally-symmetric shapes (rectangles, regular polygons, capsules) -B == B, but Reflected()
    // keeps it correct for any convex shape.
    public ConvexShape MinkowskiSum(ConvexShape other)
    {
        var pts = new List<Vec2>(Verts.Length * other.Verts.Length);
        foreach (var a in Verts)
        {
            foreach (var b in other.Verts)
            {
                pts.Add(a + b);
            }
        }

        return FromHull(pts);
    }

    // Is point p strictly inside (or on) this CCW polygon? Interior is on the LEFT of every directed
    // edge, so p is inside iff it is left-of (>= -eps) all edges.
    public bool ContainsPoint(Vec2 p, double eps = 1e-12)
    {
        var n = Verts.Length;
        for (var i = 0; i < n; i++)
        {
            var a = Verts[i];
            var b = Verts[(i + 1) % n];
            // det(b - a, p - a) >= 0 => p left of edge a->b.
            if (Vec2.Det(b - a, p - a) < -eps)
            {
                return false;
            }
        }

        return true;
    }

    // Support radius along a unit direction: the greatest projection of any vertex onto `dir` (the
    // half-extent of the shape in that direction). Used for the isotropic-fallback radius in the
    // rare deep-overlap recovery branch and for neighbour-range sizing.
    public double SupportRadius(Vec2 dir)
    {
        var max = double.NegativeInfinity;
        foreach (var v in Verts)
        {
            var d = Vec2.Dot(v, dir);
            if (d > max)
            {
                max = d;
            }
        }

        return max;
    }

    // Grow the footprint outward by `margin` on all sides (exact convex offset with rounded corners),
    // computed as the Minkowski sum with a small disc-approximating octagon. Used for the
    // non-holonomic TRACKING-ERROR margin: the SOLVE uses the inflated shape so real footprints stay
    // apart even when bounded steering cannot perfectly track the avoidance velocity (NH-ORCA). The
    // true (un-inflated) shape is still used for rendering and overlap measurement.
    public ConvexShape Inflate(double margin)
    {
        if (margin <= 0.0)
        {
            return this;
        }

        return MinkowskiSum(RegularPolygon(8, margin));
    }

    // Largest distance from the centroid-origin to any vertex (bounding-circle radius). A safe
    // neighbour-range and rendering bound.
    public double CircumRadius()
    {
        var max = 0.0;
        foreach (var v in Verts)
        {
            max = Math.Max(max, v.Abs);
        }

        return max;
    }

    // Andrew's monotone-chain convex hull, returning CCW vertices with collinear points dropped.
    // Deterministic (stable ordering, no RNG). Handles the degenerate <=2 distinct-point cases by
    // returning the distinct points as-is (a thin shape).
    private static ConvexShape FromHull(List<Vec2> pts)
    {
        // Sort by x then y (deterministic).
        pts.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));

        // Remove exact duplicates.
        var uniq = new List<Vec2>(pts.Count);
        foreach (var p in pts)
        {
            if (uniq.Count == 0 || uniq[^1].X != p.X || uniq[^1].Y != p.Y)
            {
                uniq.Add(p);
            }
        }

        if (uniq.Count <= 2)
        {
            return new ConvexShape(uniq.ToArray());
        }

        var hull = new List<Vec2>(uniq.Count + 1);

        // Lower hull.
        foreach (var p in uniq)
        {
            while (hull.Count >= 2 && Vec2.Det(hull[^1] - hull[^2], p - hull[^2]) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(p);
        }

        // Upper hull.
        var lower = hull.Count + 1;
        for (var i = uniq.Count - 2; i >= 0; i--)
        {
            var p = uniq[i];
            while (hull.Count >= lower && Vec2.Det(hull[^1] - hull[^2], p - hull[^2]) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(p);
        }

        hull.RemoveAt(hull.Count - 1);   // last == first
        return new ConvexShape(hull.ToArray());
    }
}
