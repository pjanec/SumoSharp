using Sim.Core;
using Sim.Core.Bridge;
using Sim.Core.Orca;
using Sim.Pedestrians.Crossing;
using Sim.Pedestrians.Lod;
using Sim.Pedestrians.Navigation;

namespace Sim.Pedestrians.Demand;

// P2-3 (docs/PEDESTRIAN-TASKS.md; docs/PEDESTRIAN-DESIGN.md §4 "Navigation", §10 "Packaging";
// docs/PEDESTRIAN-NAVMESH-CONTRACT.md): pedestrian origin->destination demand -- the piece that makes
// a scenario populate ITSELF instead of a test hand-registering one ped at a time. PedDemand is a thin
// layer ABOVE PedLodManager (it never touches OrcaCrowd/PedRouteController directly): each simulated
// second it may (a) spawn new peds -- pick an O/D pair, call IPedNavigation.FindPath once, and
// PedLodManager.AddPed them in as low-power (PathArc) by default, exactly as the design's "low-power
// motion is the cheap default" calls for -- and (b) despawn peds that have reached their destination
// via PedLodManager.RemovePed (the P2-3 addition), keeping the live population near a target cap by
// spawning again whenever an arrival frees a slot.
//
// Determinism (CLAUDE.md "no System.Random"; docs/PEDESTRIAN-DESIGN.md §8): every random decision --
// WHEN a ped spawns and WHICH O/D pair it draws -- comes from Sim.Core.VehicleRng (SplitMix64), seeded
// per draw from (config.Seed, a stable integer key, a distinguishing salt), never a shared/global RNG
// instance. Two runs built from the same PedDemandConfig and stepped with the same (now, dt) sequence
// produce IDENTICAL spawn times, O/D choices, and (because PedLodManager/PathArcMotion/OrcaCrowd are
// themselves deterministic) identical trajectories -- see PedDemandTests' determinism gate.
public sealed class PedDemand
{
    // Distinct salts so the "when do we spawn" stream and the "which O/D pair" stream, though both
    // derived from the same config.Seed, never alias each other (VehicleRng.SeedFor's own salted-
    // overload convention, mirrored from Sim.Core's C7-i speedFactor-vs-dawdle independence).
    private const ulong SpawnTimingSalt = 0x5044_5354_5054_4D01UL; // "PDSTPTM1" ascii-ish, arbitrary distinct constant
    private const ulong OriginDestSalt = 0x5044_5354_4F44_3101UL;  // "PDSTOD1", ditto

    // LIVE-PROD-1b (docs/PEDESTRIAN-LIVELINESS-DESIGN.md §4; docs/PEDESTRIAN-TASKS.md): a THIRD,
    // dedicated salt for every liveliness random decision (how many pauses, where along the route,
    // how long each one lasts). Kept fully separate from SpawnTimingSalt/OriginDestSalt so flipping
    // `PedDemandConfig.Liveliness` on/off can NEVER perturb the existing spawn-timing or O/D streams --
    // enabling liveliness only ever adds NEW draws on this salt's own independent stream, it never
    // consumes an extra draw from (or reorders) either of the other two.
    private const ulong LivelinessSalt = 0x5044_5354_4C49_5601UL;  // "PDSTLIV1" ascii-ish, arbitrary distinct constant

    // P8-3b (docs/PEDESTRIAN-P8-3-DEMAND-DESIGN.md §4): a FOURTH, dedicated salt for the weighted
    // sub-area O/D draw. Kept separate from OriginDestSalt so the weighted path (WeightedEndpoints set)
    // never aliases the uniform path's stream. This salt is only ever consumed when WeightedEndpoints is
    // set -- the null (default) path constructs no stream on it, so it can never perturb existing goldens.
    private const ulong SubareaODSalt = 0x5044_5354_5741_5701UL;   // "PDSTWAW1" ascii-ish, arbitrary distinct constant
    private const ulong WeaveSalt = 0x5044_5354_5745_5601UL;       // "PDSTWEV1" -- the per-ped lateral-weave stream, independent of the others

    // Bug #6 (crosswalk-wait kerb clustering): a FIFTH, dedicated salt for the per-ped lateral
    // sidestep-along-the-kerb offset drawn while waiting at a signalized crossing. Independent of
    // every other stream -- when `PedDemandConfig.CrosswalkWaitSpreadRadius` is 0 (the default) this
    // salt's stream is never even constructed, let alone drawn from, so the off path stays
    // byte-identical to pre-change PedDemand (the ITERON RULE).
    private const ulong WaitJitterSalt = 0x5044_5354_5741_4A31UL; // "PDSTWAJ1" per-ped crosswalk-wait lateral spread, independent of every other stream

    // Phase 2b (docs/LIVE-CITY-CROSSWALK-SIGNAL-DESIGN.md): the anim tag a low-power ped plays while
    // waiting at a signalized crossing's kerb on red. Anything other than ActivityTimeline.WalkAnimTag
    // renders as a paused disc (SceneGen.BuildLivelyCrowd), so a waiting ped is visibly stopped at the kerb.
    private const string CrosswalkWaitTag = "wait";

    private readonly PedDemandConfig _config;
    private readonly IPedNavigation _navigation;
    private readonly PedLodManager _lodManager;

    private readonly SortedSet<int> _liveIds = new();
    private readonly Dictionary<int, Vec2> _destinationOf = new();
    private readonly List<PedSpawnEvent> _spawnEvents = new();
    private readonly List<PedArrivalEvent> _arrivalEvents = new();

    private VehicleRng _spawnTimingRng;
    private double _nextSpawnAt;
    private int _nextId = 1;

    public PedDemand(PedDemandConfig config, IPedNavigation navigation, PedLodManager lodManager, double startTime = 0.0)
    {
        // With WeightedEndpoints set, the endpoint set supplies O/D, so Origins/Destinations may be
        // empty (the P8-3b relaxation); without it, the uniform path still requires both.
        if (config.WeightedEndpoints is { } weighted)
        {
            if (weighted.Count == 0)
            {
                throw new ArgumentException("PedDemandConfig.WeightedEndpoints must have at least one endpoint.", nameof(config));
            }
        }
        else
        {
            if (config.Origins.Count == 0)
            {
                throw new ArgumentException("PedDemandConfig.Origins must have at least one point.", nameof(config));
            }

            if (config.Destinations.Count == 0)
            {
                throw new ArgumentException("PedDemandConfig.Destinations must have at least one point.", nameof(config));
            }
        }

        _config = config;
        _navigation = navigation;
        _lodManager = lodManager;

        _spawnTimingRng = VehicleRng.SeedFor(config.Seed, entityIndex: 0, salt: SpawnTimingSalt);
        _nextSpawnAt = startTime + DrawInterArrivalInterval();
    }

    /// Number of peds currently live (spawned, not yet arrived/despawned).
    public int LiveCount => _liveIds.Count;

    /// Total peds ever spawned (including any since arrived).
    public int SpawnCount { get; private set; }

    /// Total peds that have reached their destination and been despawned.
    public int ArrivalCount { get; private set; }

    /// FindPath(origin, destination) returned null (unreachable pair) -- the spawn attempt was
    /// skipped rather than registering an un-routable ped. Not expected to fire for a well-formed
    /// O/D configuration; exposed so a test/caller can assert it stays at 0 for its scenario.
    public int UnreachableSkipCount { get; private set; }

    /// Currently-live ped ids, ascending (SortedSet -- deterministic enumeration order).
    public IReadOnlyCollection<int> LiveIds => _liveIds;

    /// Every spawn, in spawn order -- for determinism/inspection (docs/PEDESTRIAN-NAVMESH-CONTRACT.md).
    public IReadOnlyList<PedSpawnEvent> SpawnEvents => _spawnEvents;

    /// Every arrival, in arrival order.
    public IReadOnlyList<PedArrivalEvent> ArrivalEvents => _arrivalEvents;

    // Advances demand by one tick [now, now+dt): spawn any peds due (population permitting), let
    // PedLodManager advance the whole population by dt exactly as a bare PedLodManager.Step call
    // would (PedDemand does not re-implement or shadow LOD/promotion physics), then despawn arrivals.
    // `field`/`externalEntities` are passed straight through to PedLodManager.Step -- PedDemand is
    // agnostic to interest sources; a caller wanting promotion/demotion alongside OD demand just
    // supplies the same InterestField it would to a bare PedLodManager.
    public void Step(double now, double dt, InterestField field, IReadOnlyList<WorldDisc> externalEntities)
    {
        SpawnDue(now, dt);

        _lodManager.Step(now, dt, field, externalEntities);

        DespawnArrivals(now + dt);
    }

    // Spawns every ped whose drawn spawn time falls in [now, now+dt), as long as doing so keeps the
    // live population at or under the cap. If the cap was reached earlier and time has since passed
    // with no free slot, the schedule is clamped to `now` rather than left to accumulate a backlog --
    // freeing a slot resumes ONE fresh exponential draw from the current instant, not a burst of
    // catch-up spawns for every interval that elapsed while capped. This is a pure function of `now`
    // (itself advanced deterministically by the caller's own dt loop), so it does not affect
    // reproducibility.
    private void SpawnDue(double now, double dt)
    {
        if (_nextSpawnAt < now)
        {
            _nextSpawnAt = now;
        }

        var horizon = now + dt;
        while (_liveIds.Count < _config.PopulationCap && _nextSpawnAt < horizon)
        {
            TrySpawnOne(_nextSpawnAt);
            _nextSpawnAt += DrawInterArrivalInterval();
        }
    }

    private double DrawInterArrivalInterval()
    {
        if (_config.SpawnRatePerSecond <= 0.0)
        {
            return double.PositiveInfinity; // a demand with no spawn rate never spawns again
        }

        // Standard inverse-CDF draw for a Poisson process's exponential inter-arrival time.
        // NextDouble() is [0,1); (1 - u) keeps the log's argument in (0,1], never exactly 0.
        var u = _spawnTimingRng.NextDouble();
        return -Math.Log(1.0 - u) / _config.SpawnRatePerSecond;
    }

    private void TrySpawnOne(double now)
    {
        var id = _nextId++;

        Vec2 origin;
        Vec2 destination;
        if (_config.WeightedEndpoints is { } weighted)
        {
            // P8-3b (docs/PEDESTRIAN-P8-3-DEMAND-DESIGN.md §4): weighted sub-area O/D. ONE per-ped stream
            // on the dedicated SubareaODSalt (never aliases the uniform OriginDestSalt path), two weighted
            // draws -> origin then destination. Every endpoint is a fringe or POI edge, so both ends are
            // appearance-legitimate by construction (the P8-3 x P8-2 synergy).
            var rng = VehicleRng.SeedFor(_config.Seed, id, SubareaODSalt);
            var originIdx = weighted.DrawWeightedIndex(ref rng);
            var destIdx = weighted.DrawWeightedIndex(ref rng);
            origin = weighted[originIdx].Pos;
            destination = weighted[destIdx].Pos;

            // Same zero-length guard as the uniform path -- deterministic, no extra draw: walk to the
            // next endpoint when origin/destination coincide.
            var guard = 0;
            while (PointsCoincide(origin, destination) && guard < weighted.Count)
            {
                destIdx = (destIdx + 1) % weighted.Count;
                destination = weighted[destIdx].Pos;
                guard++;
            }
        }
        else
        {
            // ONE per-ped stream, seeded from (config.Seed, id, OriginDestSalt) -- independent of every
            // other ped's stream and of the spawn-timing stream above (VehicleRng.SeedFor's salted
            // overload), so which O/D pair a ped draws never depends on how many peds spawned before it
            // beyond its own id, nor on thread/evaluation order (this class is single-threaded anyway).
            var rng = VehicleRng.SeedFor(_config.Seed, id, OriginDestSalt);
            var originIndex = PickIndex(ref rng, _config.Origins.Count);
            var destIndex = PickIndex(ref rng, _config.Destinations.Count);

            origin = _config.Origins[originIndex];
            destination = _config.Destinations[destIndex];

            // Avoid a trivial zero-length trip when an alternative destination exists -- deterministic
            // (no extra random draw): just walk to the next candidate destination.
            var guard = 0;
            while (PointsCoincide(origin, destination) && guard < _config.Destinations.Count)
            {
                destIndex = (destIndex + 1) % _config.Destinations.Count;
                destination = _config.Destinations[destIndex];
                guard++;
            }
        }

        var path = _navigation.FindPath(origin, destination);
        if (path is null)
        {
            UnreachableSkipCount++;
            return; // id is simply skipped (a sparse id space is harmless) -- no retry, no extra draw
        }

        // ITERON RULE (CLAUDE.md; docs/PEDESTRIAN-TASKS.md LIVE-PROD-1b): with Liveliness omitted
        // (null, the default), this is the EXACT call the pre-liveliness code made -- no new RNG
        // stream is even constructed -- so a plain PedDemandConfig stays bit-identical to today's
        // behaviour. Only when Liveliness is set do we spend a (fresh-salted) draw building a timeline
        // and call AddPedLively instead of AddPed.
        if (_config.Liveliness is { } liveliness)
        {
            var livelinessRng = VehicleRng.SeedFor(_config.Seed, id, LivelinessSalt);

            // W2 (docs/PEDESTRIAN-WEAVE-PRODUCTION-DESIGN.md): when the weave is enabled, this ped gets a
            // per-ped weave seed off the SHARED scenario root (same discipline as the liveliness stream) and
            // GlobalSeed = the scenario seed for the counterflow interface. Off => 0/0 => weave inactive =>
            // timelines are built exactly as before (byte-identical poses; the ITERON RULE holds).
            var weaveSeed = _config.EnableWeave ? VehicleRng.SeedFor(_config.Seed, id, WeaveSalt).RawState : 0UL;
            var globalSeed = _config.EnableWeave ? _config.Seed : 0UL;

            // Bug #6: a dedicated per-ped rng for the crosswalk-wait lateral spread, fully independent
            // of every other stream (own salt). Constructed unconditionally (cheap -- just a SplitMix64
            // seed mix), but only ever DRAWN FROM inside SplitWalkAtCrossings when
            // CrosswalkWaitSpreadRadius > 0, so its mere existence never perturbs anything.
            var waitJitterRng = VehicleRng.SeedFor(_config.Seed, id, WaitJitterSalt);

            var timeline = BuildLivelyTimeline(
                path, now, liveliness, _config.MaxSpeed, ref livelinessRng,
                _config.EnableWeave, weaveSeed, globalSeed, _navigation.HalfWidthsAlong,
                _config.CrosswalkSignals, _config.CrosswalkWaitSpreadRadius, ref waitJitterRng);
            _lodManager.AddPedLively(id, timeline, _config.MaxSpeed, _config.Radius, now);
        }
        else
        {
            _lodManager.AddPed(id, path, _config.MaxSpeed, _config.Radius, now);
        }

        _destinationOf[id] = destination;
        _liveIds.Add(id);
        SpawnCount++;
        _spawnEvents.Add(new PedSpawnEvent(id, now, origin, destination));
    }

    // LIVE-PROD-1b (docs/PEDESTRIAN-LIVELINESS-DESIGN.md §4): builds the ActivityTimeline for a lively
    // spawn -- the routed `path` as Walk leg(s) with 0..MaxPausesPerTrip seeded Pause beats spliced in
    // at seeded along-route fractions. A Pause holds the ped wherever the preceding Walk left it (no
    // route change -- ActivityTimeline's own anchor-inheritance, see PauseSegment's remarks), so the
    // LAST segment is always a Walk ending at `path[^1]` (the ped's real, unmodified destination):
    // DespawnArrivals compares PositionOf/PoseAt against that same point, so a lively ped still
    // arrives and despawns normally -- a mid-route Pause delays arrival, it never prevents or advances
    // it.
    //
    // Every draw here comes from `rng` (caller-seeded off LivelinessSalt), so the sequence is: for
    // each of the MaxPausesPerTrip candidate pause slots, ONE acceptance draw, and -- only if accepted
    // -- ONE fraction draw immediately followed by ONE duration draw (so a single candidate's three
    // values are always adjacent in the stream regardless of how many other candidates land). Accepted
    // candidates are then sorted by fraction so they always splice into the route in along-route
    // order, independent of draw order.
    private static ActivityTimeline BuildLivelyTimeline(
        IReadOnlyList<Vec2> path, double now, PedLivelinessConfig liveliness, double maxSpeed, ref VehicleRng rng,
        bool weave, ulong seed, ulong globalSeed, Func<IReadOnlyList<Vec2>, IReadOnlyList<double>> halfWidthsAlong,
        CrosswalkSignals? crosswalkSignals, double waitSpreadRadius, ref VehicleRng waitRng)
    {
        // W2: each Walk leg's per-vertex half-width, sampled from the navmesh for that exact (possibly
        // pause-split) sub-path -- so an interpolated split point gets the width of the polygon it lands in.
        // Weave off => null widths => the WalkSegment weave is inactive (pose stays on the centreline).
        WalkSegment MakeWalk(IReadOnlyList<Vec2> pts) =>
            weave ? new WalkSegment(pts, maxSpeed, halfWidthsAlong(pts)) : new WalkSegment(pts, maxSpeed);

        var pauses = new List<(double Fraction, double Duration)>();
        for (var i = 0; i < liveliness.MaxPausesPerTrip; i++)
        {
            if (rng.NextDouble() < liveliness.PauseProbability)
            {
                var fraction = rng.NextDouble();
                var duration = liveliness.MinPauseSeconds
                    + rng.NextDouble() * (liveliness.MaxPauseSeconds - liveliness.MinPauseSeconds);
                pauses.Add((fraction, duration));
            }
        }

        pauses.Sort((a, b) => a.Fraction.CompareTo(b.Fraction));

        var segments = new List<ActivitySegment>();
        var remainingPath = path;
        var consumedFraction = 0.0;

        foreach (var (fraction, duration) in pauses)
        {
            if (remainingPath.Count < 2)
            {
                break; // route already fully consumed by an earlier pause -- drop any further pause
            }

            // `fraction` is drawn along the ORIGINAL route; re-express it relative to what's LEFT of
            // the route after any earlier (smaller-fraction) pause already sliced off a prefix.
            var denom = 1.0 - consumedFraction;
            var relFraction = denom > 1e-9 ? Math.Clamp((fraction - consumedFraction) / denom, 0.0, 1.0) : 1.0;

            var (splitPoint, segIndex) = PointAtFraction(remainingPath, relFraction);

            var prefix = new List<Vec2>(segIndex + 2);
            for (var k = 0; k <= segIndex; k++)
            {
                prefix.Add(remainingPath[k]);
            }

            if (prefix.Count == 0 || (prefix[^1] - splitPoint).Abs > 1e-9)
            {
                prefix.Add(splitPoint);
            }

            if (prefix.Count >= 2)
            {
                segments.Add(MakeWalk(prefix));
            }

            segments.Add(new PauseSegment(duration, liveliness.PauseAnimTag));

            var suffix = new List<Vec2>(remainingPath.Count - segIndex) { splitPoint };
            for (var k = segIndex + 1; k < remainingPath.Count; k++)
            {
                suffix.Add(remainingPath[k]);
            }

            remainingPath = suffix;
            consumedFraction = fraction;
        }

        if (remainingPath.Count >= 2)
        {
            segments.Add(MakeWalk(remainingPath));
        }
        else if (segments.Count == 0)
        {
            // No pause ever got far enough to consume anything (e.g. zero accepted draws, or a
            // degenerate 0/1-point path) -- fall back to walking the untouched original path so the
            // timeline is never empty (ActivityTimeline requires >= 1 segment).
            segments.Add(MakeWalk(path));
        }

        // Phase 2b: splice a kerb wait before any signalized crossing this route steps onto (opt-in via
        // PedDemandConfig.CrosswalkSignals; null -> this is skipped entirely -> byte-identical to pre-2b).
        // The insertion draws NO rng and is a pure function of the (already deterministic) segment
        // durations, so the ped stays a pure ActivityTimeline (server==IG holds -- W1/W3).
        if (crosswalkSignals is { } signals)
        {
            segments = InsertCrosswalkWaits(segments, now, maxSpeed, signals, waitSpreadRadius, ref waitRng, halfWidthsAlong);
        }

        return new ActivityTimeline(now, segments, seed, globalSeed);
    }

    // Phase 2b (docs/LIVE-CITY-CROSSWALK-SIGNAL-DESIGN.md §2b): walk the built segments keeping a running
    // clock `t` (= absolute time the ped reaches the START of the current segment; every ActivitySegment's
    // Duration is exact, so this matches ActivityTimeline's own timing). For each WalkSegment, detect where
    // the route ENTERS a signalized crossing and, if the ped would arrive on red (or without room to
    // clear), split the walk at the kerb and insert a Pause until NextWalkStart. No rng, no promotion.
    private static List<ActivitySegment> InsertCrosswalkWaits(
        List<ActivitySegment> segments, double now, double speed, CrosswalkSignals signals,
        double waitSpreadRadius, ref VehicleRng waitRng,
        Func<IReadOnlyList<Vec2>, IReadOnlyList<double>> halfWidthsAlong)
    {
        if (signals.SignalizedCount == 0 || speed <= 0.0)
        {
            return segments; // nothing signalized in range -> untouched
        }

        var result = new List<ActivitySegment>(segments.Count);
        var t = now;
        foreach (var seg in segments)
        {
            if (seg is WalkSegment w && w.Path.Count >= 2)
            {
                SplitWalkAtCrossings(w, ref t, speed, signals, result, waitSpreadRadius, ref waitRng, halfWidthsAlong);
            }
            else
            {
                result.Add(seg);
                t += seg.Duration;
            }
        }

        return result;
    }

    // Split one WalkSegment at each signalized-crossing entry, inserting a kerb Pause when the ped would
    // arrive on red. A sub-segment [path[i], path[i+1]] is "on" a crossing iff its MIDPOINT is inside that
    // crossing's polygon (midpoints are unambiguously inside one polygon, unlike portal vertices which sit
    // on a shared boundary edge). ENTRY = a sub-segment on crossing X whose predecessor was not on X; the
    // kerb is path[i] (the entry vertex). Advances `t` by every emitted sub-walk/pause so downstream
    // segments keep correct absolute timing.
    private static void SplitWalkAtCrossings(
        WalkSegment w, ref double t, double speed, CrosswalkSignals signals, List<ActivitySegment> result,
        double waitSpreadRadius, ref VehicleRng waitRng,
        Func<IReadOnlyList<Vec2>, IReadOnlyList<double>> halfWidthsAlong)
    {
        var path = w.Path;
        var n = path.Count;

        // Classify each sub-segment by the signalized crossing (if any) its midpoint lies on.
        var segCross = new string?[n - 1];
        var anyOnCrossing = false;
        for (var i = 0; i + 1 < n; i++)
        {
            var mid = (path[i] + path[i + 1]) * 0.5;
            segCross[i] = signals.TryLocate(mid, out var id) ? id : null;
            anyOnCrossing |= segCross[i] != null;
        }

        if (!anyOnCrossing)
        {
            result.Add(w); // this walk never touches a signalized crossing -> pass through untouched
            t += w.Duration;
            return;
        }

        var segStart = 0;
        for (var i = 0; i + 1 < n; i++)
        {
            var here = segCross[i];
            var prev = i == 0 ? null : segCross[i - 1];
            var isEntry = here != null && !string.Equals(here, prev, StringComparison.Ordinal);
            if (!isEntry)
            {
                continue;
            }

            // Absolute time the ped reaches the kerb (path[i]) if it walks segStart..i at `speed`.
            var kerbTime = t + (SubPathLength(path, segStart, i) / speed);
            var stepOn = signals.NextWalkStart(here!, kerbTime, speed);
            var wait = stepOn - kerbTime;
            if (wait <= 1e-6)
            {
                continue; // arrives on green with room -> cross continuously, no split
            }

            if (i > segStart)
            {
                var pre = SubWalk(w, segStart, i);
                result.Add(pre);
                t += pre.Duration;
            }

            // Bug #6 (crosswalk-wait kerb clustering): OFF (waitSpreadRadius<=0) keeps exactly the
            // original single-pause path -- byte-identical, and `waitRng` is never touched (the ITERON
            // RULE). ON: the ped steps into a per-ped seeded 2-D spot in the waiting BLOB at the kerb --
            // spread laterally along the kerb AND back into the sidewalk (a dense cluster at the crossing
            // mouth, "a circle at the end of the rod", not a single line along the road) -- waits there,
            // then crosses DIAGONALLY straight from its spot to the crossing exit path[i+1]. No step-back:
            // the diagonal consumes the crossing sub-segment path[i]->path[i+1], so segStart advances to
            // i+1. The far side needs no spread -- nobody waits there; peds disperse via their onward
            // routes (and cross while MOVING, so the exit vertex forms no stationary cluster).
            var kerbDir = path[i + 1] - path[i];
            var blobPoint = Vec2.Zero;
            var useBlob = false;
            if (waitSpreadRadius > 0.0 && kerbDir.Abs >= 1e-9)
            {
                var d = kerbDir / kerbDir.Abs;                  // crossing direction (rod axis), unit
                var perp = new Vec2(-d.Y, d.X);                 // along the near kerb, unit
                var u = (waitRng.NextDouble() * 2.0 - 1.0) * waitSpreadRadius; // lateral, both sides
                var v = waitRng.NextDouble() * waitSpreadRadius;              // back into the sidewalk (never onto the road)
                var candidate = path[i] + (perp * u) - (d * v);
                if ((candidate - path[i]).Abs >= 1e-6)
                {
                    blobPoint = candidate;
                    useBlob = true;
                }
            }

            if (!useBlob)
            {
                result.Add(new PauseSegment(wait, CrosswalkWaitTag));
                t += wait;
                segStart = i; // crossing sub-segment still lives in the tail / next pre-walk
            }
            else
            {
                var enterDur = (blobPoint - path[i]).Abs / speed;
                result.Add(new WalkSegment(new List<Vec2> { path[i], blobPoint }, speed)); // step into the blob
                t += enterDur;

                var blobWait = Math.Max(0.0, wait - enterDur); // leave the blob at ~the same green instant
                result.Add(new PauseSegment(blobWait, CrosswalkWaitTag));
                t += blobWait;

                // Weave the diagonal cross like a sidewalk leg -- ONLY when this ped is weaving (the split
                // walk carried per-vertex widths). Sampling the crosswalk half-widths lets LateralWeave put
                // each crosser on its own travel-direction RIGHT side, so the two opposing streams keep to
                // opposite halves of the crossing (no head-on centreline pass-through) and wave-shape within
                // their side -- the sidewalk behaviour extended onto the crossing (docs/Lod/LateralWeave.cs).
                // Weave off (HalfWidths null) => plain straight cross, byte-identical to before.
                var crossPts = new List<Vec2> { blobPoint, path[i + 1] };
                result.Add(w.HalfWidths != null
                    ? new WalkSegment(crossPts, speed, halfWidthsAlong(crossPts))
                    : new WalkSegment(crossPts, speed)); // diagonal cross
                t += (path[i + 1] - blobPoint).Abs / speed;

                segStart = i + 1; // the diagonal consumed path[i]->path[i+1]; continue from the exit vertex
            }
        }

        if (segStart == 0)
        {
            result.Add(w); // an entry was found but every arrival was already on green -> untouched
            t += w.Duration;
        }
        else if (segStart < n - 1)
        {
            var tail = SubWalk(w, segStart, n - 1);
            result.Add(tail);
            t += tail.Duration;
        }
        // else segStart == n-1: a #6 diagonal crossing already ended at the walk's final vertex, so the
        // route is fully covered -- adding SubWalk(w, n-1, n-1) would be a degenerate 1-point leg.
    }

    // Arc length of path[from..to] (inclusive vertices), i.e. the walk distance from vertex `from` to `to`.
    private static double SubPathLength(IReadOnlyList<Vec2> path, int from, int to)
    {
        var len = 0.0;
        for (var k = from; k < to; k++)
        {
            len += (path[k + 1] - path[k]).Abs;
        }

        return len;
    }

    // A WalkSegment over path[from..to] (inclusive), slicing the parallel per-vertex half-widths so the
    // weave (if any) uses the same widths this sub-path had in the original segment.
    private static WalkSegment SubWalk(WalkSegment w, int from, int to)
    {
        var pts = new List<Vec2>(to - from + 1);
        List<double>? widths = w.HalfWidths != null ? new List<double>(to - from + 1) : null;
        for (var k = from; k <= to; k++)
        {
            pts.Add(w.Path[k]);
            widths?.Add(w.HalfWidths![k]);
        }

        return widths != null ? new WalkSegment(pts, w.Speed, widths) : new WalkSegment(pts, w.Speed);
    }

    // Interpolated point at `fraction` (0..1) of `path`'s own arc length, plus the index `i` such that
    // the point lies on segment [path[i], path[i+1]]. Mirrors PathArcMotion's own degenerate
    // (duplicate-point) segment skipping so a split point lands exactly where PathArcMotion's own arc
    // walk would land at the same arc-length.
    private static (Vec2 Point, int SegmentIndex) PointAtFraction(IReadOnlyList<Vec2> path, double fraction)
    {
        if (path.Count < 2)
        {
            return (path[^1], 0);
        }

        var total = PathArcMotion.PathLength(path);
        if (total <= 1e-12)
        {
            return (path[^1], path.Count - 2);
        }

        var target = Math.Clamp(fraction, 0.0, 1.0) * total;
        var acc = 0.0;
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var segLen = (path[i + 1] - path[i]).Abs;
            if (segLen <= 1e-12)
            {
                continue; // degenerate (duplicate-point) segment -- no arc-length, skip it
            }

            if (acc + segLen >= target)
            {
                var t = (target - acc) / segLen;
                return (path[i] + (path[i + 1] - path[i]) * t, i);
            }

            acc += segLen;
        }

        return (path[^1], path.Count - 2);
    }

    // Uniform index in [0, count) from one NextDouble() draw; count<=1 short-circuits without
    // consuming a draw so a single-point origin/destination set never perturbs the other stream.
    private static int PickIndex(ref VehicleRng rng, int count)
    {
        if (count <= 1)
        {
            return 0;
        }

        var raw = (int)(rng.NextDouble() * count);
        return raw >= count ? count - 1 : raw; // guard the (rare) u -> 1.0 rounding edge
    }

    private static bool PointsCoincide(Vec2 a, Vec2 b) => (a - b).Abs < 1e-9;

    // Despawns every live ped within ArrivalRadius of ITS OWN destination at `now`. Collects the
    // arrived set first, then removes in ascending-id order -- deterministic regardless of the
    // SortedSet's own enumeration order (already ascending, but sorting is explicit and cheap
    // insurance rather than relying on that detail).
    private void DespawnArrivals(double now)
    {
        if (_liveIds.Count == 0)
        {
            return;
        }

        List<int>? arrived = null;
        foreach (var id in _liveIds)
        {
            var pos = _lodManager.PositionOf(id, now);
            var dest = _destinationOf[id];
            if ((pos - dest).Abs <= _config.ArrivalRadius)
            {
                (arrived ??= new List<int>()).Add(id);
            }
        }

        if (arrived is null)
        {
            return;
        }

        arrived.Sort();
        foreach (var id in arrived)
        {
            _lodManager.RemovePed(id);
            _liveIds.Remove(id);
            _destinationOf.Remove(id);
            ArrivalCount++;
            _arrivalEvents.Add(new PedArrivalEvent(id, now));
        }
    }
}

// Immutable configuration for a PedDemand instance (docs/PEDESTRIAN-NAVMESH-CONTRACT.md "OD demand").
// Origins/Destinations are flat point sets rather than a full O->D matrix: a scenario that needs
// per-pair weighting can express it by repeating points (a point listed twice is twice as likely to
// be drawn) -- kept deliberately simple, matching the task's "a set of origin points/regions +
// destination points/regions (or an O->D matrix)" as the minimal member of that family.
public sealed class PedDemandConfig
{
    public required IReadOnlyList<Vec2> Origins { get; init; }
    public required IReadOnlyList<Vec2> Destinations { get; init; }

    /// Mean spawn rate, peds/sec (Poisson process). <= 0 means "never spawn" (a static population).
    public required double SpawnRatePerSecond { get; init; }

    /// Target concurrent live population; PedDemand spawns to (and holds at) this cap, never over it.
    public required int PopulationCap { get; init; }

    /// Seeds every random decision this instance makes (spawn timing, O/D choice). Same seed + same
    /// (now, dt) step sequence => identical spawn/arrival events and trajectories.
    public required ulong Seed { get; init; }

    public double MaxSpeed { get; init; } = 1.4;
    public double Radius { get; init; } = 0.3;

    /// A ped despawns once within this distance of its destination point.
    public double ArrivalRadius { get; init; } = 0.5;

    /// LIVE-PROD-1b (docs/PEDESTRIAN-LIVELINESS-DESIGN.md §4; docs/PEDESTRIAN-TASKS.md): ADDITIVE,
    /// optional liveliness block. Null (the default) => every spawn goes through `AddPed` exactly as
    /// before -- bit-identical to pre-liveliness `PedDemand` (the ITERON RULE). Set it to have
    /// `TrySpawnOne` instead build an `ActivityTimeline` (the routed path as Walk segments with seeded
    /// Pause beats spliced in) and call `AddPedLively`.
    public PedLivelinessConfig? Liveliness { get; init; }

    /// W2 (docs/PEDESTRIAN-WEAVE-PRODUCTION-DESIGN.md): ADDITIVE, off by default. When false, lively
    /// timelines are built exactly as before (no seed, no per-vertex widths => the weave is inactive =>
    /// byte-identical poses; the ITERON RULE). When true, each lively spawn's Walk legs carry the baked
    /// per-vertex sidewalk half-width and the timeline carries a per-ped weave seed (off `Seed` via
    /// `VehicleRng.SeedFor`) + GlobalSeed=`Seed`, so the ped weaves deterministically within its sidewalk
    /// (server==IG holds -- W1). Only takes effect together with `Liveliness` (the low-power lively path).
    public bool EnableWeave { get; init; }

    /// P8-3b (docs/PEDESTRIAN-P8-3-DEMAND-DESIGN.md §4): ADDITIVE, optional weighted sub-area endpoint
    /// set. Null (the default) => O/D are drawn from the uniform `Origins`/`Destinations` exactly as
    /// before -- bit-identical to pre-P8-3 `PedDemand` (the ITERON RULE; no stream is constructed on the
    /// dedicated sub-area salt). When set, `TrySpawnOne` draws origin+destination from it (weighted by
    /// endpoint weight) instead; every endpoint is a fringe or POI edge, so both ends are appearance-
    /// legitimate by construction. `Origins`/`Destinations` may be empty when this is set.
    public SubareaDemand? WeightedEndpoints { get; init; }

    /// Phase 2b (docs/LIVE-CITY-CROSSWALK-SIGNAL-DESIGN.md): ADDITIVE, optional. Null (the default) =>
    /// lively timelines are built exactly as before -- byte-identical to pre-2b `PedDemand` (no crossing
    /// lookup runs). When set (only meaningful together with `Liveliness`), each lively route is
    /// post-processed so that wherever it steps onto a SIGNALIZED crossing the ped first waits at the kerb
    /// until the analytic walk window (from the static `<tlLogic>`), then crosses. The wait is a pure
    /// function of time (no rng, no runtime signal polling), so server==IG and two runs stay byte-identical.
    public CrosswalkSignals? CrosswalkSignals { get; init; }

    /// Bug #6 (crosswalk-wait kerb clustering): ADDITIVE, opt-in. 0.0 (the default) => off => the
    /// crosswalk kerb wait is the single point `path[i]` exactly as before -- byte-identical to
    /// pre-change `PedDemand` (no rng stream is even drawn on the dedicated wait-jitter salt). >0.0 =>
    /// each ped waiting at a signalized crossing steps into a per-ped seeded 2-D spot in the waiting BLOB
    /// at the kerb -- offset up to this many metres laterally along the kerb AND back into the sidewalk (a
    /// dense cluster at the crossing mouth, not a single line) -- waits there, then crosses DIAGONALLY
    /// straight to the crossing exit. So waiting peds form a realistic blob instead of stacking on one
    /// point, and each crosses from where it stood.
    /// Only meaningful together with `Liveliness` and `CrosswalkSignals`.
    public double CrosswalkWaitSpreadRadius { get; init; } = 0.0;
}

/// LIVE-PROD-1b (docs/PEDESTRIAN-LIVELINESS-DESIGN.md §4): per-trip liveliness knobs. Deliberately
/// small -- a per-spawn, per-candidate-slot probability of inserting a Pause beat, each of a seeded
/// duration in [MinPauseSeconds, MaxPauseSeconds] at a seeded position along the route. (Dwell/POI
/// visits are a documented extension, §4/§8, but are NOT implemented here -- the task calls the Pause
/// beats "the required core" and says to include Dwell only if it stays clean; keeping this block to
/// just Pause keeps `PedDemand` simple and keeps every random draw's role unambiguous.)
public sealed class PedLivelinessConfig
{
    /// Probability, independently checked for each of up to `MaxPausesPerTrip` candidate slots, that
    /// a Pause beat is inserted there. 0 => liveliness is "on" (uses AddPedLively/ActivityTimeline) but
    /// never actually pauses; 1 => every candidate slot gets a Pause.
    public double PauseProbability { get; init; } = 0.35;

    /// Pause duration is drawn uniformly in [MinPauseSeconds, MaxPauseSeconds].
    public double MinPauseSeconds { get; init; } = 2.0;
    public double MaxPauseSeconds { get; init; } = 6.0;

    /// Upper bound on how many Pause beats a single trip may get (0 disables pausing entirely while
    /// still routing lively/ActivityTimeline-based, e.g. to test the plumbing without any beats).
    public int MaxPausesPerTrip { get; init; } = 1;

    /// Animation tag played during a Pause beat (docs/PEDESTRIAN-LIVELINESS-DESIGN.md §3's tag
    /// vocabulary) -- SceneGen.BuildLivelyCrowd renders anything other than `ActivityTimeline.WalkAnimTag`
    /// as the paused disc kind, so any tag here reads as "paused" regardless of its exact value.
    public string PauseAnimTag { get; init; } = "sip";
}

/// One spawn: `Id` registered with PedLodManager at `Time`, routed from `Origin` to `Destination`.
public readonly record struct PedSpawnEvent(int Id, double Time, Vec2 Origin, Vec2 Destination);

/// One arrival: `Id` despawned at `Time` (it reached its destination's ArrivalRadius).
public readonly record struct PedArrivalEvent(int Id, double Time);
