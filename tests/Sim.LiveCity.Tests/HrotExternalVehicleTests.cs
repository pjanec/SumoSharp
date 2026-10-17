using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sim.Core;
using Sim.Replication;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// docs/HROT-INTEGRATION-DESIGN.md H13 -- hand one of the HOST's vehicles to SUMO as a shadow car and take it back:
// LiveCitySim.SpawnExternalVehicle / DespawnExternalVehicle / ExternalVehicleArrived. Read back through the sim's
// own in-memory vehicle wire (VehicleSource) and a recording lifecycle sink on the record tee.
public class HrotExternalVehicleTests
{
    private readonly ITestOutputHelper _out;
    public HrotExternalVehicleTests(ITestOutputHelper output) => _out = output;

    // A four-edge eastbound chain v1..v4 (x 100..500, y 100) inside the crop, and a two-edge chain w1..w2 at y 900,
    // OUTSIDE the crop -- demand never spawns there, so a shadow on it never meets a demand car.
    private const string ChainNetXml = """
        <net>
          <edge id="v1" from="A" to="B"><lane id="v1_0" index="0" speed="13.9" length="100.0" shape="100.00,100.00 200.00,100.00"/></edge>
          <edge id="v2" from="B" to="C"><lane id="v2_0" index="0" speed="13.9" length="100.0" shape="200.00,100.00 300.00,100.00"/></edge>
          <edge id="v3" from="C" to="D"><lane id="v3_0" index="0" speed="13.9" length="100.0" shape="300.00,100.00 400.00,100.00"/></edge>
          <edge id="v4" from="D" to="E"><lane id="v4_0" index="0" speed="13.9" length="100.0" shape="400.00,100.00 500.00,100.00"/></edge>
          <edge id="w1" from="P" to="Q"><lane id="w1_0" index="0" speed="13.9" length="100.0" shape="100.00,900.00 200.00,900.00"/></edge>
          <edge id="w2" from="Q" to="R"><lane id="w2_0" index="0" speed="13.9" length="100.0" shape="200.00,900.00 300.00,900.00"/></edge>
          <connection from="v1" to="v2" fromLane="0" toLane="0"/>
          <connection from="v2" to="v3" fromLane="0" toLane="0"/>
          <connection from="v3" to="v4" fromLane="0" toLane="0"/>
          <connection from="w1" to="w2" fromLane="0" toLane="0"/>
        </net>
        """;

    private sealed class LifecycleRecorder : IReplicationSink
    {
        public List<LifecycleRecord> Records { get; } = new();
        public void PublishGeometry(IReadOnlyList<GeometryCodec.LaneGeo> lanes) { }
        public void PublishLifecycle(in LifecycleRecord record) => Records.Add(record);
        public void PublishFrame(uint step, double time, ReadOnlySpan<VehicleRecord> movers) { }
        public void PublishTrafficLights(uint step, double time, IReadOnlyList<TlCodec.TlEntry> lights) { }
        public void Dispose() { }
    }

    private static void WithSim(int cars, Action<LiveCitySim, LifecycleRecorder> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "livecity-h13-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "net.xml"), ChainNetXml);
        var cfg = LiveCityConfig.ForDataset(dir);
        cfg.X0 = 0; cfg.Y0 = 0; cfg.X1 = 1000; cfg.Y1 = 500;
        cfg.CarTargetConcurrent = cars;
        var recorder = new LifecycleRecorder();
        try
        {
            using var sim = new LiveCitySim(cfg, recordVehSink: recorder);
            body(sim, recorder);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void H13_AShadowNearALane_DrivesThreeEdges_AndArrivesAtTheDestinationPoint()
    {
        WithSim(cars: 0, (sim, rec) =>
        {
            // 0.6 m off v1's centre line, heading 10 degrees north of east; destination 50 m into v4.
            Assert.True(sim.SpawnExternalVehicle("hrot:17", 120.0, 100.6, 10.0 * Math.PI / 180.0, 5.0, 450.0, 100.0));
            Assert.True(sim.IsExternalVehicleLive("hrot:17"));

            var sawOnWire = false;
            var lastSampleX = double.NaN;
            var lastSampleY = double.NaN;
            var steps = 0;
            for (; steps < 400 && !sim.ExternalVehicleArrived("hrot:17"); steps++)
            {
                sim.Step();
                sawOnWire |= sim.VehicleSource.Names.Values.Contains("hrot:17");
                foreach (var car in sim.SampleCars())
                {
                    if (car.Name == "hrot:17") (lastSampleX, lastSampleY) = (car.X, car.Y);
                }

            }

            Assert.True(sim.ExternalVehicleArrived("hrot:17"), $"not arrived after {steps} steps");
            Assert.False(sim.IsExternalVehicleLive("hrot:17"));
            Assert.True(sawOnWire, "the shadow must ride the vehicle wire under its given name");
            Assert.Equal(0L, sim.ArrivedTotal); // not demand's arrival

            // The last pose the engine reported before the car left the sim: short of (450, 100) by at most one
            // step of travel (SUMO arrives the car the step it passes arrivalPos), never at v4's end (x = 500).
            var dist = Math.Abs(450.0 - lastSampleX);
            _out.WriteLine($"arrived after {steps} steps; last sampled x={lastSampleX:F2} ({dist:F2} m from the destination) y={lastSampleY:F2}");
            Assert.True(lastSampleX <= 450.0 + 1e-6 && dist < 7.5, $"last x={lastSampleX:F2}");
            Assert.True(Math.Abs(lastSampleY - 100.0) < 0.5, $"last y={lastSampleY:F2}");

            // Lifecycle: exactly one spawn record under the given name, then the despawn tombstone for that handle.
            var spawn = Assert.Single(rec.Records, r => r.IsSpawn && r.Name == "hrot:17");
            Assert.Single(rec.Records, r => !r.IsSpawn && r.Handle.Equals(spawn.Handle));
            Assert.DoesNotContain("hrot:17", sim.VehicleSource.Names.Values); // the reader dropped it
        });
    }

    [Fact]
    public void H13_TheStartLaneMustAgreeWithTheHeading_AndBadRequestsReturnFalse()
    {
        WithSim(cars: 0, (sim, _) =>
        {
            Assert.False(sim.SpawnExternalVehicle("hrot:1", 150.0, 100.0, Math.PI, 5.0, 450.0, 100.0));                 // opposite the only lane
            Assert.False(sim.SpawnExternalVehicle("hrot:1", 150.0, 100.0, 100.0 * Math.PI / 180.0, 5.0, 450.0, 100.0)); // 100 degrees off
            Assert.False(sim.SpawnExternalVehicle("hrot:1", 150.0, 130.0, 0.0, 5.0, 450.0, 100.0));                     // 30 m from any lane
            Assert.False(sim.SpawnExternalVehicle("hrot:1", 150.0, 100.0, 0.0, 5.0, 450.0, 160.0));                     // no destination lane
            Assert.False(sim.SpawnExternalVehicle("hrot:1", 150.0, 100.0, 0.0, 5.0, 250.0, 900.0));                     // no route v -> w
            Assert.False(sim.SpawnExternalVehicle("hrot:1", 180.0, 100.0, 0.0, 5.0, 150.0, 100.0));                     // behind, same edge
            Assert.False(sim.SpawnExternalVehicle("hrot:1", 150.0, 100.0, 0.0, 5.0, 450.0, 100.0, vTypeId: "nope"));    // unknown type
            Assert.Equal(0, sim.ExternalVehicleCount);

            Assert.True(sim.SpawnExternalVehicle("hrot:1", 150.0, 100.0, 80.0 * Math.PI / 180.0, 5.0, 450.0, 100.0));   // 80 degrees: accepted
            Assert.False(sim.SpawnExternalVehicle("hrot:1", 120.0, 100.0, 0.0, 5.0, 450.0, 100.0));                     // id already live
        });
    }

    [Fact]
    public void H13_DespawnMidRoute_RemovesIt_AndTheWirePublishesTheRemoval()
    {
        WithSim(cars: 0, (sim, rec) =>
        {
            Assert.True(sim.SpawnExternalVehicle("hrot:5", 120.0, 100.0, 0.0, 10.0, 450.0, 100.0));
            for (var i = 0; i < 15; i++) sim.Step();
            Assert.Contains("hrot:5", sim.VehicleSource.Names.Values);
            var x = sim.SampleCars().Single(c => c.Name == "hrot:5").X;
            Assert.InRange(x, 150.0, 400.0); // mid-route

            Assert.True(sim.DespawnExternalVehicle("hrot:5"));
            Assert.False(sim.DespawnExternalVehicle("hrot:5"));
            sim.Step();

            Assert.Empty(sim.SampleCars());
            Assert.DoesNotContain("hrot:5", sim.VehicleSource.Names.Values);
            var spawn = Assert.Single(rec.Records, r => r.IsSpawn && r.Name == "hrot:5");
            Assert.Single(rec.Records, r => !r.IsSpawn && r.Handle.Equals(spawn.Handle));
            Assert.False(sim.ExternalVehicleArrived("hrot:5"));
            Assert.Equal(0, sim.ExternalVehicleCount);

            // The id is free again.
            Assert.True(sim.SpawnExternalVehicle("hrot:5", 120.0, 100.0, 0.0, 10.0, 450.0, 100.0));
        });
    }

    // Demand restricted to the v chain by a route file; the shadow drives the disconnected w chain, so it cannot
    // touch a demand car physically. Then demand with the shadow must be IDENTICAL to demand without it: same
    // spawns, same ids from the provider, same poses, same arrivals -- it took no demand slot, no provider call.
    [Fact]
    public void H13_DemandNeitherCountsTheShadow_NorCallsTheIdProvider()
    {
        static List<string> Run(bool withShadow, out long arrived, out int provided)
        {
            var dir = Path.Combine(Path.GetTempPath(), "livecity-h13-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "net.xml"), ChainNetXml);
            File.WriteAllText(Path.Combine(dir, "scenario.rou.xml"),
                "<routes><route id=\"r\" edges=\"v1 v2 v3 v4\"/></routes>");
            var cfg = LiveCityConfig.ForDataset(dir);
            cfg.CarTargetConcurrent = 3;
            var calls = 0;
            cfg.VehicleIdProvider = () => "d" + (++calls);
            var frames = new List<string>();
            try
            {
                using var sim = new LiveCitySim(cfg);
                if (withShadow)
                {
                    Assert.True(sim.SpawnExternalVehicle("hrot:9", 110.0, 900.0, 0.0, 0.0, 295.0, 900.0));
                    Assert.Equal(0, calls);
                }

                var shadowSeen = !withShadow;
                for (var i = 0; i < 120; i++)
                {
                    sim.Step();
                    var cars = sim.SampleCars();
                    shadowSeen |= cars.Any(c => c.Name == "hrot:9");
                    frames.Add(string.Join(";", cars.Where(c => c.Name != "hrot:9")
                        .OrderBy(c => c.Name, StringComparer.Ordinal)
                        .Select(c => $"{c.Name}@{c.X:F3},{c.Y:F3}")));
                }

                Assert.True(shadowSeen);
                if (withShadow) Assert.True(sim.ExternalVehicleArrived("hrot:9"), "the shadow should have arrived on w2");
                arrived = sim.ArrivedTotal;
                provided = calls;
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }

            return frames;
        }

        var without = Run(withShadow: false, out var arrivedWithout, out var providedWithout);
        var with = Run(withShadow: true, out var arrivedWith, out var providedWith);

        Assert.True(arrivedWithout > 0 && providedWithout > 3, "the comparison needs demand turnover");
        Assert.Equal(providedWithout, providedWith);
        Assert.Equal(arrivedWithout, arrivedWith); // the shadow's own arrival is not demand's
        for (var i = 0; i < without.Count; i++)
        {
            Assert.True(without[i] == with[i], $"step {i}: demand diverged\n  without: {without[i]}\n  with:    {with[i]}");
        }
    }
}
