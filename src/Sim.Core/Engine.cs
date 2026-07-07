using Sim.Ingest;

namespace Sim.Core;

// Task 2: ingest + engine skeleton wired to the rung-1 scenario. This is PLUMBING -- it
// reproduces SUMO's plan/execute contract and lane-relative position model (DESIGN.md "The
// plan/execute contract", "Seam 2") but intentionally does NOT implement the Krauss/MSCFModel
// speed law yet (see ComputeConstrainedSpeed below). It is therefore expected to be OUT of
// tolerance against golden.fcd.xml until Task 3 lands the real car-following math.
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
            _vehicles.Add(new VehicleRuntime { Def = def });
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
            v.Inserted = true;
        }
    }

    // Plan phase (seam 1, parallel-safe): reads start-of-step world state, writes only to the
    // owning vehicle's own MoveIntent. No shared-state writes, even single-threaded.
    private void PlanMovements()
    {
        foreach (var v in _vehicles)
        {
            if (!v.Inserted)
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

    // Multi-constraint speed reducer (DESIGN.md seam 1): the next speed is the MINIMUM over a
    // collection of constraints (leader car-following, junction/foe, stop line, and later
    // shadow-lane leaders). Only one constraint source exists today, but the collection/reduce
    // shape is built now on purpose: junctions already require "min over N constraints" for
    // correct phase-1 behavior, so laying it down here means shadow lanes/junctions slot in
    // later without restructuring this method.
    //
    // TASK 3: real Krauss/MSCFModel speed constraint goes here. This is intentionally a STUB
    // that yields the vehicle's current speed unchanged (a no-op "hold speed" constraint) --
    // with departSpeed=0 the vehicle never moves. Do NOT implement the Krauss safe-speed /
    // free-flow acceleration law in this task.
    private static double ComputeConstrainedSpeed(VehicleRuntime v)
    {
        var constraints = new List<double>
        {
            v.Kinematics.Speed, // STUB constraint: "hold current speed" -- replace in Task 3
        };

        return constraints.Min();
    }

    // Execute phase: apply each vehicle's own MoveIntent and integrate position. Euler per
    // config.sumocfg's step-method.ballistic=false: pos += newSpeed * dt (integration method
    // is a config flag per DESIGN.md, not hard-coded -- Ballistic support is a later task).
    private void ExecuteMoves(double dt)
    {
        foreach (var v in _vehicles)
        {
            if (!v.Inserted)
            {
                continue;
            }

            v.Kinematics.Speed = v.Intent.NewSpeed;
            v.Kinematics.Pos += v.Intent.NewSpeed * dt;
            v.Kinematics.LatOffset = v.Intent.LatOffset;

            // Structural changes (lane swaps) would flush through a command buffer here at
            // step end. None exist yet -- rung 1 is a single straight lane.
        }
    }

    private void EmitTrajectory(TrajectorySet trajectory, double time)
    {
        foreach (var v in _vehicles)
        {
            if (!v.Inserted)
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
