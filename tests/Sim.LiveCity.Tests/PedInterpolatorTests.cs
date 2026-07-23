using System;
using System.Collections.Generic;
using Sim.LiveCity;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// Ped-smoothing fix (docs/LIVE-CITY-VISUALS-NOTES.md-adjacent task) -- PedInterpolator's unit-level
// contract: linear-by-id interpolation between the two bracketing pushed snapshots, hold at the ends,
// new-id-appears-at-later-position, missing-id-is-dropped, and determinism (no hidden clock/RNG).
public class PedInterpolatorTests
{
    private readonly ITestOutputHelper _output;

    public PedInterpolatorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static PedInterpFrame Ped(int id, double x, double y, PedRegime regime = PedRegime.LowPowerWalking, string tag = "walk")
        => new(id, x, y, regime, tag);

    [Fact]
    public void Sample_MidBracket_LinearlyInterpolatesById()
    {
        var interp = new PedInterpolator();
        interp.Push(0.0, new[] { Ped(1, 0.0, 0.0) });
        interp.Push(0.5, new[] { Ped(1, 1.0, 0.0) });

        var mid = interp.Sample(0.25);

        Assert.Single(mid);
        Assert.Equal(1, mid[0].Id);
        Assert.Equal(0.5, mid[0].X, precision: 9);
        Assert.Equal(0.0, mid[0].Y, precision: 9);

        _output.WriteLine($"Sample(0.25) for ped 1 moving (0,0)->(1,0) over [0,0.5]: X={mid[0].X:F6} Y={mid[0].Y:F6}");
    }

    [Fact]
    public void Sample_AcrossFullBracket_IsMonotonicAndContinuous()
    {
        var interp = new PedInterpolator();
        interp.Push(0.0, new[] { Ped(1, 0.0, 0.0) });
        interp.Push(0.5, new[] { Ped(1, 1.0, 0.0) });

        var times = new[] { 0.0, 0.1, 0.2, 0.25, 0.3, 0.4, 0.5 };
        var xs = new List<double>();
        foreach (var t in times)
        {
            var s = interp.Sample(t);
            xs.Add(s[0].X);
        }

        _output.WriteLine("Sub-tick samples for ped 1, X over t in [0,0.5]:");
        for (var i = 0; i < times.Length; i++)
        {
            _output.WriteLine($"  t={times[i]:F2}  X={xs[i]:F6}");
        }

        for (var i = 1; i < xs.Count; i++)
        {
            Assert.True(xs[i] > xs[i - 1], $"expected strictly increasing X, got {xs[i - 1]} then {xs[i]} at t={times[i]}");
        }

        // Exact linear-interpolation check: X(t) == t/0.5 for this straight-line case.
        for (var i = 0; i < times.Length; i++)
        {
            Assert.Equal(times[i] / 0.5, xs[i], precision: 9);
        }
    }

    [Fact]
    public void Sample_NewPedOnlyInLaterSnapshot_AppearsAtLaterPosition()
    {
        var interp = new PedInterpolator();
        interp.Push(0.0, new[] { Ped(1, 0.0, 0.0) });
        interp.Push(0.5, new[] { Ped(1, 1.0, 0.0), Ped(2, 5.0, 5.0) });

        // At and after its own snapshot's time, ped 2 is present at its (fixed, un-lerped) later position --
        // there is no earlier data for it to interpolate from.
        var atLater = interp.Sample(0.5);
        var afterLater = interp.Sample(0.6); // past the newest snapshot -> holds newest verbatim.
        Assert.Contains(atLater, p => p.Id == 2 && p.X == 5.0 && p.Y == 5.0);
        Assert.Contains(afterLater, p => p.Id == 2 && p.X == 5.0 && p.Y == 5.0);

        _output.WriteLine($"ped 2 (new-only-in-later) at t=0.5: present={atLater.Count == 2}; at t=0.6: present={afterLater.Count == 2}");
    }

    [Fact]
    public void Sample_PedGoneInLaterSnapshot_IsDropped()
    {
        var interp = new PedInterpolator();
        interp.Push(0.0, new[] { Ped(1, 0.0, 0.0), Ped(2, 5.0, 5.0) });
        interp.Push(0.5, new[] { Ped(1, 1.0, 0.0) }); // ped 2 despawned

        var mid = interp.Sample(0.25);
        Assert.Single(mid);
        Assert.Equal(1, mid[0].Id);
    }

    [Fact]
    public void Sample_BeforeOldestOrAtOrAfterNewest_HoldsTheNearestEnd()
    {
        var interp = new PedInterpolator();
        interp.Push(1.0, new[] { Ped(1, 0.0, 0.0) });
        interp.Push(1.5, new[] { Ped(1, 1.0, 0.0) });

        var before = interp.Sample(0.0);
        Assert.Equal(0.0, before[0].X, precision: 9);

        var atNewest = interp.Sample(1.5);
        Assert.Equal(1.0, atNewest[0].X, precision: 9);

        var afterNewest = interp.Sample(10.0);
        Assert.Equal(1.0, afterNewest[0].X, precision: 9);
    }

    [Fact]
    public void Sample_IsDeterministic_AcrossRepeatedCallsAndInstances()
    {
        var a = new PedInterpolator();
        var b = new PedInterpolator();
        var frames = new (double Time, PedInterpFrame[] Peds)[]
        {
            (0.0, new[] { Ped(1, 0.0, 0.0), Ped(2, 10.0, 0.0) }),
            (0.5, new[] { Ped(1, 1.0, 2.0), Ped(2, 11.0, -1.0), Ped(3, 20.0, 20.0) }),
            (1.0, new[] { Ped(1, 2.0, 4.0), Ped(3, 21.0, 21.0) }),
        };

        foreach (var (t, peds) in frames)
        {
            a.Push(t, peds);
            b.Push(t, peds);
        }

        foreach (var qt in new[] { 0.1, 0.37, 0.5, 0.75, 0.999 })
        {
            var sa1 = a.Sample(qt);
            var sa2 = a.Sample(qt); // repeated call, same instance
            var sb = b.Sample(qt); // separate instance, identical inputs

            Assert.Equal(sa1.Count, sa2.Count);
            Assert.Equal(sa1.Count, sb.Count);
            for (var i = 0; i < sa1.Count; i++)
            {
                Assert.Equal(sa1[i].X, sa2[i].X, precision: 12);
                Assert.Equal(sa1[i].X, sb[i].X, precision: 12);
                Assert.Equal(sa1[i].Y, sa2[i].Y, precision: 12);
                Assert.Equal(sa1[i].Y, sb[i].Y, precision: 12);
            }
        }
    }

    [Fact]
    public void Sample_HugeDelta_SnapsInsteadOfLerping()
    {
        var interp = new PedInterpolator { SnapDistanceMeters = 5.0 };
        interp.Push(0.0, new[] { Ped(1, 0.0, 0.0) });
        interp.Push(0.5, new[] { Ped(1, 500.0, 0.0) }); // a respawn/route-jump-sized gap

        var mid = interp.Sample(0.25);
        Assert.Equal(500.0, mid[0].X, precision: 9); // snapped to the later position, not lerped to 250
    }
}
