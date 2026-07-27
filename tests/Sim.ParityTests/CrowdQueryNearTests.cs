using System;
using Sim.Core.Bridge;
using Sim.Core.Orca;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// docs/LIVE-CITY-CAR-YIELDS-PED-DESIGN.md §8: `ICrowdFootprintSource.QueryNear` is the ONLY window a
// vehicle has onto the pedestrian crowd, and every consumer passes a small fixed span
// (`stackalloc WorldDisc[16]` in CrowdYieldConstraint, CrowdLongitudinalConstraint and
// ComputeLateralEvasion's crowd scan). When more movers are in range than fit, WHICH ones survive is the
// whole ball game: a car must never be blind to the pedestrian directly in front of it because sixteen
// irrelevant ones forty metres away happened to be enumerated first.
//
// These tests pin the contract "the span receives the NEAREST movers, ties broken deterministically" for
// all three implementations. Each was written as a failing repro against the previous fill-in-arbitrary-
// order-until-full behaviour, which is what let a car in the 800-ped demo run at 16.5 m/s straight at a
// pedestrian it was structurally unable to see.
public class CrowdQueryNearTests
{
    private readonly ITestOutputHelper _out;
    public CrowdQueryNearTests(ITestOutputHelper output) => _out = output;

    private const double Origin = 0.0;

    // OrcaCrowd walked its agent slots in index order and stopped the moment the span filled, so an agent
    // in a HIGH slot index was invisible however close it was.
    [Fact]
    public void OrcaCrowd_ReturnsTheNearest_NotTheFirstSixteenSlots()
    {
        var crowd = new OrcaCrowd();

        // Slots 0..19: far away (40 m out, spread so they are all distinct distances).
        for (var i = 0; i < 20; i++)
        {
            crowd.Add(new Vec2(40.0 + i, 0.0), 0.3, maxSpeed: 0.0, goal: new Vec2(40.0 + i, 0.0));
        }

        // Slot 20: the pedestrian standing right in front of the car.
        crowd.Add(new Vec2(2.0, 0.0), 0.3, maxSpeed: 0.0, goal: new Vec2(2.0, 0.0));

        Span<WorldDisc> into = stackalloc WorldDisc[16];
        var got = crowd.QueryNear(Origin, Origin, 100.0, into);

        Assert.Equal(16, got);
        var sawTheNearOne = false;
        for (var i = 0; i < got; i++)
        {
            if (Math.Abs(into[i].X - 2.0) < 1e-9) sawTheNearOne = true;
        }

        _out.WriteLine($"OrcaCrowd: got {got} discs, nearest returned x={NearestX(into, got):F1} " +
                       $"(the 2.0 m ped {(sawTheNearOne ? "IS" : "is NOT")} among them)");
        Assert.True(sawTheNearOne,
            "the pedestrian 2 m in front of the query point must survive truncation; it sits in a high " +
            "agent slot, and the previous fill-in-slot-order-until-full behaviour dropped it entirely");

        // Stronger: the returned set must BE the 16 nearest, i.e. nothing beyond the 16th-nearest sneaks in.
        // Here that means the 2.0 m agent plus the 15 closest of the 40+ m ones (x = 40..54).
        for (var i = 0; i < got; i++)
        {
            Assert.True(into[i].X <= 54.0 + 1e-9, $"returned a disc at x={into[i].X} that is not among the 16 nearest");
        }
    }

    // The same defect, one level up: CompositeFootprintSource filled its children IN ORDER, so once the
    // first child (the promoted-ORCA crowd) saturated the span, the second (crossing occupancy) received
    // ZERO slots -- starved exactly in the dense-crowd case it exists for.
    [Fact]
    public void Composite_DoesNotStarveLaterChildren_WhenTheFirstOneSaturates()
    {
        var busy = new OrcaCrowd();
        for (var i = 0; i < 30; i++)
        {
            busy.Add(new Vec2(30.0 + i, 0.0), 0.3, maxSpeed: 0.0, goal: new Vec2(30.0 + i, 0.0));
        }

        // The second child holds the one that actually matters: a mover 1.5 m from the query point.
        var critical = new OrcaCrowd();
        critical.Add(new Vec2(1.5, 0.0), 0.3, maxSpeed: 0.0, goal: new Vec2(1.5, 0.0));

        var composite = new CompositeFootprintSource(busy, critical);
        Span<WorldDisc> into = stackalloc WorldDisc[16];
        var got = composite.QueryNear(Origin, Origin, 100.0, into);

        var sawCritical = false;
        for (var i = 0; i < got; i++)
        {
            if (Math.Abs(into[i].X - 1.5) < 1e-9) sawCritical = true;
        }

        _out.WriteLine($"Composite: got {got} discs; the 1.5 m mover from the SECOND child " +
                       $"{(sawCritical ? "IS" : "is NOT")} among them");
        Assert.True(sawCritical,
            "a nearby mover in a later child must not be starved by an earlier child saturating the span");
    }

    // A single child must still behave exactly as it does unwrapped (no reordering surprise for the
    // common one-source wiring).
    [Fact]
    public void Composite_WithOneChild_MatchesThatChildDirectly()
    {
        var crowd = new OrcaCrowd();
        for (var i = 0; i < 25; i++)
        {
            crowd.Add(new Vec2(5.0 + i, i % 3), 0.3, maxSpeed: 0.0, goal: new Vec2(5.0 + i, i % 3));
        }

        Span<WorldDisc> direct = stackalloc WorldDisc[16];
        Span<WorldDisc> viaComposite = stackalloc WorldDisc[16];
        var a = crowd.QueryNear(Origin, Origin, 100.0, direct);
        var b = new CompositeFootprintSource(crowd).QueryNear(Origin, Origin, 100.0, viaComposite);

        Assert.Equal(a, b);
        for (var i = 0; i < a; i++)
        {
            Assert.Equal(direct[i].X, viaComposite[i].X);
            Assert.Equal(direct[i].Y, viaComposite[i].Y);
        }
    }

    // Determinism: the same query must give the same set in the same order, every time -- the crowd path
    // has to stay reproducible run-to-run even though it is off the golden path.
    [Fact]
    public void QueryNear_IsDeterministic_IncludingExactDistanceTies()
    {
        var crowd = new OrcaCrowd();
        // Four exact ties at each radius, so the tie-break (agent slot order) is exercised, not luck.
        for (var ring = 1; ring <= 8; ring++)
        {
            crowd.Add(new Vec2(ring, 0.0), 0.3, maxSpeed: 0.0, goal: new Vec2(ring, 0.0));
            crowd.Add(new Vec2(-ring, 0.0), 0.3, maxSpeed: 0.0, goal: new Vec2(-ring, 0.0));
            crowd.Add(new Vec2(0.0, ring), 0.3, maxSpeed: 0.0, goal: new Vec2(0.0, ring));
            crowd.Add(new Vec2(0.0, -ring), 0.3, maxSpeed: 0.0, goal: new Vec2(0.0, -ring));
        }

        Span<WorldDisc> first = stackalloc WorldDisc[10];
        Span<WorldDisc> second = stackalloc WorldDisc[10];
        var a = crowd.QueryNear(Origin, Origin, 100.0, first);
        var b = crowd.QueryNear(Origin, Origin, 100.0, second);

        Assert.Equal(a, b);
        for (var i = 0; i < a; i++)
        {
            Assert.Equal(first[i].X, second[i].X);
            Assert.Equal(first[i].Y, second[i].Y);
        }

        // And every returned disc must be within the nearest ring band that fits: 10 slots over rings of
        // four means rings 1 and 2 in full, plus two from ring 3 -- nothing from ring 4 or beyond.
        for (var i = 0; i < a; i++)
        {
            var d = Math.Sqrt((first[i].X * first[i].X) + (first[i].Y * first[i].Y));
            Assert.True(d <= 3.0 + 1e-9, $"returned a disc at distance {d:F2}, past the 10 nearest");
        }
    }

    private static double NearestX(ReadOnlySpan<WorldDisc> discs, int count)
    {
        var best = double.PositiveInfinity;
        for (var i = 0; i < count; i++) best = Math.Min(best, Math.Abs(discs[i].X));
        return best;
    }
}
