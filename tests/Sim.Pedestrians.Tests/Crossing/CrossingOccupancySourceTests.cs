using System;
using System.Collections.Generic;
using Sim.Core.Bridge;
using Sim.Core.Orca;
using Sim.Pedestrians.Crossing;
using Sim.Pedestrians.Navigation.Bake;
using Xunit;

namespace Sim.Pedestrians.Tests.Crossing;

// CrossingOccupancySource (docs/LIVE-CITY-CROSSING-YIELD-DESIGN.md P2-T3): a low-power ped standing on a
// crosswalk becomes a virtual gate disc a car can see, WITHOUT promoting the ped. Verifies the occupancy
// logic (in/out of the polygon), the QueryNear radius filter, and the empty fast-path.
public class CrossingOccupancySourceTests
{
    // One 4x4 m square "crossing" polygon at the origin.
    private static CrossingOccupancySource BuildOneCrossing() =>
        new(new[]
        {
            new BakedPolygon(
                Index: 0, Id: ":j_c0", Kind: BakedPolygonKind.Crossing,
                Vertices: new[] { new Vec2(0, 0), new Vec2(4, 0), new Vec2(4, 4), new Vec2(0, 4) }),
        }, pedRadius: 0.3);

    [Fact]
    public void PedOnCrossing_BecomesAGateDisc_VisibleToAQueryNearby()
    {
        var src = BuildOneCrossing();
        Assert.Equal(1, src.CrossingCount);

        src.Update(new List<Vec2> { new(2, 2) }); // ped in the middle of the crossing
        Assert.Equal(1, src.OccupiedCount);

        Span<WorldDisc> into = new WorldDisc[8];
        var n = src.QueryNear(2, 2, 5, into);
        Assert.Equal(1, n);
        Assert.Equal(2.0, into[0].X);
        Assert.Equal(2.0, into[0].Y);
        Assert.Equal(0.0, into[0].Vx); // a stopped "closed gate"
        Assert.Equal(0.0, into[0].Vy);
    }

    [Fact]
    public void PedOffTheCrossing_ProducesNoGate()
    {
        var src = BuildOneCrossing();
        src.Update(new List<Vec2> { new(10, 10) }); // well outside the polygon
        Assert.Equal(0, src.OccupiedCount);

        Span<WorldDisc> into = new WorldDisc[8];
        Assert.Equal(0, src.QueryNear(10, 10, 5, into));
    }

    [Fact]
    public void QueryFarFromAnOccupiedCrossing_ReturnsNothing()
    {
        var src = BuildOneCrossing();
        src.Update(new List<Vec2> { new(2, 2) });

        Span<WorldDisc> into = new WorldDisc[8];
        Assert.Equal(0, src.QueryNear(100, 100, 1, into)); // gate exists, but not near this car
    }

    [Fact]
    public void EmptyUpdate_FastPathReturnsZero()
    {
        var src = BuildOneCrossing();
        src.Update(Array.Empty<Vec2>());
        Assert.Equal(0, src.OccupiedCount);

        Span<WorldDisc> into = new WorldDisc[8];
        Assert.Equal(0, src.QueryNear(2, 2, 5, into));
    }
}
