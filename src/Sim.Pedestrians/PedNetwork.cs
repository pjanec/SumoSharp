using Sim.Core.Orca;

namespace Sim.Pedestrians;

// The pedestrian-network model produced by PedNetworkParser. Deliberately immutable and
// data-only: this is pure geometry, read from a net.net.xml (+ optional walkable.add.xml),
// consumed later by the navmesh/tactical-routing providers (docs/PEDESTRIAN-DESIGN.md §4).
//
// This is a SEPARATE ingest from the parity src/Sim.Ingest/NetworkParser.cs
// (docs/PEDESTRIAN-DESIGN.md §0 Principle 6) — it must never be merged with, nor replace, the
// lane-parity network model.
public sealed record PedNetwork(
    IReadOnlyList<PedLane> Sidewalks,
    IReadOnlyList<PedCrossing> Crossings,
    IReadOnlyList<PedWalkingArea> WalkingAreas,
    IReadOnlyList<WalkablePolygon> WalkablePolygons,
    IReadOnlyList<WalkableAccessPoint> AccessPoints)
{
    // R1 (docs/PEDESTRIAN-R1-CONNECTION-STITCH-DESIGN.md): the net's own DECLARED pedestrian connectivity --
    // one entry per SUMO <connection> whose both ends are pedestrian lanes (sidewalk/crossing/walkingArea).
    // The navmesh baker stitches portals from these so a junction whose split walkingArea pieces abut only at
    // an ambiguous corner (which the purely-geometric adjacency pass conservatively won't bridge) is still one
    // component -- the net says a pedestrian walks straight through. Init-only with an empty default, so every
    // existing `new PedNetwork(...)` caller is unaffected.
    public IReadOnlyList<PedConnection> PedConnections { get; init; } = Array.Empty<PedConnection>();

    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §6 (capability probe + graceful degrade): the empty
    // network -- every record list empty, no PedConnections -- used as the fallback when
    // `PedNetworkParser.Load` throws on a malformed net.xml (an internal-edge id that does not match
    // SUMO's ":<junction>_[cw]<N>" convention, a crossing/walkingarea edge with no pedestrian lane,
    // etc.). A caller that loads this sees zero sidewalks/crossings and can degrade cleanly (no
    // pedestrians) instead of propagating the parse exception.
    public static readonly PedNetwork Empty = new(
        Array.Empty<PedLane>(),
        Array.Empty<PedCrossing>(),
        Array.Empty<PedWalkingArea>(),
        Array.Empty<WalkablePolygon>(),
        Array.Empty<WalkableAccessPoint>());
}

// A declared pedestrian connection between two baked-polygon LANE ids (the id space of BakedPolygon.Id):
// a sidewalk lane, crossing lane, or walkingArea lane. Unordered for connectivity (a pedestrian move is
// bidirectional on the navmesh).
public sealed record PedConnection(string AId, string BId);

// A pedestrian-usable sidewalk lane on a normal (non-internal, non-crossing, non-walkingarea)
// edge, i.e. a <lane allow="pedestrian" .../> child of an edge with no "function" attribute.
//
// `ShapeZ` is the OUTPUT-ONLY per-vertex elevation channel (metres), index-aligned with the 2-D shape
// above and `null` on a 2-D net -- deliberately the same pattern, and the same discipline, as the
// vehicle side's `Sim.Ingest.Lane.ShapeZ`: it is read by the RENDER seam only and by no routing,
// steering, ORCA or ActivityTimeline decision anywhere. That is what keeps every committed 2-D scenario
// bit-identical (docs/EXTERNAL-NET-LOADING-DESIGN.md §3.2/§3.3). Null, never an empty array and never
// zeros, so "this net has no elevation" is distinguishable from "this net is at sea level".
public sealed record PedLane(
    string Id,
    string EdgeId,
    double Width,
    IReadOnlyList<Vec2> Shape,
    IReadOnlyList<double>? ShapeZ = null);

// A signalized-or-not pedestrian crossing: an edge with function="crossing". TlLogicId is set
// only when the crossing's junction has a matching <tlLogic>, i.e. the crossing is
// TLS-controlled (its lane carries a signal-link mapping via <connection tl="..." .../>).
//
// `ShapeZ` is the OUTPUT-ONLY per-vertex elevation channel (metres), index-aligned with the 2-D shape
// above and `null` on a 2-D net -- deliberately the same pattern, and the same discipline, as the
// vehicle side's `Sim.Ingest.Lane.ShapeZ`: it is read by the RENDER seam only and by no routing,
// steering, ORCA or ActivityTimeline decision anywhere. That is what keeps every committed 2-D scenario
// bit-identical (docs/EXTERNAL-NET-LOADING-DESIGN.md §3.2/§3.3). Null, never an empty array and never
// zeros, so "this net has no elevation" is distinguishable from "this net is at sea level".
//
// `OutlineZ` is the same channel for `Outline` (C1.SC4): the outline is what a consumer extrudes into a
// crosswalk polygon, so a 3-D net's zebra would sit at z=0 without it. Index-aligned with `Outline`,
// null when the outline is 2-D or absent.
public sealed record PedCrossing(
    string Id,
    string JunctionId,
    double Width,
    IReadOnlyList<Vec2> Shape,
    IReadOnlyList<Vec2> Outline,
    IReadOnlyList<string> CrossingEdges,
    string? TlLogicId,
    IReadOnlyList<double>? ShapeZ = null,
    IReadOnlyList<double>? OutlineZ = null);

// A walkingarea: an edge with function="walkingarea". The lane's shape is the walkable polygon
// covering the junction corner.
//
// `PolygonZ` is the OUTPUT-ONLY per-vertex elevation channel (metres), index-aligned with the 2-D shape
// above and `null` on a 2-D net -- deliberately the same pattern, and the same discipline, as the
// vehicle side's `Sim.Ingest.Lane.ShapeZ`: it is read by the RENDER seam only and by no routing,
// steering, ORCA or ActivityTimeline decision anywhere. That is what keeps every committed 2-D scenario
// bit-identical (docs/EXTERNAL-NET-LOADING-DESIGN.md §3.2/§3.3). Null, never an empty array and never
// zeros, so "this net has no elevation" is distinguishable from "this net is at sea level".
public sealed record PedWalkingArea(
    string Id,
    string JunctionId,
    double Width,
    IReadOnlyList<Vec2> Polygon,
    IReadOnlyList<double>? PolygonZ = null);

// An open walkable surface from walkable.add.xml (e.g. a plaza or parking-lot polygon) that SUMO
// does not model as pedestrian infrastructure but the navmesh providers must still consume.
public sealed record WalkablePolygon(
    string Id,
    string Type,
    IReadOnlyList<Vec2> Shape);

// A point-of-interest marking where a walkable surface connects to the road/lane world (e.g. a
// parking-lot entry/exit), from walkable.add.xml <poi>.
public sealed record WalkableAccessPoint(
    string Id,
    string Type,
    Vec2 Position);
