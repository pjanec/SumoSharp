using CycloneDDS.Runtime;
using Sim.Core;
using Sim.Core.Orca;
using Sim.Replication;
using Sim.Replication.Dds;
using Xunit;

namespace Sim.Replication.Dds.Tests;

// docs/HROT-INTEGRATION-DESIGN.md H14 -- the ped wire over DDS: names on the lifecycle topic, the `Source` key on all
// four ped topics, DdsMultiSourcePedSubscriber keeping publishers apart, a Despawn dropping one ped, a silent source
// expiring, and the float64 PathArc start time. Domains 232 and 220 (unused elsewhere; 233+ overflows the discovery port range). Discovery is asynchronous, so
// every wait polls against a deadline.
public class HrotPedDdsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private static bool PollUntil(Func<bool> done, Action eachRound)
    {
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            eachRound();
            if (done()) return true;
            Thread.Sleep(50);
        }

        return done();
    }

    private static void Spawn(DdsPedReplicationSink sink, uint index, string name, double start, Vec2 a, Vec2 b)
    {
        var h = new VehicleHandle(index, 0);
        sink.PublishPathArc(new PathArcRecord(h, speed: 1.0, startTime: start, new[] { a, b }));
        sink.PublishPedLifecycle(new PedLifecycleRecord(h, PedLifecycleKind.Spawn, start, name));
    }

    [Fact]
    public void H14_TheMultiSourcePedSubscriber_KeepsSourcesApart_DropsADespawn_AndExpiresASilentSource()
    {
        using var participant = new DdsParticipant(232);
        var now = 0.0;
        using var multi = new DdsMultiSourcePedSubscriber(participant, sourceTimeoutSeconds: 5.0, clockSeconds: () => now);
        using var sinkA = new DdsPedReplicationSink(participant, source: 1, float64StartTimes: true);
        using var sinkB = new DdsPedReplicationSink(participant, source: 2);

        // Both publishers use ped index 5 -- distinct instances only because of the Source key.
        const double t0 = 1_000_000.03; // float32 would put this 0.03 s off
        Spawn(sinkA, 5, "hrot:a5", t0, new Vec2(0, 0), new Vec2(100, 0));
        Spawn(sinkB, 5, "hrot:b5", 100.0, new Vec2(0, 50), new Vec2(0, 150));

        Assert.True(PollUntil(
            () => multi.Sources.TryGetValue(1, out var a) && a.TryGetId("hrot:a5", out _)
                  && multi.Sources.TryGetValue(2, out var b) && b.TryGetId("hrot:b5", out _),
            multi.Pump), $"sources: {string.Join(",", multi.Sources.Keys)}");

        var srcA = multi.Sources[1];
        var srcB = multi.Sources[2];
        Assert.False(srcA.TryGetId("hrot:b5", out _));
        Assert.False(srcB.TryGetId("hrot:a5", out _));
        Assert.Equal(t0, multi.RawSources[1].PathArcs.Single().StartTime); // float64 start time survived DDS

        Assert.True(srcA.TryGetPoseAt("hrot:a5", t0 + 2.0, out var pa, out _, out _));
        Assert.True(srcB.TryGetPoseAt("hrot:b5", 102.0, out var pb, out _, out _));
        Assert.True((pa - new Vec2(2, 0)).Abs < 0.02, $"a5 at {pa}");
        Assert.True((pb - new Vec2(0, 52)).Abs < 0.02, $"b5 at {pb}");

        // A Despawn on A drops A's ped 5 only (and A's sink disposes its instances -- the source stays).
        sinkA.PublishPedLifecycle(new PedLifecycleRecord(new VehicleHandle(5, 0), PedLifecycleKind.Despawn, t0 + 3.0));
        Assert.True(PollUntil(() => !srcA.TryGetId("hrot:a5", out _), multi.Pump), "A's despawn never landed");
        Assert.True(multi.Sources.ContainsKey(1));
        Assert.DoesNotContain(5, srcA.KnownIds);
        Assert.True(srcB.TryGetId("hrot:b5", out _));

        // A keeps talking, B goes silent: at the timeout only B is removed.
        now = 4.0;
        Spawn(sinkA, 6, "hrot:a6", t0 + 4.0, new Vec2(0, 0), new Vec2(10, 0));
        Assert.True(PollUntil(() => srcA.TryGetId("hrot:a6", out _), multi.Pump), "A's second ped never landed");
        now = 8.0;
        multi.Pump();
        Assert.True(multi.Sources.ContainsKey(1));
        Assert.False(multi.Sources.ContainsKey(2));
        Assert.Equal(1, multi.SourcesRemoved);
    }

    [Fact]
    public void H14_TheSingleSourcePedReader_SeesNamesAndDespawns()
    {
        using var participant = new DdsParticipant(220);
        using var sink = new DdsPedReplicationSink(participant, source: 3);
        using var source = new DdsPedReplicationSource(participant);

        Spawn(sink, 9, "hrot:9", 10.0, new Vec2(0, 0), new Vec2(5, 0));
        Assert.True(PollUntil(() => source.Lifecycles.Any(l => l.Kind == PedLifecycleKind.Spawn), source.Pump));
        Assert.Equal("hrot:9", source.Lifecycles.First(l => l.Kind == PedLifecycleKind.Spawn).Name);

        sink.PublishPedLifecycle(new PedLifecycleRecord(new VehicleHandle(9, 0), PedLifecycleKind.Despawn, 12.0));
        Assert.True(PollUntil(() => source.Lifecycles.Any(l => l.Kind == PedLifecycleKind.Despawn), source.Pump));
        Assert.Equal(12.0, source.LatestSimTime);
    }
}
