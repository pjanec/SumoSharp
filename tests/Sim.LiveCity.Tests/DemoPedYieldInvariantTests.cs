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
        long CloseFastPassCount,
        double WorstClearance,
        double WorstClearanceSpeed,
        long ArrivedTotal);

    [Fact]
    public void DemoAuthoritative_NoCarPassesPedInZoneCloseAndFast()
    {
        const int steps = 300; // 150 s at Dt=0.5 -- long enough for downtown traffic + crossing peds to
                                // actually interact inside the high-realism zone, short enough for a
                                // couple of minutes total across both arms.

        var sw = Stopwatch.StartNew();
        var baseline = RunArm(pedYieldEnv: "0", steps, "BASELINE (LIVECITY_PEDYIELD=0)");
        var baselineElapsed = sw.Elapsed;
        sw.Restart();
        var fixedArm = RunArm(pedYieldEnv: null, steps, "FIXED (Task-B guard on)");
        var fixedElapsed = sw.Elapsed;

        _out.WriteLine(
            $"BASELINE: close-fast-pass events = {baseline.CloseFastPassCount}, worst clearance "
            + $"{baseline.WorstClearance:F3} m @ {baseline.WorstClearanceSpeed:F2} m/s, ArrivedTotal = "
            + $"{baseline.ArrivedTotal}, wall time {baselineElapsed.TotalSeconds:F1} s.");
        _out.WriteLine(
            $"FIXED:    close-fast-pass events = {fixedArm.CloseFastPassCount}, worst clearance "
            + $"{fixedArm.WorstClearance:F3} m @ {fixedArm.WorstClearanceSpeed:F2} m/s, ArrivedTotal = "
            + $"{fixedArm.ArrivedTotal}, wall time {fixedElapsed.TotalSeconds:F1} s.");
        _out.WriteLine(
            $"Total wall time (both arms): {(baselineElapsed + fixedElapsed).TotalSeconds:F1} s.");

        if (fixedArm.CloseFastPassCount == 0)
        {
            _out.WriteLine("FIXED arm reached ZERO close-fast-pass events over this run.");
        }
        else
        {
            _out.WriteLine(
                $"FIXED arm did NOT reach zero: {fixedArm.CloseFastPassCount} close-fast-pass event(s) "
                + "remain. This is a legitimate result (the L2 guarantee bounds speed near a ped, it does "
                + "not claim zero over an arbitrary demo-scale run) -- reported plainly, not hidden.");
        }

        // (1) LIVE + NON-VACUOUS: the probe must actually detect the pre-Task-B defect (close AND fast
        //     passes are real and common in the baseline arm). If this is 0, the probe itself is dead
        //     (wrong zone, wrong radius, or no traffic ever entered the zone) -- per instructions this is
        //     reported, NOT patched by weakening the assertion.
        Assert.True(baseline.CloseFastPassCount > 0,
            $"expected the BASELINE arm (LIVECITY_PEDYIELD=0) to record > 0 close-fast-pass events "
            + $"(clearance < {CloseClearanceMeters:F1} m, speed > {FastSpeedMps:F1} m/s, car inside the "
            + $"high-realism zone), got 0. The probe is either dead or no car-ped close-fast encounter "
            + "occurred inside the zone during this run.");

        // (2) THE FIX: the FIXED arm (Task-B guard on) must record strictly fewer close-fast-pass events
        //     than the baseline -- the core regression guard for §3.1 (L1 swerve suppression) + §3.2
        //     (L2 CrowdYieldConstraint proximity cap).
        Assert.True(fixedArm.CloseFastPassCount < baseline.CloseFastPassCount,
            $"REGRESSION: FIXED arm close-fast-pass count ({fixedArm.CloseFastPassCount}) was not strictly "
            + $"less than the BASELINE count ({baseline.CloseFastPassCount}). The Task-B guard "
            + "(docs/LIVE-CITY-CAR-YIELDS-PED-DESIGN.md §3.1/§3.2) should reduce close-fast passes.");

        // (3) NO-NEW-GRIDLOCK TRIPWIRE: the guard must not tank throughput. "Within 15%" with a small
        //     absolute floor (2 vehicles) so the check stays meaningful even when the 150 s window yields
        //     a modest arrival count (a strict multiplicative 15% of a small integer rounds to near-zero
        //     tolerance, which would make the check flaky rather than diagnostic).
        var throughputTolerance = Math.Max(2.0, baseline.ArrivedTotal * 0.15);
        var throughputDelta = Math.Abs(fixedArm.ArrivedTotal - baseline.ArrivedTotal);
        Assert.True(throughputDelta <= throughputTolerance,
            $"REGRESSION: FIXED arm ArrivedTotal ({fixedArm.ArrivedTotal}) diverged from BASELINE "
            + $"({baseline.ArrivedTotal}) by {throughputDelta}, exceeding the 15% tripwire "
            + $"(tolerance {throughputTolerance:F1}). The Task-B guard should not newly gridlock the demo.");
    }

    private ArmResult RunArm(string? pedYieldEnv, int steps, string label)
    {
        var prevEnv = Environment.GetEnvironmentVariable("LIVECITY_PEDYIELD");
        try
        {
            // Latches in the LiveCitySim ctor -- must be set BEFORE `new LiveCitySim(cfg)`.
            Environment.SetEnvironmentVariable("LIVECITY_PEDYIELD", pedYieldEnv);

            var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
            // Pin the scenario so the assertions are about ENGINE behaviour, not config/env drift -- same
            // discipline as LiveCitySimTests' DenseFlow_OverAThousandSeconds_KeepsDischarging_NoGridlock:
            // explicit values for every knob a stray LIVECITY_* env var could otherwise perturb.
            cfg.CarTargetConcurrent = 160;      // default demo density
            cfg.PedPopulationCap = 160;         // ped count comparable to the demo (default)
            cfg.PedSpawnRatePerSecond = 8.0;    // default fill rate
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

            long closeFastPassCount = 0;
            var worstClearance = double.PositiveInfinity;
            var worstClearanceSpeed = 0.0;
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

                        if (carSpeed > FastSpeedMps && clearance < worstClearance)
                        {
                            worstClearance = clearance;
                            worstClearanceSpeed = carSpeed;
                        }

                        if (inZone && clearance < CloseClearanceMeters && carSpeed > FastSpeedMps)
                        {
                            closeFastPassCount++;
                        }
                    }
                }
            }

            return new ArmResult(closeFastPassCount, worstClearance, worstClearanceSpeed, sim.ArrivedTotal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVECITY_PEDYIELD", prevEnv);
        }
    }
}
