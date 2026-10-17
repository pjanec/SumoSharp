using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sim.Core.Bridge;
using Sim.Ingest;
using Xunit;

namespace Sim.LiveCity.Tests;

// docs/HROT-INTEGRATION-DESIGN.md H12 -- a host's own moving bodies fed into a running LiveCitySim
// (LiveCitySim.SetExternalBodies -> ExternalBodyFeed): a VEHICLE body becomes a moving lane obstacle on every
// vehicle lane it overlaps, a PERSON (or an off-road vehicle) a crowd disc on Engine.CrowdSource. Vehicle-only nets
// (no sidewalks => peds disabled, the HROT configuration), read back through SampleCars / ExternalBodies.
public class HrotExternalBodyTests
{
    // Same two 100 m single-lane edges as HrotLiveCitySeamTests: v1 (x 100..200) then v2 (x 200..300), y = 100.
    private const string TwoEdgeNetXml = """
        <net>
          <edge id="v1" from="A" to="B">
            <lane id="v1_0" index="0" speed="13.9" length="100.0" shape="100.00,100.00 200.00,100.00"/>
          </edge>
          <edge id="v2" from="B" to="C">
            <lane id="v2_0" index="0" speed="13.9" length="100.0" shape="200.00,100.00 300.00,100.00"/>
          </edge>
          <connection from="v1" to="v2" fromLane="0" toLane="0"/>
        </net>
        """;

    // One 200 m two-lane edge: lane 0 centre y = 100, lane 1 centre y = 103.2 (default 3.2 m width).
    private const string TwoLaneNetXml = """
        <net>
          <edge id="e0" from="A" to="B">
            <lane id="e0_0" index="0" speed="13.9" length="200.0" shape="100.00,100.00 300.00,100.00"/>
            <lane id="e0_1" index="1" speed="13.9" length="200.0" shape="100.00,103.20 300.00,103.20"/>
          </edge>
        </net>
        """;

    private static (LiveCityConfig Cfg, string Dir) Config(string netXml, int cars)
    {
        var dir = Path.Combine(Path.GetTempPath(), "livecity-h12-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "net.xml"), netXml);
        var cfg = LiveCityConfig.ForDataset(dir);
        cfg.X0 = 0; cfg.Y0 = 0; cfg.X1 = 1000; cfg.Y1 = 1000;
        cfg.CarTargetConcurrent = cars;
        return (cfg, dir);
    }

    private static void WithSim(string netXml, int cars, Action<LiveCitySim> body)
    {
        var (cfg, dir) = Config(netXml, cars);
        try
        {
            using var sim = new LiveCitySim(cfg);
            Assert.False(sim.PedestriansEnabled); // the HROT configuration: no crowd source of the sim's own
            body(sim);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Steps `steps` times; fails if any car's reported position ever reaches `limitX` on the y = 100 lane. Returns
    // each car's x per step (by vehicle name) so the caller can test for a stopped queue head.
    private static Dictionary<string, List<double>> StepKeepingBehind(LiveCitySim sim, int steps, double limitX)
    {
        var track = new Dictionary<string, List<double>>();
        for (var i = 0; i < steps; i++)
        {
            sim.Step();
            foreach (var car in sim.SampleCars())
            {
                Assert.True(car.X < limitX, $"step {i}: car {car.Name} at x={car.X:F2} passed into the body (limit {limitX})");
                if (!track.TryGetValue(car.Name, out var xs)) track[car.Name] = xs = new List<double>();
                xs.Add(car.X);
            }
        }

        return track;
    }

    // The queue head: a car whose last 10 samples sit within `nearX - 15 .. nearX` and moved < 0.1 m in total.
    private static void AssertSomeCarIsStoppedBefore(Dictionary<string, List<double>> track, double nearX)
    {
        var stopped = track.Values.Where(xs => xs.Count >= 10)
            .Select(xs => xs.Skip(xs.Count - 10).ToList())
            .Where(last => last[^1] > nearX - 15.0 && last[^1] < nearX && last.Max() - last.Min() < 0.1)
            .ToList();
        Assert.True(stopped.Count > 0,
            "expected a car stopped just behind the body; last positions: "
            + string.Join(", ", track.Values.Select(xs => xs[^1].ToString("F1"))));
    }

    private static double MaxXOver(LiveCitySim sim, int steps)
    {
        var max = double.NegativeInfinity;
        for (var i = 0; i < steps; i++)
        {
            sim.Step();
            foreach (var car in sim.SampleCars()) max = Math.Max(max, car.X);
        }

        return max;
    }

    [Fact]
    public void H12_AParkedVehicleBody_StopsTheCarsBehindIt_AndRemovingItLetsThemGo()
    {
        WithSim(TwoEdgeNetXml, cars: 6, sim =>
        {
            // A 6 m x 2.5 m vehicle parked across x 247..253 on v2.
            sim.SetExternalBodies(new[] { new ExternalBody(7, ExternalBodyKind.Vehicle, 250.0, 100.0, 0.0, 0.0, 6.0, 2.5) });
            Assert.NotNull(sim.ExternalBodies);
            Assert.Equal(1, sim.ExternalBodies!.ObstacleCount);
            Assert.Equal(0, sim.ExternalBodies.DiscCount);

            var track = StepKeepingBehind(sim, 200, limitX: 247.0);
            AssertSomeCarIsStoppedBefore(track, 247.0);

            sim.SetExternalBodies(ReadOnlySpan<ExternalBody>.Empty);
            Assert.Equal(0, sim.ExternalBodies.ObstacleCount);
            Assert.Equal(0, sim.ExternalBodies.BodyCount);

            var arrivedBefore = sim.ArrivedTotal;
            Assert.True(MaxXOver(sim, 100) > 260.0, "after the body is removed the queue must drive on past it");
            Assert.True(sim.ArrivedTotal > arrivedBefore);
        });
    }

    [Fact]
    public void H12_AMovingVehicleBody_IsUpdatedInPlace_NotReAdded()
    {
        WithSim(TwoEdgeNetXml, cars: 0, sim =>
        {
            var lanes = new List<int>();
            for (var i = 0; i < 5; i++)
            {
                sim.SetExternalBodies(new[] { new ExternalBody(1, ExternalBodyKind.Vehicle, 220.0 + (i * 5.0), 100.0, 10.0, 0.0, 5.0, 2.0) });
                sim.Step();
                Assert.Equal(1, sim.ExternalBodies!.ObstacleCount);
                Assert.True(sim.ExternalBodies.LanesOf(1, lanes));
                Assert.Equal(new[] { sim.Network.LaneHandleById["v2_0"] }, lanes);
            }

            // Straddling the v1/v2 joint (x 197.5..202.5): one obstacle on each lane.
            sim.SetExternalBodies(new[] { new ExternalBody(1, ExternalBodyKind.Vehicle, 200.0, 100.0, 10.0, 0.0, 5.0, 2.0) });
            sim.ExternalBodies!.LanesOf(1, lanes);
            Assert.Equal(new[] { sim.Network.LaneHandleById["v1_0"], sim.Network.LaneHandleById["v2_0"] }.OrderBy(h => h), lanes);
        });
    }

    [Fact]
    public void H12_APersonBodyInTheLane_StopsTheCars_WithPedestriansDisabled()
    {
        WithSim(TwoEdgeNetXml, cars: 6, sim =>
        {
            sim.SetExternalBodies(new[] { new ExternalBody(42, ExternalBodyKind.Person, 250.0, 100.0, 0.0, 0.0, 0.6, 0.6) });
            Assert.Equal(0, sim.ExternalBodies!.ObstacleCount);
            Assert.Equal(1, sim.ExternalBodies.DiscCount);
            Assert.Equal(0.3, sim.ExternalBodies.Discs[0].Radius, 12);

            var track = StepKeepingBehind(sim, 200, limitX: 250.0 - 0.3);
            AssertSomeCarIsStoppedBefore(track, 250.0 - 0.3);

            sim.SetExternalBodies(ReadOnlySpan<ExternalBody>.Empty);
            Assert.Equal(0, sim.ExternalBodies.DiscCount);
            Assert.True(MaxXOver(sim, 100) > 260.0, "after the person leaves the cars drive on");
        });
    }

    [Fact]
    public void H12_AStraddlingBody_RegistersOnBothLanes_AndAFarOneBecomesADisc()
    {
        WithSim(TwoLaneNetXml, cars: 0, sim =>
        {
            var lane0 = sim.Network.LaneHandleById["e0_0"];
            var lane1 = sim.Network.LaneHandleById["e0_1"];
            var lanes = new List<int>();
            void Put(double y) =>
                sim.SetExternalBodies(new[] { new ExternalBody(5, ExternalBodyKind.Vehicle, 150.0, y, 0.0, 0.0, 5.0, 2.5) });

            // Centred on the lane divide (|lat| = 1.6 < 1.6 + 1.25 on both).
            Put(101.6);
            var feed = sim.ExternalBodies!;
            Assert.True(feed.LanesOf(5, lanes));
            Assert.Equal(new[] { lane0, lane1 }.OrderBy(h => h), lanes);
            Assert.Equal(2, feed.ObstacleCount);
            Assert.Equal(0, feed.DiscCount);

            // 1 m right of lane 0's centre: lane 1 is 4.2 m away (>= 2.85) -- the body LEFT lane 1.
            Put(99.0);
            feed.LanesOf(5, lanes);
            Assert.Equal(new[] { lane0 }, lanes);
            Assert.Equal(1, feed.ObstacleCount);
            Assert.Equal(0, feed.DiscCount);

            // 5 m off lane 0's centre (> 3 m + half the lane width): no lane, so a disc chain along the lane.
            Put(95.0);
            feed.LanesOf(5, lanes);
            Assert.Empty(lanes);
            Assert.Equal(0, feed.ObstacleCount);
            Assert.True(feed.DiscCount >= 2, $"an off-road 5 m vehicle is a chain of discs, got {feed.DiscCount}");
            Assert.All(feed.Discs, d => Assert.Equal(1.25, d.Radius, 12));
            Assert.All(feed.Discs, d => Assert.Equal(95.0, d.Y, 9)); // oriented along the nearest lane (+x)

            // Far from every lane, stationary: one circumscribing disc.
            Put(80.0);
            Assert.Equal(1, feed.DiscCount);
            Assert.Equal(2.5, feed.Discs[0].Radius, 12);

            sim.SetExternalBodies(ReadOnlySpan<ExternalBody>.Empty);
            Assert.Equal(0, feed.ObstacleCount);
            Assert.Equal(0, feed.DiscCount);
            Assert.Equal(0, feed.BodyCount);
        });
    }

    [Fact]
    public void H12_TheFeedsQueryNear_KeepsTheNearest()
    {
        WithSim(TwoEdgeNetXml, cars: 0, sim =>
        {
            var bodies = new List<ExternalBody>();
            for (var i = 0; i < 20; i++)
            {
                bodies.Add(new ExternalBody(i, ExternalBodyKind.Person, 140.0 + i, 0.0, 0.0, 0.0, 0.6, 0.6));
            }

            bodies.Add(new ExternalBody(99, ExternalBodyKind.Person, 102.0, 0.0, 0.0, 0.0, 0.6, 0.6));
            sim.SetExternalBodies(bodies.ToArray());

            Span<WorldDisc> into = stackalloc WorldDisc[16];
            var got = sim.ExternalBodies!.QueryNear(100.0, 0.0, 100.0, into);
            Assert.Equal(16, got);
            Assert.Equal(102.0, into[0].X, 12);
            for (var i = 1; i < got; i++)
            {
                Assert.True(into[i].X >= into[i - 1].X, "nearest-first order");
                Assert.True(into[i].X <= 154.0 + 1e-9, $"x={into[i].X} is not among the 16 nearest");
            }

            Assert.Equal(0, sim.ExternalBodies.QueryNear(100.0, 0.0, 1.0, into));
        });
    }

    [Fact]
    public void H12_NeverCallingIt_LeavesNoFeed_AndAnEmptyFirstCallCreatesNone()
    {
        WithSim(TwoEdgeNetXml, cars: 2, sim =>
        {
            for (var i = 0; i < 5; i++) sim.Step();
            Assert.Null(sim.ExternalBodies);
            sim.SetExternalBodies(ReadOnlySpan<ExternalBody>.Empty);
            Assert.Null(sim.ExternalBodies);
        });
    }

    // ── LaneSpatialIndex ───────────────────────────────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root (Traffic.sln) not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void H12_LaneSpatialIndex_MatchesABruteForceScan_OnTheDemoCityNet()
    {
        var model = NetworkParser.Parse(Path.Combine(RepoRoot(), "scenarios", "_ped", "demo_city", "box", "net.xml"));
        var index = new LaneSpatialIndex(model);

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var lane in model.LanesByHandle)
        {
            foreach (var (x, y) in lane.Shape)
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }

        var rng = new Random(12345);
        var got = new List<int>();
        var nonEmpty = 0;
        for (var q = 0; q < 400; q++)
        {
            // Some queries outside the net's box, so "nothing beyond" is exercised as well.
            var x = minX - 50.0 + (rng.NextDouble() * (maxX - minX + 100.0));
            var y = minY - 50.0 + (rng.NextDouble() * (maxY - minY + 100.0));
            var r = rng.NextDouble() * 40.0;

            index.LanesNear(x, y, r, got);

            var expected = new List<int>();
            foreach (var lane in model.LanesByHandle)
            {
                if (lane.Shape.Count > 0 && LaneProjection.Project(lane.Shape, x, y).Distance <= r)
                {
                    expected.Add(lane.Handle);
                }
            }

            Assert.Equal(expected, got); // same set, ascending handle order, no duplicates
            if (got.Count > 0) nonEmpty++;
        }

        Assert.True(nonEmpty > 50, $"only {nonEmpty} of 400 queries hit any lane -- the comparison is too weak");
        index.LanesNear(minX, minY, -1.0, got);
        Assert.Empty(got);
    }
}
