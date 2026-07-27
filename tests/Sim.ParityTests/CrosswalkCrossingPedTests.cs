using System;
using System.Collections.Generic;
using System.IO;
using Sim.Core;
using Sim.Core.Orca;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// Task A crosswalk scope (docs/LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md §F2 "Crosswalk scope"). A pedestrian
// WALKING ACROSS a car's lane (a crosswalk crossing) is a first-class dodgeable CrowdSource threat, so the
// question "is the stopped-car wobble fixed for a ped on a crosswalk, moving or standing?" splits into two
// physically distinct cases, verified here on the bridge-crossing-normal fixture (single 7.2 m lane,
// centreline y=-3.6, +x; car v0 departs x=0, maxSpeed 5; the fixture is built for exactly this -- see its
// rou.rou.xml comment). The car reaches x~22 around t=4-5, so a ped placed at x=22 is mid-lane as it arrives.
//
//   CASE A -- ped keeps WALKING THROUGH (LatSpeed != 0): the car does an anticipatory dodge AT SPEED
//     (posLat grows while Speed=5), then briefly brakes as the ped passes; there is NO lateral motion while
//     the car is stopped. Engine.SuppressHeldCrowdSwerve is INERT here (it gates on LatSpeed ~ 0), so the
//     trajectory is byte-identical fix on/off. => no stopped-wobble to fix; a moving ped never floats a
//     stopped car. (The separate question "should the car STOP for the crossing ped rather than weave past
//     it at speed?" is the ped-vehicle avoidance session's hard yield -- Task B -- not the wobble.)
//
//   CASE B -- ped WALKS IN AND STOPS mid-crossing at the centreline (LatSpeed -> 0): this is the wobble.
//     With the fix OFF the held car steers a full metre-plus sideways WHILE STOPPED (posLat 1.23 -> 2.0 at
//     Speed 0) and drives AROUND the ped. With the fix ON the held car recentres (posLat -> ~0) and WAITS
//     centred behind it -- "lateral motion only with forward motion" restored. So the wobble IS fixed for a
//     ped that is (or becomes) static in the car's path, whether it was standing or stopped mid-crossing.
public class CrosswalkCrossingPedTests
{
    private static readonly string ScenarioDir =
        Path.Combine(RepoRoot(), "scenarios", "_fixtures", "bridge-crossing-normal");

    private const double PedX = 22.0;      // ahead of the car; it is mid-lane as the car arrives (~t=4-5)
    private const double PedStartY = 2.0;  // north of the lane (lane spans y in [-7.2, 0], centre -3.6)
    private const double LaneCentreY = -3.6;
    private const double PedMaxSpeed = 1.3;
    private const double PedRadius = 0.6;  // the demo's inflated ORCA footprint radius
    private const int Steps = 20;

    private readonly ITestOutputHelper _out;
    public CrosswalkCrossingPedTests(ITestOutputHelper output) => _out = output;

    // One run. `pedGoalY` = the ped's target y: far south (-12) => walks through; = centreline => stops
    // mid-crossing. Returns per-tick (speed, posLat) for the ego vehicle.
    private static (List<double> Speed, List<double> PosLat) Run(bool suppress, double pedGoalY)
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));
        engine.LaneChangeMinSpeed = 1.5;
        engine.SuppressHeldCrowdSwerve = suppress;

        var crowd = new OrcaCrowd();
        var ped = crowd.Add(new Vec2(PedX, PedStartY), PedRadius, maxSpeed: PedMaxSpeed, goal: new Vec2(PedX, pedGoalY));
        engine.CrowdSource = crowd;

        var speed = new List<double>();
        var posLat = new List<double>();
        VehicleHandle? h = null;
        for (var i = 0; i < Steps; i++)
        {
            engine.Step();
            crowd.Step(1.0);
            if (h is null && engine.VehicleHandles.Length > 0) h = engine.VehicleHandles[0];
            if (h is not null && engine.TryGetVehicle(h.Value, out var s)) { speed.Add(s.Speed); posLat.Add(s.PosLat); }
        }
        return (speed, posLat);
    }

    [Fact]
    public void MovingPedCrossingThrough_TheFixIsInert_NoStoppedFloat()
    {
        var on = Run(suppress: true, pedGoalY: -12.0);
        var off = Run(suppress: false, pedGoalY: -12.0);

        // The suppression gate keys on a laterally-STATIC agent; a ped that keeps moving (LatSpeed != 0)
        // never trips it, so the two trajectories are byte-identical.
        Assert.Equal(off.Speed.Count, on.Speed.Count);
        for (var i = 0; i < on.Speed.Count; i++)
        {
            Assert.Equal(off.Speed[i], on.Speed[i], precision: 12);
            Assert.Equal(off.PosLat[i], on.PosLat[i], precision: 12);
        }

        // And there is no lateral motion while the car is (nearly) stopped -- a moving ped is dodged at speed
        // and/or braked for; it does not float a stopped car.
        var cumWhileStopped = CumLatChangeWhileStopped(on.Speed, on.PosLat, stoppedThreshold: 0.3);
        _out.WriteLine($"moving-through: cumulative |dPosLat| while stopped = {cumWhileStopped:F4} m (fix on==off)");
        Assert.True(cumWhileStopped < 1e-6,
            $"a moving crossing ped must not float a stopped car; observed {cumWhileStopped:F6} m while Speed<0.3");
    }

    [Fact]
    public void PedStopsMidCrossing_FixOff_CarSteersSidewaysWhileStopped()
    {
        var (speed, posLat) = Run(suppress: false, pedGoalY: LaneCentreY);
        LogTrace("stops-at-centre OFF", speed, posLat);

        // The un-fixed car steers a full lane-half sideways while fully stopped, then drives around the ped.
        var maxLatWhileStopped = MaxAbsPosLatWhileStopped(speed, posLat, stoppedThreshold: 0.3);
        _out.WriteLine($"stops-at-centre OFF: max |posLat| while stopped = {maxLatWhileStopped:F3} m");
        Assert.True(maxLatWhileStopped > 1.5,
            $"expected the un-fixed car to steer sideways (>1.5 m) while stopped for the static ped; " +
            $"observed only {maxLatWhileStopped:F3} m");
    }

    [Fact]
    public void PedStopsMidCrossing_FixOn_CarRecentresAndWaits_NoFloat()
    {
        var (speed, posLat) = Run(suppress: true, pedGoalY: LaneCentreY);
        LogTrace("stops-at-centre ON", speed, posLat);

        // Once the ped is static in the path, the held car recentres and stays put: settle near the centre,
        // and keep waiting (still held, not driving around) -- lateral motion only with forward motion.
        var (lastStoppedLat, lastStoppedSpeed) = LastStoppedTick(speed, posLat, stoppedThreshold: 0.3);
        _out.WriteLine($"stops-at-centre ON: final stopped-tick |posLat|={lastStoppedLat:F3} m, speed={lastStoppedSpeed:F3}");
        Assert.True(lastStoppedLat < 0.2,
            $"expected the fixed car to recentre (|posLat| < 0.2 m) while held for the static ped; " +
            $"observed {lastStoppedLat:F3} m");

        // Cross-check against the OFF behaviour to prove the fix is what changed it.
        var off = Run(suppress: false, pedGoalY: LaneCentreY);
        var offMax = MaxAbsPosLatWhileStopped(off.Speed, off.PosLat, stoppedThreshold: 0.3);
        var onMax = MaxAbsPosLatWhileStopped(speed, posLat, stoppedThreshold: 0.3);
        _out.WriteLine($"stops-at-centre: max |posLat| while stopped -- OFF={offMax:F3} m, ON settles to {lastStoppedLat:F3} m");
        Assert.True(lastStoppedLat < offMax - 1.0,
            "the fix must leave the car substantially more centred than the un-fixed float");
    }

    // Sum of |PosLat[i]-PosLat[i-1]| over consecutive ticks with Speed < threshold.
    private static double CumLatChangeWhileStopped(IReadOnlyList<double> speed, IReadOnlyList<double> posLat, double stoppedThreshold)
    {
        double cum = 0; double? prev = null;
        for (var i = 0; i < speed.Count; i++)
        {
            if (speed[i] < stoppedThreshold) { if (prev is not null) cum += Math.Abs(posLat[i] - prev.Value); prev = posLat[i]; }
            else prev = null;
        }
        return cum;
    }

    private static double MaxAbsPosLatWhileStopped(IReadOnlyList<double> speed, IReadOnlyList<double> posLat, double stoppedThreshold)
    {
        double m = 0;
        for (var i = 0; i < speed.Count; i++) if (speed[i] < stoppedThreshold) m = Math.Max(m, Math.Abs(posLat[i]));
        return m;
    }

    private static (double Lat, double Speed) LastStoppedTick(IReadOnlyList<double> speed, IReadOnlyList<double> posLat, double stoppedThreshold)
    {
        for (var i = speed.Count - 1; i >= 0; i--) if (speed[i] < stoppedThreshold) return (Math.Abs(posLat[i]), speed[i]);
        return (double.NaN, double.NaN);
    }

    private void LogTrace(string label, IReadOnlyList<double> speed, IReadOnlyList<double> posLat)
    {
        for (var i = 0; i < speed.Count; i++) _out.WriteLine($"{label} t={i} speed={speed[i]:F2} posLat={posLat[i]:F2}");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
