using Sim.Core.Bridge;
using Sim.Core.Orca;
using Sim.Pedestrians.Lod;
using Sim.Pedestrians.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests.Lod;

// P6-2-5 (docs/PEDESTRIAN-P6-2-REGION-DESIGN.md §6): the high-power crowd's opt-in region decomposition,
// exercised THROUGH PedLodManager. OrcaRegionDecompositionTests already gates the OrcaCrowd path directly;
// this proves the manager passthrough (UseRegionDecompositionHighCrowd) is bit-identical to the default path
// when a real promoted population (>256, so the region dispatch genuinely engages) is stepped through the
// full manager -- promotions, routing, steering and all. Default-off is covered by every other ped test
// staying green.
public class PedLodRegionDecompositionTests
{
    private const double Dt = 0.1;

    // Null nav -> PedLodManager falls back to a straight line from the ped's position to its goal (see
    // PedLodManager.Step), which is all this determinism check needs.
    private sealed class StraightLineNav : IPedNavigation
    {
        public IReadOnlyList<Vec2>? FindPath(Vec2 start, Vec2 goal) => null;
    }

    private readonly ITestOutputHelper _out;

    public PedLodRegionDecompositionTests(ITestOutputHelper output) => _out = output;

    // Builds a manager with `n` peds packed in a compact cluster, each walking across it, plus a wide interest
    // source at the centre so ALL of them promote to FreeKinematic (the high-power OrcaCrowd) within a step or
    // two -> _highCrowd.Count >= n, engaging the region path when enabled.
    private static (PedLodManager Manager, InterestField Field, int[] Ids) Build(int n)
    {
        var publisher = new PedPublisher();
        var manager = new PedLodManager(new StraightLineNav(), publisher, arriveRadius: 0.3, dwellSeconds: 1.0);

        var ids = new int[n];
        // A ~40 m square cluster; each ped crosses to the opposite side so paths interleave (real ORCA work).
        const int side = 20; // 20x20 = 400 peds
        for (var i = 0; i < n; i++)
        {
            var gx = i % side;
            var gy = i / side;
            var start = new Vec2(gx * 2.0, gy * 2.0);
            var goal = new Vec2((side - 1) * 2.0 - gx * 2.0, (side - 1) * 2.0 - gy * 2.0);
            var id = i + 1;
            ids[i] = id;
            manager.AddPed(id, new[] { start, goal }, maxSpeed: 1.3, radius: 0.3, now: 0.0);
        }

        var field = new InterestField();
        // Centre of the cluster, radius large enough to cover every ped -> all promote.
        var centre = new Vec2((side - 1) * 1.0, (side - 1) * 1.0);
        field.Register(new InterestSource(centre, promoteRadius: 1000.0, demoteRadius: 2000.0));

        return (manager, field, ids);
    }

    [Fact]
    public void HighCrowdRegionDecomposition_BitIdenticalToDefault_ThroughTheManager()
    {
        const int n = 400; // > 256 ParallelStepThreshold, so the region dispatch actually engages
        var (baseline, baseField, ids) = Build(n);
        var (region, regField, _) = Build(n);
        region.UseRegionDecompositionHighCrowd = true;
        region.HighCrowdRegionCellSizeMultiplier = 3.0;

        var noEntities = System.Array.Empty<WorldDisc>();
        var now = 0.0;
        var everHighPower = 0;

        for (var step = 0; step < 40; step++)
        {
            baseline.Step(now, Dt, baseField, noEntities);
            region.Step(now, Dt, regField, noEntities);
            now += Dt;

            // Count high-power AFTER the promotion dwell (>= dwellSeconds = 1.0 s = ~10 steps) has elapsed, so
            // the peds have actually promoted into the high-power OrcaCrowd and the region path genuinely
            // engages. Take the max seen across the post-dwell window.
            if (step >= 15)
            {
                var high = 0;
                foreach (var id in ids)
                {
                    if (region.ModelOf(id) == PedDrModel.FreeKinematic)
                    {
                        high++;
                    }
                }

                if (high > everHighPower)
                {
                    everHighPower = high;
                }
            }

            foreach (var id in ids)
            {
                var pb = baseline.PositionOf(id, now);
                var pr = region.PositionOf(id, now);
                Assert.True(pb.X == pr.X && pb.Y == pr.Y,
                    $"region-decomp diverged from default at step {step}, ped {id}: " +
                    $"default=({pb.X:R},{pb.Y:R}) region=({pr.X:R},{pr.Y:R})");
                // Both runs must agree on the DR model too (promotion timing identical).
                Assert.Equal(baseline.ModelOf(id), region.ModelOf(id));
            }
        }

        Assert.True(everHighPower >= 256,
            $"expected >=256 high-power peds so the region path engages through the manager, got {everHighPower}");
        _out.WriteLine($"[P6-2-5] manager-level region decomposition bit-identical over 40 steps, " +
                       $"{n} peds ({everHighPower} high-power at step 1)");
    }
}
