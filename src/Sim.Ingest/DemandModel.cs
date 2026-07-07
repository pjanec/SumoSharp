namespace Sim.Ingest;

// Immutable demand model, parsed once from .rou.xml. Only attributes explicitly present are
// captured for VType (CLAUDE.md/DESIGN.md: --save-state itself does not expand vType defaults,
// so this parser must not invent one either -- resolved defaulting is a separate, later
// cross-check against golden.vtype.json, not this ingest step).
public sealed record VType(
    string Id,
    string? VClass,
    double? Sigma);

public sealed record Route(
    string Id,
    IReadOnlyList<string> Edges);

public sealed record VehicleDef(
    string Id,
    string TypeId,
    string RouteId,
    double Depart,
    double DepartPos,
    double DepartSpeed,
    int DepartLaneIndex);

public sealed record DemandModel(
    IReadOnlyList<VType> VTypes,
    IReadOnlyDictionary<string, VType> VTypesById,
    IReadOnlyList<Route> Routes,
    IReadOnlyDictionary<string, Route> RoutesById,
    IReadOnlyList<VehicleDef> Vehicles);
