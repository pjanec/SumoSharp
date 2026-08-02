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
    //
    // `set` (not `init`) SOLELY for the panic-evac per-vehicle param override
    // (Engine.SetVehicleParams -- PANIC-EVAC.md R2: "flee mode is just another override/call"):
    // the external evac layer bulk-swaps a running vehicle's knobs to aggressive values by
    // assigning a `VType with { ... }` copy. Nothing on the golden/parity path ever assigns this
    // after creation, so the determinism hash (909605E965BFFE59) is byte-identical unless a caller
    // opts in -- exactly the inert-when-unused posture every other laneless/evac seam carries.
    public required ResolvedVType VType { get; set; }

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

    // C10-i: continuous lane-change maneuver (lanechange.duration > 0). While a change is in
    // progress the vehicle slides laterally over several steps instead of snapping; `LaneHandle`
    // (the emitted lane) stays the SOURCE lane until the vehicle crosses the lane midpoint, then
    // becomes the target. LcTargetHandle == -1 means "no maneuver in progress" (the instant-snap
    // default for every duration==0 scenario). LcStepsElapsed counts steps since the change was
    // committed; LcStepsTotal is round(duration/stepLength). See Engine.AdvanceLaneChanges.
    public int LcTargetHandle = -1;
    public string LcTargetId = string.Empty;
    public int LcStepsElapsed;
    public int LcStepsTotal;

    // LIVECITY-DIAGSTOP diagnostic (journal Entry 64): how the LAST maneuver ended and how recently.
    // Stamped when a continuous maneuver ends (completed at full LcStepsTotal, or aborted by
    // ClearLaneChangeManeuver), decayed one per step in AdvanceLaneChanges. While > 0 the read-buffer
    // projection publishes phase "just-completed"/"just-aborted", the engine proxy of the owner's
    // "standing-car orientation vs lane direction" metric (the IG renders the lateral slide, so a car
    // that stops during or right after a maneuver stands diagonal on screen). Never read by any
    // behavioral path; always 0 on duration==0 scenarios (no maneuver ever starts) -> byte-identical.
    public int LcEndedCooldownSteps;
    public bool LcEndedByCompletion;

    // Entry 65 refinement: the ego speed at the maneuver-end step. A completion at driving speed
    // followed by a stop leaves the car ALIGNED (the slide finished before the stop) -- only an end
    // at (near-)standstill is the diagonal-stand candidate. The projection splits the ended phases
    // on this so the DIAGSTOP count does not launder aligned stoppers into the owner's metric.
    public float LcEndSpeed;

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

    // C7-i (TASKS.md "speedFactor distribution (heterogeneous desired speeds)"): this vehicle's
    // own chosen speedFactor (MSVehicleType::computeChosenSpeedDeviation -- see
    // NormcDistribution.cs), drawn ONCE at creation (Engine.LoadScenario) from a SEPARATE,
    // SALTED VehicleRng (VehicleRng.SeedFor(Seed, EntityIndex, salt) -- never RngState above, and
    // never persisted/re-seeded after this one draw, matching SUMO's own once-at-vehicle-build
    // call site, MSVehicleControl.cpp:113). Threaded into KraussModel.LaneVehicleMaxSpeed at
    // every one of its four Engine.cs call sites in place of the old `vType.SpeedFactor`-only
    // read. When ScenarioConfig.SpeedDev<=0 (every pre-C7 scenario's `default.speeddev="0"`),
    // NormcDistribution.SampleNormc's `dev<=0` branch returns the vType's mean speedFactor
    // (1.0 for every existing scenario) WITHOUT any draw at all -- this field is then simply
    // `vType.SpeedFactor` exactly, which is exactly why every sigma=0/speeddev=0 scenario stays
    // byte-identical to every pre-C7 rung.
    public double SpeedFactor;

    // D3: this vehicle's scheduled stops (Sim.Ingest.VehicleDef.Stops) moved to Engine's
    // `_stopsByEntity` side table (keyed by EntityIndex), populated once at LoadScenario only
    // for vehicles that actually have stops -- the managed `Queue<StopRuntime>` no longer lives
    // on every vehicle record. Front-of-queue-only access pattern is unchanged.

    // Rung 8b: SUMO's MSLCM_LC2013::myKeepRightProbability -- a stateful per-vehicle accumulator
    // for the keep-right (Rechtsfahrgebot) lane-change incentive. Starts at 0 (SUMO's ctor
    // default); only ever mutated by Engine.ExecuteMoves from the plan phase's MoveIntent
    // (CLAUDE.md rule 3 -- Plan writes only MoveIntent, never this field directly).
    public double KeepRightProbability;

    // C4-vii-b: memo for ApplyKeepRightDecision's strategic stayOnBest suppressor
    // (KeepRightStrategicStay) -- "must this vehicle NOT accumulate keep-right because its right
    // neighbour is a must-avoid turn/exit lane within TURN_LANE_DIST". That answer is a pure
    // function of the current lane + remaining route, so it only changes when the vehicle changes
    // lane (or reroutes); the underlying ComputeBestLanes is an allocating route-wide pass, so
    // memoizing it here (keyed by the LaneHandle it was computed for; -1 = not yet computed) keeps
    // that pass off the per-step hot path -- it fires at most once per lane the vehicle occupies
    // instead of every step. Invalidated on reroute (CommandBuffer's ReplaceRoute resets it to -1).
    // Inert for lane-0/single-lane vehicles: ApplyKeepRightDecision returns on `RightNeighbor < 0`
    // before ever reading this.
    public int KeepRightStayCacheLane = -1;

    // DIAGNOSTIC ONLY (#15): the id of the constraint that bound this vehicle's speed on its last real
    // plan pass (see Engine.ComputeMoveIntent's argmin fold). Never read by sim logic -> parity-neutral.
    public byte BindingConstraint;

    // DIAGNOSTIC ONLY (URGENT-STRATEGIC-FOLLOW T1.1): the outcome of this step's
    // TryStrategicLaneChange, written at every exit of that method. Codes in
    // Engine.StrategicOutcomeNames. Never read by sim logic -> parity-neutral, the same
    // guarantee BindingConstraint above carries. 0 == not evaluated this step.
    public byte LcStrategicOutcome;

    // DIAGNOSTIC ONLY: the EntityIndex of the foe/leader that BindingConstraint's winning arm selected,
    // for the three constraints that identify a single blocking vehicle (crossJxnLeader=2,
    // junctionYield=10, internalJunctionAdmission=14/17) -- see the capture sites at the
    // ComputeMoveIntent fold call. -1 when the winning arm has no single identifiable foe (every other
    // binder, plus the JunctionYieldConstraint arms that block on geometry/visibility rather than a
    // specific vehicle). Never read by sim logic -> parity-neutral, same guarantee as BindingConstraint.
    public int BlockerEntityIndex = -1;

    // DEADLOCK-RING D2 (docs/DEADLOCK-RING-DESIGN.md §2): the per-entity ring-break release. When
    // >= 0, this vehicle is the elected breaker of a confirmed blocker-graph ring and its
    // stop-form constraint edges (keepClear 11 / internalJunctionAdmission 14/17) toward EXACTLY
    // this target entity are skipped, so the follow-form arms (adaptToJunctionLeader, corridor
    // FOLLOW, leaderFollow) bound its speed instead -- creep into gaps, never through bodies.
    // Written ONLY by Engine.DetectAndBreakRings (single-threaded end-of-step pass); read by the
    // plan phase next step (one-step lag, same discipline as HeldAtLinkLastStep). All -1/0 when
    // RingBreakGate is off -- never read then, so parity-neutral by construction.
    public int RingReleaseTargetEntity = -1;
    public long RingReleaseStartStep = -1;
    public double RingReleaseStartRouteDist;
    public long RingReleaseCooldownUntilStep = -1;

    // DIAGNOSTIC ONLY (#15): when JunctionYieldConstraint bound this vehicle, WHICH arm did (low 4 bits:
    // 1 cycleHold, 2 cautiousApproach, 3 sameTargetMerge, 4 externalAgent, 5 adaptToJunctionLeader,
    // 6 approachingCross) plus bit 0x80 = the ego link held a protected-green signal priority. Never read
    // by sim logic.
    public byte JunctionYieldArm;

    // DIAGNOSTIC ONLY (#15): the speed (m/s) of the junction foe that bound this vehicle via
    // JunctionYieldConstraint's foe arms (-1 = no foe arm bound this step). Tells a moving foe (real
    // cross traffic -> legitimate wait) from a ~0 foe (a car stopped ON the junction -> box block).
    public float JunctionYieldFoeSpeed = -1f;
    public bool KeepRightStaySuppress;

    // Turn-lane segregation fix (docs/GETBESTLANES-RESUME.md follow-up): the position-INDEPENDENT
    // components of SUMO's stayOnBest rule 2 (MSLCM_LC2013.cpp:1410-1418: `bestLaneOffset == 0 &&
    // neighLeftPlace * 2 < laDist`), cached alongside the VARIANT_21 memo above (same LaneHandle key,
    // same reroute invalidation). `KeepRightStayRule2Eligible` (Entry 34b: now "stay-rules
    // eligible") = the route is multi-edge and bestLanes produced an entry for ego's lane, so the
    // two offset fields below are valid and the right-direction stay rules (:1398/:1411) can run.
    // `KeepRightStayCurrOffset` = ego's lane's bestLaneOffset; `KeepRightStayRightOffsetZero` =
    // the right lane's bestLaneOffset is ALSO 0 -- the input to SUMO's :1131-1150 override, which
    // sets the effective bestLaneOffset to -1 (changing right IS changing to best) when both are
    // 0 and thereby SKIPS every stay rule; that override is what lets an equally-valid right lane
    // receive speed-gain/keep-right changes while a route-leaving or worse right lane is stayed.
    // `KeepRightStayRightContLength` = that right lane's best-lanes continuation length (SUMO's
    // neigh.length), from which ApplyKeepRightDecision derives the POSITION-dependent
    // neighLeftPlace = MAX2(0, length - posOnLane) fresh each step. Both are pure functions of
    // (lane, remaining route), so they memoize on the same key; the per-step part is only the cheap
    // distance compare + laDist. Inert (Eligible=false) for single-edge routes and any lane whose
    // right neighbour continues the route -- byte-identical there.
    public bool KeepRightStayRule2Eligible;
    public double KeepRightStayRightContLength;
    public int KeepRightStayCurrOffset;
    public bool KeepRightStayRightOffsetZero;

    // Entry 34b: the LEFT-direction mirror of the stay-rule cache above (SUMO runs the same
    // :1398/:1411 stay complex for laneOffset=+1 with neigh = the LEFT lane and laDist scaled by
    // myLookaheadLeft=2). Same memo key discipline: valid for LeftStayCacheLane == LaneHandle,
    // reset on reroute alongside the right-side cache. Eligible=false for single-edge routes, so
    // every single-edge golden's left/speed-gain path is byte-identical.
    public int LeftStayCacheLane = -1;
    public bool LeftStayEligible;
    public double LeftStayNeighContLength;
    public int LeftStayCurrOffset;
    public bool LeftStayNeighOffsetZero;

    // Entry 34 (docs/JUNCTION-REALISM-SESSION-JOURNAL.md): EGO's OWN lane's best-lanes continuation
    // length (SUMO's curr.length, MSLCM_LC2013.cpp:1135), cached in the SAME memoized pass as the
    // two fields above (same LaneHandle key, same reroute invalidation). Feeds thisLaneVSafe's
    // anticipateFollowSpeed distance in the right-direction lane-change block. 0 = no continuation
    // entry / single-edge route -> the reader falls back to the lane's own length, which is exactly
    // SUMO's LaneQ.length for a route that ends on this edge.
    public double KeepRightStayCurrContLength;

    // Rung A2: SUMO's MSLCM_LC2013::mySpeedGainProbability -- a stateful per-vehicle accumulator
    // for the speed-gain (overtaking) lane-change incentive. Starts at 0 (SUMO's ctor default);
    // unlike KeepRightProbability (plan-phase, pre-move), this is decided/written by the new
    // post-move phase (Engine.DecideSpeedGainChanges) that runs AFTER ExecuteMoves -- SUMO's
    // changeLanes phase reads post-move gaps (MSNet.cpp:784/790/796), so this field is written
    // directly there rather than threaded through MoveIntent (CLAUDE.md rule 3 is still honored:
    // it is written once, after all vehicles' moves are settled, from a single frozen post-move
    // snapshot built at the top of that phase -- not a mid-query shared-state write).
    public double SpeedGainProbability;

    // P2G-2 (docs/HIGH-DENSITY-P2G2-COOPERATIVE-LC-DESIGN.md): SUMO's MSLCM_LC2013 myVSafes speed-advice
    // channel. A blocked lane-changer's informFollower writes (as a MIN) the speed THIS vehicle should
    // slow to so the changer can cut in ("make room"); the car-following phase reads it as an additive
    // vPos cap NEXT step and clears it. +Infinity == no advice. Written/consumed ONLY when
    // Engine.CoordinatedLaneChange is on, so it stays +Infinity (inert, byte-identical) by default.
    public double CoopSpeedAdvice = double.PositiveInfinity;

    // #15 per-area realism LOD (docs/LIVE-CITY-15-PER-AREA-LOD-DESIGN.md). When true, THIS vehicle is in a
    // LOW-realism area (distant/unobserved) and its lane changing takes the cheap flow-preserving path: the
    // cooperative informFollower, the into-occupied vetoes, and the stopped keep-right float guard are all
    // skipped for it (identical to CooperativeInformFollower being off, but per-car). The host sets it each
    // step from the car's position vs the demo's static high-realism pocket; every parity/bench golden leaves
    // it false (and the global cooperative flags off), so all gating sites are byte-identical there. A pure
    // function of frozen start-of-step position => order-independent => serial==parallel preserved.
    public bool LowRealismLaneChange;

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

    // C11-ii: SUMO's MSCFModel_ACC::ACCVehicleVariables (MSCFModel_ACC.h:140-146) -- the
    // per-vehicle ACC control-mode hysteresis state (0=speed control,1=gap control) plus the
    // "written at most once per timestep" guard timestamp, both initialized to 0 (matching
    // ACCVehicleVariables' own ctor / createVehicleVariables default). Only ever read/written
    // from inside AccModel.FollowSpeed, threaded there `ref` from Engine.cs's FollowSpeedFor
    // dispatch -- and ONLY when this vehicle's OWN VType.CarFollowModel=="ACC" (see AccModel.cs's
    // own header comment). Written ONLY by the vehicle that owns it (never a leader's/foe's),
    // exactly like RngState's own per-entity dawdle draw -- see Engine.UseParallelPlan's header
    // comment for why that per-entity-write pattern is already established as parallel-safe.
    // Byte-identical for every non-ACC vType: these two fields are simply never touched.
    public int AccControlMode;
    public double AccLastUpdateTime;

    // C11-iii: SUMO's MSCFModel_CACC::CACCVehicleVariables (MSCFModel_CACC.h:222-228) --
    // `class CACCVehicleVariables : public MSCFModel_ACC::ACCVehicleVariables` literally
    // INHERITS ACC_ControlMode/lastUpdateTime rather than declaring its own copies, adding only
    // ONE new field: CACC_ControlMode (the CACC-specific speed/gap-control hysteresis mode).
    // Ported as CaccControlMode below (default 0, matching createVehicleVariables' own
    // CACC_ControlMode=0 default). The inherited pair is DELIBERATELY reused rather than
    // duplicated: a CACC-typed vehicle's embedded ACC-fallback state (used only when its leader is
    // NOT itself CACC) reuses THIS SAME vehicle's AccControlMode/AccLastUpdateTime fields above --
    // see CaccModel.cs's own header comment for the full citation, including why this reuse is
    // required for byte-parity (the shared lastUpdateTime guard's cross-call interaction), not
    // merely a storage optimization. No collision with an actually-ACC-typed vehicle: a vehicle's
    // CarFollowModel is fixed at exactly one string, so Engine.cs's dispatch never routes a given
    // vehicle through both AccModel.FollowSpeed and CaccModel.FollowSpeed. Only ever read/written
    // from inside CaccModel.FollowSpeed, threaded `ref` from Engine.cs's FollowSpeedFor dispatch,
    // and ONLY when this vehicle's OWN VType.CarFollowModel=="CACC" -- byte-identical for every
    // other vType (Krauss/IDM/ACC): this field is simply never touched.
    public int CaccControlMode;

    // C11-iv: SUMO's MSCFModel_IDM::VehicleVariables::levelOfService (MSCFModel_IDM.h:189-194)
    // -- the IDMM per-vehicle headway-adaptation memory, ctor-defaulted to 1.0 (NOT 0 -- see that
    // ctor's own initializer list). Set to 1.0 for EVERY vehicle at creation (Engine.LoadScenario),
    // not just IDMM ones: only IdmModel/Engine's IDMM dispatch arms ever read or write it (see
    // IdmModel.V's headwayTimeOverride parameter and the IDMM finalizeSpeed arm in Engine.cs's
    // ComputeMoveIntent), so this is byte-identical-inert for every non-IDMM vType, exactly like
    // AccControlMode/CaccControlMode above are for their own non-owning vTypes. At LOS=1.0 (the
    // vendored ctor default, and the value plain IDM/ACC/CACC leave it at forever since they never
    // touch it) the IDMM headway formula collapses to `tau` exactly -- see IdmModel's own
    // Idmm-adaptation comments for that derivation.
    public double LevelOfService;

    // C11-iii: the ego's OWN acceleration from the LAST COMPLETED step
    // ((newSpeed-oldSpeed)/dt), the exact analog of MSVehicle::getAcceleration() at the instant
    // CACC's cooperative gap-control law (MSCFModel_CACC.cpp:287) reads it. Written ONLY in
    // Engine.ExecuteMoves (the EXECUTE phase, right next to the pre-existing `oldSpeed` capture)
    // and read ONLY in the FOLLOWING step's PLAN phase by CaccModel's cooperative branch --
    // consistent with the frozen-start-of-step-snapshot invariant (CLAUDE.md rule 2): never a
    // leader's/foe's acceleration, only this vehicle's own. Default 0.0, matching
    // getAcceleration()'s own value before any step has executed. Written for EVERY vehicle
    // (unconditionally, alongside oldSpeed) but read by nothing except CaccModel -- so this field
    // is byte-identical-inert for every non-CACC vType, exactly like AccControlMode/
    // AccLastUpdateTime above are for every non-ACC/non-CACC vType.
    public double Acceleration;

    // C4-ii: accumulated waiting time (MSVehicle::myWaitingTime), in seconds -- the running count
    // of consecutive time the vehicle has been effectively halted. Ported from
    // MSVehicle::updateWaitingTime (MSVehicle.cpp:4081-4088): each Execute step, `+= dt` while the
    // new speed is <= SUMO_const_haltingSpeed (0.1) AND this step's acceleration is <=
    // accelThresholdForWaiting (0.5*maxAccel); otherwise reset to 0 (the vehicle is moving/
    // accelerating away). Written ONLY in Engine.ExecuteMoves (the EXECUTE phase) and read ONLY in
    // the FOLLOWING step's PLAN phase by JunctionYieldConstraint's all-way-stop arm (the
    // arrival-order tie-break: whoever has waited longer goes first) -- consistent with the
    // frozen-start-of-step-snapshot invariant (CLAUDE.md rule 2), never a foe's mid-step value.
    // Default 0.0. Read by nothing except the all-way-stop arm, so byte-identical-inert for every
    // junction that is not `type="allway_stop"` (and thus for every pre-C4-ii scenario).
    public double WaitingTime;

    // F3/isLeader T2.2 (docs/F3-ISLEADER-PORT-DESIGN.md §2, §2b, §4). Ports SUMO's three
    // `SUMOTime myJunctionEntryTime` / `myJunctionEntryTimeNeverYield` / `myJunctionConflictEntryTime`
    // fields (MSVehicle.h, initialised to `SUMOTime_MAX` at MSVehicle.cpp:1000-1002), assigned at the
    // lane-advance seam Engine.cs documents as "the ONE site a lane is fully left" (MSVehicle.cpp:
    // 4354-4368's `enterLaneAtMove` timestamp block). `long` STEP INDICES (Engine._elapsedSteps), NOT
    // `double` seconds: the eventual isLeader tie-break (T2.3) compares entry times for EXACT
    // EQUALITY, and SUMO's own `SUMOTime` is an integer millisecond count -- storing accumulated
    // `double` seconds would make that equality fire or not fire on floating-point noise, exactly the
    // determinism bug the parity bar exists to catch (design doc §4). `long.MaxValue` is the
    // `SUMOTime_MAX` sentinel.
    //
    // STAGE 1 IS PARITY-INERT BY CONSTRUCTION: these three fields are written (Engine.cs's lane-
    // advance seam) but read by NOTHING yet -- IsLeader itself is T2.3, arm 5 wiring is T2.4. Written
    // ONLY in the EXECUTE phase (ExecuteMoveVehicle), each vehicle its own fields only -- safe under
    // region-parallel ExecuteMoves, the same discipline as RouteDistanceTraveled/WaitingTime above.
    //
    // `JunctionEntryTime` ("ET"): when ego entered the junction; RELINQUISHABLE (renewed on a cont
    // turn's second stage, restored FROM `JunctionEntryTimeNeverYield` -- MSVehicle.cpp:4361's "renew
    // yielded request"; also the field SUMO's unported yield-request reset would blank, design §5b).
    public long JunctionEntryTime = long.MaxValue;

    // `JunctionEntryTimeNeverYield` ("ETN"): the SAME instant as `JunctionEntryTime` on entry, but
    // NEVER relinquished afterward -- the source `JunctionEntryTime` is restored from on a cont turn's
    // second stage, and the pair used for SUMO's same-source-lane (queue-order) case (design §3(a)).
    public long JunctionEntryTimeNeverYield = long.MaxValue;

    // `JunctionConflictEntryTime` ("CET"): when ego entered the junction's CONFLICT AREA specifically
    // -- set on a non-cont entry link and on the internal->internal (cont second-stage) hop, but
    // NEVER on a cont link's first stage (MSLink::isConflictEntryLink, MSLink.cpp:1292-1296). Staying
    // at `long.MaxValue` while a vehicle sits in a cont turn's waiting bay is LOAD-BEARING (design
    // §2b): it makes `egoET > foeET` true against any foe, so a car waiting in the bay yields to
    // everything.
    public long JunctionConflictEntryTime = long.MaxValue;

    // C8-ii: the simulation time of this vehicle's last ACTION step (MSVehicle::myLastActionTime).
    // With actionStepLength > dt a vehicle re-plans its speed only every actionStepLength seconds
    // (its "reaction time"); between action steps it continues with the acceleration decided at the
    // last one. isActionStep (MSVehicle.h:638) is `(t - myLastActionTime) % actionStepLength == 0`;
    // this field is updated to the current time on each action step and read at the top of the next
    // plan to decide whether to re-plan or hold. Initialized to NegativeInfinity so the FIRST plan
    // (on insertion) is always an action step, matching MSVehicle's `myActionStep(true)` initial
    // state. Written only in the PLAN phase (Engine.ComputeMoveIntent), this vehicle's own field
    // only -- parallel-safe exactly like RngState/LevelOfService. Entirely inert when
    // actionStepLength == dt (every pre-C8-ii scenario): the gate that reads it is skipped, so no
    // field access happens at all and behavior is byte-identical.
    public double LastActionTime;

    // C4-viii: SUMO's MSLink::ApproachingVehicleInformation::willPass -- "does this vehicle intend to
    // ENTER its upcoming junction link THIS step". SUMO computes it as
    // `setRequest = (vNext > NUMERICAL_EPS_SPEED && !abortRequestAfterMinor) || leavingCurrentIntersection`
    // (MSVehicle.cpp:2732) and registers it via MSLink::setApproaching BEFORE any MSLink::opened()
    // crossing-yield decision reads it (MSLink.cpp:935 short-circuits `if (!avi.willPass) return
    // false`). The load-bearing fact is the PLANNED vNext, not the start-of-step speed: a foe that is
    // moving at start-of-step but BRAKING TO A STOP this step (because it is itself yielding) has
    // vNext ~ 0 and willPass=false, so it must NOT block ego -- which is what unwinds the dense-grid
    // saturation gridlock. The engine has one PlanMovements pass, so this is cached once per step by a
    // PRE-PASS (Engine.ComputeWillPass) from the frozen start-of-step snapshot, BEFORE PlanMovements,
    // using each vehicle's planned vNext computed WITHOUT the foe-willPass refinement (one level of
    // approximation, mirroring setApproaching-before-opened()), then read in JunctionYieldConstraint's
    // approaching-foe arm. One bool per vehicle, zero-alloc (the KeepRightProbability/LastActionTime
    // plan-phase-cache pattern). Default false. Inert wherever no foe is braking-to-stop at a crossing
    // (every committed scenario) -- there, no vehicle's WillPass is ever read.
    public bool WillPass;

    // Determinism guard (journal Entry 30): LAST step's WillPass, copied for every active vehicle in
    // a serial prologue at the top of Engine.ComputeWillPass, BEFORE any parallel pre-pass iteration
    // runs. The internal-junction approach arm's pre-pass invocation reads a FOE's willPass while the
    // pre-pass itself is writing WillPass one-vehicle-per-parallel-iteration, so reading the live
    // field there returns last step's or this step's value depending on thread schedule (measured:
    // 3 distinct FCDs from 4 identical runs). Reading THIS field instead is deterministic and
    // parallel-safe -- and semantically the closest to what the racy read returned in practice (the
    // last-settled value). The REAL pass still reads the live, fully-populated WillPass. Own-field,
    // written only in the serial prologue.
    public bool WillPassPrev;

    // P2-G Bug-3 (generalized): set true by Engine.RedLightConstraint when THIS vehicle is held by a
    // red/yellow traffic light this step (it can brake and will stop before the stop line, so it does
    // NOT enter the junction). Read in Engine.ComputeWillPass to force WillPass=false for such a
    // vehicle -- mirroring SUMO's mySetRequest, which a vehicle stopping for a red does not set. This
    // makes the crossing gate's `!foe.WillPass` release ego from yielding to a red-held foe uniformly,
    // whether the foe is a plain or a cont (internal-junction) turn -- the ad-hoc single-lane red
    // check could not reach a cont foe (its request-matrix lane is the internal continuation, not the
    // red entry lane). Reset at the top of each ComputeMoveIntent. Default false.
    public bool HeldByRedThisStep;

    // C4-viii-b (bug C, the hold arm): set by Engine.ResolveRightBeforeLeftCycles when it breaks a
    // symmetric right-before-left response cycle. The resolver selects a maximal non-conflicting
    // subset of the cycle's links to PASS and marks the rest to YIELD; a yielding vehicle's WillPass
    // is set false, but WillPass=false alone does NOT hold a vehicle -- the crossing gate only makes
    // ego yield to a foe whose WillPass is TRUE, so in a rock-paper-scissors cycle where only ONE
    // vehicle is granted the pass, the OTHER yielders (whose sole higher-priority foe is itself a
    // yielder, WillPass=false) would see no passing foe and wrongly enter, re-locking the junction
    // mid-box. This flag is the resolver's DIRECT abort of those vehicles' entry -- the deterministic
    // analogue of SUMO's MSVehicle::planMoveInternal RNG abort clearing mySetRequest (MSVehicle.cpp:
    // 2818-2839), which holds the aborted vehicle at the stop line regardless of any foe's state.
    // Read ONLY in the real (prePass=false) JunctionYieldConstraint, and ONLY while ego is still on
    // its approach lane (the hold gates ENTRY). Reset to false for every active vehicle at the top of
    // each ResolveRightBeforeLeftCycles pass, then set true for the yielders -- so it is a fresh
    // per-step decision, never stale. Default false; inert for every scenario without an actual
    // right-before-left cycle (no committed golden has one -- the unchanged Sim.Bench hash + green
    // suite are the proof, exactly as for the WillPass write the resolver already performs).
    public bool JunctionCycleHold;

    // Perf (willPass/plan fusion): set by JunctionYieldConstraint DURING the willPass pre-pass
    // (Engine.ComputeWillPass) iff this vehicle takes the finite approaching-foe CROSSING yield --
    // the ONE and only place a real (prePass=false) plan can differ from the pre-pass plan (the
    // `!foe.WillPass` short-circuit at line ~3499 relaxes exactly that finite yield). Every other
    // prePass/real divergence is a side-effect (RngState/LevelOfService/GiveWaySide/LatOffset/
    // LastActionTime) that Engine._fusionEligible excludes at load time. When the scenario is
    // fusion-eligible and this flag is false, PlanMovements REUSES the pre-pass MoveIntent instead
    // of recomputing it -- byte-identical, and it halves the per-junction-vehicle plan cost. Reset
    // to false before each pre-pass ComputeMoveIntent; only ever written by that vehicle's own
    // pre-pass (parallel-safe, per-ego field).
    // G1 (docs/NEED-checkrewindlinklanes-partial-port.md): our stand-in for SUMO's
    // `MSVehicle::myHaveToWaitOnNextLink` -- "this vehicle chose the WAIT branch at its next link", i.e. it
    // is holding at a junction entry rather than proceeding through. `checkRewindLinkLanes`' forward pass
    // propagates blockage backward from such a vehicle (MSVehicle.cpp:5126), which is what stops cars being
    // admitted into a junction interior they cannot clear.
    //
    // ⚠ WRITTEN IN THE COMMIT PHASE, READ IN THE PLAN PHASE -- so it always carries the PREVIOUS step's
    // decision. SUMO reads it same-step because its planMove is sequential; our plan phase runs in parallel
    // over a frozen snapshot, where reading a this-step decision would be order-dependent. One step of lag
    // is the price of order-independence, and it is the only deviation in the G1 port.
    public bool HeldAtLinkLastStep;

    public bool CrossingYieldTaken;

    // Perf (willPass/plan fusion): the pre-pass tells PlanMovements to REUSE this vehicle's already-
    // computed Intent (skip the second ComputeMoveIntent). True iff the scenario is _fusionEligible,
    // the vehicle was WillPassRelevant (so the pre-pass actually computed its Intent), and it did NOT
    // take the crossing yield (CrossingYieldTaken == false). Own-field, set once per step.
    public bool ReuseIntent;

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

    // Rung ER3 (give-way): this vehicle's current "clear the way for an emergency vehicle" intent,
    // recomputed each PLAN step (Engine.DetectGiveWaySide) from the frozen start-of-step snapshot.
    // 0 = none, -1 = clear toward the right lane edge, +1 = clear toward the left lane edge. Read
    // by the ER4 (multi-lane, Engine.DecideGiveWayChanges) and ER5 (single-lane lateral drift,
    // Engine.ComputeLateralEvasion) execution arms, and exported via VehicleExportSnapshot. Written
    // ONLY in the PLAN phase by the owning vehicle (parallel-safe exactly like LevelOfService /
    // WillPass). Default 0, and left 0 for every scenario with no active bluelight EV in range
    // (Engine._anyBluelight short-circuits detection), so byte-identical-inert wherever give-way
    // does not trigger.
    public int GiveWaySide;

    // Rung ER4 (give-way execution, multi-lane): true iff the approaching blue-light EV that
    // triggered this vehicle's give-way intent is in this vehicle's OWN lane (so this vehicle
    // should VACATE the lane by changing to an adjacent one, rather than merely drifting to the
    // edge). Computed alongside GiveWaySide in the PLAN phase from the frozen start-of-step
    // snapshot (Engine.DetectGiveWay), read by the ER4 lane-change arm (Engine.TryGiveWayLaneChange).
    // Default false; left false whenever no EV shares this vehicle's lane, so inert wherever
    // give-way does not trigger a lane change.
    public bool GiveWayEvSameLane;

    // Rung OV1 (opposite-direction overtaking): true iff this vehicle (a) is held up behind a
    // slower same-lane leader and (b) sees the oncoming (opposite-direction) lane clear far enough
    // ahead to consider overtaking through it. Recomputed each PLAN step (Engine.DetectOvertake)
    // from the frozen start-of-step snapshot, written only by the owning vehicle (parallel-safe like
    // GiveWaySide), and exported via VehicleExportSnapshot. Default false; left false for every vType
    // without lcOpposite (Engine._anyLcOpposite short-circuits detection), so inert wherever
    // opposite-direction overtaking is absent. Consumed by the OV2/OV3 decision/execution arms.
    public bool OvertakeActive;

    // Rung D2 (OV3 return-gap enforcement): while overtaking, the EntityIndex of the same-lane leader
    // this vehicle is passing (-1 = none). Remembered when the overtake commits (DetectOvertake held
    // up), and read AFTER this vehicle has nosed ahead of that leader -- once GetLeader no longer
    // returns it -- so the overtaker stays spilled until it is a safe following gap AHEAD of the
    // just-passed leader before recentering, instead of cutting back in the instant it edges past.
    // Transient plan-phase state like OvertakeActive (not captured in the file snapshot); default -1.
    public int OvertakePassedLeaderIndex = -1;

    // Rung OV4 (cooperative oncoming shift): true iff this vehicle is an oncoming driver that sees a
    // spilled opposite-direction overtaker (a bidi-lane vehicle encroaching across the centre line)
    // closing head-on within range, and is therefore pulling to its OWN outer lane edge to widen the
    // corridor for the overtake -- the mirror of the ER3/ER5 give-way drift. Recomputed each PLAN
    // step (Engine.DetectCooperativeShift) from the frozen start-of-step snapshot (it reads the
    // overtaker's already-committed LatOffset, never a same-step plan flag, so it is parallel-safe
    // like GiveWaySide/OvertakeActive), and exported via VehicleExportSnapshot. Default false; left
    // false for every vType wherever no vType has lcOpposite (Engine._anyLcOpposite short-circuits
    // detection), so inert wherever opposite-direction overtaking is absent. Consumed by
    // ComputeLateralEvasion, which drifts ego to its outer edge while it is set.
    public bool CooperativeShift;

    // DR2 (dead-reckoning coordination, issue #3): true iff this vehicle's laneless-RVO / cross-regime
    // lateral solve ACTIVELY COUPLED to a neighbour or crowd agent THIS step (ComputeRvoLateral's
    // forbCount > 0) -- i.e. it is mid-swerve, so its short-horizon lateral is a reactive manoeuvre, NOT
    // linearly lane-predictable. The DR publisher reads this (via Engine.GetDrModel / the DrModels column)
    // to classify the vehicle FreeKinematic-while-swerving vs LaneArc. Pure plan-phase SIDE-WRITE:
    // nothing on the Run()/golden path reads it, so it is byte-identical (determinism hash unmoved), and
    // it is only ever WRITTEN under LanelessRvo && _sublane -- left false for every parity scenario, so a
    // plain lane vehicle is always LaneArc. Recomputed fresh each real plan pass.
    public bool LateralManoeuvre;

    // P1E-4 (HIGH-DENSITY-P1E-DESIGN.md §1A, §9): device.rerouting equip + periodic-reroute
    // schedule -- DISTINCT from the obstacle-triggered BlockedByObstacleSeconds/AvoidedEdges
    // above (that is UpdateReroutes' own one-shot detour mechanism; this is MSDevice_Routing's
    // periodic congestion-reactive device). RerouteEquipped is drawn ONCE at creation
    // (Engine.BuildRuntime) from a salted per-entity RNG against ScenarioConfig.RerouteProbability
    // -- false (and NextRerouteTime left at +infinity, so it can never become due) for every
    // vehicle whenever ScenarioConfig.ReroutePeriod<=0 (every pre-P1E-4 scenario), which is
    // exactly the inert-when-absent guard. NextRerouteTime is the next sim-time this vehicle's
    // periodic reroute pass (Engine.UpdatePeriodicReroutes) is due; it re-arms by
    // `+= ReroutePeriod` each time the vehicle is actually considered (whether or not the
    // candidate route was installed -- §1B). LastRoutingTime is the sim-time of this vehicle's
    // own last periodic routing attempt (MSDevice_Routing's `myLastRouting`), read by the
    // skip-if-stale-weights guard (§1A: skip iff LastRoutingTime >= the last edge-weight
    // adaptation time) -- NegativeInfinity so a vehicle that has never routed is never
    // spuriously treated as "weights unchanged since I last routed".
    public bool RerouteEquipped;
    public double NextRerouteTime = double.PositiveInfinity;
    public double LastRoutingTime = double.NegativeInfinity;

    // P1E-6 (HIGH-DENSITY-P1E-DESIGN.md §11): true once this vehicle's ONE pre-insertion reroute
    // attempt (Engine.InsertDepartingVehicles' pre-insertion pass) has run -- regardless of whether
    // it actually installed a new route (structural failure / identical-edge-list short-circuit
    // both still set this, exactly like the periodic pass always re-arms NextRerouteTime whether or
    // not it installs, §1B). Guards the pass to fire AT MOST ONCE per vehicle lifetime, at/after its
    // own depart time, distinct from and never touched by the periodic schedule
    // (NextRerouteTime/LastRoutingTime above keep firing depart+period, +period, ... unchanged --
    // SUMO does both). Defaults false for every vehicle; `new VehicleRuntime{...}` in
    // Engine.BuildRuntime always constructs a fresh instance (never carries over a recycled slot's
    // old value), so a recycled EntityIndex's occupant starts with PreInsertionRerouteDone=false
    // again, same as any other freshly-built vehicle -- no separate reset code needed.
    public bool PreInsertionRerouteDone;

    // P1F-2 (HIGH-DENSITY-P1F-DESIGN.md §2, §5): true while this vehicle is mid-teleport -- it has
    // been lifted off its lane by the jam-check phase (MSVehicleTransfer::add) and is sitting in
    // Engine._transferQueue awaiting re-insertion (MSVehicleTransfer::checkInsertions). While set,
    // the vehicle is excluded from EVERY active-vehicle query (VehicleQuery, BuildActiveIndices,
    // BuildRegionActive, the parallel-emit scan, TryResolveActive) exactly as SUMO's transferring
    // vehicle is off the network (not planned, not moved, not emitted in FCD). Cleared when the
    // re-insertion pass places it back on a lane. Default false; ONLY ever set when
    // ScenarioConfig.TimeToTeleport>0, so every pre-P1F scenario (time-to-teleport=-1) never
    // touches it and the active-query filters stay byte-identical.
    public bool InTransfer;

    // Entry 62: set by ExecuteMoveVehicle's C4-vii-c strand clamp (wrong-lane dead-end, pos
    // pinned at the lane end, speed 0); consumed the same step by Engine.RescueStrandedVehicles'
    // serial sibling-snap pass. Never set on any committed-golden path (the clamp itself is not
    // reached there).
    public bool StrandClamped;

    // GAP-2 (docs/SUMOSHARP-SERVE-PATH-DROP-IN.md §2, docs/SERVE-PATH-PLAN.md): SUMO's
    // MSDevice_Tripinfo::myWaitingTime (MSDevice_Tripinfo::notifyMove, MSDevice_Tripinfo.cpp:179-193)
    // -- a TRIP-TOTAL accumulator, DISTINCT from WaitingTime above (which resets to 0 the instant the
    // vehicle moves/accelerates away -- that field is SUMO's *consecutive*-halt timer,
    // MSVehicle::updateWaitingTime, used by the all-way-stop tie-break). This one NEVER resets: each
    // Engine.ExecuteMoves step, while the vehicle is NOT currently halted at a reached <stop>
    // (!IsStoppedAtStop) AND newSpeed <= haltingSpeed AND this step's acceleration <=
    // accelThresholdForWaiting (0.5*maxAccel) -- the SAME predicate WaitingTime already evaluates --
    // `+= dt`. Written ONLY in Engine.ExecuteMoves; read ONLY by Engine's trip-arrival capture
    // (CaptureCompletedTrips) to populate a completed trip's tripinfo `waitingTime`. Default 0.0;
    // BuildRuntime always constructs a fresh VehicleRuntime (append or recycled slot), so this is
    // always 0 at a vehicle's insertion -- no separate reset path needed.
    public double TripWaitingTime;

    // GAP-2: SUMO's MSVehicle::myTimeLoss (MSVehicle::updateTimeLoss, MSVehicle.cpp:4095-4105) -- a
    // TRIP-TOTAL accumulator of "how much slower than the lane's free-flow speed was I", in seconds.
    // Each Engine.ExecuteMoves step, while the vehicle is NOT currently halted at a reached <stop>
    // (!IsStoppedAtStop): `+= dt * (vmax - newSpeed) / vmax`, where vmax is this lane's
    // KraussModel.LaneVehicleMaxSpeed for this vehicle (lane speed limit x this vehicle's SpeedFactor,
    // capped at VType.MaxSpeed) -- SUMO's `myLane->getVehicleMaxSpeed(this)`. Never resets. Written
    // ONLY in Engine.ExecuteMoves; read ONLY by CaptureCompletedTrips. Default 0.0, same fresh-
    // instance-per-insertion guarantee as TripWaitingTime above.
    public double TripTimeLoss;

    // GAP-2: the RESOLVED depart position -- this vehicle's Kinematics.Pos at the exact moment of
    // insertion (TryInsertOnLane's `insertPos`), captured ONCE there and never touched again. This is
    // SUMO's `-veh.getPositionOnLane()` seed for myRouteLength at NOTIFICATION_DEPARTED
    // (MSDevice_Tripinfo.cpp:239-245): unlike Kinematics.Pos (which advances as the vehicle moves and
    // wraps at each lane boundary), this stays the vehicle's ORIGINAL insertion offset for the whole
    // trip, which CaptureCompletedTrips needs for the routeLength formula (routeLength = sum of full
    // lengths of every route edge before the arrival edge, minus this depart pos, plus the configured
    // arrival pos). Default 0.0 -- always overwritten by TryInsertOnLane before a vehicle can become
    // Inserted (and therefore before it can ever Arrive).
    public double DepartPosResolved;

    // GAP-2 follow-up (routeLength across device.rerouting reroutes): SUMO's MSDevice_Tripinfo
    // myRouteLength -- a RUNNING distance accumulator, NOT a route-pool recomputation. Initialized to
    // -departPos at insertion (TryInsertOnLane), += the length of each lane the vehicle fully LEAVES
    // (Engine.ExecuteMoveVehicle's lane-boundary crossing), and routeLength = this + arrivalPos at
    // arrival (BuildCompletedTripInfo). Because it accumulates as the vehicle drives, it survives a
    // device.rerouting ReplaceRoute (which rebuilds the lane-sequence pool for only the REMAINING
    // route) -- the prior pool-sum formula lost all distance travelled BEFORE a reroute (reported
    // 0.33-0.49x on rerouted trips). For a non-rerouted trip this equals the old pool-sum exactly
    // (all lanes left == all pool lanes before the arrival lane), so single-route goldens (66/72) are
    // byte-identical. Default 0; TryInsertOnLane always overwrites it before the vehicle can move.
    public double RouteDistanceTraveled;

    // GAP-3 (docs/SUMOSHARP-SERVE-PATH-DROP-IN.md §3, SUMO's MSLane.cpp:2212 `veh->isParking()` ->
    // MSVehicleTransfer::add -- the vehicle is lifted OFF the lane's vehicle list): true ONLY while
    // this vehicle is currently `Reached` at a `<stop parkingArea=...>` (StopRuntime.IsParking) --
    // set/cleared in Engine.ExecuteMoves' stop-transition apply block, the SAME step
    // StopRuntime.Reached flips (matching scenario 48's golden: the parked lateral offset appears
    // the step AFTER insertion, not at t=0, and disappears the same step the vehicle resumes).
    // Consumed in two places, both GATED so a scenario with no parkingArea stop is byte-identical:
    // (1) ComputeMoveIntent's LatOffset selection (off-lane bay offset while parked instead of the
    // usual evasion/sublane drift); (2) LaneNeighborQuery.Refill/RefillRegion, which now excludes an
    // IsParked vehicle from every per-lane neighbor bucket -- so it is invisible to GetLeader/
    // GetNeighborLeader/GetRearmost/OnLane exactly like SUMO's real off-lane transfer, and a
    // following through-vehicle is never blocked by it. Default false; BuildRuntime always
    // constructs a fresh instance, so a recycled EntityIndex's occupant starts unparked.
    public bool IsParked;
}
