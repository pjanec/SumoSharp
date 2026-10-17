using Xunit;

namespace Sim.Scene.Tests;

public class SceneFrameTests
{
    [Fact]
    public void ToLocal_SubtractsOriginOnEveryAxis_NoAxisSwap()
    {
        var frame = new SceneFrame(1000.0, 2000.0, 300.0);

        var (x, y, z) = frame.ToLocal(1012.5, 1990.25, 305.75);

        Assert.Equal(12.5f, x);
        Assert.Equal(-9.75f, y);
        Assert.Equal(5.75f, z);
    }

    [Fact]
    public void ToLocal_SubtractsInDoubleBeforeTheFloatCast()
    {
        // At 1e5 a float ULP is ~8 mm: casting first would lose the 1 mm difference.
        var frame = new SceneFrame(91850.0, 73960.0, 0.0);

        var (x, y, _) = frame.ToLocal(91850.001, 73960.002, 0.0);

        Assert.Equal((float)(91850.001 - 91850.0), x);
        Assert.Equal((float)(73960.002 - 73960.0), y);
        Assert.NotEqual(0f, x);
    }

    [Fact]
    public void Identity_IsAPlainFloatCast_AndDefaultIsFlatIdentity()
    {
        var (x, y, z) = SceneFrame.Identity.ToLocal(3.5, -4.25, 7.0);
        Assert.Equal((3.5f, -4.25f, 7.0f), (x, y, z));
        Assert.True(SceneFrame.Identity.IsIdentity);
        Assert.True(default(SceneFrame).IsIdentity);
        Assert.False(default(SceneFrame).HasTerrain);
        Assert.True(default(SceneFrame).Terrain.IsFlat);
    }

    [Fact]
    public void GroundToLocal_WithoutTerrain_IsHeightAboveTheFlatDatum()
    {
        var frame = new SceneFrame(10.0, 20.0, 385.0);

        var (x, y, z) = frame.GroundToLocal(15.0, 25.0, 1.5);

        Assert.Equal((5f, 5f, 1.5f), (x, y, z));
    }

    [Fact]
    public void GroundToLocal_WithTerrain_FollowsTheField()
    {
        var net = TestNet.Box;
        var field = TerrainField.FromNetwork(net);
        var frame = new SceneFrame(0.0, 0.0, 0.0, field);

        var (x, y, z) = frame.GroundToLocal(2000.0, 3000.0, 2.0);

        Assert.Equal((float)2000.0, x);
        Assert.Equal((float)3000.0, y);
        Assert.Equal((float)(field.HeightAt(2000.0, 3000.0) + 2.0), z);
    }

    [Fact]
    public void ToWorld_InvertsTheHorizontalMapping()
    {
        var frame = new SceneFrame(1000.0, 2000.0, 0.0);
        var (lx, ly, _) = frame.ToLocal(1234.5, 2345.5, 0.0);

        var (wx, wy) = frame.ToWorld(lx, ly);

        Assert.Equal(1234.5, wx, 3);
        Assert.Equal(2345.5, wy, 3);
    }
}
