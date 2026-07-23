using System;
using System.Collections.Generic;

namespace Sim.LiveCity;

// docs/LIVE-CITY-VISUALS-NOTES.md-adjacent fix (ped smoothing task) -- pedestrians were drawn straight from
// LiveCitySim.Sample() (the raw sim snapshot, refreshed once per sim tick, default Dt=0.5s => 2 Hz) while
// the render loop runs at 60 fps, so a ped visibly held its position for ~0.5s then snapped to the next
// tick's -- a step function. Cars already avoid this via DrClock + KinematicReconstructor; peds need the
// analogous (much simpler -- no lanes, no heading) fix: a short history of {time, {id,x,y,regime,animTag}}
// snapshots, linearly interpolated BY ID between the two ticks bracketing the render query time. Shared by
// both viewers (Raylib's RunLiveCity/RunLiveCityReplay, City3D's ProcessLiveCity/ProcessLiveCityReplay) so
// the two renderers agree pixel-for-pixel on where a ped sits between ticks. Pure/deterministic: no
// Stopwatch, no System.Random -- the caller supplies both the tick's sim time (Push) and the render query
// instant (Sample), exactly mirroring DrClock.ResolveAt's "caller owns the clock" contract.
public readonly struct PedInterpFrame
{
    public PedInterpFrame(int id, double x, double y, PedRegime regime, string animTag)
    {
        Id = id;
        X = x;
        Y = y;
        Regime = regime;
        AnimTag = animTag;
    }

    public int Id { get; }
    public double X { get; }
    public double Y { get; }
    public PedRegime Regime { get; }
    public string AnimTag { get; }
}

public sealed class PedInterpolator
{
    private readonly struct Snapshot
    {
        public Snapshot(double time, IReadOnlyList<PedInterpFrame> peds)
        {
            Time = time;
            Peds = peds;
        }

        public double Time { get; }
        public IReadOnlyList<PedInterpFrame> Peds { get; }
    }

    // How many ticks of history to retain. Sample only ever needs the pair bracketing the query time, but a
    // caller's playout delay can lag more than one tick behind the newest sample (e.g. a car DR delay of
    // 0.5s against a 0.5s ped tick), so a few ticks of slack keeps the bracket search valid instead of
    // falling back to "hold oldest". Small ped counts (<a few hundred) make even a generous capacity cheap.
    private const int DefaultCapacity = 8;

    private readonly int _capacity;
    private readonly List<Snapshot> _history;

    public PedInterpolator(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(2, capacity);
        _history = new List<Snapshot>(_capacity);
    }

    // Beyond this same-id position delta between two bracketing snapshots, treat it as a genuine
    // discontinuity (a respawn / route restart) rather than motion, and snap to the later position instead
    // of lerping across the gap. Generous above any modeled ped speed (~1.3 m/s) times a coarse tick (up to
    // a few seconds), per the design's "a ped teleport (huge delta) can just snap (optional)".
    public double SnapDistanceMeters { get; init; } = 5.0;

    // Feed one sim tick's ped snapshot, stamped with that tick's sim time. Times must be non-decreasing;
    // pushing the same (or an out-of-order/regressed) time REPLACES the newest entry instead of growing the
    // history with a duplicate/regressed timestamp -- keeps Sample's bracket search well-defined even if a
    // caller re-samples the same sim step twice.
    public void Push(double time, IReadOnlyList<PedInterpFrame> peds)
    {
        if (_history.Count > 0 && time <= _history[^1].Time)
        {
            _history[^1] = new Snapshot(time, peds);
            return;
        }

        _history.Add(new Snapshot(time, peds));
        if (_history.Count > _capacity)
        {
            _history.RemoveAt(0);
        }
    }

    // Returns the peds' render-time positions at `renderTime`, linearly interpolated by id between the two
    // pushed snapshots bracketing it. Before the oldest pushed snapshot (or with fewer than two snapshots
    // pushed so far) holds the oldest; at or after the newest holds the newest -- exactly DrClock.ResolveAt's
    // own "extrapolate past the ends by holding" shape, just without DrClock's velocity extrapolation (peds
    // have no reported speed/accel to extrapolate from). A ped present in both bracketing snapshots lerps
    // position by the bracket fraction (regime/animTag -- discrete, not interpolated -- taken from the LATER
    // snapshot); a ped only in the later snapshot appears at the later position (no prior data to lerp from);
    // a ped only in the earlier snapshot (despawned) is dropped.
    public IReadOnlyList<PedInterpFrame> Sample(double renderTime)
    {
        if (_history.Count == 0)
        {
            return Array.Empty<PedInterpFrame>();
        }

        var oldest = _history[0];
        if (_history.Count == 1 || renderTime <= oldest.Time)
        {
            return oldest.Peds;
        }

        var newest = _history[^1];
        if (renderTime >= newest.Time)
        {
            return newest.Peds;
        }

        // Bracket search: the last snapshot with Time <= renderTime, and its immediate successor.
        var ai = 0;
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Time <= renderTime)
            {
                ai = i;
                break;
            }
        }

        var a = _history[ai];
        var b = _history[ai + 1];
        var span = b.Time - a.Time;
        var f = span > 1e-9 ? (renderTime - a.Time) / span : 1.0;

        var bById = new Dictionary<int, PedInterpFrame>(b.Peds.Count);
        foreach (var pb in b.Peds)
        {
            bById[pb.Id] = pb;
        }

        var result = new List<PedInterpFrame>(b.Peds.Count);
        var seenInA = new HashSet<int>();

        foreach (var pa in a.Peds)
        {
            seenInA.Add(pa.Id);
            if (!bById.TryGetValue(pa.Id, out var pb))
            {
                continue; // gone in the later snapshot -> dropped
            }

            var dx = pb.X - pa.X;
            var dy = pb.Y - pa.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            result.Add(dist > SnapDistanceMeters
                ? pb // teleport -> snap to the later position rather than lerp across the gap
                : new PedInterpFrame(pa.Id, pa.X + dx * f, pa.Y + dy * f, pb.Regime, pb.AnimTag));
        }

        foreach (var pb in b.Peds)
        {
            if (!seenInA.Contains(pb.Id))
            {
                result.Add(pb); // new since `a` -> appears at the later position
            }
        }

        return result;
    }
}
