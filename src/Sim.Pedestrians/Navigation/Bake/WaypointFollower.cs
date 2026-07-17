using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation.Bake;

// ILocalSteering: steers toward path[waypointIndex], advancing the caller-held cursor once within
// arriveRadius, easing speed toward zero on the final waypoint so the agent settles instead of
// oscillating around the goal (docs/PEDESTRIAN-DESIGN.md §4). Pure function of its arguments plus
// the `ref waypointIndex` it owns -- no System.Random, no hidden state -- so it is deterministic
// and safe to call from any (single- or multi-threaded) driver.
public sealed class WaypointFollower : ILocalSteering
{
    // How many arrive-radii of remaining distance the final-waypoint speed ease ramps over.
    private const double FinalEaseRadii = 3.0;

    public Vec2 DesiredVelocity(
        Vec2 position,
        IReadOnlyList<Vec2> path,
        ref int waypointIndex,
        double maxSpeed,
        double arriveRadius)
    {
        if (path.Count == 0)
        {
            waypointIndex = 0;
            return Vec2.Zero;
        }

        // Advance past every waypoint already reached. A loop (not a single "if") so a long dt or
        // a coarse path can legitimately skip more than one waypoint in one call; still a pure
        // function of `position`, so still deterministic.
        while (waypointIndex < path.Count && (path[waypointIndex] - position).Abs <= arriveRadius)
        {
            waypointIndex++;
        }

        if (waypointIndex >= path.Count)
        {
            return Vec2.Zero; // path complete
        }

        var target = path[waypointIndex];
        var toTarget = target - position;
        var dist = toTarget.Abs;
        if (dist <= 1e-9)
        {
            return Vec2.Zero;
        }

        var speed = maxSpeed;
        var isFinalWaypoint = waypointIndex == path.Count - 1;
        if (isFinalWaypoint)
        {
            var easeDistance = arriveRadius * FinalEaseRadii;
            if (easeDistance > 0.0 && dist < easeDistance)
            {
                speed = maxSpeed * Math.Clamp(dist / easeDistance, 0.0, 1.0);
            }
        }

        return (toTarget / dist) * speed;
    }
}
