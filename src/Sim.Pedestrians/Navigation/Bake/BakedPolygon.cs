using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation.Bake;

// The source kind a baked polygon came from (docs/PEDESTRIAN-DESIGN.md §4, POC-1a "SUMO-geometry
// bake" provider). Purely informational -- routing/containment treat all kinds identically.
public enum BakedPolygonKind
{
    WalkingArea,
    Crossing,

    // P2-1: one whole-polyline mitred strip per sidewalk lane (was per-segment before P2-1 -- the
    // enum member kept its name in spirit but now bakes to exactly one polygon per PedLane, not one
    // per shape segment; see WalkablePolygonBaker's remarks).
    SidewalkSegment,
    WalkablePolygon,
}

// One walkable polygon baked from PedNetwork geometry (WalkablePolygonBaker.Bake). `Index` is the
// polygon's stable position in the deterministic bake order -- it is also this polygon's node id
// in the SumoNavMesh adjacency graph, so A*'s "tie-break by polygon index" is exactly "tie-break
// by Index". `Vertices` is an implicitly-closed ring (see PolygonGeometry).
//
// `Spine` (P2-1): the ORIGINAL centreline polyline this polygon was buffered from, set only for
// sidewalk polygons (null otherwise). A whole-lane mitred strip (WalkablePolygonBaker) can be a
// NON-CONVEX polygon once its lane bends, so SumoNavMesh must not assume a straight line between
// any two points inside the same polygon stays walkable (true for the small convex crossing/
// walkingarea/quad polygons, false in general for a bent strip) -- see SumoNavMesh.FindPath, which
// threads the path through `Spine`'s interior vertices instead of a direct segment whenever this has
// 3+ points (a genuine bend). A straight (2-point) lane's Spine has exactly 2 points -- no interior
// vertices to thread through -- so FindPath's direct-segment fast path is unaffected there.
public sealed record BakedPolygon(
    int Index,
    string Id,
    BakedPolygonKind Kind,
    IReadOnlyList<Vec2> Vertices,
    IReadOnlyList<Vec2>? Spine = null)
{
    public Vec2 Centroid { get; } = PolygonGeometry.VertexAverage(Vertices);
}
