using CycloneDDS.Runtime;
using CycloneDDS.Runtime.Tracking;
using Sim.Core;
using Sim.Replication;
using Sim.Replication.Dds;
using Xunit;

namespace Sim.Replication.Dds.Tests;

// docs/HROT-INTEGRATION-DESIGN.md H3, H7, H8 — in-process DDS loopback rails. Each test owns one DDS domain
// (221-229, unused elsewhere in this repo) so tests never see each other's samples. Discovery is
// asynchronous, so every wait POLLS (re-writing the volatile frame each round) against a deadline instead of
// sleeping a fixed time.
public class HrotDdsSeamTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private static VehicleRecord Car(VehicleHandle h, double pos) =>
        new(h, DrModel.LaneArc, laneHandle: 3, pos, posLat: 0.0, speed: 10.0, accel: 0.0, latSpeed: 0.0,
            new UpcomingLanes(new[] { 3 }));

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

    // ── H3: sim-time stamps (the rail H3 left pending) ──────────────────────────────────────────

    [Fact]
    public void H3_WithTheFlag_HistoryTimesAreTheFramesSimTimes_WithoutIt_WallClock()
    {
        using var participant = new DdsParticipant(221);
        using var sink = new DdsReplicationSink(participant);
        using var sim = new DdsSubscriber(participant, stampWithSimTime: true);
        using var wall = new DdsSubscriber(participant);
        var h = new VehicleHandle(5, 1);

        var ok = PollUntil(
            () => sim.History.ContainsKey(h) && wall.History.ContainsKey(h),
            () =>
            {
                sink.PublishFrame(7, 1234.5, new[] { Car(h, 1.0) });
                sim.Pump();
                wall.Pump();
            });

        Assert.True(ok, "no frame arrived");
        Assert.Equal(1234.5, sim.History[h][sim.History[h].Count - 1].TimestampSeconds, 6);
        Assert.True(wall.History[h][wall.History[h].Count - 1].TimestampSeconds > 1_000_000_000.0); // Unix-epoch seconds
    }

    // ── H10: a burst of sparse frames reaches a reader that pumps once ──────────────────────────

    [Fact]
    public void H10_FiveFramesBackToBack_EachWithItsOwnCar_AllReachAReaderThatPumpsOnce()
    {
        using var writerSide = new DdsParticipant(226);
        using var readerSide = new DdsParticipant(226);
        using var sink = new DdsReplicationSink(writerSide);
        using var sub = new DdsSubscriber(readerSide, stampWithSimTime: true);
        var probe = new VehicleHandle(100, 1);
        uint step = 1;
        Assert.True(PollUntil(() => sub.History.ContainsKey(probe), () => { sink.PublishFrame(step, step * 0.5, new[] { Car(probe, step) }); step++; sub.Pump(); }),
            "the reader never matched the writer");

        for (uint i = 0; i < 5; i++, step++)
            sink.PublishFrame(step, step * 0.5, new[] { Car(new VehicleHandle(200 + i, 1), 1.0) });
        Thread.Sleep(500);
        sub.Pump();

        var lost = Enumerable.Range(0, 5).Where(i => !sub.History.ContainsKey(new VehicleHandle((uint)(200 + i), 1))).ToList();
        Assert.True(lost.Count == 0, $"KEEP_LAST too shallow: the cars of {lost.Count} of 5 frames were lost");
    }

    // ── H11: every topic is named and QoS-typed ON ITS TYPE (ddsmonitor, 1-arg ctors) ──────────

    [Theory]
    [InlineData(typeof(DdsWireFrame), DdsTopicNames.Vehicles)]
    [InlineData(typeof(DdsVehicleLifecycle), DdsTopicNames.Lifecycle)]
    [InlineData(typeof(DdsNetworkGeometry), DdsTopicNames.Geometry)]
    [InlineData(typeof(DdsTlState), DdsTopicNames.Tl)]
    [InlineData(typeof(DdsViewerCommand), DdsTopicNames.Commands)]
    [InlineData(typeof(DdsViewerStatus), DdsTopicNames.Status)]
    [InlineData(typeof(DdsPedCrowdFrame), DdsTopicNames.PedCrowd)]
    [InlineData(typeof(DdsPedLeg), DdsTopicNames.PedPathArc)]
    [InlineData(typeof(DdsPedActivityLeg), DdsTopicNames.PedActivity)]
    [InlineData(typeof(DdsPedLifecycle), DdsTopicNames.PedLifecycle)]
    public void H11_EveryTopicType_CarriesItsNameAndQos(Type type, string name)
    {
        var topic = type.GetCustomAttributes(typeof(CycloneDDS.Schema.DdsTopicAttribute), false).Cast<CycloneDDS.Schema.DdsTopicAttribute>().Single();
        Assert.Equal(name, topic.TopicName);
        Assert.Single(type.GetCustomAttributes(typeof(CycloneDDS.Schema.DdsQosAttribute), false));
    }

    [Fact]
    public void H11_TheVehicleStream_KeepsTheSparseStreamDepth()
    {
        var q = (CycloneDDS.Schema.DdsQosAttribute)typeof(DdsWireFrame).GetCustomAttributes(typeof(CycloneDDS.Schema.DdsQosAttribute), false).Single();
        Assert.Equal(DdsQos.SparseStreamDepth, q.HistoryDepth);
    }

    /// <summary>The 1-arg ctors still meet: a frame written by the sink reaches a subscriber on another participant.</summary>
    [Fact]
    public void H11_TheImplicitNames_StillMatch_AcrossParticipants()
    {
        using var a = new DdsParticipant(227);
        using var b = new DdsParticipant(227);
        using var sink = new DdsReplicationSink(a);
        using var sub = new DdsSubscriber(b, stampWithSimTime: true);
        var h = new VehicleHandle(9, 1);
        Assert.True(PollUntil(() => sub.History.ContainsKey(h), () => { sink.PublishFrame(1, 0.5, new[] { Car(h, 1.0) }); sub.Pump(); }), "no frame arrived");
    }

    // ── H7: the spawn tag over DDS ──────────────────────────────────────────────────────────────

    [Fact]
    public void H7_TheSpawnTag_CrossesDds_AndIsForgottenOnDespawn()
    {
        using var participant = new DdsParticipant(222);
        using var sink = new DdsReplicationSink(participant);
        using var sub = new DdsSubscriber(participant);
        var tagged = new VehicleHandle(5, 1);
        var plain = new VehicleHandle(6, 1);

        sink.PublishLifecycle(new LifecycleRecord(tagged, true, 0, 4.5f, 1.8f, "car-5", externalType: 0x7FFF_0000_0001L, color: 0xABCDEF));
        sink.PublishLifecycle(new LifecycleRecord(plain, true, 0, 4.5f, 1.8f, "car-6"));
        Assert.True(PollUntil(() => sub.Dims.Count == 2, sub.Pump), "lifecycle did not arrive");

        Assert.Equal(new VehicleTag(0x7FFF_0000_0001L, 0xABCDEF), sub.Tags[tagged]);
        Assert.False(sub.Tags.ContainsKey(plain));

        sink.PublishLifecycle(new LifecycleRecord(tagged, isSpawn: false, vTypeId: 0, length: 0f, width: 0f));
        Assert.True(PollUntil(() => !sub.Dims.ContainsKey(tagged), sub.Pump), "despawn did not arrive");
        Assert.False(sub.Tags.ContainsKey(tagged));
    }

    // ── H8: two publishers, one domain ──────────────────────────────────────────────────────────

    [Fact]
    public void H8_TwoSinksWithTheSameCarHandle_AreTwoSourcesEachWithItsOwnCar()
    {
        using var participant = new DdsParticipant(223);
        using var sinkA = new DdsReplicationSink(participant, source: 1);
        using var sinkB = new DdsReplicationSink(participant, source: 2);
        using var multi = new DdsMultiSourceSubscriber(participant, stampWithSimTime: true);
        using var single = new DdsSubscriber(participant);
        var h = new VehicleHandle(5, 1); // the SAME handle on both publishers

        sinkA.PublishLifecycle(new LifecycleRecord(h, true, 0, 4.0f, 1.7f, "A-car", 11, 0x110000));
        sinkB.PublishLifecycle(new LifecycleRecord(h, true, 0, 9.0f, 2.5f, "B-car", 22, 0x002200));

        var ok = PollUntil(
            () => multi.Sources.Count == 2
                  && multi.Sources.Values.All(s => s.Dims.ContainsKey(h) && s.History.ContainsKey(h)),
            () =>
            {
                sinkA.PublishFrame(1, 100.0, new[] { Car(h, 10.0) });
                sinkB.PublishFrame(1, 200.0, new[] { Car(h, 77.0) });
                multi.Pump();
                single.Pump();
            });
        Assert.True(ok, $"sources={multi.Sources.Count}");

        var a = multi.Sources[1];
        var b = multi.Sources[2];
        Assert.Equal((4.0f, 1.7f), a.Dims[h]);
        Assert.Equal((9.0f, 2.5f), b.Dims[h]);
        Assert.Equal("A-car", a.Names[h]);
        Assert.Equal("B-car", b.Names[h]);
        Assert.Equal(new VehicleTag(11, 0x110000), a.Tags[h]);
        Assert.Equal(new VehicleTag(22, 0x002200), b.Tags[h]);
        Assert.All(Enumerable.Range(0, a.History[h].Count), k => Assert.Equal(10.0, a.History[h][k].Record.Pos, 6));
        Assert.All(Enumerable.Range(0, b.History[h].Count), k => Assert.Equal(77.0, b.History[h][k].Record.Pos, 6));
        Assert.Equal(100.0, a.LatestVehicleSampleTime!.Value, 6); // H3 applies per source
        Assert.Equal(200.0, b.LatestVehicleSampleTime!.Value, 6);
        Assert.True(a.Connected && b.Connected);

        // The single-source reader is unchanged: no demux, the two publishers' cars share one handle.
        Assert.True(single.Dims.ContainsKey(h));
        Assert.Single(single.Dims);
        Assert.True(single.History.ContainsKey(h));
    }

    [Fact]
    public void H8_ASourceWhoseSinkIsDisposed_Disappears_TheOtherStays()
    {
        using var participant = new DdsParticipant(224);
        using var sinkA = new DdsReplicationSink(participant, source: 1);
        var sinkB = new DdsReplicationSink(participant, source: 2);
        using var multi = new DdsMultiSourceSubscriber(participant);
        var h = new VehicleHandle(5, 1);

        sinkA.PublishLifecycle(new LifecycleRecord(h, true, 0, 4f, 1.7f, "A"));
        sinkB.PublishLifecycle(new LifecycleRecord(h, true, 0, 4f, 1.7f, "B"));
        Assert.True(PollUntil(() => multi.Sources.Count == 2, multi.Pump), "both sources never appeared");
        var b = multi.Sources[2];

        sinkB.Dispose();

        Assert.True(PollUntil(() => !multi.Sources.ContainsKey(2), multi.Pump), "source 2 outlived its writers");
        Assert.True(multi.Sources.ContainsKey(1));
        Assert.False(b.Connected);
        Assert.Equal(1, multi.SourcesRemoved);
    }

    [Fact]
    public void H8_AnIdleSource_IsRemovedAfterTheConfiguredTimeout_AndNotBefore()
    {
        using var participant = new DdsParticipant(225);
        using var sink = new DdsReplicationSink(participant, source: 9);
        var now = 0.0;
        using var multi = new DdsMultiSourceSubscriber(participant, stampWithSimTime: false, sourceTimeoutSeconds: 5.0, clockSeconds: () => now);

        sink.PublishLifecycle(new LifecycleRecord(new VehicleHandle(1, 1), true, 0, 4f, 1.7f, "x"));
        Assert.True(PollUntil(() => multi.Sources.ContainsKey(9), multi.Pump), "source never appeared");

        now = 4.0;
        multi.Pump();
        Assert.True(multi.Sources.ContainsKey(9));

        now = 10.0;
        multi.Pump();
        Assert.False(multi.Sources.ContainsKey(9));
    }

    [Fact]
    public void H8_TheTrackedSenderOfEachSource_IsItsParticipantsAppInstanceId()
    {
        const uint domain = 226;
        using var receiver = new DdsParticipant(domain);
        receiver.EnableSenderTracking(new SenderIdentityConfig { AppDomainId = (int)domain, AppInstanceId = 99 });
        using var nodeA = new DdsParticipant(domain);
        nodeA.EnableSenderTracking(new SenderIdentityConfig { AppDomainId = (int)domain, AppInstanceId = 41 });
        using var nodeB = new DdsParticipant(domain);
        nodeB.EnableSenderTracking(new SenderIdentityConfig { AppDomainId = (int)domain, AppInstanceId = 42 });

        using var multi = new DdsMultiSourceSubscriber(receiver);
        using var sinkA = new DdsReplicationSink(nodeA, source: 1);
        using var sinkB = new DdsReplicationSink(nodeB, source: 2);
        var h = new VehicleHandle(5, 1);
        sinkA.PublishLifecycle(new LifecycleRecord(h, true, 0, 4f, 1.7f, "A"));
        sinkB.PublishLifecycle(new LifecycleRecord(h, true, 0, 4f, 1.7f, "B"));

        var ok = PollUntil(
            () => multi.SenderAppInstanceOf(1) is not null && multi.SenderAppInstanceOf(2) is not null,
            () =>
            {
                sinkA.PublishFrame(1, 1.0, new[] { Car(h, 1.0) });
                sinkB.PublishFrame(1, 1.0, new[] { Car(h, 2.0) });
                multi.Pump();
            });

        Assert.True(ok, $"senders: 1={multi.SenderAppInstanceOf(1)} 2={multi.SenderAppInstanceOf(2)} sources={multi.Sources.Count}");
        Assert.Equal(41, multi.SenderAppInstanceOf(1));
        Assert.Equal(42, multi.SenderAppInstanceOf(2));
        Assert.Null(multi.SenderAppInstanceOf(3));
    }

    [Fact]
    public void H8_WithoutSenderTracking_TheSenderIsNull()
    {
        using var participant = new DdsParticipant(227);
        using var sink = new DdsReplicationSink(participant, source: 4);
        using var multi = new DdsMultiSourceSubscriber(participant);

        sink.PublishLifecycle(new LifecycleRecord(new VehicleHandle(1, 1), true, 0, 4f, 1.7f, "x"));
        Assert.True(PollUntil(() => multi.Sources.ContainsKey(4), multi.Pump), "source never appeared");

        Assert.Null(multi.SenderAppInstanceOf(4));
    }
}
