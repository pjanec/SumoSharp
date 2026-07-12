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
// B6-lat: LatSpeed is the agent's LATERAL velocity in m/s (positive = LEFT, same convention as
// LatPos/Kinematics.LatOffset) -- for a pedestrian LUNGING across the lane rather than standing still.
// Engine.AdvanceObstacles dead-reckons LatPos += LatSpeed*dt each step (the same "extrapolate a
// reported velocity between owner corrections" contract Speed uses for FrontPos), and the lateral
// evasion PREDICTS where the agent will be by the time the car reaches it -- so the car can react to a
// lunge faster than its own swerve speed (SwerveMaxLateralSpeed) by dodging to the side the agent is
// vacating. Default 0 == a laterally-static agent: byte-identical to the pre-B6-lat behaviour.
//
// VALUE TYPE (SUMOSHARP-API.md §4.2 / D5): this was a `sealed record` (a heap class) stored in a
// `Dictionary<string, ExternalObstacle>` and rewritten via `record with` on every per-step
// UpdateObstacle -- one heap allocation + one string-hash lookup per obstacle per step. It is now a
// `readonly record struct`: the backing store is a struct-of-arrays keyed by ObstacleHandle
// (ObstacleStore), and this struct is only ever MATERIALISED by value from the store's columns when a
// consumer iterates (`foreach (var o in _obstacles.Values)`), so a read is a stack copy (doubles + two
// string references + a byte) and an update is an in-place column write -- zero heap allocation on both
// paths. Every field the lane-based engine reads (FrontPos/LaneId/Speed/Width/Id/...) is unchanged, so
// the car-following / evasion / junction consumers are byte-identical. Id stays because
// ComputeLateralEvasion uses it as a deterministic tie-break among overlapping obstacles.
//
// B1-RVO (D17): AvoidanceClass is the reserved reciprocity-class byte the future RVO layer consumes
// (see AvoidanceClass.cs). Default OneSided is inert for the lane-based engine.
public readonly record struct ExternalObstacle(
    string Id,
    string LaneId,
    double FrontPos,
    double Length,
    double StartTime,
    double EndTime,
    double Speed = 0.0,
    double MaxDecel = 0.0,
    double LatPos = 0.0,
    double Width = 0.0,
    double LatSpeed = 0.0,
    AvoidanceClass AvoidanceClass = AvoidanceClass.OneSided);
