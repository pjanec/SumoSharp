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
        public IReadOnlyList<Vec2> Path = Array.Empty<Vec2>();
        public double PathStartTime;

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
    private bool _useParallelHighCrowd;

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
        _highCrowd = new OrcaCrowd();
        _highController = new PedRouteController(_highCrowd, _steering, _arriveRadius);
    }

    // Registers a new ped as low-power (PathArc), following `path` at `maxSpeed` from `now`.
    // `path[^1]` is treated as the ped's destination (used to re-route on later promote/demote).
    // Publishes the spawn PathArcRecord (the "path sent once").
    public int AddPed(int id, IReadOnlyList<Vec2> path, double maxSpeed, double radius, double now)
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
            PathStartTime = now,
            StateEnteredAt = now,
        };

        _peds.Add(id, entry);
        _publisher.PublishPathArc(id, path, now, maxSpeed, now);
        return id;
    }

    public PedDrModel ModelOf(int id) => _peds[id].Model;

    public int HighPowerCount => _highPowerLiveCount;

    // The ped's current world position: for Low-power this is the pure PathArcMotion function
    // evaluated AT `now` (so it can be queried for any `now`, not just at a Step boundary); for
    // High-power this is the last-committed OrcaCrowd position (the truth only advances via Step).
    public Vec2 PositionOf(int id, double now)
    {
        var e = _peds[id];
        return e.Model == PedDrModel.FreeKinematic
            ? _highCrowd.Position(e.HighIndex)
            : PathArcMotion.PositionAt(e.Path, e.PathStartTime, e.MaxSpeed, now);
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
        IReadOnlyList<InterestSource> interestSources,
        IReadOnlyList<WorldDisc> externalEntities)
    {
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

            if (e.Model == PedDrModel.PathArc)
            {
                if (stateAge >= _dwellSeconds && AnySourceWithinPromote(interestSources, pos))
                {
                    toPromote.Add(id);
                }
            }
            else if (e.Model == PedDrModel.FreeKinematic)
            {
                if (AllSourcesOutsideDemote(interestSources, pos))
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
            var velocity = PathArcMotion.VelocityAt(e.Path, e.PathStartTime, e.MaxSpeed, now);
            var steeringPath = _navigation.FindPath(pos, e.Destination) ?? new[] { pos, e.Destination };

            e.Model = PedDrModel.FreeKinematic;
            e.StateEnteredAt = now;
            e.OutsideSince = double.NaN;
            e.Path = steeringPath;

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
            var newPath = _navigation.FindPath(pos, e.Destination) ?? new[] { pos, e.Destination };

            _highController.RemoveRoute(e.HighIndex);
            _highCrowd.Remove(e.HighIndex);
            _highPowerLiveCount--;

            e.Model = PedDrModel.PathArc;
            e.Path = newPath;
            e.PathStartTime = now;
            e.StateEnteredAt = now;
            e.HighIndex = OrcaHandle.Invalid;
            _publisher.PublishPathArc(id, newPath, now, e.MaxSpeed, now);
            _publisher.PublishSwitch(id, PedDrModel.FreeKinematic, PedDrModel.PathArc, now);
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
        foreach (var id in ids)
        {
            var e = _peds[id];
            if (e.Model == PedDrModel.FreeKinematic)
            {
                _publisher.PublishSample(id, newNow, _highCrowd.Position(e.HighIndex), _highCrowd.Velocity(e.HighIndex));
            }
            else
            {
                _publisher.MaybePublishHeartbeat(id, newNow);
            }
        }
    }

    private static bool AnySourceWithinPromote(IReadOnlyList<InterestSource> sources, Vec2 pos)
    {
        for (var i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            if ((pos - s.Position).Abs <= s.PromoteRadius)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AllSourcesOutsideDemote(IReadOnlyList<InterestSource> sources, Vec2 pos)
    {
        for (var i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            if ((pos - s.Position).Abs <= s.DemoteRadius)
            {
                return false;
            }
        }

        return true;
    }
}
