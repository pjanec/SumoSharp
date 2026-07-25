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
    private const double CarLength = 5.0;  // the fixture vType's resolved passenger defaults
    private const double CarWidth = 1.8;

    // The "close-fast-pass" the owner's Task B rule forbids: coming within CloseMetres of a pedestrian
    // while travelling faster than FastMetresPerSecond. Deliberately generous (a 1.5 m gap at 2 m/s is
    // still a slow, deliberate squeeze past, not a near-miss at speed).
    private const double CloseMetres = 1.5;
    private const double FastMetresPerSecond = 2.0;
    private const double LaneSouthEdgeY = -7.2;   // lane spans y in [-7.2, 0]

    private readonly ITestOutputHelper _out;
    public CrosswalkCrossingPedTests(ITestOutputHelper output) => _out = output;

    // One run. `pedGoalY` = the ped's target y: far south (-12) => walks through; = centreline => stops
    // mid-crossing. `yieldZone` arms the Task B-guard over the whole fixture (null = off, the default).
    // Returns per-tick (speed, posLat) for the ego vehicle, plus the world-space geometry the yield
    // assertions need: the car's body-to-ped-disc clearance and the ped's y, per tick.
    private static Run Drive(bool suppress, double pedGoalY, bool yieldZone = false)
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));
        engine.LaneChangeMinSpeed = 1.5;
        engine.SuppressHeldCrowdSwerve = suppress;
        if (yieldZone)
        {
            // Centred on the crossing, big enough that ego is inside it for the whole run.
            engine.SetCrowdYieldZone(PedX, LaneCentreY, 500.0);
        }

        var crowd = new OrcaCrowd();
        var ped = crowd.Add(new Vec2(PedX, PedStartY), PedRadius, maxSpeed: PedMaxSpeed, goal: new Vec2(PedX, pedGoalY));
        engine.CrowdSource = crowd;

        var run = new Run();
        VehicleHandle? h = null;
        for (var i = 0; i < Steps; i++)
        {
            engine.Step();
            crowd.Step(1.0);
            if (h is null && engine.VehicleHandles.Length > 0) h = engine.VehicleHandles[0];
            if (h is null || !engine.TryGetVehicle(h.Value, out var s)) continue;

            run.Speed.Add(s.Speed);
            run.PosLat.Add(s.PosLat);

            // Clearance computed INDEPENDENTLY of the engine's own guard: the car body is the rectangle
            // x in [X - Length, X], y in [Y +/- Width/2] (VehicleState.X is the FRONT bumper, SUMO's Pos
            // convention) and this fixture's lane runs due +x, so the axis-aligned form is exact here.
            var p = crowd.Position(ped);
            var dx = Math.Max(Math.Max((s.X - CarLength) - p.X, p.X - s.X), 0.0);
            var dy = Math.Max(Math.Max((s.Y - (CarWidth / 2.0)) - p.Y, p.Y - (s.Y + (CarWidth / 2.0))), 0.0);
            run.Clearance.Add(Math.Sqrt((dx * dx) + (dy * dy)) - PedRadius);
            run.PedY.Add(p.Y);
        }

        return run;
    }

    // Back-compat shim for the three Task A tests below, which only care about (speed, posLat).
    private static (List<double> Speed, List<double> PosLat) Run_(bool suppress, double pedGoalY)
    {
        var r = Drive(suppress, pedGoalY);
        return (r.Speed, r.PosLat);
    }

    private sealed class Run
    {
        public List<double> Speed { get; } = new();
        public List<double> PosLat { get; } = new();
        public List<double> Clearance { get; } = new();   // world-space car body -> ped disc, metres
        public List<double> PedY { get; } = new();
    }

    [Fact]
    public void MovingPedCrossingThrough_TheFixIsInert_NoStoppedFloat()
    {
        var on = Run_(suppress: true, pedGoalY: -12.0);
        var off = Run_(suppress: false, pedGoalY: -12.0);

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
        var (speed, posLat) = Run_(suppress: false, pedGoalY: LaneCentreY);
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
        var (speed, posLat) = Run_(suppress: true, pedGoalY: LaneCentreY);
        LogTrace("stops-at-centre ON", speed, posLat);

        // Once the ped is static in the path, the held car recentres and stays put: settle near the centre,
        // and keep waiting (still held, not driving around) -- lateral motion only with forward motion.
        var (lastStoppedLat, lastStoppedSpeed) = LastStoppedTick(speed, posLat, stoppedThreshold: 0.3);
        _out.WriteLine($"stops-at-centre ON: final stopped-tick |posLat|={lastStoppedLat:F3} m, speed={lastStoppedSpeed:F3}");
        Assert.True(lastStoppedLat < 0.2,
            $"expected the fixed car to recentre (|posLat| < 0.2 m) while held for the static ped; " +
            $"observed {lastStoppedLat:F3} m");

        // Cross-check against the OFF behaviour to prove the fix is what changed it.
        var off = Run_(suppress: false, pedGoalY: LaneCentreY);
        var offMax = MaxAbsPosLatWhileStopped(off.Speed, off.PosLat, stoppedThreshold: 0.3);
        var onMax = MaxAbsPosLatWhileStopped(speed, posLat, stoppedThreshold: 0.3);
        _out.WriteLine($"stops-at-centre: max |posLat| while stopped -- OFF={offMax:F3} m, ON settles to {lastStoppedLat:F3} m");
        Assert.True(lastStoppedLat < offMax - 1.0,
            "the fix must leave the car substantially more centred than the un-fixed float");
    }

    // -----------------------------------------------------------------------------------------------
    // Task B-guard (docs/LIVE-CITY-CAR-YIELDS-PED-DESIGN.md): the car YIELDS to the crossing pedestrian
    // instead of weaving past it. CASE A above is the defect these two tests bracket -- the OFF test
    // keeps the measured defect committed (so the ON test can never quietly become vacuous), the ON test
    // states the contract.
    // -----------------------------------------------------------------------------------------------

    // CHARACTERISATION of the defect, with the guard OFF (the default, and what every golden runs): the
    // car dodges the crossing ped at full speed and then passes it at 0.70 m of body-to-disc clearance
    // while doing 3.90 m/s -- a close-fast-pass, with the ped still inside the lane (y = -5.8, south edge
    // -7.2). If a future change fixes this WITHOUT the zone, this test goes red and should be retired
    // deliberately rather than relaxed.
    [Fact]
    public void MovingPedCrossingThrough_YieldZoneOff_StillCloseFastPassesTheCrossingPed()
    {
        var run = Drive(suppress: true, pedGoalY: -12.0, yieldZone: false);
        var (i, clr) = WorstCloseApproachWhileMoving(run);

        _out.WriteLine($"zone OFF: worst clearance {clr:F3} m at t={i} with speed {run.Speed[i]:F2} m/s, " +
                       $"max |posLat| {MaxAbs(run.PosLat):F2} m, ped y={run.PedY[i]:F2}");

        // The measured defect, pinned. (Values from the committed fixture; tolerance covers nothing but
        // formatting -- this trajectory is deterministic.)
        Assert.Equal(0.70, clr, precision: 2);
        Assert.Equal(3.90, run.Speed[i], precision: 2);
        Assert.True(run.PedY[i] > LaneSouthEdgeY, "the defect is that the car passes while the ped is STILL IN THE LANE");

        // ... and it got there by weaving at speed rather than braking.
        Assert.True(MaxAbs(run.PosLat) > 1.0, "expected the un-guarded car to swerve around the ped");
        Assert.True(CountCloseFastPasses(run) > 0, "the OFF arm must exhibit the close-fast-pass this suite is about");
    }

    // THE CONTRACT, with the guard ON: no close-fast-pass at any tick, no weave at all, the car actually
    // holds while the pedestrian is in the lane, and it is back at full speed promptly afterwards (a
    // yield, not a stall).
    [Fact]
    public void MovingPedCrossingThrough_YieldZoneOn_CarYieldsAndNeverCloseFastPasses()
    {
        var run = Drive(suppress: true, pedGoalY: -12.0, yieldZone: true);
        var (i, clr) = WorstCloseApproachWhileMoving(run);
        _out.WriteLine($"zone ON : worst clearance {clr:F3} m at t={i} with speed {run.Speed[i]:F2} m/s, " +
                       $"max |posLat| {MaxAbs(run.PosLat):F2} m");
        for (var t = 0; t < run.Speed.Count; t++)
        {
            _out.WriteLine($"  t={t,2} speed={run.Speed[t]:F2} posLat={run.PosLat[t]:F2} " +
                           $"clearance={run.Clearance[t]:F2} pedY={run.PedY[t]:F2}");
        }

        // 1. never close AND fast.
        Assert.Equal(0, CountCloseFastPasses(run));

        // 2. no weave: the guard makes the car stay centred and brake rather than steer around the ped.
        Assert.True(MaxAbs(run.PosLat) < 1e-9,
            $"the guarded car must not swerve around the ped; max |posLat| was {MaxAbs(run.PosLat):F4} m");

        // 3. it really yields -- it comes to a (near) stop at some tick while the ped is inside the lane.
        var heldWhilePedInLane = false;
        for (var t = 0; t < run.Speed.Count; t++)
        {
            if (run.Speed[t] < 0.5 && run.PedY[t] > LaneSouthEdgeY) heldWhilePedInLane = true;
        }

        Assert.True(heldWhilePedInLane, "expected the guarded car to HOLD while the pedestrian was still in the lane");

        // 4. and it is not a stall: once the ped has left the lane the car is back at its maxSpeed within
        //    a few ticks (the fixture's vType maxSpeed is 5).
        var pedClearedAt = -1;
        for (var t = 0; t < run.PedY.Count; t++)
        {
            if (run.PedY[t] <= LaneSouthEdgeY) { pedClearedAt = t; break; }
        }

        Assert.True(pedClearedAt >= 0, "fixture sanity: the ped must leave the lane during the run");
        var resumedAt = -1;
        for (var t = pedClearedAt; t < run.Speed.Count; t++)
        {
            if (run.Speed[t] >= 4.99) { resumedAt = t; break; }
        }

        _out.WriteLine($"zone ON : ped cleared the lane at t={pedClearedAt}, car back at maxSpeed at t={resumedAt}");
        Assert.True(resumedAt >= 0 && resumedAt - pedClearedAt <= 4,
            $"the yield must not stall traffic: ped cleared at t={pedClearedAt}, maxSpeed regained at t={resumedAt}");
    }

    // Worst (smallest) clearance over the ticks where the car is actually MOVING faster than the
    // close-fast-pass speed threshold; falls back to the global minimum if it never moves that fast.
    private static (int Index, double Clearance) WorstCloseApproachWhileMoving(Run run)
    {
        var best = -1;
        var bestClr = double.PositiveInfinity;
        for (var t = 0; t < run.Clearance.Count; t++)
        {
            if (run.Speed[t] <= FastMetresPerSecond) continue;
            if (run.Clearance[t] < bestClr) { bestClr = run.Clearance[t]; best = t; }
        }

        if (best >= 0) return (best, bestClr);

        for (var t = 0; t < run.Clearance.Count; t++)
        {
            if (run.Clearance[t] < bestClr) { bestClr = run.Clearance[t]; best = t; }
        }

        return (best, bestClr);
    }

    private static int CountCloseFastPasses(Run run)
    {
        var n = 0;
        for (var t = 0; t < run.Clearance.Count; t++)
        {
            if (run.Clearance[t] < CloseMetres && run.Speed[t] > FastMetresPerSecond) n++;
        }

        return n;
    }

    private static double MaxAbs(IReadOnlyList<double> xs)
    {
        double m = 0;
        foreach (var x in xs) m = Math.Max(m, Math.Abs(x));
        return m;
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
