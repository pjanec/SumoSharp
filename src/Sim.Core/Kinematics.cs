namespace Sim.Core;

// Seam 2 (DESIGN.md "The four seams"): lane-relative (lane id lives on the owning runtime
// record, Pos here) is the source of truth; global x/y is derived for output only, never fed
// back. LatOffset is reserved for the phase-2 sublane model and is always 0 in phase 1 lane
// mode -- the cost is one always-zero double, the benefit is no reshape later. Lateral
// kinematics (LatSpeed/LatAccel) stay OUT of this struct until laneless work actually needs
// them; adding them later is additive, not a redesign.
public struct Kinematics
{
    public double Pos;
    public double Speed;
    public double LatOffset;
}
