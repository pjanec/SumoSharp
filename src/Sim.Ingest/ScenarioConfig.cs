namespace Sim.Ingest;

// Resolved subset of a .sumocfg needed to drive the engine loop. Integration method is a
// config flag, not a baked-in choice (DESIGN.md), so Ballistic/Euler is carried explicitly.
// C1-i: Seed is the sumocfg's <random_number><seed value="..."/></random_number> (SUMO's global
// RNG seed, e.g. RandHelper::initRandGlobal); parsed here for completeness/future ensemble-
// harness use (TASKS.md C1-ii/C1-iii). Not auto-applied to Engine.Seed by LoadScenario -- see
// Engine.Seed's own header comment for why that stays the single, caller-controlled source of
// truth for the per-entity dawdle RNG instead.
// C10-i: LaneChangeDuration is the sumocfg's <processing><lanechange.duration> -- the wall-clock
// seconds a lane change takes to complete laterally (MSAbstractLaneChangeModel's continuous change).
// Default 0 = the instant lane-index snap every pre-C10 scenario uses (byte-identical). > 0 spreads
// the change over round(duration/stepLength) steps, holding the source lane label until the vehicle
// crosses the lane midpoint (MSVehicle emits the lane whose half the vehicle center is in).
// Phase 2 (sublane): LateralResolution is the sumocfg's <processing><lateral-resolution> -- SUMO's
// MSGlobals::gLateralResolution, the width (m) of a sublane. Default 0 = the sublane model is OFF
// (every phase-1 scenario), so the engine's lateral state stays lane-centred and byte-identical.
// > 0 activates the continuous-lateral / sublane model (MSLCM_SL2015); it is the single global
// master switch, exactly as in SUMO, not a per-vType flag.
public sealed record ScenarioConfig(
    double Begin,
    double End,
    double StepLength,
    bool Ballistic,
    double TimeToTeleport,
    double ActionStepLength,
    double SpeedDev,
    int Seed,
    double LaneChangeDuration = 0.0,
    double LateralResolution = 0.0);
