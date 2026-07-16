using Sim.Core;
using Sim.Replication;

namespace CityLib;

// docs/DEMO-CITY3D-DESIGN.md "Data path" — the consumer-side ILaneShapeSource: built from the RECEIVED
// geometry topic (bus.Source.Geometry), never by re-parsing the .net.xml, so local and remote consume
// geometry identically (design's "ReplicationLaneShapeSource" bullet). Wraps whatever
// IReadOnlyDictionary<int, GeometryCodec.LaneGeo> a bound IReplicationSource exposes -- local
// (InMemoryReplicationBus) and remote (DDS) both shape it the same way, so this type never needs to know
// which transport is behind it.
public sealed class ReplicationLaneShapeSource : ILaneShapeSource
{
    private readonly IReadOnlyDictionary<int, GeometryCodec.LaneGeo> _geometry;

    // Decoded (double,double) polylines, cached per lane handle so repeated per-frame PoseResolver walks
    // (several vehicles a frame, several frames a second) don't re-allocate the same lane's points every
    // call. GeometryCodec.LaneGeo.Points is immutable once received (durable geometry, published once), so
    // this cache is safe to keep for the lifetime of the source.
    private readonly Dictionary<int, (double X, double Y)[]> _shapeCache = new();

    public ReplicationLaneShapeSource(IReadOnlyDictionary<int, GeometryCodec.LaneGeo> geometry)
        => _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));

    public double LaneLength(int laneHandle) => Get(laneHandle).Length;

    public IReadOnlyList<(double X, double Y)> LaneShape(int laneHandle)
    {
        if (_shapeCache.TryGetValue(laneHandle, out var cached))
        {
            return cached;
        }

        var lane = Get(laneHandle);
        var points = new (double X, double Y)[lane.Points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = (lane.Points[i].X, lane.Points[i].Y);
        }

        _shapeCache[laneHandle] = points;
        return points;
    }

    // The wire's GeometryCodec.LaneGeo carries only 2-D points (float X, Y) -- elevation is NOT on the
    // replication wire yet (see GeometryCodec's header comment: "15 B + points*8 B", no z field). A
    // wire-fed lane source is therefore always flat here; the LOCAL path gets real elevation from
    // Sim.Core.NetworkLaneSource (built straight off the parsed NetworkModel, which does carry Lane.ShapeZ)
    // instead. This is the one asymmetry between the two ILaneShapeSource implementations the demo uses --
    // documented rather than silently returning stale/zeroed data.
    public IReadOnlyList<double>? LaneShapeZ(int laneHandle) => null;

    private GeometryCodec.LaneGeo Get(int laneHandle)
    {
        if (_geometry.TryGetValue(laneHandle, out var lane))
        {
            return lane;
        }

        throw new KeyNotFoundException(
            $"ReplicationLaneShapeSource: no geometry received yet for lane handle {laneHandle}.");
    }
}
