using System;
using System.Collections.Generic;
using System.Linq;
using CityLib;
using Xunit;
using Xunit.Abstractions;

namespace CityLib.Tests;

// docs/EXTERNAL-NET-VIEWER-DESIGN.md §7.2.2: the grey reference grid, baked over the net's extent and
// draped over the TerrainField instead of being a flat mesh translated under the camera.
public class GroundGridBuilderTests
{
    private readonly ITestOutputHelper _output;

    public GroundGridBuilderTests(ITestOutputHelper output) => _output = output;

    // The same east-west ramp TerrainFieldTests uses: 100 -> 200 m over 1 km.
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

    private static IEnumerable<float> YsOf(GroundGridBuilder.GridMesh mesh)
    {
        for (var i = 1; i < mesh.Vertices.Length; i += 3)
        {
            yield return mesh.Vertices[i];
        }
    }

    // ---- flat frame: planar, and at the documented offset --------------------------------------------

    [Fact]
    public void OnAFlatFrame_EveryGridVertexSitsAtTheSameY()
    {
        var mesh = GroundGridBuilder.Build(SumoGodotFrame.Identity, 0.0, 0.0, 500.0, 500.0);

        Assert.True(mesh.SegmentCount > 0);
        Assert.All(YsOf(mesh), y => Assert.Equal(GroundGridBuilder.GroundOffsetSumoZ, (double)y, 5));
    }

    [Fact]
    public void OnAFlatFrame_TheGridSitsBelowTheZoneTintAndTheRoads()
    {
        // The reason the offset exists: the grid must lose the depth test to both.
        Assert.True(GroundGridBuilder.GroundOffsetSumoZ < -0.05);
        Assert.True(GroundGridBuilder.GroundOffsetSumoZ < 0.0);
    }

    [Fact]
    public void OnASmallExtent_TheSpacingIsTheTwentyFiveMetreMinimum()
    {
        var mesh = GroundGridBuilder.Build(SumoGodotFrame.Identity, 0.0, 0.0, 500.0, 500.0);
        Assert.Equal(GroundGridBuilder.MinSpacingMeters, mesh.Spacing, 9);
    }

    [Fact]
    public void OnAHugeExtent_TheSpacingGrowsSoTheLineCountStaysBounded()
    {
        // 100 km at 25 m would be 4000 lines per axis. The cap grows the spacing instead.
        var mesh = GroundGridBuilder.Build(SumoGodotFrame.Identity, 0.0, 0.0, 100000.0, 100000.0);

        Assert.True(mesh.Spacing > GroundGridBuilder.MinSpacingMeters, $"spacing {mesh.Spacing}");
        Assert.True(mesh.LineCount <= (2 * GroundGridBuilder.MaxLinesPerAxis) + 4,
            $"line count {mesh.LineCount} should stay near 2x the per-axis cap");
        _output.WriteLine($"100 km extent: spacing {mesh.Spacing:F1} m, {mesh.LineCount} lines, {mesh.SegmentCount} segments");
    }

    [Fact]
    public void LinePositionsAreSnappedToTheSpacing_SoARebakeLinesUp()
    {
        // Baking over two different (overlapping) extents must put the shared lines in the same place --
        // otherwise a re-bake on a different crop visibly shifts the whole floor.
        var a = GroundGridBuilder.Build(SumoGodotFrame.Identity, 0.0, 0.0, 500.0, 500.0);
        var b = GroundGridBuilder.Build(SumoGodotFrame.Identity, -137.0, -137.0, 500.0, 500.0);

        Assert.Equal(a.Spacing, b.Spacing, 9);

        static HashSet<int> XLines(GroundGridBuilder.GridMesh m, double spacing)
        {
            var set = new HashSet<int>();
            for (var i = 0; i < m.Vertices.Length; i += 3)
            {
                set.Add((int)Math.Round(m.Vertices[i] / spacing));
            }

            return set;
        }

        var shared = XLines(a, a.Spacing);
        shared.IntersectWith(XLines(b, b.Spacing));
        Assert.True(shared.Count >= 20, $"expected the two bakes to share most lines; shared {shared.Count}");
    }

    // ---- terrain frame: the point of the whole exercise ----------------------------------------------

    [Fact]
    public void OnATerrainFrame_TheGridIsNotPlanar()
    {
        var frame = new SumoGodotFrame(500.0, 0.0, 150.0, RampField());
        var mesh = GroundGridBuilder.Build(frame, 0.0, -200.0, 1000.0, 200.0);

        var ys = YsOf(mesh).ToArray();
        var spread = ys.Max() - ys.Min();

        _output.WriteLine($"ramp grid: {mesh.SegmentCount} segments, Y spread {spread:F2} m");
        Assert.True(spread > 80f, $"grid should follow the ~100 m of ramp relief; spread {spread:F2} m");
    }

    [Fact]
    public void OnATerrainFrame_EveryGridVertexSitsOnTheField()
    {
        // Not merely "varies" -- each vertex must be exactly the field's height at that point, offset by
        // the documented ground offset. This is what "draped over the terrain" has to mean.
        var field = RampField();
        var frame = new SumoGodotFrame(500.0, 0.0, 150.0, field);
        var mesh = GroundGridBuilder.Build(frame, 0.0, -200.0, 1000.0, 200.0);

        var worst = 0.0;
        var n = mesh.Vertices.Length / 3;
        for (var i = 0; i < n; i++)
        {
            var b = i * 3;
            var gx = mesh.Vertices[b];
            var gy = mesh.Vertices[b + 1];
            var gz = mesh.Vertices[b + 2];

            var (sx, sy) = frame.ToSumo(gx, gz);
            var expected = (float)(field.HeightAt(sx, sy) - frame.OriginZ + GroundGridBuilder.GroundOffsetSumoZ);
            worst = Math.Max(worst, Math.Abs(gy - expected));
        }

        Assert.True(worst < 1e-3, $"grid vertex off the field by {worst:F6} m");
        _output.WriteLine($"drape check: {n} vertices, worst |gridY - fieldY| = {worst:E2} m");
    }

    [Fact]
    public void OnATerrainFrame_LinesAreSubdividedRatherThanBeingSingleSegments()
    {
        var frame = new SumoGodotFrame(500.0, 0.0, 150.0, RampField());
        var mesh = GroundGridBuilder.Build(frame, 0.0, -200.0, 1000.0, 200.0);

        // A single segment per line would make it a chord across the terrain, which is the bug.
        Assert.True(mesh.SegmentCount > mesh.LineCount * 4,
            $"{mesh.SegmentCount} segments across {mesh.LineCount} lines is not a subdivision");
    }

    [Fact]
    public void OnAFlatFrame_EachLineIsASingleSegment()
    {
        // The converse: subdividing a flat grid would just cost vertices for an identical surface.
        var mesh = GroundGridBuilder.Build(SumoGodotFrame.Identity, 0.0, 0.0, 500.0, 500.0);
        Assert.Equal(mesh.LineCount, mesh.SegmentCount);
    }

    [Fact]
    public void ADegenerateExtent_StillProducesAGrid_AndDoesNotHang()
    {
        // Reversed and zero-size rectangles are both reachable from a degenerate scene bbox.
        var reversed = GroundGridBuilder.Build(SumoGodotFrame.Identity, 500.0, 500.0, 0.0, 0.0);
        Assert.True(reversed.SegmentCount > 0);

        var point = GroundGridBuilder.Build(SumoGodotFrame.Identity, 10.0, 10.0, 10.0, 10.0);
        Assert.True(point.SegmentCount > 0);
        Assert.Equal(GroundGridBuilder.MinSpacingMeters, point.Spacing, 9);
    }
}
