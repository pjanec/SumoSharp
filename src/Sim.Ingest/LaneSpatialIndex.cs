namespace Sim.Ingest;

// HROT H12 (docs/HROT-INTEGRATION-DESIGN.md): "which lanes pass within r of this world point?" -- the spatial
// half of feeding a host's own moving bodies (vehicles, soldiers) into the lane engine as lane obstacles.
//
// A uniform grid over every lane's polyline SEGMENTS, built once from a NetworkModel. Same construction as
// SumoRouteGraphNav's B1 nearest-lane grid (src/Sim.Pedestrians/Navigation/RouteGraph/SumoRouteGraphNav.cs,
// BuildGrid/ComputeCellSize): each segment is registered in every cell its axis-aligned bounding box
// overlaps, and the cell size is the median segment length. That grid is private to the pedestrian route
// graph and indexes RouteNodes (sidewalks/crossings/walking areas), not vehicle Lanes, and Sim.Ingest cannot
// reference Sim.Pedestrians -- so the approach is reused, not the type. One deliberate difference: the cell
// size is FLOORED at MinCellSize (10 m, the nav floors at 1 m). A vehicle net mixes many sub-metre internal-lane
// segments (which drag the median down) with long diagonal edge segments, and AABB registration costs
// (len/cell)^2 cells per diagonal segment -- a 500 m diagonal at a 1 m cell is ~125 000 cells.
//
// Exact, not approximate: a candidate segment is accepted only when its true closest-point distance is
// <= radius, computed with exactly LaneProjection.Project's arithmetic, so LanesNear agrees with a brute-force
// LaneProjection scan. Read-only after construction and allocation-free per query (the caller owns the output
// list), so it is safe to query from several threads at once.
public sealed class LaneSpatialIndex
{
    public const double MinCellSize = 10.0;

    private readonly IReadOnlyList<Lane> _lanes;
    private readonly double _cellSize;
    private readonly Dictionary<(int Cx, int Cy), (int Lane, int Seg)[]> _grid;

    public LaneSpatialIndex(NetworkModel network)
        : this((network ?? throw new ArgumentNullException(nameof(network))).LanesByHandle)
    {
    }

    // `lanesByHandle[h].Handle == h` (NetworkModel.LanesByHandle's contract); the returned ids are those handles.
    public LaneSpatialIndex(IReadOnlyList<Lane> lanesByHandle)
    {
        _lanes = lanesByHandle ?? throw new ArgumentNullException(nameof(lanesByHandle));
        _cellSize = ComputeCellSize(_lanes);
        _grid = BuildGrid(_lanes, _cellSize);
    }

    public double CellSize => _cellSize;

    public int LaneCount => _lanes.Count;

    // Every lane (dense Lane.Handle) whose centre-line polyline passes within `radius` of (x, y), i.e. whose
    // LaneProjection.Project(...).Distance <= radius. `laneHandlesOut` is CLEARED first, then filled
    // deduplicated in ascending handle order (deterministic, independent of grid layout). Every lane kind is
    // returned (internal junction lanes, sidewalks, ...) -- the caller filters, e.g. on Lane.AllowsRoadVehicle.
    // A negative or NaN radius returns nothing.
    public void LanesNear(double x, double y, double radius, List<int> laneHandlesOut)
    {
        if (laneHandlesOut is null)
        {
            throw new ArgumentNullException(nameof(laneHandlesOut));
        }

        laneHandlesOut.Clear();
        if (!(radius >= 0.0) || _grid.Count == 0)
        {
            return;
        }

        var rSq = radius * radius;
        var cx0 = (int)Math.Floor((x - radius) / _cellSize);
        var cx1 = (int)Math.Floor((x + radius) / _cellSize);
        var cy0 = (int)Math.Floor((y - radius) / _cellSize);
        var cy1 = (int)Math.Floor((y + radius) / _cellSize);

        for (var cx = cx0; cx <= cx1; cx++)
        {
            for (var cy = cy0; cy <= cy1; cy++)
            {
                if (!_grid.TryGetValue((cx, cy), out var candidates))
                {
                    continue;
                }

                foreach (var (lane, seg) in candidates)
                {
                    if (SegmentDistanceSq(_lanes[lane].Shape, seg, x, y) <= rSq)
                    {
                        laneHandlesOut.Add(lane);
                    }
                }
            }
        }

        // A lane is registered once per (segment, cell) it overlaps, so the raw hits repeat: sort, then unique
        // in place. No per-index scratch state, which is what keeps concurrent queries safe.
        if (laneHandlesOut.Count > 1)
        {
            laneHandlesOut.Sort();
            var w = 1;
            for (var i = 1; i < laneHandlesOut.Count; i++)
            {
                if (laneHandlesOut[i] != laneHandlesOut[w - 1])
                {
                    laneHandlesOut[w++] = laneHandlesOut[i];
                }
            }

            laneHandlesOut.RemoveRange(w, laneHandlesOut.Count - w);
        }
    }

    // A single-point shape is one zero-length segment (so it is still findable, like SumoRouteGraphNav); an
    // empty shape has none.
    private static int SegmentCount(IReadOnlyList<(double X, double Y)> shape) =>
        shape.Count <= 1 ? shape.Count : shape.Count - 1;

    private static ((double X, double Y) A, (double X, double Y) B) Segment(
        IReadOnlyList<(double X, double Y)> shape, int seg) =>
        shape.Count == 1 ? (shape[0], shape[0]) : (shape[seg], shape[seg + 1]);

    // Squared closest-point distance from (px, py) to segment `seg` -- term for term LaneProjection.Project's
    // per-segment arithmetic (clamped t, closest point, squared offset), so the two agree to the bit.
    private static double SegmentDistanceSq(IReadOnlyList<(double X, double Y)> shape, int seg, double px, double py)
    {
        var ((x1, y1), (x2, y2)) = Segment(shape, seg);
        var dx = x2 - x1;
        var dy = y2 - y1;
        var segLenSq = dx * dx + dy * dy;
        var t = segLenSq > 0.0 ? ((px - x1) * dx + (py - y1) * dy) / segLenSq : 0.0;
        t = Math.Clamp(t, 0.0, 1.0);
        var ddx = px - (x1 + dx * t);
        var ddy = py - (y1 + dy * t);
        return ddx * ddx + ddy * ddy;
    }

    private static double ComputeCellSize(IReadOnlyList<Lane> lanes)
    {
        var lengths = new List<double>();
        foreach (var lane in lanes)
        {
            var n = SegmentCount(lane.Shape);
            for (var s = 0; s < n; s++)
            {
                var ((x1, y1), (x2, y2)) = Segment(lane.Shape, s);
                lengths.Add(Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)));
            }
        }

        if (lengths.Count == 0)
        {
            return MinCellSize;
        }

        lengths.Sort();
        return Math.Max(lengths[lengths.Count / 2], MinCellSize);
    }

    private static Dictionary<(int Cx, int Cy), (int Lane, int Seg)[]> BuildGrid(IReadOnlyList<Lane> lanes, double cellSize)
    {
        var build = new Dictionary<(int Cx, int Cy), List<(int Lane, int Seg)>>();
        for (var lane = 0; lane < lanes.Count; lane++)
        {
            var shape = lanes[lane].Shape;
            var n = SegmentCount(shape);
            for (var s = 0; s < n; s++)
            {
                var ((x1, y1), (x2, y2)) = Segment(shape, s);
                var cx0 = (int)Math.Floor(Math.Min(x1, x2) / cellSize);
                var cx1 = (int)Math.Floor(Math.Max(x1, x2) / cellSize);
                var cy0 = (int)Math.Floor(Math.Min(y1, y2) / cellSize);
                var cy1 = (int)Math.Floor(Math.Max(y1, y2) / cellSize);
                for (var cx = cx0; cx <= cx1; cx++)
                {
                    for (var cy = cy0; cy <= cy1; cy++)
                    {
                        if (!build.TryGetValue((cx, cy), out var list))
                        {
                            list = new List<(int Lane, int Seg)>();
                            build[(cx, cy)] = list;
                        }

                        list.Add((lane, s));
                    }
                }
            }
        }

        var grid = new Dictionary<(int Cx, int Cy), (int Lane, int Seg)[]>(build.Count);
        foreach (var kv in build)
        {
            grid[kv.Key] = kv.Value.ToArray();
        }

        return grid;
    }
}
