using System;
using Sim.Core.Orca;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// P2-1 (docs/PEDESTRIAN-TASKS.md, PEDESTRIAN-DESIGN.md §3(b)): the opt-in static-obstacle spatial
// index (OrcaCrowd.UseObstacleSpatialIndex), mirroring OrcaSpatialHashTests' grid-vs-brute-force
// bit-identity gate for the obstacle scan instead of the agent-agent scan. The hard requirement is
// again BIT-IDENTITY: the index is a pure pre-filter over the SAME obstacle segments the
// brute-force scan would visit, processed in the same (sorted, de-duplicated) order, so every ORCA
// obstacle line -- and hence every trajectory -- matches to the last bit. Default is off, so the
// whole obstacle world stays byte-identical to pre-P2-1; callers opt in for the perf win.
public class OrcaObstacleSpatialIndexTests
{
    private const double Dt = 0.25;
    private const double Radius = 0.4;
    private const double MaxSpeed = 1.2;

    private readonly ITestOutputHelper _out;

    public OrcaObstacleSpatialIndexTests(ITestOutputHelper output) => _out = output;

    // A grid of small square "building" obstacles with gaps between them (like the parked-cars /
    // buildings case §3(b) calls out), scattered across a region several agents must cross.
    private static void AddBuildingGrid(OrcaCrowd crowd, int cols, int rows, double spacing, double size)
    {
        for (var gx = 0; gx < cols; gx++)
        {
            for (var gy = 0; gy < rows; gy++)
            {
                var x0 = gx * spacing;
                var y0 = gy * spacing;
                crowd.AddObstacle(new[]
                {
                    new Vec2(x0, y0),
                    new Vec2(x0 + size, y0),
                    new Vec2(x0 + size, y0 + size),
                    new Vec2(x0, y0 + size),
                });
            }
        }
    }

    private static void AddCrossingAgents(OrcaCrowd crowd, int cols, int rows, double spacing)
    {
        // Agents travel along the "streets" between buildings (offset from the building grid so
        // they start clear of any obstacle), crossing the whole field diagonally so their paths
        // sweep past many different obstacle cells over the run.
        var width = cols * spacing;
        var height = rows * spacing;
        for (var i = 0; i < rows; i++)
        {
            var y = (i + 0.5) * spacing - (spacing * 0.5);
            crowd.Add(new Vec2(-3.0, y), Radius, MaxSpeed, goal: new Vec2(width + 3.0, height - y));
            crowd.Add(new Vec2(width + 3.0, y), Radius, MaxSpeed, goal: new Vec2(-3.0, height - y));
        }
    }

    [Fact]
    public void ManyObstacles_IndexOn_MatchesBruteForce_Serial_ExactPositionAndVelocity()
    {
        OrcaCrowd Build(bool useIndex)
        {
            var crowd = new OrcaCrowd { SymmetryBreak = 0.05, UseObstacleSpatialIndex = useIndex };
            AddBuildingGrid(crowd, cols: 8, rows: 8, spacing: 6.0, size: 2.0);   // 64 obstacles, 256 segments
            AddCrossingAgents(crowd, cols: 8, rows: 8, spacing: 6.0);           // 16 agents
            return crowd;
        }

        var brute = Build(false);
        var indexed = Build(true);
        Assert.True(brute.Count > 0 && brute.Count == indexed.Count);

        const int steps = 200;
        for (var step = 0; step < steps; step++)
        {
            brute.Step(Dt);
            indexed.Step(Dt);

            for (var i = 0; i < brute.Count; i++)
            {
                var pb = brute.Position(i);
                var pi = indexed.Position(i);
                Assert.True(pb.X == pi.X && pb.Y == pi.Y,
                    $"serial: obstacle index diverged from brute-force at step {step}, agent {i}: " +
                    $"brute=({pb.X:R},{pb.Y:R}) indexed=({pi.X:R},{pi.Y:R})");

                var vb = brute.Velocity(i);
                var vi = indexed.Velocity(i);
                Assert.True(vb.X == vi.X && vb.Y == vi.Y,
                    $"serial: obstacle index velocity diverged from brute-force at step {step}, agent {i}");
            }
        }

        _out.WriteLine($"serial: index bit-identical to brute-force over {steps} steps, " +
                        $"{brute.Count} agents, 64 obstacles (256 segments)");
    }

    [Fact]
    public void ManyObstacles_IndexOn_MatchesBruteForce_UnderParallelStep()
    {
        // Enough agents to actually engage OrcaCrowd's parallel plan/execute path
        // (ParallelStepThreshold == 256), crossing a field scattered with many obstacles, so this
        // exercises the "concurrent Plan() workers read the (read-only) obstacle grid" path the
        // serial test above cannot.
        OrcaCrowd Build(bool useIndex)
        {
            var crowd = new OrcaCrowd
            {
                SymmetryBreak = 0.05,
                UseObstacleSpatialIndex = useIndex,
                UseParallelStep = true,
            };
            AddBuildingGrid(crowd, cols: 10, rows: 10, spacing: 5.0, size: 1.5);  // 100 obstacles, 400 segments
            for (var gx = 0; gx < 18; gx++)
            {
                for (var gy = 0; gy < 18; gy++)   // 324 agents >= ParallelStepThreshold
                {
                    var p = new Vec2(gx * 2.6 - 22.0, gy * 2.6 - 22.0);
                    crowd.Add(p, Radius, MaxSpeed, goal: -p);
                }
            }

            return crowd;
        }

        var brute = Build(false);
        var indexed = Build(true);
        Assert.True(brute.Count >= 256, "test setup must exceed OrcaCrowd's ParallelStepThreshold to engage the parallel path");
        Assert.Equal(brute.Count, indexed.Count);

        const int steps = 60;
        for (var step = 0; step < steps; step++)
        {
            brute.Step(Dt);
            indexed.Step(Dt);

            for (var i = 0; i < brute.Count; i++)
            {
                var pb = brute.Position(i);
                var pi = indexed.Position(i);
                Assert.True(pb.X == pi.X && pb.Y == pi.Y,
                    $"parallel: obstacle index diverged from brute-force at step {step}, agent {i}: " +
                    $"brute=({pb.X:R},{pb.Y:R}) indexed=({pi.X:R},{pi.Y:R})");
            }
        }

        _out.WriteLine($"parallel: index bit-identical to brute-force over {steps} steps, " +
                        $"{brute.Count} agents, 100 obstacles (400 segments)");
    }

    [Fact]
    public void ObstacleIndex_CandidateExaminationsScaleSubLinearlyWithObstacleCount()
    {
        // Scatter n well-separated 1x1 obstacle boxes far apart (50-unit spacing) so only the ONE
        // box nearest the probing agent is ever actually in range, regardless of how many total
        // obstacles exist elsewhere in the world. Brute-force examines every one of the 4*n segment
        // candidates on every Plan() call (it is defined as O(obstacles)); with the index on,
        // examinations should stay flat as n grows, because the grid query only returns candidates
        // whose bounding box falls within the agent's obstacle-range ring.
        int[] obstacleCounts = { 8, 32, 128, 512 };
        var examinedBruteForce = new long[obstacleCounts.Length];
        var examinedIndexed = new long[obstacleCounts.Length];

        for (var c = 0; c < obstacleCounts.Length; c++)
        {
            var n = obstacleCounts[c];

            OrcaCrowd Build(bool useIndex)
            {
                var crowd = new OrcaCrowd { UseObstacleSpatialIndex = useIndex };
                for (var k = 0; k < n; k++)
                {
                    var cx = (k % 100) * 50.0;
                    var cy = (k / 100) * 50.0;
                    crowd.AddObstacle(new[]
                    {
                        new Vec2(cx, cy), new Vec2(cx + 1, cy),
                        new Vec2(cx + 1, cy + 1), new Vec2(cx, cy + 1),
                    });
                }

                crowd.Add(new Vec2(0.5, 5), Radius, MaxSpeed, goal: new Vec2(0.5, 10));
                return crowd;
            }

            var brute = Build(false);
            brute.Step(Dt);
            brute.ResetObstacleCandidateCounter();
            brute.Step(Dt);
            examinedBruteForce[c] = brute.ObstacleCandidatesExamined;

            var indexed = Build(true);
            indexed.Step(Dt);
            indexed.ResetObstacleCandidateCounter();
            indexed.Step(Dt);
            examinedIndexed[c] = indexed.ObstacleCandidatesExamined;
        }

        _out.WriteLine("obstacles:       " + string.Join(", ", obstacleCounts));
        _out.WriteLine("brute examined:  " + string.Join(", ", examinedBruteForce));
        _out.WriteLine("indexed examined:" + string.Join(", ", examinedIndexed));

        // Brute force examines every obstacle vertex on every Plan() call for the one active agent.
        Assert.Equal(4 * obstacleCounts[^1], examinedBruteForce[^1]);

        // Indexed stays (near-)flat: bounded by the handful of segments actually within range,
        // regardless of how many far-away obstacles exist elsewhere -- proving sub-linear scaling.
        Assert.True(examinedIndexed[^1] <= examinedIndexed[0] * 4,
            $"indexed examinations grew ~linearly with obstacle count ({examinedIndexed[0]} -> {examinedIndexed[^1]}); " +
            "expected near-flat (sub-linear) scaling");
        Assert.True(examinedIndexed[^1] < examinedBruteForce[^1] / 4,
            $"indexed ({examinedIndexed[^1]}) did not examine meaningfully fewer candidates than brute force " +
            $"({examinedBruteForce[^1]}) at {obstacleCounts[^1]} obstacles");
    }
}
