using System.Globalization;
using System.Xml.Linq;

namespace Sim.Ingest;

// Parses the rung-1 subset of .rou.xml: <vType>, <route>, <vehicle>. Missing optional
// attributes fall back to documented SUMO defaults where the value is purely numeric/simple;
// symbolic values (departPos="base"/"random", departSpeed="max", departLane="free"/"best",
// etc.) are NOT resolved here -- that placement/defaulting logic is a Task 3+ concern. This
// parser only has to be correct for rung 1's fully-numeric attributes.
public static class DemandParser
{
    public static DemandModel Parse(string path)
    {
        using var stream = File.OpenRead(path);
        return ParseDocument(XDocument.Load(stream));
    }

    public static DemandModel ParseXml(string xml) => ParseDocument(XDocument.Parse(xml));

    private static DemandModel ParseDocument(XDocument doc)
    {
        var root = doc.Root ?? throw new InvalidDataException("rou.xml has no root element.");

        var vTypes = new List<VType>();
        var vTypesById = new Dictionary<string, VType>();
        foreach (var vTypeEl in root.Elements("vType"))
        {
            var vType = new VType(
                Id: RequireAttribute(vTypeEl, "id"),
                VClass: vTypeEl.Attribute("vClass")?.Value,
                Sigma: ParseNullableDouble(vTypeEl, "sigma"),
                MaxSpeed: ParseNullableDouble(vTypeEl, "maxSpeed"),
                Accel: ParseNullableDouble(vTypeEl, "accel"),
                Decel: ParseNullableDouble(vTypeEl, "decel"),
                Tau: ParseNullableDouble(vTypeEl, "tau"),
                MinGap: ParseNullableDouble(vTypeEl, "minGap"),
                Length: ParseNullableDouble(vTypeEl, "length"),
                EmergencyDecel: ParseNullableDouble(vTypeEl, "emergencyDecel"),
                SpeedFactor: ParseNullableDouble(vTypeEl, "speedFactor"),
                // Rung A3: a vType ATTRIBUTE, not a <param> child -- SUMO's getJMParam reads the
                // attribute map (SUMOVTypeParameter's map of junction-model params populated
                // straight from the <vType>'s own XML attributes for jm* names).
                JmDriveAfterRedTime: ParseNullableDouble(vTypeEl, "jmDriveAfterRedTime"),
                // C11-i: SUMOVTypeParameter.cpp's carFollowModel="..." vType attribute (a plain
                // string tag name -- "Krauss", "IDM", etc. -- SUMOXMLDefinitions::CarFollowModels).
                CarFollowModel: vTypeEl.Attribute("carFollowModel")?.Value);

            vTypes.Add(vType);
            vTypesById[vType.Id] = vType;
        }

        var routes = new List<Route>();
        var routesById = new Dictionary<string, Route>();
        foreach (var routeEl in root.Elements("route"))
        {
            var route = new Route(
                Id: RequireAttribute(routeEl, "id"),
                Edges: RequireAttribute(routeEl, "edges").Split(' ', StringSplitOptions.RemoveEmptyEntries));

            routes.Add(route);
            routesById[route.Id] = route;
        }

        var vehicles = new List<VehicleDef>();
        foreach (var vehicleEl in root.Elements("vehicle"))
        {
            var stops = vehicleEl.Elements("stop")
                .Select(stopEl => new StopDef(
                    LaneId: RequireAttribute(stopEl, "lane"),
                    StartPos: ParseNullableDouble(stopEl, "startPos") ?? 0.0,
                    EndPos: ParseNullableDouble(stopEl, "endPos") ?? 0.0,
                    Duration: ParseNullableDouble(stopEl, "duration") ?? 0.0))
                .ToList();

            vehicles.Add(new VehicleDef(
                Id: RequireAttribute(vehicleEl, "id"),
                TypeId: vehicleEl.Attribute("type")?.Value ?? string.Empty,
                RouteId: RequireAttribute(vehicleEl, "route"),
                Depart: ParseNullableDouble(vehicleEl, "depart") ?? 0.0,
                DepartPos: ParseNullableDouble(vehicleEl, "departPos") ?? 0.0,
                DepartSpeed: ParseNullableDouble(vehicleEl, "departSpeed") ?? 0.0,
                DepartLaneIndex: ParseNullableInt(vehicleEl, "departLane") ?? 0,
                Stops: stops));
        }

        return new DemandModel(vTypes, vTypesById, routes, routesById, vehicles);
    }

    private static double? ParseNullableDouble(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        return value is null ? null : double.Parse(value, CultureInfo.InvariantCulture);
    }

    private static int? ParseNullableInt(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        return value is null ? null : int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string RequireAttribute(XElement element, string name) =>
        element.Attribute(name)?.Value
        ?? throw new InvalidDataException($"<{element.Name}> is missing required attribute '{name}'.");
}
