namespace Sim.Core;

// D9 (FastDataPlane ECS readiness -- info/replication export SEAM, READINESS ONLY, TASKS.md
// line ~651). This is the "ECS component -> external descriptor" SOURCE shape FDP's own
// `IDescriptorTranslator` consumes (see FastDataPlane Docs/architectural-rules.md: a translator
// reads a component snapshot by value and produces an external/network descriptor from it --
// this project does NOT build that translator or any network descriptor, only the in-house
// shape a later one would be handed). One `VehicleExportSnapshot` is built ONCE per active
// vehicle per Export-phase frame (see Engine.EmitTrajectory) and carries exactly the identity +
// exportable component state a replication/info layer would need: the FDP-shaped `Entity`
// handle (D5) a descriptor translator keys its external id off, the plain `EntityIndex` (the
// same stable slot key the engine's own side tables already use), the SUMO-facing `VehicleId`,
// the frame `Time`, and the lane-relative + derived-global fields `TrajectoryPoint` already
// carries (`Lane`/`Pos`/`Speed`/`X`/`Y`/`Angle`).
//
// `readonly struct`, always passed `in` (see `ISimExportObserver.OnVehicleExported`): this is
// the CLAUDE.md rule 4 zero-alloc discipline / D4's "no `new`/boxing in the hot path" rule
// applied to the export seam -- building this snapshot is a stack copy of doubles + two small
// strings + a struct handle, not a heap allocation, so registering zero observers costs nothing
// beyond the copy already implied by reading `v.Kinematics`/`v.LaneId` (which EmitTrajectory did
// unconditionally before this rung too), and registering N observers costs one extra virtual
// call per observer per vehicle per frame, never an extra allocation.
public readonly struct VehicleExportSnapshot
{
    // D5's FDP-shaped handle -- the id a later IDescriptorTranslator-style consumer would key
    // its external/network descriptor off, instead of the raw VehicleId string.
    public readonly Entity Entity;

    // The plain int slot key (== Entity.Index) -- mirrors the key the engine's own side tables
    // (lane-sequence pool, stop/avoided-edge tables) already use, for a consumer that wants the
    // cheap array-index form rather than the FDP handle.
    public readonly int EntityIndex;

    // SUMO-facing identity -- same string TrajectoryPoint.VehicleId carries (Def.Id).
    public readonly string VehicleId;

    // SUMO-facing vType id (Def.TypeId, e.g. "truck0") -- the FCD `type=` attribute a consumer
    // joins against .rou.xml <vType> to recover length/width/vClass. VB-0: added so an FCD
    // writer built on this seam can round-trip SUMO's FCD `type` field; inert for every other
    // consumer (TrajectoryPoint never carried it and still doesn't).
    public readonly string VehicleType;

    public readonly double Time;
    public readonly string Lane;
    public readonly double Pos;
    public readonly double Speed;
    public readonly double X;
    public readonly double Y;
    public readonly double Angle;

    // Phase 2 (sublane): the lane-relative lateral offset (== Kinematics.LatOffset, +left of
    // travel), SUMO's FCD `posLat`. 0 for every lane-centred vehicle, so inert for phase-1 output.
    public readonly double PosLat;

    // Rung ER3 (give-way): the vehicle's current give-way intent, computed each plan step from
    // the frozen start-of-step snapshot -- 0 = none, -1 = clear toward the right lane edge, +1 =
    // clear toward the left lane edge (see Engine.DetectGiveWaySide). Always 0 for every vehicle
    // in every scenario that has no active blue-light emergency vehicle in range, so it is inert
    // wherever give-way is not triggered. Exposed here so a behavioral observer/test can assert
    // the DETECTION independently of the ER4/ER5 execution (which shows up in Lane / LatOffset).
    public readonly int GiveWaySide;

    // Rung OV1 (opposite-direction overtaking): whether this vehicle currently intends to overtake
    // through the oncoming lane (held up behind a slower leader with the opposite lane clear ahead).
    // Always false wherever no vType sets lcOpposite, so inert for every existing scenario. Exposed
    // so a behavioral test can assert the DECISION independently of the (later) execution.
    public readonly bool OvertakeActive;

    // Rung OV4 (cooperative oncoming shift): whether this vehicle is an oncoming driver currently
    // pulling to its own outer lane edge to make room for a spilled opposite-direction overtaker
    // closing head-on. Always false wherever no vType sets lcOpposite, so inert for every existing
    // scenario. Exposed so a behavioral test can assert the cooperative DECISION independently of the
    // resulting lateral drift (which shows up in Y).
    public readonly bool CooperativeShift;

    // P0-D (--summary-output aggregates, MSNet.cpp:607-647/MSVehicleControl.cpp:516-543): the
    // CURRENT lane's edge's speed limit (MSEdge::getSpeedLimit() analog -- max over the edge's own
    // lanes, since a lane may in principle post a different limit than its neighbours) and whether
    // this vehicle is presently held at the front of its own stop queue with that stop `Reached`
    // (MSVehicleControl::getStoppedVehiclesCount()'s per-vehicle predicate). Both computed once per
    // vehicle per Export-phase frame from the SAME start-of-frame state FCD/TrajectoryPoint already
    // reads (EmitTrajectory's serial loop), so a `SummaryWriterObserver` registered here aggregates
    // over EXACTLY the frame the committed FCD trajectory does. Inert (0.0/false) for every consumer
    // that does not read them -- FcdWriterObserver, TrajectorySet and every prior scenario/test never
    // touch these two fields, so this is additive, not a behavior change.
    public readonly double EdgeSpeedLimit;
    public readonly bool IsStoppedAtStop;

    /// <summary>
    /// DIAGNOSTIC ONLY: which constraint won <c>ComputeMoveIntent</c>'s <c>Math.Min</c> fold for this
    /// vehicle this step (the "binder"). Never read by the simulation.
    /// </summary>
    /// <remarks>
    /// Carried on the snapshot rather than looked up through <see cref="Engine.BindingConstraints"/>,
    /// because that span is indexed by the READ-BUFFER column and is only populated when a host pumps
    /// the read buffer — whereas <see cref="EntityIndex"/> is the ECS entity index. Reading one with the
    /// other silently produced 100% out-of-range on the first attempt at a binder log, which is the
    /// whole reason this field exists. Legend: see <c>Sim.Harness.BinderLogObserver.BinderNames</c>.
    /// </remarks>
    public readonly byte BindingConstraint;

    /// <summary>
    /// DIAGNOSTIC ONLY: the <see cref="EntityIndex"/> of the foe/leader vehicle that
    /// <see cref="BindingConstraint"/>'s winning arm selected, for the constraints that identify a
    /// single blocking vehicle (crossJxnLeader=2, junctionYield=10, internalJunctionAdmission=14/17).
    /// -1 when the winning binder has no single identifiable foe. Never read by the simulation --
    /// see <see cref="Engine"/>'s <c>VehicleRuntime.BlockerEntityIndex</c> capture sites.
    /// </summary>
    public readonly int BlockerEntityIndex;

    public VehicleExportSnapshot(
        Entity entity,
        int entityIndex,
        string vehicleId,
        string vehicleType,
        double time,
        string lane,
        double pos,
        double speed,
        double x,
        double y,
        double angle,
        int giveWaySide = 0,
        bool overtakeActive = false,
        bool cooperativeShift = false,
        double posLat = 0.0,
        double edgeSpeedLimit = 0.0,
        bool isStoppedAtStop = false,
        byte bindingConstraint = 0,
        int blockerEntityIndex = -1)
    {
        Entity = entity;
        EntityIndex = entityIndex;
        VehicleId = vehicleId;
        VehicleType = vehicleType;
        Time = time;
        Lane = lane;
        Pos = pos;
        Speed = speed;
        X = x;
        Y = y;
        Angle = angle;
        GiveWaySide = giveWaySide;
        OvertakeActive = overtakeActive;
        CooperativeShift = cooperativeShift;
        PosLat = posLat;
        EdgeSpeedLimit = edgeSpeedLimit;
        IsStoppedAtStop = isStoppedAtStop;
        BindingConstraint = bindingConstraint;
        BlockerEntityIndex = blockerEntityIndex;
    }
}
