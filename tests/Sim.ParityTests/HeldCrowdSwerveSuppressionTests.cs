using System;
using System.Collections.Generic;
using System.IO;
using Sim.Core;
using Sim.Core.Orca;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// Task A redo (docs/LIVE-CITY-REALISM-AB-DESIGN.md §Task A, docs/LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md §F2).
// A car held (nearly) stopped by a pedestrian drifted sideways -- its lateral offset (VehicleState.PosLat)
// swung a full lane width while forward Speed stayed ~0 ("lateral motion without forward motion", the demo's
// "floating"/wobble). Mechanism: this demo fixture has NO lateral-resolution (no sublane driver), so a plain
// phase-1 vehicle's lateral intent comes from Engine.ComputeLateralEvasion's crowd-swerve reacting to a
// CrowdSource pedestrian disc (Q6/option b -- see NormalModeCrowdSwerveTests). When the ped sits right at the
// car's start (x=3.5, departSpeed=0) the car is longitudinally HELD by Engine.CrowdLongitudinalConstraint
// (BindingConstraint == 13) yet the crowd-swerve still steers posLat 0 -> 2.0 -> 2.7 across the two held
// ticks: PosLat moves while Speed is ~0.
//
// The fix (Engine.SuppressHeldCrowdSwerve, default false): in ComputeLateralEvasion's crowd-swerve branch,
// when ego is HELD by the crowd this step (BindingConstraint == 13) AND the agent is laterally STATIC
// (LatSpeed ~ 0), suppress the swerve and recentre instead -- wait in-lane behind the ped. This is targeted,
// NOT the reverted blanket lateral freeze (which pinned mid-lane-change offsets -> straddle -> car-car
// overlaps, F2): it only recentres (can never straddle) and only for a held static ped. A car swerving PAST
// a ped at SPEED is never held (BindingConstraint == 3 throughout -- see SuppressOn_StillSwervesPastPedAtSpeed
// below), so legitimate dodges/passes are untouched. Default false => byte-identical on every golden
// (no committed golden/bench sets SuppressHeldCrowdSwerve or attaches a CrowdSource).
//
// Reproducing setup (bridge-crossing-normal: single 7.2 m lane, centreline y=-3.6, +x, NORMAL/non-sublane
// vehicle, vType "car" maxSpeed=5 sigma=0 departSpeed=0) with ONE stationary CrowdSource ped centred on the
// lane at x=3.5 (goal == start, so it never moves) radius 1.3 m: swerve target 2.7 m from centre (=
// VehHalfWidth 0.9 + SwerveLateralGap 0.5 + pedRadius 1.3), exactly at the lane clearance limit, so both sides
// are (barely) feasible and the swerve is preferred over a hard stop -- but the ped is close enough that the
// car's speed is ~0 for the two ticks the swerve takes.
public class HeldCrowdSwerveSuppressionTests
{
    private static readonly string ScenarioDir =
        Path.Combine(RepoRoot(), "scenarios", "_fixtures", "bridge-crossing-normal");

    private const double HeldPedX = 3.5;   // right at the car's start -> car is HELD (binder 13) at ~0 speed
    private const double PassPedX = 30.0;  // far down the lane -> car has runway, swerves PAST at full speed
    private const double PedY = -3.6;      // lane centreline (dead-centre on the car's lane)
    private const double HeldPedRadius = 1.3;
    private const double PassPedRadius = 0.35;
    private const int Steps = 40;

    private readonly ITestOutputHelper _out;

    public HeldCrowdSwerveSuppressionTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void SuppressOff_HeldCarWobblesLaterally_WhileNearlyStoppedByPedestrian()
    {
        var (speed, posLat) = RunHeld(suppress: false);
        LogTrace("suppress OFF", speed, posLat);

        var (windowLen, cumChange) = MaxCumulativeChangeWhileStopped(speed, posLat, stoppedThreshold: 0.3);
        _out.WriteLine($"suppress OFF: best stopped-window length={windowLen}, cumulative |PosLat| change={cumChange:F4} m");

        // The un-fixed car steers sideways while held nearly stopped -- the bug the owner flagged.
        Assert.True(cumChange > 0.5,
            $"expected the un-fixed (suppress OFF) car to wobble laterally by more than 0.5 m while held " +
            $"nearly stopped (Speed < 0.3); observed only {cumChange:F4} m over a {windowLen}-tick window");
    }

    [Fact]
    public void SuppressOn_HeldCarDoesNotDriftLaterally_WhileNearlyStopped()
    {
        var (speed, posLat) = RunHeld(suppress: true);
        LogTrace("suppress ON", speed, posLat);

        var (windowLen, cumChange) = MaxCumulativeChangeWhileStopped(speed, posLat, stoppedThreshold: 0.3);
        var stoppedTicks = CountStoppedTicks(speed, stoppedThreshold: 0.3);
        _out.WriteLine($"suppress ON: stoppedTicks={stoppedTicks}, max-drift window length={windowLen}, cumulative |PosLat| change={cumChange:F4} m");

        // With the fix, a car held nearly stopped by the static ped stays centred: no lateral motion without
        // forward motion. Across every stopped tick, cumulative |PosLat| change must be essentially zero.
        Assert.True(cumChange < 1e-6,
            $"expected the fixed (suppress ON) held car to NOT drift laterally while nearly stopped " +
            $"(Speed < 0.3); observed {cumChange:F6} m of cumulative lateral motion");

        // Non-vacuous: the car must actually BE held nearly stopped for a stretch (else "no drift" is trivial).
        Assert.True(stoppedTicks >= 2,
            $"expected the car to be held nearly stopped (Speed < 0.3) for at least 2 ticks in this scenario; " +
            $"observed {stoppedTicks}");
    }

    [Fact]
    public void SuppressOn_StillSwervesPastPedAtSpeed_AndDrivesOn()
    {
        // Same fix ON, but the ped is far down the lane: the car is NOT held (it reaches the ped at full
        // speed, BindingConstraint == 3 throughout), so the crowd-swerve must still fire and the car passes.
        var (peakLat, lastX, minGap) = RunPass(suppress: true);
        _out.WriteLine($"suppress ON pass: peakLat={peakLat:F2} lastX={lastX:F1} minGap={minGap:F3}");

        Assert.True(peakLat > 1.0, $"fix ON must NOT suppress a legitimate at-speed swerve (peak |posLat| = {peakLat:F2})");
        Assert.True(lastX > 40.0, $"car must drive past the ped, not stop short (ended at x={lastX:F1})");
        Assert.True(minGap > 0.0, $"car overlapped the ped (min gap = {minGap:F3})");
    }

    [Fact]
    public void SuppressOn_IsDeterministic_AcrossIndependentRuns()
    {
        var run1 = RunHeld(suppress: true);
        var run2 = RunHeld(suppress: true);

        Assert.Equal(run1.Speed.Count, run2.Speed.Count);
        for (var i = 0; i < run1.Speed.Count; i++)
        {
            Assert.Equal(run1.Speed[i], run2.Speed[i], precision: 12);
            Assert.Equal(run1.PosLat[i], run2.PosLat[i], precision: 12);
        }
    }

    private static (List<double> Speed, List<double> PosLat) RunHeld(bool suppress)
    {
        var engine = NewEngine(suppress);
        var crowd = new OrcaCrowd();
        var pedPos = new Vec2(HeldPedX, PedY);
        crowd.Add(pedPos, HeldPedRadius, maxSpeed: 0.0, goal: pedPos); // stationary: goal == own position
        engine.CrowdSource = crowd;

        var speed = new List<double>();
        var posLat = new List<double>();
        VehicleHandle? handle = null;

        for (var i = 0; i < Steps; i++)
        {
            engine.Step();
            crowd.Step(1.0);

            if (handle is null && engine.VehicleHandles.Length > 0)
            {
                handle = engine.VehicleHandles[0];
            }

            if (handle is not null && engine.TryGetVehicle(handle.Value, out var s))
            {
                speed.Add(s.Speed);
                posLat.Add(s.PosLat);
            }
        }

        return (speed, posLat);
    }

    private static (double PeakLat, double LastX, double MinGap) RunPass(bool suppress)
    {
        var engine = NewEngine(suppress);
        var crowd = new OrcaCrowd();
        var pedPos = new Vec2(PassPedX, PedY);
        crowd.Add(pedPos, PassPedRadius, maxSpeed: 0.0, goal: pedPos); // stationary, but far down the lane
        engine.CrowdSource = crowd;

        const double vehHalfWidth = 0.9, vehLength = 5.0;
        double peakLat = 0.0, lastX = 0.0, minGap = double.PositiveInfinity;
        VehicleHandle? handle = null;

        for (var i = 0; i < Steps; i++)
        {
            engine.Step();
            crowd.Step(1.0);

            if (handle is null && engine.VehicleHandles.Length > 0)
            {
                handle = engine.VehicleHandles[0];
            }

            if (handle is not null && engine.TryGetVehicle(handle.Value, out var s))
            {
                peakLat = Math.Max(peakLat, Math.Abs(s.PosLat));
                lastX = s.X;
                var gap = RectDiscDistance(s.X, s.Y, vehLength, vehHalfWidth, PassPedX, PedY) - PassPedRadius;
                minGap = Math.Min(minGap, gap);
            }
        }

        return (peakLat, lastX, minGap);
    }

    private static Engine NewEngine(bool suppress)
    {
        var engine = new Engine();   // NORMAL mode: no LanelessRvo, fixture has no lateral-resolution
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));
        engine.LaneChangeMinSpeed = 1.5;   // demo realism setting; part of the canonical repro
        engine.SuppressHeldCrowdSwerve = suppress;
        return engine;
    }

    // Distance from a disc centre to the vehicle's axis-aligned footprint rectangle [X-Length, X] x
    // [Y-HalfWidth, Y+HalfWidth]. The lane runs along +X, so the footprint is axis-aligned.
    private static double RectDiscDistance(double x, double y, double length, double halfWidth, double px, double py)
    {
        var dx = Math.Max(Math.Max((x - length) - px, px - x), 0.0);
        var dy = Math.Max(Math.Max((y - halfWidth) - py, py - (y + halfWidth)), 0.0);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // Largest sum of |PosLat[i] - PosLat[i-1]| over any maximal run of consecutive ticks whose Speed is
    // below `stoppedThreshold`. Returns the run's length alongside the cumulative change.
    private static (int Length, double CumulativeChange) MaxCumulativeChangeWhileStopped(
        IReadOnlyList<double> speed, IReadOnlyList<double> posLat, double stoppedThreshold)
    {
        var bestLen = 0;
        var bestCum = 0.0;
        var curLen = 0;
        var curCum = 0.0;
        double? prevPosLat = null;

        for (var i = 0; i < speed.Count; i++)
        {
            if (speed[i] < stoppedThreshold)
            {
                if (prevPosLat is not null)
                {
                    curCum += Math.Abs(posLat[i] - prevPosLat.Value);
                }
                curLen++;
                prevPosLat = posLat[i];
            }
            else
            {
                if (curCum > bestCum)
                {
                    bestCum = curCum;
                    bestLen = curLen;
                }
                curLen = 0;
                curCum = 0.0;
                prevPosLat = null;
            }
        }

        if (curCum > bestCum)
        {
            bestCum = curCum;
            bestLen = curLen;
        }

        return (bestLen, bestCum);
    }

    private static int CountStoppedTicks(IReadOnlyList<double> speed, double stoppedThreshold)
    {
        var n = 0;
        foreach (var s in speed)
        {
            if (s < stoppedThreshold) n++;
        }
        return n;
    }

    private void LogTrace(string label, IReadOnlyList<double> speed, IReadOnlyList<double> posLat)
    {
        for (var i = 0; i < speed.Count; i++)
        {
            _out.WriteLine($"{label} t={i} speed={speed[i]:F4} posLat={posLat[i]:F4}");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Traffic.sln not found above test assembly).");
    }
}
