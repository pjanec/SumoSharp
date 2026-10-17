using Xunit;

namespace Sim.Scene.Tests;

public class JunctionMeshBuilderTests
{
    // netconvert emits this junction's outline slightly self-intersecting (a bowtie sliver at the arc's end),
    // so one CW triangle is unavoidable there; see JunctionMeshBuilder's header. Every other box junction is
    // a clean simple polygon.
    private const string SelfIntersectingJunction = "ra_s_r3";

    private static double TriangleSignedArea(SceneMesh mesh, int t)
    {
        var v = mesh.Vertices;
        var a = mesh.Indices[t * 3] * 3;
        var b = mesh.Indices[(t * 3) + 1] * 3;
        var c = mesh.Indices[(t * 3) + 2] * 3;
        return ((((double)v[b] - v[a]) * ((double)v[c + 1] - v[a + 1]))
              - (((double)v[b + 1] - v[a + 1]) * ((double)v[c] - v[a]))) * 0.5;
    }

    private static double Shoelace(IReadOnlyList<(double X, double Y)> p)
    {
        // Drop a closing duplicate point, then |signed area|.
        var n = p.Count;
        if (n > 1 && p[0] == p[n - 1])
        {
            n--;
        }

        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % n];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(sum) * 0.5;
    }

    // Planar area of the emitted triangles, from the float vertices (signed: a flipped triangle would
    // subtract, so this also proves every triangle is CCW from above).
    private static double SignedMeshArea(SceneMesh mesh)
    {
        var v = mesh.Vertices;
        var sum = 0.0;
        for (var t = 0; t < mesh.Indices.Length; t += 3)
        {
            var a = mesh.Indices[t] * 3;
            var b = mesh.Indices[t + 1] * 3;
            var c = mesh.Indices[t + 2] * 3;
            sum += (((double)v[b] - v[a]) * ((double)v[c + 1] - v[a + 1]))
                 - (((double)v[b + 1] - v[a + 1]) * ((double)v[c] - v[a]));
        }

        return sum * 0.5;
    }

    [Fact]
    public void BoxJunctions_TrianglesCoverExactlyThePolygonArea()
    {
        var net = TestNet.Box;
        var checkedCount = 0;
        var degenerate = 0;
        foreach (var junction in net.Junctions)
        {
            if (junction.Shape.Count < 3)
            {
                continue;
            }

            var expected = Shoelace(junction.Shape);
            // A frame centred on the junction keeps its floats small, so the float-vertex area check is meaningful.
            var cx = junction.Shape.Average(p => p.X);
            var cy = junction.Shape.Average(p => p.Y);
            var mesh = JunctionMeshBuilder.Build(new SceneFrame(cx, cy, 0.0), junction.Shape);

            if (expected < 1e-9)
            {
                degenerate++;
                Assert.True(mesh.IsEmpty, $"{junction.Id}: zero-area polygon must produce no triangles");
                continue;
            }

            Assert.False(mesh.IsEmpty, junction.Id);
            Assert.True(Math.Abs(mesh.Area - expected) / expected <= 1e-6,
                $"{junction.Id}: mesh area {mesh.Area} vs polygon {expected}; shape={string.Join(" ", junction.Shape)}");
            Assert.InRange(Math.Abs(SignedMeshArea(mesh) - expected) / expected, 0.0, 1e-5);
            Assert.All(mesh.Indices, i => Assert.InRange(i, 0, mesh.VertexCount - 1));
            if (junction.Id != SelfIntersectingJunction)
            {
                Assert.All(Enumerable.Range(0, mesh.TriangleCount), t => Assert.True(TriangleSignedArea(mesh, t) > 0.0, $"{junction.Id}: triangle {t} is not CCW"));
            }

            checkedCount++;
        }

        Assert.True(checkedCount > 100, $"expected many box junctions with a polygon, checked {checkedCount}");
        Assert.True(degenerate > 0, "the box net has zero-area junction outlines (3 points, A-B-A spur); they must be skipped, not crash");
    }

    [Fact]
    public void Winding_DoesNotMatter_AndAClosingDuplicateIsDropped()
    {
        var ccw = new (double X, double Y)[] { (0, 0), (4, 0), (4, 3), (0, 3) };
        var cw = ccw.Reverse().ToArray();
        var closed = ccw.Append(ccw[0]).ToArray();

        foreach (var poly in new[] { ccw, cw, closed })
        {
            var mesh = JunctionMeshBuilder.Build(SceneFrame.Identity, poly);
            Assert.Equal(2, mesh.TriangleCount);
            Assert.Equal(12.0, mesh.Area, 9);
            Assert.Equal(12.0, SignedMeshArea(mesh), 9);
        }
    }

    [Fact]
    public void ConcaveLShape_IsTriangulatedWithoutCoveringTheNotch()
    {
        // L: 4x4 square minus its upper-right 2x2 quadrant = 12 m^2.
        var l = new (double X, double Y)[] { (0, 0), (4, 0), (4, 2), (2, 2), (2, 4), (0, 4) };

        var mesh = JunctionMeshBuilder.Build(SceneFrame.Identity, l);

        Assert.Equal(4, mesh.TriangleCount);
        Assert.Equal(12.0, mesh.Area, 9);
        Assert.Equal(12.0, SignedMeshArea(mesh), 9);
    }

    [Fact]
    public void DegenerateInputs_AreSkipped()
    {
        Assert.True(JunctionMeshBuilder.Build(SceneFrame.Identity, Array.Empty<(double X, double Y)>()).IsEmpty);
        Assert.True(JunctionMeshBuilder.Build(SceneFrame.Identity, new (double X, double Y)[] { (0, 0), (1, 1) }).IsEmpty);
        // Three points but collinear: no area.
        Assert.True(JunctionMeshBuilder.Build(SceneFrame.Identity, new (double X, double Y)[] { (0, 0), (1, 1), (2, 2) }).IsEmpty);
        // "A -> B -> A" spur, as netconvert emits for some internal junctions.
        Assert.True(JunctionMeshBuilder.Build(SceneFrame.Identity, new (double X, double Y)[] { (0, 0), (5, 2), (0, 0) }).IsEmpty);
    }

    [Fact]
    public void Z_FollowsTheFramesGround()
    {
        var poly = new (double X, double Y)[] { (0, 0), (4, 0), (4, 3), (0, 3) };

        var mesh = JunctionMeshBuilder.Build(new SceneFrame(0, 0, 100.0), poly);

        // Flat datum: ground == OriginZ, 0 above ground -> local z 0.
        for (var i = 2; i < mesh.Vertices.Length; i += 3)
        {
            Assert.Equal(0f, mesh.Vertices[i]);
        }
    }
}
