using System;
using System.Collections.Generic;
using System.IO;
using Sim.Core;
using Sim.Core.Orca;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// Task A (docs/LIVE-CITY-REALISM-AB-DESIGN.md): a car held (nearly) stopped by a pedestrian drifted
// sideways -- its lateral offset (VehicleState.PosLat) oscillated by up to a full lane width while its
// forward Speed stayed ~0. Mechanism: this demo fixture has NO lateral-resolution (no sublane driver),
// so a plain phase-1 vehicle's lateral intent comes from Engine.ComputeLateralEvasion's crowd-swerve
// reacting to a CrowdSource pedestrian disc (Q6/option b: a CrowdSource agent is a first-class
// dodgeable threat, so the vehicle PREFERS to swerve around it rather than hard-stop -- see
// NormalModeCrowdSwerveTests). When the pedestrian's disc is wide enough that the swerve target sits
// right at the edge of what fits inside the (single, un-neighboured) 7.2 m lane, ego commits to a
// multi-tick swerve (SwerveMaxLateralSpeed = 2.0 m/s, so a ~2.7 m target takes 2 ticks at dt=1s) while
// Engine.CrowdLongitudinalConstraint (binder 13) still sees the pedestrian overlapping ego's CURRENT
// (not-yet-arrived) lateral footprint and keeps forward speed pinned near 0 -- so PosLat visibly moves
// while Speed stays ~0 for those ticks: "lateral motion without forward motion", the bug the owner
// flagged.
//
// The fix (already committed on this branch, NOT touched by this test): Engine.FreezeLateralWhenStopped
// (default false). In Engine's move-commit (Engine.cs, right after the position/lateral commit comment
// block referencing "Task A"), when FreezeLateralWhenStopped == true AND the vehicle's new speed is
// below Engine.LaneChangeMinSpeed, the per-step lateral commit is frozen to the vehicle's CURRENT
// PosLat instead of the freshly computed intent -- so a vehicle held below the lane-change speed floor
// cannot drift sideways at all. Default false => byte-identical on every golden (no committed
// golden/bench sets FreezeLateralWhenStopped or attaches a CrowdSource).
//
// Reproducing setup (found by sweeping pedX/pedRadius and printing per-tick Speed/PosLat -- see PR
// description for the raw sweep): the bridge-crossing-normal fixture (single 7.2 m lane, centreline
// y=-3.6, +x, NORMAL/non-sublane vehicle, vType "car" maxSpeed=5 sigma=0 departSpeed=0 from rou.rou.xml)
// with ONE stationary CrowdSource pedestrian centred on the lane at x=3.5 (goal == start position, so
// it never moves) with radius 1.3 m. VehHalfWidth=0.9 (default passenger width 1.8) + SwerveLateralGap
// 0.5 + pedRadius 1.3 = swerve target 2.7 m from lane centre -- exactly at the lane's clearance limit
// (7.2/2 - 0.9 = 2.7), so BOTH in-lane sides are (barely) feasible and the swerve is preferred over a
// hard stop (Q6 rung), but the pedestrian sits close enough (x=3.5, car departs at x=0 with
// departSpeed=0) that the car's speed is ~0 for the two ticks the swerve takes to complete -- while
// PosLat moves from 0 -> 2.0 -> 2.7 (verified empirically below).
public class StoppedCarLateralFreezeTests
{
    private static readonly string ScenarioDir =
        Path.Combine(RepoRoot(), "scenarios", "_fixtures", "bridge-crossing-normal");

    private const double PedX = 3.5;
    private const double PedY = -3.6;   // lane centreline (dead-centre on the car's lane)
    private const double PedRadius = 1.3;
    private const double LaneChangeMinSpeed = 1.5;
    private const int Steps = 40;

    private readonly ITestOutputHelper _out;

    public StoppedCarLateralFreezeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void FreezeOff_CarWobblesLaterally_WhileHeldNearlyStoppedByPedestrian()
    {
        var (speed, posLat) = Run(freeze: false);

        LogTrace("freeze OFF", speed, posLat);

        // Find the largest cumulative |PosLat| change (sum of consecutive step-to-step deltas) over any
        // run of consecutive "stopped" ticks (Speed < 0.3), i.e. the wobble that happens while the car
        // is NOT actually driving forward.
        var (windowLen, cumChange) = MaxCumulativeChangeWhileStopped(speed, posLat, stoppedThreshold: 0.3);

        _out.WriteLine($"freeze OFF: best stopped-window length={windowLen}, cumulative |PosLat| change={cumChange:F4} m");

        Assert.True(cumChange > 0.5,
            $"expected the un-fixed (freeze OFF) car to wobble laterally by more than 0.5 m while held " +
            $"nearly stopped (Speed < 0.3) by the pedestrian; observed only {cumChange:F4} m over a " +
            $"{windowLen}-tick stopped window");
    }

    [Fact]
    public void FreezeOn_PosLatIsFrozen_OnceSpeedDropsBelowLaneChangeMinSpeed()
    {
        var (speed, posLat) = Run(freeze: true);

        LogTrace("freeze ON", speed, posLat);

        var firstBelow = -1;
        for (var i = 0; i < speed.Count; i++)
        {
            if (speed[i] < LaneChangeMinSpeed)
            {
                firstBelow = i;
                break;
            }
        }

        Assert.True(firstBelow >= 0, "expected the car's speed to drop below LaneChangeMinSpeed at some point in this scenario");

        // From the first tick speed is below LaneChangeMinSpeed onward, PosLat must never move by more
        // than 1e-6 relative to the PREVIOUS tick, for as long as speed stays below the threshold.
        var frozenAt = posLat[firstBelow];
        for (var i = firstBelow; i < speed.Count; i++)
        {
            if (speed[i] >= LaneChangeMinSpeed)
            {
                break; // freeze only claims to hold while speed stays below the floor
            }

            var delta = Math.Abs(posLat[i] - frozenAt);
            Assert.True(delta <= 1e-6,
                $"tick {i}: PosLat drifted by {delta:E3} m (from {frozenAt:F6} to {posLat[i]:F6}) while " +
                $"Speed={speed[i]:F4} < LaneChangeMinSpeed={LaneChangeMinSpeed} -- FreezeLateralWhenStopped " +
                "should have held it exactly");
        }
    }

    [Fact]
    public void FreezeOn_IsDeterministic_AcrossIndependentRuns()
    {
        var run1 = Run(freeze: true);
        var run2 = Run(freeze: true);

        Assert.Equal(run1.Speed.Count, run2.Speed.Count);
        for (var i = 0; i < run1.Speed.Count; i++)
        {
            Assert.Equal(run1.Speed[i], run2.Speed[i], precision: 12);
            Assert.Equal(run1.PosLat[i], run2.PosLat[i], precision: 12);
        }
    }

    private static (List<double> Speed, List<double> PosLat) Run(bool freeze)
    {
        var engine = new Engine();   // NORMAL mode: no LanelessRvo, fixture has no lateral-resolution
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));
        engine.LaneChangeMinSpeed = LaneChangeMinSpeed;
        engine.FreezeLateralWhenStopped = freeze;

        var crowd = new OrcaCrowd();
        var pedPos = new Vec2(PedX, PedY);
        crowd.Add(pedPos, PedRadius, maxSpeed: 0.0, goal: pedPos); // stationary: goal == own position
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
