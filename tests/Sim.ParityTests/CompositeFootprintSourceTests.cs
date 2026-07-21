using System;
using Sim.Core.Bridge;
using Xunit;

namespace Sim.ParityTests;

// CompositeFootprintSource (docs/LIVE-CITY-CROSSING-YIELD-DESIGN.md P2-T1): fans one CrowdSource query out
// to several children and concatenates their discs, never overflowing the caller's span.
public class CompositeFootprintSourceTests
{
    // A trivial source that always returns one disc at a fixed spot.
    private sealed class OneDisc : ICrowdFootprintSource
    {
        private readonly WorldDisc _d;
        public OneDisc(double x) => _d = new WorldDisc(x, 0, 0, 0, 0.3);

        public int QueryNear(double x, double y, double radius, Span<WorldDisc> into)
        {
            if (into.Length == 0) return 0;
            into[0] = _d;
            return 1;
        }
    }

    [Fact]
    public void QueryNear_ConcatenatesChildren()
    {
        var composite = new CompositeFootprintSource(new OneDisc(1.0), new OneDisc(2.0));
        Span<WorldDisc> into = new WorldDisc[8];

        var n = composite.QueryNear(0, 0, 100, into);

        Assert.Equal(2, n);
        Assert.Equal(1.0, into[0].X);
        Assert.Equal(2.0, into[1].X);
    }

    [Fact]
    public void QueryNear_NeverOverflowsTheSpan()
    {
        var composite = new CompositeFootprintSource(new OneDisc(1.0), new OneDisc(2.0), new OneDisc(3.0));
        Span<WorldDisc> into = new WorldDisc[2]; // room for only two of the three

        var n = composite.QueryNear(0, 0, 100, into);

        Assert.Equal(2, n);
        Assert.Equal(1.0, into[0].X);
        Assert.Equal(2.0, into[1].X);
    }

    [Fact]
    public void QueryNear_EmptyComposite_ReturnsZero()
    {
        var composite = new CompositeFootprintSource();
        Span<WorldDisc> into = new WorldDisc[4];
        Assert.Equal(0, composite.QueryNear(0, 0, 100, into));
    }
}
