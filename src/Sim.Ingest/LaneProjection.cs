namespace Sim.Ingest;

// The INVERSE of LaneGeometry.PositionAtOffset: given a world point, find where it lands in a lane's
// own (longitudinal offset, lateral offset) frame. This is the world -> lane-relative half of the
// cross-regime bridge (docs/LANELESS-DIRECTION.md) -- it lets the lane engine express an open-space
// crowd agent (which lives in world x/y) as a lane-relative footprint neighbour the sublane/RVO solve
// can consume, exactly mirroring how PositionAtOffset expresses a lane-relative vehicle in world x/y.
//
// Purely geometric and side-effect-free (like LaneGeometry): it never feeds back into kinematic
// state. Convention matches PositionAtOffset: `offset` is arc length from the lane start along the
// polyline; positive `latOffset` is LEFT of travel (the +90 deg / CCW normal), so a point on the
// left of the lane centre-line yields a positive lateral offset.
public static class LaneProjection
{
    // Project (px, py) onto the lane polyline `shape`. Returns the arc-length `Offset` of the closest
    // point on the centre-line, the signed `LatOffset` (perpendicular distance, +left of travel), and
    // the unsigned `Distance` to the centre-line. Finds the globally closest segment (a lane can bend),
    // so it is correct for multi-segment shapes, not just straight lanes.
    public static (double Offset, double LatOffset, double Distance) Project(
        IReadOnlyList<(double X, double Y)> shape, double px, double py)
    {
        if (shape.Count == 0)
        {
            throw new InvalidOperationException("Lane shape must contain at least one point.");
        }

        if (shape.Count == 1)
        {
            var dx0 = px - shape[0].X;
            var dy0 = py - shape[0].Y;
            return (0.0, 0.0, Math.Sqrt(dx0 * dx0 + dy0 * dy0));
        }

        var bestDistSq = double.PositiveInfinity;
        var bestOffset = 0.0;
        var bestLat = 0.0;
        var arcBase = 0.0;   // arc length accumulated up to the start of the current segment

        for (var i = 0; i < shape.Count - 1; i++)
        {
            var (x1, y1) = shape[i];
            var (x2, y2) = shape[i + 1];
            var dx = x2 - x1;
            var dy = y2 - y1;
            var segLenSq = dx * dx + dy * dy;
            var segLen = Math.Sqrt(segLenSq);

            // Parameter t of the closest point on this segment, clamped to [0, 1].
            var t = segLenSq > 0.0 ? ((px - x1) * dx + (py - y1) * dy) / segLenSq : 0.0;
            t = Math.Clamp(t, 0.0, 1.0);

            var cx = x1 + dx * t;
            var cy = y1 + dy * t;
            var ddx = px - cx;
            var ddy = py - cy;
            var distSq = ddx * ddx + ddy * ddy;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestOffset = arcBase + t * segLen;
                // Signed lateral: + when the point is LEFT of travel. Left-normal of (dx,dy) is
                // (-dy, dx); the sign of the point's projection onto it gives the side.
                var lat = segLen > 0.0 ? (ddx * (-dy) + ddy * dx) / segLen : 0.0;
                bestLat = lat;
            }

            arcBase += segLen;
        }

        return (bestOffset, bestLat, Math.Sqrt(bestDistSq));
    }
}
