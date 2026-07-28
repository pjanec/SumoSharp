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

    // ---- C5·SC2 (as amended): ONE mandatory signature -----------------------------------------------

    [Fact]
    public void ThereIsExactlyOneRenderPoseOverload_AndItCarriesElevation()
    {
        // C5 originally shipped `TryGetRenderPose` as a 5-out-param sibling of a 4-out-param (z-less)
        // form, and SC2 asserted the two agreed. The z-less form has since been REMOVED (see
        // docs/EXTERNAL-NET-VIEWER-DESIGN.md §"z is mandatory, not additive"): a dual API means a
        // renderer can keep calling the 2-D form and draw every ped at the wrong height with nothing to
        // catch it. Asserted by REFLECTION rather than by a call, because the whole point is that the
        // other form must not be callable -- a compile-time check cannot express its own absence.
        var overloads = typeof(PedRemoteReconstructor)
            .GetMethods()
            .Where(m => m.Name == nameof(PedRemoteReconstructor.TryGetRenderPose))
            .ToArray();

        Assert.Single(overloads);

        var ps = overloads[0].GetParameters();
        Assert.Equal(5, ps.Length);
        Assert.Equal(typeof(double).MakeByRefType(), ps[2].ParameterType);
        Assert.True(ps[2].IsOut);

        // ...and it does return a real height on a z-carrying stream.
        var (bus, publisher, wire) = NewWire();
        publisher.PublishPathArc(id: 1, Ramp, startTime: 0.0, speed: 1.0, time: 0.0, pathZ: RampZ);
        wire.Publish(publisher.Events);

        var recon = new PedRemoteReconstructor(bus.Source);
        recon.Pump(20.0);

        Assert.True(recon.TryGetRenderPose(1, out var pos, out var z, out var visible, out var tag));
        Assert.True(visible);
        Assert.NotEqual(Vec2.Zero, pos);
        Assert.Equal(ActivityTimeline.IdleAnimTag, tag);
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

    // ---- lively peds: the follow-up that closed W1's gap ---------------------------------------------

    [Fact]
    public void ALivelyPedsTimeline_CarriesElevationOverTheWire()
    {
        // A ped in the ActivityTimeline model is published as a timeline, never as a PathArc. W1 extended
        // the PathArc record only, which left the ENTIRE lively population -- most of the live-city
        // scene -- flat on this surface. ActivityTimelineWire now carries a per-WalkSegment elevation
        // channel, so it reconstructs like everyone else.
        var (bus, publisher, wire) = NewWire();
        var timeline = new ActivityTimeline(
            0.0, new ActivitySegment[] { new WalkSegment(Ramp, 1.0, null, RampZ) });
        publisher.PublishActivityTimeline(id: 1, timeline, time: 0.0);
        // The switch event is what puts the IG into the timeline model -- exactly what PedLodManager
        // publishes alongside the timeline. Without it the IG stays in PathArc with no path and
        // reconstructs the origin, which would make this test pass for the wrong reason.
        publisher.PublishSwitch(id: 1, PedDrModel.PathArc, PedDrModel.ActivityTimeline, time: 0.0);
        wire.Publish(publisher.Events);

        var recon = new PedRemoteReconstructor(bus.Source, playoutDelaySeconds: 0.0);

        for (var t = 10.0; t <= 90.0; t += 10.0)
        {
            recon.Pump(t);
            Assert.True(recon.TryGetRenderPose(1, out var pos, out var z, out _, out _));
            Assert.NotEqual(Vec2.Zero, pos);

            // 1 m/s along a 100 m ramp rising 370 -> 380: the exact height at t is 370 + t/10. The
            // timeline wire is lossless (full doubles, unlike PathArc's cm quantization), so this is
            // tight rather than within a centimetre.
            var expected = 370.0 + (t / 10.0);
            Assert.True(Math.Abs(z - expected) <= 0.001, $"t={t}: expected {expected:F3}, got {z:F3}");
        }
    }

    [Fact]
    public void ALivelyPedOnAFlatNet_StillReconstructsZeroElevation()
    {
        // The 2-D regression for the same path: no channel on the WalkSegment => 0.0, exactly as before
        // the timeline wire learned about elevation.
        var (bus, publisher, wire) = NewWire();
        var timeline = new ActivityTimeline(0.0, new ActivitySegment[] { new WalkSegment(Ramp, 1.0) });
        publisher.PublishActivityTimeline(id: 1, timeline, time: 0.0);
        publisher.PublishSwitch(id: 1, PedDrModel.PathArc, PedDrModel.ActivityTimeline, time: 0.0);
        wire.Publish(publisher.Events);

        var recon = new PedRemoteReconstructor(bus.Source, playoutDelaySeconds: 0.0);
        recon.Pump(20.0);

        Assert.True(recon.TryGetRenderPose(1, out var pos, out var z, out _, out _));
        Assert.NotEqual(Vec2.Zero, pos);
        Assert.Equal(0.0, z);
    }

    [Fact]
    public void AMultiLegTimeline_ReadsElevationFromTheLegThePedIsActuallyOn()
    {
        // A route split by a kerb pause is two Walk legs at different heights. Reading the first leg's
        // channel for the whole trip would be wrong for the second half, so the leg is selected by
        // proximity to the reconstructed pose.
        var legA = new[] { new Vec2(0, 0), new Vec2(10, 0) };
        var legAz = new[] { 100.0, 100.0 };
        var legB = new[] { new Vec2(10, 0), new Vec2(20, 0) };
        var legBz = new[] { 200.0, 200.0 };

        var (bus, publisher, wire) = NewWire();
        var timeline = new ActivityTimeline(0.0, new ActivitySegment[]
        {
            new WalkSegment(legA, 1.0, null, legAz),
            new PauseSegment(2.0, "wait"),
            new WalkSegment(legB, 1.0, null, legBz),
        });
        publisher.PublishActivityTimeline(id: 1, timeline, time: 0.0);
        publisher.PublishSwitch(id: 1, PedDrModel.PathArc, PedDrModel.ActivityTimeline, time: 0.0);
        wire.Publish(publisher.Events);

        var recon = new PedRemoteReconstructor(bus.Source, playoutDelaySeconds: 0.0);

        recon.Pump(3.0); // on leg A
        Assert.True(recon.TryGetRenderPose(1, out _, out var zA, out _, out _));
        Assert.Equal(100.0, zA, 3);

        recon.Pump(18.0); // past the pause, well onto leg B
        Assert.True(recon.TryGetRenderPose(1, out _, out var zB, out _, out _));
        Assert.Equal(200.0, zB, 3);
    }
}
