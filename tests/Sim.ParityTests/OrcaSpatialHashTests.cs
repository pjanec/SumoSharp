using System;
using Sim.Core.Orca;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// Q3 (docs/LANELESS-HANDOFF §next-steps): the shared uniform spatial hash for the crowd
// (OrcaCrowd.UseSpatialHash), replacing the O(n^2) brute-force neighbour scan. The hard requirement is
// BIT-IDENTITY: grid-on must produce exactly the same trajectory as grid-off (the grid is only a
// pre-filter that skips out-of-range agents; candidates are sorted to the same order the brute-force
// scan visits, so the neighbour set, order, every LP and every position match to the last bit). Default
// is off, so the whole lane/bridge world is byte-identical; callers opt in for the perf win.
public class OrcaSpatialHashTests
{
    private const double Dt = 0.25;
    private const double Radius = 0.5;
    private const double MaxSpeed = 1.5;

    private readonly ITestOutputHelper _out;

    public OrcaSpatialHashTests(ITestOutputHelper output) => _out = output;

    // Run `build` twice -- grid off, then grid on -- and assert the two crowds stay bit-identical
    // (exact double equality on every agent's position) at every step for `steps` steps.
    private void AssertGridMatchesBruteForce(Func<OrcaCrowd> build, int steps, string label)
    {
        var brute = build();
        var grid = build();
        grid.UseSpatialHash = true;

        var n = brute.Count;
        Assert.True(n > 0 && n == grid.Count);

        for (var step = 0; step < steps; step++)
        {
            brute.Step(Dt);
            grid.Step(Dt);
            for (var i = 0; i < n; i++)
            {
                var pb = brute.Position(i);
                var pg = grid.Position(i);
                Assert.True(pb.X == pg.X && pb.Y == pg.Y,
                    $"{label}: grid diverged from brute-force at step {step}, agent {i}: " +
                    $"brute=({pb.X:R},{pb.Y:R}) grid=({pg.X:R},{pg.Y:R})");
            }
        }

        _out.WriteLine($"{label}: grid bit-identical to brute-force over {steps} steps, {n} agents");
    }

    [Fact]
    public void CounterFlow_GridBitIdenticalToBruteForce()
    {
        AssertGridMatchesBruteForce(() =>
        {
            var crowd = new OrcaCrowd(24) { SymmetryBreak = 0.05 };
            for (var i = 0; i < 6; i++)
            {
                crowd.Add(new Vec2(-12, i * 1.5), Radius, MaxSpeed, goal: new Vec2(12, i * 1.5));
                crowd.Add(new Vec2(12, i * 1.5 + 0.75), Radius, MaxSpeed, goal: new Vec2(-12, i * 1.5 + 0.75));
            }

            return crowd;
        }, steps: 200, "counter-flow");
    }

    [Fact]
    public void CounterFlow_WithMaxNeighboursAndRemoval_GridBitIdentical()
    {
        // Exercises the nearest-k bounded-insertion path AND removal-on-arrival THROUGH the grid, the
        // combination most likely to expose an ordering discrepancy if one existed.
        AssertGridMatchesBruteForce(() =>
        {
            var crowd = new OrcaCrowd(24) { SymmetryBreak = 0.05, MaxNeighbours = 6, RemoveOnArrival = true, ArrivalRadius = 0.2 };
            for (var i = 0; i < 6; i++)
            {
                crowd.Add(new Vec2(-12, i * 1.5), Radius, MaxSpeed, goal: new Vec2(12, i * 1.5));
                crowd.Add(new Vec2(12, i * 1.5 + 0.75), Radius, MaxSpeed, goal: new Vec2(-12, i * 1.5 + 0.75));
            }

            return crowd;
        }, steps: 300, "counter-flow+maxN+removal");
    }

    [Fact]
    public void SpreadCrowd_ManyCells_GridBitIdentical()
    {
        // A grid of agents each heading to the opposite corner: positions span many cells (so the grid
        // genuinely partitions), and paths cross in the middle.
        AssertGridMatchesBruteForce(() =>
        {
            var crowd = new OrcaCrowd(64) { SymmetryBreak = 0.05 };
            for (var gx = 0; gx < 8; gx++)
            {
                for (var gy = 0; gy < 8; gy++)
                {
                    var p = new Vec2(gx * 3.0 - 10.5, gy * 3.0 - 10.5);
                    crowd.Add(p, Radius, MaxSpeed, goal: -p);
                }
            }

            return crowd;
        }, steps: 150, "spread-crowd");
    }

    [Fact]
    public void SpatialHash_IsDeterministic_RunToRun()
    {
        OrcaCrowd Build()
        {
            var crowd = new OrcaCrowd(64) { SymmetryBreak = 0.05, UseSpatialHash = true };
            for (var gx = 0; gx < 8; gx++)
            {
                for (var gy = 0; gy < 8; gy++)
                {
                    var p = new Vec2(gx * 3.0 - 10.5, gy * 3.0 - 10.5);
                    crowd.Add(p, Radius, MaxSpeed, goal: -p);
                }
            }

            return crowd;
        }

        var a = Build();
        var b = Build();
        for (var step = 0; step < 150; step++)
        {
            a.Step(Dt);
            b.Step(Dt);
            for (var i = 0; i < a.Count; i++)
            {
                Assert.Equal(a.Position(i).X, b.Position(i).X);
                Assert.Equal(a.Position(i).Y, b.Position(i).Y);
            }
        }
    }
}
