using System;
using System.Diagnostics;
using System.IO;
using Sim.LiveCity;
using Xunit;

namespace Sim.LiveCity.Tests;

public class LiveCitySimTests
{
    // Resolve the repo root the same way CLAUDE.md prescribes ("git rev-parse --show-toplevel"), with a
    // walk-up-from-AppContext.BaseDirectory fallback for an environment without git on PATH.
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

    private static LiveCityConfig MakeConfig(bool yield = true, double? dt = null)
    {
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        cfg.YieldEnabled = yield;
        if (dt is { } d)
        {
            cfg.Dt = d;
        }

        return cfg;
    }

    [Fact]
    public void CoupledSim_OverAFewMinutes_ProducesCarsPedsAndYieldEvents()
    {
        using var sim = new LiveCitySim(MakeConfig(yield: true));

        for (var i = 0; i < 120; i++)
        {
            sim.Step();
        }

        Assert.True(sim.PeakCars > 0, $"expected PeakCars > 0, got {sim.PeakCars}");
        Assert.True(sim.PeakPeds > 0, $"expected PeakPeds > 0, got {sim.PeakPeds}");
        Assert.True(sim.PeakOccupiedCrossings > 0, $"expected PeakOccupiedCrossings > 0, got {sim.PeakOccupiedCrossings}");
        Assert.True(sim.CarYieldObservations > 0, $"expected CarYieldObservations > 0, got {sim.CarYieldObservations}");

        // Wire non-vacuousness: pump both sources and assert something real arrived.
        sim.VehicleSource.Pump();
        Assert.True(sim.VehicleSource.History.Count > 0, "expected >=1 vehicle in the replicated History");

        sim.PedSource.Pump();
        Assert.True(sim.PedSource.LatestCrowdFrame.Count > 0, "expected >=1 ped in the latest crowd frame");
    }

    [Fact]
    public void TwoRuns_SameConfig_AreByteExactDeterministic()
    {
        using var simA = new LiveCitySim(MakeConfig(yield: true));
        using var simB = new LiveCitySim(MakeConfig(yield: true));

        for (var step = 0; step < 120; step++)
        {
            simA.Step();
            simB.Step();

            var snapA = simA.Sample();
            var snapB = simB.Sample();

            Assert.Equal(snapA.Cars.Count, snapB.Cars.Count);
            for (var i = 0; i < snapA.Cars.Count; i++)
            {
                var a = snapA.Cars[i];
                var b = snapB.Cars[i];
                Assert.Equal(a.Handle, b.Handle);
                Assert.Equal(a.X, b.X);
                Assert.Equal(a.Y, b.Y);
                Assert.Equal(a.Z, b.Z);
                Assert.Equal(a.AngleDeg, b.AngleDeg);
            }

            Assert.Equal(snapA.Peds.Count, snapB.Peds.Count);
            for (var i = 0; i < snapA.Peds.Count; i++)
            {
                var a = snapA.Peds[i];
                var b = snapB.Peds[i];
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.X, b.X);
                Assert.Equal(a.Y, b.Y);
                Assert.Equal(a.Regime, b.Regime);
            }
        }
    }

    // docs/LIVE-CITY-VISUALS-NOTES.md (tick-rate task), deliverable 1: the SAME determinism proof as
    // TwoRuns_SameConfig_AreByteExactDeterministic above, but at Dt=0.1 (10 Hz, cfg.SimHz's non-default
    // side) instead of the 0.5 (2 Hz) default -- proves LiveCityConfig.Dt/SimHz plumbs all the way through
    // to LiveCitySim's engine step-length (via the InvariantCulture-formatted config XML) and the ped
    // demand's stepDt without breaking either the coupled sim's liveness (cars>0 && peds>0) or its
    // byte-exact determinism (same seed+Dt => identical run).
    [Fact]
    public void TwoRuns_AtTenHz_AreByteExactDeterministic_AndProduceCarsAndPeds()
    {
        using var simA = new LiveCitySim(MakeConfig(yield: true, dt: 0.1));
        using var simB = new LiveCitySim(MakeConfig(yield: true, dt: 0.1));

        for (var step = 0; step < 120; step++)
        {
            simA.Step();
            simB.Step();

            var snapA = simA.Sample();
            var snapB = simB.Sample();

            Assert.Equal(snapA.Cars.Count, snapB.Cars.Count);
            for (var i = 0; i < snapA.Cars.Count; i++)
            {
                var a = snapA.Cars[i];
                var b = snapB.Cars[i];
                Assert.Equal(a.Handle, b.Handle);
                Assert.Equal(a.X, b.X);
                Assert.Equal(a.Y, b.Y);
                Assert.Equal(a.Z, b.Z);
                Assert.Equal(a.AngleDeg, b.AngleDeg);
            }

            Assert.Equal(snapA.Peds.Count, snapB.Peds.Count);
            for (var i = 0; i < snapA.Peds.Count; i++)
            {
                var a = snapA.Peds[i];
                var b = snapB.Peds[i];
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.X, b.X);
                Assert.Equal(a.Y, b.Y);
                Assert.Equal(a.Regime, b.Regime);
            }
        }

        Assert.True(simA.PeakCars > 0, $"expected PeakCars > 0 at Dt=0.1, got {simA.PeakCars}");
        Assert.True(simA.PeakPeds > 0, $"expected PeakPeds > 0 at Dt=0.1, got {simA.PeakPeds}");
    }

    [Fact]
    public void YieldOnVsOff_ProduceDifferentCoupling_AndYieldOnIsPositive()
    {
        using var simOn = new LiveCitySim(MakeConfig(yield: true));
        using var simOff = new LiveCitySim(MakeConfig(yield: false));

        var trajectoryDiffers = false;

        for (var step = 0; step < 120; step++)
        {
            simOn.Step();
            simOff.Step();

            var onSnap = simOn.Sample();
            var offSnap = simOff.Sample();

            if (onSnap.Cars.Count != offSnap.Cars.Count)
            {
                trajectoryDiffers = true;
                continue;
            }

            for (var i = 0; i < onSnap.Cars.Count; i++)
            {
                if (Math.Abs(onSnap.Cars[i].X - offSnap.Cars[i].X) > 1e-9
                    || Math.Abs(onSnap.Cars[i].Y - offSnap.Cars[i].Y) > 1e-9)
                {
                    trajectoryDiffers = true;
                    break;
                }
            }
        }

        Assert.True(simOn.CarYieldObservations > 0, $"expected yield-ON CarYieldObservations > 0, got {simOn.CarYieldObservations}");
        Assert.True(
            trajectoryDiffers || simOn.CarYieldObservations != simOff.CarYieldObservations,
            $"expected yield ON/OFF to differ: onObs={simOn.CarYieldObservations} offObs={simOff.CarYieldObservations} trajectoryDiffers={trajectoryDiffers}");
    }
}
