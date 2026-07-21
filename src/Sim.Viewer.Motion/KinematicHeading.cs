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

    // Critically-damped decay time (s) for the projective front-position error (the deviation of the
    // authoritative front from a straight-line prediction). Turns produce ~no deviation (zero lag);
    // facet kinks and lateral slides are absorbed and eased over ~2-3x this time. Tuned against the
    // per-vehicle rear-bumper reversal gate (0.40 -> fleet median 0 visible reversals).
    public double PositionSmoothTime { get; init; } = 0.40;

    // Critically-damped smoothing time (s) for the output HEADING. The drawn body extends a full length
    // from the pivot, so any residual heading micro-step is amplified into a tail wiggle; a light heading
    // low-pass removes the last of it. 0 disables it.
    public double HeadingSmoothTime { get; init; } = 0.0;

    // Lane-change ease (docs/IGBRIDGE-DECISIONS.md §5.2). SUMO changes lane instantly; the engine's
    // lateral-straddle signal marks it. For LaneChangeEaseWindow seconds after that signal, the front
    // error-decay uses the longer LaneChangeSmoothTime so the ~3.2 m lateral slide is spread into a gentle
    // ~1.3 s ease (a real car yaws ~10 deg into a lane change, not ~25). Turns are unaffected (no straddle).
    public double LaneChangeSmoothTime { get; init; } = 0.55;
    public double LaneChangeEaseWindow { get; init; } = 1.3;
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
        public double Fx;   // critically-damped front reference position
        public double Fy;
        public double Fvx;  // its velocity (SmoothDamp state)
        public double Fvy;
        public double Rx;   // rear-axle position
        public double Ry;
        public float Deg;   // last (emitted, smoothed) body heading (navi)
        public double DegVel; // angular SmoothDamp velocity for the heading low-pass
        public double EaseTimer; // seconds of lane-change ease remaining
        public bool Init;
    }

    // Unity-style critically-damped smoothing toward `target`; C1, no overshoot.
    private static double SmoothDamp(double current, double target, ref double vel, double smoothTime, double dt)
    {
        if (smoothTime <= 1e-6)
        {
            vel = 0.0;
            return target;
        }

        var omega = 2.0 / smoothTime;
        var x = omega * dt;
        var exp = 1.0 / (1.0 + x + 0.48 * x * x + 0.235 * x * x * x);
        var change = current - target;
        var temp = (vel + omega * change) * dt;
        vel = (vel - omega * temp) * exp;
        return target + (change + temp) * exp;
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
        double speed, double length, float dt, bool lateralEvent = false)
    {
        var lwb = _p.WheelbaseFactor * length;
        var frontOver = _p.FrontOverhangFactor * length;

        _state.TryGetValue(handle, out var s);

        if (!s.Init)
        {
            s.Fx = frontX;
            s.Fy = frontY;
            s.Fvx = 0.0;
            s.Fvy = 0.0;
        }

        // (0) PROJECTIVE error-blending of the FRONT reference. A genuine TURN moves the front continuously
        // along its heading (no deviation), while a FACET kink or a LANE CHANGE is a *deviation* from that
        // smooth motion. So predict where the rendered front should be (moving at `speed` along the last
        // heading) and only absorb + critically-damp the DEVIATION of the authoritative front from that
        // prediction. Result: zero lag on turns (no deviation to decay), and facets/lane-changes eased over
        // ~2-3x the smoothing time. A teleport snaps.
        var (hdx, hdy) = Dir(s.Init ? s.Deg : laneHeadingDeg);
        var predX = s.Fx + speed * dt * hdx;
        var predY = s.Fy + speed * dt * hdy;
        var errX = predX - frontX;
        var errY = predY - frontY;

        if (errX * errX + errY * errY > _p.ReseedJumpMeters * _p.ReseedJumpMeters)
        {
            s.Fx = frontX;
            s.Fy = frontY;
            s.Fvx = 0.0;
            s.Fvy = 0.0;
        }
        else
        {
            // Lane-change ease: while the engine's lateral-straddle signal is active (or within the ease
            // window after it), decay the deviation over the longer LaneChangeSmoothTime -> the ~3.2 m
            // lateral slide spreads into a gentle ~1.3 s ease. Turns never set this, so they stay crisp.
            if (lateralEvent)
            {
                s.EaseTimer = _p.LaneChangeEaseWindow;
            }

            var smoothTime = s.EaseTimer > 0.0 ? _p.LaneChangeSmoothTime : _p.PositionSmoothTime;
            s.EaseTimer = Math.Max(0.0, s.EaseTimer - dt);

            errX = SmoothDamp(errX, 0.0, ref s.Fvx, smoothTime, dt);
            errY = SmoothDamp(errY, 0.0, ref s.Fvy, smoothTime, dt);
            s.Fx = frontX + errX;
            s.Fy = frontY + errY;
        }

        var smFrontX = s.Fx;
        var smFrontY = s.Fy;

        // Front axle: inset from the smoothed front reference by the front overhang, along the CURRENT body
        // axis (previous frame's heading; the lane heading on the first frame). A small (~0.15*len) term.
        var insetDeg = s.Init ? s.Deg : laneHeadingDeg;
        var (ix, iy) = Dir(insetDeg);
        var faX = smFrontX - frontOver * ix;
        var faY = smFrontY - frontOver * iy;

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

        float rawDeg;
        if (d < 1e-6 || speed < _p.HoldSpeed)
        {
            // Degenerate / near-stationary: hold heading (no spinning at a red light), keep the rear rigid.
            rawDeg = s.Deg;
            var (hx, hy) = Dir(rawDeg);
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
            rawDeg = Navi(faX - s.Rx, faY - s.Ry);
        }

        // Heading low-pass (critically-damped, angular): removes the last residual micro-steps that the
        // full-length body lever would amplify into a tail wiggle.
        var delta = ((rawDeg - s.Deg + 540f) % 360f) - 180f;
        var smoothedDelta = SmoothDamp(0.0, delta, ref s.DegVel, _p.HeadingSmoothTime, dt);
        var outDeg = (float)(((s.Deg + smoothedDelta) % 360.0 + 360.0) % 360.0);
        s.Deg = outDeg;
        _state[handle] = s;

        var cx = (faX + s.Rx) * 0.5; // vehicle center ~ midpoint of the two axles
        var cy = (faY + s.Ry) * 0.5;
        return new KinematicPose(cx, cy, smFrontX, smFrontY, s.Rx, s.Ry, outDeg);
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
