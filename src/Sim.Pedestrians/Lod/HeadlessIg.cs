using Sim.Core.Orca;

namespace Sim.Pedestrians.Lod;

// A headless "IG" (docs/PEDESTRIAN-POC-PLAN.md POC-3): consumes a PedPublisher's event stream and
// reconstructs each ped's pose with NO access to server-internal state -- only what has been Applied.
// This is the receiving half of the "server == IG for low-power" identity (docs/PEDESTRIAN-DESIGN.md
// §8): its PathArc branch calls the exact same PathArcMotion.PositionAt the server calls, so the two
// can only ever agree, never merely "usually match".
public sealed class HeadlessIg
{
    private sealed class PedState
    {
        public PedDrModel Model = PedDrModel.PathArc;
        public IReadOnlyList<Vec2>? Path;

        // C4/C5: the path's per-vertex elevation as it arrived on the wire (kind 5), or null for a
        // kind-4 (z-less) stream. Output-only, exactly like the server-side channel.
        public IReadOnlyList<double>? PathZ;
        public double PathStartTime;
        public double Speed;
        public Vec2 LastPos;
        public Vec2 LastVel;
        public double LastSampleTime;
        public ActivityTimeline? Timeline; // LIVE-POC-1: set once from the one-time ActivityTimelineRecord
    }

    private readonly Dictionary<int, PedState> _peds = new();

    // Feed one event (in wire order) into the IG's model of the world.
    public void Apply(PedEvent evt)
    {
        var state = GetOrCreate(evt.Id);
        switch (evt)
        {
            case PathArcRecord r:
                state.Path = r.Path;
                state.PathZ = r.PathZ;
                state.PathStartTime = r.StartTime;
                state.Speed = r.Speed;
                break;

            case ActivityTimelineRecord a:
                state.Timeline = a.Timeline;
                break;

            case DrSwitchEvent s:
                // Seed-on-switch (docs/LIVE-CITY-PED-LOD-LIFECYCLE-DESIGN.md §2): a promotion is delivered
                // as a lifecycle DrSwitchEvent (always sent), but the ped's FIRST FreeKinematicSample can
                // be absent from this batch (the publish scheduler under-sends a just-promoted ped). Without
                // a seed the FreeKinematic branch below would reconstruct from LastPos == default(Vec2) ==
                // (0,0) -- the ped snaps to the world origin (culled from the view => the reported "vanish")
                // until a real sample finally lands. So on switch TO FreeKinematic, seed the high-power pose
                // from the pose this ped is currently reconstructing under its (still low-power) model at the
                // switch time: an on-body, zero-velocity anchor that the first real sample overwrites
                // seamlessly (samples are applied AFTER lifecycle within a Drain, so a present first sample
                // still wins this same step). Reconstruct reads the pre-switch Model, so it must run BEFORE
                // the Model flip below.
                if (s.To == PedDrModel.FreeKinematic)
                {
                    state.LastPos = Reconstruct(evt.Id, s.Time);
                    state.LastVel = Vec2.Zero;
                    state.LastSampleTime = s.Time;
                }

                state.Model = s.To;
                break;

            case FreeKinematicSample f:
                state.LastPos = f.Pos;
                state.LastVel = f.Vel;
                state.LastSampleTime = f.Time;
                break;

            case HeartbeatEvent:
                break; // liveness only -- no pose information
        }
    }

    // Convenience for tests: feed a whole (ordered) event batch at once.
    public void ApplyAll(IEnumerable<PedEvent> events)
    {
        foreach (var evt in events)
        {
            Apply(evt);
        }
    }

    // Reconstructs id's world position at `now`, using ONLY what has been Applied so far.
    public Vec2 Reconstruct(int id, double now)
    {
        var state = _peds[id];
        return state.Model switch
        {
            PedDrModel.PathArc => state.Path is null
                ? Vec2.Zero
                : PathArcMotion.PositionAt(state.Path, state.PathStartTime, state.Speed, now),
            PedDrModel.FreeKinematic => state.LastPos + (state.LastVel * (now - state.LastSampleTime)),
            PedDrModel.Stationary => state.LastPos,
            PedDrModel.ActivityTimeline => state.Timeline is null ? Vec2.Zero : state.Timeline.PoseAt(now).Pos,
            _ => Vec2.Zero,
        };
    }

    // C5 (docs/EXTERNAL-NET-LOADING-DESIGN.md §3.6): the reconstructed SURFACE ELEVATION at `now`.
    //
    // For the PathArc model this is `PathArcMotion.SampleAt` walking the SAME arc length, on the SAME
    // segment, with the SAME fraction `t` that `Reconstruct` above uses for the position -- one shared
    // evaluator, so the reconstructed z and the reconstructed pos cannot disagree, and the remote
    // surface lands on the same number the in-process one does.
    //
    // A LIVELY (ActivityTimeline) ped is published as a timeline, never as a PathArc, so its elevation
    // rides the per-WalkSegment channel `ActivityTimelineWire` now carries (the follow-up to W1, which
    // had extended the PathArc record only and therefore left the lively population -- most of the
    // live-city scene -- flat on this surface). Resolved by projecting the reconstructed pose onto the
    // timeline's own walk geometry, the same way the server does for the same model.
    //
    // 0.0 whenever the stream carries no elevation -- a kind-4 publisher, or a 2-D net. Per §9.1 that is
    // deliberately indistinguishable from "genuinely at 0 m"; a consumer needing to tell them apart
    // checks the net.
    public double ReconstructElevation(int id, double now)
        => ReconstructElevationAt(id, now, Reconstruct(id, now));

    // C5·SC3: the same query, but evaluated AT a caller-supplied position -- the smoothed render
    // position, so a ped's height tracks the body actually drawn rather than the raw wire sample it is
    // still catching up to. For the PathArc model the arc-length answer is used unchanged (it is exact
    // and cheaper); for the dead-reckoned models the supplied position is what gets projected.
    public double ReconstructElevationAt(int id, double now, Vec2 at)
    {
        var state = _peds[id];

        // Ordered by which elevation source is the TRUTH for this ped, so a stale channel never wins:
        //
        // 1. A PathArc (routed) ped -> its own PathZ, walked by arc length. Highest precedence: a routed
        //    ped's own path is authoritative even if a timeline lingers from an earlier lively phase.
        if (state.Model == PedDrModel.PathArc
            && state.Path is { Count: > 0 } arcPath && state.PathZ is { Count: > 0 })
        {
            return PathArcMotion.ElevationAt(arcPath, state.PathZ, state.PathStartTime, state.Speed, now);
        }

        // 2. Any timeline-bearing ped that is NOT PathArc -- a lively ActivityTimeline ped, OR one promoted
        //    to high-power (FreeKinematic ORCA) which KEEPS its timeline (nothing clears it on promotion) --
        //    reads the surface off the timeline's per-leg channel, projecting the render pose onto its walk
        //    geometry. This is the fix for promoted lively peds that otherwise had no Path and rendered at
        //    z=0 (sunk far below an elevated net).
        if (state.Timeline is { } timeline)
        {
            return TimelineElevationAt(timeline, at);
        }

        // 3. FreeKinematic/Stationary promoted from a ROUTE (has a Path, no timeline): the pose is
        //    dead-reckoned off the polyline, so project the rendered position onto it.
        if (state.Path is { Count: > 0 } path && state.PathZ is { Count: > 0 })
        {
            return Sim.Pedestrians.Navigation.PolylineElevation.AtNearestPoint(path, state.PathZ, at);
        }

        // 4. No elevation source at all (kind-4 publisher / 2-D net): 0.0 per §9.1.
        return 0.0;
    }

    // Elevation along a timeline's Walk legs: pick the leg whose polyline the pose is nearest to, then
    // read the height off that leg's own channel. Walking the legs (rather than assuming the first) is
    // what keeps a multi-leg timeline -- a route split by kerb pauses, or a Walk-Pause-Walk trip --
    // correct at every point of it. Legs with no channel (a 2-D net) contribute nothing, so such a
    // timeline yields 0.0 exactly as before.
    private static double TimelineElevationAt(ActivityTimeline timeline, Vec2 at)
    {
        var best = double.PositiveInfinity;
        var bestZ = 0.0;

        foreach (var segment in timeline.Segments)
        {
            if (segment is not WalkSegment { Path.Count: > 0 } walk
                || walk.Elevations is not { Count: > 0 })
            {
                continue;
            }

            var d2 = NearestDistanceSquared(walk.Path, at);
            if (d2 < best)
            {
                best = d2;
                bestZ = Sim.Pedestrians.Navigation.PolylineElevation.AtNearestPoint(walk.Path, walk.Elevations, at);
            }
        }

        return bestZ;
    }

    private static double NearestDistanceSquared(IReadOnlyList<Vec2> shape, Vec2 p)
    {
        if (shape.Count == 1)
        {
            return (p - shape[0]).Abs * (p - shape[0]).Abs;
        }

        var best = double.PositiveInfinity;
        for (var i = 0; i < shape.Count - 1; i++)
        {
            var a = shape[i];
            var b = shape[i + 1];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len2 = (dx * dx) + (dy * dy);
            var t = len2 > 0.0
                ? Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / len2, 0.0, 1.0)
                : 0.0;
            var qx = a.X + (t * dx);
            var qy = a.Y + (t * dy);
            var d2 = ((p.X - qx) * (p.X - qx)) + ((p.Y - qy) * (p.Y - qy));
            if (d2 < best)
            {
                best = d2;
            }
        }

        return best;
    }

    // LIVE-POC-1: the ActivityTimeline model carries more than a position -- heading, animation tag,
    // and visibility -- so this is a PARALLEL method rather than a change to Reconstruct's signature
    // (per docs/PEDESTRIAN-LIVELINESS-DESIGN.md §12, keeping every existing Reconstruct call site
    // unchanged). For the ActivityTimeline model this calls the exact same ActivityTimeline.PoseAt the
    // server calls -- the server==IG identity extended to animation state. Other models fall back to a
    // minimal (position-only, always-visible, Idle) sample built from Reconstruct, so this stays total
    // over every PedDrModel.
    public PoseSample ReconstructSample(int id, double now)
    {
        var state = _peds[id];
        if (state.Model == PedDrModel.ActivityTimeline)
        {
            return state.Timeline is null
                ? new PoseSample(Vec2.Zero, Vec2.Zero, ActivityTimeline.IdleAnimTag, true)
                : state.Timeline.PoseAt(now);
        }

        return new PoseSample(Reconstruct(id, now), Vec2.Zero, ActivityTimeline.IdleAnimTag, true);
    }

    // The IG's current belief about id's DR model (asserting a promotion/demotion was actually
    // *observed* on the wire, not just true on the server).
    public PedDrModel ModelOf(int id) => _peds[id].Model;

    public bool Knows(int id) => _peds.ContainsKey(id);

    private PedState GetOrCreate(int id)
    {
        if (!_peds.TryGetValue(id, out var state))
        {
            state = new PedState();
            _peds[id] = state;
        }

        return state;
    }
}
