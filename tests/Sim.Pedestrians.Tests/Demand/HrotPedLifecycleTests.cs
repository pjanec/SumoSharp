using Sim.Core;
using Sim.Core.Bridge;
using Sim.Core.Orca;
using Sim.Pedestrians.Demand;
using Sim.Pedestrians.Lod;
using Sim.Pedestrians.Navigation.Bake;
using Sim.Replication;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests.Demand;

// docs/HROT-INTEGRATION-DESIGN.md H14 -- host-named peds, their lifecycle on the ped wire (Spawn with the name,
// Despawn on arrival AND on removal), PedDemand.RemovePed mid-run, the float64 PathArc start time, and sim-time poses
// on the receiving side. Same POC-0 fixture + SUMO-bake navmesh as PedDemandTests (short trips, so peds arrive fast).
public class HrotPedLifecycleTests
{
    private readonly ITestOutputHelper _out;
    public HrotPedLifecycleTests(ITestOutputHelper output) => _out = output;

    private const double Dt = 0.1;
    private static readonly Vec2 WestNorthArm = new(112.6, 140.0);
    private static readonly Vec2 EastNorthArm = new(127.4, 140.0);

    private static SumoNavMesh BuildNav()
    {
        var network = PedNetworkParser.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "net.net.xml"),
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "walkable.add.xml"));
        var polygons = WalkablePolygonBaker.Bake(network);
        return new SumoNavMesh(polygons, new SumoWalkableSpace(polygons));
    }

    private sealed class Rig
    {
        public Rig(Func<string>? idProvider, bool announce, ulong seed = 12345UL)
        {
            var nav = BuildNav();
            Publisher = new PedPublisher();
            Manager = new PedLodManager(nav, Publisher, 0.3, 0.5);
            Demand = new PedDemand(new PedDemandConfig
            {
                Origins = new[] { WestNorthArm, EastNorthArm },
                Destinations = new[] { WestNorthArm, EastNorthArm },
                SpawnRatePerSecond = 3.0,
                PopulationCap = 10,
                Seed = seed,
                MaxSpeed = 1.4,
                Radius = 0.3,
                ArrivalRadius = 0.5,
                IdProvider = idProvider,
                AnnounceLifecycle = announce,
            }, nav, Manager);
            Wire = new PedReplicationPublisher(Bus.Sink);
            Remote = new PedRemoteReconstructor(Bus.Source);
        }

        public PedPublisher Publisher { get; }
        public PedLodManager Manager { get; }
        public PedDemand Demand { get; }
        public InMemoryPedReplicationBus Bus { get; } = new();
        public PedReplicationPublisher Wire { get; }
        public PedRemoteReconstructor Remote { get; }
        public double Now { get; private set; }
        private int _published;

        public void Step(int steps = 1)
        {
            for (var i = 0; i < steps; i++)
            {
                Demand.Step(Now, Dt, new InterestField(), Array.Empty<WorldDisc>());
                Now += Dt;
                Flush();
            }
        }

        // Forward whatever the in-process publisher appended since the last flush (removals happen between steps).
        public void Flush()
        {
            var batch = Publisher.Events.Skip(_published).ToList();
            _published = Publisher.Events.Count;
            Wire.Publish(batch);
            Remote.Pump(Now);
        }
    }

    [Fact]
    public void H14_NamedPeds_AnnounceTheirName_AndAnArrivalPublishesDespawn()
    {
        var issued = new List<string>();
        var rig = new Rig(() => { var n = "net-" + (issued.Count + 1000); issued.Add(n); return n; }, announce: false);
        rig.Step(600); // 60 s: many 15 m trips complete

        Assert.True(rig.Demand.ArrivalCount > 3, $"arrivals {rig.Demand.ArrivalCount}");
        Assert.Equal(0, rig.Demand.NameRejectCount);

        var lifecycles = rig.Bus.Source.Lifecycles;
        var spawns = lifecycles.Where(l => l.Kind == PedLifecycleKind.Spawn).ToList();
        Assert.Equal(rig.Demand.SpawnCount, spawns.Count);
        Assert.Equal(issued.Take(spawns.Count), spawns.Select(s => s.Name)); // provider order = spawn order

        // Every arrival is a Despawn on the wire, and the reconstructor dropped the ped and its name.
        foreach (var arrival in rig.Demand.ArrivalEvents)
        {
            Assert.Contains(lifecycles, l => l.Kind == PedLifecycleKind.Despawn && (int)l.Handle.Index == arrival.Id);
            Assert.DoesNotContain(arrival.Id, rig.Remote.KnownIds);
            Assert.False(rig.Remote.TryGetName(arrival.Id, out _));
            Assert.False(rig.Remote.Ig.Knows(arrival.Id));
        }

        // The live set on both sides is the same peds under the same names.
        Assert.Equal(rig.Demand.LiveIds.OrderBy(i => i), rig.Remote.KnownIds.OrderBy(i => i));
        foreach (var id in rig.Demand.LiveIds)
        {
            Assert.True(rig.Remote.TryGetName(id, out var name));
            Assert.Equal(rig.Demand.NameOf(id), name);
            Assert.True(rig.Remote.TryGetId(name, out var back) && back == id);
        }
    }

    [Fact]
    public void H14_RemovePed_MidRun_ReturnsItsState_ThenTheRunGoesOn_AndTheRemoteDropsIt()
    {
        var rig = new Rig(() => "p" + Guid.NewGuid().ToString("N")[..8], announce: false);
        rig.Step(40);
        var id = rig.Demand.LiveIds.First(i => rig.Manager.VelocityOf(i, rig.Now).Abs > 0.5); // a walking ped
        var name = rig.Demand.NameOf(id);
        var expected = rig.Manager.PositionOf(id, rig.Now);
        Assert.True(rig.Remote.TryGetId(name, out _));

        Assert.True(rig.Demand.TryGetIdByName(name, out var byName) && byName == id);
        Assert.True(rig.Demand.RemovePed(id, rig.Now, out var state));
        _out.WriteLine($"removed {name}: ({state.X:F2},{state.Y:F2},{state.Z:F2}) v=({state.Vx:F2},{state.Vy:F2}) heading={state.Heading:F3}");
        Assert.Equal(expected.X, state.X, 9);
        Assert.Equal(expected.Y, state.Y, 9);
        var speed = Math.Sqrt((state.Vx * state.Vx) + (state.Vy * state.Vy));
        Assert.InRange(speed, 0.5, 1.4 * 1.05); // walking, never faster than its max speed
        Assert.Equal(Math.Atan2(state.Vy, state.Vx), state.Heading, 12);

        Assert.False(rig.Demand.RemovePed(id, rig.Now, out _));
        Assert.False(rig.Demand.TryGetIdByName(name, out _));
        Assert.Equal(1, rig.Demand.RemovedCount);

        rig.Flush();
        Assert.Contains(rig.Bus.Source.Lifecycles, l => l.Kind == PedLifecycleKind.Despawn && (int)l.Handle.Index == id);
        Assert.False(rig.Remote.TryGetId(name, out _));
        Assert.DoesNotContain(id, rig.Remote.KnownIds);

        var ex = Record.Exception(() => rig.Step(300)); // RemovePed under PedDemand used to throw on the next Step
        Assert.Null(ex);
        Assert.Equal(rig.Demand.SpawnCount - rig.Demand.ArrivalCount - rig.Demand.RemovedCount, rig.Demand.LiveCount);
    }

    [Fact]
    public void H14_Removal_IsDeterministic_AndOffByDefault_TheStreamIsUnchanged()
    {
        static List<string> Run(bool announce, bool remove)
        {
            var rig = new Rig(null, announce);
            var trace = new List<string>();
            for (var i = 0; i < 300; i++)
            {
                rig.Step();
                if (remove && i == 50)
                {
                    Assert.True(rig.Demand.RemovePed(rig.Demand.LiveIds.Min(), rig.Now, out _));
                }
            }

            foreach (var e in rig.Publisher.Events)
            {
                trace.Add(e switch
                {
                    Sim.Pedestrians.Lod.PathArcRecord p => $"arc {p.Id} {p.StartTime:R} {p.Path.Count}",
                    PedSpawnedRecord s => $"spawn {s.Id} {s.Name}",
                    PedDespawnedRecord d => $"despawn {d.Id} {d.Time:R}",
                    _ => $"{e.GetType().Name} {e.Id} {e.Time:R}",
                });
            }

            return trace;
        }

        var plain = Run(announce: false, remove: false);
        Assert.DoesNotContain(plain, t => t.StartsWith("spawn") || t.StartsWith("despawn")); // default: no new events

        var announced = Run(announce: true, remove: false);
        Assert.Contains(announced, t => t.StartsWith("spawn"));
        Assert.Equal(plain, announced.Where(t => !t.StartsWith("spawn") && !t.StartsWith("despawn"))); // only additions

        Assert.Equal(Run(announce: true, remove: true), Run(announce: true, remove: true)); // reproducible
    }

    [Fact]
    public void H14_PathArcStartTime_Float64RoundTripsExactly_WhereFloat32Quantizes()
    {
        const double t = 1_000_000.03; // float32 spacing here is 0.0625 s
        var rec = new[] { new Sim.Replication.PathArcRecord(new VehicleHandle(7, 0), 1.3, t, new[] { new Vec2(1, 2), new Vec2(3, 4) }) };

        var wide = new byte[FrameCodec.PathArcFrameSize(rec, float64StartTime: true)];
        FrameCodec.WritePathArcFrame(wide, 0, 0f, rec, float64StartTime: true);
        Assert.Equal(FrameCodec.KindPathArc64, FrameCodec.ReadHeader(wide).Kind);
        Assert.Equal(t, FrameCodec.ReadPathArcFrame(wide)[0].StartTime);

        var narrow = new byte[FrameCodec.PathArcFrameSize(rec)];
        FrameCodec.WritePathArcFrame(narrow, 0, 0f, rec);
        Assert.Equal(FrameCodec.KindPathArc, FrameCodec.ReadHeader(narrow).Kind);
        var lost = Math.Abs(FrameCodec.ReadPathArcFrame(narrow)[0].StartTime - t);
        _out.WriteLine($"float32 startTime error at t={t}: {lost} s");
        Assert.True(lost > 0.01);
        Assert.Equal(narrow.Length + 4, wide.Length);

        // The in-memory bus honours the flag end to end.
        var bus = new InMemoryPedReplicationBus(float64StartTimes: true);
        bus.Sink.PublishPathArc(rec[0]);
        bus.Source.Pump();
        Assert.Equal(t, bus.Source.PathArcs[0].StartTime);
    }

    [Fact]
    public void H14_ARemotePoseAtASimTime_IsFrozenWhileTheHostClockIsPaused()
    {
        var rig = new Rig(() => "n" + rig2Counter++, announce: false);
        rig.Step(30);
        var id = rig.Demand.LiveIds.First(i => rig.Manager.VelocityOf(i, rig.Now).Abs > 0.5);
        var name = rig.Demand.NameOf(id);
        var t = rig.Now;

        Assert.True(rig.Remote.TryGetPoseAt(name, t, out var before, out _, out _));
        var server = rig.Manager.PositionOf(id, t);
        Assert.True((before - server).Abs < 0.02, $"remote {before} vs server {server}"); // the wire's cm quantization

        // "Pause": wall time passes and the reader keeps pumping with no new data -- the pose at t does not move.
        Thread.Sleep(120);
        for (var i = 0; i < 5; i++) rig.Remote.Pump(t);
        Assert.True(rig.Remote.TryGetPoseAt(name, t, out var after, out _, out _));
        Assert.Equal(before, after);

        // Advancing the SIM time (not the wall clock) is what moves it.
        Assert.True(rig.Remote.TryGetPoseAt(name, t + 1.0, out var later, out _, out _));
        Assert.True((later - before).Abs > 0.5);

        var live = new List<RemotePedPose>();
        Assert.Equal(rig.Demand.LiveCount, rig.Remote.CopyLivePeds(t, live));
        Assert.Contains(live, p => p.Name == name && Math.Abs(p.X - before.X) < 1e-12);
    }

    private static int rig2Counter;
}
