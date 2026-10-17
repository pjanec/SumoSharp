using Sim.Ingest;

namespace Sim.Scene;

// What RoadSceneBuilder.Build returns: the static road scene as KIND-tagged meshes (one merged mesh per
// kind, in the fixed order Asphalt, Junction, LaneMarking, Crosswalk -- kinds with nothing to draw are
// omitted) plus the signal-head placements. All geometry is in the SceneFrame's local SUMO frame (Z up).
public sealed class RoadScene
{
    public RoadScene(IReadOnlyList<(SceneKind Kind, SceneMesh Mesh)> meshes, IReadOnlyList<SignalHead> signalHeads)
    {
        Meshes = meshes;
        SignalHeads = signalHeads;
    }

    public IReadOnlyList<(SceneKind Kind, SceneMesh Mesh)> Meshes { get; }
    public IReadOnlyList<SignalHead> SignalHeads { get; }
}

// docs/HROT-INTEGRATION-DESIGN.md H2 (NEW): the one-call aggregate of the static road scene, composed the
// way the City3D viewer's Main.cs composes it, minus everything engine-specific:
//   * Asphalt     -- every lane's ribbon, INTERNAL (junction-interior), crossing and sidewalk lanes
//                    included (Main.BuildRoadMeshes: RoadMeshBuilder.BuildAll(includeInternal: true));
//   * Junction    -- every junction's fill polygon (NEW; the viewer did not draw these);
//   * Crosswalk   -- zebra stripes on every crossing lane (CrosswalkBuilder.IsCrossingLaneId);
//   * LaneMarking -- dashed seam on every ROAD-vehicle lane that has a road-vehicle left neighbour
//                    (lane.AllowsRoadVehicle && lane.LeftNeighbor >= 0) -- a crossing lane never also
//                    gets markings, exactly as in Main.BuildCrosswalksAndLaneMarkings;
//   * SignalHeads -- one per road-TL-controlled approach lane (the same set the engine publishes as
//                    TlStateByLane: non-internal lanes whose connection is controlled by a <tlLogic>
//                    with a link index), via TrafficLightPlacer.Place.
public static class RoadSceneBuilder
{
    public static RoadScene Build(NetworkModel net, SceneFrame frame)
    {
        var asphalt = new List<SceneMesh>();
        var junctions = new List<SceneMesh>();
        var crosswalks = new List<SceneMesh>();
        var markings = new List<SceneMesh>();

        foreach (var (_, mesh) in RoadMeshBuilder.BuildAll(frame, net, includeInternal: true))
        {
            if (!mesh.IsEmpty)
            {
                asphalt.Add(mesh);
            }
        }

        foreach (var (_, mesh) in JunctionMeshBuilder.BuildAll(frame, net))
        {
            junctions.Add(mesh);
        }

        var controlled = new List<int>();
        foreach (var lane in net.LanesByHandle)
        {
            if (CrosswalkBuilder.IsCrossingLaneId(lane.Id))
            {
                var (mesh, stripeCount) = CrosswalkBuilder.Build(frame, lane.Shape, lane.Width, shapeZ: lane.ShapeZ);
                if (stripeCount > 0)
                {
                    crosswalks.Add(mesh);
                }
            }
            else if (lane.AllowsRoadVehicle && lane.LeftNeighbor >= 0)
            {
                var (mesh, dashCount) = LaneMarkingBuilder.Build(frame, lane.Shape, lane.Width, shapeZ: lane.ShapeZ);
                if (dashCount > 0)
                {
                    markings.Add(mesh);
                }
            }

            if (!lane.Id.StartsWith(':')
                && net.TryGetTlControlledConnection(lane.EdgeId, lane.Index, out var conn)
                && conn.Tl is { } tl && net.TlLogicsById.ContainsKey(tl)
                && conn.LinkIndex is { } li && li >= 0)
            {
                controlled.Add(lane.Handle);
            }
        }

        var meshes = new List<(SceneKind Kind, SceneMesh Mesh)>(4);
        AddMerged(meshes, SceneKind.Asphalt, asphalt);
        AddMerged(meshes, SceneKind.Junction, junctions);
        AddMerged(meshes, SceneKind.LaneMarking, markings);
        AddMerged(meshes, SceneKind.Crosswalk, crosswalks);

        return new RoadScene(meshes, TrafficLightPlacer.Place(frame, net, controlled));
    }

    private static void AddMerged(List<(SceneKind Kind, SceneMesh Mesh)> into, SceneKind kind, List<SceneMesh> parts)
    {
        if (parts.Count > 0)
        {
            into.Add((kind, SceneMesh.Merge(parts)));
        }
    }
}
