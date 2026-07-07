using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// Rung 9b-i: focused unit test for the polyline-polyline intersection helper used to compute
// junction conflict geometry, isolated from any net.xml fixture.
public class PolylineGeometryTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void TryIntersect_TwoPerpendicularSegments_ReturnsCrossingPointAndArcLengths()
    {
        // A: horizontal segment from (0,0) to (10,0).
        var a = new (double X, double Y)[] { (0, 0), (10, 0) };
        // B: vertical segment from (5,-5) to (5,5), crossing A at (5,0).
        var b = new (double X, double Y)[] { (5, -5), (5, 5) };

        var found = PolylineGeometry.TryIntersect(a, b, out var intersection);

        Assert.True(found);
        Assert.Equal(5.0, intersection.ArcA, Tolerance);
        Assert.Equal(5.0, intersection.ArcB, Tolerance);
        Assert.Equal(5.0, intersection.Point.X, Tolerance);
        Assert.Equal(0.0, intersection.Point.Y, Tolerance);
    }

    [Fact]
    public void TryIntersect_SegmentsThatOnlyTouchAtASharedEndpoint_ReturnsFalse()
    {
        // Two segments that meet only at (10,0) -- a merge, not a crossing.
        var a = new (double X, double Y)[] { (0, 0), (10, 0) };
        var b = new (double X, double Y)[] { (10, 0), (10, 10) };

        var found = PolylineGeometry.TryIntersect(a, b, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryIntersect_ParallelSegments_ReturnsFalse()
    {
        var a = new (double X, double Y)[] { (0, 0), (10, 0) };
        var b = new (double X, double Y)[] { (0, 1), (10, 1) };

        var found = PolylineGeometry.TryIntersect(a, b, out _);

        Assert.False(found);
    }
}
