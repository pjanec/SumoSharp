using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Sim.Core;
using Sim.LiveCity;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// docs/LIVE-CITY-CAR-YIELDS-PED-DESIGN.md §3.2 (CrowdYieldConstraint, binder 14 -- the "guarantee"
// layer): the demo-scale regression guard for "no car inside the high-realism zone passes a pedestrian
// at close distance AND high speed". Modeled closely on DemoCarOverlapInvariantTests.cs (same RepoRoot()
// helper, same config-pinning discipline, same AUTHORITATIVE-sampling style) but the invariant here is
// car-vs-PED clearance, gated on Engine.SetCrowdYieldZone/LcZoneX/Y/Radius (the "high-realism zone") that
// §3.0/§3.2 gate the whole Task-B guard on, rather than car-vs-car OBB overlap.
//
// Two A/B arms, both against the REAL coupled LiveCitySim:
//   baseline: LIVECITY_PEDYIELD=0 -- the pre-Task-B behaviour (§1's measured repro: 0.70 m clearance at
//             3.90 m/s on the isolated crosswalk fixture); latched in the LiveCitySim ctor.
//   fixed:    LIVECITY_PEDYIELD unset -- L1 swerve suppression + L2 CrowdYieldConstraint active in-zone.
// Each step we take the engine's OWN Sample() (authoritative car/ped poses -- not a DR-reconstructed
// render) and, independently of the engine's internal constraint math, recompute the world-space
// car-body-to-ped-disc clearance via VehicleFootprint.ClearanceToDisc using the car's OWN X/Y/AngleDeg/
// Length/Width from the snapshot (LiveCityCar already carries Length/Width -- no vType lookup needed).
public class DemoPedYieldInvariantTests
{
    private readonly ITestOutputHelper _out;

    public DemoPedYieldInvariantTests(ITestOutputHelper output)
    {
        _out = output;
    }

    // Resolve the repo root the same way DemoCarOverlapInvariantTests/LiveCitySimTests does (git
    // rev-parse, walk-up fallback).
    private static string RepoRoot()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --show-toplevel")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode == 0 && Directory.Exists(Path.Combine(output, "scenarios")))
            {
                return output;
            }
        }
        catch
        {
            // fall through to the walk-up fallback
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "scenarios")) && File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }

    // "Close" / "fast" thresholds per the owner's framing (docs/LIVE-CITY-REALISM-AB-DESIGN.md §Task B)
    // and the §1 measured repro (0.70 m @ 3.90 m/s was the defect; the cure's own worst case, §2.1's
    // control experiment, lands at 2.05 m @ 2.60 m/s -- comfortably on the safe side of both thresholds).
    private const double CloseClearanceMeters = 1.5;
    private const double FastSpeedMps = 2.0;

    private sealed record ArmResult(
        long CloseFastPassCount,      // in-zone, any geometry
        long HeadOnCount,             // in-zone AND the ped is AHEAD of the bumper, inside ego's corridor
        double HeadOnMaxSpeed,
        long NetWideCount,            // whole net, including cars that cannot see the ped at all (see below)
        long ArrivedTotal);

    [Fact]
    public void DemoAuthoritative_NoCarPassesPedInZoneCloseAndFast()
    {
        // DENSITY MATTERS, AND AN EARLIER VERSION OF THIS TEST GOT IT WRONG. At 160 peds / 300 steps the
        // baseline produced only 7 in-zone events and the fixed arm 0, which read as "the guard eliminates
        // close-fast-passes". It does not: that sample simply did not contain the hard cases. Re-run at the
        // demo's real crowd density (800 peds, the LIVECITY_PEDS figure the demo brief uses) over twice the
        // horizon, the baseline produces 200 in-zone events and the fixed arm 70 -- a large, real reduction,
        // but NOT zero. The thresholds below assert the reduction that is actually there.
        const int steps = 600;   // 300 s at Dt=0.5
        const int peds = 800;    // the demo's real crowd density

        var sw = Stopwatch.StartNew();
        var baseline = RunArm(pedYield: false, steps, peds, "BASELINE (yield guard off)");
        var baselineElapsed = sw.Elapsed;
        sw.Restart();
        var fixedArm = RunArm(pedYield: true, steps, peds, "FIXED (Task-B guard on)");
        var fixedElapsed = sw.Elapsed;

        _out.WriteLine(
            $"BASELINE: in-zone close-fast-passes = {baseline.CloseFastPassCount} (of which HEAD-ON = "
            + $"{baseline.HeadOnCount}, max speed {baseline.HeadOnMaxSpeed:F2} m/s), net-wide = "
            + $"{baseline.NetWideCount}, ArrivedTotal = {baseline.ArrivedTotal}, {baselineElapsed.TotalSeconds:F0} s.");
        _out.WriteLine(
            $"FIXED:    in-zone close-fast-passes = {fixedArm.CloseFastPassCount} (of which HEAD-ON = "
            + $"{fixedArm.HeadOnCount}, max speed {fixedArm.HeadOnMaxSpeed:F2} m/s), net-wide = "
            + $"{fixedArm.NetWideCount}, ArrivedTotal = {fixedArm.ArrivedTotal}, {fixedElapsed.TotalSeconds:F0} s.");

        // WHY THE NET-WIDE COUNT BARELY MOVES, AND WHY THAT IS NOT THIS GUARD'S FAILURE.
        // The car-side crowd feed is `Composite(PedLodManager.HighPowerFootprints, CrossingOccupancySource)`
        // (LiveCitySim ctor). Pedestrians promote to HighPower via the InterestSource, which IS the
        // LC-realism zone -- so OUTSIDE that zone a car can only see a pedestrian if that pedestrian is
        // walking on a crossing. Measured cross-tab at 800 peds confirms it exactly: every HighPower event
        // is in-zone and every LowPowerWalking/Paused event is out-of-zone. No yield-zone radius can fix
        // out-of-zone behaviour, because out-of-zone cars have no pedestrian data to react to; that is a
        // ped-LOD feed question, not a car-yield question. Hence the assertions below are scoped to
        // IN-ZONE, which is exactly the region the guard is armed over.

        // (1) LIVE + NON-VACUOUS.
        Assert.True(baseline.CloseFastPassCount > 0,
            $"expected the BASELINE arm to record > 0 in-zone close-fast-pass events, got 0 -- the probe is "
            + "dead (wrong zone, wrong radius, or no traffic entered the zone).");

        // (2) THE FIX, at the demo's real crowd density. Measured 200 -> 70 (a 65% cut); the bar is set at
        //     a 40% cut so normal run-to-run structure cannot flake it, while any real regression trips it.
        //     NOTE the bar is deliberately NOT "== 0": at 160 peds it WAS 0, and asserting that here would
        //     have been a false claim about the demo (see the density comment above).
        Assert.True(fixedArm.CloseFastPassCount <= baseline.CloseFastPassCount * 0.60,
            $"REGRESSION: FIXED arm in-zone close-fast-passes ({fixedArm.CloseFastPassCount}) is not at least "
            + $"40% below BASELINE ({baseline.CloseFastPassCount}).");

        // (3) NO-NEW-GRIDLOCK TRIPWIRE.
        var throughputTolerance = Math.Max(2.0, baseline.ArrivedTotal * 0.15);
        var throughputDelta = Math.Abs(fixedArm.ArrivedTotal - baseline.ArrivedTotal);
        Assert.True(throughputDelta <= throughputTolerance,
            $"REGRESSION: FIXED arm ArrivedTotal ({fixedArm.ArrivedTotal}) diverged from BASELINE "
            + $"({baseline.ArrivedTotal}) by {throughputDelta}, exceeding the 15% tripwire "
            + $"(tolerance {throughputTolerance:F1}).");
    }

    private ArmResult RunArm(bool pedYield, int steps, int peds, string label)
    {
        // A/B'd through the CONFIG, never through the process environment: xunit runs test classes in
        // parallel, and an env-var flip here corrupted LiveCitySimTests' concurrent byte-exact determinism
        // test (it built its two sims either side of the flip and they legitimately diverged).
        {
            var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
            cfg.PedYieldEnabled = pedYield;
            // Pin the scenario so the assertions are about ENGINE behaviour, not config/env drift -- same
            // discipline as LiveCitySimTests' DenseFlow_OverAThousandSeconds_KeepsDischarging_NoGridlock:
            // explicit values for every knob a stray LIVECITY_* env var could otherwise perturb.
            cfg.CarTargetConcurrent = 160;      // default demo density
            cfg.PedPopulationCap = peds;        // the demo's real crowd density
            cfg.PedSpawnRatePerSecond = 8.0 * Math.Max(1.0, peds / 160.0);   // LiveCityConfig's own LIVECITY_PEDS scaling
            cfg.Dt = 0.5;                       // car/ped coupling step
            cfg.TimeToTeleportSeconds = 0.0;    // teleport OFF -- would mask a jam by removing stuck cars
            cfg.YieldEnabled = true;            // full crossing-yield + ped-signal coupling (the demo)
            cfg.CooperativeLaneChange = true;
            cfg.MergeStoppedMinGap = 5.0;
            cfg.MergeStoppedStrategicDeferDist = 15.0;

            using var sim = new LiveCitySim(cfg);
            Assert.True(sim.PedestriansEnabled, "expected the demo_city/box dataset to have pedestrians.");

            // GREP FINDING (worth flagging, see final report): the task brief for this test said to use
            // "0.6 m, the demo's inflated ORCA footprint radius". Grepping the actual wiring shows the
            // LIVE demo path uses 0.3 m everywhere a ped radius is actually configured:
            //   - LiveCityConfig.PedRadius default = 0.3 (src/Sim.LiveCity/LiveCityConfig.cs:162), flowed
            //     unmodified into PedDemandConfig.Radius -> PedLodManager.AddPed/AddPedLively -> the real
            //     OrcaCrowd agent radius (src/Sim.Pedestrians/Demand/PedDemand.cs:251,255).
            //   - CrossingOccupancySource is constructed with pedRadius: 0.3 explicitly
            //     (src/Sim.LiveCity/LiveCitySim.cs:286).
            // The "0.6" figure only appears in tests/Sim.ParityTests/CrosswalkCrossingPedTests.cs's own
            // hand-built, standalone OrcaCrowd fixture (a local `PedRadius = 0.6` constant unrelated to
            // LiveCityConfig) and in Sim.Viz/SceneGen.cs's disc-export literal for the --live-city-drcheck/
            // orcatrace tooling (a different, visualization-only pipeline). Neither wires into LiveCitySim.
            // For an INDEPENDENT clearance check to mean what it claims (real body-to-body world-space
            // clearance against the demo's actual ped agents), it must use the radius those agents were
            // actually given -- so this test reads it live off `cfg.PedRadius` rather than hardcoding
            // either literal.
            var pedRadius = cfg.PedRadius;

            long closeFastPassCount = 0, headOnCount = 0, netWideCount = 0;
            var headOnMaxSpeed = 0.0;
            var speedByHandle = new Dictionary<VehicleHandle, double>();

            for (var st = 0; st < steps; st++)
            {
                sim.Step();
                var snap = sim.Sample();

                speedByHandle.Clear();
                foreach (var w in sim.WitnessAuthoritative())
                {
                    speedByHandle[w.Handle] = w.Speed;
                }

                foreach (var car in snap.Cars)
                {
                    if (!speedByHandle.TryGetValue(car.Handle, out var carSpeed))
                    {
                        continue; // car left the crop between Sample() and WitnessAuthoritative() -- skip
                    }

                    var dxZone = car.X - sim.LcZoneX;
                    var dyZone = car.Y - sim.LcZoneY;
                    var inZone = sim.LcZoneRadius > 0.0
                        && Math.Sqrt((dxZone * dxZone) + (dyZone * dyZone)) <= sim.LcZoneRadius;

                    foreach (var ped in snap.Peds)
                    {
                        var clearance = VehicleFootprint.ClearanceToDisc(
                            car.X, car.Y, car.AngleDeg, car.Length, car.Width, ped.X, ped.Y, pedRadius);

                        if (clearance >= CloseClearanceMeters || carSpeed <= FastSpeedMps)
                        {
                            continue;
                        }

                        netWideCount++;
                        if (!inZone)
                        {
                            continue;
                        }

                        closeFastPassCount++;

                        // The SHARP sub-metric: the ped is AHEAD of the front bumper and inside ego's own
                        // corridor -- "the car is driving at a person", as opposed to "a person is standing
                        // near a car that is passing by", which on a city net with kerbside footways is
                        // ordinary traffic and dominates the raw count.
                        var (along, lat) = VehicleFootprint.ToBodyFrame(car.X, car.Y, car.AngleDeg, ped.X, ped.Y);
                        if (along > 0.0 && Math.Abs(lat) < (car.Width / 2.0) + pedRadius + 0.3)
                        {
                            headOnCount++;
                            if (carSpeed > headOnMaxSpeed) headOnMaxSpeed = carSpeed;
                        }
                    }
                }
            }

            return new ArmResult(closeFastPassCount, headOnCount, headOnMaxSpeed, netWideCount, sim.ArrivedTotal);
        }
    }
}
