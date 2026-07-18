namespace Sim.Core;

// Runtime mirror of a single scheduled <stop> (Sim.Ingest.StopDef), ported from the fields
// MSVehicle::MSStop (sumo/src/microsim/MSStop.h) actually reads for a non-waypoint lane stop
// (stop.getSpeed()==0): lane/startPos/endPos/duration plus the `reached` flag and the
// per-step-decremented `duration` countdown (MSVehicle.cpp's processNextStop, ~lines 1613-1897).
// Only ever mutated during Execute (Engine.ExecuteMoves applies a StopTransition computed
// during Plan) -- CLAUDE.md rule 3: the plan phase must not write shared/runtime state, even
// state as narrowly-scoped as "this vehicle's own next stop."
internal sealed class StopRuntime
{
    public required string LaneId { get; init; }
    public required double StartPos { get; init; }
    public required double EndPos { get; init; }

    // MSStop::getMinDuration's fallback (no until/ended modeled): the configured <stop
    // duration="..."/> in seconds, used to (re)initialize RemainingDuration once reached.
    public required double Duration { get; init; }

    // GAP-3 (docs/SUMOSHARP-SERVE-PATH-DROP-IN.md §3): true iff this stop originated from a
    // `<stop parkingArea="...">` (Sim.Ingest.StopDef.ParkingAreaId != null) rather than a plain
    // `<stop lane="...">`. Distinguishes the two at the ONE seam that matters for behavior: once
    // `Reached`, a parking stop takes the vehicle OFF the running lane (VehicleRuntime.IsParked --
    // lateral offset, excluded from leader queries) while a plain lane stop stays an ordinary
    // on-lane, blocking stop exactly as before GAP-3 (scenarios 03/13/44 are untouched -- this
    // field is false for every StopDef whose ParkingAreaId is null, which is every one of them).
    // Default false so any StopRuntime built without setting it (there are none post-GAP-3, but the
    // property is not `required` to avoid disturbing any other construction site) is a plain stop.
    public bool IsParking { get; init; }

    public bool Reached;
    public double RemainingDuration;
}
