namespace Sim.Core.Bridge;

// The shared bounded "keep the nearest" accumulator every ICrowdFootprintSource.QueryNear implementation
// uses, so they cannot drift apart on the one property their callers depend on.
//
// WHY THIS EXISTS (docs/LIVE-CITY-CAR-YIELDS-PED-DESIGN.md §8): QueryNear is the ONLY window a vehicle has
// onto the pedestrian crowd, and every consumer passes a small fixed span (`stackalloc WorldDisc[16]` in
// CrowdYieldConstraint, CrowdLongitudinalConstraint, and ComputeLateralEvasion's crowd scan). The original
// implementations filled that span in enumeration order and stopped when it was full, which is fine at a
// handful of agents and silently catastrophic at demo density: measured at 800 pedestrians, a car had far
// more than 16 of them inside its ~66 m query radius, so WHICH sixteen it saw was decided by agent slot
// index -- and the pedestrian directly in front of the bumper was routinely not among them. That is how a
// car ended up doing 16.5 m/s straight at a pedestrian the yield guard was, on paper, watching for.
//
// Zero-alloc and deterministic: distances are recomputed from the discs already in the result set rather
// than kept in a parallel array, and ties keep the INCUMBENT, so the result is a stable "nearest k in
// enumeration order among equals" -- reproducible run to run and independent of thread scheduling.
public static class WorldDiscQuery
{
    // Offer `candidate` to a nearest-first result set holding `count` discs in `into`. Returns the new
    // count. O(count) worst case, and count is the caller's span length (16 in every current consumer).
    public static int InsertNearest(Span<WorldDisc> into, int count, in WorldDisc candidate, double x, double y)
    {
        if (into.Length == 0)
        {
            return 0;
        }

        var dSq = DistanceSquared(candidate, x, y);

        // Full, and no closer than the worst we already hold -> drop. `>=` (not `>`) is what makes ties
        // keep the incumbent, i.e. what makes the tie-break enumeration-stable.
        if (count == into.Length && dSq >= DistanceSquared(into[count - 1], x, y))
        {
            return count;
        }

        // Shift the strictly-farther tail right by one and drop `candidate` into the hole. When the set is
        // already full this overwrites the last (farthest) entry, which the guard above proved is beaten.
        var i = count < into.Length ? count : into.Length - 1;
        while (i > 0 && DistanceSquared(into[i - 1], x, y) > dSq)
        {
            into[i] = into[i - 1];
            i--;
        }

        into[i] = candidate;
        return count < into.Length ? count + 1 : count;
    }

    private static double DistanceSquared(in WorldDisc disc, double x, double y)
    {
        var dx = disc.X - x;
        var dy = disc.Y - y;
        return (dx * dx) + (dy * dy);
    }
}
