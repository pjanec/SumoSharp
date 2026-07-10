namespace Sim.Core;

// B1: a live, non-SUMO obstacle injected onto a lane (DESIGN.md "Two futures" -- a real vehicle,
// pedestrian, or detection fed in from outside any offline SUMO run). FrontPos mirrors a
// vehicle's `pos` convention: it is the obstacle's DOWNSTREAM (front) edge on the lane, so its
// back (the edge a follower must not cross) is FrontPos - Length, exactly like a stopped leader's
// back = leader.Pos - leader.Length in LeaderFollowSpeedConstraint. Active only during
// [StartTime, EndTime) -- both default to "always active" so the common single-argument-set
// AddObstacle call (no start/end) is unconditionally active.
//
// B5-i: Speed and MaxDecel generalize this from a purely STATIC obstacle (B1's only case) to a
// MOVING one driven by an external, non-SUMO layer (navmesh/RVO agent, pedestrian, live
// detection). Speed is the agent's along-lane velocity in m/s, exactly as reported by that
// external layer for THIS step -- Engine dead-reckons FrontPos forward by Speed*dt once per step
// (AdvanceObstacles, Input phase) between owner corrections, the same "extrapolate a reported
// velocity, don't simulate a driver" contract UpdateObstacle documents. MaxDecel is the agent's
// braking capability, used only when Speed != 0 (see ObstacleConstraint's predMaxDecel
// conditional) -- for a static obstacle it is irrelevant (BrakeGap(0, ...) == 0 regardless), so
// leaving it at its default here never changes B1 behavior. Both default to the static case
// (Speed=0, MaxDecel=0) so every existing AddObstacle call site is unaffected.
//
// B6 (lateral evasion / swerve): LatPos and Width give the agent a LATERAL footprint within the
// lane, so a car can swerve AROUND it instead of only stopping behind it (the "pedestrian jumps off
// the sidewalk into the driving lane" case). LatPos is the agent's lateral CENTER, lane-center-
// relative in metres, positive = LEFT of travel direction (matching Kinematics.LatOffset). Width is
// its lateral EXTENT. The default Width = 0 preserves the pre-B6 semantics EXACTLY: an obstacle with
// no lateral extent is treated as blocking the WHOLE lane width (ObstacleConstraint's lateral-overlap
// gate returns true unconditionally), so every existing AddObstacle/AddMovingObstacle call site (which
// never sets Width) still makes cars stop dead behind it, byte-identical. Only an obstacle with an
// explicit Width > 0 is "dodgeable" -- a car whose own footprint can clear [LatPos - Width/2,
// LatPos + Width/2] (within its lane, or by spilling into a safe adjacent lane) swerves past instead
// of stopping.
public sealed record ExternalObstacle(
    string Id,
    string LaneId,
    double FrontPos,
    double Length,
    double StartTime,
    double EndTime,
    double Speed = 0.0,
    double MaxDecel = 0.0,
    double LatPos = 0.0,
    double Width = 0.0);
