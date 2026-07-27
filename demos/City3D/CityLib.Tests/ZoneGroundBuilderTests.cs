using System;
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

        var mesh = ZoneGroundBuilder.Build(SumoGodotFrame.Identity, polygon);

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

        var mesh = ZoneGroundBuilder.Build(SumoGodotFrame.Identity, polygon);

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

        var mesh = ZoneGroundBuilder.Build(SumoGodotFrame.Identity, polygon);

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

        var mesh = ZoneGroundBuilder.Build(SumoGodotFrame.Identity, polygon);

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
        var mesh = ZoneGroundBuilder.Build(SumoGodotFrame.Identity, new (double X, double Y)[] { (0, 0), (1, 1) });

        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.Indices);
        Assert.Equal(0.0, mesh.Area);
    }

    // ---- 6: terrain (docs/EXTERNAL-NET-VIEWER-DESIGN.md §7.2.3) --------------------------------------
    //
    // A district polygon has a handful of vertices, so sampling the field only at those makes every fan
    // triangle a plane through three terrain points -- over a big district on a slope the middle of the
    // tint ends up under the road. The fix is midpoint subdivision down to the field's cell size.

    // The east-west ramp the terrain tests use: 100 -> 200 m over 1 km.
    private static TerrainField RampField()
    {
        var shape = new List<(double X, double Y)>();
        var zs = new List<double>();
        for (var i = 0; i <= 10; i++)
        {
            shape.Add((i * 100.0, 0.0));
            zs.Add(100.0 + (i * 10.0));
        }

        return TerrainField.FromLaneGeometry(
            new[] { ((IReadOnlyList<(double X, double Y)>)shape, (IReadOnlyList<double>?)zs) },
            cellSize: 50.0);
    }

    private static readonly (double X, double Y)[] BigDistrict =
        { (0, -200), (1000, -200), (1000, 200), (0, 200) };

    [Fact]
    public void Build_OnATerrainFrame_SubdividesAndFollowsTheField()
    {
        var field = RampField();
        var frame = new SumoGodotFrame(500.0, 0.0, 150.0, field);

        var mesh = ZoneGroundBuilder.Build(frame, BigDistrict);

        // Far more than the 4 vertices / 2 triangles the flat fan would produce.
        Assert.True(mesh.Vertices.Length / 3 > 100, $"expected subdivision; got {mesh.Vertices.Length / 3} vertices");
        Assert.True(mesh.Indices.Length / 3 > 100, $"expected subdivision; got {mesh.Indices.Length / 3} triangles");

        // EVERY vertex -- interior midpoints included -- sits exactly on the field.
        var worst = 0.0;
        for (var i = 0; i < mesh.Vertices.Length; i += 3)
        {
            var (sx, sy) = frame.ToSumo(mesh.Vertices[i], mesh.Vertices[i + 2]);
            var expected = (float)(field.HeightAt(sx, sy) - frame.OriginZ - 0.05);
            worst = Math.Max(worst, Math.Abs(mesh.Vertices[i + 1] - expected));
        }

        Assert.True(worst < 1e-3, $"zone vertex off the field by {worst:F6} m");
    }

    [Fact]
    public void Build_OnATerrainFrame_LeavesTheAreaSortKeyAlone()
    {
        // Area is the largest-area-first draw-order key. Subdivision must not touch it, or zones start
        // painting over each other in a different order.
        var frame = new SumoGodotFrame(500.0, 0.0, 150.0, RampField());

        var flat = ZoneGroundBuilder.Build(SumoGodotFrame.Identity, BigDistrict);
        var draped = ZoneGroundBuilder.Build(frame, BigDistrict);

        Assert.Equal(1000.0 * 400.0, draped.Area, 1e-6);
        Assert.Equal(flat.Area, draped.Area, 1e-9);
    }

    [Fact]
    public void Build_OnATerrainFrame_TheInteriorIsNotAChordAcrossTheSlope()
    {
        // THE failure this closes. Without subdivision the mesh's interior is a plane through the corner
        // heights; the mid-district vertex would be at the mean of the ends rather than on the ramp. Here
        // the mesh must contain a vertex at (500, y) whose height is the field's, not the corner mean.
        var field = RampField();
        var frame = new SumoGodotFrame(500.0, 0.0, 150.0, field);
        var mesh = ZoneGroundBuilder.Build(frame, BigDistrict);

        var found = false;
        for (var i = 0; i < mesh.Vertices.Length; i += 3)
        {
            var (sx, sy) = frame.ToSumo(mesh.Vertices[i], mesh.Vertices[i + 2]);
            if (Math.Abs(sx - 250.0) < 1.0 && Math.Abs(sy) < 1.0)
            {
                found = true;
                var expected = (float)(field.HeightAt(250.0, 0.0) - 150.0 - 0.05);
                Assert.Equal((double)expected, (double)mesh.Vertices[i + 1], 3);
            }
        }

        Assert.True(found, "expected the subdivision to generate an interior vertex at SUMO (250, 0)");
    }

    [Fact]
    public void Build_OnAFlatFrame_IsUnchangedByTheTerrainWork()
    {
        // The 2-D regression: a frame with no field takes the original fan path, vertex for vertex.
        var mesh = ZoneGroundBuilder.Build(SumoGodotFrame.Identity, BigDistrict);

        Assert.Equal(4 * 3, mesh.Vertices.Length);
        Assert.Equal(2 * 3, mesh.Indices.Length);
        for (var i = 1; i < mesh.Vertices.Length; i += 3)
        {
            Assert.Equal(-0.05, (double)mesh.Vertices[i], 5);
        }
    }

    [Fact]
    public void Build_SubdivisionIsBounded_ByMaxSubdivisionDepth()
    {
        // A pathologically large polygon against a fine cell must not blow up: depth 5 caps each fan
        // triangle at 4^5 = 1024 sub-triangles.
        var field = RampField();
        var frame = new SumoGodotFrame(0.0, 0.0, 150.0, field);
        var huge = new (double X, double Y)[] { (0, 0), (100000, 0), (100000, 100000), (0, 100000) };

        var mesh = ZoneGroundBuilder.Build(frame, huge);

        var triangles = mesh.Indices.Length / 3;
        var cap = 2 * (int)Math.Pow(4, ZoneGroundBuilder.MaxSubdivisionDepth); // 2 fan triangles
        Assert.True(triangles <= cap, $"{triangles} triangles exceeds the depth cap of {cap}");
    }

    [Fact]
    public void Build_OnATerrainFrame_SharesVerticesAcrossSplitEdges()
    {
        // Sibling sub-triangles must reference the SAME vertex on a shared edge, or the tint shows
        // hairline cracks where two rounded copies land a ULP apart.
        var frame = new SumoGodotFrame(500.0, 0.0, 150.0, RampField());
        var mesh = ZoneGroundBuilder.Build(frame, BigDistrict);

        var vertexCount = mesh.Vertices.Length / 3;
        var referenced = mesh.Indices.Length;

        Assert.True(referenced > vertexCount * 2,
            $"{referenced} index slots over {vertexCount} vertices suggests no sharing at all");
        Assert.All(mesh.Indices, i => Assert.InRange(i, 0, vertexCount - 1));
    }
}
