using System;
using CityLib;
using Xunit;

namespace CityLib.Tests;

// Task T3.1 Part A (docs/DEMO-CITY3D-DESIGN.md "Multi-channel video wall", user-refined to a pure CLI
// off-axis frustum tool, no screen autodetection). Three success conditions, each its own fact:
//   1. a centered screen reduces to a symmetric frustum matching SymmetricFromFov;
//   2. an off-center screen produces the classic asymmetric |Left| != |Right|, in the correct direction;
//   3. two horizontally-adjacent tiles sharing an eye are seamless (tile-0's Right edge == tile-1's Left
//      edge, both as raw extents and as the actual world-space near-plane ray).
public class OffAxisFrustumTests
{
    private const double Tol = 1e-9;

    [Fact]
    public void OffAxis_CenteredScreen_IsSymmetricAndMatchesEquivalentFov()
    {
        // Eye 5 units back on the Z axis, screen a 4x2 rectangle centered at the origin on the z=0 plane --
        // the eye sits exactly on the screen's own perpendicular through its center, so this MUST reduce to
        // an ordinary symmetric frustum.
        var pe = (0.0, 0.0, 5.0);
        var pa = (-2.0, -1.0, 0.0); // bottom-left
        var pb = (2.0, -1.0, 0.0);  // bottom-right
        var pc = (-2.0, 1.0, 0.0);  // top-left
        const double near = 1.0;
        const double far = 100.0;

        var spec = OffAxisFrustum.OffAxis(pe, pa, pb, pc, near, far);

        Assert.Equal(-spec.Right, spec.Left, Tol);
        Assert.Equal(-spec.Top, spec.Bottom, Tol);

        // Hand-derived expected extents (see the design-doc-referenced worked example in
        // OffAxisFrustum.cs): d = 5 (eye-to-screen distance), left/right = +-2*near/d = +-0.4,
        // bottom/top = +-1*near/d = +-0.2.
        Assert.Equal(-0.4, spec.Left, Tol);
        Assert.Equal(0.4, spec.Right, Tol);
        Assert.Equal(-0.2, spec.Bottom, Tol);
        Assert.Equal(0.2, spec.Top, Tol);

        // The implied vertical FOV (from Top/near) must reproduce IDENTICAL extents through the
        // "just offset+angles+fov" convenience path.
        var fovYRad = 2.0 * Math.Atan(spec.Top / spec.Near);
        var fovYDeg = fovYRad * 180.0 / Math.PI;
        var aspect = (spec.Right - spec.Left) / (spec.Top - spec.Bottom);

        var viaFov = OffAxisFrustum.SymmetricFromFov(fovYDeg, aspect, near, far);

        Assert.Equal(spec.Left, viaFov.Left, Tol);
        Assert.Equal(spec.Right, viaFov.Right, Tol);
        Assert.Equal(spec.Bottom, viaFov.Bottom, Tol);
        Assert.Equal(spec.Top, viaFov.Top, Tol);
    }

    [Fact]
    public void OffAxis_OffCenterScreen_ProducesAsymmetryInTheCorrectDirection()
    {
        // Same screen as above, but the eye is shifted +1 along X -- i.e. toward the screen's right
        // (`pb`) side. The eye is now closer (in angle) to the right edge and farther from the left edge,
        // so the classic off-axis asymmetry must show |Left| > |Right| (a wider negative extent on the
        // side the eye moved AWAY from).
        var pe = (1.0, 0.0, 5.0);
        var pa = (-2.0, -1.0, 0.0);
        var pb = (2.0, -1.0, 0.0);
        var pc = (-2.0, 1.0, 0.0);
        const double near = 1.0;
        const double far = 100.0;

        var spec = OffAxisFrustum.OffAxis(pe, pa, pb, pc, near, far);

        // Hand-derived: d is unchanged (the X shift doesn't affect the eye-to-plane distance) = 5;
        // left = -3*near/d = -0.6, right = 1*near/d = 0.2.
        Assert.Equal(-0.6, spec.Left, Tol);
        Assert.Equal(0.2, spec.Right, Tol);
        Assert.True(
            Math.Abs(spec.Left) > Math.Abs(spec.Right),
            $"expected the eye shifted toward +X to widen |Left| beyond |Right|, got Left={spec.Left}, Right={spec.Right}");

        // Bottom/top are untouched by a purely horizontal eye shift (the screen's vertical extent relative
        // to the eye's Y=0 didn't change).
        Assert.Equal(-0.2, spec.Bottom, Tol);
        Assert.Equal(0.2, spec.Top, Tol);
    }

    [Fact]
    public void OffAxis_TwoAdjacentTilesSharingAnEye_AreSeamless()
    {
        // A 2-wide wall on the z=-5 plane (screen 4 units in front of a shared eye at the origin, eye
        // itself off-center at x=0.3 so BOTH tiles individually are asymmetric, not just trivially
        // centered), split at x=0 into a left tile and a right tile:
        //   tile0: x in [-2, 0], tile1: x in [0, 2], both y in [-1, 1].
        // tile0's bottom-right corner (0,-1,-5) and top-... shared edge is exactly tile1's bottom-left
        // corner -- the whole point of a video wall built from ONE flat plane: adjacent tiles' Right/Left
        // near-plane extents must coincide exactly, or the wall shows a gap (extents diverge outward) or
        // overlap (extents diverge inward) at the seam.
        var pe = (0.3, 0.0, 0.0);
        const double near = 1.0;
        const double far = 100.0;

        var tile0 = OffAxisFrustum.OffAxis(
            pe,
            pa: (-2.0, -1.0, -5.0),
            pb: (0.0, -1.0, -5.0),
            pc: (-2.0, 1.0, -5.0),
            near, far);

        var tile1 = OffAxisFrustum.OffAxis(
            pe,
            pa: (0.0, -1.0, -5.0),
            pb: (2.0, -1.0, -5.0),
            pc: (0.0, 1.0, -5.0),
            near, far);

        // Both tiles are coplanar and share the eye, so they must resolve to the SAME camera axes
        // (Right/Up/Normal) -- only the asymmetric extents differ per tile.
        Assert.Equal(tile0.RightX, tile1.RightX, Tol);
        Assert.Equal(tile0.RightY, tile1.RightY, Tol);
        Assert.Equal(tile0.RightZ, tile1.RightZ, Tol);
        Assert.Equal(tile0.UpX, tile1.UpX, Tol);
        Assert.Equal(tile0.NormalZ, tile1.NormalZ, Tol);

        // The core seam-continuity assertion: tile0's RIGHT near-plane extent equals tile1's LEFT extent.
        // Hand-derived: d = 5 for both (z-shift only); tile0.Right = dot(vr, pb0-pe)*near/d =
        // dot((1,0,0),(-0.3,-1,-5))*1/5 = -0.3/5 = -0.06; tile1.Left = dot(vr, pa1-pe)*near/d, and
        // pa1 == pb0 (both are the world point (0,-1,-5)), so it MUST be the identical -0.06.
        Assert.Equal(-0.06, tile0.Right, Tol);
        Assert.Equal(-0.06, tile1.Left, Tol);
        Assert.Equal(tile0.Right, tile1.Left, Tol);

        // Not just the raw extent number -- the actual world-space point the seam edge projects to (at
        // the near plane, for two different vertical positions along the shared vertical edge) must be
        // identical from both tiles' frustum math, i.e. no gap/overlap seam in the rendered wall.
        AssertSeamRayMatches(tile0, tile1, tile0.Right, tile1.Left, y: 0.0);
        AssertSeamRayMatches(tile0, tile1, tile0.Right, tile1.Left, y: tile0.Top);
        AssertSeamRayMatches(tile0, tile1, tile0.Right, tile1.Left, y: tile0.Bottom);
    }

    // The world-space point on a tile's near plane at local (x, y): Eye + Forward*Near + Right*x + Up*y,
    // where Forward = -Normal (OffAxisFrustum.cs's header comment: Normal is the screen's toward-the-eye
    // normal, so the camera looks along -Normal).
    private static (double X, double Y, double Z) NearPlanePoint(OffAxisFrustum.FrustumSpec spec, double x, double y)
    {
        var fx = -spec.NormalX;
        var fy = -spec.NormalY;
        var fz = -spec.NormalZ;
        return (
            spec.EyeX + fx * spec.Near + spec.RightX * x + spec.UpX * y,
            spec.EyeY + fy * spec.Near + spec.RightY * x + spec.UpY * y,
            spec.EyeZ + fz * spec.Near + spec.RightZ * x + spec.UpZ * y);
    }

    private static void AssertSeamRayMatches(
        OffAxisFrustum.FrustumSpec tile0, OffAxisFrustum.FrustumSpec tile1, double x0, double x1, double y)
    {
        var p0 = NearPlanePoint(tile0, x0, y);
        var p1 = NearPlanePoint(tile1, x1, y);
        Assert.Equal(p0.X, p1.X, Tol);
        Assert.Equal(p0.Y, p1.Y, Tol);
        Assert.Equal(p0.Z, p1.Z, Tol);
    }
}
