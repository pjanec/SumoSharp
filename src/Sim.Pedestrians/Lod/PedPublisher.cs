using Sim.Core.Orca;

namespace Sim.Pedestrians.Lod;

// The in-memory event stream a real DDS lifecycle/high-rate topic pair would carry (docs/PEDESTRIAN-
// DESIGN.md §7; docs/PEDESTRIAN-POC-PLAN.md POC-3). This POC proves the MECHANISM -- what gets sent,
// and how rarely, for low-power vs high-power -- not the transport, so there is no DDS wiring here at
// all: PedPublisher just appends to an ordered in-process list a HeadlessIg can drain.
public abstract record PedEvent(int Id, double Time);

// Emitted once per PathArc "leg": on spawn, and again on every demotion (with a fresh re-routed path).
// The path is sent ONCE, never repeated per step -- exactly what makes the low-power population near-
// free on the wire (docs/PEDESTRIAN-DESIGN.md §7 bandwidth math).
public sealed record PathArcRecord(
    int Id,
    double Time,
    IReadOnlyList<Vec2> Path,
    double StartTime,
    double Speed,
    // C4: per-vertex elevation for `Path`, index-aligned, null on a 2-D net. Defaulted so every
    // existing construction site compiles unchanged and keeps emitting a z-less (kind 4) record.
    IReadOnlyList<double>? PathZ = null)
    : PedEvent(Id, Time);

// A DR-model switch on the lifecycle topic -- the promotion/demotion broadcast. The IG applies this at
// its Time: before it the ped is reconstructed under `From`, from it onward under `To`.
public sealed record DrSwitchEvent(int Id, double Time, PedDrModel From, PedDrModel To) : PedEvent(Id, Time);

// LIVE-POC-1 (docs/PEDESTRIAN-LIVELINESS-DESIGN.md §1, §12): the ActivityTimeline analogue of
// PathArcRecord -- broadcast ONCE (on spawn, or again on a re-plan after a mid-activity promotion/
// demotion, §10), never repeated per step. The IG stores the whole timeline and reconstructs pose +
// animation + visibility by calling the exact same ActivityTimeline.PoseAt the server calls.
public sealed record ActivityTimelineRecord(int Id, double Time, ActivityTimeline Timeline) : PedEvent(Id, Time);

// One high-power position/velocity sample, streamed every step a ped is FreeKinematic. POC-3 success
// condition 4 only requires this to be silent while low-power; a publish-on-predicted-error gate (the
// car stack's DrErrorPublishPolicy) is explicitly optional polish here, so every high-power step emits
// one -- see PedLodManager remarks for why that is still a faithful "silent when low-power" story.
public sealed record FreeKinematicSample(int Id, double Time, Vec2 Pos, Vec2 Vel) : PedEvent(Id, Time);

// Low-rate liveness signal for a PathArc ped. Carries no pose -- the IG already has the path -- so it
// costs almost nothing on the wire and lets a late/lossy channel confirm the ped still exists.
public sealed record HeartbeatEvent(int Id, double Time) : PedEvent(Id, Time);

// In-memory publisher: appends every emitted event to `Events` in wire order and tracks per-id send
// counters -- the numbers POC-3 success conditions 1 and 4 are measured against
// (FreeKinematicSamplesSent, PathArcRecordsSent, HeartbeatsSent).
public sealed class PedPublisher
{
    private readonly List<PedEvent> _events = new();
    private readonly Dictionary<int, double> _lastHeartbeatAt = new();
    private readonly Dictionary<int, int> _freeKinematicSamplesSent = new();
    private readonly Dictionary<int, int> _pathArcRecordsSent = new();
    private readonly Dictionary<int, int> _activityTimelineRecordsSent = new();
    private readonly Dictionary<int, int> _heartbeatsSent = new();

    public PedPublisher(double heartbeatInterval = 3.0)
    {
        HeartbeatInterval = heartbeatInterval;
    }

    public double HeartbeatInterval { get; }

    public IReadOnlyList<PedEvent> Events => _events;

    // ---- A6 / docs/LIVE-CITY-THREADED-TICK-DESIGN.md §6 Stage 3 -------------------------------------
    //
    // `_events` is an APPEND-ONLY history: nothing ever removed an entry, and the live-city host emits one
    // event per published ped per step. At 20 000 peds over a long run that is an unbounded list of heap
    // records -- the log's item A6, and the reason the ped handoff was the one path still allocating per
    // tick after the car side was pooled.
    //
    // It is a HISTORY on purpose for POC-3's counters and for every test that inspects the whole stream, so
    // it is not silently changed. Instead a host that has already forwarded a batch onto the wire says so:
    // `DrainInto` copies the tail into a caller-owned (reused) list, and `ClearEvents` drops the history it
    // just took. The per-id send COUNTERS are untouched by clearing -- they are what the POC success
    // conditions are measured against, and they are O(peds), not O(steps).
    //
    // A caller that never calls `ClearEvents` (every test, every other host) behaves exactly as before.

    /// Append `Events[fromIndex..]` to `into` (cleared first) and return how many were copied. No
    /// allocation once `into` has grown to its steady-state size.
    public int DrainInto(int fromIndex, List<PedEvent> into)
    {
        into.Clear();
        if (fromIndex < 0)
        {
            fromIndex = 0;
        }

        for (var i = fromIndex; i < _events.Count; i++)
        {
            into.Add(_events[i]);
        }

        return _events.Count - fromIndex;
    }

    /// Drop the accumulated event history. Only for a caller that has already forwarded everything in it
    /// (the live-city host, which publishes each step's batch onto the wire and keeps nothing). Send
    /// counters and heartbeat bookkeeping survive.
    public void ClearEvents() => _events.Clear();

    public IReadOnlyDictionary<int, int> FreeKinematicSamplesSent => _freeKinematicSamplesSent;
    public IReadOnlyDictionary<int, int> PathArcRecordsSent => _pathArcRecordsSent;
    public IReadOnlyDictionary<int, int> ActivityTimelineRecordsSent => _activityTimelineRecordsSent;
    public IReadOnlyDictionary<int, int> HeartbeatsSent => _heartbeatsSent;

    public void PublishPathArc(
        int id, IReadOnlyList<Vec2> path, double startTime, double speed, double time,
        IReadOnlyList<double>? pathZ = null)
    {
        _events.Add(new PathArcRecord(id, time, path, startTime, speed, pathZ));
        Increment(_pathArcRecordsSent, id);
    }

    public void PublishSwitch(int id, PedDrModel from, PedDrModel to, double time)
    {
        _events.Add(new DrSwitchEvent(id, time, from, to));
    }

    // Broadcast-once (LIVE-POC-1): sends the whole ActivityTimeline exactly once, mirroring
    // PublishPathArc's "path sent once" discipline.
    public void PublishActivityTimeline(int id, ActivityTimeline timeline, double time)
    {
        _events.Add(new ActivityTimelineRecord(id, time, timeline));
        Increment(_activityTimelineRecordsSent, id);
    }

    public void PublishSample(int id, double time, Vec2 pos, Vec2 vel)
    {
        _events.Add(new FreeKinematicSample(id, time, pos, vel));
        Increment(_freeKinematicSamplesSent, id);
    }

    // Emits at most once per HeartbeatInterval seconds per id -- a no-op otherwise, so calling this
    // every step for every low-power ped is the correct, cheap usage pattern (PedLodManager does).
    public void MaybePublishHeartbeat(int id, double time)
    {
        if (_lastHeartbeatAt.TryGetValue(id, out var last) && time - last < HeartbeatInterval)
        {
            return;
        }

        _lastHeartbeatAt[id] = time;
        _events.Add(new HeartbeatEvent(id, time));
        Increment(_heartbeatsSent, id);
    }

    private static void Increment(Dictionary<int, int> counts, int id)
    {
        counts.TryGetValue(id, out var n);
        counts[id] = n + 1;
    }
}
