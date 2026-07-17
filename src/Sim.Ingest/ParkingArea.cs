using System.Globalization;
using System.Xml.Linq;

namespace Sim.Ingest;

// P0-C2 (docs/HIGH-DENSITY-P0-DESIGN.md "P0-C2"): a roadsideCapacity-based <parkingArea> declared
// in an additional-file. Only the SUMO-faithful straight-lane, roadsideCapacity form is in scope --
// `<space>`-child (explicitly-placed-lot) areas are deferred. `StartPos`/`EndPos` are the on-lane
// extent of the parking strip; `RoadsideCapacity` is the number of evenly-spaced roadside lots.
public sealed record ParkingArea(
    string Id,
    string LaneId,
    double StartPos,
    double EndPos,
    int RoadsideCapacity)
{
    // Lot-0 position of an EMPTY roadside area on a straight/unscaled lane (MSParkingArea.cpp:72,106
    // + getLastFreePos:196-204): the first lot centre sits one full slot-width in from startPos.
    //   lot0 = startPos + (endPos - startPos) / roadsideCapacity
    // Verified against SUMO 1.20.0: 195 + (210-195)/5 = 198.0 (scenarios/48). Requires
    // roadsideCapacity >= 1 -- a capacity-0 area hits SUMO's `begPos - minGap` branch, which is out
    // of scope, so a parkingArea-stop that references one is rejected loudly here rather than
    // silently mis-placed.
    public double Lot0Position()
    {
        if (RoadsideCapacity < 1)
        {
            throw new InvalidDataException(
                $"<parkingArea id='{Id}'> has roadsideCapacity={RoadsideCapacity}; a departPos=\"stop\" " +
                "into it needs roadsideCapacity >= 1 (capacity-0 areas hit a different SUMO placement " +
                "branch that is out of scope for P0-C2).");
        }

        return StartPos + (EndPos - StartPos) / RoadsideCapacity;
    }
}

// P0-C2: minimal additional-file parser -- extracts the <parkingArea> registry. Everything else in
// an additional-file (the pre-P0-C2 "load-and-discard" surface) is still tolerated/ignored; only a
// missing root element is an error, exactly as before.
public static class AdditionalFileParser
{
    // Parse every <parkingArea> under `root`. `laneLength` resolves a lane id to its length, used
    // only for the endPos default. Defaults per SUMO NLTriggerBuilder.cpp:566-569:
    // startPos=0, endPos=lane length, roadsideCapacity=0.
    public static IEnumerable<ParkingArea> ParseParkingAreas(XElement root, Func<string, double> laneLength)
    {
        foreach (var el in root.Descendants("parkingArea"))
        {
            var id = el.Attribute("id")?.Value
                ?? throw new InvalidDataException("<parkingArea> is missing required attribute 'id'.");
            var laneId = el.Attribute("lane")?.Value
                ?? throw new InvalidDataException($"<parkingArea id='{id}'> is missing required attribute 'lane'.");

            var startPos = ParseNullableDouble(el, "startPos") ?? 0.0;
            var endPos = ParseNullableDouble(el, "endPos") ?? laneLength(laneId);
            var roadsideCapacity = ParseNullableInt(el, "roadsideCapacity") ?? 0;

            yield return new ParkingArea(id, laneId, startPos, endPos, roadsideCapacity);
        }
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
}
