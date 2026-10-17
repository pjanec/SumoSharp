using Sim.Ingest;
using Sim.Replication;
using SceneRoad = Sim.Scene.RoadMeshBuilder;

namespace CityLib;

// docs/DEMO-CITY3D-DESIGN.md "Procedural scene generation -> Roads" -- the Godot-space face of the
// engine-agnostic ribbon math. docs/HROT-INTEGRATION-DESIGN.md H2: the algorithm itself now lives in
// Sim.Scene.RoadMeshBuilder (SUMO's own Z-up frame); this class keeps CityLib's public API (RibbonMesh,
// signatures) and applies the (x, y, z) -> (x, z, -y) axis swap to every vertex and normal, so the City3D
// viewer and CityLib.Tests see numerically identical results to before the extraction.
public readonly struct RibbonMesh
{
    public RibbonMesh(float[] vertices, int[] indices, float[] normals, double area)
    {
        Vertices = vertices;
        Indices = indices;
        Normals = normals;
        Area = area;
    }

    // xyz triples, already in GODOT space (CoordinateTransform.SumoToGodot applied).
    public float[] Vertices { get; }
    public int[] Indices { get; }
    public float[] Normals { get; }
    // Sum of the per-quad planar (ground-plane) areas, in square metres.
    public double Area { get; }
}

public static class RoadMeshBuilder
{
    // Turns ONE lane's centerline (+ width, + optional per-vertex elevation) into a flat ribbon mesh in
    // Godot space (Y = elevation from shapeZ, or 0 when null, minus the frame's elevation origin). Pass
    // SumoGodotFrame.Identity for the un-recentered mapping. Math: Sim.Scene.RoadMeshBuilder.Build.
    public static RibbonMesh Build(
        SumoGodotFrame frame, IReadOnlyList<(double X, double Y)> shape, IReadOnlyList<double>? shapeZ, double width)
        => SceneToGodot.Ribbon(SceneRoad.Build(frame.Scene, shape, shapeZ, width));

    // Iterates every lane in `network` (LanesByHandle, D2 dense handle order) and builds its ribbon mesh.
    // `includeInternal` (default true): when false, skips lanes whose Id starts with ':'.
    public static IEnumerable<(int Handle, RibbonMesh Mesh)> BuildAll(
        SumoGodotFrame frame, NetworkModel network, bool includeInternal = true)
    {
        foreach (var (handle, mesh) in SceneRoad.BuildAll(frame.Scene, network, includeInternal))
        {
            yield return (handle, SceneToGodot.Ribbon(mesh));
        }
    }

    // docs/DEMO-CITY3D-DESIGN.md "Data path -> Remote mode" / task T2.2b: the REMOTE-shaped counterpart --
    // ribbons from the RECEIVED wire geometry (IReplicationSource.Geometry) when there is no NetworkModel,
    // threading the wire's additive per-point Z (Stage E1) through. Math: Sim.Scene.RoadMeshBuilder.
    public static IEnumerable<(int Handle, RibbonMesh Mesh)> BuildAll(
        SumoGodotFrame frame, IReadOnlyDictionary<int, GeometryCodec.LaneGeo> geometry, bool includeInternal = true)
    {
        foreach (var (handle, mesh) in SceneRoad.BuildAll(frame.Scene, geometry, includeInternal))
        {
            yield return (handle, SceneToGodot.Ribbon(mesh));
        }
    }
}
