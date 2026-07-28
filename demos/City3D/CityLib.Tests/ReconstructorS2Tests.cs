using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using CityLib;
using Sim.Core;
using Sim.Viewer.Motion;
using Xunit;
using Xunit.Abstractions;

namespace CityLib.Tests;

// VIEWER-KINEMATIC-SMOOTHING S2 / T2.2 success conditions (docs/VIEWER-KINEMATIC-SMOOTHING-TASKS.md). These
// assert the behaviour that changed when City3D's Reconstructor dropped DrPoseSmoother for the shared
// KinematicReconstructor facade:
//   * the drawn box now pivots on the vehicle CENTER (half a length behind the front reference) -- the pivot
//     fix (was drawn ~half a length too far forward);
//   * a lane-change straddle is reconstructed CONTINUOUSLY -- before S2 `Reconstructor` did `if
//     (resolved.IsLateralStraddle) continue;`, so a lane-changing vehicle VANISHED for those frames and had no
//     lateral ease at all;
//   * a junction turn follows the connecting-lane arc;
//   * poses advance without back-jumps; a stopped vehicle does not creep;
//   * the reconstruction is deterministic (a fixed packet stream + fixed frame-dt schedule -> identical
//     ReconstructedVehicle transforms across two runs).
//
// Smoothness/arc/ease/stop cases use the PRODUCTION wall-clock frame loop (Thread.Sleep, like
// ReconstructionTests) so DrClock produces the smooth render-rate front a live viewer sees. Determinism uses
// the fixed-dt override (Reconstruct's frameDtOverride), which additionally pins the DrClock query instant to
// the packet stream (ResolveAt) so the pass is a pure function with no wall clock to diverge.
public class ReconstructorS2Tests
{
    private readonly ITestOutputHelper _output;
    public ReconstructorS2Tests(ITestOutputHelper output) => _output = output;

    private const int FramesPerTick = 3;

    // Every scenario these tests drive has `step-length = 1` in its sumocfg, so ONE `sim.Tick()` advances the
    // sim by a whole second. The wall-clock frame loop below therefore has to sleep dt/FramesPerTick to be
    // what this class's header claims it is -- a REAL-TIME render loop.
    //
    // It used to sleep a hardcoded 15 ms, which drove the sim at ~22x real time, and that is not a detail:
    // `DrClock` advances its render clock at WALL rate scaled by a fitted wall<->sim rate, and caps catch-up
    // at `frameDt * simRate * 3` (a deliberate anti-jump guard, DrClock.cs:255) with `frameDt` clamped to
    // 0.1 s. At 15 ms/frame that cap is 0.045 sim-s per frame against a feed of 0.333, so the render clock
    // fell ~1 s behind the sample stream and never recovered inside a test's span. Measured consequence: the
    // query instant sat ~0.95 s in the past, i.e. where the car was still approaching the stop line -- the
    // stopped-pivot distance read 6.57 m instead of L/2 = 2.50, and the junction center read 1.01 m off the
    // centreline instead of 0.13. Both are pure clock lag; the reconstructor was right the whole time.
    private const double SimStepSeconds = 1.0;
    private const int FrameMillis = (int)(SimStepSeconds * 1000 / FramesPerTick);

    private const float FixedDt = 1f / 60f;
    private const double Delay = 0.4;

    // ---- pivot fix (facade property): the reconstructor returns a CENTER exactly half a length behind the
    // front reference it is fed. This is the property City3D's center-anchored box now relies on. ----
    [Fact]
    public void Facade_StraightVehicle_ReturnsCenterHalfLengthBehindFront()
    {
        var recon = new KinematicReconstructor { CoarseFeed = true };
        var handle = new VehicleHandle(7, 1);
        (double Length, double Width) dims = (4.5, 1.8);
        const double speed = 12.0;

        // Front advances due east (navi 90 deg => +X in SUMO). Drive well past the position/heading smoothing
        // time constants (0.6 s) so front and center are at steady state.
        double fx = 0, fy = 0;
        var r = default(KinematicReconResult);
        for (var i = 0; i < 240; i++)
        {
            r = recon.ResolveFromFront(handle, fx, fy, 90f, speed, dims, FixedDt, lateralEvent: false, predictHeadingDeg: null);
            fx += speed * FixedDt;
        }

        var dx = r.FrontX - r.CenterX;
        var dy = r.FrontY - r.CenterY;
        var dist = Math.Sqrt(dx * dx + dy * dy);

        Assert.True(Math.Abs(dist - dims.Length / 2) < 0.1,
            $"center should sit ~half a length ({dims.Length / 2:F2} m) behind the front, was {dist:F3} m");
        // due east: center is WEST of (behind) the front, and there is no lateral (y) offset on a straight road.
        Assert.True(r.FrontX > r.CenterX, $"center must be behind (west of) the front: frontX={r.FrontX:F3} centerX={r.CenterX:F3}");
        Assert.True(Math.Abs(dy) < 0.05, $"straight road: no lateral center offset expected, got {dy:F3} m");
        Assert.InRange(r.HeadingDeg, 89f, 91f);
    }

    // ---- pivot fix (end-to-end through CityLib.Reconstructor): at a red-light STOP (no playout-delay lag, no
    // extrapolation) the reconstructed CENTER sits ~half a length behind the SUMO snapshot FRONT (getPosition =
    // front bumper). If the pipeline still fed the front, this distance would be ~0; it is ~L/2. ----
    [Fact]
    public void Reconstructor_StoppedVehicle_CenterIsHalfLengthBehindSnapshotFront()
    {
        var (net, rou, cfg) = Paths("09-traffic-light");
        using var sim = new SimSource(net, rou, cfg);
        var recon = new Reconstructor(SumoGodotFrame.Identity);

        var behind = new List<double>();
        double length = 0;

        // 31 ticks: veh0 reaches the red at sim t ~26 and is released at ~30, so this covers the stop with
        // margin. Kept as tight as the assertion allows because the loop is now real-time (see FrameMillis).
        for (var t = 0; t < 31; t++)
        {
            sim.Tick();
            for (var f = 0; f < FramesPerTick; f++)
            {
                Thread.Sleep(FrameMillis);
                var poses = recon.Reconstruct(sim.Source, sim.LocalLanes, Delay);
                var snap = sim.Snapshot;
                foreach (var v in poses)
                {
                    if (v.Handle.Index != 0 || v.Speed > 0.05f)
                    {
                        continue; // veh0 only, and only while genuinely stopped
                    }

                    var si = Array.IndexOf(snap.Handles, v.Handle);
                    if (si < 0)
                    {
                        continue;
                    }

                    // ...and only while the SNAPSHOT is stopped too. The filter above is on the RECONSTRUCTED
                    // speed, which is a pose from `Delay` seconds ago, while the front below is read from the
                    // LIVE snapshot -- so on the frames right after the light turns green the car has already
                    // pulled away in the snapshot while the reconstruction still shows it at rest. Those
                    // frames measure the playout delay, not the pivot, and they dragged the max from 2.65 m
                    // to 4.83 m. Comparing two instants requires both of them to satisfy the premise.
                    if (snap.Speed[si] >= 0.05)
                    {
                        continue;
                    }

                    length = v.Length;
                    var fxS = snap.PosX[si];
                    var fyS = snap.PosY[si];
                    var cxS = v.X;      // Godot X == SUMO x
                    var cyS = -v.Z;     // Godot Z == -SUMO y
                    behind.Add(Math.Sqrt((fxS - cxS) * (fxS - cxS) + (fyS - cyS) * (fyS - cyS)));
                }
            }
        }

        Assert.True(behind.Count >= 5, $"expected several stopped frames for veh0, got {behind.Count}");
        behind.Sort();
        var median = behind[behind.Count / 2];
        _output.WriteLine($"stopped center-behind-snapshot-front: median={median:F3} (L/2={length / 2:F3}) n={behind.Count}");
        Assert.True(Math.Abs(median - length / 2) < 0.3,
            $"stopped center should sit ~L/2 ({length / 2:F2} m) behind the front bumper, median was {median:F3} m " +
            "(≈0 would mean the front is still being fed -- the pivot bug)");
    }

    // ---- lane-change straddle is reconstructed CONTINUOUSLY (was skipped). 12-overtake: veh1 changes lane to
    // overtake. Pre-S2 the straddle frames hit `continue` -> veh1 disappeared and never eased across. ----
    [Fact]
    public void Reconstructor_LaneChangeStraddle_IsReconstructedContinuously_NotSkipped()
    {
        var (net, rou, cfg) = Paths("12-overtake");
        using var sim = new SimSource(net, rou, cfg);
        var recon = new Reconstructor(SumoGodotFrame.Identity);
        var classifyClock = new DrClock(); // independent, pure ResolveAt re-classification of straddle frames

        var target = new VehicleHandle(1, 1);
        int present = 0, firstFrame = -1, lastFrame = -1, straddleFrames = 0, frameIdx = 0;
        double minZ = double.MaxValue, maxZ = double.MinValue, maxBackJump = 0;
        float? prevX = null;

        // 40 ticks, trimmed for the same reason as the other wall-clock loops: one tick is now one second of
        // real time. This test already passed at the old 15 ms pacing -- its assertions (presence, no back
        // jump, lateral span) survive clock lag -- but it is paced correctly here too, so the whole class
        // exercises one honest render loop rather than two different ones.
        for (var t = 0; t < 40; t++)
        {
            sim.Tick();
            for (var f = 0; f < FramesPerTick; f++)
            {
                Thread.Sleep(FrameMillis);
                var poses = recon.Reconstruct(sim.Source, sim.LocalLanes, Delay);
                var sampleT = (sim.Source.LatestVehicleSampleTime ?? 0) - Delay;

                foreach (var v in poses)
                {
                    if (v.Handle != target)
                    {
                        continue;
                    }

                    present++;
                    if (firstFrame < 0) firstFrame = frameIdx;
                    lastFrame = frameIdx;
                    minZ = Math.Min(minZ, v.Z);
                    maxZ = Math.Max(maxZ, v.Z);

                    // overtake route runs due east: a backward jump is a decrease in X (continuity guard).
                    if (prevX is { } px)
                    {
                        var drop = px - v.X;
                        if (drop > maxBackJump) maxBackJump = drop;
                    }
                    prevX = v.X;

                    // Independently re-classify: does DrClock see a lateral straddle for this vehicle now? Pure
                    // function of (history, sampleT), so it flags exactly the frames that pre-S2 did `continue`.
                    if (sim.Source.History.TryGetValue(target, out var hist) && hist.Count > 0)
                    {
                        try
                        {
                            if (classifyClock.ResolveAt(hist, sampleT, sim.LocalLanes).IsLateralStraddle)
                            {
                                straddleFrames++;
                            }
                        }
                        catch (KeyNotFoundException) { }
                    }
                }
                frameIdx++;
            }
        }

        var span = lastFrame - firstFrame + 1;
        var zRange = maxZ - minZ;
        _output.WriteLine($"veh1: present={present}/{span} straddleFrames={straddleFrames} zRange={zRange:F3} maxBackJump={maxBackJump:F3}");

        // The scenario actually exercises the lateral-straddle path (the one pre-S2 skipped).
        Assert.True(straddleFrames > 0, "expected the overtake to produce at least one lateral-straddle frame");
        // The lane-changer is now present on EVERY frame of its active window -- pre-S2 it vanished on the
        // straddle frames (gap = span - present > 0). Gapless == not skipped.
        Assert.True(present == span, $"lane-changer skipped {span - present} frame(s) during its active window (straddle was dropped)");
        // The lane change is actually rendered laterally (~one lane width) and eased, not a hole in the stream.
        Assert.True(zRange > 2.5, $"expected ~a lane-width of lateral travel across the overtake, got zRange={zRange:F3} m");
        // Continuous: no backward jump along travel.
        Assert.True(maxBackJump < 0.5, $"backward jump of {maxBackJump:F3} m during the lane change (not continuous)");
    }

    // ---- junction turn follows the connecting-lane arc. 44-multilane-junction-turn: veh0 makes a ~90 deg
    // turn; the center tracks a lane centerline through the arc (bounded offset) with a smooth heading. ----
    [Fact]
    public void Reconstructor_JunctionTurn_FollowsConnectingLaneArc_Smoothly()
    {
        var (net, rou, cfg) = Paths("44-multilane-junction-turn");
        using var sim = new SimSource(net, rou, cfg);
        var recon = new Reconstructor(SumoGodotFrame.Identity);

        var target = new VehicleHandle(0, 1);
        int present = 0, firstFrame = -1, lastFrame = -1, frameIdx = 0, framesSinceSeen = 0;
        double netYaw = 0, maxOffSmooth = 0, maxLatStepSmooth = 0;
        double? prevYaw = null;
        (float X, float Z)? prevPos = null;

        // 36 ticks covers the whole turn (measured: >90 deg of net yaw by then). Trimmed because the loop is
        // real-time now -- one tick is one second of wall clock.
        for (var t = 0; t < 36; t++)
        {
            sim.Tick();
            for (var f = 0; f < FramesPerTick; f++)
            {
                Thread.Sleep(FrameMillis);
                var poses = recon.Reconstruct(sim.Source, sim.LocalLanes, Delay);
                foreach (var v in poses)
                {
                    if (v.Handle != target)
                    {
                        continue;
                    }

                    present++;
                    if (firstFrame < 0) firstFrame = frameIdx;
                    lastFrame = frameIdx;
                    framesSinceSeen++;

                    if (prevYaw is { } pyw)
                    {
                        var d = Math.Atan2(Math.Sin(v.YawRad - pyw), Math.Cos(v.YawRad - pyw));
                        netYaw += Math.Abs(d);
                    }
                    prevYaw = v.YawRad;

                    if (prevPos is { } pp)
                    {
                        var step = Math.Sqrt((v.X - pp.X) * (v.X - pp.X) + (v.Z - pp.Z) * (v.Z - pp.Z));
                        // Smooth frame = past warmup and not an init/extrapolation jump. Measure the arc-tracking
                        // there (a spawn/extrapolation jump is not what "follows the arc" is about).
                        if (framesSinceSeen > 8 && step < 0.8)
                        {
                            var off = MinDistToLaneCenterline(sim.Network, v.X, -v.Z);
                            if (off > maxOffSmooth) maxOffSmooth = off;

                            // per-frame lateral (perpendicular-to-heading) motion -- small == the body isn't
                            // sliding sideways through the turn.
                            var yaw = v.YawRad;
                            var fx = Math.Sin(-yaw);
                            var fz = -Math.Cos(-yaw);
                            var mx = v.X - pp.X;
                            var mz = v.Z - pp.Z;
                            var lat = Math.Abs(mx * (-fz) + mz * fx);
                            if (lat > maxLatStepSmooth) maxLatStepSmooth = lat;
                        }
                    }
                    prevPos = (v.X, v.Z);
                }
                frameIdx++;
            }
        }

        var span = lastFrame - firstFrame + 1;
        var netYawDeg = netYaw * 180 / Math.PI;
        _output.WriteLine($"veh0: present={present}/{span} netYawDeg={netYawDeg:F1} maxOffSmooth={maxOffSmooth:F3} maxLatStep={maxLatStepSmooth:F3}");

        Assert.True(present == span, $"turning vehicle skipped {span - present} frame(s) through the junction");
        Assert.True(netYawDeg > 90.0, $"expected a real junction turn (>90 deg of heading change), got {netYawDeg:F1} deg");
        // Center tracks a lane centerline through the whole arc within the design tolerance (the front hits the
        // connecting-lane centerline via the facade look-ahead; the center off-tracks a little inside the arc).
        Assert.True(maxOffSmooth < 0.6, $"center strayed {maxOffSmooth:F3} m off the nearest lane centerline through the turn (arc not followed)");
        // The turn is a rotation, not a sideways slide: per-frame lateral motion stays tiny.
        Assert.True(maxLatStepSmooth < 0.1, $"unexpected sideways slide through the turn: {maxLatStepSmooth:F3} m/frame");
    }

    // ---- a stopped vehicle does not creep: over consecutive genuinely-stopped frames the center barely moves
    // (a driving car covers ~0.2 m/frame; a creep bug would drift it forward continuously). ----
    [Fact]
    public void Reconstructor_StoppedVehicle_DoesNotCreep()
    {
        var (net, rou, cfg) = Paths("09-traffic-light");
        using var sim = new SimSource(net, rou, cfg);
        var recon = new Reconstructor(SumoGodotFrame.Identity);

        var target = new VehicleHandle(0, 1);
        (float X, float Z)? prev = null;
        var prevStopped = false;
        var creepSpeeds = new List<double>();

        // The frame duration is MEASURED, not assumed: `Thread.Sleep(333)` overshoots on a loaded box, and
        // dividing by the nominal 0.333 s would then overstate the speed by however much it overshot -- a
        // flakiness source built into the metric. A Stopwatch costs nothing here and removes it.
        var frameWall = Stopwatch.StartNew();

        // The creep metric is a SPEED, not a per-frame distance. It used to be `max per-frame metres < 0.12`,
        // with the threshold reasoned from a 60 Hz loop ("~0.2 m at 13 m/s / 60 Hz") -- so the bound was
        // frame-rate dependent while being written as an absolute. That only held at the old (wrong) 15 ms
        // pacing by coincidence: at the correct dt/3 = 333 ms/frame the SAME physical hold covers 20x the
        // distance per frame, and the test failed at 0.61 m/frame while the body was in fact settling at
        // centimetres per second. Dividing by the frame's own duration makes the assertion mean what its
        // comment always said, at any frame rate.

        for (var t = 0; t < 31; t++)
        {
            sim.Tick();
            for (var f = 0; f < FramesPerTick; f++)
            {
                Thread.Sleep(FrameMillis);
                var frameSeconds = Math.Max(frameWall.Elapsed.TotalSeconds, 1e-3);
                frameWall.Restart();
                var snap = sim.Snapshot;
                foreach (var v in recon.Reconstruct(sim.Source, sim.LocalLanes, Delay))
                {
                    if (v.Handle != target)
                    {
                        continue;
                    }

                    // Stopped means stopped in BOTH the reconstruction and the snapshot -- same reasoning as
                    // the pivot test's filter. The reconstructed speed alone admits the frames right after the
                    // light turns green, where the body is legitimately accelerating away and its "creep" is
                    // just motion (measured: 0.22 m/frame with this filter, 0.61 m/frame without it).
                    var si = Array.IndexOf(snap.Handles, v.Handle);
                    var stopped = v.Speed < 0.05f && si >= 0 && snap.Speed[si] < 0.05;

                    if (stopped && prevStopped && prev is { } pp)
                    {
                        var d = Math.Sqrt(((v.X - pp.X) * (v.X - pp.X)) + ((v.Z - pp.Z) * (v.Z - pp.Z)));
                        creepSpeeds.Add(d / frameSeconds);
                    }

                    prev = (v.X, v.Z);
                    prevStopped = stopped;
                }
            }
        }

        creepSpeeds.Sort();
        var maxCreep = creepSpeeds.Count > 0 ? creepSpeeds[^1] : 0.0;
        var medianCreep = creepSpeeds.Count > 0 ? creepSpeeds[creepSpeeds.Count / 2] : 0.0;
        _output.WriteLine(
            $"stopped pairs={creepSpeeds.Count} creep max={maxCreep:F4} m/s median={medianCreep:F4} m/s "
            + $"(frames measured, nominal {FrameMillis} ms)");

        Assert.True(creepSpeeds.Count >= 5,
            $"expected the vehicle to sit stopped at the red for several frames, got {creepSpeeds.Count}");

        // Two bounds, because they catch different things. A driving car here does ~13 m/s, so a body that
        // "holds at the stop" (KinematicHeading's HoldSpeed) must be an order of magnitude below that even on
        // its worst frame -- that is the original bug this test was written for. The MEDIAN pins the steady
        // state separately: a single settle frame as the smoother's 0.6 s time constant converges onto the
        // stop is not creep, but sustained motion would be, and only the median can tell them apart.
        Assert.True(maxCreep < 1.5,
            $"vehicle crept at {maxCreep:F3} m/s while stopped (expected it to hold; a driving car is ~13 m/s)");
        Assert.True(medianCreep < 0.2,
            $"vehicle creeps CONTINUOUSLY at {medianCreep:F3} m/s while stopped -- a settle frame is fine, a "
            + "steady drift is not");
    }

    // ---- determinism: the same scripted packet stream + fixed frame-dt schedule yields identical
    // ReconstructedVehicle transforms across two independent runs (no System.Random, per-entity state, no wall
    // clock in the fixed-dt path). ----
    [Fact]
    public void Reconstructor_FixedDt_IsDeterministic_AcrossTwoRuns()
    {
        var runA = RunDeterministic("09-traffic-light", 48);
        var runB = RunDeterministic("09-traffic-light", 48);

        Assert.Equal(runA.Count, runB.Count);
        Assert.True(runA.Count > 20, $"expected a meaningful number of frames, got {runA.Count}");

        for (var i = 0; i < runA.Count; i++)
        {
            var a = runA[i];
            var b = runB[i];
            Assert.Equal(a.Count, b.Count); // same vehicles, same order, every frame
            for (var j = 0; j < a.Count; j++)
            {
                Assert.Equal(a[j].Handle, b[j].Handle);
                // Exact equality: identical inputs through identical deterministic code must be bit-for-bit equal.
                Assert.Equal(a[j].X, b[j].X);
                Assert.Equal(a[j].Y, b[j].Y);
                Assert.Equal(a[j].Z, b[j].Z);
                Assert.Equal(a[j].YawRad, b[j].YawRad);
                Assert.Equal(a[j].PitchRad, b[j].PitchRad);
                Assert.Equal(a[j].Length, b[j].Length);
                Assert.Equal(a[j].Width, b[j].Width);
                Assert.Equal(a[j].Speed, b[j].Speed);
            }
        }
    }

    // One deterministic run: fixed frame-dt (so DrClock's query instant is pinned to the packet stream via
    // ResolveAt, no Stopwatch), copying each frame's vehicles out of the reused scratch buffer.
    private static List<List<ReconstructedVehicle>> RunDeterministic(string scenario, int ticks)
    {
        var (net, rou, cfg) = Paths(scenario);
        using var sim = new SimSource(net, rou, cfg);
        var recon = new Reconstructor(SumoGodotFrame.Identity);
        var frames = new List<List<ReconstructedVehicle>>();

        for (var t = 0; t < ticks; t++)
        {
            sim.Tick();
            for (var f = 0; f < FramesPerTick; f++)
            {
                var poses = recon.Reconstruct(sim.Source, sim.LocalLanes, Delay, FixedDt);
                frames.Add(poses.ToList());
            }
        }

        return frames;
    }

    // Min distance from a SUMO-space point to any published lane's centerline polyline.
    private static double MinDistToLaneCenterline(Sim.Ingest.NetworkModel net, double x, double y)
    {
        var best = double.MaxValue;
        foreach (var lane in net.LanesByHandle)
        {
            var shape = lane.Shape;
            for (var i = 0; i + 1 < shape.Count; i++)
            {
                var d = DistPointSeg(x, y, shape[i].Item1, shape[i].Item2, shape[i + 1].Item1, shape[i + 1].Item2);
                if (d < best) best = d;
            }
        }

        return best;
    }

    private static double DistPointSeg(double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-12)
        {
            return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        }

        var t = ((px - ax) * dx + (py - ay) * dy) / len2;
        t = Math.Max(0, Math.Min(1, t));
        var cx = ax + t * dx;
        var cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    private static (string Net, string Rou, string Cfg) Paths(string scenario)
    {
        var d = Path.Combine(RepoRoot(), "scenarios", scenario);
        return (Path.Combine(d, "net.net.xml"), Path.Combine(d, "rou.rou.xml"), Path.Combine(d, "config.sumocfg"));
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
