namespace Sim.Core;

// Perf (PERF-ROADMAP.md Layer 0a): the hot path (the engine's per-vehicle-per-step EmitTrajectory
// -> Add) is now a single append to a List<TrajectoryPoint> of STRUCTS -- amortized zero
// allocation, no per-point object and no red-black-tree node (the former
// Dictionary<string, SortedDictionary<double, TrajectoryPoint>> allocated one tree node PER emitted
// point). The by-(vehicle, time) query surface (VehicleIds / PointsFor / TryGet) that
// TrajectoryComparator, the benches and Sim.Viz use is UNCHANGED, but its backing index is now built
// LAZILY on first query and cached -- so it costs nothing during a run that only appends (e.g. the
// benchmark's timed run) and is materialized once, off the hot path, when a consumer first reads it.
// The lazy index preserves the former SortedDictionary's time-ASCENDING iteration order, so
// order-sensitive consumers (Sim.Bench's TrajectoryHash) see byte-identical output.
public sealed class TrajectorySet
{
    private readonly List<TrajectoryPoint> _points = new();
    private Dictionary<string, SortedDictionary<double, TrajectoryPoint>>? _index;

    public void Add(in TrajectoryPoint point)
    {
        _points.Add(point);
        _index = null; // invalidate the lazily-built query index
    }

    // Every point emitted, in emission (append) order. Used by the comparator's statistical pooling,
    // Sim.Bench and Sim.Viz -- all order-insensitive or re-sorting.
    public IReadOnlyList<TrajectoryPoint> AllPoints => _points;

    public IReadOnlyCollection<string> VehicleIds => Index().Keys;

    public IReadOnlyDictionary<double, TrajectoryPoint> PointsFor(string vehicleId) =>
        Index().TryGetValue(vehicleId, out var byTime) ? byTime : EmptyTimePoints;

    public bool TryGet(string vehicleId, double time, out TrajectoryPoint point)
    {
        if (Index().TryGetValue(vehicleId, out var byTime) && byTime.TryGetValue(time, out var found))
        {
            point = found;
            return true;
        }

        point = default;
        return false;
    }

    // Build (or return the cached) by-(vehicle, time) index. SortedDictionary preserves the former
    // structure's time-ascending iteration exactly. Built once per (Add-quiescent) query batch; the
    // hot append path never touches it. Points are appended in step order, so each inner dictionary
    // receives ascending times (cheap inserts).
    private Dictionary<string, SortedDictionary<double, TrajectoryPoint>> Index()
    {
        if (_index is not null)
        {
            return _index;
        }

        var index = new Dictionary<string, SortedDictionary<double, TrajectoryPoint>>();
        foreach (var point in _points)
        {
            if (!index.TryGetValue(point.VehicleId, out var byTime))
            {
                byTime = new SortedDictionary<double, TrajectoryPoint>();
                index[point.VehicleId] = byTime;
            }

            byTime[point.Time] = point;
        }

        _index = index;
        return index;
    }

    private static readonly SortedDictionary<double, TrajectoryPoint> EmptyTimePoints = new();
}
