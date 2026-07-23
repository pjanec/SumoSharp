using System;

namespace CityLib;

// docs/LIVE-CITY-VIEWERS-DESIGN.md's camera-controller deliverable ("the viewer currently has only a fixed
// preset camera, which blocks the owner from verifying the 3D scene at all") -- pure spherical
// orbit-camera math, no Godot type anywhere (same "pure math, no Godot types, tested" pattern as
// OffAxisFrustum/CarTransform/TrafficLightPlacer). Models the camera as a FOCUS point plus (yaw, pitch,
// distance): the position is always `focus - Forward(yaw,pitch) * distance`, so every "move the camera"
// gesture -- orbit (rotate around the focus), pan (translate the focus in the camera's own right/up
// plane), zoom (change distance) -- is a small, independently testable mutation of these four numbers.
// Main.cs (Godot layer) owns input wiring + turns GetPose()/CameraPosition()/Focus into a Camera3D
// transform every frame; this class never touches a window, mouse, or engine type.
public sealed class OrbitCameraController
{
    public const float DefaultMinDistance = 1f;
    public const float DefaultMaxDistance = 20000f;

    // Strictly inside +-90 degrees so the look-at basis never degenerates at the poles (gimbal flip).
    public const float DefaultMaxPitchRad = 1.55334f; // ~89 degrees

    public float FocusX { get; private set; }
    public float FocusY { get; private set; }
    public float FocusZ { get; private set; }
    public float YawRad { get; private set; }
    public float PitchRad { get; private set; }
    public float Distance { get; private set; }

    public float MinDistance { get; }
    public float MaxDistance { get; }
    public float MaxPitchRad { get; }

    // The pose Reset() returns to -- "the controller starts from that pose and lets the user move freely"
    // (design): whatever (focus, yaw, pitch, distance) the constructor was given IS the initial framing,
    // whether that came from BuildCameraAndLight's/UpdateCloseCameraFraming's computed preset (the normal
    // path) or from a `--cam-yaw`/`--cam-pitch`/`--cam-dist`/`--cam-focus` debug override (Main.cs
    // reconstructs the controller with the overridden numbers, so Reset() honors the override too).
    private readonly float _initFocusX, _initFocusY, _initFocusZ;
    private readonly float _initYaw, _initPitch, _initDistance;

    public OrbitCameraController(
        float focusX, float focusY, float focusZ,
        float yawRad, float pitchRad, float distance,
        float minDistance = DefaultMinDistance, float maxDistance = DefaultMaxDistance,
        float maxPitchRad = DefaultMaxPitchRad)
    {
        if (minDistance <= 0f || maxDistance < minDistance)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistance), "OrbitCameraController: require 0 < minDistance <= maxDistance.");
        }

        MinDistance = minDistance;
        MaxDistance = maxDistance;
        MaxPitchRad = maxPitchRad;

        FocusX = focusX;
        FocusY = focusY;
        FocusZ = focusZ;
        YawRad = yawRad;
        PitchRad = Clamp(pitchRad, -maxPitchRad, maxPitchRad);
        Distance = Clamp(distance, minDistance, maxDistance);

        _initFocusX = FocusX;
        _initFocusY = FocusY;
        _initFocusZ = FocusZ;
        _initYaw = YawRad;
        _initPitch = PitchRad;
        _initDistance = Distance;
    }

    // Seeds a controller from an already-computed (cameraPosition, focus) pair -- exactly the pose
    // BuildCameraAndLight/UpdateCloseCameraFraming already compute for the fixed preset -- so the
    // interactive controller starts EXACTLY where the old fixed camera used to sit, then lets the user
    // move freely from there (the deliverable's core requirement).
    public static OrbitCameraController FromLookAt(
        (float X, float Y, float Z) cameraPosition, (float X, float Y, float Z) focus,
        float minDistance = DefaultMinDistance, float maxDistance = DefaultMaxDistance,
        float maxPitchRad = DefaultMaxPitchRad)
    {
        var dx = cameraPosition.X - focus.X;
        var dy = cameraPosition.Y - focus.Y;
        var dz = cameraPosition.Z - focus.Z;
        var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        if (distance < 1e-6f)
        {
            // Degenerate (camera sitting exactly on the focus): no direction to derive yaw/pitch from --
            // fall back to the default "looking down -Z" pose at the clamped minimum distance.
            return new OrbitCameraController(focus.X, focus.Y, focus.Z, 0f, 0f, minDistance, minDistance, maxDistance, maxPitchRad);
        }

        // Inverse of Axes()'s Forward = (-cos(pitch)sin(yaw), -sin(pitch), -cos(pitch)cos(yaw)), i.e.
        // (dx,dy,dz) = -Forward*distance = (cos(pitch)sin(yaw), sin(pitch), cos(pitch)cos(yaw)) * distance.
        var yaw = MathF.Atan2(dx, dz);
        var pitch = MathF.Asin(Clamp(dy / distance, -1f, 1f));

        return new OrbitCameraController(focus.X, focus.Y, focus.Z, yaw, pitch, distance, minDistance, maxDistance, maxPitchRad);
    }

    // Rotates the camera around the focus point. Positive deltaYaw sweeps the camera from +Z toward +X
    // (see OrbitCameraControllerTests' worked example); positive deltaPitch raises the camera above the
    // focus. Pitch is clamped to +-MaxPitchRad so orbiting straight up/down can never flip the view.
    public void Orbit(float deltaYawRad, float deltaPitchRad)
    {
        YawRad = WrapAngle(YawRad + deltaYawRad);
        PitchRad = Clamp(PitchRad + deltaPitchRad, -MaxPitchRad, MaxPitchRad);
    }

    // Translates the FOCUS point in the camera's own current right/up plane -- panning moves what the
    // camera is looking AT, not the camera's orbit angle/distance, so a pan followed by an orbit still
    // orbits around the new point. dRight/dUp are world-space metres (Main.cs scales screen-pixel deltas by
    // the current Distance before calling this, so the pan speed feels consistent whether zoomed in or out).
    public void Pan(float dRight, float dUp)
    {
        var (right, up, _) = Axes();
        FocusX += (right.X * dRight) + (up.X * dUp);
        FocusY += (right.Y * dRight) + (up.Y * dUp);
        FocusZ += (right.Z * dRight) + (up.Z * dUp);
    }

    // Multiplicative dolly toward/away from the focus: factor < 1 zooms in (closer), factor > 1 zooms out
    // (farther). Multiplicative (not additive) so one wheel notch feels proportionally the same whether
    // already close-up or far out. Clamped to [MinDistance, MaxDistance].
    public void Zoom(float factor)
    {
        if (factor <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), "OrbitCameraController.Zoom: factor must be > 0.");
        }

        Distance = Clamp(Distance * factor, MinDistance, MaxDistance);
    }

    // The reset key's target: back to exactly the pose this controller was constructed with.
    public void Reset()
    {
        FocusX = _initFocusX;
        FocusY = _initFocusY;
        FocusZ = _initFocusZ;
        YawRad = _initYaw;
        PitchRad = _initPitch;
        Distance = _initDistance;
    }

    public (float X, float Y, float Z) Focus => (FocusX, FocusY, FocusZ);

    // The camera's world position for the CURRENT yaw/pitch/distance/focus -- Main.cs assigns this to the
    // Camera3D node's Position every frame, then LookAt(Focus, up).
    public (float X, float Y, float Z) CameraPosition()
    {
        var (_, _, forward) = Axes();
        return (
            FocusX - (forward.X * Distance),
            FocusY - (forward.Y * Distance),
            FocusZ - (forward.Z * Distance));
    }

    // Right/Up/Forward unit axes for the current yaw/pitch (no roll). Forward points FROM the camera
    // TOWARD the focus. yaw=0, pitch=0 => Forward=(0,0,-1), i.e. the camera sits on +Z looking toward -Z --
    // the same placement BuildCameraAndLight's original `center + (0, extent*heightFactor,
    // extent*backFactor)` (backFactor > 0) produces, so a controller seeded via FromLookAt from that pose
    // reproduces it exactly.
    private ((float X, float Y, float Z) Right, (float X, float Y, float Z) Up, (float X, float Y, float Z) Forward) Axes()
    {
        var cosYaw = MathF.Cos(YawRad);
        var sinYaw = MathF.Sin(YawRad);
        var cosPitch = MathF.Cos(PitchRad);
        var sinPitch = MathF.Sin(PitchRad);

        var forward = (X: -cosPitch * sinYaw, Y: -sinPitch, Z: -cosPitch * cosYaw);
        var right = (X: cosYaw, Y: 0f, Z: -sinYaw); // horizontal, independent of pitch (no roll)
        var up = (
            X: (right.Y * forward.Z) - (right.Z * forward.Y),
            Y: (right.Z * forward.X) - (right.X * forward.Z),
            Z: (right.X * forward.Y) - (right.Y * forward.X));

        return (right, up, forward);
    }

    private static float Clamp(float value, float min, float max) => value < min ? min : (value > max ? max : value);

    private static float WrapAngle(float angleRad)
    {
        const float twoPi = MathF.PI * 2f;
        var a = angleRad % twoPi;
        if (a > MathF.PI)
        {
            a -= twoPi;
        }
        else if (a < -MathF.PI)
        {
            a += twoPi;
        }

        return a;
    }
}
