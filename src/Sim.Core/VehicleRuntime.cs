using Sim.Ingest;

namespace Sim.Core;

// Per-vehicle mutable runtime state, plus the immutable spawn template (Def) it was created
// from. D3 (FastDataPlane ECS readiness): every field left on this class is now unmanaged
// (scalars/structs -- `Kinematics`/`MoveIntent` are already unmanaged structs) or one of the
// two IMMUTABLE blueprint refs (`Def`, `VType`); the managed, variable-length state that used
// to live here (`LaneSequence`/`LaneSequenceHandles`, `Stops`, `AvoidedEdges`) has moved to
// engine-owned side storage keyed by `EntityIndex` -- a shared int pool with a per-entity
// [start,len) slice for the lane sequence, and `Dictionary<int, ...>` side tables for the rare/
// cold stop queue and avoided-edge set. This is the FDP-readiness posture: the class is now
// chunk-storable (no `Queue`/`HashSet`/`IReadOnlyList`/`int[]` fields) modulo `Def`/`VType`
// still being managed refs and the flat scalar layout not yet grouped into sub-structs --
// turning `Def`/`VType` into TKB handles and grouping the scalars is deferred to D7's store
// boundary (out of this rung's scope; see TASKS.md D3/D7).
internal sealed class VehicleRuntime
{
    public required VehicleDef Def { get; init; }

    // Resolved (fully-defaulted) vType parameters (Sim.Ingest.VTypeDefaults) -- the car-
    // following model reads these, never the raw .rou.xml VType with its optional fields.
    public required ResolvedVType VType { get; init; }

    // D3: this vehicle's stable index in Engine._vehicles, set once at creation (LoadScenario).
    // Vehicles are never removed from that list -- only flagged Arrived -- so the list index is
    // a stable entity id. D5 adds `Entity` (below) as the actual FDP-shaped handle; EntityIndex
    // remains the plain int key the engine's side storage uses directly (lane-sequence pool
    // slice owner id is implicit via LaneSeqStart/LaneSeqLen below; Stops/AvoidedEdges side
    // tables are keyed by this directly) -- always equal to Entity.Index.
    public int EntityIndex;

    // D5 (FastDataPlane ECS readiness): the FDP-shaped handle for this vehicle -- `new
    // Entity(EntityIndex, 0)`, set once at creation alongside EntityIndex (LoadScenario).
    // Generation stays 0 (no recycling yet, see Entity.cs's header comment); nothing in the
    // engine keys off this yet (EntityIndex/side tables are unchanged), it exists so callers
    // can start holding the FDP-shaped handle instead of a raw int.
    public Entity Entity;

    public bool Inserted;

    // Set once the vehicle runs off the end of LaneSequence (route end) during execute;
    // distinct from Inserted so InsertDepartingVehicles never mistakes "arrived" for "not yet
    // departed" and re-inserts.
    public bool Arrived;
    public string LaneId = string.Empty;

    // D2: the dense handle of LaneId (`_network.LaneHandleById[LaneId]`) -- kept in lockstep
    // with LaneId by every Engine write site (insertion, lane traversal, LC swaps, reroute).
    // LaneId remains authoritative for correctness/emit; LaneHandle exists purely so hot-path
    // lookups (LaneNeighborQuery buckets, `_network.LanesByHandle[...]`) can index an array
    // instead of hashing a string every vehicle, every step.
    public int LaneHandle;

    // D3: this vehicle's lane-sequence is now a SLICE `[LaneSeqStart, LaneSeqStart+LaneSeqLen)`
    // into Engine's shared `_laneSeqPool` (a single `List<int>` of lane HANDLES, blob-style) --
    // replacing the old per-vehicle `IReadOnlyList<string> LaneSequence`/`int[]
    // LaneSequenceHandles` managed collections. Set once at insertion (TryInsertOnLane) by
    // appending the resolved handle sequence to the pool; a reroute (UpdateReroutes) appends a
    // NEW slice and simply repoints Start/Len (the old slice is abandoned in the pool -- it only
    // grows; D7 can compact if that ever matters). LaneSeqIndex is the index of the CURRENT lane
    // (LaneHandle) within this slice, advanced by ExecuteMoves as the vehicle's Pos crosses each
    // lane's end. A single-edge route resolves to a one-element slice, so this collapses to rung
    // 1-8's single-lane "reached the end -> arrived" behavior exactly.
    public int LaneSeqStart;
    public int LaneSeqLen;
    public int LaneSeqIndex;
    public Kinematics Kinematics;
    public MoveIntent Intent;

    // C1-i: this vehicle's private dawdle RNG state (Sim.Core.VehicleRng -- a single unmanaged
    // `ulong`, D3-clean). Seeded ONCE, at creation (Engine.LoadScenario), from
    // `VehicleRng.SeedFor(engine.Seed, EntityIndex)` -- never reseeded mid-run. Advanced by
    // exactly one draw per active vehicle per step, ONLY when VType.Sigma>0, inside
    // KraussModel.FinalizeSpeed's dawdle2 port (threaded there `ref` from
    // Engine.ComputeMoveIntent so the draw persists) -- when Sigma==0 this field is written at
    // creation and never read/advanced again, which is exactly why sigma==0 stays
    // byte-identical to every pre-C1 rung (no draw occurs, so this field's value never
    // influences the result). Each entity draws from its own copy here, never a shared/global
    // RNG, which is what keeps Engine.UseParallelPlan race-free with sigma>0 (see that
    // property's own header comment).
    public VehicleRng RngState;

    // D3: this vehicle's scheduled stops (Sim.Ingest.VehicleDef.Stops) moved to Engine's
    // `_stopsByEntity` side table (keyed by EntityIndex), populated once at LoadScenario only
    // for vehicles that actually have stops -- the managed `Queue<StopRuntime>` no longer lives
    // on every vehicle record. Front-of-queue-only access pattern is unchanged.

    // Rung 8b: SUMO's MSLCM_LC2013::myKeepRightProbability -- a stateful per-vehicle accumulator
    // for the keep-right (Rechtsfahrgebot) lane-change incentive. Starts at 0 (SUMO's ctor
    // default); only ever mutated by Engine.ExecuteMoves from the plan phase's MoveIntent
    // (CLAUDE.md rule 3 -- Plan writes only MoveIntent, never this field directly).
    public double KeepRightProbability;

    // Rung A2: SUMO's MSLCM_LC2013::mySpeedGainProbability -- a stateful per-vehicle accumulator
    // for the speed-gain (overtaking) lane-change incentive. Starts at 0 (SUMO's ctor default);
    // unlike KeepRightProbability (plan-phase, pre-move), this is decided/written by the new
    // post-move phase (Engine.DecideSpeedGainChanges) that runs AFTER ExecuteMoves -- SUMO's
    // changeLanes phase reads post-move gaps (MSNet.cpp:784/790/796), so this field is written
    // directly there rather than threaded through MoveIntent (CLAUDE.md rule 3 is still honored:
    // it is written once, after all vehicles' moves are settled, from a single frozen post-move
    // snapshot built at the top of that phase -- not a mid-query shared-state write).
    public double SpeedGainProbability;

    // C2-ii: SUMO's MSLCM_LC2013::myLookAheadSpeed -- a stateful per-vehicle "how fast have I
    // recently been driving" estimate feeding the STRATEGIC lane-change look-ahead distance
    // (laDist, MSLCM_LC2013.cpp:1227-1239) and the keep-right STAY guard's own laDist term.
    // Starts at 0.0 (SUMO's ctor default, LOOK_AHEAD_MIN_SPEED); only ever touched inside
    // Engine.TryStrategicLaneChange, which itself is gated on the vehicle's ACTUAL lane
    // differing from its route pool's target lane on the same edge -- for every single-lane-
    // per-edge scenario (and any scenario where the depart lane already is the continuing
    // lane) that gate is always false, so this field is written once at creation (0.0) and
    // never read/advanced again, exactly like RngState's own sigma==0 byte-identical argument.
    public double LookAheadSpeed;

    // B3: live reroute-around-blockage bookkeeping (DESIGN.md "Two futures" -- not a SUMO
    // field). BlockedByObstacleSeconds accumulates dt while a FUTURE edge of this vehicle's
    // remaining route is sitting under an active external obstacle; reset to 0 the moment no
    // future edge is blocked. Both start at their zero values (0 / empty), which is exactly the
    // inert-when-absent case: with RerouteThresholdSeconds left at its default (+infinity),
    // Engine.UpdateReroutes returns immediately every step and neither this field nor the
    // AvoidedEdges side table below is ever touched.
    public double BlockedByObstacleSeconds;

    // D3: this vehicle's already-routed-around-once edge set moved to Engine's
    // `_avoidedByEntity` side table (keyed by EntityIndex), lazily created only when a vehicle
    // first reroutes -- the managed `HashSet<string>` no longer lives on every vehicle record.
    // Off the hot path (reroute is opt-in via RerouteThresholdSeconds).
}
