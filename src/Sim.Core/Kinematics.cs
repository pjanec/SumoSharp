namespace Sim.Core;

// Seam 2 (DESIGN.md "The four seams"): lane-relative (lane id lives on the owning runtime
// record, Pos here) is the source of truth; global x/y is derived for output only, never fed
// back. LatOffset is the continuous lateral position (from lane centreline, +left of travel),
// always 0 in phase 1 lane mode -- the cost is one always-zero double, the benefit is no reshape
// later. Phase 2 (sublane, P2.1): LatSpeed is added here -- the additive extension the original
// header anticipated ("adding them later is additive, not a redesign"). It is the lateral velocity
// (m/s, +left), 0 for every lane-centred vehicle, so it stays inert until the sublane model
// (lateral-resolution > 0) drives it. LatAccel is deferred until SL2015's lateral integration
// actually needs it (the continuous change we port first is lateral-speed-bounded, not
// accel-bounded); adding it later is likewise additive.
public struct Kinematics
{
    public double Pos;
    public double Speed;
    public double LatOffset;
    public double LatSpeed;
}
