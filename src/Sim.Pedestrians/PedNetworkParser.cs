using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Sim.Core.Orca;

namespace Sim.Pedestrians;

// Reads ONLY the pedestrian geometry out of a SUMO net.net.xml (+ optional walkable.add.xml),
// using System.Xml.Linq. This is a wholly separate ingest from the parity
// src/Sim.Ingest/NetworkParser.cs (docs/PEDESTRIAN-DESIGN.md §0 Principle 6) — it must never
// reference, call into, or be merged with that parser, and it must never be edited to serve
// parity needs.
//
// Classification rule (matches netconvert's own edge "function" attribute):
//   - no "function" attribute, lane has allow="pedestrian"  => sidewalk (PedLane)
//   - function="crossing"                                    => crossing (PedCrossing)
//   - function="walkingarea"                                 => walkingarea (PedWalkingArea)
// Internal (function="internal") edges are vehicle-only turn geometry and are ignored here.
public static class PedNetworkParser
{
    // Crossing/walkingarea internal edge ids follow SUMO's ":<junction>_c<N>" /
    // ":<junction>_w<N>" convention. Match the trailing "_c<digits>" or "_w<digits>" suffix and
    // take everything before it as the junction id (junction ids may themselves contain
    // underscores, so this must anchor on the suffix, not split naively).
    private static readonly Regex JunctionFromInternalEdgeId =
        new(@"^:(?<junction>.+)_[cw]\d+$", RegexOptions.Compiled);

    public static PedNetwork Load(string netPath, string? walkableAddPath = null)
    {
        var netDoc = XDocument.Load(netPath);
        var root = netDoc.Root ?? throw new InvalidOperationException($"'{netPath}' has no root element.");

        var tlLogicIds = new HashSet<string>(
            root.Elements("tlLogic").Select(e => (string)e.Attribute("id")!),
            StringComparer.Ordinal);

        var sidewalks = new List<PedLane>();
        var crossings = new List<PedCrossing>();
        var walkingAreas = new List<PedWalkingArea>();

        foreach (var edge in root.Elements("edge"))
        {
            var function = (string?)edge.Attribute("function");
            var edgeId = (string)edge.Attribute("id")!;

            if (function is null)
            {
                // Normal edge: any pedestrian-allowed lane on it is a sidewalk.
                foreach (var lane in edge.Elements("lane"))
                {
                    if (!AllowsPedestrian(lane))
                    {
                        continue;
                    }

                    sidewalks.Add(new PedLane(
                        Id: (string)lane.Attribute("id")!,
                        EdgeId: edgeId,
                        Width: ParseWidth(lane),
                        Shape: ParseShape(lane.Attribute("shape")),
                        ShapeZ: ParseShapeZ(lane.Attribute("shape"))));
                }
            }
            else if (function == "crossing")
            {
                var lane = edge.Elements("lane").FirstOrDefault(AllowsPedestrian)
                    ?? throw new InvalidOperationException($"Crossing edge '{edgeId}' has no pedestrian lane.");

                var junctionId = JunctionIdFromInternalEdgeId(edgeId);
                var crossingEdges = ((string?)edge.Attribute("crossingEdges"))
                    ?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    ?? Array.Empty<string>();

                crossings.Add(new PedCrossing(
                    Id: (string)lane.Attribute("id")!,
                    JunctionId: junctionId,
                    Width: ParseWidth(lane),
                    Shape: ParseShape(lane.Attribute("shape")),
                    Outline: ParseShape(lane.Attribute("outlineShape")),
                    CrossingEdges: crossingEdges,
                    TlLogicId: tlLogicIds.Contains(junctionId) ? junctionId : null,
                    ShapeZ: ParseShapeZ(lane.Attribute("shape")),
                    OutlineZ: ParseShapeZ(lane.Attribute("outlineShape"))));
            }
            else if (function == "walkingarea")
            {
                var lane = edge.Elements("lane").FirstOrDefault(AllowsPedestrian)
                    ?? throw new InvalidOperationException($"Walkingarea edge '{edgeId}' has no pedestrian lane.");

                walkingAreas.Add(new PedWalkingArea(
                    Id: (string)lane.Attribute("id")!,
                    JunctionId: JunctionIdFromInternalEdgeId(edgeId),
                    Width: ParseWidth(lane),
                    Polygon: ParseShape(lane.Attribute("shape")),
                    PolygonZ: ParseShapeZ(lane.Attribute("shape"))));
            }
            // function="internal" (and any other function) is vehicle-only turn geometry; ignored.
        }

        var walkablePolygons = new List<WalkablePolygon>();
        var accessPoints = new List<WalkableAccessPoint>();

        if (walkableAddPath is not null)
        {
            var addDoc = XDocument.Load(walkableAddPath);
            var addRoot = addDoc.Root ?? throw new InvalidOperationException($"'{walkableAddPath}' has no root element.");

            foreach (var poly in addRoot.Elements("poly"))
            {
                walkablePolygons.Add(new WalkablePolygon(
                    Id: (string)poly.Attribute("id")!,
                    Type: (string?)poly.Attribute("type") ?? string.Empty,
                    Shape: ParseShape(poly.Attribute("shape"))));
            }

            foreach (var poi in addRoot.Elements("poi"))
            {
                accessPoints.Add(new WalkableAccessPoint(
                    Id: (string)poi.Attribute("id")!,
                    Type: (string?)poi.Attribute("type") ?? string.Empty,
                    Position: new Vec2(
                        ParseDouble((string)poi.Attribute("x")!),
                        ParseDouble((string)poi.Attribute("y")!))));
            }
        }

        // R1 (docs/PEDESTRIAN-R1-CONNECTION-STITCH-DESIGN.md): read the net's declared pedestrian connectivity.
        // A <connection from="edge" fromLane="i" to="edge2" toLane="j" .../> names EDGE ids + lane indices; the
        // corresponding lane id is "edge_i" -- the SAME id space as BakedPolygon.Id (all baked polygons are
        // keyed by their lane id). A connection is PEDESTRIAN iff BOTH resolved lane ids are pedestrian lanes
        // (in the sidewalk/crossing/walkingArea sets); vehicle-lane connections resolve to non-ped ids and are
        // dropped here -- which is exactly what correctly leaves a car-only-linked surface (e.g. the demo-city
        // dining plaza) isolated rather than fabricating a ped path the net does not declare.
        var pedLaneIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sidewalks) pedLaneIds.Add(s.Id);
        foreach (var c in crossings) pedLaneIds.Add(c.Id);
        foreach (var w in walkingAreas) pedLaneIds.Add(w.Id);

        var pedConnections = new List<PedConnection>();
        foreach (var conn in root.Elements("connection"))
        {
            var from = (string?)conn.Attribute("from");
            var to = (string?)conn.Attribute("to");
            var fromLane = (string?)conn.Attribute("fromLane");
            var toLane = (string?)conn.Attribute("toLane");
            if (from is null || to is null || fromLane is null || toLane is null)
            {
                continue;
            }

            var aId = from + "_" + fromLane;
            var bId = to + "_" + toLane;
            if (aId != bId && pedLaneIds.Contains(aId) && pedLaneIds.Contains(bId))
            {
                pedConnections.Add(new PedConnection(aId, bId));
            }
        }

        return new PedNetwork(sidewalks, crossings, walkingAreas, walkablePolygons, accessPoints)
        {
            PedConnections = pedConnections,
        };
    }

    private static bool AllowsPedestrian(XElement lane)
    {
        var allow = (string?)lane.Attribute("allow");
        if (allow is null)
        {
            return false;
        }

        return allow.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Contains("pedestrian", StringComparer.Ordinal);
    }

    private static double ParseWidth(XElement lane)
    {
        var width = (string?)lane.Attribute("width");
        return width is null ? 0.0 : ParseDouble(width);
    }

    private static string JunctionIdFromInternalEdgeId(string edgeId)
    {
        var match = JunctionFromInternalEdgeId.Match(edgeId);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Internal edge id '{edgeId}' does not match the ':<junction>_[cw]<N>' convention.");
        }

        return match.Groups["junction"].Value;
    }

    // docs/EXTERNAL-NET-LOADING-DESIGN.md §3.2 (C1): the optional 3rd (z / elevation) component of each
    // shape vertex, index-aligned with `ParseShape`'s output above.
    //
    // Mirrors `Sim.Ingest.NetworkParser.ParseShapeZ` deliberately -- same all-or-nothing rule, same null
    // return -- so the ped side and the vehicle side treat a 2-D net identically instead of inventing a
    // second convention. Returns **null** (never an empty array, never zeros) when the attribute is
    // absent or ANY vertex lacks a z, which is what lets a consumer distinguish "this net has no
    // elevation" from "this net is at sea level", and what keeps every 2-D scenario bit-identical.
    //
    // Vertices are skipped here under exactly the same condition `ParseShape` skips them (`parts.Length
    // < 2`), so the two outputs stay index-aligned even for a malformed token -- that alignment is the
    // whole contract of this channel.
    private static IReadOnlyList<double>? ParseShapeZ(XAttribute? shapeAttr)
    {
        if (shapeAttr is null)
        {
            return null;
        }

        var tokens = shapeAttr.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var zs = new List<double>(tokens.Length);

        foreach (var token in tokens)
        {
            var parts = token.Split(',');
            if (parts.Length < 2)
            {
                continue; // skipped by ParseShape too -- stay index-aligned with it
            }

            if (parts.Length < 3)
            {
                return null; // 2-D shape -> no elevation profile at all
            }

            zs.Add(ParseDouble(parts[2]));
        }

        return zs.Count > 0 ? zs : null;
    }

    private static IReadOnlyList<Vec2> ParseShape(XAttribute? shapeAttr)
    {
        if (shapeAttr is null)
        {
            return Array.Empty<Vec2>();
        }

        var text = shapeAttr.Value;
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var points = new List<Vec2>(tokens.Length);

        foreach (var token in tokens)
        {
            var parts = token.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }

            points.Add(new Vec2(ParseDouble(parts[0]), ParseDouble(parts[1])));
        }

        return points;
    }

    private static double ParseDouble(string s) =>
        double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
