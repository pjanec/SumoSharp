namespace Sim.Core.Bridge;

// Combines several ICrowdFootprintSource children into one, for wiring into Engine.CrowdSource. Used by
// the live-city coupling to make a car yield to BOTH promoted (high-power ORCA) pedestrians AND
// low-power pedestrians occupying a crosswalk (the CrossingOccupancySource) -- two sources, one seam.
//
// QueryNear fans the query out to each child and concatenates their discs into `into`, stopping when the
// span is full (never writing more than the span length, per the ICrowdFootprintSource contract). Cheap:
// the cost a vehicle pays is the sum of the children's own QueryNear, each of which is expected to be
// O(nearby movers) with a fast empty path. Zero-alloc; side-effect-free (children read frozen state).
public sealed class CompositeFootprintSource : ICrowdFootprintSource
{
    private readonly ICrowdFootprintSource[] _sources;

    public CompositeFootprintSource(params ICrowdFootprintSource[] sources)
    {
        _sources = sources ?? System.Array.Empty<ICrowdFootprintSource>();
    }

    public int QueryNear(double x, double y, double radius, System.Span<WorldDisc> into)
    {
        var n = 0;
        foreach (var source in _sources)
        {
            if (n >= into.Length)
            {
                break;
            }

            n += source.QueryNear(x, y, radius, into[n..]);
        }

        return n;
    }
}
