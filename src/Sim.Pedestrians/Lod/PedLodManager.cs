using Sim.Core.Bridge;
using Sim.Core.Orca;
using Sim.Pedestrians.Navigation;
using Sim.Pedestrians.Navigation.Bake;

namespace Sim.Pedestrians.Lod;

// Sim-LOD promotion/demotion + PathArc<->FreeKinematic switching (docs/PEDESTRIAN-DESIGN.md §5, §7;
// docs/PEDESTRIAN-POC-PLAN.md POC-3). Owns a population of peds, each either:
//   - Low-power (PedDrModel.PathArc): pose is a pure function of (path, startTime, speed, now) via
//     PathArcMotion -- O(1), no neighbour query, no ORCA.
//   - High-power (PedDrModel.FreeKinematic): a real agent in a persistent high-power OrcaCrowd,
//     routed by a persistent PedRouteController + WaypointFollower exactly like POC-1a, reacting to
//     every other high-power ped AND to `externalEntities`.
//
// A ped is high-power iff its (frozen, start-of-step) position lies within ANY active
// InterestSource.PromoteRadius; it demotes once it has been continuously outside EVERY source's
// (larger) DemoteRadius for `dwellSeconds`. `dwellSeconds` ALSO gates how soon a ped may leave the
// state it just entered (both directions) -- the "minimum-dwell in each state" the design calls for,
// collapsed into one knob for this POC (a production version might separate "how long outside before
// demoting" from "minimum time before ANY transition"; see the report for this simplification).
//
// P0-3 (docs/PEDESTRIAN-TASKS.md, PEDESTRIAN-POC7C-FINDINGS.md Q2): the POC-3 version of this class
// had NO agent removal on either the crowd or the route-controller side, so every membership change
// rebuilt the ENTIRE high-power OrcaCrowd from scratch AND re-derived EVERY still-high ped's steering
// route (even peds nothing happened to) -- an O(current-high-power-count) cost per switch, measured
// at 100k as the dominant reason a churning (constantly promoting/demoting) world cost 3.6x a stable
// one. P0-1/P0-2 gave OrcaCrowd a real O(1) Add/Remove and P0-3 (this class) now uses it directly:
// `_highCrowd`/`_highController` are PERSISTENT for the lifetime of this manager -- a promotion Adds
// exactly the one newly-promoted ped and registers exactly its route; a demotion Removes exactly
// that one ped's handle and route. Every OTHER high-power ped's handle, position, velocity, route,
// AND waypoint cursor are completely untouched by someone else's promotion/demotion -- there is
// nothing left to rebuild.
//
// P1-1 (docs/PEDESTRIAN-TASKS.md; docs/PEDESTRIAN-DESIGN.md §5): the POC-3 version of Step took a
// bare `IReadOnlyList<InterestSource>` and full-double-scanned it (every ped against every source,
// O(peds * sources)) with no stable identity for a caller juggling several independently-moving
// sources. Step now takes an `InterestField` (see InterestField.cs): a managed, multi-source field
// with stable per-source ids (Register/Move/Remove) and a bounded, grid-indexed per-ped query
// (RebuildIndex once per step, Query once per ped) -- same promotion/demotion semantics and hysteresis
// as POC-3, but the per-step scan no longer multiplies with the source count.

// Diagnostic-only, ADDITIVE: one ped's internal LOD state, exposed read-only by
// PedLodManager.DiagnosticSnapshot for external investigation tooling (e.g. Sim.Viz
// --live-city-pedtrace). Mirrors fields that are otherwise private on PedLodManager.PedEntry;
// never consulted by any existing behavior.
public readonly record struct PedLodDiag(
    int Id, PedDrModel Model, bool HighIndexValid,
    double StateEnteredAt, double OutsideSince, int RouteVertexCount, Sim.Core.Orca.Vec2 Pos);

public sealed class PedLodManager
{
    private sealed class PedEntry
    {
        public required int Id;
        public required Vec2 Destination;
        public required double MaxSpeed;
        public required double Radius;

        public PedDrModel Model = PedDrModel.PathArc;

        // The polyline currently being followed: the PathArc leg's polyline when Low, the navmesh
        // steering route (set once, at promotion) when High.
        //
        // C3 (docs/EXTERNAL-NET-LOADING-DESIGN.md §3.4): assigning `Path` INVALIDATES the cached
        // elevation channel below. Done through a property rather than a plain field on purpose -- the
        // path is reassigned at five separate places (spawn, promote, demote, weave-resume, reroute) and
        // a cache that had to be refreshed by hand at each of them would eventually be missed, leaving a
        // ped's height stuck on the route it used to be walking.
        private IReadOnlyList<Vec2> _path = Array.Empty<Vec2>();

        public IReadOnlyList<Vec2> Path
        {
            get => _path;
            set
            {
                _path = value;
                PathSurfaces = null;   // provenance belongs to the path it came from
                PathZ = null;
                PathZGeometry = Array.Empty<Vec2>();
                PathZValid = false;
            }
        }

        public double PathStartTime;

        // C3: per-vertex elevation for `Path`, index-aligned with it, filled LAZILY on the first
        // elevation query after a path change (see PedLodManager.ElevationOf) and null on a 2-D net.
        // `PathZValid` distinguishes "not computed yet" from the legitimate "computed, and this net has
        // no elevation" -- without it a 2-D net would recompute an all-null answer on every query.
        // Per-vertex OPAQUE surface ids for `Path`, from the router's provenance-carrying FindPath, or
        // null when the provider offers none. Cleared with the path (see the setter above), because a
        // stale mapping would silently read heights off the previous route's surfaces.
        public IReadOnlyList<int>? PathSurfaces;

        public IReadOnlyList<double>? PathZ;
        public bool PathZValid;

        // The polyline `PathZ` is index-aligned WITH -- `Path` for most models, but a lively ped's
        // timeline walk geometry instead (see PedLodManager.ElevationGeometryOf). Stored rather than
        // re-derived so the projection can never be run against a different list than the channel.
        public IReadOnlyList<Vec2> PathZGeometry = Array.Empty<Vec2>();

        // LIVE-PROD-1a: set when this ped is a LIVELY low-power ped (Model == ActivityTimeline) -- its
        // low-power pose/velocity come from Timeline.PoseAt/VelocityAt instead of PathArcMotion. Null for
        // an ordinary PathArc ped (the whole population before liveliness is enabled), so every branch
        // that special-cases it is inert and the PathArc path stays bit-identical.
        public ActivityTimeline? Timeline;

        // P1-2 (evac panic, docs/PEDESTRIAN-DESIGN.md §6): when true this ped is PINNED high-power --
        // it promotes on the next step regardless of any InterestSource, and never demotes while pinned.
        // Default false -> the interest-field-driven promotion path is exactly as before (bit-identical).
        public bool ForcedHighPower;

        // W4 (docs/PEDESTRIAN-WEAVE-PRODUCTION-DESIGN.md): the ped's deterministic-weave seeds, kept across a
        // promote->demote so a demoted weaving ped RESUMES the weave (emits a weaving ActivityTimeline resume
        // leg) rather than a flat PathArc leg. 0 == not a weaving ped -> demote stays exactly as before.
        public ulong WeaveSeed;
        public ulong WeaveGlobalSeed;

        public OrcaHandle HighIndex = OrcaHandle.Invalid;    // handle into the persistent high-power OrcaCrowd, or Invalid when Low

        public double StateEnteredAt;             // sim time this ped entered its CURRENT LOD state
        public double OutsideSince = double.NaN;   // sim time since continuously outside every demote
                                                    // radius (High only); NaN = currently inside one
    }

    private readonly IPedNavigation _navigation;
    private readonly PedPublisher _publisher;
    private readonly ILocalSteering _steering;
    private readonly double _arriveRadius;
    private readonly double _dwellSeconds;

    private readonly Dictionary<int, PedEntry> _peds = new();

    // Persistent for the manager's whole lifetime (P0-3) -- see class remarks. Never replaced.
    private readonly OrcaCrowd _highCrowd;
    private readonly PedRouteController _highController;

    // The high-power crowd's footprints, exposed read-only so a car/pedestrian coupling can wire it into
    // `Engine.CrowdSource` (the live-city demo: cars yield to promoted peds -- e.g. peds promoted onto a
    // crossing). This is the SAME persistent `_highCrowd` the manager owns (`OrcaCrowd : ICrowdFootprintSource`),
    // so only currently-promoted (FreeKinematic) peds are visible to the engine -- low-power/weave peds are
    // never in it, by design. Inert for every existing consumer (nothing reads this unless it opts in).
    public Sim.Core.Bridge.ICrowdFootprintSource HighPowerFootprints => _highCrowd;
    private bool _useParallelHighCrowd;
    private bool _useRegionDecompHighCrowd;

    // Live high-power ped count. NOT the same as `_highCrowd.Count` any more: OrcaCrowd.Count is a
    // high-water mark of slots ever allocated (P0-1), so it stays at its peak even after every
    // currently-high ped demotes, whereas this is decremented on every demotion -- the accurate
    // "is anyone currently high-power" signal Step() and HighPowerCount both need.
    private int _highPowerLiveCount;

    public bool UseParallelHighCrowd
    {
        get => _useParallelHighCrowd;
        set
        {
            _useParallelHighCrowd = value;
            _highCrowd.UseParallelStep = value;
        }
    }

    // P6-2 (docs/PEDESTRIAN-P6-2-REGION-DESIGN.md): opt in the high-power crowd to spatial region
    // decomposition -- the cache-local parallel plan that raises ped per-core throughput (the combined-load
    // GO). Bit-identical to serial (OrcaRegionDecompositionTests); default off, so the manager's behaviour is
    // unchanged unless a caller enables it. Takes precedence over UseParallelHighCrowd on the underlying crowd.
    public bool UseRegionDecompositionHighCrowd
    {
        get => _useRegionDecompHighCrowd;
        set
        {
            _useRegionDecompHighCrowd = value;
            _highCrowd.UseRegionDecomposition = value;
        }
    }

    // P6-2-4 tuning passthrough: region cell side = this multiple of NeighbourDist on the high-power crowd.
    public double HighCrowdRegionCellSizeMultiplier
    {
        get => _highCrowd.RegionCellSizeMultiplier;
        set => _highCrowd.RegionCellSizeMultiplier = value;
    }

    public PedLodManager(
        IPedNavigation navigation,
        PedPublisher publisher,
        double arriveRadius = 0.3,
        double dwellSeconds = 1.0,
        ILocalSteering? steering = null)
    {
        _navigation = navigation;
        _publisher = publisher;
        _arriveRadius = arriveRadius;
        _dwellSeconds = dwellSeconds;
        _steering = steering ?? new WaypointFollower();

        // P0-4 (docs/PEDESTRIAN-POC7C-FINDINGS.md follow-up hypothesis; docs/PEDESTRIAN-DESIGN.md §9):
        // the persistent high-power crowd was constructed bare (UseSpatialHash defaults to false), so
        // every Plan() neighbour gather brute-force-scanned the WHOLE crowd -- O(n^2) for the ~10k
        // high-power agents this manager is built to hold at scale. UseSpatialHash is a proven
        // bit-identical pre-filter (OrcaSpatialHashTests): it changes candidate discovery from a full
        // scan to a 3x3-cell gather, sorted to the SAME order the brute-force scan would visit, so the
        // neighbour set (and hence every trajectory) is unchanged -- only the wall-clock cost drops.
        // This manager never calls AddObstacle on `_highCrowd` (no static walls in the LOD population),
        // so UseObstacleSpatialIndex is left off (inert either way, but there is nothing for it to
        // accelerate here).
        _highCrowd = new OrcaCrowd { UseSpatialHash = true };
        _highController = new PedRouteController(_highCrowd, _steering, _arriveRadius);
    }

    // Registers a new ped as low-power (PathArc), following `path` at `maxSpeed` from `now`.
    // `path[^1]` is treated as the ped's destination (used to re-route on later promote/demote).
    // Publishes the spawn PathArcRecord (the "path sent once").
    public int AddPed(
        int id, IReadOnlyList<Vec2> path, double maxSpeed, double radius, double now,
        IReadOnlyList<int>? pathSurfaces = null)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("A ped's initial path must have at least one point.", nameof(path));
        }

        var entry = new PedEntry
        {
            Id = id,
            Destination = path[^1],
            MaxSpeed = maxSpeed,
            Radius = radius,
            Path = path,
            PathSurfaces = pathSurfaces,
            PathStartTime = now,
            StateEnteredAt = now,
        };

        _peds.Add(id, entry);
        // C4: publish the path's elevation alongside it, so the remote surface reconstructs the same
        // height the in-process one reports. Null on a 2-D net => a kind-4 frame => byte-identical wire.
        _publisher.PublishPathArc(id, path, now, maxSpeed, now, ElevationChannelFor(path, pathSurfaces));
        return id;
    }

    // LIVE-PROD-1a (docs/PEDESTRIAN-LIVELINESS-DESIGN.md §4, §10): registers a LIVELY low-power ped whose
    // pose is `timeline` (Walk legs plus Pause/Dwell/Interact beats) evaluated by ActivityTimeline.PoseAt,
    // rather than a bare PathArc leg. It is still low-power and O(1)/step (PoseAt is a pure function of
    // time), and still server==IG: the whole timeline is broadcast ONCE here (ActivityTimelineRecord,
    // mirroring AddPed's "path sent once") and the IG reconstructs pose+visibility by calling the same
    // PoseAt. `timeline`'s final pose is treated as the destination (used to re-route on promote/demote
    // and for demand-side arrival). The ped can promote to a full reactive OrcaCrowd agent exactly like a
    // PathArc ped -- see Step's promotion branch, which carries PoseAt/VelocityAt forward.
    public int AddPedLively(int id, ActivityTimeline timeline, double maxSpeed, double radius, double now)
    {
        var entry = new PedEntry
        {
            Id = id,
            Destination = timeline.PoseAt(timeline.EndTime).Pos,
            MaxSpeed = maxSpeed,
            Radius = radius,
            Model = PedDrModel.ActivityTimeline,
            Timeline = timeline,
            PathStartTime = now,
            StateEnteredAt = now,
            WeaveSeed = timeline.Seed,
            WeaveGlobalSeed = timeline.GlobalSeed,
        };

        _peds.Add(id, entry);
        _publisher.PublishActivityTimeline(id, timeline, now);
        _publisher.PublishSwitch(id, PedDrModel.PathArc, PedDrModel.ActivityTimeline, now);
        return id;
    }

    // Re-anchor a provenance list onto the path `ReanchorAt` produced. That method prepends the anchor
    // and drops a leading routed vertex coincident with it, so the surface list must undergo the same
    // shift; the prepended anchor inherits the first surviving vertex's surface, which is the one the
    // ped is standing on. Returns null when there was no provenance to begin with, or when the two
    // lengths cannot be reconciled -- a wrong-length list is worse than none, since it would silently
    // read heights off the wrong surfaces.
    private static IReadOnlyList<int>? ReanchorSurfaces(
        IReadOnlyList<Vec2> routed, IReadOnlyList<int>? routedSurfaces, IReadOnlyList<Vec2> newPath)
    {
        if (routedSurfaces is null || routedSurfaces.Count != routed.Count || newPath.Count == 0)
        {
            return null;
        }

        var dropped = routed.Count - (newPath.Count - 1);
        if (dropped < 0 || dropped > routed.Count)
        {
            return null;
        }

        var result = new int[newPath.Count];
        result[0] = routedSurfaces[Math.Min(dropped, routedSurfaces.Count - 1)];
        for (var i = 1; i < newPath.Count; i++)
        {
            var src = dropped + i - 1;
            result[i] = src < routedSurfaces.Count ? routedSurfaces[src] : routedSurfaces[^1];
        }

        return result;
    }

    // W4: force a routed path to START exactly at `anchor` (the frozen demote pose), so a resume leg's
    // pose at arc 0 IS the ped's current position -- machine-precision no-pop across the LOD switch. Drops
    // any leading routed point that coincides with the anchor (avoids a zero-length first segment).
    private static IReadOnlyList<Vec2> ReanchorAt(IReadOnlyList<Vec2> routed, Vec2 anchor)
    {
        var pts = new List<Vec2>(routed.Count + 1) { anchor };
        foreach (var p in routed)
        {
            if ((p - anchor).Abs > 1e-9 || pts.Count > 1)
            {
                pts.Add(p);
            }
        }

        if (pts.Count < 2)
        {
            pts.Add(anchor); // degenerate route -- keep a valid 2-point leg
        }

        return pts;
    }

    // #4b (docs/LIVE-CITY-PED-LOD-LIFECYCLE-DESIGN.md §3.1): route from `pos` to `destination` via the nav
    // graph; if that returns null -- `pos` has drifted OFF the walkable graph, the case that made the old
    // `?? new[] { pos, destination }` fallback cut a single straight line across off-route -- RECOVER onto
    // `lastRoute` (the ped's retained on-graph polyline to this SAME destination: its low-power path at a
    // promotion, its steering route at a demotion). Splice from `pos` to the nearest vertex on that polyline
    // and follow it to the end, yielding a multi-segment on-graph resume path instead of a beeline. Behaviour
    // changes ONLY on the null path (rare -- SumoNavMesh usually projects a slightly-off pose back onto the
    // graph); when FindPath succeeds the result is returned verbatim, so the common path is byte-identical.
    // Falls back to the original beeline only when `lastRoute` is itself degenerate (e.g. a prior straight-
    // line fallback), so this is never worse than before.
    private IReadOnlyList<Vec2> RecoverRoute(
        Vec2 pos, Vec2 destination, IReadOnlyList<Vec2> lastRoute, out IReadOnlyList<int>? surfaces)
    {
        var routed = _navigation.FindPath(pos, destination, out surfaces);
        if (routed is not null)
        {
            return routed;
        }

        // Every fallback below SPLICES or BEELINES rather than routing, so no provenance survives --
        // null it rather than let the router's ids be mistaken for the spliced path's.
        surfaces = null;

        if (lastRoute.Count >= 2)
        {
            var best = 0;
            var bestD2 = double.PositiveInfinity;
            for (var k = 0; k < lastRoute.Count; k++)
            {
                var d2 = (lastRoute[k] - pos).AbsSq;
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    best = k;
                }
            }

            var recovered = new List<Vec2>(lastRoute.Count - best + 1) { pos };
            for (var k = best; k < lastRoute.Count; k++)
            {
                if ((lastRoute[k] - recovered[^1]).Abs > 1e-9)
                {
                    recovered.Add(lastRoute[k]);
                }
            }

            if (recovered.Count >= 2)
            {
                return recovered;
            }
        }

        return new[] { pos, destination };
    }

    // ADDITIVE (P2-3, docs/PEDESTRIAN-TASKS.md; docs/PEDESTRIAN-NAVMESH-CONTRACT.md): removes a ped
    // entirely -- the "arrived at its OD destination, despawn" case a demand generator needs, distinct
    // from a demotion (which keeps the ped, just switches its DR model). If the ped is currently
    // High-power (FreeKinematic), releases its OrcaCrowd handle and route exactly like a demotion's
    // removal side (P0-3) -- every OTHER high-power ped's handle/route/waypoint cursor is untouched --
    // so a despawn never leaks a crowd slot (HighCrowdSlotHighWater may still record the slot as ever-
    // allocated, but HighPowerCount/live occupancy drops immediately and the slot is free-listed for
    // reuse). If Low-power, PathArc motion is a pure function of (path, startTime, speed, now) with no
    // crowd-side state at all, so dropping the dictionary entry is the whole removal. Inert (no-op) if
    // `id` is not currently registered, mirroring OrcaCrowd.Remove / PedRouteController.RemoveRoute's
    // established "removing something already gone is harmless" convention.
    public void RemovePed(int id)
    {
        if (!_peds.TryGetValue(id, out var e))
        {
            return;
        }

        if (e.Model == PedDrModel.FreeKinematic)
        {
            _highController.RemoveRoute(e.HighIndex);
            _highCrowd.Remove(e.HighIndex);
            _highPowerLiveCount--;
        }

        _peds.Remove(id);
    }

    public PedDrModel ModelOf(int id) => _peds[id].Model;

    // P1-2 (docs/PEDESTRIAN-DESIGN.md §6 evac panic; the PedestrianWorld facade's `SetForcedHighPower`):
    // pin/unpin a ped to high-power. While pinned it promotes on the next Step regardless of any
    // InterestSource and never demotes; unpinning lets it demote normally once it is outside every
    // demote radius. Inert (no-op) if `id` is not registered, mirroring RemovePed's convention. The
    // actual PathArc->FreeKinematic Add happens in the next Step's promotion pass (never mid-call),
    // preserving the "structural mutations are deferred to Step" discipline the whole manager relies on.
    public void SetForcedHighPower(int id, bool on)
    {
        if (_peds.TryGetValue(id, out var e))
        {
            e.ForcedHighPower = on;
        }
    }

    // LIVE-PROD-1b (docs/PEDESTRIAN-LIVELINESS-DESIGN.md §4, §10): the ped's current animation tag, so
    // a caller (a Sim.Viz demo, or eventually an IG) can pick a disc kind / anim clip without
    // re-evaluating PoseAt itself or reaching into PedEntry (private). An ActivityTimeline ped reports
    // its live `PoseAt(now).AnimTag` (walk/pause/dwell tag, per the timeline); every other model (plain
    // PathArc, FreeKinematic) has no richer per-step state than "in motion", so it reports
    // ActivityTimeline.WalkAnimTag -- read-only, additive, and touches no existing behavior.
    public string AnimTagOf(int id, double now)
    {
        var e = _peds[id];
        return e.Model == PedDrModel.ActivityTimeline ? e.Timeline!.PoseAt(now).AnimTag : ActivityTimeline.WalkAnimTag;
    }

    public int HighPowerCount => _highPowerLiveCount;

    // Diagnostic-only (P0-4 investigation, docs/PEDESTRIAN-POC7C-FINDINGS.md follow-up hypothesis):
    // the persistent `_highCrowd`'s slot high-water mark (OrcaCrowd.Count -- the number of slots EVER
    // allocated, not the live count), for benchmarks/tests to compare against HighPowerCount and
    // quantify how far the high-water mark has drifted above the live count after a churn spike. Never
    // consulted by Step() itself; purely observability.
    public int HighCrowdSlotHighWater => _highCrowd.Count;

    // Diagnostic-only, ADDITIVE (live-city ped LOD lifecycle investigation): a read-only snapshot of
    // every ped's internal LOD state, otherwise private on `PedEntry`. Never consulted by Step() or any
    // other existing behavior -- purely observability, for a headless trace tool to correlate
    // server-side LOD transitions against the wire (see Sim.Viz --live-city-pedtrace).
    public IEnumerable<PedLodDiag> DiagnosticSnapshot(double now)
    {
        var ids = new List<int>(_peds.Keys);
        ids.Sort();
        foreach (var id in ids)
        {
            var e = _peds[id];
            yield return new PedLodDiag(
                id,
                e.Model,
                e.HighIndex.IsValid,
                e.StateEnteredAt,
                e.OutsideSince,
                e.Path.Count,
                PositionOf(id, now));
        }
    }

    // C3 (docs/EXTERNAL-NET-LOADING-DESIGN.md §3.4, -TASKS.md C3): the ped's current SURFACE ELEVATION
    // -- the render-side companion to `PositionOf` below, and the value `LiveCitySim.Sample()` puts in
    // `LiveCityPed.Z`.
    //
    // Resolved along THE PED'S OWN PATH, never by searching the network: the path's per-vertex elevation
    // comes from `IPedNavigation.ElevationsAlong` (computed once per path and cached on the entry), and
    // the height is read off it at the ped's current position. So a ped on a bridge follows the bridge,
    // because the path it is walking IS the bridge's.
    //
    // WHY BY PROJECTION ONTO THAT PATH rather than by the arc-length cursor: the three LOD models locate
    // a ped three different ways -- `PathArc` by arc length, `ActivityTimeline` by a timeline that
    // includes PAUSES (so arc length and elapsed time part company), and `FreeKinematic` by the ORCA
    // crowd's own committed position (which is deliberately not on the polyline at all while avoiding
    // someone). One projection onto the ped's own ~10-30 vertex polyline is correct for all three, where
    // an arc-length lerp would be right only for the first. It is a handful of segment tests against a
    // list the ped already holds -- not a spatial query, and not the nearest-lane search this design
    // explicitly rejects.
    //
    // Returns 0.0 on a 2-D net (no elevation channel), which is exactly the value this surface reported
    // before elevation existed.
    public double ElevationOf(int id, double now)
    {
        var e = _peds[id];

        if (!e.PathZValid)
        {
            // WHICH polyline carries this ped's geometry depends on its model, and getting this wrong is
            // silent: a LIVELY (ActivityTimeline) ped never populates `Path` at all -- `AddPedLively`
            // builds it straight from a timeline whose WalkSegments hold the geometry -- so projecting
            // against `Path` would have returned 0.0 for the entire lively population while looking
            // perfectly correct for everyone else. (Measured on the 3-D fixture before this branch
            // existed: 85 of 160 live peds reported z = 0.)
            var geometry = ElevationGeometryOf(e);

            // Provenance applies only when the geometry IS the ped's own `Path`; a lively ped's
            // timeline geometry is a different (possibly re-sliced) list, so its ids would not line up
            // and it falls back to proximity -- correct except where surfaces stack.
            var surfaces = ReferenceEquals(geometry, e.Path) ? e.PathSurfaces : null;
            e.PathZ = geometry.Count > 0 ? _navigation.ElevationsAlong(geometry, surfaces) : null;
            e.PathZGeometry = geometry;
            e.PathZValid = true;

            // An all-zero channel (the interface's flat default, i.e. a provider with no elevation model)
            // is dropped to null so the common 2-D case costs one null check per query instead of a
            // projection walk that can only ever return 0.
            if (e.PathZ is { Count: > 0 } zs)
            {
                var anyNonZero = false;
                for (var i = 0; i < zs.Count; i++)
                {
                    if (zs[i] != 0.0)
                    {
                        anyNonZero = true;
                        break;
                    }
                }

                if (!anyNonZero)
                {
                    e.PathZ = null;
                }
            }
        }

        if (e.PathZ is null)
        {
            return 0.0;
        }

        return Sim.Pedestrians.Navigation.PolylineElevation.AtNearestPoint(
            e.PathZGeometry, e.PathZ, PositionOf(id, now));
    }

    // C4: the elevation channel to publish with a PathArc leg -- null (rather than an all-zero array)
    // on a 2-D net, which is what makes the publisher emit the original kind-4 frame and keeps the wire
    // byte-identical there.
    private IReadOnlyList<double>? ElevationChannelFor(IReadOnlyList<Vec2> path, IReadOnlyList<int>? surfaces = null)
    {
        if (path.Count == 0)
        {
            return null;
        }

        var zs = _navigation.ElevationsAlong(path, surfaces);
        for (var i = 0; i < zs.Count; i++)
        {
            if (zs[i] != 0.0)
            {
                return zs;
            }
        }

        return null;
    }

    // The polyline a ped's elevation is resolved against: its `Path` for a PathArc or FreeKinematic ped,
    // and the concatenation of its timeline's Walk legs for a lively (ActivityTimeline) one, which is
    // where that ped's geometry actually lives. Pause/Dwell/Interact legs contribute no geometry -- they
    // hold position at wherever the preceding Walk ended, which is already on the concatenation.
    private static IReadOnlyList<Vec2> ElevationGeometryOf(PedEntry e)
    {
        if (e.Timeline is not { } timeline)
        {
            return e.Path;
        }

        List<Vec2>? combined = null;
        IReadOnlyList<Vec2>? only = null;

        foreach (var segment in timeline.Segments)
        {
            if (segment is not WalkSegment walk || walk.Path.Count == 0)
            {
                continue;
            }

            if (only is null)
            {
                only = walk.Path; // the overwhelmingly common single-Walk case allocates nothing
                continue;
            }

            combined ??= new List<Vec2>(only);
            combined.AddRange(walk.Path);
        }

        if (combined is not null)
        {
            return combined;
        }

        return only ?? e.Path;
    }

    // The ped's current world position: for Low-power this is the pure PathArcMotion function
    // evaluated AT `now` (so it can be queried for any `now`, not just at a Step boundary); for
    // High-power this is the last-committed OrcaCrowd position (the truth only advances via Step).
    public Vec2 PositionOf(int id, double now)
    {
        var e = _peds[id];
        return e.Model switch
        {
            PedDrModel.FreeKinematic => _highCrowd.Position(e.HighIndex),
            PedDrModel.ActivityTimeline => e.Timeline!.PoseAt(now).Pos,
            _ => PathArcMotion.PositionAt(e.Path, e.PathStartTime, e.MaxSpeed, now),
        };
    }

    // Advances every ped by `dt`, from time `now` to `now + dt`:
    //   1. Evaluate promotion/demotion (pure function of frozen ped/source positions + dwell timers),
    //      in ascending ped-id order.
    //   2. Apply transitions: flip PedDrModel, Add/Remove the ONE affected ped's OrcaCrowd handle and
    //      PedRouteController route (P0-3 -- O(1) per switch, no rebuild), emit lifecycle events
    //      (DrSwitchEvent, and on demotion a fresh PathArcRecord).
    //   3. Advance motion: low-power peds are a pure function of time (nothing to "step"); the
    //      high-power crowd is stepped once, avoiding `externalEntities`.
    //   4. Publish this step's wire traffic: a FreeKinematicSample per high-power ped, a (rate-limited)
    //      HeartbeatEvent per low-power ped.
    public void Step(
        double now,
        double dt,
        InterestField interestField,
        IReadOnlyList<WorldDisc> externalEntities)
    {
        // Freeze the interest field's spatial index for this whole step (P1-1, docs §5: "Promotion is
        // a pure function of frozen state (source positions are start-of-step)") -- every ped queried
        // below sees the exact same source snapshot, regardless of evaluation order. See
        // InterestField.RebuildIndex remarks for why this is O(sources), not O(peds).
        interestField.RebuildIndex();

        var ids = new List<int>(_peds.Keys);
        ids.Sort(); // ascending ped-id order -- deterministic evaluation and application

        var frozenPos = new Dictionary<int, Vec2>(ids.Count);
        foreach (var id in ids)
        {
            frozenPos[id] = PositionOf(id, now);
        }

        var toPromote = new List<int>();
        var toDemote = new List<int>();
        foreach (var id in ids)
        {
            var e = _peds[id];
            var pos = frozenPos[id];
            var stateAge = now - e.StateEnteredAt;

            // Low-power = PathArc OR ActivityTimeline (a lively low-power ped, LIVE-PROD-1a); both promote
            // the same way. FreeKinematic is the only high-power model. Stationary is not used here.
            if (e.Model != PedDrModel.FreeKinematic)
            {
                // A pinned ped (P1-2 SetForcedHighPower, evac panic) promotes immediately -- no interest
                // source and no minimum-dwell gate; otherwise the ordinary interest-field promotion.
                if (e.ForcedHighPower || (stateAge >= _dwellSeconds && interestField.Query(pos).AnyWithinPromote))
                {
                    toPromote.Add(id);
                }
            }
            else if (e.Model == PedDrModel.FreeKinematic)
            {
                // A pinned ped never demotes while pinned.
                if (!e.ForcedHighPower && interestField.Query(pos).AllOutsideDemote)
                {
                    if (double.IsNaN(e.OutsideSince))
                    {
                        e.OutsideSince = now;
                    }

                    if (stateAge >= _dwellSeconds && now - e.OutsideSince >= _dwellSeconds)
                    {
                        toDemote.Add(id);
                    }
                }
                else
                {
                    e.OutsideSince = double.NaN; // back inside someone's demote radius: cancel the countdown
                }
            }
        }

        // Promotions: PathArc -> FreeKinematic. Adds ONLY this ped to the persistent high-power
        // OrcaCrowd (carrying its frozen position + PathArc-derived velocity forward) and registers
        // ONLY its route -- every already-high ped's handle/route is untouched (P0-3).
        foreach (var id in toPromote)
        {
            var e = _peds[id];
            var pos = frozenPos[id];
            var velocity = e.Model == PedDrModel.ActivityTimeline
                ? e.Timeline!.VelocityAt(now)
                : PathArcMotion.VelocityAt(e.Path, e.PathStartTime, e.MaxSpeed, now);
            var steeringPath = RecoverRoute(pos, e.Destination, e.Path, out var steeringSurfaces);

            e.Model = PedDrModel.FreeKinematic;
            e.StateEnteredAt = now;
            e.OutsideSince = double.NaN;
            e.Path = steeringPath;          // clears the old provenance
            e.PathSurfaces = steeringSurfaces; // ...then attach this route's own
            e.Timeline = null; // now a reactive high-power agent; a later demotion resumes as plain PathArc

            var handle = _highCrowd.Add(pos, e.Radius, e.MaxSpeed, goal: pos, velocity: velocity);
            _highController.AddRoute(handle, steeringPath, e.MaxSpeed);
            e.HighIndex = handle;
            _highPowerLiveCount++;

            _publisher.PublishSwitch(id, PedDrModel.PathArc, PedDrModel.FreeKinematic, now);
        }

        // Demotions: FreeKinematic -> PathArc. Re-routes from the ped's CURRENT (frozen) position to
        // its destination via IPedNavigation (see the class remarks for why re-route rather than
        // resume), then Removes ONLY this ped's OrcaCrowd handle and route -- every other high-power
        // ped's handle/route/waypoint cursor is untouched (P0-3).
        foreach (var id in toDemote)
        {
            var e = _peds[id];
            var pos = frozenPos[id];
            var routed = RecoverRoute(pos, e.Destination, e.Path, out var routedSurfaces);
            // Re-anchor the resume leg EXACTLY at the frozen high-power pose, so the low-power pose at the
            // demote instant is the ped's current position to machine precision (no pop across the LOD switch).
            var newPath = ReanchorAt(routed, pos);
            // ReanchorAt prepends the anchor and may drop a coincident leading vertex, so the provenance
            // must be re-anchored the same way or it would be off by one against the new path.
            var newSurfaces = ReanchorSurfaces(routed, routedSurfaces, newPath);

            _highController.RemoveRoute(e.HighIndex);
            _highCrowd.Remove(e.HighIndex);
            _highPowerLiveCount--;

            e.StateEnteredAt = now;
            e.PathStartTime = now;
            e.HighIndex = OrcaHandle.Invalid;

            if (e.WeaveSeed != 0)
            {
                // W4: a weaving ped RESUMES the deterministic weave -- emit a single-Walk ActivityTimeline
                // resume leg (exact ActivityTimelineWire, unlike the quantized PathArc record) carrying the
                // ped's own weave seed + the baked per-vertex half-widths. The Offset start-taper is 0 at the
                // re-anchored start, so the pose leaves `pos` with no pop and weaves back in over the lead-in.
                var widths = _navigation.HalfWidthsAlong(newPath);
                var resume = new ActivityTimeline(
                    now,
                    new ActivitySegment[] { new WalkSegment(newPath, e.MaxSpeed, widths, ElevationChannelFor(newPath, newSurfaces)) },
                    e.WeaveSeed, e.WeaveGlobalSeed);

                e.Model = PedDrModel.ActivityTimeline;
                e.Timeline = resume;
                e.Path = newPath;
                e.PathSurfaces = newSurfaces;
                _publisher.PublishActivityTimeline(id, resume, now);
                _publisher.PublishSwitch(id, PedDrModel.FreeKinematic, PedDrModel.ActivityTimeline, now);
            }
            else
            {
                e.Model = PedDrModel.PathArc;
                e.Path = newPath;
                e.PathSurfaces = newSurfaces;
                _publisher.PublishPathArc(id, newPath, now, e.MaxSpeed, now, ElevationChannelFor(newPath, newSurfaces));
                _publisher.PublishSwitch(id, PedDrModel.FreeKinematic, PedDrModel.PathArc, now);
            }
        }

        if (_highPowerLiveCount > 0)
        {
            var discs = new WorldDisc[externalEntities.Count];
            for (var i = 0; i < discs.Length; i++)
            {
                discs[i] = externalEntities[i];
            }

            _highCrowd.SetExternalObstacles(discs);
            _highController.Update();
            _highCrowd.Step(dt);
        }

        var newNow = now + dt;

        // Emit this step's FreeKinematic samples as one CONTIGUOUS run (all before any heartbeat), then the
        // low-power heartbeats. PedReplicationPublisher batches CONSECUTIVE same-time FreeKinematicSamples
        // into one crowd frame; a non-sample event (a heartbeat) interleaved among the samples forces a
        // premature FlushCrowdFrame, fragmenting one step's crowd into several frames -- and the receiver
        // only applies the LATEST crowd frame, so every high-power ped except those in the final fragment
        // is never updated on the wire and renders frozen (docs/LIVE-CITY-PED-LOD-LIFECYCLE-DESIGN.md §2,
        // the #3 producer half). Splitting the single interleaved loop into two ordered passes restores the
        // publisher's documented "samples are consecutive => one crowd frame per step" contract. Both passes
        // keep ascending-id order, so the wire event sequence stays fully deterministic; heartbeats carry no
        // pose, so their relative position does not affect any reconstruction.
        foreach (var id in ids)
        {
            var e = _peds[id];
            if (e.Model == PedDrModel.FreeKinematic)
            {
                _publisher.PublishSample(id, newNow, _highCrowd.Position(e.HighIndex), _highCrowd.Velocity(e.HighIndex));
            }
        }

        foreach (var id in ids)
        {
            var e = _peds[id];
            if (e.Model != PedDrModel.FreeKinematic)
            {
                _publisher.MaybePublishHeartbeat(id, newNow);
            }
        }
    }

}
