namespace Sim.Ingest;

// Fully-resolved vType parameters used by the car-following model. .rou.xml only captures
// attributes explicitly present (see VType above); everything else must be filled from SUMO's
// vClass-default tables so the engine's init matches what SUMO actually resolves internally.
//
// Values and citations below are cross-checked against BOTH the vendored source AND a
// libsumo/TraCI dump of the resolved passenger defaults (they agree -- see
// scenarios/01-single-free-flow/VTYPE_CROSSCHECK.md / golden.vtype.json). CLAUDE.md rule 6:
// diff resolved defaults against golden.state.xml/golden.vtype.json before chasing trajectory
// drift -- this resolver plus its init cross-check test exists precisely for that.
public sealed record ResolvedVType(
    string Id,
    string VClass,
    string CarFollowModel,
    double Length,
    double MinGap,
    double MaxSpeed,
    double Accel,
    double Decel,
    double EmergencyDecel,
    double ApparentDecel,
    double Sigma,
    double Tau,
    double SpeedFactor,
    double Width,
    double Height);

public static class VTypeDefaults
{
    // Only the "passenger" vClass is resolved so far -- rung 1 is the only vClass in scope.
    public static ResolvedVType ResolvePassenger(VType vType)
    {
        if (vType.VClass != "passenger")
        {
            throw new NotSupportedException(
                $"VTypeDefaults.ResolvePassenger only resolves vClass='passenger' (vType '{vType.Id}' has vClass='{vType.VClass}').");
        }

        // SUMOVTypeParameter.cpp getDefaultDecel default branch: return 4.5;
        const double decel = 4.5;

        return new ResolvedVType(
            Id: vType.Id,
            VClass: vType.VClass,
            // SUMOVTypeParameter.cpp:331 cfModel(SUMO_TAG_CF_KRAUSS) -- default CF model.
            CarFollowModel: "Krauss",
            // SUMOVehicleClass.cpp getDefaultVehicleLength default branch: return 5;
            Length: 5.0,
            // SUMOVTypeParameter.cpp:61 minGap(2.5) (passenger does not override).
            MinGap: 2.5,
            // SUMOVTypeParameter.cpp:63 maxSpeed(200. / 3.6) (passenger does not override).
            MaxSpeed: 200.0 / 3.6,
            // SUMOVTypeParameter.cpp getDefaultAccel default branch: return 2.6;
            Accel: 2.6,
            Decel: decel,
            // getDefaultEmergencyDecel default option -> MAX2(decel=4.5, vcDecel=9.0) = 9.0.
            EmergencyDecel: 9.0,
            // MSCFModel.cpp:61 getCFParam(SUMO_ATTR_APPARENTDECEL, myDecel) -- defaults to decel.
            ApparentDecel: decel,
            // SUMOVTypeParameter.cpp getDefaultImperfection default branch: return 0.5; the
            // rou.xml <vType> may override this explicitly (rung 1's scenario sets sigma="0"
            // for determinism), so honor that override when present.
            Sigma: vType.Sigma ?? 0.5,
            // MSCFModel.cpp:63 getCFParam(SUMO_ATTR_TAU, 1.0).
            Tau: 1.0,
            // SUMOVTypeParameter.cpp:317 speedFactor("normc", 1.0, 0.0, 0.2, 2.0) -- mean 1.0.
            // Phase 1 has no System.Random / RNG at all (CLAUDE.md), and rung 1's config.sumocfg
            // additionally forces default.speeddev="0", so the drawn speedFactor is exactly its
            // mean, 1.0, with no per-vehicle deviation to model yet.
            SpeedFactor: 1.0,
            // SUMOVTypeParameter.cpp:65 width(1.8).
            Width: 1.8,
            // SUMOVTypeParameter.cpp:66 height(1.5).
            Height: 1.5);
    }
}
