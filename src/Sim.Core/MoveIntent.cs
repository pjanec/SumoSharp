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

    // Rung 5: the plan phase's proposed update to the front of this vehicle's own stop queue
    // (see StopTransition/StopRuntime) -- null when there is no stop, the stop isn't reached
    // yet, or reached-and-still-holding-with-nothing-to-change-in-bookkeeping (there is always
    // something to write while reached, so in practice this is null only pre-reach). Applied by
    // Engine.ExecuteMoves, never read/written elsewhere during Plan.
    public StopTransition? StopUpdate;

    // Rung 8b/A2 (LC2013 keep-right + speed-gain, DESIGN.md Seam 4): NOT threaded through
    // MoveIntent -- unlike StopUpdate above, both lane-change sub-decisions are decided AND
    // applied directly to VehicleRuntime.LaneId/KeepRightProbability/SpeedGainProbability in a
    // dedicated post-move phase (Engine.DecideSpeedGainChanges, called after ExecuteMoves), not
    // in Plan. This mirrors real SUMO: MSLCM_LC2013::_wantsChange (both its keep-right and
    // speed-gain sub-blocks) runs once per vehicle from MSLaneChanger's post-executeMovements
    // `changeLanes()` pass, never from planMovements -- see DecideSpeedGainChanges's own
    // CORRECTED-ORDERING comment for why rung 8b's original Plan-phase placement (correct only by
    // coincidence on every prior neighbor-free scenario) had to move.
}
