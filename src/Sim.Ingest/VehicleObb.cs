namespace Sim.Ingest;

// Oriented-bounding-box footprint of a vehicle, and overlap between two of them.
//
// This lives next to LaneGeometry ON PURPOSE: LaneGeometry OWNS the angle convention this depends on, and
// every past bug here came from a consumer re-deriving that convention by hand. There must be exactly ONE
// implementation, and it must live beside the thing that defines the convention.
//
// TWO CONVENTIONS, both previously got wrong (docs/NEED-obb-anchor-halflength.md):
//
// 1. THE ANGLE IS naviDEGREE, NOT MATH DEGREES. LaneGeometry.PositionAtOffset returns
//    `naviDeg = NormalizeDegrees(90 - atan2(dy, dx) * 180/PI)` (LaneGeometry.cs:59-60): 0 deg = NORTH,
//    increasing CLOCKWISE (SUMO's GeomHelper::naviDegree, the convention MSVehicle writes to FCD).
//    Inverting it, the math angle is `alpha = 90 - theta`, so the unit tangent is
//        forward = (cos alpha, sin alpha) = (+sin theta, cos theta)
//    A previous version used `(-sin theta, cos theta)`. That is a REFLECTION about the y-axis, not a sign
//    flip: it agrees (up to a harmless sign, since a box is symmetric about its axes) only when
//    sin theta == 0 -- i.e. due north/south -- and is PERPENDICULAR to the truth at 45 deg. Junction
//    internal lanes are curved, hence mostly diagonal, so it was wrong exactly where it mattered. It is
//    also why "validated at angle=90" could not catch it: 90 deg is a degenerate case where both agree.
//
// 2. THE POSE IS THE FRONT BUMPER, NOT THE CENTRE. `Kinematics.Pos` is front-bumper arc-length (SUMO
//    getPositionOnLane()/FCD convention) and PositionAtOffset returns the point AT that arc-length --
//    it subtracts nothing. So a box built as (pose +/- Length/2) is drawn a HALF LENGTH TOO FAR FORWARD.
//    The centre is `pose - (Length/2) * forward`.
//
// The two interact: the anchor correction is applied ALONG the forward axis, so a wrong axis mis-applies
// the anchor too. Never fix one without the other.
//
// Guarded by VehicleObbConventionTests, which derives the tangent from LaneGeometry itself by finite
// difference and asserts this basis reproduces it -- a self-validating check, not a restatement.
public static class VehicleObb
{
    // Unit forward (length-axis) and right (width-axis) vectors for a naviDegree heading.
    public static ((double X, double Y) Forward, (double X, double Y) Right) Basis(double angleDeg)
    {
        var th = angleDeg * Math.PI / 180.0;
        var forward = (X: Math.Sin(th), Y: Math.Cos(th));
        // Right = forward rotated -90 deg in world axes (perpendicular; sign is irrelevant to a symmetric
        // box, but fixed here so corner order is stable for callers that want it).
        var right = (X: Math.Cos(th), Y: -Math.Sin(th));
        return (forward, right);
    }

    // Box CENTRE from a FRONT-BUMPER pose (the pose LaneGeometry/the read surface produce).
    public static (double X, double Y) CentreFromFrontBumper(double x, double y, double angleDeg, double length)
    {
        var (f, _) = Basis(angleDeg);
        return (x - (length * 0.5) * f.X, y - (length * 0.5) * f.Y);
    }

    // Penetration depth (m) between two vehicles given FRONT-BUMPER poses, via the separating-axis test
    // over the four box axes. Returns 0 when a separating axis exists (disjoint), else the minimum
    // penetration across axes.
    //
    // NOTE the return value is a MINIMUM over axes, so for two deeply-overlapping boxes it saturates at the
    // smaller half-extent sum -- e.g. two identically-posed 5.0 x 1.8 m cars report exactly 1.800 (the
    // WIDTH), not a meaningful depth. Do not read a saturated value as "1.8 m of penetration".
    public static double Penetration(
        double ax, double ay, double aAngleDeg, double aLength, double aWidth,
        double bx, double by, double bAngleDeg, double bLength, double bWidth)
    {
        var ac = CentreFromFrontBumper(ax, ay, aAngleDeg, aLength);
        var bc = CentreFromFrontBumper(bx, by, bAngleDeg, bLength);

        var (af, ar) = Basis(aAngleDeg);
        var (bf, br) = Basis(bAngleDeg);

        double HalfExtent((double X, double Y) f, (double X, double Y) r, double len, double wid, double axX, double axY)
            => Math.Abs((f.X * axX + f.Y * axY) * (len * 0.5))
             + Math.Abs((r.X * axX + r.Y * axY) * (wid * 0.5));

        var minPen = double.PositiveInfinity;
        foreach (var (axX, axY) in new[] { (af.X, af.Y), (ar.X, ar.Y), (bf.X, bf.Y), (br.X, br.Y) })
        {
            var centreGap = Math.Abs((bc.X - ac.X) * axX + (bc.Y - ac.Y) * axY);
            var pen = HalfExtent(af, ar, aLength, aWidth, axX, axY)
                    + HalfExtent(bf, br, bLength, bWidth, axX, axY)
                    - centreGap;
            if (pen <= 0.0)
            {
                return 0.0; // separating axis -> disjoint
            }

            if (pen < minPen)
            {
                minPen = pen;
            }
        }

        return minPen;
    }
}
