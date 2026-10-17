using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Sim.Core;
using Sim.Replication;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// docs/HROT-INTEGRATION-DESIGN.md H15 -- remove a live car by name, mid-run, returning its state: LiveCitySim.RemoveVehicle
// (+ RemovedVehicleCount, ReplicationPublisher.PublishDespawn). Read back through the sim's own in-memory vehicle wire
// (VehicleSource, self-pumped) and the record/DDS tee (a recording sink that forwards into a second in-memory bus, i.e.
// a separate receiver). Demand drives the eastbound chain v1..v4 (the crop holds only it); w1..w2 lies outside the crop.
public class HrotVehicleRemovalTests
{
    private readonly ITestOutputHelper _out;
    public HrotVehicleRemovalTests(ITestOutputHelper output) => _out = output;

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

    // Records every lifecycle record and frame (as a canonical text line) and forwards everything into `Inner`, so the
    // tee has a real receiver (`Bus.Source`) of its own.
    private sealed class Tee : IReplicationSink
    {
        public InMemoryReplicationBus Bus { get; } = new();
        public List<LifecycleRecord> Lifecycles { get; } = new();
        public List<string> Lines { get; } = new();

        public void PublishGeometry(IReadOnlyList<GeometryCodec.LaneGeo> lanes) => Bus.Sink.PublishGeometry(lanes);

        public void PublishLifecycle(in LifecycleRecord record)
        {
            Lifecycles.Add(record);
            Lines.Add($"L {record.Handle} {record.IsSpawn} {record.Name} {record.Tag.ExternalType}");
            Bus.Sink.PublishLifecycle(record);
        }

        public void PublishFrame(uint step, double time, ReadOnlySpan<VehicleRecord> movers)
        {
            var sb = new StringBuilder();
            sb.Append("F ").Append(step).Append(' ').Append(time.ToString("R", CultureInfo.InvariantCulture));
            foreach (var m in movers)
            {
                sb.Append(" | ").Append(m.Handle).Append(' ').Append(m.LaneHandle).Append(' ')
                    .Append(m.Pos.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(m.Speed.ToString("R", CultureInfo.InvariantCulture));
            }

            Lines.Add(sb.ToString());
            Bus.Sink.PublishFrame(step, time, movers);
        }

        public void PublishTrafficLights(uint step, double time, IReadOnlyList<TlCodec.TlEntry> lights) =>
            Bus.Sink.PublishTrafficLights(step, time, lights);

        public void Dispose() { }
    }

    private static void WithSim(int cars, Action<LiveCitySim, Tee, List<string>> body, Action<LiveCityConfig>? tweak = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "livecity-h15-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "net.xml"), ChainNetXml);
        var cfg = LiveCityConfig.ForDataset(dir);
        cfg.X0 = 0; cfg.Y0 = 0; cfg.X1 = 1000; cfg.Y1 = 500;
        cfg.CarTargetConcurrent = cars;
        var issued = new List<string>();
        cfg.VehicleIdProvider = () => { var n = "car-" + (issued.Count + 1); issued.Add(n); return n; };
        cfg.VehicleTypes = new[]
        {
            new LiveCityVehicleType("civ", "passenger", Length: 5.0, Width: 1.8, MaxSpeed: null, Weight: 1.0, ExternalType: 7001, Color: 0x112233),
        };
        tweak?.Invoke(cfg);
        var tee = new Tee();
        try
        {
            using var sim = new LiveCitySim(cfg, recordVehSink: tee);
            body(sim, tee, issued);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The front-bumper point the wire's latest sample for `handle` dead-reckons to at `time` (lane arc + v*dt + a*dt^2/2,
    // walked along the published lane geometry) -- the receiver's view of the car.
    private static (double X, double Y) WirePoseAt(IReplicationSource src, VehicleHandle handle, double time)
    {
        Assert.True(src.TryGetLatest(handle, out var sample));
        var r = sample.Record;
        var dt = time - sample.TimestampSeconds;
        var pos = r.Pos + (r.Speed * dt) + (0.5 * r.Accel * dt * dt);
        var pts = src.Geometry[r.LaneHandle].Points;
        for (var i = 1; i < pts.Length; i++)
        {
            var seg = Math.Sqrt(((pts[i].X - pts[i - 1].X) * (pts[i].X - pts[i - 1].X)) + ((pts[i].Y - pts[i - 1].Y) * (pts[i].Y - pts[i - 1].Y)));
            if (pos <= seg || i == pts.Length - 1)
            {
                var f = seg > 0 ? pos / seg : 0;
                return (pts[i - 1].X + (f * (pts[i].X - pts[i - 1].X)), pts[i - 1].Y + (f * (pts[i].Y - pts[i - 1].Y)));
            }

            pos -= seg;
        }

        return (pts[0].X, pts[0].Y);
    }

    // A demand car on the road and moving (more than 1 m in the last step), by its last two snapshots.
    private static LiveCityCar MovingCar(LiveCitySim sim, Dictionary<VehicleHandle, double> prevX)
    {
        var moving = sim.SampleCars().Where(c => prevX.TryGetValue(c.Handle, out var px) && c.X - px > 1.0).ToList();
        Assert.NotEmpty(moving);
        return moving[0];
    }

    [Fact]
    public void H15_RemoveAMovingDemandCar_MidRun_ReturnsItsState_AndNeverReappears()
    {
        WithSim(cars: 3, (sim, tee, issued) =>
        {
            var prevX = new Dictionary<VehicleHandle, double>();
            for (var i = 0; i < 60; i++)
            {
                prevX = sim.SampleCars().ToDictionary(c => c.Handle, c => c.X);
                sim.Step();
            }

            var car = MovingCar(sim, prevX);
            Assert.Contains(car.Name, issued);
            Assert.Contains(car.Name, sim.VehicleSource.Names.Values);
            var wire = WirePoseAt(sim.VehicleSource, car.Handle, sim.Time);
            var arrivedBefore = sim.ArrivedTotal;

            Assert.True(sim.RemoveVehicle(car.Name, out var state));
            _out.WriteLine($"removed {car.Name} at t={sim.Time}: centre=({state.X:F3},{state.Y:F3},{state.Z:F3}) heading={state.Heading:F4} " +
                           $"speed={state.Speed:F3} vType={state.VType} ext={state.ExternalType}; front was ({car.X:F3},{car.Y:F3}); wire front ({wire.X:F3},{wire.Y:F3})");

            // Centre = front - L/2 along the heading; the heading is east on this chain; the type and tag are the spawn's.
            Assert.InRange(state.Heading, -0.01, 0.01);
            Assert.Equal(car.X - 2.5, state.X, 3);
            Assert.Equal(car.Y, state.Y, 3);
            Assert.Equal(0.0, state.Z);
            var frontX = state.X + (2.5 * Math.Cos(state.Heading));
            var frontY = state.Y + (2.5 * Math.Sin(state.Heading));
            var gap = Math.Sqrt(((frontX - wire.X) * (frontX - wire.X)) + ((frontY - wire.Y) * (frontY - wire.Y)));
            Assert.True(gap < 1.0, $"state is {gap:F3} m from the wire's pose");
            Assert.InRange(state.Speed, 3.0, 13.9 + 0.5);
            Assert.Equal("civ", state.VType);
            Assert.Equal(7001L, state.ExternalType);

            // The wire saw it go -- now, without a Step: the live bus (self-pumped) and the tee's receiver.
            Assert.DoesNotContain(car.Name, sim.VehicleSource.Names.Values);
            Assert.False(sim.VehicleSource.History.ContainsKey(car.Handle));
            tee.Bus.Source.Pump();
            Assert.DoesNotContain(car.Name, tee.Bus.Source.Names.Values);
            Assert.DoesNotContain(car.Handle, tee.Bus.Source.Dims.Keys);
            Assert.DoesNotContain(sim.SampleCars(), c => c.Name == car.Name);

            for (var i = 0; i < 120; i++)
            {
                sim.Step();
                Assert.DoesNotContain(sim.SampleCars(), c => c.Name == car.Name);
                Assert.DoesNotContain(car.Name, sim.VehicleSource.Names.Values);
            }

            // Exactly one tombstone for the handle (no second one from the next snapshot diff), one spawn for the name.
            Assert.Single(tee.Lifecycles, r => r.IsSpawn && r.Name == car.Name);
            Assert.Single(tee.Lifecycles, r => !r.IsSpawn && r.Handle.Equals(car.Handle));
            Assert.Equal(1L, sim.RemovedVehicleCount);
            Assert.True(sim.ArrivedTotal >= arrivedBefore);
            Assert.False(sim.RemoveVehicle(car.Name, out _));
        });
    }

    // The one that fails without the immediate forward: a paused host (no Step after the removal) still sees the despawn.
    [Fact]
    public void H15_TheDespawnReachesTheWire_WhilePaused()
    {
        WithSim(cars: 3, (sim, tee, issued) =>
        {
            for (var i = 0; i < 40; i++) sim.Step();
            var car = sim.SampleCars()[0];
            var lifecyclesBefore = tee.Lifecycles.Count;
            tee.Bus.Source.Pump();
            Assert.Contains(car.Name, tee.Bus.Source.Names.Values);

            Assert.True(sim.RemoveVehicle(car.Name, out _));

            // No Step: the live bus and the record/DDS tee already carry the tombstone.
            Assert.Equal(lifecyclesBefore + 1, tee.Lifecycles.Count);
            var tomb = tee.Lifecycles[^1];
            Assert.False(tomb.IsSpawn);
            Assert.Equal(car.Handle, tomb.Handle);
            Assert.DoesNotContain(car.Name, sim.VehicleSource.Names.Values);
            Assert.False(sim.VehicleSource.Dims.ContainsKey(car.Handle));
            tee.Bus.Source.Pump();
            Assert.DoesNotContain(car.Name, tee.Bus.Source.Names.Values);
            Assert.False(tee.Bus.Source.History.ContainsKey(car.Handle));
        });
    }

    // An H13 shadow by its id, Active and then paused; a Pending shadow (spawned, no Step yet) has no pose and no record.
    [Fact]
    public void H15_AShadowIsRemovableByItsId_PausedToo_AndAPendingOneHasNoPose()
    {
        WithSim(cars: 0, (sim, tee, issued) =>
        {
            Assert.True(sim.SpawnExternalVehicle("hrot:5", 120.0, 900.0, 0.0, 10.0, 290.0, 900.0));
            for (var i = 0; i < 6; i++) sim.Step();
            Assert.Contains("hrot:5", sim.VehicleSource.Names.Values);

            Assert.True(sim.RemoveVehicle("hrot:5", out var state));
            Assert.False(sim.IsExternalVehicleLive("hrot:5"));
            Assert.False(sim.ExternalVehicleArrived("hrot:5"));
            Assert.DoesNotContain("hrot:5", sim.VehicleSource.Names.Values); // no Step
            Assert.InRange(state.Y, 899.9, 900.1);
            Assert.Equal(0L, sim.RemovedVehicleCount); // the host's car, not demand's
            Assert.Equal(0L, state.ExternalType);

            sim.Step();
            Assert.Equal(0L, sim.ArrivedTotal); // the despawned slot's lifecycle event is not an arrival
            Assert.Single(tee.Lifecycles, r => !r.IsSpawn);

            // Pending: spawned between steps, removed before any Step -- true, NaN pose, nothing on the wire.
            Assert.True(sim.SpawnExternalVehicle("hrot:6", 120.0, 900.0, 0.0, 10.0, 290.0, 900.0));
            var count = tee.Lifecycles.Count;
            Assert.True(sim.RemoveVehicle("hrot:6", out var pending));
            Assert.True(double.IsNaN(pending.X) && double.IsNaN(pending.Heading));
            Assert.Equal(0.0, pending.Speed);
            Assert.Equal(count, tee.Lifecycles.Count);
            for (var i = 0; i < 5; i++) sim.Step();
            Assert.Equal(count, tee.Lifecycles.Count);
            Assert.Empty(sim.SampleCars());
            Assert.Equal(0L, sim.ArrivedTotal);
            Assert.Empty(issued);
        });
    }

    // Unknown names are false; a second removal is false; a removal is not an arrival and frees the demand slot at once.
    [Fact]
    public void H15_UnknownAndRepeatedNamesAreFalse_ArrivedTotalUnchanged_AndDemandRefillsTheSlot()
    {
        WithSim(cars: 1, (sim, tee, issued) =>
        {
            Assert.False(sim.RemoveVehicle("nobody", out _));
            Assert.False(sim.RemoveVehicle("", out _));
            Assert.False(sim.RemoveVehicle(null!, out _));

            for (var i = 0; i < 4; i++) sim.Step();
            var car = Assert.Single(sim.SampleCars());

            Assert.True(sim.RemoveVehicle(car.Name, out _));
            Assert.False(sim.RemoveVehicle(car.Name, out _));
            Assert.False(sim.DespawnExternalVehicle(car.Name)); // a demand car is not a shadow

            var attempts = issued.Count; // the provider is called once per spawn attempt (an unroutable pair consumes one)
            sim.Step();
            Assert.Equal(0L, sim.ArrivedTotal); // no car can have finished the 400 m chain by now
            Assert.Equal(1L, sim.RemovedVehicleCount);
            Assert.True(issued.Count > attempts, "the very next step must try to refill the one slot");
            Assert.False(sim.RemoveVehicle(car.Name, out _));
            Assert.Single(tee.Lifecycles, r => !r.IsSpawn);
        });
    }

    // Two identical runs that remove the same car at the same step stay identical afterwards (every lifecycle record and
    // every frame on the tee); a run whose only calls are false (unknown names) is identical to a run with none.
    [Fact]
    public void H15_Determinism_AndAFalseCallChangesNothing()
    {
        static List<string> Run(Action<LiveCitySim, int>? between, out long removed)
        {
            List<string>? lines = null;
            long r = 0;
            WithSim(cars: 4, (sim, tee, _) =>
            {
                for (var i = 0; i < 160; i++)
                {
                    sim.Step();
                    between?.Invoke(sim, i);
                }

                lines = tee.Lines.ToList();
                r = sim.RemovedVehicleCount;
            });
            removed = r;
            return lines!;
        }

        static void RemoveAt(LiveCitySim sim, int i)
        {
            if (i == 50 || i == 90)
            {
                var victim = sim.SampleCars().OrderBy(c => c.Name, StringComparer.Ordinal).First().Name;
                Assert.True(sim.RemoveVehicle(victim, out _));
            }
        }

        var a = Run(RemoveAt, out var removedA);
        var b = Run(RemoveAt, out var removedB);
        Assert.Equal(2L, removedA);
        Assert.Equal(removedA, removedB);
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++) Assert.True(a[i] == b[i], $"line {i} diverged\n  {a[i]}\n  {b[i]}");

        var none = Run(null, out _);
        var falseOnly = Run((sim, i) => Assert.False(sim.RemoveVehicle("ghost-" + i, out _)), out var removedF);
        Assert.Equal(0L, removedF);
        Assert.Equal(none.Count, falseOnly.Count);
        for (var i = 0; i < none.Count; i++) Assert.True(none[i] == falseOnly[i], $"line {i} diverged\n  {none[i]}\n  {falseOnly[i]}");
        Assert.False(a.SequenceEqual(none)); // the removals did change the run
    }
}
