namespace Sim.Evac;

// PANIC-EVAC.md R3 / §8.1: the localized security incident (bomb, shooting, armed strike) — the
// single external trigger. A world point, a start time, and an influence radius. Phase 1 models fear
// as a pure function of distance to this point (radius-only); Phase 2 layers contagion + line-of-sight
// + jam-unease on top (still local-information only, never a global broadcast).
public readonly record struct Incident(double X, double Y, double StartTime, double Radius)
{
    // The incident has not "gone off" until its start time — before then fear is zero everywhere.
    public bool IsActive(double time) => time >= StartTime;

    // Radius-only fear (Phase 1): 1.0 at the epicentre, falling linearly to 0.0 at the radius,
    // and 0.0 beyond it or before the incident starts. A distant actor therefore feels nothing —
    // which is exactly R3's "distant traffic stays organized, unaware, just jammed".
    public double FearAt(double x, double y, double time)
    {
        if (time < StartTime)
        {
            return 0.0;
        }

        var dist = DistanceTo(x, y);
        return dist >= Radius ? 0.0 : 1.0 - dist / Radius;
    }

    public double DistanceTo(double x, double y)
    {
        var dx = x - X;
        var dy = y - Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
