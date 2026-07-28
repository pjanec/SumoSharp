using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Sim.LiveCity;
using Sim.Pedestrians.Lod;
using Sim.Replication;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// docs/LIVE-CITY-THREADED-TICK-DESIGN.md §5/§6 Stage 2 and Stage 3, engine side.
//
// Stage 2's own success condition (frames > 3x p50 -> ~0) can only be measured on a GPU with the Stage-1
// instrument. What CAN be settled headlessly -- and must be, because the failure modes are silent -- is
// the two mechanisms the threading rests on:
//
//   * the `Request*` slots: a render thread's writes must land at a DEFINED point (the top of the next
//     step), last-writer-wins, and must actually take effect;
//   * Stage 3's bounded ped-event history: the publisher must stop growing without bound, and the ped WIRE
//     must still reconstruct the same poses the in-process sim has -- which is the assertion that catches a
//     pooled-buffer bug, since a stale-slack decode shows up as a wrong pose, not as an exception.
public class ThreadedTickHandoffTests
{
    private readonly ITestOutputHelper _output;

    public ThreadedTickHandoffTests(ITestOutputHelper output) => _output = output;

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
            // fall through
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

    private static LiveCityConfig DemoCfg() => LiveCityConfig.ForRepoRoot(RepoRoot());

    // ---- §5: render -> sim writes become messages ---------------------------------------------------

    [Fact]
    public void ARequestedZone_IsNotAppliedUntilTheNextStep()
    {
        // The whole point of the request slot: the value must NOT land the instant the render thread asks,
        // because "the instant the render thread asks" is somewhere inside a step.
        using var sim = new LiveCitySim(DemoCfg());
        sim.Step();

        var before = (sim.LcZoneX, sim.LcZoneY, sim.LcZoneRadius);
        sim.RequestLcRealismZone(before.LcZoneX + 250.0, before.LcZoneY + 150.0, 90.0);

        Assert.Equal(before.LcZoneX, sim.LcZoneX, 9); // still untouched
        Assert.Equal(before.LcZoneY, sim.LcZoneY, 9);
        Assert.Equal(before.LcZoneRadius, sim.LcZoneRadius, 9);

        sim.Step();

        Assert.Equal(before.LcZoneX + 250.0, sim.LcZoneX, 6);
        Assert.Equal(before.LcZoneY + 150.0, sim.LcZoneY, 6);
        Assert.Equal(90.0, sim.LcZoneRadius, 6);
    }

    [Fact]
    public void TheLastRequestBeforeAStepWins_AndEarlierOnesAreDiscarded()
    {
        // Last-writer-wins is the correct semantics for a UI dial or a camera-driven zone: applying every
        // intermediate value a 60 Hz frame loop produced would be both pointless and expensive (a radius
        // change rebuilds the ORCA interest source).
        using var sim = new LiveCitySim(DemoCfg());
        sim.Step();

        for (var i = 1; i <= 20; i++)
        {
            sim.RequestLcRealismZone(1000.0 + i, 2000.0 + i, 40.0 + i);
        }

        sim.Step();

        Assert.Equal(1020.0, sim.LcZoneX, 6);
        Assert.Equal(2020.0, sim.LcZoneY, 6);
        Assert.Equal(60.0, sim.LcZoneRadius, 6);
    }

    [Fact]
    public void ARequestedDt_TakesEffectOnTheStepThatObservesIt()
    {
        // Applied BEFORE `dt` is read, so the requesting step advances by the NEW dt -- not the one after.
        // Getting this backwards would make the tick-rate slider feel a step late at low Hz.
        using var sim = new LiveCitySim(DemoCfg());
        sim.Step();

        var t0 = sim.Time;
        sim.RequestDt(0.25);
        Assert.Equal(0.5, sim.Dt, 9); // not yet

        sim.Step();

        Assert.Equal(0.25, sim.Dt, 9);
        Assert.Equal(t0 + 0.25, sim.Time, 6);
    }

    [Fact]
    public void RequestedDensities_AreAppliedAtTheNextStep()
    {
        using var sim = new LiveCitySim(DemoCfg());
        sim.Step();

        // The car target lives on the by-reference config the sim holds, so the config object is the
        // observable -- that is the same seam `SetCarDensity` writes through.
        var cfg = DemoCfg();
        using var sim2 = new LiveCitySim(cfg);
        sim2.Step();
        sim2.RequestCarDensity(777);
        Assert.NotEqual(777, cfg.CarTargetConcurrent); // not yet
        sim2.Step();
        Assert.Equal(777, cfg.CarTargetConcurrent);

        sim.RequestPedDensity(321, 9.5);
        sim.Step();

        Assert.NotNull(sim.PedDemand);
        Assert.Equal(321, sim.PedDemand!.PopulationCap);
        Assert.Equal(9.5, sim.PedDemand.SpawnRatePerSecond, 9);
    }

    [Fact]
    public void WithNoPendingRequest_AStepChangesNothingAboutTheZoneOrDt()
    {
        // The inert case, so `ApplyPendingRequests` cannot be quietly re-applying a stale slot every step
        // (which would, among other things, rebuild the interest source on every tick).
        using var sim = new LiveCitySim(DemoCfg());
        sim.RequestLcRealismZone(1234.0, 5678.0, 55.0);
        sim.Step();

        for (var i = 0; i < 5; i++)
        {
            sim.Step();
            Assert.Equal(1234.0, sim.LcZoneX, 6);
            Assert.Equal(5678.0, sim.LcZoneY, 6);
            Assert.Equal(55.0, sim.LcZoneRadius, 6);
            Assert.Equal(0.5, sim.Dt, 9);
        }
    }

    // ---- A22: engine parallelism is capped, and the cap is opt-in -----------------------------------

    [Fact]
    public void MaxParallelismResolution_IsUncappedByDefault_AndLeavesHeadroomWhenAsked()
    {
        // -1 for every existing caller is what keeps this inert: a bench and the whole test suite are
        // byte-identical to before the knob existed.
        Assert.Equal(-1, new LiveCityConfig().ResolveMaxParallelism());

        var explicitCap = new LiveCityConfig { MaxParallelism = 6 };
        Assert.Equal(6, explicitCap.ResolveMaxParallelism());

        var headroom = new LiveCityConfig { LeaveCoresFree = 4 };
        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 4), headroom.ResolveMaxParallelism());

        // ...and it can never resolve to 0 or negative on a small machine, which would mean "uncapped"
        // to ParallelOptions -- the exact opposite of what was asked for.
        var overSubtracted = new LiveCityConfig { LeaveCoresFree = 1024 };
        Assert.Equal(1, overSubtracted.ResolveMaxParallelism());

        // An explicit cap wins over the headroom form.
        var both = new LiveCityConfig { MaxParallelism = 3, LeaveCoresFree = 8 };
        Assert.Equal(3, both.ResolveMaxParallelism());
    }

    [Fact]
    public void ACappedSim_StillProducesTheSameTrajectory_AsAnUncappedOne()
    {
        // Both parallel knobs are scheduling-only, so capping them must not move a single car or ped. This
        // is the behavioural licence for A22 -- without it, "leave cores for the renderer" would be a
        // trajectory change hiding inside a perf tweak.
        static List<(int Id, double X, double Y)> Run(int leaveCoresFree)
        {
            var cfg = DemoCfg();
            cfg.LeaveCoresFree = leaveCoresFree;
            using var sim = new LiveCitySim(cfg);
            var trace = new List<(int, double, double)>();
            for (var i = 0; i < 60; i++)
            {
                sim.Step();
                var snap = sim.Sample();
                foreach (var car in snap.Cars)
                {
                    trace.Add((car.Handle.Index == 0 ? 0 : (int)car.Handle.Index, car.X, car.Y));
                }

                foreach (var ped in snap.Peds)
                {
                    trace.Add((-1 - ped.Id, ped.X, ped.Y));
                }
            }

            return trace;
        }

        var uncapped = Run(0);
        var capped = Run(4);

        Assert.NotEmpty(uncapped);
        Assert.Equal(uncapped.Count, capped.Count);
        for (var i = 0; i < uncapped.Count; i++)
        {
            Assert.Equal(uncapped[i].Id, capped[i].Id);
            Assert.Equal(BitConverter.DoubleToInt64Bits(uncapped[i].X), BitConverter.DoubleToInt64Bits(capped[i].X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(uncapped[i].Y), BitConverter.DoubleToInt64Bits(capped[i].Y));
        }

        _output.WriteLine($"A22: {uncapped.Count} car+ped samples bitwise identical, uncapped vs "
            + $"{Math.Max(1, Environment.ProcessorCount - 4)}-of-{Environment.ProcessorCount} cores");
    }

    // ---- Stage 3: the ped-event history is bounded, and the wire still agrees with the sim ----------

    [Fact]
    public void ThePedEventHistory_IsDrainedEveryStep_InsteadOfGrowingForever()
    {
        // A6. `PedPublisher.Events` was append-only: one heap record per published ped per step, retained
        // for the life of the process. It is now drained into the wire batch and cleared each step.
        //
        // Two assertions, because either alone is worthless: the history must be EMPTY after every step
        // (it no longer accumulates), and the per-step batch must be NON-ZERO (so the emptiness is a drain,
        // not simply nothing ever being published).
        var cfg = DemoCfg();
        using var sim = new LiveCitySim(cfg);

        var batches = new List<int>();
        for (var i = 0; i < 120; i++)
        {
            sim.Step();
            Assert.Equal(0, sim.PedEventHistoryCount);
            batches.Add(sim.LastPedEventBatchCount);
        }

        var total = batches.Sum();
        var peak = batches.Max();

        _output.WriteLine($"Stage 3: {total} ped events published over 120 steps (peak batch {peak}); the "
            + "publisher's retained history was 0 after every one of them");

        Assert.True(total > 100, $"expected real ped publishing to have happened; total batch was {total}");
        Assert.True(peak > 0);
    }

    [Fact]
    public void ThePedWireStillReconstructsTheSamePosesTheSimHas_OverAPooledBus()
    {
        // THE Stage-3 correctness test. The ped bus now recycles its payload buffers, and a recycled buffer
        // that is longer than the payload carries the PREVIOUS publish's bytes in its tail. If any decode
        // path read that slack, peds would reconstruct at wrong positions -- silently, with no exception.
        //
        // So: reconstruct every ped off the wire and compare against the sim's own `Sample()`. This is the
        // server==IG identity, and it is exactly what a stale-slack decode would break.
        var cfg = DemoCfg();
        using var sim = new LiveCitySim(cfg);

        var recon = new PedRemoteReconstructor(sim.PedSource, playoutDelaySeconds: 0.0);

        var worst = 0.0;
        var compared = 0;

        for (var step = 0; step < 120; step++)
        {
            sim.Step();
            recon.Pump(sim.Time);

            if (step < 20)
            {
                continue; // let the crowd populate before judging
            }

            var snap = sim.Sample();
            foreach (var ped in snap.Peds)
            {
                if (!recon.TryGetRenderPose(ped.Id, out var pos, out _, out var visible, out _) || !visible)
                {
                    continue;
                }

                var dx = pos.X - ped.X;
                var dy = pos.Y - ped.Y;
                var d = Math.Sqrt((dx * dx) + (dy * dy));
                worst = Math.Max(worst, d);
                compared++;
            }
        }

        Assert.True(compared > 500, $"expected a real crowd to compare; got {compared} paired samples");
        _output.WriteLine($"Stage 3: {compared} wire-vs-sim ped poses, worst |delta| = {worst:F3} m");

        // A metre is loose on purpose: the wire quantizes to 1 cm and the reconstructor applies capped-
        // correction smoothing, so an exact match is not the claim. A decode reading stale slack bytes would
        // land tens or hundreds of metres out, or at the origin -- nowhere near this.
        Assert.True(worst < 1.0, $"worst wire-vs-sim ped divergence {worst:F3} m -- a corrupted decode?");
    }

    [Fact]
    public void ThePedBusPayloadBuffers_StopBeingAllocatedOnceWarm()
    {
        // The zero-alloc half of Stage 3, on the real host rather than a synthetic publish loop.
        var cfg = DemoCfg();
        using var sim = new LiveCitySim(cfg);

        for (var i = 0; i < 40; i++)
        {
            sim.Step();
            sim.PedSource.Pump();
        }

        var afterWarmup = sim.PedBusBuffersAllocated;

        for (var i = 0; i < 60; i++)
        {
            sim.Step();
            sim.PedSource.Pump();
        }

        var afterRun = sim.PedBusBuffersAllocated;
        _output.WriteLine($"Stage 3: ped bus buffers {afterWarmup} after warmup -> {afterRun} after 60 more steps");

        // Not "exactly equal": the live crowd is still growing over these steps, so a bigger batch can
        // legitimately need one bigger buffer. What must NOT happen is an allocation per step.
        Assert.True(afterRun - afterWarmup < 20,
            $"{afterRun - afterWarmup} new buffers over 60 steps suggests the pool is not being used");
    }
}
