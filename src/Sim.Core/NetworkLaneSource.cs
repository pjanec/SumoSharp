using Sim.Ingest;

namespace Sim.Core;

// Adapts a parsed NetworkModel to ILaneShapeSource so PoseResolver can walk lane geometry by dense handle.
// Both the engine (which holds a NetworkModel) and a network/replication client (which parses the same
// net the geometry topic was built from) construct one. Read-only; no per-call allocation.
public sealed class NetworkLaneSource : ILaneShapeSource
{
    private readonly IReadOnlyList<Lane> _lanesByHandle;

    public NetworkLaneSource(NetworkModel network)
    {
        _lanesByHandle = (network ?? throw new ArgumentNullException(nameof(network))).LanesByHandle;
    }

    public double LaneLength(int laneHandle) => _lanesByHandle[laneHandle].Length;

    public IReadOnlyList<(double X, double Y)> LaneShape(int laneHandle) => _lanesByHandle[laneHandle].Shape;

    public IReadOnlyList<double>? LaneShapeZ(int laneHandle) => _lanesByHandle[laneHandle].ShapeZ;
}
