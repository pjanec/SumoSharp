using Sim.Core;

namespace Sim.Viewer.Motion;

// Tunables for the kinematic single-track (bicycle) reconstruction (docs/IGBRIDGE-DECISIONS.md §5.3).
// All render-side; none affect parity.
public sealed class KinematicHeadingParams
{
    public double WheelbaseFactor { get; init; } = 0.6;      // wheelbase / vehicle length
    public double FrontOverhangFactor { get; init; } = 0.15; // front-axle inset from the front ref / length
    public double HoldSpeed { get; init; } = 0.5;            // below this: hold heading (no spin at stops)
    public double ReseedJumpMeters { get; init; } = 7.0;     // front jump beyond link+this -> reseed (teleport)
}

// One reconstructed rigid-body pose: the vehicle geometric center (what the owner's IG models pivot on),
// plus the front reference and the dragged rear axle (for a renderer that anchors elsewhere), and the
// body heading in navi-degrees.
public readonly struct KinematicPose
{
    public KinematicPose(double centerX, double centerY, double frontX, double frontY,
        double rearX, double rearY, float headingDeg)
    {
        CenterX = centerX;
        CenterY = centerY;
        FrontX = frontX;
        FrontY = frontY;
        RearX = rearX;
        RearY = rearY;
        HeadingDeg = headingDeg;
    }

    public double CenterX { get; }
    public double CenterY { get; }
    public double FrontX { get; }
    public double FrontY { get; }
    public double RearX { get; }
    public double RearY { get; }
    public float HeadingDeg { get; }
}

// Kinematic single-track ("bicycle") reconstruction (docs/IGBRIDGE-DECISIONS.md §5.3). Fixes the
// "vehicle on rails" artifact: instead of pinning the front to the lane polyline and swinging the rear
// around it (the chord-heading model), the reliable front reference TOWS a rear axle that cannot slip
// sideways -- exactly a car's unsteered rear wheels. The rear follows a smooth path and cuts inside the
// corner (real off-tracking); heading = rear->front integrates polyline facets away; on a lane change the
// towed rear lags so the body yaws into the change and back. Stateful per vehicle, integrated at the emit
// cadence; deterministic (seeded from the lane heading, no System.Random, order-independent per handle).
// Shared in Sim.Viewer.Motion so the 2D/3D viewers and the IG feed all inherit it (fix once, fix all).
public sealed class KinematicHeading
{
    private struct State
    {
        public double Rx;   // rear-axle position
        public double Ry;
        public float Deg;   // last body heading (navi)
        public bool Init;
    }

    private readonly Dictionary<VehicleHandle, State> _state = new();
    private readonly KinematicHeadingParams _p;

    public KinematicHeading(KinematicHeadingParams? p = null) => _p = p ?? new KinematicHeadingParams();

    public void Clear() => _state.Clear();

    // Advance one vehicle by one emit step. `frontX/Y` is the (smooth) lane front reference from
    // PoseResolver, `laneHeadingDeg` its lane/chord heading (used only to seed and to inset the front
    // axle), `speed` the resolved speed, `length` the vehicle length, `dt` the emit interval.
    public KinematicPose Update(
        VehicleHandle handle, double frontX, double frontY, float laneHeadingDeg,
        double speed, double length, float dt)
    {
        var lwb = _p.WheelbaseFactor * length;
        var frontOver = _p.FrontOverhangFactor * length;

        _state.TryGetValue(handle, out var s);

        // Front axle: inset from the front reference by the front overhang, along the CURRENT body axis
        // (previous frame's heading; the lane heading on the first frame). A small (~0.15*len) term.
        var insetDeg = s.Init ? s.Deg : laneHeadingDeg;
        var (ix, iy) = Dir(insetDeg);
        var faX = frontX - frontOver * ix;
        var faY = frontY - frontOver * iy;

        if (!s.Init)
        {
            var (lx, ly) = Dir(laneHeadingDeg);
            s.Rx = faX - lwb * lx;
            s.Ry = faY - lwb * ly;
            s.Deg = laneHeadingDeg;
            s.Init = true;
        }

        var vx = faX - s.Rx;
        var vy = faY - s.Ry;
        var d = Math.Sqrt(vx * vx + vy * vy);

        // Teleport / handle reuse: the front axle jumped far beyond a plausible step + the link length.
        // Dragging across that gap would fling the body; reseed the rear from the lane heading instead.
        if (d > lwb + _p.ReseedJumpMeters)
        {
            var (lx, ly) = Dir(laneHeadingDeg);
            s.Rx = faX - lwb * lx;
            s.Ry = faY - lwb * ly;
            s.Deg = laneHeadingDeg;
            vx = faX - s.Rx;
            vy = faY - s.Ry;
            d = lwb;
        }

        float deg;
        if (d < 1e-6 || speed < _p.HoldSpeed)
        {
            // Degenerate / near-stationary: hold heading (no spinning at a red light), keep the rear rigid.
            deg = s.Deg;
            var (hx, hy) = Dir(deg);
            s.Rx = faX - lwb * hx;
            s.Ry = faY - lwb * hy;
        }
        else
        {
            // Drag: pull the rear axle toward the front axle, staying exactly `lwb` behind, along the
            // previous rear->front direction -> no lateral slip. Heading = the (new) rear->front axis.
            var ux = vx / d;
            var uy = vy / d;
            s.Rx = faX - lwb * ux;
            s.Ry = faY - lwb * uy;
            deg = Navi(faX - s.Rx, faY - s.Ry);
        }

        s.Deg = deg;
        _state[handle] = s;

        var cx = (faX + s.Rx) * 0.5; // vehicle center ~ midpoint of the two axles
        var cy = (faY + s.Ry) * 0.5;
        return new KinematicPose(cx, cy, frontX, frontY, s.Rx, s.Ry, deg);
    }

    // Navi-degree (0 = north, clockwise) unit vector -- identical convention to PoseResolver.
    private static (double X, double Y) Dir(float naviDeg)
    {
        var math = (90.0 - naviDeg) * Math.PI / 180.0;
        return (Math.Cos(math), Math.Sin(math));
    }

    private static float Navi(double dx, double dy)
    {
        var deg = 90.0 - Math.Atan2(dy, dx) * 180.0 / Math.PI;
        deg %= 360.0;
        if (deg < 0.0)
        {
            deg += 360.0;
        }

        return (float)deg;
    }
}
