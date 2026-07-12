namespace Sim.Core;

// SUMOSHARP-API.md §9: the input to Engine.DefineVType -- the runtime-settable subset of SUMO's vType
// attributes. Every field is an OPTIONAL override on top of the vClass default table (VTypeDefaults),
// exactly like a parsed rou.xml <vType>: a null leaves SUMO's vClass default in place, so
// `new VTypeParams()` yields the pure `VClass` defaults. Runtime-defined vTypes therefore resolve
// through the SAME VTypeDefaults.Resolve pipeline as loaded ones (parity-consistent).
//
// NOTE: the sublane lateral attributes (maxSpeedLat / latAlignment / minGapLat) are intentionally NOT
// here yet -- this branch's VType has no such fields; they are folded in at the laneless-branch merge
// (LANELESS-DIRECTION.md / SUMOSHARP-API.md §15), which owns those VType additions.
public sealed record VTypeParams
{
    // vClass selects the default table row (passenger/truck/bus/bicycle/…). Defaults to passenger.
    public string VClass { get; init; } = "passenger";

    public double? Accel { get; init; }
    public double? Decel { get; init; }
    public double? EmergencyDecel { get; init; }
    public double? MaxSpeed { get; init; }
    public double? Tau { get; init; }
    public double? MinGap { get; init; }
    public double? Length { get; init; }

    // Driver imperfection. Null -> vClass default (0.5 for passenger). Set 0.0 for deterministic motion.
    public double? Sigma { get; init; }
    public double? SpeedFactor { get; init; }

    // "Krauss" (default), "IDM", "IDMM", "ACC", "CACC", "Rail" -- must match a supported model.
    public string? CarFollowModel { get; init; }

    public bool? HasBluelight { get; init; }
    public bool? LcOpposite { get; init; }
}
