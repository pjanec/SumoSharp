using System;
using System.Collections.Generic;
using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation;

// docs/EXTERNAL-NET-LOADING-DESIGN.md §3.4 (C2): the one place that turns a retained per-vertex
// elevation channel (PedLane.ShapeZ / PedCrossing.ShapeZ / PedWalkingArea.PolygonZ) into "the surface
// height at this 2-D point on that element".
//
// Shared by both `IPedNavigation` providers so they cannot drift apart, and kept deliberately small:
// project the query point onto the element's own polyline, then LERP its two bracketing elevations by
// the same parameter. This is retain-and-interpolate, not a search over the network -- the caller has
// already decided WHICH element the point belongs to (via the provider's existing lane/polygon
// location step, the same one `HalfWidthsAlong` uses); all this does is read off the height along it.
//
// OUTPUT-ONLY, like every other consumer of these channels: nothing here feeds steering, ORCA or
// routing.
public static class PolylineElevation
{
    // Elevation at the point on `shape` nearest to `p`, interpolated from `shapeZ`.
    //
    // Returns 0.0 whenever there is nothing to interpolate -- a null/empty channel (a 2-D net), or a
    // degenerate shape. 0.0 is the documented flat default throughout this feature, so a 2-D net
    // behaves exactly as it did before elevation existed.
    //
    // `shapeZ` shorter than `shape` is tolerated rather than trusted: indices past its end clamp to its
    // last value. Index alignment is C1's contract and is asserted by its tests; this is the belt to
    // that braces, so a malformed net degrades to a slightly-wrong height instead of an exception on a
    // render path.
    public static double AtNearestPoint(IReadOnlyList<Vec2> shape, IReadOnlyList<double>? shapeZ, Vec2 p)
    {
        if (shapeZ is not { Count: > 0 } zs || shape.Count == 0)
        {
            return 0.0;
        }

        if (shape.Count == 1)
        {
            return zs[0];
        }

        var bestD2 = double.PositiveInfinity;
        var bestSeg = 0;
        var bestT = 0.0;

        for (var i = 0; i < shape.Count - 1; i++)
        {
            var a = shape[i];
            var b = shape[i + 1];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len2 = (dx * dx) + (dy * dy);

            var t = len2 > 0.0
                ? Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / len2, 0.0, 1.0)
                : 0.0;

            var qx = a.X + (t * dx);
            var qy = a.Y + (t * dy);
            var d2 = ((p.X - qx) * (p.X - qx)) + ((p.Y - qy) * (p.Y - qy));

            if (d2 < bestD2)
            {
                bestD2 = d2;
                bestSeg = i;
                bestT = t;
            }
        }

        var z0 = zs[Math.Min(bestSeg, zs.Count - 1)];
        var z1 = zs[Math.Min(bestSeg + 1, zs.Count - 1)];
        return z0 + ((z1 - z0) * bestT);
    }

    // `AtNearestPoint` for every vertex of `path`, in order -- the shape `ElevationsAlong` returns when
    // a provider resolves the whole path against one element. Never null, always `path.Count` long.
    public static double[] AlongAgainst(
        IReadOnlyList<Vec2> path, IReadOnlyList<Vec2> shape, IReadOnlyList<double>? shapeZ)
    {
        var result = new double[path.Count];
        for (var i = 0; i < path.Count; i++)
        {
            result[i] = AtNearestPoint(shape, shapeZ, path[i]);
        }

        return result;
    }
}
