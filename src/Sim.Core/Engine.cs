using Sim.Ingest;

namespace Sim.Core;

// Task 3: real Krauss/MSCFModel car-following speed law (ported from
// sumo/src/microsim/cfmodels/MSCFModel*.cpp -- see KraussModel.cs) wired into the plan/execute
// contract and lane-relative position model built in Task 2 (DESIGN.md "The plan/execute
// contract", "Seam 2").
public sealed class Engine : IEngine
{
    private NetworkModel? _network;
    private DemandModel? _demand;
    private ScenarioConfig? _config;
    private readonly List<VehicleRuntime> _vehicles = new();

    public void LoadScenario(string netXmlPath, string rouXmlPath, string sumocfgPath)
    {
        _network = NetworkParser.Parse(netXmlPath);
        _demand = DemandParser.Parse(rouXmlPath);
        _config = ScenarioConfigParser.Parse(sumocfgPath);

        _vehicles.Clear();
        foreach (var def in _demand.Vehicles)
        {
            var rawVType = _demand.VTypesById[def.TypeId];
            // vType defaults resolver (CLAUDE.md rule 6: match vType/init first): only vClass
            // and any explicit overrides (e.g. rou.xml's sigma="0") come from the raw parse;
            // everything else is a resolved SUMO vClass default (VTypeDefaults.ResolvePassenger).
            var vType = VTypeDefaults.ResolvePassenger(rawVType);
            var runtime = new VehicleRuntime { Def = def, VType = vType };

            // Rung 5: seed this vehicle's own stop queue (StopRuntime) from its immutable Def.
            // Reached/RemainingDuration start at their defaults (false/0) -- ProcessNextStop only
            // initializes RemainingDuration once the stop is actually reached.
            foreach (var stopDef in def.Stops)
            {
                runtime.Stops.Enqueue(new StopRuntime
                {
                    LaneId = stopDef.LaneId,
                    StartPos = stopDef.StartPos,
                    EndPos = stopDef.EndPos,
                    Duration = stopDef.Duration,
                });
            }

            _vehicles.Add(runtime);
        }
    }

    public TrajectorySet Run(int steps)
    {
        if (_network is null || _demand is null || _config is null)
        {
            throw new InvalidOperationException("LoadScenario must be called before Run.");
        }

        var trajectory = new TrajectorySet();
        var dt = _config.StepLength;

        for (var step = 0; step < steps; step++)
        {
            var time = _config.Begin + step * dt;

            InsertDepartingVehicles(time);
            EmitTrajectory(trajectory, time);

            // Plan/execute contract (DESIGN.md): plan reads start-of-step state and writes
            // only MoveIntent; execute applies all intents afterward. A follower must never
            // see a leader's updated position within the same step. The neighbor query is
            // built ONCE per step, here, from the same frozen start-of-step snapshot every
            // vehicle's plan phase reads (Seam 1: neighbor discovery behind an interface).
            var neighbors = LaneNeighborQuery.Build(_vehicles);
            PlanMovements(neighbors);
            ExecuteMoves(dt);
        }

        return trajectory;
    }

    // Rung 6: gap-gated departure insertion, ported from
    // sumo/src/microsim/MSLane.cpp's isInsertionSuccess (leader-gap tail, ~line 1085-1099),
    // safeInsertionSpeed (~line 1328), and checkFailure (~line 780). Vehicles queue at their
    // departLane/departPos until a leader-gap check passes; unconditional insertion (rungs
    // 1-5) was a placeholder this rung replaces.
    //
    // Derivation used here (all four vehicles in this scenario have departPos="0",
    // departSpeed="0" explicitly given, i.e. patchSpeed=false per MSInsertionControl):
    //   gap = leaderBackPos + seen - egoMinGap, called with seen = -pos (MSLane.cpp:1097)
    //       = (leaderPos - leaderLength) - insertPos - egoMinGap
    //   checkFailure(speed=0, nspeed=min(departSpeed, insertionFollowSpeed(...))=0):
    //       nspeed < speed is 0 < 0 = false -> never fails on speed with departSpeed=0.
    //   => insertion fails iff gap < 0 (INVALID_SPEED, COLLISION is in the default
    //      insertionChecks set); succeeds (at departPos/departSpeed unmodified, since
    //      patchSpeed=false leaves `speed` -- not `nspeed` -- as the value actually used) iff
    //      there is no leader, or gap >= 0.
    //
    // Scoped out (not needed by this single-lane, no-stop, no-junction scenario; a literal
    // port would also cover these but they do not exist here): MSInsertionControl's
    // RANDOM/FREE depart procedures and full retry bookkeeping, multi-lane/lane-choice,
    // junction-foe and stop-line insertion checks, follower-gap/pedestrian/shadow-lane
    // checks, rail bidi handling, and the departPos<0 "measured from lane end" convention
    // (we use departPos directly since it is always >=0 here).
    private void InsertDepartingVehicles(double time)
    {
        // Group not-yet-inserted, not-arrived candidates whose depart time has come by their
        // target insertion lane (each candidate resolves independently; grouping is only to
        // process each lane's depart queue in isolation). Ordered by target lane id for
        // deterministic per-step processing order (this scenario has exactly one lane).
        var candidatesByLane = new SortedDictionary<string, List<VehicleRuntime>>(StringComparer.Ordinal);

        foreach (var v in _vehicles)
        {
            if (v.Inserted || v.Arrived || v.Def.Depart > time)
            {
                continue;
            }

            var route = _demand!.RoutesById[v.Def.RouteId];
            var edge = _network!.EdgesById[route.Edges[0]];
            var lane = edge.Lanes.First(l => l.Index == v.Def.DepartLaneIndex);

            if (!candidatesByLane.TryGetValue(lane.Id, out var list))
            {
                list = new List<VehicleRuntime>();
                candidatesByLane[lane.Id] = list;
            }

            list.Add(v);
        }

        foreach (var (laneId, candidates) in candidatesByLane)
        {
            // FIFO order: SUMO's depart queue is processed in departure order (ties broken by
            // the route file's vehicle order); List<T>.OrderBy is a stable sort, so ties
            // preserve _demand.Vehicles/_vehicles enumeration order (rou.xml file order).
            foreach (var v in candidates.OrderBy(c => c.Def.Depart))
            {
                if (!TryInsertOnLane(v, laneId))
                {
                    // MSLane::isInsertionSuccess fails for this candidate this step -> stop
                    // attempting further (later-departing) candidates on this lane this step;
                    // they queue behind it (FIFO). A vehicle inserted earlier in THIS loop
                    // (for an earlier candidate on the same lane) becomes the leader the next
                    // candidate is checked against, since TryInsertOnLane re-scans _vehicles
                    // fresh on each call.
                    break;
                }
            }
        }
    }

    // MSLane::isInsertionSuccess's leader-gap check only (see InsertDepartingVehicles' header
    // comment for the full derivation/scope). Returns true and performs the insertion iff
    // there is no leader on the lane or gap >= 0; otherwise leaves `v` untouched and returns
    // false (queued for a later step).
    private bool TryInsertOnLane(VehicleRuntime v, string laneId)
    {
        var insertPos = v.Def.DepartPos;

        // MSLane::getLastVehicleInformation / getLeader (same-lane branch): nearest already-
        // inserted, not-arrived vehicle with Pos >= insertPos on this lane -- includes any
        // vehicle inserted earlier THIS SAME step, since this re-scans _vehicles (the engine's
        // authoritative list) on every call rather than a stale snapshot.
        VehicleRuntime? leader = null;
        foreach (var other in _vehicles)
        {
            if (!other.Inserted || other.Arrived || other.LaneId != laneId)
            {
                continue;
            }

            if (other.Kinematics.Pos >= insertPos && (leader is null || other.Kinematics.Pos < leader.Kinematics.Pos))
            {
                leader = other;
            }
        }

        if (leader is not null)
        {
            // MSLane.cpp:1097 safeInsertionSpeed(veh, seen=-pos, leaders, speed): gap =
            // leaderBackPos + seen - egoMinGap = leaderBackPos - insertPos - egoMinGap.
            var leaderBackPos = leader.Kinematics.Pos - leader.VType.Length;
            var gap = leaderBackPos - insertPos - v.VType.MinGap;

            if (gap < 0)
            {
                // checkFailure's INVALID_SPEED/COLLISION path (MSLane.cpp:1098): no safe gap
                // yet -- do not insert this step.
                return false;
            }
        }

        var route = _demand!.RoutesById[v.Def.RouteId];
        var edge = _network!.EdgesById[route.Edges[0]];
        var lane = edge.Lanes.First(l => l.Index == v.Def.DepartLaneIndex);

        v.LaneId = lane.Id;
        v.Kinematics = new Kinematics
        {
            // patchSpeed=false (departSpeed explicitly given): the vehicle is inserted at its
            // requested departPos/departSpeed unchanged -- `nspeed` (the safe-insertion-speed
            // computation) is only used for the checkFailure gate above, never applied as the
            // actual insertion speed in this branch.
            Pos = v.Def.DepartPos,
            Speed = v.Def.DepartSpeed,
            LatOffset = 0.0,
        };

        // Arrival position (route end). Rung 1's route is a single edge/lane, so summing
        // that lane's length across the route's edges gives the position at which the
        // vehicle has reached the end of its route and should be removed.
        v.ArrivalPos = route.Edges
            .Select(edgeId => _network!.EdgesById[edgeId].Lanes.First(l => l.Index == v.Def.DepartLaneIndex).Length)
            .Sum();

        v.Inserted = true;
        return true;
    }

    // Plan phase (seam 1, parallel-safe): reads start-of-step world state (including the frozen
    // `neighbors` snapshot), writes only to the owning vehicle's own MoveIntent. No shared-state
    // writes, even single-threaded -- the rung-5 stop-transition decision (see ProcessNextStop)
    // is threaded through MoveIntent.StopUpdate rather than mutating v.Stops here, so this rule
    // holds even though a vehicle's own stop bookkeeping "changes" every step it is stopped.
    private void PlanMovements(LaneNeighborQuery neighbors)
    {
        foreach (var v in _vehicles)
        {
            if (!v.Inserted || v.Arrived)
            {
                continue;
            }

            v.Intent = ComputeMoveIntent(v, neighbors);
        }
    }

    // Multi-constraint speed reducer (DESIGN.md seam 1): vPos is the MINIMUM over a collection
    // of constraints (leader car-following, junction/foe, stop line, and later shadow-lane
    // leaders), computed as a real collection/reduce even when the collection has only one
    // binding entry -- junctions/leaders slot in later without restructuring this method.
    // vPos then feeds MSCFModel.cpp's finalizeSpeed (KraussModel.FinalizeSpeed) for the
    // free-flow acceleration/deceleration bounding, exactly mirroring MSVehicle's plan-phase
    // call chain (per-constraint CF calls -> finalizeSpeed's vStop = MIN2(vPos,
    // processNextStop(vPos))).
    //
    // Plan/execute contract (DESIGN.md): this reads only start-of-step state off `v` (including
    // the front of v.Stops, never mutated here), the frozen `neighbors` snapshot, and the
    // immutable network/vType data -- no shared-state writes happen here; the resulting
    // StopTransition is handed back for ExecuteMoves to apply.
    private MoveIntent ComputeMoveIntent(VehicleRuntime v, LaneNeighborQuery neighbors)
    {
        var lane = _network!.LanesById[v.LaneId];
        var dt = _config!.StepLength;
        // default.action-step-length=1 in rung 1's config, equal to dt; kept as its own value
        // (not silently assumed == dt) since MSCFModel.cpp divides by it separately from TS.
        var actionStepLengthSecs = _config.ActionStepLength > 0 ? _config.ActionStepLength : dt;

        var laneVehicleMaxSpeed = KraussModel.LaneVehicleMaxSpeed(lane.Speed, v.VType);

        var constraints = new List<double>
        {
            // Leader car-following (MSCFModel_Krauss.cpp followSpeed -> MSCFModel.cpp
            // maximumSafeFollowSpeed): the REAL formula our resolved carFollowModel="Krauss"
            // uses -- NOT MSCFModel_KraussOrig1::vsafe (removed; see rung-4 briefing, that
            // formula is dead code once a real leader exists). No leader => +infinity
            // (non-binding), matching a gap=+infinity KraussOrig1 vsafe call's short-circuit
            // but via the real code path: simply contribute nothing when there is no leader.
            LeaderFollowSpeedConstraint(v, neighbors, dt),

            // Desired free-flow speed (MSLane::getVehicleMaxSpeed): lane speed limit adapted
            // by this vehicle's speedFactor, capped by its vType maxSpeed.
            laneVehicleMaxSpeed,

            // Stop line (rung 5): MSVehicle.cpp's planMoveInternal "process stops" block
            // (~lines 2467-2540), non-waypoint arm only. +infinity (non-binding) once reached
            // (the source's own approach-block condition `!stop.reached || (waypoint &&
            // keepStopping())` is simply false for a non-waypoint stop that IS reached) or when
            // there is no stop at all.
            StopLineConstraint(v, dt, actionStepLengthSecs),
        };

        var vPos = constraints.Min();

        // MSCFModel.cpp:191 finalizeSpeed: `vStop = MIN2(vPos, veh->processNextStop(vPos))`.
        // ProcessNextStop reads only the front stop's START-OF-STEP snapshot (Reached/
        // RemainingDuration) and returns the transition to apply at Execute -- never mutates.
        var (processedVelocity, stopUpdate) = ProcessNextStop(v, vPos, actionStepLengthSecs);
        var vStop = Math.Min(vPos, processedVelocity);

        var newSpeed = KraussModel.FinalizeSpeed(v.Kinematics.Speed, vPos, vStop, laneVehicleMaxSpeed, v.VType, dt, actionStepLengthSecs);

        return new MoveIntent
        {
            NewSpeed = newSpeed,
            LatOffset = 0.0,
            StopUpdate = stopUpdate,
        };
    }

    // MSVehicle.cpp's planMoveInternal "process stops" block (~2467-2540), non-waypoint
    // (stop.getSpeed()==0) arm only: newStopDist = seen + endPos - lane->getLength(), which on a
    // single lane (seen = laneLength - pos) collapses to `endPos + NUMERICAL_EPS - pos`;
    // stopSpeed = MAX2(cfModel.stopSpeed(this, getSpeed(), newStopDist), vMinComfortable) where
    // vMinComfortable = cfModel.minNextSpeed(getSpeed()) (line 2191). Non-binding (+infinity)
    // once the stop is reached (matches the source's own approach-block guard) or absent.
    private static double StopLineConstraint(VehicleRuntime v, double dt, double actionStepLengthSecs)
    {
        if (v.Stops.Count == 0)
        {
            return double.PositiveInfinity;
        }

        var stop = v.Stops.Peek();
        if (stop.Reached || stop.LaneId != v.LaneId)
        {
            return double.PositiveInfinity;
        }

        var newStopDist = stop.EndPos + KraussModel.NumericalEps - v.Kinematics.Pos;
        var vMinComfortable = KraussModel.MinNextSpeed(v.Kinematics.Speed, v.VType, dt);
        var stopSpeed = KraussModel.StopSpeed(newStopDist, v.Kinematics.Speed, v.VType, dt, actionStepLengthSecs);

        return Math.Max(stopSpeed, vMinComfortable);
    }

    // Ported from MSVehicle::processNextStop (sumo/src/microsim/MSVehicle.cpp:1613-1897),
    // non-waypoint (stop.getSpeed()==0) arm only, Euler branch only (the ballistic
    // `getSpeed() - getMaxDecel()` arm is dead per phase-1 CLAUDE.md/DESIGN.md). Reads only the
    // front stop's START-OF-STEP snapshot; returns (the value processNextStop would have
    // returned, the StopTransition for ExecuteMoves to apply -- null if nothing changes, exactly
    // like the source's implicit "no side effect on stop.reached" paths).
    private static (double ReturnedVelocity, StopTransition? Transition) ProcessNextStop(
        VehicleRuntime v,
        double currentVelocity,
        double actionStepLengthSecs)
    {
        if (v.Stops.Count == 0)
        {
            // MSVehicle.cpp:1614-1617: myStops.empty() -> return currentVelocity.
            return (currentVelocity, null);
        }

        var stop = v.Stops.Peek();
        if (stop.LaneId != v.LaneId)
        {
            // MSVehicle.cpp:1762's `stop.edge == myCurrEdge` guard -- not on the stop's edge/lane
            // yet; rung 5's single-lane scenario never exercises this, guarded for safety.
            return (currentVelocity, null);
        }

        if (stop.Reached)
        {
            // MSVehicle.cpp:1627-1628: stop.duration -= getActionStepLength() (every call while
            // reached, BEFORE the keepStopping() check).
            var remaining = stop.RemainingDuration - actionStepLengthSecs;

            // MSVehicle.cpp:1578-1588 keepStopping(): non-waypoint (getSpeed()==0) simplifies to
            // `duration > 0` (no triggered/collision/parking flags modeled in rung 5).
            var keepStopping = remaining > 0;

            if (!keepStopping)
            {
                // MSVehicle.cpp:1663-1679: resumeFromStopping() pops the stop; not a railway, so
                // falls through to the function's tail `return currentVelocity;` (line 1896)
                // unchanged -- the vehicle plans freely again from here.
                return (currentVelocity, new StopTransition(Resume: true, Reached: false, RemainingDuration: 0.0));
            }

            // MSVehicle.cpp:1731-1739, Euler branch: still holding -> return 0.
            return (0.0, new StopTransition(Resume: false, Reached: true, RemainingDuration: remaining));
        }

        // MSVehicle.cpp:1794-1808: reachedThreshold = stop.getReachedThreshold() - NUMERICAL_EPS;
        // getReachedThreshold() (MSStop.cpp:64) is pars.startPos for a normal (non-opposite) lane
        // stop.
        var reachedThreshold = stop.StartPos - KraussModel.NumericalEps;
        if (v.Kinematics.Pos >= reachedThreshold
            && currentVelocity <= 0.0 + KraussModel.HaltingSpeed
            && v.LaneId == stop.LaneId)
        {
            // MSVehicle.cpp:1808/1824: stop.reached = true; stop.duration = getMinDuration(time)
            // -- no until/ended modeled, so getMinDuration is just the configured duration
            // (MSStop.cpp:134-147's final `else` arm).
            return (currentVelocity, new StopTransition(Resume: false, Reached: true, RemainingDuration: stop.Duration));
        }

        // MSVehicle.cpp:1896: return currentVelocity; (no change to stop.reached this step).
        return (currentVelocity, null);
    }

    // MSLane::getLeader's gap formula (MSLane.cpp:2817/2841): gap = leaderBackPos -
    // egoMinGap - egoPos, where leaderBackPos = leaderPos - leaderLength. predMaxDecel is the
    // leader's OWN decel (MSVehicle::getCurrentApparentDecel(), which for our phase-1 vTypes
    // -- no apparent-decel override beyond the vType default -- equals the leader's vType
    // decel). Returns +infinity (non-binding) when ego has no leader on its lane.
    private static double LeaderFollowSpeedConstraint(VehicleRuntime ego, LaneNeighborQuery neighbors, double dt)
    {
        var leader = neighbors.GetLeader(ego);
        if (leader is null)
        {
            return double.PositiveInfinity;
        }

        var leaderBackPos = leader.Kinematics.Pos - leader.VType.Length;
        var gap = leaderBackPos - ego.VType.MinGap - ego.Kinematics.Pos;

        return KraussModel.FollowSpeed(
            egoSpeed: ego.Kinematics.Speed,
            gap: gap,
            predSpeed: leader.Kinematics.Speed,
            predMaxDecel: leader.VType.Decel,
            vType: ego.VType,
            dt: dt);
    }

    // Execute phase: apply each vehicle's own MoveIntent and integrate position. Euler per
    // config.sumocfg's step-method.ballistic=false: pos += newSpeed * dt (integration method
    // is a config flag per DESIGN.md, not hard-coded -- Ballistic support is a later task).
    private void ExecuteMoves(double dt)
    {
        foreach (var v in _vehicles)
        {
            if (!v.Inserted || v.Arrived)
            {
                continue;
            }

            v.Kinematics.Speed = v.Intent.NewSpeed;
            v.Kinematics.Pos += v.Intent.NewSpeed * dt;
            v.Kinematics.LatOffset = v.Intent.LatOffset;

            // Rung 5: apply the plan phase's proposed stop-queue update (Engine.ProcessNextStop).
            // This is the only place v.Stops is ever mutated (CLAUDE.md rule 3).
            if (v.Intent.StopUpdate is { } stopUpdate)
            {
                if (stopUpdate.Resume)
                {
                    v.Stops.Dequeue();
                }
                else
                {
                    var stop = v.Stops.Peek();
                    stop.Reached = stopUpdate.Reached;
                    stop.RemainingDuration = stopUpdate.RemainingDuration;
                }
            }

            // Vehicle arrival/removal: once the vehicle reaches the end of its route it is
            // marked Arrived and stops being planned/executed/emitted from the NEXT step
            // onward (the step in which it crosses the line is still emitted beforehand, since
            // EmitTrajectory runs at the top of the loop before Plan/Execute -- this reproduces
            // golden.fcd.xml's presence set exactly: present through the last in-bounds step,
            // absent afterward, with no extra "arrival" row).
            if (v.Kinematics.Pos >= v.ArrivalPos)
            {
                v.Arrived = true;
            }

            // Structural changes (lane swaps) would flush through a command buffer here at
            // step end. None exist yet -- rung 1 is a single straight lane.
        }
    }

    // The engine emits FULL double-precision trajectory values. The goldens are regenerated
    // with SUMO's `--precision` raised well above the default 2 (see scripts/regen-goldens.sh
    // and each scenario's provenance) so the committed FCD carries enough digits for the
    // per-scenario tolerance (1e-3) to be a *real* bar. Do NOT round emitted values to match a
    // low-precision golden: that would silently cap parity sensitivity at ~0.5*10^-precision
    // regardless of tolerance.json, masking genuine sub-0.01 trajectory drift. Lane-relative
    // Pos/Speed are the source of truth; x/y/angle are derived from the lane polyline.
    private void EmitTrajectory(TrajectorySet trajectory, double time)
    {
        foreach (var v in _vehicles)
        {
            if (!v.Inserted || v.Arrived)
            {
                continue;
            }

            var lane = _network!.LanesById[v.LaneId];
            var (x, y, angle) = LaneGeometry.PositionAtOffset(lane.Shape, v.Kinematics.Pos);

            trajectory.Add(new TrajectoryPoint(
                VehicleId: v.Def.Id,
                Time: time,
                Lane: v.LaneId,
                Pos: v.Kinematics.Pos,
                Speed: v.Kinematics.Speed,
                X: x,
                Y: y,
                Angle: angle,
                Acceleration: null));
        }
    }
}
