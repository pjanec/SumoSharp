using System.Diagnostics;
using Sim.Core;
using Sim.Ingest;
using Sim.Replication;
using Sim.Viewer.Motion;

namespace CityLib;

// One vehicle's fully-reconstructed render pose, in GODOT coordinates/yaw (CoordinateTransform already
// applied) -- the plain struct a later Godot-glue layer turns into a MultiMesh per-instance transform. No
// Godot type here (CityLib stays engine-agnostic); see docs/DEMO-CITY3D-DESIGN.md "Cars".
public readonly struct ReconstructedVehicle
{
    public ReconstructedVehicle(
        VehicleHandle handle, float x, float y, float z, float yawRad, float pitchRad,
        float length, float width, float speed)
    {
        Handle = handle;
        X = x; Y = y; Z = z;
        YawRad = yawRad; PitchRad = pitchRad;
        Length = length; Width = width;
        Speed = speed;
    }

    public VehicleHandle Handle { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public float YawRad { get; }
    public float PitchRad { get; }
    public float Length { get; }
    public float Width { get; }
    public float Speed { get; }
}

// docs/DEMO-CITY3D-DESIGN.md "Data path" -- the per-render-frame reconstruction pipeline (samples'
// MotionReconstruction template, verbatim): one DrClock + one DrPoseSmoother shared across all vehicles
// (the smoother is per-handle INTERNALLY -- Sim.Viewer.Motion.DrPoseSmoother keys its chase state by
// VehicleHandle), driven purely off an IReplicationSource + an ILaneShapeSource. Neither type here is
// transport- or origin-specific, so the SAME Reconstructor instance/logic runs unchanged whether `lanes`
// is a local Sim.Core.NetworkLaneSource or a wire-fed ReplicationLaneShapeSource (T1.2 condition 3), and
// whether `source` is an in-process InMemoryReplicationBus.Source or (later) a DDS IReplicationSource.
public sealed class Reconstructor
{
    private readonly DrClock _clock = new();
    private readonly DrPoseSmoother _smoother = new();
    private readonly List<ReconstructedVehicle> _scratch = new();
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private double _lastWallSec = -1.0;

    // Call once per render frame. Mirrors the design's data-path recipe exactly:
    //   source.Pump() -> DrClock.Pump(newest) -> per vehicle: Resolve -> PoseResolver.Resolve -> Smooth
    // `delaySeconds` is the playout delay (design "Playout delay": a stable manual knob, ~0.3-0.5s).
    public IReadOnlyList<ReconstructedVehicle> Reconstruct(
        IReplicationSource source, ILaneShapeSource lanes, double delaySeconds)
    {
        source.Pump();
        _clock.Pump(source.LatestVehicleSampleTime);

        var nowWall = _wall.Elapsed.TotalSeconds;
        var frameDt = _lastWallSec >= 0.0 ? (float)(nowWall - _lastWallSec) : 0f;
        _lastWallSec = nowWall;

        _scratch.Clear();

        Span<int> upcomingBuf = stackalloc int[UpcomingLanes.Count];

        foreach (var kv in source.History)
        {
            var handle = kv.Key;
            var history = kv.Value;
            if (history.Count == 0 || !source.Dims.TryGetValue(handle, out var dims))
            {
                continue;
            }

            DrClock.Resolved resolved;
            try
            {
                resolved = _clock.Resolve(history, delaySeconds, lanes);
            }
            catch (KeyNotFoundException)
            {
                // Geometry for this vehicle's lane hasn't arrived on this source yet (only possible on a
                // remote/wire-fed lane source with real transport latency) -- skip this vehicle this frame
                // rather than throw out of the whole reconstruction pass.
                continue;
            }

            if (resolved.IsLateralStraddle)
            {
                // Sibling-lane (lane-change) blending is out of Stage-1 scope (design's "Data path" recipe
                // covers the single-lane-window case the demo scenarios exercise); skip defensively rather
                // than half-implement the two-state Cartesian lerp here.
                continue;
            }

            var state = resolved.State with { Length = dims.Length, Width = dims.Width };
            var n = resolved.Upcoming.CopyTo(upcomingBuf);
            if (n == 0)
            {
                upcomingBuf[0] = state.LaneHandle;
                n = 1;
            }

            var path = upcomingBuf[..n];
            Pose pose;
            try
            {
                pose = PoseResolver.Resolve(
                    lanes, state, path, ReadOnlySpan<int>.Empty, dt: 0.0, RenderRealism.ChordHeading);
            }
            catch (KeyNotFoundException)
            {
                continue;
            }

            var (sx, sy, sdeg) = _smoother.Smooth(handle, pose.X, pose.Y, pose.HeadingDeg, state.Speed, frameDt);

            var (gx, gy, gz) = CoordinateTransform.SumoToGodot(sx, sy, pose.Z);
            var yawRad = CoordinateTransform.NaviDegToGodotYawRad(sdeg);
            var pitchRad = ComputePitchRad(lanes, state, path);

            _scratch.Add(new ReconstructedVehicle(
                handle, gx, gy, gz, yawRad, pitchRad, dims.Length, dims.Width, (float)state.Speed));
        }

        return _scratch;
    }

    // docs/DEMO-CITY3D-DESIGN.md "Cars": "pitch = z-gradient along travel (tilt on ramps)". Approximated as
    // the elevation slope 1 m ahead of the vehicle's current arc position, walked along its own upcoming
    // lane window (so it correctly crosses into the next lane near an edge boundary); 0 when the lane
    // source carries no elevation at all (a 2-D net, or any wire-fed ILaneShapeSource -- LaneShapeZ is
    // always null on the wire today, see ReplicationLaneShapeSource).
    private static float ComputePitchRad(ILaneShapeSource lanes, DrState state, ReadOnlySpan<int> path)
    {
        if (path.IsEmpty || lanes.LaneShapeZ(state.LaneHandle) is null)
        {
            return 0f;
        }

        const double stepMetres = 1.0;
        var z0 = WalkElevation(lanes, path, state.Pos);
        var z1 = WalkElevation(lanes, path, state.Pos + stepMetres);
        return (float)Math.Atan2(z1 - z0, stepMetres);
    }

    // Walk `arc` metres forward along `path` (current lane first, mirrors PoseResolver's own SampleForward
    // walk) and sample elevation on whichever lane the walk lands on; 0 if that lane has no elevation data.
    private static double WalkElevation(ILaneShapeSource lanes, ReadOnlySpan<int> path, double arc)
    {
        var remaining = arc < 0.0 ? 0.0 : arc;
        for (var i = 0; i < path.Length; i++)
        {
            var h = path[i];
            var len = lanes.LaneLength(h);
            if (remaining <= len || i == path.Length - 1)
            {
                var shapeZ = lanes.LaneShapeZ(h);
                return shapeZ is null ? 0.0 : LaneGeometry.ElevationAtOffset(lanes.LaneShape(h), shapeZ, remaining);
            }

            remaining -= len;
        }

        return 0.0;
    }
}
