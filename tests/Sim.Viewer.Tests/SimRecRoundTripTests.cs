using System;
using System.Diagnostics;
using System.IO;
using Sim.LiveCity;
using Sim.Replication;
using Sim.Replication.Recording;
using Xunit;

namespace Sim.Viewer.Tests;

// docs/LIVE-CITY-VIEWERS-TASKS.md Stage C (C1/C2) success conditions:
//   C1 -- recording a LiveCitySim run yields a non-empty .simrec; a round-trip test reads back the same
//         geometry/lifecycle/frame/TL and ped records that were written.
//   C2 -- replaying a recording reconstructs positions matching the live run at the same sim times within
//         DR tolerance; SeekTo(t) for an arbitrary t (incl. backward) yields the same state as playing
//         linearly to t; consumed unchanged by Reconstructor (RenderHelpers.PumpAndBuildVehicleDraws, the
//         exact call every windowed live-city mode uses).
public sealed class SimRecRoundTripTests
{
    // Mirrors LiveCitySimTests.RepoRoot -- resolves the repo root via `git rev-parse --show-toplevel`
    // (CLAUDE.md's prescribed method), with a walk-up fallback for an environment without git on PATH.
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

    private static LiveCityConfig MakeConfig() => LiveCityConfig.ForRepoRoot(RepoRoot());

    private static (string Path, double Duration, int Steps) RecordRun(int steps)
    {
        var cfg = MakeConfig();
        var path = Path.Combine(Path.GetTempPath(), $"simrec-test-{Guid.NewGuid():N}.simrec");

        using (var recorder = new RecordingReplicationSink(path, cfg.Dt, datasetId: "test"))
        using (var sim = new LiveCitySim(cfg, recorder))
        {
            for (var i = 0; i < steps; i++)
            {
                sim.Step();
                recorder.WritePedFrame(sim.Time, ToTuples(sim.Sample().Peds));
            }
        }

        return (path, steps * cfg.Dt, steps);
    }

    private static (int, float, float, float, byte, string)[] ToTuples(System.Collections.Generic.IReadOnlyList<LiveCityPed> peds)
    {
        var arr = new (int, float, float, float, byte, string)[peds.Count];
        for (var i = 0; i < peds.Count; i++)
        {
            var p = peds[i];
            arr[i] = (p.Id, (float)p.X, (float)p.Y, (float)p.Z, (byte)p.Regime, p.AnimTag);
        }

        return arr;
    }

    [Fact]
    public void RecordingLiveCitySim_ProducesNonEmptySimrec_WithGeometryLifecycleFrameAndPedRecords()
    {
        var (path, _, steps) = RecordRun(steps: 60);
        try
        {
            var fileInfo = new FileInfo(path);
            Assert.True(fileInfo.Length > 0, "expected a non-empty .simrec file");

            var geometryCount = 0;
            var lifecycleCount = 0;
            var frameCount = 0;
            var tlCount = 0;
            var pedFrameCount = 0;
            var pedRecordsSeen = 0;

            using (var reader = new SimRecReader(path))
            {
                Assert.Equal("test", reader.DatasetId);
                while (reader.TryReadNext(out var entry))
                {
                    switch (entry.Kind)
                    {
                        case SimRecFormat.RecordType.Geometry:
                            geometryCount++;
                            Assert.True(entry.GeometryBytes!.Length > 0);
                            break;
                        case SimRecFormat.RecordType.Lifecycle:
                            lifecycleCount++;
                            break;
                        case SimRecFormat.RecordType.VehicleFrame:
                            frameCount++;
                            break;
                        case SimRecFormat.RecordType.TrafficLights:
                            tlCount++;
                            break;
                        case SimRecFormat.RecordType.PedFrame:
                            pedFrameCount++;
                            pedRecordsSeen += entry.Peds!.Length;
                            break;
                    }
                }
            }

            Assert.Equal(1, geometryCount); // published once
            Assert.True(lifecycleCount > 0, $"expected >=1 lifecycle (spawn) record, got {lifecycleCount}");
            Assert.True(frameCount > 0, $"expected >=1 vehicle frame, got {frameCount}");
            Assert.Equal(steps, pedFrameCount); // one PEDFRAME per LiveCitySim.Step()
            Assert.True(pedRecordsSeen > 0, $"expected >=1 ped record across all PEDFRAMEs, got {pedRecordsSeen}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Replay_MatchesLiveHistory_AtTheSameSimTime_WithinDrTolerance()
    {
        // Record a fresh, independent run (not the same LiveCitySim instance the assertions below re-derive
        // from -- LiveCitySimTests.TwoRuns_SameConfig_AreByteExactDeterministic already proves two
        // same-config runs are byte-identical, so a SEPARATE live run is a valid ground truth for the
        // recorded one).
        var (path, _, steps) = RecordRun(steps: 80);
        try
        {
            // Ground truth: a second, byte-identical LiveCitySim run (same seeds/config).
            var cfg = MakeConfig();
            using var liveSim = new LiveCitySim(cfg);
            for (var i = 0; i < steps; i++)
            {
                liveSim.Step();
            }

            var liveSnap = liveSim.Sample();
            Assert.True(liveSnap.Cars.Count > 0, "expected >=1 live car to compare against");

            var clock = new PlaybackClock();
            using var fileSource = new ReplicationFileSource(path, clock);
            clock.Duration = fileSource.Duration;
            clock.SeekTo(liveSim.Time);
            fileSource.Pump();

            Assert.True(fileSource.GeometryComplete, "expected replay geometry to be complete after pumping to the final time");
            Assert.True(fileSource.History.Count > 0, "expected >=1 vehicle in the replayed History");

            var matched = 0;
            foreach (var car in liveSnap.Cars)
            {
                if (!fileSource.TryGetLatest(car.Handle, out var sample))
                {
                    continue; // a vehicle whose last publish predates the scheduler's adaptive gate this exact tick
                }

                matched++;
                var dx = sample.Record.Pos - FindLiveArc(liveSim, car);
                // Same-lane arc-length position should match closely -- both runs are the SAME deterministic
                // recipe (LiveCitySimTests proves byte-exact reproduction), and the wire itself only loses
                // float32 precision, so a few cm of tolerance is generous, not loose.
                Assert.True(Math.Abs(dx) < 0.05, $"vehicle {car.Handle}: replay Pos={sample.Record.Pos:F4} vs live-derived arc {FindLiveArc(liveSim, car):F4} (dx={dx:F4})");
            }

            Assert.True(matched > 0, "expected >=1 vehicle to be comparable between the live run and its replay");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The live run's SimulationSnapshot doesn't expose per-vehicle lane-arc `Pos` through LiveCitySnapshot
    // (LiveCityCar only carries world X/Y), so pull it straight from the engine's own snapshot via the
    // handle -- the same authoritative source LiveCitySim.Sample() itself reads from.
    private static double FindLiveArc(LiveCitySim liveSim, LiveCityCar car)
    {
        liveSim.VehicleSource.Pump();
        if (liveSim.VehicleSource.TryGetLatest(car.Handle, out var sample))
        {
            return sample.Record.Pos;
        }

        return double.NaN;
    }

    [Fact]
    public void SeekTo_ArbitraryInteriorTime_MatchesLinearPlaythroughToThatTime()
    {
        var (path, duration, _) = RecordRun(steps: 100);
        try
        {
            var targetT = duration * 0.6;

            // Linear playthrough to targetT.
            var clockA = new PlaybackClock();
            using var sourceA = new ReplicationFileSource(path, clockA);
            clockA.Duration = sourceA.Duration;
            var dt = sourceA.Dt > 0.0 ? sourceA.Dt : 0.5;
            for (var t = 0.0; t <= targetT; t += dt)
            {
                clockA.SeekTo(Math.Min(t, targetT));
                sourceA.Pump();
            }

            clockA.SeekTo(targetT);
            sourceA.Pump();

            // Direct SeekTo from a freshly-opened source.
            var clockB = new PlaybackClock();
            using var sourceB = new ReplicationFileSource(path, clockB);
            clockB.Duration = sourceB.Duration;
            sourceB.SeekTo(targetT);

            Assert.Equal(sourceA.History.Count, sourceB.History.Count);
            Assert.True(sourceA.History.Count > 0, "expected >=1 vehicle at the interior seek target");

            foreach (var handle in sourceA.History.Keys)
            {
                Assert.True(sourceB.TryGetLatest(handle, out var sampleB), $"vehicle {handle} missing from the direct-seek source");
                Assert.True(sourceA.TryGetLatest(handle, out var sampleA));
                Assert.Equal(sampleA.Record.Pos, sampleB.Record.Pos, 3);
                Assert.Equal(sampleA.Record.LaneHandle, sampleB.Record.LaneHandle);
            }

            // Now seek BACKWARD from targetT to an earlier time on sourceA, and confirm it matches a fresh
            // direct seek to that earlier time too (the design's explicit "incl. a backward seek" case).
            var earlierT = duration * 0.25;
            sourceA.SeekTo(earlierT);

            var clockC = new PlaybackClock();
            using var sourceC = new ReplicationFileSource(path, clockC);
            clockC.Duration = sourceC.Duration;
            sourceC.SeekTo(earlierT);

            Assert.Equal(sourceC.History.Count, sourceA.History.Count);
            foreach (var handle in sourceC.History.Keys)
            {
                Assert.True(sourceA.TryGetLatest(handle, out var sA), $"vehicle {handle} missing after backward SeekTo");
                Assert.True(sourceC.TryGetLatest(handle, out var sC));
                Assert.Equal(sC.Record.Pos, sA.Record.Pos, 3);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PedFrameTrack_ReturnsNonEmptyPedListsAcrossTheRecording()
    {
        var (path, duration, _) = RecordRun(steps: 40);
        try
        {
            var track = new PedFrameTrack(path);
            Assert.True(track.FrameCount > 0, "expected >=1 recorded PEDFRAME");

            var sawNonEmpty = false;
            for (var t = 0.0; t <= duration; t += 1.0)
            {
                if (track.PedsAt(t).Count > 0)
                {
                    sawNonEmpty = true;
                    break;
                }
            }

            Assert.True(sawNonEmpty, "expected at least one sampled time with a non-empty ped list");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Ped-smoothing fix (docs/LIVE-CITY-VISUALS-NOTES.md-adjacent task) -- PedsAtInterpolated is the replay
    // analog of Sim.LiveCity.PedInterpolator: a mid-frame query must land STRICTLY between the two recorded
    // frames bracketing it, not snap to the nearest one (PedsAt's old step-function behaviour).
    [Fact]
    public void PedFrameTrack_PedsAtInterpolated_LerpsStrictlyBetweenBracketingFrames()
    {
        var (path, _, _) = RecordRun(steps: 40);
        try
        {
            var track = new PedFrameTrack(path);
            Assert.True(track.FrameCount >= 2, "expected >=2 recorded PEDFRAMEs to bracket a mid-frame query");

            // Find a ped id present across two consecutive recorded frames whose position actually differs
            // (a still-paused ped's before/after positions would trivially "match" the endpoints too).
            var found = false;
            for (var i = 0; i < track.FrameCount - 1 && !found; i++)
            {
                var tA = i * 0.5; // RecordRun uses LiveCityConfig's default Dt=0.5s.
                var tB = (i + 1) * 0.5;
                var before = track.PedsAt(tA);
                var after = track.PedsAt(tB);

                foreach (var pb in before)
                {
                    var match = false;
                    (int Id, float X, float Y, float Z, byte Regime, string AnimTag) pa = default;
                    foreach (var candidate in after)
                    {
                        if (candidate.Id == pb.Id)
                        {
                            match = true;
                            pa = candidate;
                            break;
                        }
                    }

                    if (!match)
                    {
                        continue;
                    }

                    var dx = pa.X - pb.X;
                    var dy = pa.Y - pb.Y;
                    if (Math.Sqrt(dx * dx + dy * dy) < 0.01)
                    {
                        continue; // this ped didn't move between the two frames -- not a useful case.
                    }

                    var mid = track.PedsAtInterpolated((tA + tB) / 2.0);
                    (int Id, float X, float Y, float Z, byte Regime, string AnimTag)? midEntry = null;
                    foreach (var m in mid)
                    {
                        if (m.Id == pb.Id)
                        {
                            midEntry = m;
                            break;
                        }
                    }

                    Assert.True(midEntry.HasValue, $"ped {pb.Id} missing from the interpolated mid-frame result");

                    var loX = Math.Min(pb.X, pa.X);
                    var hiX = Math.Max(pb.X, pa.X);
                    var loY = Math.Min(pb.Y, pa.Y);
                    var hiY = Math.Max(pb.Y, pa.Y);

                    Assert.True(midEntry!.Value.X >= loX && midEntry.Value.X <= hiX,
                        $"ped {pb.Id}: mid X={midEntry.Value.X} not within [{loX},{hiX}] (before X={pb.X}, after X={pa.X})");
                    Assert.True(midEntry.Value.Y >= loY && midEntry.Value.Y <= hiY,
                        $"ped {pb.Id}: mid Y={midEntry.Value.Y} not within [{loY},{hiY}] (before Y={pb.Y}, after Y={pa.Y})");

                    // At the exact midpoint fraction (f=0.5) the interpolated value should be the arithmetic
                    // mean, up to float roundtrip -- a stronger check than just "inside the bracket".
                    Assert.Equal((pb.X + pa.X) / 2.0, midEntry.Value.X, 3);
                    Assert.Equal((pb.Y + pa.Y) / 2.0, midEntry.Value.Y, 3);

                    found = true;
                    break;
                }
            }

            Assert.True(found, "expected at least one moving ped across two consecutive recorded frames");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
