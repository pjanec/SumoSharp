using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation.Bake;

// Integration driver (POC-1a): ties the strategic/tactical navigation layers to the operational
// OrcaCrowd (docs/PEDESTRIAN-DESIGN.md §4). Each registered agent holds a path (from
// IPedNavigation.FindPath) and a waypoint cursor; every Update():
//
//   1. Calls ILocalSteering.DesiredVelocity for the agent. OrcaCrowd has no "external preferred
//      velocity" input -- it always steers each agent toward its OWN goal (OrcaCrowd.Plan), so the
//      returned velocity itself is not fed into the crowd. What Update() actually needs from the
//      steering call is its SIDE EFFECT: advancing `waypointIndex` past any waypoint already
//      reached, i.e. deciding WHICH waypoint is now the current steering target.
//   2. Sets the crowd agent's GOAL to that current target (path[waypointIndex]) via
//      OrcaCrowd.SetGoal. OrcaCrowd's own goal-seeking + reciprocal-avoidance solve then turns
//      "walk toward this point" into an actual velocity for the step, negotiating with every other
//      crowd agent exactly as it already does -- this is what gets ORCA avoidance "for free" on
//      top of path-following, per the task description.
//
// Call Update() once per tick BEFORE OrcaCrowd.Step(dt); the caller owns stepping the crowd itself
// (this class only ever reads/sets goals, never calls Step).
//
// P0-3 (docs/PEDESTRIAN-TASKS.md, PEDESTRIAN-DESIGN.md §3(d)/§5): routes are stored in a Route?[]
// slab INDEXED BY OrcaHandle.Index -- mirroring OrcaCrowd's own SoA-plus-free-list convention (P0-1)
// exactly -- instead of the POC-3 List<Route> scanned linearly by handle equality. This makes
// AddRoute/RemoveRoute O(1) (a slot write, no scan, no shift) so PedLodManager can register/retire
// ONE ped's route on promotion/demotion without touching any other agent's entry -- the whole point
// of P0-3 (a promotion used to force PedLodManager to rebuild the entire high-power crowd AND
// re-derive every high-power ped's route, an O(high-power count) cost per membership change; P0-1's
// OrcaCrowd.Add/Remove removed the crowd side of that, this removes the route side). Update() walks
// ascending index 0.._highWater-1 skipping null slots -- the SAME "ascending slot index, skip
// dead/absent" pattern OrcaCrowd.Step/RebuildGrid use, so Update()'s per-step cost is O(currently
// registered routes' index span), not touched by how many promotions/demotions happened to get
// there. Each slot also carries the exact OrcaHandle it was registered for (not just its Index), so
// a caller holding a STALE handle (already RemoveRoute'd, or recycled by OrcaCrowd to a different
// agent since) never silently reads a different agent's route -- RouteAt treats a generation
// mismatch as "not found", exactly the "not a registered route -> vacuous default" contract the
// original List-based lookups already had for an unmatched handle.
//
// Deterministic: Update() always visits slots in ascending index order, no System.Random.
public sealed class PedRouteController
{
    private sealed class Route
    {
        public required OrcaHandle CrowdIndex;
        public required IReadOnlyList<Vec2> Path;
        public required double MaxSpeed;
        public int WaypointIndex;
    }

    private readonly OrcaCrowd _crowd;
    private readonly ILocalSteering _steering;
    private readonly double _arriveRadius;

    // Slab indexed by OrcaHandle.Index; see class remarks. Grown like OrcaCrowd.Grow (double on
    // demand), never shrunk -- a null entry costs one array slot, cheap relative to a Route object.
    private Route?[] _routes = new Route?[16];

    // One past the highest index ever registered -- bounds Update()'s scan so it need not walk the
    // whole (possibly over-allocated) array. Mirrors OrcaCrowd.Count's "high-water mark, not live
    // count" semantics: RemoveRoute never lowers it (the slot is recycled by a later AddRoute at the
    // same index instead, exactly as OrcaCrowd's free list recycles the same crowd slot).
    private int _highWater;

    public PedRouteController(OrcaCrowd crowd, ILocalSteering steering, double arriveRadius)
    {
        _crowd = crowd;
        _steering = steering;
        _arriveRadius = arriveRadius;
    }

    // Registers an already-added crowd agent (the handle OrcaCrowd.Add returned) to follow `path`
    // at `maxSpeed`, and immediately targets its goal at the path's first steering waypoint. O(1)
    // (a slot write); overwrites any stale entry left at the same index by a prior RemoveRoute.
    public void AddRoute(OrcaHandle crowdIndex, IReadOnlyList<Vec2> path, double maxSpeed)
    {
        var i = crowdIndex.Index;
        EnsureCapacity(i + 1);

        var route = new Route { CrowdIndex = crowdIndex, Path = path, MaxSpeed = maxSpeed, WaypointIndex = 0 };
        _routes[i] = route;
        if (i + 1 > _highWater)
        {
            _highWater = i + 1;
        }

        UpdateGoal(route);
    }

    // O(1): retires the route registered for `crowdIndex`'s slot, so Update() no longer touches it
    // and a later AddRoute at the same (recycled) index starts clean. Inert no-op if `crowdIndex` is
    // out of range or does not match what is currently registered there (mirrors OrcaCrowd.Remove's
    // "inert-when-absent/stale" convention) -- called by PedLodManager in the SAME step it Removes
    // the matching OrcaCrowd handle (demotion), so every OTHER agent's route is completely
    // undisturbed (no shifting, no shuffling, no re-deriving).
    public void RemoveRoute(OrcaHandle crowdIndex)
    {
        var i = crowdIndex.Index;
        if (i >= 0 && i < _routes.Length && _routes[i] is { } route && route.CrowdIndex == crowdIndex)
        {
            _routes[i] = null;
        }
    }

    // True once crowdIndex has advanced past the last waypoint of its registered path.
    public bool IsRouteComplete(OrcaHandle crowdIndex)
    {
        var route = RouteAt(crowdIndex);
        return route is null || route.WaypointIndex >= route.Path.Count;
    }

    public int WaypointIndexOf(OrcaHandle crowdIndex)
    {
        var route = RouteAt(crowdIndex);
        return route?.WaypointIndex ?? -1;
    }

    // Advances every registered route's waypoint cursor and (re)targets its crowd goal. Call
    // before OrcaCrowd.Step(dt) each tick. O(_highWater), i.e. proportional to the current
    // registered-route index span, not to how many Add/RemoveRoute calls produced it.
    public void Update()
    {
        for (var i = 0; i < _highWater; i++)
        {
            var route = _routes[i];
            if (route is not null)
            {
                UpdateGoal(route);
            }
        }
    }

    private Route? RouteAt(OrcaHandle crowdIndex)
    {
        var i = crowdIndex.Index;
        if (i < 0 || i >= _routes.Length)
        {
            return null;
        }

        var route = _routes[i];
        return route is not null && route.CrowdIndex == crowdIndex ? route : null;
    }

    private void UpdateGoal(Route route)
    {
        if (route.Path.Count == 0)
        {
            return;
        }

        var position = _crowd.Position(route.CrowdIndex);
        _steering.DesiredVelocity(position, route.Path, ref route.WaypointIndex, route.MaxSpeed, _arriveRadius);

        var targetIndex = Math.Min(route.WaypointIndex, route.Path.Count - 1);
        _crowd.SetGoal(route.CrowdIndex, route.Path[targetIndex]);
    }

    private void EnsureCapacity(int needed)
    {
        if (_routes.Length >= needed)
        {
            return;
        }

        var newCapacity = Math.Max(needed, Math.Max(8, _routes.Length * 2));
        Array.Resize(ref _routes, newCapacity);
    }
}
