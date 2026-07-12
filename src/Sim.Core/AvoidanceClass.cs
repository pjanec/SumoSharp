namespace Sim.Core;

// Coordinated with LANELESS-DIRECTION.md (§15 B1) / SUMOSHARP-API.md (D17): a per-obstacle avoidance
// class the future RVO/ORCA lateral layer reads to pick each agent's reciprocity `share` -- how much
// of the mutual avoidance THIS agent takes responsibility for. It is INERT for the lane-based engine:
// ObstacleConstraint / ComputeLateralEvasion make a car brake or swerve behind an obstacle regardless
// of this value, so every existing scenario is byte-identical. Reserved now (a single byte column in
// ObstacleStore) so the store never changes shape when the laneless Stage 3 lands and consumes it via
// the neutral RvoNeighbor abstraction.
//
// Default is OneSided == 0, so a default-initialised row and every AddObstacle call that omits the
// argument is one-sided -- the safe assumption for a "dumb" blocker the RVO agents must fully avoid.
public enum AvoidanceClass : byte
{
    // The agent does not move to avoid others; the RVO solver gives the OTHER side the full share
    // (a live blocker -- a stopped car, a detection -- that everyone else goes around). The default.
    OneSided = 0,

    // A fixed, immovable blocker: treated like OneSided by the avoidance (others take the full share),
    // but flagged distinctly so a solver can also skip predicting its motion entirely.
    StaticBlocker = 1,

    // A cooperative agent (a navmesh/RVO crowd agent, or a SUMO vehicle in the unified solve) that
    // avoids reciprocally: the solver splits the avoidance, share ~0.5 each.
    Reciprocal = 2,
}
