namespace Sim.Core;

// B1: a live, non-SUMO obstacle injected onto a lane (DESIGN.md "Two futures" -- a real vehicle,
// pedestrian, or detection fed in from outside any offline SUMO run). FrontPos mirrors a
// vehicle's `pos` convention: it is the obstacle's DOWNSTREAM (front) edge on the lane, so its
// back (the edge a follower must not cross) is FrontPos - Length, exactly like a stopped leader's
// back = leader.Pos - leader.Length in LeaderFollowSpeedConstraint. Active only during
// [StartTime, EndTime) -- both default to "always active" so the common single-argument-set
// AddObstacle call (no start/end) is unconditionally active.
public sealed record ExternalObstacle(
    string Id,
    string LaneId,
    double FrontPos,
    double Length,
    double StartTime,
    double EndTime);
