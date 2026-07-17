using Sim.Core;
using Sim.Pedestrians.Parking;
using Xunit;

namespace Sim.Pedestrians.Tests.Parking;

// POC-6a (docs/PEDESTRIAN-POC-PLAN.md POC-6, success condition 1): a car's full
//     LaneTravel -> ParkingManeuver -> Parked -> ParkingManeuver -> LaneTravel
// round trip, asserting the OBSERVABLE state at every transition (in-Engine vs in-crowd), that the
// parked position lies inside the parkinglot polygon, and that the re-inserted car carries a valid
// lane + non-negative speed.
public class ParkingRoundTripTests
{
    [Fact]
    public void Car_RoundTrip_ThroughAllFourRegimes_WithValidObservableStateAtEachTransition()
    {
        var engine = ParkingScenarioFixture.NewEngine();
        var vType = ParkingScenarioFixture.DefineCarType(engine);
        var handle = ParkingScenarioFixture.SpawnAndSettleCar(engine, vType);

        var controller = new ParkingController(engine, ParkingScenarioFixture.ManeuverArriveRadius);
        var carId = controller.Track(handle);

        // ----- LaneTravel: present in the Engine read surface -----
        Assert.Equal(CarRegime.LaneTravel, controller.RegimeOf(carId));
        Assert.True(engine.TryGetVehicle(handle, out var laneState));
        Assert.True(laneState.Speed >= 0.0);

        var pedNet = ParkingScenarioFixture.LoadPedNetwork();
        var lotPolygon = ParkingScenarioFixture.ParkingLotPolygon(pedNet).Shape;
        var entryPos = ParkingScenarioFixture.EntryPoi(pedNet);
        var exitPos = ParkingScenarioFixture.ExitPoi(pedNet);

        // ----- LaneTravel -> ParkingManeuver -----
        controller.EnterLot(carId, entryPos, ParkingScenarioFixture.Slot, ParkingScenarioFixture.ManeuverMaxSpeed);

        Assert.Equal(CarRegime.ParkingManeuver, controller.RegimeOf(carId));
        Assert.False(engine.TryGetVehicle(handle, out _), "car should have left the Engine's read surface.");
        Assert.True(controller.TryGetManeuverPosition(carId, out var maneuverStart));
        Assert.Equal(entryPos.X, maneuverStart.X, precision: 6);
        Assert.Equal(entryPos.Y, maneuverStart.Y, precision: 6);

        // ----- ParkingManeuver -> Parked -----
        var parked = false;
        for (var i = 0; i < 60 && !parked; i++)
        {
            engine.Step();
            controller.Step(1.0);
            parked = controller.RegimeOf(carId) == CarRegime.Parked;
        }

        Assert.True(parked, "car did not reach Parked within the step budget.");
        var parkedPos = controller.ParkedPositionOf(carId);
        Assert.NotNull(parkedPos);
        Assert.True(
            ParkingScenarioFixture.PointInPolygon(parkedPos!.Value, lotPolygon),
            $"parked position {parkedPos} lies outside the parkinglot polygon.");

        // ----- Parked -> ParkingManeuver (depart) -----
        var exitRoute = new[] { ParkingScenarioFixture.ExitEdge };
        controller.Depart(
            carId, exitPos, ParkingScenarioFixture.ManeuverMaxSpeed, vType,
            exitRoute, departPos: 5.0, departSpeed: 0.0, departLane: ParkingScenarioFixture.VehicleLaneIndex);

        Assert.Equal(CarRegime.ParkingManeuver, controller.RegimeOf(carId));

        // ----- ParkingManeuver -> LaneTravel (re-insertion) -----
        var resumed = false;
        for (var i = 0; i < 60 && !resumed; i++)
        {
            engine.Step();
            controller.Step(1.0);
            resumed = controller.RegimeOf(carId) == CarRegime.LaneTravel;
        }

        Assert.True(resumed, "car did not resume LaneTravel within the step budget.");

        var newHandle = controller.EngineHandleOf(carId);
        Assert.NotNull(newHandle);
        Assert.NotEqual(handle, newHandle!.Value);   // a genuinely NEW handle, not the stale original

        // The re-inserted car is Pending until a subsequent Step() actually places it -- settle
        // exactly like the initial spawn (Engine.SpawnVehicle's own doc comment).
        VehicleState reinserted = default;
        var inserted = false;
        for (var i = 0; i < 10 && !inserted; i++)
        {
            engine.Step();
            inserted = engine.TryGetVehicle(newHandle.Value, out reinserted);
        }

        Assert.True(inserted, "re-inserted car did not appear in the Engine within the settle budget.");
        Assert.False(string.IsNullOrEmpty(reinserted.LaneId));
        Assert.True(reinserted.Speed >= 0.0);

        // Lifecycle events fired, once each, in the expected order.
        var kinds = new List<CarLifecycleEventKind>();
        foreach (var e in controller.Events)
        {
            kinds.Add(e.Kind);
        }

        Assert.Equal(
            new[]
            {
                CarLifecycleEventKind.EnteredLot,
                CarLifecycleEventKind.Parked,
                CarLifecycleEventKind.Departed,
                CarLifecycleEventKind.ResumedLaneTravel,
            },
            kinds);
    }
}
