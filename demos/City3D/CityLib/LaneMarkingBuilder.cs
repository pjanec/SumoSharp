using SceneMarking = Sim.Scene.LaneMarkingBuilder;

namespace CityLib;

// docs/LIVE-CITY-VISUALS-NOTES.md "Lane markings" row: dashed white lane-DIVIDER striping on a lane's LEFT
// edge. docs/HROT-INTEGRATION-DESIGN.md H2: the dash math now lives in Sim.Scene.LaneMarkingBuilder (SUMO's
// Z-up frame); this wrapper keeps CityLib's public API and returns Godot-space RibbonMesh.
public static class LaneMarkingBuilder
{
    // See Sim.Scene.LaneMarkingBuilder.Build for the parameters (`laneWidth` is the LANE's width, the seam
    // sits laneWidth/2 left of the centreline; dash/gap run lengths; paint width; elevation offset above
    // the lane's own surface).
    public static (RibbonMesh Mesh, int DashCount) Build(
        SumoGodotFrame frame,
        IReadOnlyList<(double X, double Y)> shape,
        double laneWidth,
        double dashLength = 1.0,
        double gapLength = 1.0,
        double markWidth = 0.15,
        double elevationOffsetSumoZ = 0.02,
        IReadOnlyList<double>? shapeZ = null)
    {
        var (mesh, count) = SceneMarking.Build(
            frame.Scene, shape, laneWidth, dashLength, gapLength, markWidth, elevationOffsetSumoZ, shapeZ);
        return (SceneToGodot.Ribbon(mesh), count);
    }
}
