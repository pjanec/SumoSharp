using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation.Bake;

// Bakes a PedNetwork's SUMO pedestrian geometry into one deterministically-ordered set of
// walkable polygons (docs/PEDESTRIAN-DESIGN.md §4, the "SUMO-geometry-bake" navigation provider,
// PEDESTRIAN-POC-PLAN.md POC-1a). This is the shared input SumoWalkableSpace (containment) and
// SumoNavMesh (routing) both build on.
//
// P2-1 (docs/PEDESTRIAN-TASKS.md, PEDESTRIAN-DESIGN.md §4): each sidewalk lane's WHOLE polyline is
// buffered into ONE mitred strip polygon (PolylineBuffer.Buffer), not baked per-segment. POC-1a's
// per-segment quad approximation left a bent (multi-segment) sidewalk as several independent quads
// that, unless consecutive segments happened to be collinear, did not share an exact cut edge at
// the joint -- a real disconnection (see PolygonGraph's remarks on the adjacency gap this caused
// and how the vertex-proximity restore in PolygonGraph avoids reintroducing the POC-1a
// 3-polygon-corner bug). Buffering the whole polyline at once removes the joint entirely: a bent
// sidewalk is now baked as ONE watertight polygon, so no adjacency is even needed across its own
// bend. For a straight (2-point, single-segment) lane -- POC-0's fixture network only has these --
// PolylineBuffer's end caps reduce to exactly the same rectangle POC-1a's single-segment quad
// produced, so this change is byte-identical there (see SumoBakeNavigationTests /
// BothProvidersAgreeTests, which assert against that fixture unchanged).
//
// FILTERING: any shape whose absolute shoelace area falls below MinArea is dropped (not baked)
// rather than kept as a zero-area polygon -- SUMO emits exactly such a degenerate (all-collinear)
// "walkingarea" at a network-boundary dead end (POC-0's ":n_w0_0" etc., where the road simply ends
// and there is no real junction area). See PolygonGeometry.SignedArea for why keeping it would be
// actively wrong (a phantom zero-width teleport in the adjacency graph).
public static class WalkablePolygonBaker
{
    // Below this absolute shoelace area (m^2), a shape is treated as a degenerate line, not a
    // walkable area (see PolygonGeometry.SignedArea) and dropped from the bake.
    private const double MinArea = 1e-6;

    public static IReadOnlyList<BakedPolygon> Bake(PedNetwork network)
    {
        var staged = new List<(string Id, BakedPolygonKind Kind, IReadOnlyList<Vec2> Vertices, IReadOnlyList<Vec2>? Spine, double Half)>();

        // WalkingAreas: Polygon already IS the walkable polygon.
        foreach (var wa in network.WalkingAreas.OrderBy(w => w.Id, StringComparer.Ordinal))
        {
            if (IsRealArea(wa.Polygon))
            {
                var half = wa.Width > 0.0 ? wa.Width / 2.0 : 0.5;
                staged.Add((wa.Id, BakedPolygonKind.WalkingArea, wa.Polygon, null, half));
            }
        }

        // Crossings: Outline is already a closed polygon.
        foreach (var crossing in network.Crossings.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (IsRealArea(crossing.Outline))
            {
                var half = crossing.Width > 0.0 ? crossing.Width / 2.0 : 0.5;
                staged.Add((crossing.Id, BakedPolygonKind.Crossing, crossing.Outline, null, half));
            }
        }

        // Sidewalks: buffer the WHOLE polyline by +-Width/2 into one mitred strip polygon (see the
        // class remarks above -- this replaces POC-1a's per-segment quad approximation). The
        // original centreline is kept as the polygon's Spine (see BakedPolygon remarks) so
        // SumoNavMesh can thread a same-polygon path through a bend instead of a naive direct
        // segment.
        foreach (var lane in network.Sidewalks.OrderBy(l => l.Id, StringComparer.Ordinal))
        {
            var half = lane.Width > 0.0 ? lane.Width / 2.0 : 0.5; // sane default if width is unset
            var strip = PolylineBuffer.Buffer(lane.Shape, half);
            if (strip.Count >= 3)
            {
                staged.Add((lane.Id, BakedPolygonKind.SidewalkSegment, strip, lane.Shape, half));
            }
        }

        // Plaza / parking-lot surfaces: Shape is already the walkable polygon. No natural corridor
        // width, so this stays at the 0.5 m default.
        foreach (var wp in network.WalkablePolygons.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (IsRealArea(wp.Shape))
            {
                staged.Add((wp.Id, BakedPolygonKind.WalkablePolygon, wp.Shape, null, 0.5));
            }
        }

        // Fixed group order above (WalkingAreas, Crossings, Sidewalks, WalkablePolygons), each
        // group internally sorted by source id -> the whole bake is deterministic and stable
        // across runs (Index assignment below only depends on `network`'s own contents).
        var result = new List<BakedPolygon>(staged.Count);
        for (var i = 0; i < staged.Count; i++)
        {
            var (id, kind, vertices, spine, half) = staged[i];
            result.Add(new BakedPolygon(i, id, kind, vertices, spine, half));
        }

        return result;
    }

    private static bool IsRealArea(IReadOnlyList<Vec2> shape) =>
        shape.Count >= 3 && Math.Abs(PolygonGeometry.SignedArea(shape)) > MinArea;
}
