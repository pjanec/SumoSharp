using Sim.Core;
using Sim.Core.Mixed;
using Sim.Core.Orca;

namespace Sim.Pedestrians.Parking;

// POC-6a (docs/PEDESTRIAN-POC-PLAN.md POC-6, success condition 1; docs/PEDESTRIAN-DESIGN.md §2 "The
// unified regime/lifecycle state machine"): the CAR side. Drives car(s) through the full
//     LaneTravel -> ParkingManeuver -> Parked -> ParkingManeuver -> LaneTravel
// round trip, reusing EXACTLY the mechanics §2 itself cites as already proven by Sim.Evac:
//   - LaneTravel -> ParkingManeuver: Engine.Despawn(handle) then MixedTrafficCrowd.Add(...) -- the
//     identical Despawn-then-Add sequence as EvacDirector.EnterOrcaPush
//     (src/Sim.Evac/EvacDirector.cs:343), driven through the same shaped, non-holonomic
//     MixedTrafficCrowd VehicleMover wraps (src/Sim.Evac/VehicleMover.cs).
//   - ParkingManeuver -> Parked: the maneuvering agent reaches its slot goal. Deactivate it in the
//     crowd (MixedTrafficCrowd.Deactivate, mirrors VehicleMover.Deactivate) and remember its final
//     pose as a static "parked" footprint -- fully decoupled from any crowd slot (mirrors
//     EvacDirector's `_abandoned` WorldDisc list, PEDESTRIAN-DESIGN.md §2's "`* -> Parked`" bullet),
//     so a later departure is a FRESH Add, never a reactivation of a deactivated slot.
//   - Parked -> ParkingManeuver: a fresh MixedTrafficCrowd.Add from the parked pose toward an exit
//     goal.
//   - ParkingManeuver -> LaneTravel: the maneuvering agent reaches the exit goal; Engine.SpawnVehicle
//     re-inserts it onto a lane at a chosen (route, departPos, departSpeed, departLane) -- the
//     re-insertion API PEDESTRIAN-DESIGN.md §2 calls out as "the new bit and a POC target".
//
// Geometric note (POC-6 task brief): POC-0's lane world (near the net's origin) and its parking-lot
// walkable polygon are NOT physically lane-connected -- by design, a regime transition is triggered at
// a CALLER-CHOSEN point (EnterLot's `startPos` / Depart's `exitGoal`), never derived from the
// despawned vehicle's lane-world coordinates. The two worlds do not need to share a coordinate system;
// the caller (e.g. a parking lot's entry/exit POI from PedNetworkParser.AccessPoints) supplies both
// ends of each maneuver explicitly.
//
// Determinism: no System.Random; cars are iterated in ascending CarId (== Track() call order);
// MixedTrafficCrowd itself is already deterministic (PLAN/EXECUTE double buffer, fixed order, no RNG).
public enum CarRegime
{
    LaneTravel,
    ParkingManeuver,
    Parked,
}

public enum CarLifecycleEventKind
{
    EnteredLot,         // LaneTravel -> ParkingManeuver, entering (Despawn -> Add)
    Parked,             // ParkingManeuver -> Parked (reached the slot)
    Departed,           // Parked -> ParkingManeuver, leaving (a fresh Add)
    ResumedLaneTravel,  // ParkingManeuver -> LaneTravel (reached the exit goal -> SpawnVehicle)
}

public readonly record struct CarLifecycleEvent(int CarId, CarLifecycleEventKind Kind, double Time, Vec2 Position);

public sealed class ParkingController
{
    private sealed class CarState
    {
        public required int CarId;
        public CarRegime Regime;
        public VehicleHandle? EngineHandle;
        public int MoverIndex = -1;

        // True while the current ParkingManeuver leg targets the EXIT goal (Depart), false while it
        // targets the SLOT goal (EnterLot) -- distinguishes which arrival branch Step() should take.
        public bool Departing;

        public Vec2 ParkedPosition;
        public double ParkedHeading;

        // Re-insertion parameters captured at Depart() time, consumed when the exit goal is reached.
        public VTypeHandle ExitVType;
        public IReadOnlyList<string> ExitRoute = Array.Empty<string>();
        public double ExitDepartPos;
        public double ExitDepartSpeed;
        public int ExitDepartLane;
    }

    private readonly Engine _engine;
    private readonly MixedTrafficCrowd _crowd;
    private readonly double _arriveRadius;

    private readonly Dictionary<int, CarState> _cars = new();
    private readonly List<int> _order = new();   // Track() call order -- the deterministic iteration order
    private readonly List<CarLifecycleEvent> _events = new();
    private int _nextCarId;
    private double _time;

    public ParkingController(Engine engine, double arriveRadius = 1.0, double safetyMargin = 0.3)
    {
        _engine = engine;
        _arriveRadius = arriveRadius;
        // Nonholonomic=true: parking-lot maneuvering is shaped, non-holonomic free-space driving
        // (kinematic-bicycle steering, no sideways teleport, no pivot-in-place) -- identical setup to
        // VehicleMover's Orca-push crowd (src/Sim.Evac/VehicleMover.cs:52).
        _crowd = new MixedTrafficCrowd
        {
            Nonholonomic = true,
            SafetyMargin = safetyMargin,
        };
    }

    public MixedTrafficCrowd Crowd => _crowd;
    public IReadOnlyList<CarLifecycleEvent> Events => _events;
    public double Time => _time;

    // Register a car currently driving under LaneTravel (already spawned into the Engine by the
    // caller). Returns a STABLE CarId that survives the Despawn/re-Spawn churn a raw VehicleHandle
    // cannot (a VehicleHandle goes stale the moment Despawn/SpawnVehicle runs).
    public int Track(VehicleHandle handle)
    {
        var id = _nextCarId++;
        _cars[id] = new CarState { CarId = id, Regime = CarRegime.LaneTravel, EngineHandle = handle };
        _order.Add(id);
        return id;
    }

    public CarRegime RegimeOf(int carId) => _cars[carId].Regime;

    // LaneTravel -> ParkingManeuver: Despawn from the Engine, Add to the maneuvering crowd at
    // `startPos` heading toward `slotGoal` at `maxManeuverSpeed`. `headingRad: null` (the default)
    // auto-faces the goal exactly (MixedTrafficCrowd.Add's own default), which is the safe choice for
    // a lone, unobstructed maneuver: zero initial heading error means SteerNonholonomic never needs to
    // turn, so the mover drives a straight line and converges precisely on the goal.
    public void EnterLot(
        int carId, Vec2 startPos, Vec2 slotGoal, double maxManeuverSpeed, double? headingRad = null)
    {
        var st = _cars[carId];
        if (st.Regime != CarRegime.LaneTravel || st.EngineHandle is not { } handle)
        {
            throw new InvalidOperationException($"car {carId} is not in LaneTravel (regime={st.Regime}).");
        }

        _engine.Despawn(handle);

        st.MoverIndex = _crowd.Add(VehicleClass.Car, startPos, slotGoal, headingRad, maxSpeedOverride: maxManeuverSpeed);
        st.Departing = false;
        st.EngineHandle = null;
        st.Regime = CarRegime.ParkingManeuver;
        Emit(carId, CarLifecycleEventKind.EnteredLot, startPos);
    }

    // Parked -> ParkingManeuver: a FRESH MixedTrafficCrowd.Add from the parked pose toward
    // `exitGoal`. `exitRoute`/departPos/departSpeed/departLane are the Engine.SpawnVehicle args used
    // once the exit goal is reached (Step() below) to re-insert the car onto a lane.
    public void Depart(
        int carId, Vec2 exitGoal, double maxManeuverSpeed, VTypeHandle exitVType,
        IReadOnlyList<string> exitRoute, double departPos, double departSpeed, int departLane,
        double? headingRad = null)
    {
        var st = _cars[carId];
        if (st.Regime != CarRegime.Parked)
        {
            throw new InvalidOperationException($"car {carId} is not Parked (regime={st.Regime}).");
        }

        st.MoverIndex = _crowd.Add(
            VehicleClass.Car, st.ParkedPosition, exitGoal, headingRad, maxSpeedOverride: maxManeuverSpeed);
        st.Departing = true;
        st.ExitVType = exitVType;
        st.ExitRoute = exitRoute;
        st.ExitDepartPos = departPos;
        st.ExitDepartSpeed = departSpeed;
        st.ExitDepartLane = departLane;
        st.Regime = CarRegime.ParkingManeuver;
        Emit(carId, CarLifecycleEventKind.Departed, st.ParkedPosition);
    }

    // Advance the maneuvering crowd one tick and resolve any arrivals (slot -> Parked, exit ->
    // re-inserted onto a lane). Iterates cars in ascending CarId (Track() order) for determinism --
    // independent of any dictionary enumeration order.
    public void Step(double dt)
    {
        _time += dt;
        _crowd.Step(dt);

        foreach (var carId in _order)
        {
            var st = _cars[carId];
            if (st.Regime != CarRegime.ParkingManeuver)
            {
                continue;
            }

            var idx = st.MoverIndex;
            var pos = _crowd.Position(idx);
            var goal = _crowd.Goal(idx);
            if ((goal - pos).Abs > _arriveRadius)
            {
                continue;
            }

            if (!st.Departing)
            {
                // Reached the slot: park. Deactivate the mover (it stops constraining/consuming crowd
                // compute) and remember its pose as a static footprint, decoupled from the crowd slot.
                st.ParkedPosition = pos;
                st.ParkedHeading = _crowd.Heading(idx);
                _crowd.Deactivate(idx);
                st.MoverIndex = -1;
                st.Regime = CarRegime.Parked;
                Emit(carId, CarLifecycleEventKind.Parked, pos);
            }
            else
            {
                // Reached the exit goal: leave the maneuvering crowd and re-insert onto a lane.
                _crowd.Deactivate(idx);
                st.MoverIndex = -1;
                var handle = _engine.SpawnVehicle(
                    st.ExitVType, st.ExitRoute, st.ExitDepartPos, st.ExitDepartSpeed, st.ExitDepartLane);
                st.EngineHandle = handle;
                st.Regime = CarRegime.LaneTravel;
                Emit(carId, CarLifecycleEventKind.ResumedLaneTravel, pos);
            }
        }
    }

    // ----- observability (tests + viz) -----

    public VehicleHandle? EngineHandleOf(int carId) => _cars[carId].EngineHandle;

    public bool TryGetManeuverPosition(int carId, out Vec2 position)
    {
        var st = _cars[carId];
        if (st.Regime == CarRegime.ParkingManeuver && st.MoverIndex >= 0)
        {
            position = _crowd.Position(st.MoverIndex);
            return true;
        }

        position = default;
        return false;
    }

    public Vec2? ParkedPositionOf(int carId)
    {
        var st = _cars[carId];
        return st.Regime == CarRegime.Parked ? st.ParkedPosition : null;
    }

    private void Emit(int carId, CarLifecycleEventKind kind, Vec2 pos) =>
        _events.Add(new CarLifecycleEvent(carId, kind, _time, pos));
}
