using System;
using System.Collections.Generic;
using System.Linq;
using Sim.Core.Orca;
using Sim.Pedestrians.Lod;
using Sim.Replication;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests;

// docs/EXTERNAL-NET-LOADING-DESIGN.md §3.5b/§3.6, -TASKS.md C5: the 5-out-param `TryGetRenderPose`
// overload, and `HeadlessIg` reconstructing z with the SAME arc fraction it uses for position.
//
// The point of C5·SC4 is that the wire surface and the in-process surface land on the same number --
// which is what makes W1 (carrying z on the wire) worth having over a receiver-side lookup.
public class PedRemoteElevationTests
{
    private readonly ITestOutputHelper _output;

    public PedRemoteElevationTests(ITestOutputHelper output) => _output = output;

    // A straight 100 m ramp climbing 10 m, so the analytic elevation at any arc position is exact.
    private static readonly Vec2[] Ramp = { new(0, 0), new(50, 0), new(100, 0) };
    private static readonly double[] RampZ = { 370.0, 375.0, 380.0 };

    private static (InMemoryPedReplicationBus Bus, PedPublisher Publisher, PedReplicationPublisher Wire) NewWire()
    {
        var bus = new InMemoryPedReplicationBus();
        var scheduler = new PedPublishScheduler(new PedDrErrorPublishPolicy());
        var meter = new PedBandwidthMeter();
        var governor = new PedBandwidthGovernor(scheduler, meter, maxMbitPerSecond: 500.0);
        return (bus, new PedPublisher(), new PedReplicationPublisher(bus.Sink, scheduler, governor, meter, stepDt: 0.5));
    }

    // ---- C5·SC5: a kind-4 (no-z) stream returns 0.0 and does not throw ------------------------------

    [Fact]
    public void AgainstAZLessStream_TheOverloadReturnsZeroAndDoesNotThrow()
    {
        var (bus, publisher, wire) = NewWire();
        publisher.PublishPathArc(id: 1, Ramp, startTime: 0.0, speed: 1.0, time: 0.0); // no pathZ => kind 4
        wire.Publish(publisher.Events);

        var recon = new PedRemoteReconstructor(bus.Source);
        recon.Pump(1.0);

        Assert.True(recon.TryGetRenderPose(1, out var pos, out var z, out var visible, out _));
        Assert.Equal(0.0, z);
        Assert.True(visible);
        Assert.NotEqual(Vec2.Zero, pos);
    }

    // ---- C5·SC2: the two overloads agree on everything they share -----------------------------------

    [Fact]
    public void BothOverloads_ReturnTheSamePoseVisibilityAndAnimTag()
    {
        var (bus, publisher, wire) = NewWire();
        publisher.PublishPathArc(id: 1, Ramp, startTime: 0.0, speed: 1.0, time: 0.0, pathZ: RampZ);
        wire.Publish(publisher.Events);

        var recon = new PedRemoteReconstructor(bus.Source);
        recon.Pump(20.0);

        var okFive = recon.TryGetRenderPose(1, out var posFive, out var z, out var visFive, out var tagFive);
        var okFour = recon.TryGetRenderPose(1, out var posFour, out var visFour, out var tagFour);

        Assert.True(okFive);
        Assert.Equal(okFour, okFive);
        Assert.Equal(posFour.X, posFive.X, 9);
        Assert.Equal(posFour.Y, posFive.Y, 9);
        Assert.Equal(visFour, visFive);
        Assert.Equal(tagFour, tagFive);
        Assert.True(z > 0.0, $"expected real elevation on a z-carrying stream, got {z}");
    }

    [Fact]
    public void AnUnknownId_ReturnsFalseAndZeroZ()
    {
        var (bus, _, _) = NewWire();
        var recon = new PedRemoteReconstructor(bus.Source);
        recon.Pump(1.0);

        Assert.False(recon.TryGetRenderPose(999, out _, out var z, out var visible, out _));
        Assert.Equal(0.0, z);
        Assert.False(visible);
    }

    // ---- z tracks the arc position along the ramp ---------------------------------------------------

    [Fact]
    public void ReconstructedZ_TracksTheAnalyticRampElevation()
    {
        // 1 m/s along a 100 m ramp rising 370 -> 380 m: at t seconds the exact height is 370 + t/10.
        var (bus, publisher, wire) = NewWire();
        publisher.PublishPathArc(id: 1, Ramp, startTime: 0.0, speed: 1.0, time: 0.0, pathZ: RampZ);
        wire.Publish(publisher.Events);

        var recon = new PedRemoteReconstructor(bus.Source, playoutDelaySeconds: 0.0);

        var worst = 0.0;
        for (var t = 5.0; t <= 95.0; t += 5.0)
        {
            recon.Pump(t);
            Assert.True(recon.TryGetRenderPose(1, out _, out var z, out _, out _));

            var expected = 370.0 + (t / 10.0);
            worst = Math.Max(worst, Math.Abs(z - expected));
            Assert.True(Math.Abs(z - expected) <= 0.05, $"t={t}: expected {expected:F3}, got {z:F3}");
        }

        _output.WriteLine($"C5 ramp: worst |reconstructedZ - analytic| = {worst:F4} m over 19 samples");
    }

    // ---- C5·SC4: the wire surface agrees with the in-process evaluator ------------------------------

    [Fact]
    public void WireZ_AgreesWithTheInProcessEvaluator_WithinFiveCentimetres()
    {
        // Both surfaces call PathArcMotion's one shared evaluator, so the only permitted difference is
        // the wire's 1 cm quantization. Compared against the in-process arc evaluation of the SAME path
        // at the SAME time -- i.e. exactly what LiveCitySim.Sample()'s ElevationOf resolves for a
        // PathArc ped.
        var (bus, publisher, wire) = NewWire();
        publisher.PublishPathArc(id: 1, Ramp, startTime: 0.0, speed: 1.0, time: 0.0, pathZ: RampZ);
        wire.Publish(publisher.Events);

        var recon = new PedRemoteReconstructor(bus.Source, playoutDelaySeconds: 0.0);

        var worst = 0.0;
        var samples = 0;
        for (var t = 1.0; t <= 99.0; t += 4.0)
        {
            recon.Pump(t);
            Assert.True(recon.TryGetRenderPose(1, out _, out var wireZ, out _, out _));

            var inProcessZ = PathArcMotion.ElevationAt(Ramp, RampZ, startTime: 0.0, speed: 1.0, now: t);
            worst = Math.Max(worst, Math.Abs(wireZ - inProcessZ));
            Assert.True(Math.Abs(wireZ - inProcessZ) <= 0.05,
                $"t={t}: wire {wireZ:F4} vs in-process {inProcessZ:F4}");
            samples++;
        }

        Assert.True(samples >= 20, $"expected >=20 sampled times; got {samples}");
        _output.WriteLine($"C5.SC4 worst |wireZ - inProcessZ| over {samples} samples: {worst:F4} m");
    }

    // ---- the documented gap, asserted so it cannot be forgotten -------------------------------------

    [Fact]
    public void ALivelyPedsWireRecord_CarriesNoElevation_TheKnownGap()
    {
        // A ped in the ActivityTimeline model is published as an ActivityTimelineRecord, and that wire
        // format has no elevation channel -- the W1 decision extended the PathArc record only. So the
        // REMOTE surface reports 0.0 for such a ped however 3-D the net is, while the IN-PROCESS surface
        // reports its real height. Asserted here rather than left as a surprise: if someone later extends
        // ActivityTimelineWire, this test fails and points at the note to delete.
        var (bus, publisher, wire) = NewWire();
        var timeline = new ActivityTimeline(0.0, new ActivitySegment[] { new WalkSegment(Ramp, 1.0) });
        publisher.PublishActivityTimeline(id: 1, timeline, time: 0.0);
        // The switch event is what puts the IG into the timeline model -- exactly what PedLodManager
        // publishes alongside the timeline. Without it the IG stays in PathArc with no path and
        // reconstructs the origin, which would make this test pass for the wrong reason.
        publisher.PublishSwitch(id: 1, PedDrModel.PathArc, PedDrModel.ActivityTimeline, time: 0.0);
        wire.Publish(publisher.Events);

        var recon = new PedRemoteReconstructor(bus.Source, playoutDelaySeconds: 0.0);
        recon.Pump(20.0);

        Assert.True(recon.TryGetRenderPose(1, out var pos, out var z, out _, out _));
        Assert.NotEqual(Vec2.Zero, pos); // the POSITION reconstructs fine
        Assert.Equal(0.0, z);            // ...the elevation does not exist on this wire format
    }
}
