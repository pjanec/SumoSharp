using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CityLib;
using Sim.Core;
using Xunit;
using Xunit.Abstractions;

namespace CityLib.Tests;

// T1.2 success conditions (docs/DEMO-CITY3D-TASKS.md), driven over scenarios/09-traffic-light through
// SimSource and reconstructed via the same recipe as samples/MotionReconstruction/Program.cs: a real
// wall-clock frame loop (Thread.Sleep), ~modest Hz, a small fixed playout delay. veh0 drives WJ->JE, a
// dead-straight east-west route (net.net.xml: WJ_0 (0,-1.6)->(300,-1.6), JE_0 (300,-1.6)->(600,-1.6)), so
// its navi-heading is a CONSTANT 90 degrees (due east) for the whole run -- a convenient, unambiguous
// ground truth for the coordinate-transform / yaw check (condition 4).
public class ReconstructionTests
{
    private readonly ITestOutputHelper _output;

    public ReconstructionTests(ITestOutputHelper output) => _output = output;

    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "09-traffic-light");

    // step-length=1s in config.sumocfg, end=50 -- 40 ticks leaves margin. A handful of render frames per
    // sim tick + a real (short) sleep between frames gives DrClock's wall<->sim rate fit real elapsed time
    // to work with, without the test run taking many seconds.
    private const int SimTicks = 40;
    private const int FramesPerTick = 3;
    private const int FrameMillis = 30;
    private const double PlayoutDelay = 0.4;

    // ---- condition 1: monotonic along-travel advance, no back-jump > 0.5m, stays in net bounds ----
    [Fact]
    public void TrackedVehicle_AdvancesMonotonically_NoBackJumpOverHalfMetre_AndStaysInNetBounds()
    {
        var (netPath, rouPath, cfgPath) = ScenarioPaths();
        using var sim = new SimSource(netPath, rouPath, cfgPath);
        var reconstructor = new Reconstructor();
        var bbox = ComputeGodotBBox(sim.Network);

        VehicleHandle? tracked = null;
        var xs = new List<float>();
        var log = new List<string>();

        for (var tick = 0; tick < SimTicks; tick++)
        {
            sim.Tick();

            for (var f = 0; f < FramesPerTick; f++)
            {
                Thread.Sleep(FrameMillis);
                var poses = reconstructor.Reconstruct(sim.Source, sim.LocalLanes, PlayoutDelay);

                foreach (var v in poses)
                {
                    tracked ??= v.Handle;
                    if (v.Handle != tracked)
                    {
                        continue;
                    }

                    AssertSanePose(v, bbox);

                    if (xs.Count > 0)
                    {
                        // The route runs due east (+X in Godot, per the transform) the whole way, so along-
                        // travel progress IS X here. A "backward jump" is a decrease in X.
                        var drop = xs[^1] - v.X;
                        Assert.True(
                            drop <= 0.5f,
                            $"backward jump of {drop:F3} m at tick {tick} frame {f} (prev X={xs[^1]:F3}, now X={v.X:F3})");
                    }

                    xs.Add(v.X);
                    log.Add(
                        $"tick={tick,2} frame={f} simTime={sim.Time,5:F1} X={v.X,7:F3} Y={v.Y,6:F3} Z={v.Z,6:F3} " +
                        $"yawRad={v.YawRad,6:F3} speed={v.Speed,5:F2}");
                }
            }
        }

        Assert.True(tracked.HasValue, "no vehicle ever appeared in the reconstruction");
        Assert.True(xs.Count > 10, $"expected a meaningful number of reconstructed frames, got {xs.Count}");

        // Net forward progress across the whole run (the vehicle brakes to a red-light stop then departs
        // at green -- individual frames may sit still, but the run as a whole must move the car forward).
        Assert.True(xs[^1] - xs[0] > 1.0f, $"expected net forward progress > 1m over the run, got {xs[^1] - xs[0]:F3}m");

        _output.WriteLine($"tracked vehicle: {tracked}, {xs.Count} reconstructed frames logged:");
        foreach (var line in log)
        {
            _output.WriteLine(line);
        }
    }

    // ---- condition 3: local<->remote seam -- same reconstruction code, either ILaneShapeSource ----
    [Fact]
    public void Reconstruction_ProducesSanePoses_ForBothLocalAndWireLaneSources()
    {
        var (netPath, rouPath, cfgPath) = ScenarioPaths();
        using var sim = new SimSource(netPath, rouPath, cfgPath);

        for (var tick = 0; tick < 5; tick++)
        {
            sim.Tick();
            Thread.Sleep(FrameMillis);
        }

        var bbox = ComputeGodotBBox(sim.Network);

        // Two independently-created Reconstructor instances (each with its own DrClock/DrPoseSmoother, so
        // neither has any prior-frame state to smooth from -- a clean first-observation comparison), one
        // fed the LOCAL Z-aware NetworkLaneSource, one fed the wire-shaped ReplicationLaneShapeSource built
        // from the SAME bus.Source.Geometry -- exactly the local<->remote seam (design "SimSource").
        var localReconstructor = new Reconstructor();
        var wireReconstructor = new Reconstructor();
        var wireLanes = new ReplicationLaneShapeSource(sim.Source.Geometry);

        var localPoses = localReconstructor.Reconstruct(sim.Source, sim.LocalLanes, PlayoutDelay);
        var wirePoses = wireReconstructor.Reconstruct(sim.Source, wireLanes, PlayoutDelay);

        Assert.NotEmpty(localPoses);
        Assert.NotEmpty(wirePoses);
        Assert.Equal(localPoses.Count, wirePoses.Count);

        var localByHandle = new Dictionary<VehicleHandle, ReconstructedVehicle>();
        foreach (var v in localPoses)
        {
            AssertSanePose(v, bbox);
            localByHandle[v.Handle] = v;
        }

        foreach (var w in wirePoses)
        {
            AssertSanePose(w, bbox);
            Assert.True(localByHandle.TryGetValue(w.Handle, out var l), $"handle {w.Handle} missing from local reconstruction");

            // Same underlying samples + same reconstruction CODE, only the ILaneShapeSource implementation
            // differs (local Z-aware vs wire-fed 2-D-only). 09-traffic-light is flat, so world position
            // (X, Z) and elevation (Y) should agree closely -- this is exactly what proves the seam is a
            // pure source swap, not a code fork.
            Assert.True(Math.Abs(l.X - w.X) < 0.05f, $"X differs: local={l.X} wire={w.X}");
            Assert.True(Math.Abs(l.Z - w.Z) < 0.05f, $"Z differs: local={l.Z} wire={w.Z}");
            Assert.True(Math.Abs(l.Y - w.Y) < 0.05f, $"Y (elevation) differs: local={l.Y} wire={w.Y}");
        }
    }

    // ---- condition 4: coordinate transform + navi->yaw conversion, exercised against real sim state ----
    [Fact]
    public void ReconstructedYaw_MatchesSimHeading_WithinOneDegree_AndPositionsAreInBounds()
    {
        var (netPath, rouPath, cfgPath) = ScenarioPaths();
        using var sim = new SimSource(netPath, rouPath, cfgPath);
        var reconstructor = new Reconstructor();
        var bbox = ComputeGodotBBox(sim.Network);

        var checkedFrames = 0;

        for (var tick = 0; tick < 20; tick++)
        {
            sim.Tick();
            Thread.Sleep(FrameMillis);
            var poses = reconstructor.Reconstruct(sim.Source, sim.LocalLanes, PlayoutDelay);
            var snap = sim.Snapshot;

            foreach (var v in poses)
            {
                AssertSanePose(v, bbox);

                var snapIndex = Array.IndexOf(snap.Handles, v.Handle);
                if (snapIndex < 0)
                {
                    continue; // vehicle already advanced past this snapshot (despawned) -- nothing to compare
                }

                var expectedYawRad = CoordinateTransform.NaviDegToGodotYawRad(snap.Angle[snapIndex]);
                var diffDeg = AngleDiffDeg(expectedYawRad, v.YawRad);
                Assert.True(
                    diffDeg < 1.0f,
                    $"tick {tick}: reconstructed yaw {v.YawRad:F4} rad vs sim heading {snap.Angle[snapIndex]:F2} deg " +
                    $"(expected yaw {expectedYawRad:F4} rad) -- diff {diffDeg:F3} deg");
                checkedFrames++;
            }
        }

        Assert.True(checkedFrames > 10, $"expected a meaningful number of checked frames, got {checkedFrames}");
    }

    private static (string Net, string Rou, string Cfg) ScenarioPaths() => (
        Path.Combine(ScenarioDir, "net.net.xml"),
        Path.Combine(ScenarioDir, "rou.rou.xml"),
        Path.Combine(ScenarioDir, "config.sumocfg"));

    private static void AssertSanePose(ReconstructedVehicle v, (float MinX, float MaxX, float MinZ, float MaxZ) bbox)
    {
        Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z), $"non-finite position: {v.X},{v.Y},{v.Z}");
        Assert.True(float.IsFinite(v.YawRad) && float.IsFinite(v.PitchRad), $"non-finite orientation: yaw={v.YawRad} pitch={v.PitchRad}");
        Assert.True(v.Length > 0f && v.Width > 0f, $"non-positive dims: length={v.Length} width={v.Width}");

        // Generous margin (the smoother's capped catch-up + extrapolation can overshoot slightly beyond the
        // raw resolved arc position, and the vehicle has physical width) -- still a tight, meaningful bound
        // against the ~600m-long net.
        const float margin = 5f;
        Assert.InRange(v.X, bbox.MinX - margin, bbox.MaxX + margin);
        Assert.InRange(v.Z, bbox.MinZ - margin, bbox.MaxZ + margin);
    }

    // Union of every published lane's endpoints, transformed to Godot space -- the net's bounding box "in
    // Godot space" the T1.2 conditions ask positions to stay inside.
    private static (float MinX, float MaxX, float MinZ, float MaxZ) ComputeGodotBBox(Sim.Ingest.NetworkModel network)
    {
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;

        foreach (var lane in network.LanesByHandle)
        {
            foreach (var (x, y) in lane.Shape)
            {
                var (gx, _, gz) = CoordinateTransform.SumoToGodot(x, y, 0.0);
                if (gx < minX) minX = gx;
                if (gx > maxX) maxX = gx;
                if (gz < minZ) minZ = gz;
                if (gz > maxZ) maxZ = gz;
            }
        }

        return (minX, maxX, minZ, maxZ);
    }

    private static float AngleDiffDeg(float aRad, float bRad)
    {
        var diffRad = MathF.Atan2(MathF.Sin(aRad - bRad), MathF.Cos(aRad - bRad));
        return MathF.Abs(diffRad) * 180f / MathF.PI;
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
