using Xunit;

namespace Sim.Scene.Tests;

public class AmbientGroundBuilderTests
{
    // A 3-D ramp net: z = 0.1 * x along x in [0, 200], y in [0, 100].
    private static TerrainField Ramp(double cell = 25.0)
    {
        var lanes = new List<(IReadOnlyList<(double X, double Y)> Shape, IReadOnlyList<double>? ShapeZ)>();
        foreach (var y in new[] { 0.0, 50.0, 100.0 })
        {
            var shape = new (double X, double Y)[] { (0, y), (100, y), (200, y) };
            lanes.Add((shape, new[] { 0.0, 10.0, 20.0 }));
        }

        return TerrainField.FromLaneGeometry(lanes, cell);
    }

    private static (double Cx, double Cy)[] QuadCentres(SceneMesh mesh)
    {
        var quads = mesh.VertexCount / 4;
        var centres = new (double, double)[quads];
        for (var q = 0; q < quads; q++)
        {
            double sx = 0, sy = 0;
            for (var k = 0; k < 4; k++)
            {
                sx += mesh.Vertices[((q * 4) + k) * 3];
                sy += mesh.Vertices[(((q * 4) + k) * 3) + 1];
            }

            centres[q] = (sx / 4, sy / 4);
        }

        return centres;
    }

    [Fact]
    public void OneQuadPerLatticeCell_TwoTrianglesEach_HeightsFromTheField()
    {
        var field = Ramp();
        var frame = new SceneFrame(0, 0, 0, field);

        var mesh = AmbientGroundBuilder.Build(frame, -1e9, -1e9, 1e9, 1e9);

        var cells = (field.CountX - 1) * (field.CountY - 1);
        Assert.Equal(cells * 4, mesh.VertexCount);
        Assert.Equal(cells * 2, mesh.TriangleCount);
        Assert.Equal(cells * field.CellSize * field.CellSize, mesh.Area, 6);
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var x = mesh.Vertices[v * 3];
            var y = mesh.Vertices[(v * 3) + 1];
            Assert.Equal(field.HeightAt(x, y), (double)mesh.Vertices[(v * 3) + 2], 3);
            Assert.True(mesh.Normals[(v * 3) + 2] > 0f, "normals point up (+z)");
        }

        // CCW from above.
        for (var t = 0; t < mesh.Indices.Length; t += 3)
        {
            var a = mesh.Indices[t] * 3;
            var b = mesh.Indices[t + 1] * 3;
            var c = mesh.Indices[t + 2] * 3;
            var cross = ((mesh.Vertices[b] - mesh.Vertices[a]) * (mesh.Vertices[c + 1] - mesh.Vertices[a + 1]))
                      - ((mesh.Vertices[b + 1] - mesh.Vertices[a + 1]) * (mesh.Vertices[c] - mesh.Vertices[a]));
            Assert.True(cross > 0f);
        }
    }

    [Fact]
    public void ExcludeCell_DropsExactlyTheRequestedCells()
    {
        var field = Ramp();
        var frame = new SceneFrame(0, 0, 0, field);
        var all = AmbientGroundBuilder.Build(frame, -1e9, -1e9, 1e9, 1e9);
        var allCentres = QuadCentres(all);

        // Drop every cell whose centre is in the western half.
        const double Cut = 100.0;
        var asked = new List<(double, double)>();
        var mesh = AmbientGroundBuilder.Build(
            frame, -1e9, -1e9, 1e9, 1e9,
            excludeCell: (cx, cy) =>
            {
                asked.Add((cx, cy));
                return cx < Cut;
            });

        var expectedKept = allCentres.Where(c => c.Cx >= Cut).ToArray();
        var kept = QuadCentres(mesh);
        Assert.Equal(allCentres.Length, asked.Count); // asked once per candidate cell
        Assert.True(kept.Length > 0 && kept.Length < allCentres.Length);
        Assert.Equal(expectedKept.Length, kept.Length);
        Assert.All(kept, c => Assert.True(c.Cx >= Cut));
        // The kept set is precisely the unexcluded subset, in the same order.
        for (var i = 0; i < kept.Length; i++)
        {
            Assert.Equal(expectedKept[i].Cx, kept[i].Cx, 3);
            Assert.Equal(expectedKept[i].Cy, kept[i].Cy, 3);
        }

        // Excluding everything -> empty; excluding nothing -> all.
        Assert.True(AmbientGroundBuilder.Build(frame, -1e9, -1e9, 1e9, 1e9, (_, _) => true).IsEmpty);
        Assert.Equal(all.Vertices, AmbientGroundBuilder.Build(frame, -1e9, -1e9, 1e9, 1e9, (_, _) => false).Vertices);
    }

    [Fact]
    public void RectangleSelectsCellsByCentre()
    {
        var field = Ramp();
        var frame = new SceneFrame(0, 0, 0, field);

        var mesh = AmbientGroundBuilder.Build(frame, 0, 0, 60, 60);

        var centres = QuadCentres(mesh);
        Assert.NotEmpty(centres);
        Assert.All(centres, c =>
        {
            Assert.InRange(c.Cx, 0, 60);
            Assert.InRange(c.Cy, 0, 60);
        });
    }

    [Fact]
    public void HeightOverride_ReplacesVertexHeights_AndSeesTheFieldHeight()
    {
        var field = Ramp();
        var frame = new SceneFrame(0, 0, 0, field);
        var seen = new List<(double X, double Y, double H)>();

        var plain = AmbientGroundBuilder.Build(frame, -1e9, -1e9, 1e9, 1e9);
        var mesh = AmbientGroundBuilder.Build(
            frame, -1e9, -1e9, 1e9, 1e9,
            heightOverride: (x, y, h) =>
            {
                seen.Add((x, y, h));
                return x < 50.0 ? -7.5 : h;
            });

        Assert.Equal(plain.VertexCount, mesh.VertexCount);
        Assert.All(seen, s => Assert.Equal(field.HeightAt(s.X, s.Y), s.H));
        var overridden = 0;
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var x = mesh.Vertices[v * 3];
            if (x < 50f)
            {
                Assert.Equal(-7.5f, mesh.Vertices[(v * 3) + 2]);
                overridden++;
            }
            else
            {
                Assert.Equal(plain.Vertices[(v * 3) + 2], mesh.Vertices[(v * 3) + 2]);
            }
        }

        Assert.True(overridden > 0);
    }

    [Fact]
    public void FlatField_SynthesisesALattice_AtTheFlatHeight()
    {
        var frame = new SceneFrame(0, 0, 12.0); // no terrain: flat at OriginZ

        var mesh = AmbientGroundBuilder.Build(frame, 0, 0, 100, 80);

        Assert.False(mesh.IsEmpty);
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            Assert.Equal(0f, mesh.Vertices[(v * 3) + 2]); // flat height 12 - origin 12
        }

        Assert.Equal(Math.Ceiling(100 / TerrainField.DefaultCellSizeMeters) * Math.Ceiling(80 / TerrainField.DefaultCellSizeMeters),
            mesh.VertexCount / 4);
    }
}
