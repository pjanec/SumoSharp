namespace Sim.Core.Bridge;

// The cross-regime bridge's neutral WORLD-SPACE footprint primitive (docs/LANELESS-DIRECTION.md,
// "the cross-regime bridge"). It unifies the two laneless regimes -- lane-derived vehicles (1D
// lateral feasible-interval solve, lane-relative) and open-space crowd agents (2D holonomic ORCA,
// world-relative) -- by expressing every mover to the OTHER regime as the same thing: a moving disc
// in absolute world coordinates. A vehicle projects to a disc (or a short chain of them along its
// length); a crowd agent already IS a disc. Deliberately value-typed and id-free: no strings, no
// dictionary, so it scales to many agents and never depends on the string-keyed ExternalObstacle
// API the owner is replacing.
public readonly struct WorldDisc
{
    public readonly double X;        // world position
    public readonly double Y;
    public readonly double Vx;       // world velocity (dead-reckoned each step by the owner)
    public readonly double Vy;
    public readonly double Radius;

    public WorldDisc(double x, double y, double vx, double vy, double radius)
    {
        X = x;
        Y = y;
        Vx = vx;
        Vy = vy;
        Radius = radius;
    }
}

// A source of nearby world-space footprint discs, queried by ONE regime to discover the other
// regime's movers around a point. Zero-alloc: the caller supplies the destination span and the
// implementation fills it, returning the count written (never more than the span length). This is
// the frozen seam between the two regimes -- the lane engine consults it (crowd agents near a
// vehicle) and the crowd consults the vehicles' disc list, both via the same WorldDisc shape.
public interface ICrowdFootprintSource
{
    // Fill `into` with movers whose centre is within `radius` of (x, y). Returns the count written.
    // Reads FROZEN start-of-step state only (the caller relies on it being side-effect-free).
    int QueryNear(double x, double y, double radius, Span<WorldDisc> into);
}
