using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// Guards the vehicle-OBB angle convention -- docs/NEED-obb-anchor-halflength.md.
//
// THIS TEST EXISTS BECAUSE THE OBVIOUS TEST DOES NOT WORK. The previous convention,
// `forward = (-sin th, cos th)`, was "validated on veh80 (angle=90 runs along world X)" and passed --
// because 90 deg is a DEGENERATE case where the wrong basis and the right one give the same axis (up to a
// sign, which a symmetric box ignores). The error only shows on non-axis-aligned headings, where it is a
// REFLECTION: at 45 deg the wrong axis is PERPENDICULAR to the truth.
//
// So this test does NOT restate the formula (that would be self-fulfilling). It DERIVES the true tangent
// from LaneGeometry by finite difference -- walking two nearby arc-length offsets along a real curved lane
// and differencing the returned points -- and asserts VehicleObb.Basis reproduces it. LaneGeometry is the
// component that OWNS the convention, so this is an independent cross-check, and it fails loudly if either
// side's convention ever drifts.
public class VehicleObbConventionTests
{
    // Straight-line sanity: for a segment pointing in a known compass direction, the basis must point the
    // same way. Written as (dx, dy) -> expected forward, so the naviDegree round-trip is exercised end to end.
    [Theory]
    [InlineData(1.0, 0.0, 1.0, 0.0)]      // east
    [InlineData(0.0, 1.0, 0.0, 1.0)]      // north
    [InlineData(-1.0, 0.0, -1.0, 0.0)]    // west
    [InlineData(0.0, -1.0, 0.0, -1.0)]    // south
    [InlineData(1.0, 1.0, 0.70710678, 0.70710678)]    // NE -- the case the old convention got PERPENDICULAR
    [InlineData(1.0, -1.0, 0.70710678, -0.70710678)]  // SE
    [InlineData(-1.0, 1.0, -0.70710678, 0.70710678)]  // NW
    public void Basis_ForwardMatchesTheSegmentDirection(double dx, double dy, double expFx, double expFy)
    {
        // A two-point lane shape pointing in (dx, dy); ask LaneGeometry for the heading it assigns.
        var len = Math.Sqrt(dx * dx + dy * dy);
        var shape = new[] { (0.0, 0.0), (dx * 100.0 / len, dy * 100.0 / len) };
        var (_, _, angleDeg) = LaneGeometry.PositionAtOffset(shape, 50.0);

        var (forward, right) = VehicleObb.Basis(angleDeg);

        Assert.Equal(expFx, forward.X, 6);
        Assert.Equal(expFy, forward.Y, 6);

        // Right must be a unit vector perpendicular to forward.
        Assert.Equal(0.0, forward.X * right.X + forward.Y * right.Y, 9);
        Assert.Equal(1.0, Math.Sqrt(right.X * right.X + right.Y * right.Y), 9);
    }

    // THE LOAD-BEARING CHECK: derive the tangent from LaneGeometry itself, on a REAL CURVED junction lane,
    // and require the basis to match it. Curved internal lanes are where the old convention failed, and a
    // finite difference of PositionAtOffset is an independent source of truth for "which way is forward".
    [Fact]
    public void Basis_MatchesFiniteDifferenceTangent_OnRealCurvedInternalLanes()
    {
        var net = NetworkParser.Parse(
            Path.Combine(RepoRoot(), "scenarios", "_ped", "demo_city", "box", "net.xml"));

        var checkedLanes = 0;
        var worstDot = 1.0;
        string worstLane = "(none)";
        double worstAt = 0;

        foreach (var lane in net.LanesByHandle)
        {
            // Internal ':' lanes only -- these are the curved ones, and the ones F3 measures.
            if (lane.Id.Length == 0 || lane.Id[0] != ':' || lane.Length < 2.0 || lane.Shape.Count < 2)
            {
                continue;
            }

            checkedLanes++;

            // Sample along the lane, skipping the very ends so the finite difference stays on one segment
            // where possible; vertices are fine too since we compare against the SAME derivative.
            for (var frac = 0.15; frac <= 0.85; frac += 0.10)
            {
                var s = lane.Length * frac;
                const double h = 1e-3;

                var (x0, y0, a0) = LaneGeometry.PositionAtOffset(lane.Shape, s - h);
                var (x1, y1, a1) = LaneGeometry.PositionAtOffset(lane.Shape, s + h);
                var (_, _, angleDeg) = LaneGeometry.PositionAtOffset(lane.Shape, s);

                // Skip samples that STRADDLE A POLYLINE VERTEX: there the finite difference averages two
                // segment directions while PositionAtOffset reports one of them, so they legitimately
                // disagree (measured up to ~10 deg on a tight roundabout lane). That is a corner artefact,
                // not a convention error -- a reflected basis scores ~0, nowhere near this. Detect it by
                // requiring the two probe angles to agree, i.e. both samples sit on the same segment.
                var angleSpread = Math.Abs(NormalizeSignedDegrees(a1 - a0));
                if (angleSpread > 1e-6)
                {
                    continue;
                }

                var dx = x1 - x0;
                var dy = y1 - y0;
                var norm = Math.Sqrt(dx * dx + dy * dy);
                if (norm < 1e-9)
                {
                    continue;
                }

                var (forward, _) = VehicleObb.Basis(angleDeg);
                var dot = forward.X * (dx / norm) + forward.Y * (dy / norm);

                if (dot < worstDot)
                {
                    worstDot = dot;
                    worstLane = lane.Id;
                    worstAt = s;
                }
            }
        }

        Assert.True(checkedLanes > 50, $"expected many internal lanes to check, found {checkedLanes}");

        // dot == +1 means the basis points along the direction of travel. Anything materially below that is
        // a convention error. The OLD basis scores ~0 here (perpendicular) on diagonal lanes.
        Assert.True(
            worstDot > 0.999,
            $"VehicleObb.Basis forward disagrees with LaneGeometry's own tangent: worst dot={worstDot:F6} on "
            + $"lane [{worstLane}] at s={worstAt:F2} over {checkedLanes} internal lanes. The basis must satisfy "
            + "forward = (+sin th, cos th) for naviDegree th (LaneGeometry.cs:59-60). A value near 0 means the "
            + "reflected form (-sin th, cos th) has come back. See docs/NEED-obb-anchor-halflength.md.");
    }

    // The reflected basis must actually FAIL this check -- otherwise the guard above proves nothing.
    // This pins the bug itself, so the test cannot silently become vacuous.
    [Fact]
    public void ReflectedBasis_IsPerpendicularAt45Degrees_SoTheGuardIsNonVacuous()
    {
        const double angleDeg = 45.0; // naviDegree 45 == north-east
        var (correct, _) = VehicleObb.Basis(angleDeg);

        var th = angleDeg * Math.PI / 180.0;
        var reflected = (X: -Math.Sin(th), Y: Math.Cos(th));

        var dot = correct.X * reflected.X + correct.Y * reflected.Y;
        Assert.Equal(0.0, dot, 9); // literally perpendicular -- not a sign flip
    }

    // The anchor half: the centre must sit exactly Length/2 BEHIND the front-bumper pose, along forward.
    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    [InlineData(217.0)]
    public void CentreFromFrontBumper_SitsHalfALengthBehindThePose(double angleDeg)
    {
        const double length = 5.0;
        var (forward, _) = VehicleObb.Basis(angleDeg);
        var (cx, cy) = VehicleObb.CentreFromFrontBumper(10.0, 20.0, angleDeg, length);

        // Vector from centre to the pose must be exactly +Length/2 * forward.
        Assert.Equal(forward.X * (length * 0.5), 10.0 - cx, 9);
        Assert.Equal(forward.Y * (length * 0.5), 20.0 - cy, 9);
    }

    // Two identically-posed cars must overlap, and the depth saturates at the WIDTH (the min-over-axes
    // property) -- this is the origin of the recurring "1.800 m" figure, pinned so it is not re-read as a depth.
    [Fact]
    public void IdenticallyPosedCars_SaturateAtVehicleWidth()
    {
        var pen = VehicleObb.Penetration(
            100.0, 200.0, 37.0, 5.0, 1.8,
            100.0, 200.0, 37.0, 5.0, 1.8);

        Assert.Equal(1.8, pen, 6);
    }

    // Well-separated cars must report exactly 0 (a separating axis exists).
    [Fact]
    public void DistantCars_DoNotOverlap()
    {
        var pen = VehicleObb.Penetration(
            0.0, 0.0, 90.0, 5.0, 1.8,
            50.0, 50.0, 90.0, 5.0, 1.8);

        Assert.Equal(0.0, pen);
    }

    // Shortest signed angular difference in degrees, in (-180, 180].
    private static double NormalizeSignedDegrees(double deg)
    {
        var d = deg % 360.0;
        if (d > 180.0) d -= 360.0;
        if (d <= -180.0) d += 360.0;
        return d;
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "scenarios"))
                && File.Exists(Path.Combine(d.FullName, "Traffic.sln")))
            {
                return d.FullName;
            }

            d = d.Parent;
        }

        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }
}
