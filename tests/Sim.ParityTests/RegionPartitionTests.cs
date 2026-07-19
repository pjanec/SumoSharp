using System;
using System.Collections.Generic;
using Sim.Core.Orca;
using Xunit;

namespace Sim.ParityTests;

// P6-2-1 (docs/PEDESTRIAN-P6-2-REGION-DESIGN.md §6): RegionPartition is the spatial-region bucketing that
// P6-2 will use to hand OrcaCrowd.Step cache-local work. These tests hold it to its success condition:
// it partitions the alive agents EXACTLY ONCE, each agent lands in the region a brute-force FloorDiv oracle
// puts it in, dead agents are excluded, agent lists are ascending, movement re-buckets correctly, and the
// result is deterministic. It is a pure partitioning structure (no solve), so this is a plain data-structure
// gate, hermetic (no SUMO, no DDS).
public class RegionPartitionTests
{
    // A deterministic spread that guarantees several regions, several agents per region (shared cells), and
    // some negative coordinates (exercises FloorDiv's floor-toward-negative-infinity). No System.Random.
    private static Vec2[] MakePositions(int n)
    {
        var pts = new Vec2[n];
        for (var i = 0; i < n; i++)
        {
            // Spread across ~[-30, +30] on both axes with clustering so multiple agents share region cells.
            var x = ((i * 37) % 61) - 30 + (i % 3) * 0.25;
            var y = ((i * 53) % 61) - 30 + (i % 4) * 0.25;
            pts[i] = new Vec2(x, y);
        }

        return pts;
    }

    private static int FloorDiv(double v, double cell) => (int)Math.Floor(v / cell);

    // Brute-force oracle: map each alive agent to its (cx,cy) region cell independently of RegionPartition.
    private static Dictionary<(int, int), List<int>> Oracle(Vec2[] pts, bool[] alive, int count, double size)
    {
        var map = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < count; i++)
        {
            if (!alive[i])
            {
                continue;
            }

            var cell = (FloorDiv(pts[i].X, size), FloorDiv(pts[i].Y, size));
            if (!map.TryGetValue(cell, out var list))
            {
                list = new List<int>();
                map[cell] = list;
            }

            list.Add(i);
        }

        return map;
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(10.0)]
    [InlineData(21.0)] // a size that is NOT a divisor of the spread, to catch off-by-one cell math
    public void Partitions_AliveAgents_ExactlyOnce_MatchingTheOracle(double regionSize)
    {
        const int n = 200;
        var pts = MakePositions(n);
        var alive = new bool[n];
        for (var i = 0; i < n; i++)
        {
            alive[i] = i % 5 != 0; // ~20% dead, interspersed
        }

        var part = new RegionPartition();
        part.Build(pts, alive, n, regionSize);

        var oracle = Oracle(pts, alive, n, regionSize);

        // Same number of non-empty regions as the oracle has occupied cells.
        Assert.Equal(oracle.Count, part.RegionCount);

        // Every agent appears exactly once across all regions, and only alive agents appear.
        var seen = new HashSet<int>();
        var total = 0;
        for (var r = 0; r < part.RegionCount; r++)
        {
            var agents = part.AgentsInRegion(r);

            // Ascending-by-index within a region.
            for (var k = 1; k < agents.Length; k++)
            {
                Assert.True(agents[k] > agents[k - 1], "region agent list must be strictly ascending by index");
            }

            // All agents in a region share the same oracle cell, and that cell's oracle members match exactly.
            Assert.True(agents.Length > 0, "RegionPartition must not expose empty regions");
            var cell0 = (FloorDiv(pts[agents[0]].X, regionSize), FloorDiv(pts[agents[0]].Y, regionSize));
            var oracleMembers = oracle[cell0];
            Assert.Equal(oracleMembers.Count, agents.Length);
            for (var k = 0; k < agents.Length; k++)
            {
                var a = agents[k];
                Assert.True(alive[a], "a dead agent must never be bucketed");
                Assert.True(seen.Add(a), $"agent {a} appeared in more than one region");
                Assert.Equal(oracleMembers[k], a); // same members, same ascending order
                var cell = (FloorDiv(pts[a].X, regionSize), FloorDiv(pts[a].Y, regionSize));
                Assert.Equal(cell0, cell);
            }

            total += agents.Length;
        }

        // Exactly the alive count, no more, no fewer.
        var aliveCount = 0;
        for (var i = 0; i < n; i++)
        {
            if (alive[i]) aliveCount++;
        }

        Assert.Equal(aliveCount, total);
        Assert.Equal(aliveCount, seen.Count);
    }

    [Fact]
    public void RegionOf_MatchesTheBucketAnAgentLandsIn()
    {
        const int n = 120;
        const double size = 8.0;
        var pts = MakePositions(n);
        var alive = new bool[n];
        Array.Fill(alive, true);

        var part = new RegionPartition();
        part.Build(pts, alive, n, size);

        for (var r = 0; r < part.RegionCount; r++)
        {
            foreach (var a in part.AgentsInRegion(r))
            {
                Assert.Equal(r, part.RegionOf(pts[a])); // an agent's position resolves to its own region
            }
        }
    }

    [Fact]
    public void ReBuild_AfterMovement_ReBucketsAcrossRegionBoundaries()
    {
        const int n = 40;
        const double size = 10.0;
        var pts = MakePositions(n);
        var alive = new bool[n];
        Array.Fill(alive, true);

        var part = new RegionPartition();
        part.Build(pts, alive, n, size);

        // Record agent 0's region CELL and the set of cell-mates it was grouped with, then move it far into a
        // different region cell and rebuild from the new frozen positions. (Its region INDEX stays 0 -- it is
        // always seen first -- so the meaningful check is the cell + who it is grouped with, not the index.)
        var cellBefore = (FloorDiv(pts[0].X, size), FloorDiv(pts[0].Y, size));
        var matesBefore = new HashSet<int>();
        foreach (var a in part.AgentsInRegion(part.RegionOf(pts[0])))
        {
            matesBefore.Add(a);
        }

        pts[0] = new Vec2(pts[0].X + 3 * size, pts[0].Y + 3 * size);
        part.Build(pts, alive, n, size);
        var cellAfter = (FloorDiv(pts[0].X, size), FloorDiv(pts[0].Y, size));

        Assert.NotEqual(cellBefore, cellAfter); // it genuinely crossed region-cell boundaries

        // Agent 0 is present exactly once, in the region its NEW position resolves to, and it is no longer
        // grouped with its old cell-mates (it re-bucketed to the new cell).
        var region0 = part.RegionOf(pts[0]);
        var occurrences = 0;
        for (var r = 0; r < part.RegionCount; r++)
        {
            foreach (var a in part.AgentsInRegion(r))
            {
                if (a == 0)
                {
                    occurrences++;
                    Assert.Equal(region0, r);
                }
            }
        }

        Assert.Equal(1, occurrences);

        // Its new region's members are exactly the new-cell oracle members (so it moved, it didn't drag its
        // old neighbours along).
        var newCellMates = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if ((FloorDiv(pts[i].X, size), FloorDiv(pts[i].Y, size)) == cellAfter)
            {
                newCellMates.Add(i);
            }
        }

        Assert.True(part.AgentsInRegion(region0).SequenceEqual(newCellMates.ToArray()),
            "agent 0's post-move region must hold exactly the agents sharing its new cell");
    }

    [Fact]
    public void Build_IsDeterministic_SameInputSamePartition()
    {
        const int n = 150;
        const double size = 7.0;
        var pts = MakePositions(n);
        var alive = new bool[n];
        for (var i = 0; i < n; i++)
        {
            alive[i] = i % 3 != 0;
        }

        var a = new RegionPartition();
        var b = new RegionPartition();
        a.Build(pts, alive, n, size);
        b.Build(pts, alive, n, size);

        Assert.Equal(a.RegionCount, b.RegionCount);
        for (var r = 0; r < a.RegionCount; r++)
        {
            Assert.True(a.AgentsInRegion(r).SequenceEqual(b.AgentsInRegion(r)),
                "same input must yield an identical region partition (region order + membership)");
        }
    }

    [Fact]
    public void Build_RejectsNonPositiveRegionSize()
    {
        var part = new RegionPartition();
        Assert.Throws<ArgumentOutOfRangeException>(() => part.Build(new Vec2[1], new bool[1], 1, 0.0));
    }
}
