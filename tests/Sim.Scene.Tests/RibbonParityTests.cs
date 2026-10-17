using Sim.Ingest;
using Sim.Scene.Tests.Reference;
using Xunit;

namespace Sim.Scene.Tests;

// H2 acceptance: Sim.Scene's Z-up ribbon, after the (x, y, z) -> (x, z, -y) swap that CityLib applies,
// equals the PRE-extraction CityLib ribbon (Reference/LegacyGodotRibbon.cs, a verbatim copy) vertex for
// vertex -- bitwise, not within a tolerance.
public class RibbonParityTests
{
    public static IEnumerable<object[]> Frames()
    {
        var net = TestNet.Box;
        var cx = 0.0;
        var cy = 0.0;
        var n = 0;
        foreach (var lane in net.LanesByHandle)
        {
            foreach (var (x, y) in lane.Shape)
            {
                cx += x;
                cy += y;
                n++;
            }
        }

        yield return new object[] { 0.0, 0.0, 0.0 };
        yield return new object[] { Math.Round(cx / n, 1), Math.Round(cy / n, 1), 0.0 };
        yield return new object[] { 91850.0, 73960.0, 385.25 };
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void SceneRibbon_AfterSwap_EqualsLegacyGodotRibbon_ForEveryBoxLane(double ox, double oy, double oz)
    {
        var net = TestNet.Box;
        var sceneFrame = new SceneFrame(ox, oy, oz);
        var legacyFrame = new LegacyGodotFrame(ox, oy, oz);

        var compared = 0;
        foreach (var lane in net.LanesByHandle)
        {
            var scene = RoadMeshBuilder.Build(sceneFrame, lane.Shape, lane.ShapeZ, lane.Width);
            var legacy = LegacyRoadMeshBuilder.Build(legacyFrame, lane.Shape, lane.ShapeZ, lane.Width);

            Assert.Equal(legacy.Vertices.Length, scene.Vertices.Length);
            Assert.Equal(legacy.Indices, scene.Indices);
            Assert.Equal(legacy.Area, scene.Area);
            for (var i = 0; i + 2 < scene.Vertices.Length; i += 3)
            {
                // (x, y, z) -> (x, z, -y)
                Assert.Equal(legacy.Vertices[i + 0], scene.Vertices[i + 0]);
                Assert.Equal(legacy.Vertices[i + 1], scene.Vertices[i + 2]);
                Assert.Equal(legacy.Vertices[i + 2], -scene.Vertices[i + 1]);
                // Z-up normal (0,0,1) <-> Godot up-normal (0,1,0).
                Assert.Equal(legacy.Normals[i + 0], scene.Normals[i + 0]);
                Assert.Equal(legacy.Normals[i + 1], scene.Normals[i + 2]);
                Assert.Equal(legacy.Normals[i + 2], 0f - scene.Normals[i + 1]);
            }

            if (scene.Vertices.Length > 0)
            {
                compared++;
            }
        }

        Assert.True(compared > 100, $"expected the box net to contribute many lanes, compared {compared}");
    }

    [Fact]
    public void SceneRibbon_WithElevation_PlacesZUp()
    {
        var shape = new (double X, double Y)[] { (0, 0), (10, 0) };
        var z = new[] { 3.0, 5.0 };

        var mesh = RoadMeshBuilder.Build(new SceneFrame(0, 0, 1.0), shape, z, 4.0);

        // left/right at each end: y = +/-2, z = elevation - originZ.
        Assert.Equal(new[] { 0f, 2f, 2f, 0f, -2f, 2f, 10f, 2f, 4f, 10f, -2f, 4f }, mesh.Vertices);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(1f, mesh.Normals[(i * 3) + 2]));
        Assert.Equal(40.0, mesh.Area, 9);
    }

    [Fact]
    public void RoadScene_OnBox_HasEveryKindAndTrafficLights()
    {
        var scene = RoadSceneBuilder.Build(TestNet.Box, new SceneFrame(2000.0, 3000.0, 0.0));

        var kinds = scene.Meshes.Select(m => m.Kind).ToArray();
        Assert.Equal(
            new[] { SceneKind.Asphalt, SceneKind.Junction, SceneKind.LaneMarking, SceneKind.Crosswalk }, kinds);
        Assert.All(scene.Meshes, m => Assert.False(m.Mesh.IsEmpty));
        Assert.All(scene.Meshes, m => Assert.Equal(m.Mesh.Vertices.Length, m.Mesh.Normals.Length));
        Assert.All(scene.Meshes, m => Assert.All(m.Mesh.Indices, i => Assert.InRange(i, 0, m.Mesh.VertexCount - 1)));
        Assert.NotEmpty(scene.SignalHeads);
        // Heads stand TrafficLightPlacer.HeadHeightMeters above their pole base, in +z.
        Assert.All(scene.SignalHeads, h => Assert.Equal(h.PoleZ + TrafficLightPlacer.HeadHeightMeters, h.HeadZ));
    }
}
