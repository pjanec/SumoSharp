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
    // GAP-3 (docs/SUMOSHARP-SERVE-PATH-DROP-IN.md §3): per-occupant lot position on a straight/
    // unscaled lane -- MSParkingArea.cpp:72 `spaceDim = (endPos-begPos)/capacity`, :106 each lot i's
    // `endPos = MIN2(myEndPos, myBegPos + MAX2(POSITION_EPS, spaceDim*(i+1)))`. lot 0 is the
    // pre-GAP-3 Lot0Position (verified against SUMO 1.20.0: 195 + (210-195)/5 = 198.0,
    // scenarios/48); lot i>0 slides further down the strip, capped at endPos so a capacity that
    // overflows the strip length never places a lot past its declared end. Requires
    // roadsideCapacity >= 1 -- a capacity-0 area hits SUMO's `begPos - minGap` branch, which is out
    // of scope, so a parkingArea-stop that references one is rejected loudly here rather than
    // silently mis-placed. `lotIndex` must be in [0, roadsideCapacity) -- callers (Engine's
    // occupant-assignment pass) are responsible for never requesting more lots than the area holds.
    public double LotPosition(int lotIndex)
    {
        if (RoadsideCapacity < 1)
        {
            throw new InvalidDataException(
                $"<parkingArea id='{Id}'> has roadsideCapacity={RoadsideCapacity}; a departPos=\"stop\" " +
                "into it needs roadsideCapacity >= 1 (capacity-0 areas hit a different SUMO placement " +
                "branch that is out of scope for P0-C2).");
        }

        if (lotIndex < 0 || lotIndex >= RoadsideCapacity)
        {
            throw new InvalidDataException(
                $"<parkingArea id='{Id}'> has roadsideCapacity={RoadsideCapacity}; lot index {lotIndex} " +
                "is out of range (too many vehicles reference this parkingArea at once for GAP-3's " +
                "static load-time occupant assignment).");
        }

        var spaceDim = (EndPos - StartPos) / RoadsideCapacity;
        return Math.Min(EndPos, StartPos + Math.Max(PositionEps, spaceDim * (lotIndex + 1)));
    }

    // Pre-GAP-3 name, kept for back-compat call sites (single-occupant scenarios, e.g. 48): lot 0
    // of an otherwise-empty area, byte-identical to the old formula (spaceDim*(0+1) == spaceDim).
    public double Lot0Position() => LotPosition(0);

    // MSParkingArea.cpp's POSITION_EPS (sumo/src/utils/common/StdDefs.h) -- the same "must advance
    // by at least this much" floor MIN2(myEndPos, myBegPos + MAX2(POSITION_EPS, ...)) uses so a
    // capacity that makes spaceDim collapse to ~0 still produces a valid (fractionally advancing)
    // per-lot endPos rather than every lot piling onto startPos.
    private const double PositionEps = 0.1;

    // GAP-3: functional (not byte-exact) lateral offset for a PARKED vehicle -- MSParkingArea.cpp's
    // off-road bay shape is `move2side(laneWidth/2 + parkingWidth/2)` off the lane centreline; with
    // no explicit `<parkingArea width=...>` parsed (out of scope), parkingWidth defaults to
    // laneWidth (MSParkingArea's own `if (myWidth==0) myWidth = SUMO_const_laneWidth`), collapsing
    // the formula to exactly one lane width -- verified against scenario 48's SUMO 1.20.0 golden
    // (lane centre y=-1.600, parked y=-4.800, |diff|=3.200 == the net's lane width). Negative
    // (toward the right-hand shoulder, SUMO's default) in this engine's "+left of travel" LatOffset
    // convention; the owner's functional bar (SUMOSHARP-SERVE-PATH-DROP-IN.md §3) only requires
    // NONZERO/off-lane, not sign- or magnitude-exact.
    public static double LateralParkOffset(double laneWidth) => -laneWidth;
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
