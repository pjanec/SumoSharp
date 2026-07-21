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
        public double Fx;   // tracked front reference position (g-h filter)
        public double Fy;
        public double Fvx;  // tracked front reference velocity (g-h filter)
        public double Fvy;
        public double Rx;   // rear-axle position
        public double Ry;
        public double PrevFaX; // previous front-axle position (for substepped drag integration)
        public double PrevFaY;
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

        _state.TryGetValue(handle, out var s);

        if (!s.Init)
        {
            var (sdx, sdy) = Dir(laneHeadingDeg);
            s.Fx = frontX;
            s.Fy = frontY;
            s.Fvx = speed * sdx;
            s.Fvy = speed * sdy;
        }

        // (0) CONSTANT-VELOCITY (g-h) tracking of the FRONT reference. The front is followed by a
        // critically-damped position+velocity filter: predict it advancing at its OWN tracked velocity, then
        // correct toward the authoritative front by the gains (g, h). Two properties matter here:
        //   * ZERO lag on smooth motion — the velocity term carries the front through a turn, so there is no
        //     deviation to decay and no catch-up lag;
        //   * the prediction uses the front's OWN velocity, NOT the body heading. The old projective blend
        //     predicted along the (lagged) body heading s.Deg, which injected a cross-track push whenever the
        //     heading lagged the travel direction — right after a sharp turn that resonated into a heading
        //     overshoot/oscillation (the owner's "beginner-driver" wobble). Decoupling the two removes it;
        //     heading smoothing is left to the rear-axle drag, a stable follower that cannot overshoot.
        // Cross-track jitter/facets are low-passed by the gains; a teleport (huge residual) snaps.
        var predX = s.Fx + s.Fvx * dt;
        var predY = s.Fy + s.Fvy * dt;

        if ((frontX - predX) * (frontX - predX) + (frontY - predY) * (frontY - predY)
            > _p.ReseedJumpMeters * _p.ReseedJumpMeters)
        {
            var (sdx, sdy) = Dir(laneHeadingDeg);
            s.Fx = frontX;
            s.Fy = frontY;
            s.Fvx = speed * sdx;
            s.Fvy = speed * sdy;
        }
        else
        {
            // Lane-change ease: while the engine's lateral-straddle signal is active (or within its window),
            // use the longer LaneChangeSmoothTime (smaller gains) so the ~3.2 m lateral slide spreads into a
            // gentle ~1.3 s ease. Turns never set this, so they stay crisp.
            if (lateralEvent)
            {
                s.EaseTimer = _p.LaneChangeEaseWindow;
            }

            var tau = s.EaseTimer > 0.0 ? _p.LaneChangeSmoothTime : _p.PositionSmoothTime;
            s.EaseTimer = Math.Max(0.0, s.EaseTimer - dt);

            // g-h gains from the smoothing time; critically damped (h = g^2 / (2 - g)), no overshoot.
            var g = tau > 1e-6 ? 1.0 - Math.Exp(-dt / tau) : 1.0;
            var h = g * g / (2.0 - g);
            var rX = frontX - predX;
            var rY = frontY - predY;
            s.Fx = predX + g * rX;
            s.Fy = predY + g * rY;
            s.Fvx += (h / dt) * rX;
            s.Fvy += (h / dt) * rY;
        }

        var smFrontX = s.Fx;
        var smFrontY = s.Fy;

        // Front axle = the (error-blended) lane front reference. The previous prev-heading overhang inset
        // was dropped: it fed the drag a laterally-offset front and broke the no-slip constraint.
        var faX = smFrontX;
        var faY = smFrontY;

        if (!s.Init)
        {
            var (lx, ly) = Dir(laneHeadingDeg);
            s.Rx = faX - lwb * lx;
            s.Ry = faY - lwb * ly;
            s.Deg = laneHeadingDeg;
            s.PrevFaX = faX;
            s.PrevFaY = faY;
            s.Init = true;
        }

        // Teleport / handle reuse: front jumped implausibly far -> reseed the rear from the lane heading.
        if ((faX - s.Rx) * (faX - s.Rx) + (faY - s.Ry) * (faY - s.Ry)
            > (lwb + _p.ReseedJumpMeters) * (lwb + _p.ReseedJumpMeters))
        {
            var (lx, ly) = Dir(laneHeadingDeg);
            s.Rx = faX - lwb * lx;
            s.Ry = faY - lwb * ly;
            s.Deg = laneHeadingDeg;
            s.PrevFaX = faX;
            s.PrevFaY = faY;
        }

        float rawDeg;
        if (speed < _p.HoldSpeed)
        {
            // Near-stationary: hold heading (no spin at a red light), keep the rear rigid behind.
            rawDeg = s.Deg;
            var (hx, hy) = Dir(rawDeg);
            s.Rx = faX - lwb * hx;
            s.Ry = faY - lwb * hy;
        }
        else
        {
            // SUBSTEPPED trailer drag: integrate the rear axle along the front's motion this frame in N fine
            // steps. The rear velocity stays along the body axis (no lateral slip) BY CONSTRUCTION; a single
            // big step lets the rear cut the corner and appear to "steer"/skid, so we substep to make the
            // discrete no-slip accurate even at high yaw rate.
            const int n = 8;
            for (var k = 1; k <= n; k++)
            {
                var f = (double)k / n;
                var ffx = s.PrevFaX + (faX - s.PrevFaX) * f;
                var ffy = s.PrevFaY + (faY - s.PrevFaY) * f;
                var vvx = ffx - s.Rx;
                var vvy = ffy - s.Ry;
                var dd = Math.Sqrt(vvx * vvx + vvy * vvy);
                if (dd > 1e-9)
                {
                    s.Rx = ffx - lwb * (vvx / dd);
                    s.Ry = ffy - lwb * (vvy / dd);
                }
            }

            rawDeg = Navi(faX - s.Rx, faY - s.Ry);
        }

        s.PrevFaX = faX;
        s.PrevFaY = faY;

        // Heading low-pass (critically-damped, angular): removes any residual micro-step. Off by default.
        var delta = ((rawDeg - s.Deg + 540f) % 360f) - 180f;
        var smoothedDelta = SmoothDamp(0.0, delta, ref s.DegVel, _p.HeadingSmoothTime, dt);
        var outDeg = (float)(((s.Deg + smoothedDelta) % 360.0 + 360.0) % 360.0);
        s.Deg = outDeg;
        _state[handle] = s;

        // Vehicle CENTER placed so the front BUMPER sits exactly on the lane front reference (faX): the body
        // center is half a length behind the front bumper along the body heading. This makes the front
        // "stick" to the lane while the rear follows the drag heading.
        var (odx, ody) = Dir(outDeg);
        var cx = faX - length * 0.5 * odx;
        var cy = faY - length * 0.5 * ody;
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
