namespace Sim.Core.Bridge;

// Combines several ICrowdFootprintSource children into one, for wiring into Engine.CrowdSource. Used by
// the live-city coupling to make a car yield to BOTH promoted (high-power ORCA) pedestrians AND
// low-power pedestrians occupying a crosswalk (the CrossingOccupancySource) -- two sources, one seam.
//
// QueryNear fans the query out to each child and MERGES their discs into `into`, keeping the nearest
// across all of them (ICrowdFootprintSource.QueryNear's contract). It used to CONCATENATE, filling `into`
// child by child and stopping when full -- which meant that once the first child (the promoted-ORCA crowd)
// saturated the span, the second (crossing occupancy) received ZERO slots. It was starved precisely in the
// dense-crowd case it exists for. Cost is the sum of the children's own QueryNear plus an O(k) insertion
// per returned disc, k = the caller's span length. Zero-alloc for spans up to 64 (every current consumer
// passes 16); side-effect-free (children read frozen state).
public sealed class CompositeFootprintSource : ICrowdFootprintSource
{
    private readonly ICrowdFootprintSource[] _sources;

    public CompositeFootprintSource(params ICrowdFootprintSource[] sources)
    {
        _sources = sources ?? System.Array.Empty<ICrowdFootprintSource>();
    }

    public int QueryNear(double x, double y, double radius, System.Span<WorldDisc> into)
    {
        if (_sources.Length == 0 || into.Length == 0)
        {
            return 0;
        }

        // One child: hand it the caller's span directly, so the single-source wiring is exactly the child's
        // own behaviour (no copy, no reordering).
        if (_sources.Length == 1)
        {
            return _sources[0].QueryNear(x, y, radius, into);
        }

        System.Span<WorldDisc> scratch = into.Length <= 64
            ? stackalloc WorldDisc[64]
            : new WorldDisc[into.Length];
        scratch = scratch[..into.Length];

        var n = 0;
        foreach (var source in _sources)
        {
            // Each child is asked for its OWN nearest `into.Length`; merging those keeps the nearest
            // overall, because a disc a child dropped was already beaten by that child's own kept set.
            var got = source.QueryNear(x, y, radius, scratch);
            for (var i = 0; i < got; i++)
            {
                n = WorldDiscQuery.InsertNearest(into, n, scratch[i], x, y);
            }
        }

        return n;
    }
}
