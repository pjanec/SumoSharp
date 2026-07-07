namespace Sim.Core;

// Seam 4 (DESIGN.md): the plan phase writes ONLY to the owning vehicle's own MoveIntent --
// never to shared state, even single-threaded (this discipline is what turns later
// multithreading into a scheduling change, not a rewrite). LatOffset is the lane-change
// bridge: LC2013 (phase 1) will write a target lane index here and integration snaps to it;
// SL2015 (phase 2) will write a continuous lateral offset and integration drifts toward it.
// It stays 0 in this task -- no lane changes exist yet.
public struct MoveIntent
{
    public double NewSpeed;
    public double LatOffset;
}
