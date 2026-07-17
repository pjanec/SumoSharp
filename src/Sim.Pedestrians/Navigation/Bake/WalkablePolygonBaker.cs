using Sim.Core.Orca;

namespace Sim.Pedestrians.Navigation.Bake;

// Bakes a PedNetwork's SUMO pedestrian geometry into one deterministically-ordered set of
// walkable polygons (docs/PEDESTRIAN-DESIGN.md §4, the "SUMO-geometry-bake" navigation provider,
// PEDESTRIAN-POC-PLAN.md POC-1a). This is the shared input SumoWalkableSpace (containment) and
// SumoNavMesh (routing) both build on.
//
// APPROXIMATION (documented, POC-1a): sidewalks are not modelled as a single strip polygon per
// lane; each shape SEGMENT is buffered independently into its own quad ("a per-segment quad set
// is fine for the POC", PEDESTRIAN-POC-PLAN.md POC-1). For a lane whose shape bends (more than one
// segment), consecutive quads meet at the segment joint but, unless the two segments are
// collinear, their cut edges will not coincide exactly there -- at a sharp bend the two quads can
// fail PolygonGraph's shared-EDGE adjacency test entirely (a real gap, not just a rounding error),
// leaving them disconnected. PolygonGraph deliberately does NOT paper over this with a
// vertex-proximity fallback (see its own remarks: that fallback caused a real routing bug at a
// 3-polygon corner in POC-0). POC-0's fixture network only has straight (2-point) sidewalk shapes,
// so this never bites here; a production bake would buffer the whole polyline as one
// mitred/rounded strip instead.
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
        var staged = new List<(string Id, BakedPolygonKind Kind, IReadOnlyList<Vec2> Vertices)>();

        // WalkingAreas: Polygon already IS the walkable polygon.
        foreach (var wa in network.WalkingAreas.OrderBy(w => w.Id, StringComparer.Ordinal))
        {
            if (IsRealArea(wa.Polygon))
            {
                staged.Add((wa.Id, BakedPolygonKind.WalkingArea, wa.Polygon));
            }
        }

        // Crossings: Outline is already a closed polygon.
        foreach (var crossing in network.Crossings.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (IsRealArea(crossing.Outline))
            {
                staged.Add((crossing.Id, BakedPolygonKind.Crossing, crossing.Outline));
            }
        }

        // Sidewalks: buffer each polyline SEGMENT by +-Width/2 perpendicular into a quad (see the
        // class remarks above for the per-segment approximation).
        foreach (var lane in network.Sidewalks.OrderBy(l => l.Id, StringComparer.Ordinal))
        {
            var half = lane.Width > 0.0 ? lane.Width / 2.0 : 0.5; // sane default if width is unset
            for (var i = 0; i + 1 < lane.Shape.Count; i++)
            {
                var a = lane.Shape[i];
                var b = lane.Shape[i + 1];
                var dir = b - a;
                if (dir.AbsSq <= PolygonGeometry.DegenerateLengthSq)
                {
                    continue; // degenerate zero-length shape segment
                }

                var offset = dir.Normalized().PerpCW * half;
                var quad = new[] { a + offset, b + offset, b - offset, a - offset };
                staged.Add(($"{lane.Id}#{i}", BakedPolygonKind.SidewalkSegment, quad));
            }
        }

        // Plaza / parking-lot surfaces: Shape is already the walkable polygon.
        foreach (var wp in network.WalkablePolygons.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (IsRealArea(wp.Shape))
            {
                staged.Add((wp.Id, BakedPolygonKind.WalkablePolygon, wp.Shape));
            }
        }

        // Fixed group order above (WalkingAreas, Crossings, Sidewalks, WalkablePolygons), each
        // group internally sorted by source id -> the whole bake is deterministic and stable
        // across runs (Index assignment below only depends on `network`'s own contents).
        var result = new List<BakedPolygon>(staged.Count);
        for (var i = 0; i < staged.Count; i++)
        {
            var (id, kind, vertices) = staged[i];
            result.Add(new BakedPolygon(i, id, kind, vertices));
        }

        return result;
    }

    private static bool IsRealArea(IReadOnlyList<Vec2> shape) =>
        shape.Count >= 3 && Math.Abs(PolygonGeometry.SignedArea(shape)) > MinArea;
}
