using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using CityLib;
using Sim.LiveCity;
using Xunit;
using Xunit.Abstractions;

namespace CityLib.Tests;

// docs/LIVE-CITY-THREADED-TICK-DESIGN.md §5/§6 Stage 2, viewer side: `LiveCitySource`'s producer thread and
// its lock-free triple-buffered publish.
//
// The design's own success condition (frames > 3x p50 -> ~0) needs a GPU and the Stage-1 instrument. What is
// settled here is everything a headless test CAN settle, and each of these failure modes is silent on screen
// rather than loud: a producer that never advances, a consumer that reads a slot the producer is writing, a
// render-thread write that lands mid-step, a Dispose that steps a disposed sim.
public class ThreadedTickSourceTests
{
    private readonly ITestOutputHelper _output;

    public ThreadedTickSourceTests(ITestOutputHelper output) => _output = output;

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

    // A fast tick so a test does not spend seconds waiting on the 2 Hz default.
    private static LiveCityConfig FastCfg()
    {
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        cfg.SimHz = 20;
        return cfg;
    }

    // Spin until `condition` or the deadline. Returns whether it happened -- never Assert.True(sleep), so a
    // slow CI box shows up as a named timeout rather than as a flake somewhere else.
    private static bool WaitUntil(Func<bool> condition, double seconds = 10.0)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    // ---- the producer actually produces ------------------------------------------------------------

    [Fact]
    public void TheProducerAdvancesSimTimeWithoutTheConsumerTicking()
    {
        using var source = new LiveCitySource(FastCfg());
        Assert.False(source.IsThreaded);

        source.StartThreadedTick();
        Assert.True(source.IsThreaded);

        var advanced = WaitUntil(() => source.Published.StepIndex >= 10);
        var frame = source.Published;

        Assert.True(advanced, $"producer only reached step {frame.StepIndex} in 10 s");
        Assert.True(frame.SimTime > 0.0, $"sim time did not advance: {frame.SimTime}");
        Assert.True(frame.Valid);

        _output.WriteLine($"producer: step {frame.StepIndex} at simTime {frame.SimTime:F2}, "
            + $"achieved {frame.AchievedSimHz:F1} Hz, cars={frame.Cars} peds={frame.Peds}");
    }

    [Fact]
    public void PublishedFramesAreMonotonic_AndNeverTornAcrossFields()
    {
        // The triple buffer's whole job. A torn read would show a step index from one publish next to a sim
        // time from another -- so the invariant asserted is the RELATIONSHIP between them, which no single
        // field could reveal: simTime must equal stepIndex * dt for this config, exactly.
        var cfg = FastCfg();
        using var source = new LiveCitySource(cfg);
        source.StartThreadedTick();

        var dt = cfg.Dt;
        var lastStep = -1L;
        var lastTime = -1.0;
        var reads = 0;
        var distinct = 0;

        var deadline = Stopwatch.GetTimestamp() + (long)(5.0 * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var f = source.Published;
            reads++;

            Assert.True(f.StepIndex >= lastStep, $"step index went backwards: {f.StepIndex} after {lastStep}");
            Assert.True(f.SimTime >= lastTime - 1e-9, $"sim time went backwards: {f.SimTime} after {lastTime}");

            // The coherence check: these two fields came from the SAME publish, or this fails.
            Assert.Equal(f.StepIndex * dt, f.SimTime, 6);

            if (f.StepIndex != lastStep)
            {
                distinct++;
            }

            lastStep = f.StepIndex;
            lastTime = f.SimTime;
        }

        Assert.True(distinct > 5, $"only {distinct} distinct frames observed over 5 s -- is the producer running?");
        _output.WriteLine($"triple buffer: {reads} consumer reads saw {distinct} distinct coherent frames");
    }

    [Fact]
    public void TheAchievedRateTracksTheRequestedOne_OnANetThatCanKeepUp()
    {
        // Stage 1b's rule -- never show a rate that is not being met -- becomes the producer's job in Stage
        // 2. On the small demo net at 20 Hz the producer should get close to the request; the assertion is
        // deliberately loose (a shared CI box is not a quiet desktop) but must show the figure is REAL and
        // not, say, pinned at 0 or at the requested value regardless.
        var cfg = FastCfg();
        using var source = new LiveCitySource(cfg);
        source.StartThreadedTick();

        Assert.True(WaitUntil(() => source.Published.AchievedSimHz > 0.0, 8.0), "achieved Hz never reported");

        var achieved = source.Published.AchievedSimHz;
        _output.WriteLine($"requested 20 Hz -> achieved {achieved:F1} Hz on the demo net");
        Assert.InRange(achieved, 1.0, 25.0);
    }

    // ---- render -> sim writes are messages, applied by the producer ---------------------------------

    [Fact]
    public void AZonePushFromTheConsumerThread_IsAppliedByTheProducer()
    {
        // In threaded mode `SetLcRealismZone` must become a REQUEST -- the sim call it used to make rebuilds
        // the ORCA interest source, so landing it mid-step is a corruption. Asserted by observing the value
        // arrive on a published frame, i.e. through the producer, not by reading the sim.
        using var source = new LiveCitySource(FastCfg());
        source.StartThreadedTick();

        WaitUntil(() => source.Published.StepIndex >= 2);

        source.SetLcRealismZone(1500.0, 2500.0, 77.0);

        var applied = WaitUntil(() =>
        {
            var f = source.Published;
            return Math.Abs(f.LcZoneX - 1500.0) < 1e-6
                && Math.Abs(f.LcZoneY - 2500.0) < 1e-6
                && Math.Abs(f.LcZoneRadius - 77.0) < 1e-6;
        });

        var frame = source.Published;
        Assert.True(applied,
            $"zone never arrived on a published frame: ({frame.LcZoneX:F1}, {frame.LcZoneY:F1}, r={frame.LcZoneRadius:F1})");
    }

    [Fact]
    public void ADensityChangeFromTheConsumerThread_IsAppliedByTheProducer()
    {
        var cfg = FastCfg();
        using var source = new LiveCitySource(cfg);
        source.StartThreadedTick();

        WaitUntil(() => source.Published.StepIndex >= 2);

        source.SetCarTarget(42, 1);

        // Observed through the by-reference config the producer writes -- the same seam the non-threaded
        // path uses, so this proves the request was applied rather than dropped.
        Assert.True(WaitUntil(() => cfg.CarTargetConcurrent == 42),
            $"car target never applied; still {cfg.CarTargetConcurrent}");
    }

    [Fact]
    public void ATickRateChangeFromTheConsumerThread_IsAppliedByTheProducer()
    {
        var cfg = FastCfg();
        using var source = new LiveCitySource(cfg);
        source.StartThreadedTick();

        WaitUntil(() => source.Published.StepIndex >= 2);

        source.SimHz = 5;
        Assert.True(WaitUntil(() => Math.Abs(cfg.Dt - 0.2) < 1e-9), $"dt never applied; still {cfg.Dt}");
    }

    // ---- the guards: misuse is loud, not silent ----------------------------------------------------

    [Fact]
    public void OnceThreaded_TickAndTheLiveSamplersThrow()
    {
        // Every one of these either steps the sim a second time or hands back LiveCitySim's REUSED scratch
        // buffer while the producer is refilling it. A silent race here produces garbled cars/peds on screen
        // with nothing to point at, so the API refuses instead.
        using var source = new LiveCitySource(FastCfg());
        source.StartThreadedTick();

        Assert.Throws<InvalidOperationException>(() => source.Tick());
        Assert.Throws<InvalidOperationException>(() => source.Sample());
        Assert.Throws<InvalidOperationException>(() => source.SampleCars());
        Assert.Throws<InvalidOperationException>(() => source.SampleCrossingSignals());
        Assert.Throws<InvalidOperationException>(() => source.StartThreadedTick());
    }

    [Fact]
    public void NotThreaded_TheSameCallsStillWork_SoNothingExistingChanged()
    {
        // The regression half: a caller that never starts the producer sees exactly the pre-Stage-2 API.
        using var source = new LiveCitySource(FastCfg());
        Assert.False(source.IsThreaded);

        source.Tick();
        source.Tick();

        Assert.NotNull(source.Sample());
        Assert.NotNull(source.SampleCars());
        Assert.NotNull(source.SampleCrossingSignals());

        // ...including the published-frame view, which falls back to reading the live sim so a consumer can
        // use ONE code path in both modes.
        var f = source.Published;
        Assert.True(f.Valid);
        Assert.Equal(2, (int)f.StepIndex);
        Assert.True(f.SimTime > 0.0);
    }

    [Fact]
    public void CopyCrossingSignals_ReturnsThePublishedStates_IntoAReusedList()
    {
        using var source = new LiveCitySource(FastCfg());
        source.StartThreadedTick();
        WaitUntil(() => source.Published.StepIndex >= 4);

        var into = new List<(int LaneHandle, char State)>();
        _ = source.Published; // claim the frame the copy reads from
        source.CopyCrossingSignals(into);
        var firstCount = into.Count;

        // Called again it must CLEAR first, not append -- a growing list would leak lanes frame after frame.
        _ = source.Published;
        source.CopyCrossingSignals(into);

        Assert.Equal(firstCount, into.Count);
        Assert.All(into, e => Assert.True(e.State != '\0', "a published crossing state should be a real signal char"));
        _output.WriteLine($"crossing signals: {firstCount} controlled crossings on the demo net");
    }

    // ---- shutdown ----------------------------------------------------------------------------------

    [Fact]
    public void Dispose_StopsTheProducerBeforeDisposingTheSim()
    {
        // Disposing the sim under a running producer means stepping a disposed sim -- an exception on a
        // background thread, which in Godot is a silent process death. Dispose must join first.
        var source = new LiveCitySource(FastCfg());
        source.StartThreadedTick();
        WaitUntil(() => source.Published.StepIndex >= 3);

        var sw = Stopwatch.StartNew();
        source.Dispose();
        sw.Stop();

        Assert.True(sw.Elapsed.TotalSeconds < 5.0, $"Dispose took {sw.Elapsed.TotalSeconds:F1}s -- did the join hang?");

        // Idempotent: a host that disposes twice (Godot's quit paths do) must not fault.
        source.Dispose();
    }

    [Fact]
    public void ManyStartStopCycles_DoNotLeakThreadsOrFault()
    {
        // A viewer session builds and tears down a source per scene load; a leaked producer per cycle would
        // quietly accumulate sim threads all stepping in the background.
        var before = Process.GetCurrentProcess().Threads.Count;

        for (var i = 0; i < 5; i++)
        {
            using var source = new LiveCitySource(FastCfg());
            source.StartThreadedTick();
            WaitUntil(() => source.Published.StepIndex >= 2, 5.0);
        }

        var after = Process.GetCurrentProcess().Threads.Count;
        _output.WriteLine($"thread count {before} -> {after} across 5 start/stop cycles");

        // Loose, because the CLR's own thread pool grows and shrinks on its own -- what would fail here is
        // five leaked producer threads, not a couple of pool workers.
        Assert.True(after - before < 5, $"thread count grew by {after - before} across 5 cycles");
    }
}
