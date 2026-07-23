using System;
using CityLib;
using Xunit;

namespace CityLib.Tests;

// Camera-controller deliverable (docs/LIVE-CITY-VIEWERS-DESIGN.md camera-controller section) -- pure
// spherical-orbit math tests, mirroring OffAxisFrustumTests' "pure math, no Godot types, tested" pattern.
// Covers: orbiting 90 degrees puts the camera where expected, zoom changes distance (and clamps), pan
// translates the focus without changing yaw/pitch/distance, pitch stays clamped away from the poles, and
// Reset() returns to the constructed pose after arbitrary mutation.
public class OrbitCameraControllerTests
{
    private const float Tol = 1e-4f;

    [Fact]
    public void CameraPosition_AtYawZeroPitchZero_SitsOnPositiveZAtDistance()
    {
        var c = new OrbitCameraController(0f, 0f, 0f, yawRad: 0f, pitchRad: 0f, distance: 10f);

        var pos = c.CameraPosition();

        Assert.Equal(0f, pos.X, Tol);
        Assert.Equal(0f, pos.Y, Tol);
        Assert.Equal(10f, pos.Z, Tol);
    }

    [Fact]
    public void Orbit_Yaw90Degrees_MovesCameraFromPositiveZToPositiveX()
    {
        var c = new OrbitCameraController(0f, 0f, 0f, yawRad: 0f, pitchRad: 0f, distance: 10f);

        c.Orbit(MathF.PI / 2f, 0f);
        var pos = c.CameraPosition();

        Assert.Equal(10f, pos.X, Tol);
        Assert.Equal(0f, pos.Y, Tol);
        Assert.Equal(0f, pos.Z, Tol);
    }

    [Fact]
    public void Orbit_Yaw360Degrees_ReturnsToStart()
    {
        var c = new OrbitCameraController(0f, 0f, 0f, yawRad: 0f, pitchRad: 0.3f, distance: 25f);
        var start = c.CameraPosition();

        c.Orbit(MathF.PI * 2f, 0f);
        var end = c.CameraPosition();

        Assert.Equal(start.X, end.X, Tol);
        Assert.Equal(start.Y, end.Y, Tol);
        Assert.Equal(start.Z, end.Z, Tol);
    }

    [Fact]
    public void Orbit_PitchNearMax_PutsCameraNearlyDirectlyAboveFocus()
    {
        // yaw=0 so any residual horizontal offset at max pitch is purely the (cos(pitch)*sin(yaw)==0)
        // Z/X-free term -- isolates the pitch clamp's effect on Y from yaw.
        var c = new OrbitCameraController(1f, 2f, 3f, yawRad: 0f, pitchRad: 0f, distance: 5f);

        c.Orbit(0f, OrbitCameraController.DefaultMaxPitchRad - c.PitchRad); // drive pitch to the clamp
        var pos = c.CameraPosition();

        Assert.Equal(OrbitCameraController.DefaultMaxPitchRad, c.PitchRad, Tol);
        Assert.Equal(1f, pos.X, 0.01f);
        Assert.Equal(2f + (5f * MathF.Sin(OrbitCameraController.DefaultMaxPitchRad)), pos.Y, 0.01f);
        Assert.Equal(3f + (5f * MathF.Cos(OrbitCameraController.DefaultMaxPitchRad)), pos.Z, 0.01f);
    }

    [Fact]
    public void Orbit_PitchClampsAtMaxPitch_NeverExceedsIt()
    {
        var c = new OrbitCameraController(0f, 0f, 0f, yawRad: 0f, pitchRad: 0f, distance: 10f);

        c.Orbit(0f, 100f); // wildly over-rotate upward
        Assert.Equal(OrbitCameraController.DefaultMaxPitchRad, c.PitchRad, Tol);

        c.Orbit(0f, -200f); // wildly over-rotate downward
        Assert.Equal(-OrbitCameraController.DefaultMaxPitchRad, c.PitchRad, Tol);
    }

    [Fact]
    public void Zoom_MultipliesDistance()
    {
        var c = new OrbitCameraController(0f, 0f, 0f, yawRad: 0f, pitchRad: 0f, distance: 100f);

        c.Zoom(0.5f);
        Assert.Equal(50f, c.Distance, Tol);

        c.Zoom(2f);
        Assert.Equal(100f, c.Distance, Tol);
    }

    [Fact]
    public void Zoom_ClampsToMinAndMaxDistance()
    {
        var c = new OrbitCameraController(0f, 0f, 0f, 0f, 0f, distance: 10f, minDistance: 5f, maxDistance: 20f);

        c.Zoom(0.01f); // would go to 0.1, clamped to 5
        Assert.Equal(5f, c.Distance, Tol);

        c.Zoom(1000f); // would blow way past 20, clamped to 20
        Assert.Equal(20f, c.Distance, Tol);
    }

    [Fact]
    public void Zoom_RejectsNonPositiveFactor()
    {
        var c = new OrbitCameraController(0f, 0f, 0f, 0f, 0f, distance: 10f);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Zoom(0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Zoom(-1f));
    }

    [Fact]
    public void Pan_AtYawZeroPitchZero_MovesFocusAlongWorldXAndY()
    {
        var c = new OrbitCameraController(0f, 0f, 0f, yawRad: 0f, pitchRad: 0f, distance: 10f);

        c.Pan(dRight: 3f, dUp: 2f);

        // Right at yaw=0 is world +X, Up at pitch=0 is world +Y.
        Assert.Equal(3f, c.FocusX, Tol);
        Assert.Equal(2f, c.FocusY, Tol);
        Assert.Equal(0f, c.FocusZ, Tol);
    }

    [Fact]
    public void Pan_DoesNotChangeYawPitchOrDistance()
    {
        var c = new OrbitCameraController(0f, 0f, 0f, yawRad: 0.4f, pitchRad: 0.2f, distance: 30f);

        c.Pan(5f, -5f);

        Assert.Equal(0.4f, c.YawRad, Tol);
        Assert.Equal(0.2f, c.PitchRad, Tol);
        Assert.Equal(30f, c.Distance, Tol);
    }

    [Fact]
    public void FromLookAt_ReproducesTheSourcePositionAndFocus()
    {
        var cameraPos = (X: 12f, Y: 40f, Z: -18f);
        var focus = (X: 5f, Y: 0f, Z: 5f);

        var c = OrbitCameraController.FromLookAt(cameraPos, focus);
        var pos = c.CameraPosition();

        Assert.Equal(cameraPos.X, pos.X, 1e-3f);
        Assert.Equal(cameraPos.Y, pos.Y, 1e-3f);
        Assert.Equal(cameraPos.Z, pos.Z, 1e-3f);
        Assert.Equal(focus.X, c.Focus.X, Tol);
        Assert.Equal(focus.Y, c.Focus.Y, Tol);
        Assert.Equal(focus.Z, c.Focus.Z, Tol);
    }

    [Fact]
    public void Reset_UndoesOrbitPanAndZoom()
    {
        var c = new OrbitCameraController(1f, 2f, 3f, yawRad: 0.1f, pitchRad: 0.2f, distance: 50f);
        var initialPos = c.CameraPosition();
        var initialFocus = c.Focus;

        c.Orbit(1.2f, -0.4f);
        c.Pan(10f, -7f);
        c.Zoom(3.3f);

        c.Reset();

        var pos = c.CameraPosition();
        Assert.Equal(initialPos.X, pos.X, Tol);
        Assert.Equal(initialPos.Y, pos.Y, Tol);
        Assert.Equal(initialPos.Z, pos.Z, Tol);
        Assert.Equal(initialFocus.X, c.Focus.X, Tol);
        Assert.Equal(initialFocus.Y, c.Focus.Y, Tol);
        Assert.Equal(initialFocus.Z, c.Focus.Z, Tol);
        Assert.Equal(50f, c.Distance, Tol);
    }

    [Fact]
    public void Constructor_ClampsInitialPitchAndDistance()
    {
        var c = new OrbitCameraController(
            0f, 0f, 0f, yawRad: 0f, pitchRad: 5f, distance: 1_000_000f,
            minDistance: 1f, maxDistance: 500f);

        Assert.Equal(OrbitCameraController.DefaultMaxPitchRad, c.PitchRad, Tol);
        Assert.Equal(500f, c.Distance, Tol);
    }

    [Fact]
    public void Constructor_RejectsInvalidDistanceRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OrbitCameraController(0f, 0f, 0f, 0f, 0f, 10f, minDistance: 50f, maxDistance: 10f));
    }
}
