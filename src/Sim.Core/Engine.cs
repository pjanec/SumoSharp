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
            _vehicles.Add(new VehicleRuntime { Def = def, VType = vType });
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
            // see a leader's updated position within the same step -- there is one vehicle
            // today, but the two-phase split is preserved so adding a second vehicle later
            // is a data change, not a control-flow rewrite.
            PlanMovements();
            ExecuteMoves(dt);
        }

        return trajectory;
    }

    private void InsertDepartingVehicles(double time)
    {
        foreach (var v in _vehicles)
        {
            if (v.Inserted || v.Def.Depart > time)
            {
                continue;
            }

            var route = _demand!.RoutesById[v.Def.RouteId];
            var edge = _network!.EdgesById[route.Edges[0]];
            var lane = edge.Lanes.First(l => l.Index == v.Def.DepartLaneIndex);

            v.LaneId = lane.Id;
            v.Kinematics = new Kinematics
            {
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
        }
    }

    // Plan phase (seam 1, parallel-safe): reads start-of-step world state, writes only to the
    // owning vehicle's own MoveIntent. No shared-state writes, even single-threaded.
    private void PlanMovements()
    {
        foreach (var v in _vehicles)
        {
            if (!v.Inserted || v.Arrived)
            {
                continue;
            }

            v.Intent = new MoveIntent
            {
                NewSpeed = ComputeConstrainedSpeed(v),
                LatOffset = 0.0,
            };
        }
    }

    // Multi-constraint speed reducer (DESIGN.md seam 1): vPos is the MINIMUM over a collection
    // of constraints (leader car-following Krauss vsafe, junction/foe, stop line, and later
    // shadow-lane leaders), computed as a real collection/reduce even though rung 1 only has
    // one binding entry -- junctions/leaders slot in later without restructuring this method.
    // vPos then feeds MSCFModel.cpp's finalizeSpeed (KraussModel.FinalizeSpeed) for the
    // free-flow acceleration/deceleration bounding, exactly mirroring MSVehicle's plan-phase
    // call chain (per-constraint CF calls -> finalizeSpeed).
    //
    // Plan/execute contract (DESIGN.md): this reads only start-of-step state off `v` and the
    // immutable network/vType data -- no shared-state writes happen here.
    private double ComputeConstrainedSpeed(VehicleRuntime v)
    {
        var lane = _network!.LanesById[v.LaneId];
        var dt = _config!.StepLength;
        // default.action-step-length=1 in rung 1's config, equal to dt; kept as its own value
        // (not silently assumed == dt) since MSCFModel.cpp divides by it separately from TS.
        var actionStepLengthSecs = _config.ActionStepLength > 0 ? _config.ActionStepLength : dt;

        var laneVehicleMaxSpeed = KraussModel.LaneVehicleMaxSpeed(lane.Speed, v.VType);

        var constraints = new List<double>
        {
            // Leader car-following (MSCFModel_KraussOrig1.cpp vsafe): rung 1 has no leader, so
            // gap=+infinity => +infinity (non-binding). Dead-but-present for rung 4+.
            KraussModel.VSafe(gap: double.PositiveInfinity, predSpeed: 0.0, predMaxDecel: 0.0, v.VType, dt),

            // Desired free-flow speed (MSLane::getVehicleMaxSpeed): lane speed limit adapted
            // by this vehicle's speedFactor, capped by its vType maxSpeed.
            laneVehicleMaxSpeed,
        };

        var vPos = constraints.Min();

        return KraussModel.FinalizeSpeed(v.Kinematics.Speed, vPos, laneVehicleMaxSpeed, v.VType, dt, actionStepLengthSecs);
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
