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
// per returned disc, k = the caller's span length. Side-effect-free (children read frozen state).
//
// PERF (docs/LIVE-CITY-PERF-SESSION-LOG.md, A13): the scratch threshold MUST cover the largest span any
// consumer passes, or the fallback heap-allocates one WorldDisc[] PER QUERY PER VEHICLE PER STEP. That is
// exactly what happened: this said `<= 64` "every current consumer passes 16", but `Engine.MaxCrowdDiscs`
// was later raised 16 -> 256 (commit f9c837c) and this comment/threshold was never updated, so every
// live-city crowd query fell into `new WorldDisc[256]` (~10 KB). Measured at 507 cars: it was ~92% of the
// WHOLE host's allocation -- engine.plan 471.9 MiB and engine.willPass 94.1 MiB over 60 steps
// (~9.15 MB/step). Keep this threshold >= Engine.MaxCrowdDiscs. 256 * 40 B = 10 KB of stack, the same
// budget each engine call site already stackallocs for its own `discs` span.
public sealed class CompositeFootprintSource : ICrowdFootprintSource
{
    // Must stay >= Engine.MaxCrowdDiscs (256) -- see the header note. Kept as a named constant so the
    // coupling between the two is greppable rather than a bare literal that drifts again.
    private const int ScratchDiscs = 256;

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

        System.Span<WorldDisc> scratch = into.Length <= ScratchDiscs
            ? stackalloc WorldDisc[ScratchDiscs]
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
