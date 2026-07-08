namespace Sim.Ingest;

// Immutable demand model, parsed once from .rou.xml. Only attributes explicitly present are
// captured for VType (CLAUDE.md/DESIGN.md: --save-state itself does not expand vType defaults,
// so this parser must not invent one either -- resolved defaulting is a separate, later
// cross-check against golden.vtype.json, not this ingest step).
//
// Each field is an optional override on top of the vClass-default table (Sim.Ingest.
// VTypeDefaults): a rou.xml <vType> only ever sets the attributes it explicitly needs (rung 4's
// leader sets maxSpeed="5.00", both rung 4 vTypes set sigma="0"), and everything else is left to
// the resolver's `override ?? default` per attribute -- never invented here.
public sealed record VType(
    string Id,
    string? VClass,
    double? Sigma,
    double? MaxSpeed = null,
    double? Accel = null,
    double? Decel = null,
    double? Tau = null,
    double? MinGap = null,
    double? Length = null,
    double? EmergencyDecel = null,
    double? SpeedFactor = null,
    // Rung A3: sumo/src/microsim/MSVehicle.cpp:7266 ignoreRed's
    // getJMParam(SUMO_ATTR_JM_DRIVE_AFTER_RED_TIME, -1) -- a vType-level (not <param> child)
    // junction-model attribute; null here (no override present) resolves to SUMO's -1 default
    // in VTypeDefaults.Resolve ("never ignore red").
    double? JmDriveAfterRedTime = null,
    // C11-i: sumo/src/utils/vehicle/SUMOVTypeParameter.cpp:331's `carFollowModel` vType
    // attribute (SUMO_TAG_CF_KRAUSS's XML tag name is "Krauss", SUMO_TAG_CF_IDM's is "IDM").
    // null here (no override present) resolves to "Krauss" in VTypeDefaults.Resolve, exactly as
    // before this rung.
    string? CarFollowModel = null);

public sealed record Route(
    string Id,
    IReadOnlyList<string> Edges);

// A scheduled <stop> child of <vehicle> (rung 5). Only the non-waypoint lane-stop subset is
// modeled (lane/startPos/endPos/duration) -- busStop/parkingArea/triggered/until/waypoint
// (speed>0) stops are Sim.Ingest's future-scenario surface, not this rung's.
public sealed record StopDef(
    string LaneId,
    double StartPos,
    double EndPos,
    double Duration);

public sealed record VehicleDef(
    string Id,
    string TypeId,
    string RouteId,
    double Depart,
    double DepartPos,
    double DepartSpeed,
    int DepartLaneIndex,
    IReadOnlyList<StopDef>? Stops = null)
{
    // Records can't default a reference-type param to a freshly-allocated empty collection
    // (default values must be compile-time constants), so callers that omit Stops get null;
    // this property is what every reader actually uses.
    public IReadOnlyList<StopDef> Stops { get; init; } = Stops ?? Array.Empty<StopDef>();
}

public sealed record DemandModel(
    IReadOnlyList<VType> VTypes,
    IReadOnlyDictionary<string, VType> VTypesById,
    IReadOnlyList<Route> Routes,
    IReadOnlyDictionary<string, Route> RoutesById,
    IReadOnlyList<VehicleDef> Vehicles);
