using System.Collections.Generic;
using System.Linq;
using CityLib;
using Xunit;

namespace CityLib.Tests;

// docs/LIVE-CITY-VISUALS-NOTES.md deliverable 2 ("Zones layer"): CityLib.ZoneGroundBuilder's pure
// polygon -> flat-mesh math -- fan triangulation, the SUMO->Godot ground-offset elevation, and the
// planar-area calc callers sort largest-area-first by.
public class ZoneGroundBuilderTests
{
    // ---- 1: a square zone -- vertex count, triangle count, and area match a plain rectangle. ----
    [Fact]
    public void Build_SquareZone_ProducesTwoTrianglesAndCorrectArea()
    {
        var polygon = new (double X, double Y)[] { (0, 0), (100, 0), (100, 100), (0, 100) };

        var mesh = ZoneGroundBuilder.Build(polygon);

        Assert.Equal(4 * 3, mesh.Vertices.Length);   // 4 vertices, xyz each
        Assert.Equal(2 * 3, mesh.Indices.Length);     // fan triangulation of a quad = 2 triangles
        Assert.Equal(100.0 * 100.0, mesh.Area, 1e-6);
    }

    // ---- 2: the committed zone_downtown polygon (a real demo_city/box rectangle) -- exact area. ----
    [Fact]
    public void Build_DowntownZonePolygon_AreaMatchesShoelaceByHand()
    {
        var polygon = new (double X, double Y)[]
        {
            (1600.0, 1600.0), (3100.0, 1600.0), (3100.0, 3100.0), (1600.0, 3100.0),
        };

        var mesh = ZoneGroundBuilder.Build(polygon);

        // 1500m x 1500m square.
        Assert.Equal(1500.0 * 1500.0, mesh.Area, 1e-6);
    }

    // ---- 3: the committed zone_arterial octagon (8 points, includes collinear mid-edge points) --
    // fan triangulation still produces the correct planar area (a convex ring is exact regardless of the
    // extra collinear vertices; those just contribute degenerate zero-area triangles). ----
    [Fact]
    public void Build_ArterialRingPolygon_ProducesNonDegenerateArea()
    {
        var polygon = new (double X, double Y)[]
        {
            (250.0, 250.0), (2350.0, 250.0), (4450.0, 250.0), (4450.0, 2350.0),
            (4450.0, 4450.0), (2350.0, 4450.0), (250.0, 4450.0), (250.0, 2350.0),
        };

        var mesh = ZoneGroundBuilder.Build(polygon);

        Assert.Equal(6 * 3, mesh.Indices.Length); // 8-gon fan = 6 triangles
        // The polygon is exactly the 4200x4200 square (250..4450 on each axis) with 4 extra collinear
        // mid-edge points -- planar area must equal the plain square's area.
        Assert.Equal(4200.0 * 4200.0, mesh.Area, 1e-3);
    }

    // ---- 4: Godot-space mapping -- SumoToGodot(x,y,z) = (x, z, -y); the default ground offset sits
    // BELOW the road surface (z=0), i.e. every emitted vertex's Godot Y is negative. ----
    [Fact]
    public void Build_DefaultGroundOffset_SitsBelowRoadSurface()
    {
        var polygon = new (double X, double Y)[] { (0, 0), (10, 0), (10, 10), (0, 10) };

        var mesh = ZoneGroundBuilder.Build(polygon);

        for (var i = 0; i < mesh.Vertices.Length; i += 3)
        {
            Assert.True(mesh.Vertices[i + 1] < 0f, $"expected Godot Y < 0 (below road surface), got {mesh.Vertices[i + 1]}");
        }

        // And the X/Z mapping is exactly CoordinateTransform.SumoToGodot's (x, z, -y).
        Assert.Equal(0f, mesh.Vertices[0], 1e-6f);   // vertex 0 = sumo (0,0) -> godot X=0
        Assert.Equal(0f, mesh.Vertices[2], 1e-6f);   // godot Z = -sumo.Y = -0 = 0
        Assert.Equal(10f, mesh.Vertices[3], 1e-6f);  // vertex 1 = sumo (10,0) -> godot X=10
        Assert.Equal(0f, mesh.Vertices[5], 1e-6f);   // godot Z = -0 = 0
        Assert.Equal(-10f, mesh.Vertices[8], 1e-6f); // vertex 2 = sumo (10,10) -> godot Z=-10
    }

    // ---- 5: a degenerate (<3-point) polygon yields an empty mesh, never throws. ----
    [Fact]
    public void Build_DegeneratePolygon_ReturnsEmptyMesh()
    {
        var mesh = ZoneGroundBuilder.Build(new (double X, double Y)[] { (0, 0), (1, 1) });

        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.Indices);
        Assert.Equal(0.0, mesh.Area);
    }
}
