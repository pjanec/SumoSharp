using Sim.Core.Orca;
using Sim.Evac;
using Xunit;

namespace Sim.ParityTests;

// PANIC-EVAC.md R7/R8 / §8.4 unit coverage for Sim.Evac.FakeNavMesh: the axis-aligned bbox derived
// from the grid net's own geometry, expanded by the vicinity width, and the resulting containment /
// clamp behaviour. Uses the same evac-grid fixture as EvacSpineTests.
public class FakeNavMeshTests
{
    private const double VicinityWidth = 8.0;

    private static readonly string NetPath =
        Path.Combine(RepoRoot(), "scenarios", "evac-grid", "net.net.xml");

    private static FakeNavMesh Build()
    {
        var net = Sim.Ingest.NetworkParser.Parse(NetPath);
        return new FakeNavMesh(net, VicinityWidth);
    }

    [Fact]
    public void Bbox_EnclosesGridGeometryPlusMargin()
    {
        var mesh = Build();

        // Grid nodes sit at {60,140,220,300} with 60m boundary stubs, so geometry spans roughly
        // [0,360] on each axis; with an 8m margin the bbox must sit a bit outside that.
        Assert.True(mesh.MinX <= 0.0, $"MinX {mesh.MinX} should be <= 0");
        Assert.True(mesh.MinY <= 0.0, $"MinY {mesh.MinY} should be <= 0");
        Assert.True(mesh.MaxX >= 360.0, $"MaxX {mesh.MaxX} should be >= 360");
        Assert.True(mesh.MaxY >= 360.0, $"MaxY {mesh.MaxY} should be >= 360");

        // The margin is actually applied: recompute the RAW geometry bbox INDEPENDENTLY (from the same
        // net) and assert the mesh bbox sits exactly VicinityWidth outside it -- a falsifiable check
        // against real geometry, not a tautology comparing the mesh bbox to itself.
        var net = Sim.Ingest.NetworkParser.Parse(NetPath);
        double gMinX = double.PositiveInfinity, gMinY = double.PositiveInfinity;
        double gMaxX = double.NegativeInfinity, gMaxY = double.NegativeInfinity;
        void Accumulate(IReadOnlyList<(double X, double Y)> shape)
        {
            foreach (var (x, y) in shape)
            {
                gMinX = Math.Min(gMinX, x);
                gMinY = Math.Min(gMinY, y);
                gMaxX = Math.Max(gMaxX, x);
                gMaxY = Math.Max(gMaxY, y);
            }
        }

        foreach (var lane in net.LanesByHandle) Accumulate(lane.Shape);
        foreach (var junction in net.Junctions) if (junction.Shape.Count > 0) Accumulate(junction.Shape);

        Assert.Equal(gMinX - VicinityWidth, mesh.MinX, 6);
        Assert.Equal(gMinY - VicinityWidth, mesh.MinY, 6);
        Assert.Equal(gMaxX + VicinityWidth, mesh.MaxX, 6);
        Assert.Equal(gMaxY + VicinityWidth, mesh.MaxY, 6);
    }

    [Fact]
    public void BoundaryLoop_HasFourCorners()
    {
        var mesh = Build();
        Assert.Equal(4, mesh.BoundaryLoop.Count);
    }

    [Fact]
    public void Contains_TrueAtCentre_FalseFarOutside()
    {
        var mesh = Build();

        Assert.True(mesh.Contains(new Vec2(180.0, 180.0)));
        Assert.False(mesh.Contains(new Vec2(mesh.MaxX + 50.0, mesh.MaxY + 50.0)));
    }

    [Fact]
    public void ClampInterior_PullsOutsidePointBackInside()
    {
        var mesh = Build();

        var clamped = mesh.ClampInterior(new Vec2(mesh.MaxX + 100.0, mesh.MaxY + 100.0));

        Assert.True(clamped.X < mesh.MaxX);
        Assert.True(clamped.Y < mesh.MaxY);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Traffic.sln not found above test assembly).");
    }
}
