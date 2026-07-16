using System;

namespace CityLib;

// docs/DEMO-CITY3D-DESIGN.md "Multi-channel video wall" — task T3.1's user-refined design: NO screen
// autodetection. Instead each video-wall channel (a tiled monitor / a projector cell / a CAVE face) is
// described on the command line and turned into a generalized, possibly OFF-AXIS (asymmetric) perspective
// projection here. This is the standard "generalized perspective projection" construction (Kooima, 2009,
// "Generalized Perspective Projection") used by every tiled-wall / CAVE renderer: ONE shared eye point can
// look through several DIFFERENT flat screens (or several DIFFERENT regions of the same flat screen)
// without those screens needing to be centered on the eye's view axis — the near-plane rectangle is
// shifted/skewed (not just the camera rotated), which is what keeps adjacent tiles seamless (task T3.1
// test 3) in a way rotating a symmetric FOV camera per-tile could never achieve (rotating introduces
// keystoning / seams at tile boundaries).
//
// Pure math, no Godot type anywhere (CityLib stays Godot-free, same rule as CoordinateTransform.cs) — the
// Viewer project (demos/City3D/Viewer/Main.cs) is the only place a Godot.Camera3D/Basis actually gets
// built from a FrustumSpec.
public static class OffAxisFrustum
{
    // Default asymmetric-mode near/far — callers may override; these are just sane defaults for a
    // city-scale (tens/hundreds of metres) scene.
    public const double DefaultNear = 0.3;
    public const double DefaultFar = 3000.0;

    // The result a Godot Camera3D can consume directly:
    //  - Eye* is the camera POSITION (world/wall frame).
    //  - Right*/Up*/Normal* are the screen's own right/up/normal axes, expressed as world-frame unit
    //    vectors — together they are exactly a camera Basis's three COLUMNS (Godot's
    //    Basis(column0, column1, column2) constructor takes axes as columns): Right = local +X, Up = local
    //    +Y, Normal = local +Z. A Node3D's local forward is -Z, so the direction the camera actually LOOKS
    //    is -Normal (see the "vn" derivation in OffAxis below) — Normal is deliberately the screen's
    //    "toward the eye" normal, not the view direction, so plain axis vectors (no extra sign-flip
    //    bookkeeping) drop straight into a Basis.
    //  - Left/Right/Bottom/Top are the SIGNED near-plane extents (Kooima's l, r, b, t) at distance Near —
    //    this is exactly what Godot's Projection.CreateFrustum(left, right, bottom, top, near, far) (or,
    //    for Camera3D.SetFrustum's size/offset form: size = Top-Bottom, offset = ((Left+Right)/2,
    //    (Bottom+Top)/2)) expects. A CENTERED screen (the eye on the screen's own view axis) reduces to the
    //    symmetric case Left == -Right, Bottom == -Top — i.e. an ordinary symmetric FOV frustum is just the
    //    special case of this construction where the eye sits on the screen's perpendicular through its
    //    center.
    public readonly record struct FrustumSpec(
        double EyeX, double EyeY, double EyeZ,
        double RightX, double RightY, double RightZ,
        double UpX, double UpY, double UpZ,
        double NormalX, double NormalY, double NormalZ,
        double Left, double Right, double Bottom, double Top,
        double Near, double Far);

    // The general off-axis (Kooima-style) construction. `pe` is the eye position; `pa`/`pb`/`pc` are three
    // corners of a FLAT screen rectangle — pa = bottom-left, pb = bottom-right, pc = top-left — all in the
    // same shared world/wall frame as `pe`. `pa`,`pb`,`pc` need not be centered on, or even facing squarely
    // toward, the eye: that asymmetry is the entire point (a tiled wall's screens sit at fixed positions;
    // the eye is wherever the CLI channel offset says it is).
    public static FrustumSpec OffAxis(
        (double X, double Y, double Z) pe,
        (double X, double Y, double Z) pa,
        (double X, double Y, double Z) pb,
        (double X, double Y, double Z) pc,
        double near = DefaultNear,
        double far = DefaultFar)
    {
        // Screen-plane basis: right along the bottom edge, up along the left edge. Both are unit vectors
        // in the shared world/wall frame; the screen need not be axis-aligned.
        var vr = Normalize(Sub(pb, pa));
        var vu = Normalize(Sub(pc, pa));
        // Normal = right x up, which (for pa=bottom-left, pb=bottom-right, pc=top-left, wound so the eye
        // is on the side the screen actually faces) points from the screen TOWARD the eye — see the header
        // comment's worked example. The camera's look direction is therefore -vn, not vn.
        var vn = Normalize(Cross(vr, vu));

        var va = Sub(pa, pe);
        var vb = Sub(pb, pe);
        var vc = Sub(pc, pe);

        // Perpendicular eye-to-screen-plane distance, projected along vn. Positive when the eye is on the
        // vn side of the screen (the only physically sane case — a channel behind its own screen is a
        // configuration error, not something this method tries to special-case).
        var d = -Dot(vn, va);

        var left = Dot(vr, va) * near / d;
        var right = Dot(vr, vb) * near / d;
        var bottom = Dot(vu, va) * near / d;
        var top = Dot(vu, vc) * near / d;

        return new FrustumSpec(
            pe.X, pe.Y, pe.Z,
            vr.X, vr.Y, vr.Z,
            vu.X, vu.Y, vu.Z,
            vn.X, vn.Y, vn.Z,
            left, right, bottom, top, near, far);
    }

    // Convenience for the "just offset + look angles + FOV" CLI form (a plain symmetric perspective —
    // exactly the OffAxis reduction where the eye sits on the screen's own perpendicular through its
    // center). `fovYDeg` is the full vertical field of view; `aspect` is width/height. `eye`/`right`/`up`/
    // `normal` default to the camera sitting at the origin looking down -Z with +Y up (Godot's own default
    // Node3D orientation) so a caller that only wants the extents (task T3.1 test 1's FOV-equivalence
    // check) can omit them; a caller placing a real CLI channel passes the offset/orientation it computed
    // from `--channel="off=...;look=...;fov=..."` so the returned FrustumSpec is immediately usable, same
    // as the OffAxis path.
    public static FrustumSpec SymmetricFromFov(
        double fovYDeg,
        double aspect,
        double near = DefaultNear,
        double far = DefaultFar,
        (double X, double Y, double Z)? eye = null,
        (double X, double Y, double Z)? right = null,
        (double X, double Y, double Z)? up = null,
        (double X, double Y, double Z)? normal = null)
    {
        var e = eye ?? (0.0, 0.0, 0.0);
        var r = right ?? (1.0, 0.0, 0.0);
        var u = up ?? (0.0, 1.0, 0.0);
        var n = normal ?? (0.0, 0.0, 1.0);

        var top = near * Math.Tan(fovYDeg * Math.PI / 360.0);
        var bottom = -top;
        var right2 = top * aspect;
        var left = -right2;

        return new FrustumSpec(
            e.X, e.Y, e.Z,
            r.X, r.Y, r.Z,
            u.X, u.Y, u.Z,
            n.X, n.Y, n.Z,
            left, right2, bottom, top, near, far);
    }

    private static (double X, double Y, double Z) Sub(
        (double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static (double X, double Y, double Z) Cross(
        (double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
    {
        var len = Math.Sqrt(Dot(v, v));
        if (len < 1e-12)
        {
            throw new ArgumentException("OffAxisFrustum: cannot normalize a near-zero-length vector (degenerate screen or look direction).");
        }

        return (v.X / len, v.Y / len, v.Z / len);
    }
}
