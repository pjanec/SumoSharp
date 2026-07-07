namespace Sim.Ingest;

// Rung 9b-i: polyline-polyline intersection used to compute junction conflict geometry (the
// crossing point of two internal lanes' shapes, ported conceptually from how
// sumo/src/microsim/MSLink.cpp's myConflicts/getLengthBehindCrossing locate where a link's
// internal lane crosses a foe link's internal lane -- SUMO computes this once at network build
// time (NBRequest/NBNode), we do the equivalent here at ingest time over the already-parsed
// lane shapes).
//
// Only PROPER (transversal) crossings count: two segments that merely touch at a shared
// endpoint -- e.g. two internal lanes that merge into the same downstream lane, ending at the
// same (x,y) -- are foes in the request/foes bitstring (right-of-way still matters at a merge)
// but are not a "crossing" in the geometric sense this helper models; callers skip a foe pair
// when TryIntersect returns false. The standard cross-product parametric test naturally
// distinguishes the two: a proper crossing has both intersection parameters strictly inside
// (0, 1); a shared-endpoint touch lands one (or both) parameters exactly on 0 or 1.
public static class PolylineGeometry
{
    private const double Epsilon = 1e-9;

    public readonly record struct Intersection(double ArcA, double ArcB, (double X, double Y) Point);

    // Scans every segment of `a` against every segment of `b` and returns the first proper
    // crossing found, together with the cumulative arc-length along each polyline to that
    // point. Our callers only ever have at most one true crossing between two internal lanes,
    // so "first" and "only" coincide in practice; segments are scanned in polyline order so a
    // caller relying on "first" gets a stable, well-defined result even in the general case.
    public static bool TryIntersect(
        IReadOnlyList<(double X, double Y)> a,
        IReadOnlyList<(double X, double Y)> b,
        out Intersection intersection)
    {
        var arcA = 0.0;
        for (var i = 0; i < a.Count - 1; i++)
        {
            var a1 = a[i];
            var a2 = a[i + 1];
            var segLenA = Distance(a1, a2);

            var arcB = 0.0;
            for (var j = 0; j < b.Count - 1; j++)
            {
                var b1 = b[j];
                var b2 = b[j + 1];
                var segLenB = Distance(b1, b2);

                if (TrySegmentIntersect(a1, a2, b1, b2, out var t, out var u))
                {
                    var point = (X: a1.X + (a2.X - a1.X) * t, Y: a1.Y + (a2.Y - a1.Y) * t);
                    intersection = new Intersection(arcA + t * segLenA, arcB + u * segLenB, point);
                    return true;
                }

                arcB += segLenB;
            }

            arcA += segLenA;
        }

        intersection = default;
        return false;
    }

    // Standard cross-product segment intersection (p = a1 + t*r, q = b1 + u*s). Only a proper
    // (strictly interior) crossing is reported: t and u must both lie strictly inside (0, 1),
    // which excludes endpoint touches (shared-endpoint merges) and collinear/parallel segments
    // (rxs ~= 0) alike -- neither is a "crossing" for this helper's purposes.
    private static bool TrySegmentIntersect(
        (double X, double Y) a1, (double X, double Y) a2,
        (double X, double Y) b1, (double X, double Y) b2,
        out double t, out double u)
    {
        var rX = a2.X - a1.X;
        var rY = a2.Y - a1.Y;
        var sX = b2.X - b1.X;
        var sY = b2.Y - b1.Y;

        var rxs = rX * sY - rY * sX;
        if (Math.Abs(rxs) < Epsilon)
        {
            // Parallel (or collinear) segments -- no proper crossing.
            t = 0;
            u = 0;
            return false;
        }

        var qpX = b1.X - a1.X;
        var qpY = b1.Y - a1.Y;

        t = (qpX * sY - qpY * sX) / rxs;
        u = (qpX * rY - qpY * rX) / rxs;

        return t > Epsilon && t < 1.0 - Epsilon && u > Epsilon && u < 1.0 - Epsilon;
    }

    private static double Distance((double X, double Y) p1, (double X, double Y) p2)
    {
        var dx = p2.X - p1.X;
        var dy = p2.Y - p1.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
