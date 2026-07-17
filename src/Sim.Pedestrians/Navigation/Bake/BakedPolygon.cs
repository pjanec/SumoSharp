using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation.Bake;

// The source kind a baked polygon came from (docs/PEDESTRIAN-DESIGN.md §4, POC-1a "SUMO-geometry
// bake" provider). Purely informational -- routing/containment treat all kinds identically.
public enum BakedPolygonKind
{
    WalkingArea,
    Crossing,
    SidewalkSegment,
    WalkablePolygon,
}

// One walkable polygon baked from PedNetwork geometry (WalkablePolygonBaker.Bake). `Index` is the
// polygon's stable position in the deterministic bake order -- it is also this polygon's node id
// in the SumoNavMesh adjacency graph, so A*'s "tie-break by polygon index" is exactly "tie-break
// by Index". `Vertices` is an implicitly-closed ring (see PolygonGeometry).
public sealed record BakedPolygon(
    int Index,
    string Id,
    BakedPolygonKind Kind,
    IReadOnlyList<Vec2> Vertices)
{
    public Vec2 Centroid { get; } = PolygonGeometry.VertexAverage(Vertices);
}
