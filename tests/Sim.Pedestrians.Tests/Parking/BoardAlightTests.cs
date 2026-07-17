using Sim.Core.Orca;
using Sim.Pedestrians.Parking;
using Xunit;

namespace Sim.Pedestrians.Tests.Parking;

// POC-6a (docs/PEDESTRIAN-POC-PLAN.md POC-6, success condition 2): a pedestrian walks to a parked car
// and BOARDS (despawns from the OrcaCrowd's active set, a BoardEvent is emitted); separately, ALIGHT
// spawns a new pedestrian beside a parked car (a new active crowd agent, an AlightEvent is emitted).
public class BoardAlightTests
{
    private const double BoardRadius = 2.5;   // > the Car footprint's half-diagonal (~2.33 m)
    private const double PedRadius = 0.3;
    private const double PedMaxSpeed = 1.4;   // m/s, a plausible walking speed

    // Drives a car through LaneTravel -> ParkingManeuver -> Parked (reusing ParkingScenarioFixture)
    // and returns its parked position -- the fixture this test's board/alight scenario builds on.
    private static Vec2 ParkOneCar()
    {
        var engine = ParkingScenarioFixture.NewEngine();
        var vType = ParkingScenarioFixture.DefineCarType(engine);
        var handle = ParkingScenarioFixture.SpawnAndSettleCar(engine, vType);

        var controller = new ParkingController(engine, ParkingScenarioFixture.ManeuverArriveRadius);
        var carId = controller.Track(handle);

        var pedNet = ParkingScenarioFixture.LoadPedNetwork();
        var entryPos = ParkingScenarioFixture.EntryPoi(pedNet);
        controller.EnterLot(carId, entryPos, ParkingScenarioFixture.Slot, ParkingScenarioFixture.ManeuverMaxSpeed);

        for (var i = 0; i < 60 && controller.RegimeOf(carId) != CarRegime.Parked; i++)
        {
            engine.Step();
            controller.Step(1.0);
        }

        Assert.Equal(CarRegime.Parked, controller.RegimeOf(carId));
        return controller.ParkedPositionOf(carId)!.Value;
    }

    [Fact]
    public void Pedestrian_WalksToParkedCar_Boards_DespawnsFromCrowd_EmitsBoardEvent()
    {
        var carPos = ParkOneCar();
        var walkStart = new Vec2(carPos.X - 5.0, carPos.Y - 10.0);   // ~11.2 m away, outside BoardRadius

        var ride = new PersonRideController(BoardRadius);
        var personId = ride.AddWalking(walkStart, PedRadius, PedMaxSpeed, goal: carPos);

        Assert.Equal(PersonRegime.Walking, ride.RegimeOf(personId));
        Assert.Equal(1, ride.Crowd.Count);

        var parkedCars = new (int CarId, Vec2 Position)[] { (0, carPos) };
        IReadOnlyList<int> boardedThisStep = Array.Empty<int>();
        var now = 0.0;
        for (var i = 0; i < 40 && boardedThisStep.Count == 0; i++)
        {
            ride.Crowd.Step(1.0);
            now += 1.0;
            boardedThisStep = ride.TryBoard(parkedCars, now);
        }

        Assert.Single(boardedThisStep);
        Assert.Equal(personId, boardedThisStep[0]);

        // Walking -> Riding: no longer an active crowd agent.
        Assert.Equal(PersonRegime.Riding, ride.RegimeOf(personId));
        Assert.Equal(0, ride.Crowd.Count);

        // A BoardEvent was emitted for this person.
        var boardEvents = new List<PersonLifecycleEvent>();
        foreach (var e in ride.Events)
        {
            if (e.Kind == PersonLifecycleEventKind.Boarded)
            {
                boardEvents.Add(e);
            }
        }

        Assert.Single(boardEvents);
        Assert.Equal(personId, boardEvents[0].PersonId);
    }

    [Fact]
    public void Alight_SpawnsPedestrianBesideParkedCar_NewActiveCrowdAgent_EmitsAlightEvent()
    {
        var carPos = ParkOneCar();
        var ride = new PersonRideController(BoardRadius);

        Assert.Equal(0, ride.Crowd.Count);

        var offset = new Vec2(2.0, 0.0);   // a small, deterministic offset beside the car
        var destination = carPos + new Vec2(15.0, 0.0);
        var personId = ride.Alight(carPos, offset, PedRadius, PedMaxSpeed, destination, now: 0.0);

        // Riding -> Walking: a NEW active crowd agent appears within a small radius of the car.
        Assert.Equal(PersonRegime.Walking, ride.RegimeOf(personId));
        Assert.Equal(1, ride.Crowd.Count);

        var spawnPos = ride.PositionOf(personId);
        var distToCar = (spawnPos - carPos).Abs;
        Assert.True(distToCar <= offset.Abs + 1e-6, $"alighted pedestrian spawned {distToCar} m from the car.");

        var alightEvents = new List<PersonLifecycleEvent>();
        foreach (var e in ride.Events)
        {
            if (e.Kind == PersonLifecycleEventKind.Alighted)
            {
                alightEvents.Add(e);
            }
        }

        Assert.Single(alightEvents);
        Assert.Equal(personId, alightEvents[0].PersonId);
    }
}
