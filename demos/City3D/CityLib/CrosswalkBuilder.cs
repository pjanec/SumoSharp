using SceneCrosswalk = Sim.Scene.CrosswalkBuilder;

namespace CityLib;

// docs/LIVE-CITY-VISUALS-NOTES.md "Crosswalk zebra" row / DESIGN-live-city-2d-viz.md §2 layer 9: perpendicular
// white zebra stripes on a SUMO internal crossing lane. docs/HROT-INTEGRATION-DESIGN.md H2: the stripe math
// (and the crossing-id test, and the ribbon merge) now live in Sim.Scene.CrosswalkBuilder, emitted in
// SUMO's Z-up frame; this wrapper keeps CityLib's public API and returns Godot-space RibbonMesh.
public static class CrosswalkBuilder
{
    public static bool IsCrossingLaneId(string? laneOrEdgeId) => SceneCrosswalk.IsCrossingLaneId(laneOrEdgeId);

    // See Sim.Scene.CrosswalkBuilder.Build for the parameters (stripe spacing/thickness, width fraction,
    // paint elevation offset above the lane's own surface).
    public static (RibbonMesh Mesh, int StripeCount) Build(
        SumoGodotFrame frame,
        IReadOnlyList<(double X, double Y)> shape,
        double width,
        double stripeSpacing = 1.0,
        double stripeThickness = 0.5,
        double widthFraction = 0.8,
        double elevationOffsetSumoZ = 0.02,
        IReadOnlyList<double>? shapeZ = null)
    {
        var (mesh, count) = SceneCrosswalk.Build(
            frame.Scene, shape, width, stripeSpacing, stripeThickness, widthFraction, elevationOffsetSumoZ, shapeZ);
        return (SceneToGodot.Ribbon(mesh), count);
    }

    // Concatenates several independently-built RibbonMesh parts into one (index-offset merge); the Viewer
    // calls this to merge its per-lane crosswalk/marking ribbons into the ONE MeshInstance3D each overlay
    // layer uses. The concatenation is frame-independent, so it reuses Sim.Scene's merge on the same arrays.
    public static RibbonMesh MergeRibbons(IReadOnlyList<RibbonMesh> parts)
    {
        var scene = new Sim.Scene.SceneMesh[parts.Count];
        for (var i = 0; i < scene.Length; i++)
        {
            scene[i] = SceneToGodot.AsScene(parts[i]);
        }

        return SceneToGodot.AsRibbon(Sim.Scene.SceneMesh.Merge(scene));
    }
}
