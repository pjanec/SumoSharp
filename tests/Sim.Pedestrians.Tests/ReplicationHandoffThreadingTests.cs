using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sim.Core;
using Sim.Core.Orca;
using Sim.Replication;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests;

// docs/LIVE-CITY-THREADED-TICK-DESIGN.md §4 hazard 1 + §6 Stage 2/Stage 3: both in-memory replication buses
// were plain `Queue<Entry>` and allocated a fresh payload buffer on EVERY publish. Threading the sim tick
// makes the first a correctness bug and the second the last per-tick allocation on the handoff.
//
// These tests are about the HANDOFF only -- not the sim. They hammer the buses from a producer thread while
// a consumer pumps, which is precisely the shape `LiveCitySource`'s producer creates.
public class ReplicationHandoffThreadingTests
{
    private readonly ITestOutputHelper _output;

    public ReplicationHandoffThreadingTests(ITestOutputHelper output) => _output = output;

    // `Pos`/`PosLat` stand in for the "x/y" this test tracks -- the bus is payload-agnostic, so any two
    // distinguishable fields prove the handoff. `LaneHandle` doubles as an identity check.
    private static VehicleRecord Car(uint id, double pos, double posLat)
        => new(new VehicleHandle(id, 0), DrModel.LaneArc, laneHandle: (int)id,
               pos, posLat, speed: 0.0, accel: 0.0, latSpeed: 0.0, upcoming: default);

    // ---- vehicle bus -------------------------------------------------------------------------------

    [Fact]
    public void VehicleBus_SurvivesAConcurrentProducerAndConsumer_WithNoLostOrTornFrames()
    {
        // The failure this guards: with a non-concurrent Queue this either throws, spins forever, or drops
        // entries -- all of which show up as missing cars on screen rather than as a crash. Every published
        // handle must be present in `History` at the end, with its last-published position intact.
        const int Steps = 2000;
        const int Cars = 40;

        var bus = new InMemoryReplicationBus();

        for (var c = 0; c < Cars; c++)
        {
            bus.Sink.PublishLifecycle(new LifecycleRecord(
                new VehicleHandle((uint)c, 0), isSpawn: true, vTypeId: 0, length: 4.5f, width: 1.8f, name: $"car{c}"));
        }

        var records = new VehicleRecord[Cars];
        var producer = Task.Run(() =>
        {
            for (var s = 0; s < Steps; s++)
            {
                for (var c = 0; c < Cars; c++)
                {
                    records[c] = Car((uint)c, c * 10.0, s);
                }

                bus.Sink.PublishFrame((uint)s, s * 0.5, records);
            }
        });

        var pumps = 0;
        while (!producer.IsCompleted)
        {
            bus.Source.Pump();
            pumps++;
        }

        producer.GetAwaiter().GetResult(); // surface any producer-side exception
        bus.Source.Pump();                 // final drain

        Assert.Equal(Cars, bus.Source.History.Count);
        for (var c = 0; c < Cars; c++)
        {
            Assert.True(bus.Source.TryGetLatest(new VehicleHandle((uint)c, 0), out var sample));
            Assert.Equal(c * 10.0, sample.Record.Pos, 3);
            Assert.Equal(Steps - 1.0, sample.Record.PosLat, 3);
        }

        Assert.Equal((Steps - 1) * 0.5, bus.Source.LatestVehicleSampleTime!.Value, 6);
        _output.WriteLine($"vehicle bus: {Steps} frames x {Cars} cars across {pumps} concurrent pumps, "
            + $"{bus.MoverBuffersAllocated} buffers allocated");
    }

    [Fact]
    public void VehicleBus_MoverBuffersArePooled_SoSteadyStatePublishingDoesNotAllocate()
    {
        // Stage 2's zero-alloc requirement, asserted on the mechanism rather than on a GC counter (which
        // would be flaky). `PublishFrame` used to do `movers.ToArray()` -- one array per step forever.
        var bus = new InMemoryReplicationBus();
        var records = Enumerable.Range(0, 200).Select(i => Car((uint)i, i, 0)).ToArray();

        // Warm up: pump between publishes so buffers are returned to the pool.
        for (var s = 0; s < 5; s++)
        {
            bus.Sink.PublishFrame((uint)s, s, records);
            bus.Source.Pump();
        }

        var afterWarmup = bus.MoverBuffersAllocated;

        for (var s = 0; s < 500; s++)
        {
            bus.Sink.PublishFrame((uint)(100 + s), s, records);
            bus.Source.Pump();
        }

        Assert.Equal(afterWarmup, bus.MoverBuffersAllocated);
        _output.WriteLine($"vehicle bus: {afterWarmup} buffers covered 505 frames of 200 cars "
            + "(0 further allocations over the last 500)");
    }

    [Fact]
    public void VehicleBus_APooledBufferLongerThanTheFrame_DoesNotLeakStaleCarsIntoTheNextFrame()
    {
        // THE hazard pooling introduces: a recycled buffer still holds the previous frame's records past the
        // new frame's end. `Entry.MoverCount` is what stops those being read. A big frame then a small one
        // is the exact sequence that exposes it.
        var bus = new InMemoryReplicationBus();

        var big = Enumerable.Range(0, 50).Select(i => Car((uint)i, i, 111)).ToArray();
        bus.Sink.PublishFrame(1, 1.0, big);
        bus.Source.Pump();
        Assert.Equal(50, bus.Source.History.Count);

        // A one-car frame that rents the (50-long) buffer back. Only car 0 may be updated.
        bus.Sink.PublishFrame(2, 2.0, new[] { Car(0, 999.0, 222.0) });
        bus.Source.Pump();

        Assert.True(bus.Source.TryGetLatest(new VehicleHandle(0, 0), out var updated));
        Assert.Equal(222.0, updated.Record.PosLat, 3);

        // Every other car's newest sample must still be from the FIRST frame -- if the stale slack had been
        // read, they would have been re-appended at time 2.0.
        for (var c = 1; c < 50; c++)
        {
            Assert.True(bus.Source.TryGetLatest(new VehicleHandle((uint)c, 0), out var s));
            Assert.Equal(1.0, s.TimestampSeconds, 6);
            Assert.Equal(111.0, s.Record.PosLat, 3);
        }
    }

    // ---- ped bus -----------------------------------------------------------------------------------

    private static PedFreeKinematicRecord Ped(uint id, double x, double y)
        => new(new VehicleHandle(id, 0), x, y, vx: 0.0, vy: 0.0, radius: 0.25);

    [Fact]
    public void PedBus_SurvivesAConcurrentProducerAndConsumer()
    {
        const int Steps = 2000;
        var bus = new InMemoryPedReplicationBus();
        var recs = Enumerable.Range(0, 30).Select(i => Ped((uint)i, i, 0f)).ToArray();

        var producer = Task.Run(() =>
        {
            for (var s = 0; s < Steps; s++)
            {
                for (var i = 0; i < recs.Length; i++)
                {
                    recs[i] = Ped((uint)i, i, s);
                }

                bus.Sink.PublishCrowdFrame((uint)s, s * 0.5f, recs);
            }
        });

        while (!producer.IsCompleted)
        {
            bus.Source.Pump();
        }

        producer.GetAwaiter().GetResult();
        bus.Source.Pump();

        Assert.Equal((uint)(Steps - 1), bus.Source.LatestCrowdStep);
        Assert.Equal(30, bus.Source.LatestCrowdFrame.Count);
        Assert.Equal(Steps - 1.0, bus.Source.LatestCrowdFrame[0].Y, 1);
        _output.WriteLine($"ped bus: {Steps} crowd frames, {bus.BuffersAllocated} buffers allocated");
    }

    [Fact]
    public void PedBus_PayloadBuffersArePooled()
    {
        var bus = new InMemoryPedReplicationBus();
        var recs = Enumerable.Range(0, 100).Select(i => Ped((uint)i, i, 0f)).ToArray();

        for (var s = 0; s < 5; s++)
        {
            bus.Sink.PublishCrowdFrame((uint)s, s, recs);
            bus.Source.Pump();
        }

        var afterWarmup = bus.BuffersAllocated;

        for (var s = 0; s < 500; s++)
        {
            bus.Sink.PublishCrowdFrame((uint)(100 + s), s, recs);
            bus.Source.Pump();
        }

        Assert.Equal(afterWarmup, bus.BuffersAllocated);
        _output.WriteLine($"ped bus: {afterWarmup} buffers covered 505 crowd frames of 100 peds");
    }

    [Fact]
    public void PedBus_APooledBufferLongerThanTheTimelineBlob_DoesNotAppendStaleTrailingBytes()
    {
        // The ActivityTimeline branch is the one that slices its payload BY LENGTH rather than reading a
        // self-describing header, so it is the one a pooled over-long buffer would corrupt -- the retained
        // blob would carry the previous publish's slack, and the receiver would decode garbage.
        var bus = new InMemoryPedReplicationBus();

        var big = new byte[400];
        for (var i = 0; i < big.Length; i++)
        {
            big[i] = 0xAB;
        }

        bus.Sink.PublishActivityTimeline(new VehicleHandle(1, 0), big);
        bus.Source.Pump();

        var small = new byte[3] { 1, 2, 3 };
        bus.Sink.PublishActivityTimeline(new VehicleHandle(2, 0), small);
        bus.Source.Pump();

        Assert.Equal(2, bus.Source.ActivityTimelines.Count);

        var (handle, blob) = bus.Source.ActivityTimelines[1];
        Assert.Equal(2u, handle.Index);
        Assert.Equal(3, blob.Length); // NOT 400, and NOT padded with 0xAB
        Assert.Equal(new byte[] { 1, 2, 3 }, blob);

        Assert.Equal(400, bus.Source.ActivityTimelines[0].TimelineBytes.Length);
        Assert.All(bus.Source.ActivityTimelines[0].TimelineBytes, b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public void PedBus_PathArcAndLifecycleRoundTripUnchanged_OverAPooledBuffer()
    {
        // The two header-driven branches: a pooled buffer's trailing slack must be ignored because the codec
        // strides by the header's own count. Publishing a LONG frame then a SHORT one reuses the big buffer.
        var bus = new InMemoryPedReplicationBus();

        var longPath = Enumerable.Range(0, 60).Select(i => new Vec2(i, -i)).ToArray();
        bus.Sink.PublishPathArc(new PathArcRecord(new VehicleHandle(7, 0), 1.25, 3.5, longPath));
        bus.Source.Pump();

        bus.Sink.PublishPathArc(new PathArcRecord(
            new VehicleHandle(8, 0), 0.9, 0.5, new[] { new Vec2(10, 20), new Vec2(30, 40) }));
        bus.Sink.PublishPedLifecycle(new PedLifecycleRecord(new VehicleHandle(9, 0), PedLifecycleKind.Spawn, 12.5));
        bus.Source.Pump();

        Assert.Equal(2, bus.Source.PathArcs.Count);
        Assert.Equal(60, bus.Source.PathArcs[0].Path.Count);

        var second = bus.Source.PathArcs[1];
        Assert.Equal(8u, second.Handle.Index);
        Assert.Equal(2, second.Path.Count); // the 60-point slack must NOT be read back
        Assert.Equal(30.0, second.Path[1].X, 2);

        Assert.Single(bus.Source.Lifecycles);
        Assert.Equal(12.5, bus.Source.Lifecycles[0].Time, 6);
        Assert.Equal(PedLifecycleKind.Spawn, bus.Source.Lifecycles[0].Kind);
    }
}
