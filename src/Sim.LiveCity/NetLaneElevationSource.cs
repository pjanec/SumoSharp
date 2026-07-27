using System;
using System.Collections.Generic;
using Sim.Core.Orca;
using Sim.Ingest;
using Sim.Pedestrians;
using Sim.Pedestrians.Lod;

namespace Sim.LiveCity;

// docs/EXTERNAL-NET-LOADING-DESIGN.md §4.2 (C2): the concrete `IPedElevationSource` -- "how high is the
// ground under this pedestrian?" answered by sampling the VEHICLE-side network model's own lane
// elevations.
//
// WHY IT LIVES IN Sim.LiveCity. It needs both `Sim.Ingest.NetworkModel` (which carries `Lane.ShapeZ`)
// and `Sim.Pedestrians.PedNetwork` (which says which lanes pedestrians walk on). Sim.Pedestrians may
// never reference Sim.Ingest (docs/PEDESTRIAN-DESIGN.md §0 Principle 6); Sim.LiveCity already references
// both. So this is the one project where the join is legal, and it needs no build-graph change.
//
// THE KEY FACT IT RESTS ON: ped-lane ids (`:J_c0_0` crossings, `:J_w0_0` walking areas, plain sidewalk
// lanes) live in the SAME id space as `NetworkModel.LanesById`, because `NetworkParser.Parse` parses
// EVERY `<edge>` including `function="crossing"` / `function="walkingarea"` / internal ones. That is
// asserted by a test rather than assumed (design §4, C2 success condition 7) -- if a future net or
// parser change broke it, this would silently degrade to z=0 everywhere, which is exactly the kind of
// quiet wrong answer a test should catch.
//
// A ped and a car standing at the same place therefore get their elevation from the same lane geometry
// via the same `LaneGeometry.ElevationAtOffset` call, so they agree by construction rather than by
// coincidence.
//
// Purely OUTPUT-SIDE, like everything else in the elevation story: nothing here feeds back into any
// pedestrian or vehicle state, and no parity path constructs it.
public sealed class NetLaneElevationSource : IPedElevationSource
{
    // Grid cell size in metres. ~25 m puts a handful of urban lane segments in a cell: small enough
    // that a query usually resolves from the first cell, large enough that a city-scale net does not
    // pay for millions of near-empty cells. Not tuned against a measurement -- it is a lookup
    // acceleration for a per-frame render query, and the correctness of the answer does not depend on
    // it (the ring search below widens until it can PROVE it has the nearest segment).
    private const double CellSize = 25.0;

    // One indexed polyline segment: which lane it belongs to, which segment within it, and the arc
    // length along the lane at the segment's start (so a projection onto it converts to a lane offset
    // with one addition, no re-walk of the polyline).
    private readonly struct Segment
    {
        public Segment(int laneIndex, double ax, double ay, double bx, double by, double arcAtStart)
        {
            LaneIndex = laneIndex;
            Ax = ax; Ay = ay; Bx = bx; By = by;
            ArcAtStart = arcAtStart;
        }

        public readonly int LaneIndex;
        public readonly double Ax, Ay, Bx, By;
        public readonly double ArcAtStart;
    }

    private readonly List<IReadOnlyList<(double X, double Y)>> _laneShapes = new();
    private readonly List<IReadOnlyList<double>> _laneShapeZ = new();
    private readonly List<Segment> _segments = new();

    // Uniform grid: cell key -> segment indices. A dictionary rather than a dense array because a cut
    // sub-area's coordinates are far from the origin (~9e4 on the committed georef fixture) and its
    // occupied cells are a thin road network, not a filled rectangle.
    private readonly Dictionary<long, List<int>> _grid = new();
    private readonly int _minCellX, _minCellY, _maxCellX, _maxCellY;

    // Diagnostic (design §4.2 / C2 success condition 7): how many ped-lane ids were looked up, and how
    // many of those resolved to a vehicle-side lane carrying a non-null ShapeZ. A test asserts the
    // ratio rather than trusting the shared-id-space claim.
    public int PedLaneIdCount { get; }

    public int ResolvedLaneCount { get; }

    // True when at least one ped lane resolved to 3-D geometry. False for a 2-D net (the demo), where
    // every `ElevationAt` returns 0.0 -- byte-identical to the pre-C2 literal zero.
    public bool HasElevation => _segments.Count > 0;

    public NetLaneElevationSource(NetworkModel network, PedNetwork pedNetwork)
    {
        if (network is null) throw new ArgumentNullException(nameof(network));
        if (pedNetwork is null) throw new ArgumentNullException(nameof(pedNetwork));

        var minCellX = int.MaxValue; var minCellY = int.MaxValue;
        var maxCellX = int.MinValue; var maxCellY = int.MinValue;
        var pedLaneIds = 0;
        var resolved = 0;

        void Index(string laneId)
        {
            pedLaneIds++;

            // A lane the vehicle-side parser did not produce, or a 2-D one, contributes nothing: a net
            // with no elevation at all indexes zero segments and degrades to the documented z=0.
            if (!network.LanesById.TryGetValue(laneId, out var lane)) return;
            if (lane.ShapeZ is not { Count: > 0 } shapeZ) return;
            if (lane.Shape.Count < 2) return;

            resolved++;
            var laneIndex = _laneShapes.Count;
            _laneShapes.Add(lane.Shape);
            _laneShapeZ.Add(shapeZ);

            var arc = 0.0;
            for (var i = 0; i < lane.Shape.Count - 1; i++)
            {
                var (ax, ay) = lane.Shape[i];
                var (bx, by) = lane.Shape[i + 1];
                _segments.Add(new Segment(laneIndex, ax, ay, bx, by, arc));
                arc += Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
            }
        }

        foreach (var sw in pedNetwork.Sidewalks) Index(sw.Id);
        foreach (var cr in pedNetwork.Crossings) Index(cr.Id);
        foreach (var wa in pedNetwork.WalkingAreas) Index(wa.Id);

        PedLaneIdCount = pedLaneIds;
        ResolvedLaneCount = resolved;

        // Bucket each segment into every cell its bounding box touches. A long segment lands in
        // several cells, which is what makes the ring search below able to find it from any of them.
        for (var s = 0; s < _segments.Count; s++)
        {
            var seg = _segments[s];
            var cx0 = CellOf(Math.Min(seg.Ax, seg.Bx));
            var cx1 = CellOf(Math.Max(seg.Ax, seg.Bx));
            var cy0 = CellOf(Math.Min(seg.Ay, seg.By));
            var cy1 = CellOf(Math.Max(seg.Ay, seg.By));

            for (var cx = cx0; cx <= cx1; cx++)
            {
                for (var cy = cy0; cy <= cy1; cy++)
                {
                    var key = Key(cx, cy);
                    if (!_grid.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<int>();
                        _grid[key] = bucket;
                    }

                    bucket.Add(s);
                    if (cx < minCellX) minCellX = cx;
                    if (cx > maxCellX) maxCellX = cx;
                    if (cy < minCellY) minCellY = cy;
                    if (cy > maxCellY) maxCellY = cy;
                }
            }
        }

        _minCellX = minCellX; _minCellY = minCellY;
        _maxCellX = maxCellX; _maxCellY = maxCellY;
    }

    // IPedElevationSource. Thread-safe: every field is read-only after construction and the search
    // allocates nothing.
    public double ElevationAt(Vec2 pos)
    {
        if (_segments.Count == 0)
        {
            return 0.0; // 2-D net (the demo): exactly the pre-C2 literal zero.
        }

        var best = -1;
        var bestD2 = double.PositiveInfinity;
        var bestT = 0.0;

        var homeX = CellOf(pos.X);
        var homeY = CellOf(pos.Y);

        // Expanding ring search. Stop only once the CLOSEST POSSIBLE point in the next ring is farther
        // than the best hit so far -- probing one cell and taking its nearest member would return a
        // segment that merely shares a cell, not the true nearest one, and near a cell boundary those
        // differ (a ped on a kerb would then take the elevation of the wrong side of the street).
        var maxRing = Math.Max(
            Math.Max(Math.Abs(homeX - _minCellX), Math.Abs(homeX - _maxCellX)),
            Math.Max(Math.Abs(homeY - _minCellY), Math.Abs(homeY - _maxCellY))) + 1;

        for (var ring = 0; ring <= maxRing; ring++)
        {
            if (best >= 0)
            {
                // Everything outside this ring is at least (ring-1)*CellSize away; once that exceeds the
                // best distance found, no farther ring can improve on it.
                var guaranteed = (ring - 1) * CellSize;
                if (guaranteed > 0 && guaranteed * guaranteed > bestD2) break;
            }

            for (var cx = homeX - ring; cx <= homeX + ring; cx++)
            {
                for (var cy = homeY - ring; cy <= homeY + ring; cy++)
                {
                    // Ring, not filled square: interior cells were covered by an earlier iteration.
                    if (ring > 0
                        && cx != homeX - ring && cx != homeX + ring
                        && cy != homeY - ring && cy != homeY + ring)
                    {
                        continue;
                    }

                    if (!_grid.TryGetValue(Key(cx, cy), out var bucket)) continue;

                    foreach (var s in bucket)
                    {
                        var seg = _segments[s];
                        var d2 = DistanceSquaredToSegment(pos.X, pos.Y, seg, out var t);
                        if (d2 < bestD2)
                        {
                            bestD2 = d2;
                            best = s;
                            bestT = t;
                        }
                    }
                }
            }
        }

        if (best < 0)
        {
            return 0.0; // unreachable for a non-empty index, but never guess a height we cannot justify
        }

        var chosen = _segments[best];
        var segLength = Math.Sqrt(
            ((chosen.Bx - chosen.Ax) * (chosen.Bx - chosen.Ax))
            + ((chosen.By - chosen.Ay) * (chosen.By - chosen.Ay)));
        var offset = chosen.ArcAtStart + (bestT * segLength);

        // The SAME function the vehicle side uses for its own PosZ (Engine/PoseResolver both call it),
        // over the same lane's shape -- which is what makes a ped and a car at one place agree.
        return LaneGeometry.ElevationAtOffset(_laneShapes[chosen.LaneIndex], _laneShapeZ[chosen.LaneIndex], offset);
    }

    // Squared distance from (px,py) to the segment, plus the clamped projection parameter t in [0,1].
    private static double DistanceSquaredToSegment(double px, double py, in Segment seg, out double t)
    {
        var dx = seg.Bx - seg.Ax;
        var dy = seg.By - seg.Ay;
        var len2 = (dx * dx) + (dy * dy);

        t = len2 > 0.0
            ? Math.Clamp((((px - seg.Ax) * dx) + ((py - seg.Ay) * dy)) / len2, 0.0, 1.0)
            : 0.0;

        var qx = seg.Ax + (t * dx);
        var qy = seg.Ay + (t * dy);
        return ((px - qx) * (px - qx)) + ((py - qy) * (py - qy));
    }

    private static int CellOf(double v) => (int)Math.Floor(v / CellSize);

    // Pack two cell coords into one key. The shift is 32 so two int cell indices never collide; cell
    // coordinates for a real net are ~1e4 at most (2.5e5 m / 25 m), far inside that.
    private static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;
}
