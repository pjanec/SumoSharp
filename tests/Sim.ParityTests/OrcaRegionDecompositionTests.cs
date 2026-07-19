using System;
using Sim.Core.Orca;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// P6-2-2/3 (docs/PEDESTRIAN-P6-2-REGION-DESIGN.md §4/§6): the load-bearing gate for the region-decomposed
// OrcaCrowd.Step. The hard requirement is BIT-IDENTITY: UseRegionDecomposition must produce EXACTLY the same
// trajectory as the serial path (and as the flat UseParallelStep path), for every agent, every step,
// regardless of region size, region order, thread count, spatial-hash on/off, or the MaxNeighbours/removal
// combination. Region decomposition changes only which agents a worker processes together; the gather and
// per-agent Plan are unchanged, so this must hold to the last bit -- if it ever doesn't, the optimization is
// wrong (or must be gated), never silently accepted (CLAUDE.md rule 3).
public class OrcaRegionDecompositionTests
{
    private const double Dt = 0.25;
    private const double Radius = 0.5;
    private const double MaxSpeed = 1.5;

    private readonly ITestOutputHelper _out;

    public OrcaRegionDecompositionTests(ITestOutputHelper output) => _out = output;

    // Run `build` twice -- serial baseline vs region-decomposed (with the given knobs) -- and assert exact
    // double equality on every agent's position at every step.
    private void AssertRegionMatchesSerial(
        Func<OrcaCrowd> build, int steps, double regionMultiplier, int maxParallelism, bool spatialHash, string label)
    {
        var baseline = build();
        var region = build();
        if (spatialHash)
        {
            baseline.UseSpatialHash = true; // the region path is meant to be paired with the hash; both on.
            region.UseSpatialHash = true;
        }

        region.UseRegionDecomposition = true;
        region.RegionCellSizeMultiplier = regionMultiplier;
        region.MaxParallelism = maxParallelism;

        var n = baseline.Count;
        Assert.True(n > 0 && n == region.Count);
        // The crowd must exceed ParallelStepThreshold (256) or the region path no-ops to serial -- these
        // scenarios are sized above it so the parallel region dispatch is genuinely exercised.
        Assert.True(n >= 256, $"{label}: crowd of {n} is below the parallel threshold; region path would not engage");

        for (var step = 0; step < steps; step++)
        {
            baseline.Step(Dt);
            region.Step(Dt);
            for (var i = 0; i < n; i++)
            {
                var pb = baseline.Position(i);
                var pr = region.Position(i);
                Assert.True(pb.X == pr.X && pb.Y == pr.Y,
                    $"{label}: region-decomp diverged from serial at step {step}, agent {i}: " +
                    $"serial=({pb.X:R},{pb.Y:R}) region=({pr.X:R},{pr.Y:R})");
            }
        }

        _out.WriteLine($"{label}: region-decomp bit-identical to serial over {steps} steps, {n} agents " +
                       $"(mult={regionMultiplier}, maxPar={maxParallelism}, hash={spatialHash})");
    }

    // A large spread crowd (many regions), each agent heading to the opposite corner so paths cross.
    private static OrcaCrowd BuildSpread(int side)
    {
        var crowd = new OrcaCrowd(side * side) { SymmetryBreak = 0.05, NeighbourDist = 4.0 };
        for (var gx = 0; gx < side; gx++)
        {
            for (var gy = 0; gy < side; gy++)
            {
                var p = new Vec2(gx * 1.6 - side * 0.8, gy * 1.6 - side * 0.8);
                crowd.Add(p, Radius, MaxSpeed, goal: -p);
            }
        }

        return crowd;
    }

    [Theory]
    [InlineData(2.0, -1)]
    [InlineData(4.0, -1)]
    [InlineData(4.0, 2)]
    [InlineData(8.0, 4)]
    [InlineData(1.0, 8)]  // region cell == neighbour cell (finest); high thread cap
    public void SpreadCrowd_RegionBitIdenticalToSerial(double regionMultiplier, int maxParallelism)
    {
        // 20x20 = 400 agents (> 256 threshold), spanning many region cells.
        AssertRegionMatchesSerial(
            () => BuildSpread(20), steps: 120, regionMultiplier, maxParallelism, spatialHash: true,
            $"spread20 mult={regionMultiplier} par={maxParallelism}");
    }

    [Fact]
    public void SpreadCrowd_RegionBitIdentical_WithoutSpatialHash()
    {
        // Region dispatch is orthogonal to the gather method: it must be bit-identical even with the
        // brute-force gather (no hash), proving the decomposition itself introduces no ordering change.
        AssertRegionMatchesSerial(
            () => BuildSpread(18), steps: 80, regionMultiplier: 4.0, maxParallelism: -1, spatialHash: false,
            "spread18 no-hash");
    }

    [Fact]
    public void DenseCounterFlow_WithMaxNeighboursAndRemoval_RegionBitIdentical()
    {
        // The nearest-k bounded-insertion path AND removal-on-arrival THROUGH the region dispatch -- the
        // combination most likely to expose an ordering discrepancy if one existed. Two dense opposing blocks.
        AssertRegionMatchesSerial(
            () =>
            {
                var crowd = new OrcaCrowd(600)
                {
                    SymmetryBreak = 0.05, NeighbourDist = 4.0, MaxNeighbours = 8,
                    RemoveOnArrival = true, ArrivalRadius = 0.2,
                };
                var id = 0;
                for (var row = 0; row < 15 && id < 600; row++)
                {
                    for (var col = 0; col < 20 && id < 600; col++)
                    {
                        var y = row * 1.2;
                        crowd.Add(new Vec2(-18 + col * 0.8, y), Radius, MaxSpeed, goal: new Vec2(18, y));
                        id++;
                        if (id >= 600) break;
                        crowd.Add(new Vec2(18 - col * 0.8, y + 0.4), Radius, MaxSpeed, goal: new Vec2(-18, y + 0.4));
                        id++;
                    }
                }

                return crowd;
            },
            steps: 200, regionMultiplier: 3.0, maxParallelism: -1, spatialHash: true, "dense-counterflow+maxN+removal");
    }
}
