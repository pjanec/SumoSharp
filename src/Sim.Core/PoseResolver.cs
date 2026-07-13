using Sim.Ingest;

namespace Sim.Core;

// SUMOSHARP-DEADRECKONING.md §6 — the portable pose-resolver. Turns a mover's dead-reckoning state (§3)
// + a render `dt` into a world pose, by integrating along the (static, once-sent) lane geometry. It is the
// ONE piece of pose math shared by (a) the engine's opt-in production render mode, (b) a local renderer,
// and (c) a networked replication client — deliberately dependency-light and deterministic so it can be
// mirrored verbatim in JS/C++. Purely output-side: it never touches simulation state or the parity path.
//
// Realism tiers (§6.2): ParityTangent = the lane tangent at the front (what the parity Angle column uses);
// ChordHeading = SUMO's own back->front chord (MSVehicle::computeAngle), correct for long vehicles on
// curves; CornerCutCorrected = ChordHeading + (Tier B, added next) swept-path off-tracking. All are
// renderer-only and derived purely from lane geometry + physical dims, so they cost zero extra wire data.

// A resolved world pose. Position is the vehicle's front reference (matching SUMO getPosition); heading is
// navi-degrees (0 = north, clockwise), matching LaneGeometry / SUMO FCD.
public readonly struct Pose
{
    public Pose(double x, double y, double z, float headingDeg)
    {
        X = x; Y = y; Z = z; HeadingDeg = headingDeg;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }
    public float HeadingDeg { get; }
}

public enum RenderRealism : byte
{
    ParityTangent = 0,   // lane tangent at the front point (byte-identical to the parity Angle column)
    ChordHeading = 1,    // back->front chord (SUMO computeAngle); correct heading for long veh on curves
    CornerCutCorrected = 2, // ChordHeading + swept-path off-tracking ("trucks swing wide") -- Tier B
}

// The dead-reckoning state of one mover (the non-span part of the §4.2 packet). Span-typed lane paths are
// passed separately to Resolve so this stays a plain (copyable, packet-friendly) struct.
public readonly struct DrState
{
    public DrModel Model { get; init; }

    // LaneArc payload.
    public int LaneHandle { get; init; }
    public double Pos { get; init; }
    public double PosLat { get; init; }
    public double Speed { get; init; }
    public double Accel { get; init; }
    public double LatSpeed { get; init; }

    // FreeKinematic payload.
    public double WorldX { get; init; }
    public double WorldY { get; init; }
    public double WorldZ { get; init; }
    public double Vx { get; init; }
    public double Vy { get; init; }

    // Physical dims (sent once per vType/handle); used for the chord/off-tracking heading.
    public double Length { get; init; }
    public double Width { get; init; }
}

// Read-only lane geometry by dense lane handle -- the once-sent static network a resolver walks. The engine
// (via NetworkModel) and a network client (which parses the same net) both supply one; see NetworkLaneSource.
public interface ILaneShapeSource
{
    double LaneLength(int laneHandle);
    IReadOnlyList<(double X, double Y)> LaneShape(int laneHandle);
    IReadOnlyList<double>? LaneShapeZ(int laneHandle);
}

public static class PoseResolver
{
    // Resolve the world pose at render time `dt` seconds after the state was sampled.
    // `upcomingLanes` = current lane first, then the lanes ahead (Engine.GetUpcomingLanes).
    // `precedingLanes` = the lane immediately behind first, then further back (for the chord/off-track back
    // point); may be empty (the back point then clamps to the current lane start).
    public static Pose Resolve(
        ILaneShapeSource lanes,
        in DrState s,
        ReadOnlySpan<int> upcomingLanes,
        ReadOnlySpan<int> precedingLanes,
        double dt,
        RenderRealism realism)
    {
        if (s.Model == DrModel.FreeKinematic)
        {
            var fx = s.WorldX + s.Vx * dt;
            var fy = s.WorldY + s.Vy * dt;
            // Heading from the velocity vector; hold still-agents at due-north (0) rather than NaN.
            var speed2 = s.Vx * s.Vx + s.Vy * s.Vy;
            var heading = speed2 > 1e-12 ? NaviFromVector(s.Vx, s.Vy) : 0f;
            return new Pose(fx, fy, s.WorldZ, heading);
        }

        // LaneArc / Stationary: integrate arc-length forward (Stationary => dt contributes nothing because
        // speed/accel are ~0, so the same path works and no special-case is needed).
        var delta = s.Speed * dt + 0.5 * s.Accel * dt * dt;
        if (delta < 0.0)
        {
            delta = 0.0; // never predict backwards on a hard decel; reconciliation corrects on the next packet
        }

        var posPred = s.Pos + delta;
        var posLatPred = s.PosLat + s.LatSpeed * dt;

        // Front reference point (+ its lane tangent), walking forward from the current lane.
        SampleForward(lanes, upcomingLanes, posPred, posLatPred, out var front, out var frontTangent);

        if (realism == RenderRealism.ParityTangent)
        {
            return new Pose(front.X, front.Y, front.Z, frontTangent);
        }

        // ChordHeading (and the base of CornerCutCorrected): heading = back->front chord, where the back
        // point is `Length` behind the front along the actual geometry (into preceding lanes if needed).
        var backArc = posPred - s.Length;
        Vec3 back;
        if (backArc >= 0.0)
        {
            SampleForward(lanes, upcomingLanes, backArc, posLatPred, out back, out _);
        }
        else
        {
            SampleBackward(lanes, upcomingLanes, precedingLanes, -backArc, posLatPred, out back);
        }

        var dx = front.X - back.X;
        var dy = front.Y - back.Y;
        var chord = (dx * dx + dy * dy) > 1e-12 ? NaviFromVector(dx, dy) : frontTangent;

        // Tier B (CornerCutCorrected off-tracking) is added in the next increment; for now it renders as
        // ChordHeading (a strict improvement over tangent, and its heading base).
        return new Pose(front.X, front.Y, front.Z, chord);
    }

    private readonly struct Vec3
    {
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
    }

    // Walk forward from the current lane (upcomingLanes[0]) by `arc` metres; sample the point + tangent.
    private static void SampleForward(
        ILaneShapeSource lanes, ReadOnlySpan<int> path, double arc, double latOffset,
        out Vec3 point, out float tangentDeg)
    {
        if (path.IsEmpty)
        {
            point = default;
            tangentDeg = 0f;
            return;
        }

        var remaining = arc < 0.0 ? 0.0 : arc;
        for (var i = 0; i < path.Length; i++)
        {
            var h = path[i];
            var len = lanes.LaneLength(h);
            if (remaining <= len || i == path.Length - 1)
            {
                Sample(lanes, h, remaining, latOffset, out point, out tangentDeg);
                return;
            }

            remaining -= len;
        }

        Sample(lanes, path[^1], remaining, latOffset, out point, out tangentDeg);
    }

    // Walk `dist` metres BEHIND the current lane's start, into precedingLanes (nearest-behind first).
    private static void SampleBackward(
        ILaneShapeSource lanes, ReadOnlySpan<int> upcoming, ReadOnlySpan<int> preceding,
        double dist, double latOffset, out Vec3 point)
    {
        if (preceding.IsEmpty)
        {
            // No known lane behind: clamp to the current lane's start.
            var cur = upcoming.IsEmpty ? -1 : upcoming[0];
            if (cur < 0) { point = default; return; }
            Sample(lanes, cur, 0.0, latOffset, out point, out _);
            return;
        }

        var remaining = dist;
        for (var i = 0; i < preceding.Length; i++)
        {
            var h = preceding[i];
            var len = lanes.LaneLength(h);
            if (remaining <= len || i == preceding.Length - 1)
            {
                // `remaining` behind this lane's END == (len - remaining) from its start.
                var fromStart = len - remaining;
                if (fromStart < 0.0) fromStart = 0.0;
                Sample(lanes, h, fromStart, latOffset, out point, out _);
                return;
            }

            remaining -= len;
        }

        Sample(lanes, preceding[^1], 0.0, latOffset, out point, out _);
    }

    private static void Sample(
        ILaneShapeSource lanes, int laneHandle, double arcOnLane, double latOffset,
        out Vec3 point, out float tangentDeg)
    {
        var shape = lanes.LaneShape(laneHandle);
        var (x, y, deg) = LaneGeometry.PositionAtOffset(shape, arcOnLane, latOffset);
        var z = lanes.LaneShapeZ(laneHandle) is { } shapeZ
            ? LaneGeometry.ElevationAtOffset(shape, shapeZ, arcOnLane)
            : 0.0;
        point = new Vec3(x, y, z);
        tangentDeg = (float)deg;
    }

    // Navi-degree (0 = north, clockwise) of a direction vector -- the same convention LaneGeometry uses.
    private static float NaviFromVector(double dx, double dy)
    {
        var deg = 90.0 - Math.Atan2(dy, dx) * 180.0 / Math.PI;
        deg %= 360.0;
        if (deg < 0.0) deg += 360.0;
        return (float)deg;
    }
}
