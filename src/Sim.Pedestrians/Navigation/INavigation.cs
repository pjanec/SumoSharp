using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation;

// The pedestrian navigation seam (docs/PEDESTRIAN-DESIGN.md §4). Three small interfaces separate the
// three motion layers so the WALKABLE SPACE, the STRATEGIC route, and the TACTICAL steering can each be
// supplied by a different provider — and, crucially, so the owner's production navmesh can drop in later
// as an IWalkableSpace / IPedNavigation implementation without any change above the seam (Principle: no
// double-build). For development we ship two providers behind these seams — a DotRecast navmesh and a
// bake straight from the SUMO pedestrian-network geometry (PedNetwork) — which also proves the seam is
// real, not a shim around a single implementation.
//
// Geometry is 2D (Sim.Core.Orca.Vec2), matching the OrcaCrowd operational layer these feed. A 2.5D /
// multi-level navmesh is a provider concern hidden behind IWalkableSpace; the POC world is planar.

/// The walkable-space provider: owns the walkable geometry and answers point queries against it. This is
/// the seam the operational (ORCA) layer also uses to confine agents (boundary segments become walls).
public interface IWalkableSpace
{
    /// True when world point <paramref name="p"/> lies inside walkable space.
    bool Contains(Vec2 p);

    /// The nearest walkable point to <paramref name="p"/> (identity if already inside). Used to snap a
    /// spawn or a goal onto the walkable area so routing always starts/ends on-mesh.
    Vec2 ClampToWalkable(Vec2 p);

    /// The boundary of walkable space as directed wall segments (interior on the left, RVO2 convention),
    /// for the operational layer to confine agents via OrcaCrowd.AddObstacle. May be empty if a provider
    /// confines by other means; callers must tolerate that.
    IReadOnlyList<WallSegment> BoundarySegments { get; }
}

/// A directed boundary wall segment (a → b). Interior of walkable space is on the left of a→b.
public readonly record struct WallSegment(Vec2 A, Vec2 B);

/// Strategic routing over walkable space: origin+destination → a smooth corridor path.
public interface IPedNavigation
{
    /// Find a path from <paramref name="start"/> to <paramref name="goal"/> as an ordered polyline of
    /// waypoints that lies within walkable space (already funnel/string-pulled to a smooth corridor), or
    /// <c>null</c> when the goal is unreachable. The first point is (near) <paramref name="start"/> and
    /// the last is (near) <paramref name="goal"/>. Deterministic: the same inputs return the same path.
    IReadOnlyList<Vec2>? FindPath(Vec2 start, Vec2 goal);

    /// W2 (docs/PEDESTRIAN-WEAVE-PRODUCTION-DESIGN.md §4): the sidewalk half-width (metres) at each vertex of
    /// `path` -- the per-vertex clamp width the deterministic low-power weave rides within. Default: a safe
    /// 0.5 m everywhere (so a navigation provider that has no width model yields a minimal, always-in-bounds
    /// band); SumoNavMesh overrides it with the baked per-polygon width. Implemented as a default interface
    /// method so the existing providers (DotRecast, the test doubles) need no change.
    IReadOnlyList<double> HalfWidthsAlong(IReadOnlyList<Vec2> path)
    {
        var widths = new double[path.Count];
        Array.Fill(widths, 0.5);
        return widths;
    }

    /// docs/EXTERNAL-NET-LOADING-DESIGN.md §3.4 (C2): the surface elevation (metres, the net's own
    /// vertical datum) at each vertex of `path`, index-aligned with it -- the channel a pedestrian's
    /// rendered height is interpolated from as it walks.
    ///
    /// Default: ALL ZEROS, so a provider with no elevation model (DotRecast, every test double) keeps
    /// today's flat behaviour and needs no edit. Deliberately the same shape and the same rationale as
    /// `HalfWidthsAlong` above -- this follows that precedent rather than inventing a second convention.
    /// `SumoNavMesh` and `SumoRouteGraphNav` override it from the elevation channels
    /// `PedNetworkParser` retains.
    ///
    /// OUTPUT-ONLY: the returned elevations are consumed at the render seam and by no steering, ORCA or
    /// routing decision, which is what keeps every 2-D scenario bit-identical.
    IReadOnlyList<double> ElevationsAlong(IReadOnlyList<Vec2> path)
    {
        return new double[path.Count]; // flat
    }
}

/// Tactical steering: turn a path + current pose into a PREFERRED velocity. This is the single point the
/// two sim-LOD tiers diverge (docs/PEDESTRIAN-DESIGN.md §4/§5): for a HIGH-power agent the result is fed
/// to OrcaCrowd as its <c>pref</c> (avoidance then adjusts it); for a LOW-power agent the result IS the
/// motion (no avoidance), which is exactly why a low-power follower must be deterministic-from-its-path.
public interface ILocalSteering
{
    /// Preferred velocity for an agent at <paramref name="position"/> following <paramref name="path"/>.
    /// <paramref name="waypointIndex"/> is the caller-held progress cursor (index of the waypoint being
    /// steered toward); this call advances it past any waypoint reached within <paramref name="arriveRadius"/>.
    /// The magnitude is capped at <paramref name="maxSpeed"/> and eased toward zero at the final waypoint so
    /// the agent settles instead of oscillating. Returns the zero vector once the path is complete.
    Vec2 DesiredVelocity(
        Vec2 position,
        IReadOnlyList<Vec2> path,
        ref int waypointIndex,
        double maxSpeed,
        double arriveRadius);
}
