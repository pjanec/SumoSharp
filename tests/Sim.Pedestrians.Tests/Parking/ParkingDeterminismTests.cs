using System.Linq;
using Sim.Core.Orca;
using Sim.Pedestrians.Parking;
using Xunit;

namespace Sim.Pedestrians.Tests.Parking;

// POC-6a (docs/PEDESTRIAN-POC-PLAN.md POC-6, success condition 4 / determinism note carried into this
// task's brief, success condition 3): the WHOLE round trip -- drive -> park -> board -> alight ->
// depart -- run twice from a fresh Engine/controllers each time yields IDENTICAL car + pedestrian
// trajectories and an IDENTICAL lifecycle-event sequence. No System.Random is used anywhere in
// ParkingController/PersonRideController/MixedTrafficCrowd/OrcaCrowd, so this is a property test of
// that fact, not a tuned coincidence.
public class ParkingDeterminismTests
{
    private const double BoardRadius = 2.5;
    private const double PedRadius = 0.3;
    private const double PedMaxSpeed = 1.4;

    private sealed record ScenarioResult(
        Vec2[] CarTrace,
        Vec2[] WalkerTrace,
        Vec2[] AlighterTrace,
        CarLifecycleEventKind[] CarEvents,
        PersonLifecycleEventKind[] PersonEvents);

    [Fact]
    public void FullScenario_DriveParkBoardAlightDepart_IsDeterministicAcrossIndependentRuns()
    {
        var run1 = RunScenario();
        var run2 = RunScenario();

        // Sanity: the run actually exercised every regime transition + both lifecycle events (a
        // vacuous "two empty/short runs matched" would not be a meaningful determinism proof).
        Assert.Equal(4, run1.CarEvents.Length);
        Assert.Equal(2, run1.PersonEvents.Length);
        Assert.True(run1.CarTrace.Length > 4);
        Assert.True(run1.WalkerTrace.Length > 0);
        Assert.True(run1.AlighterTrace.Length > 0);

        Assert.Equal(run1.CarEvents, run2.CarEvents);
        Assert.Equal(run1.PersonEvents, run2.PersonEvents);

        Assert.Equal(run1.CarTrace.Length, run2.CarTrace.Length);
        for (var i = 0; i < run1.CarTrace.Length; i++)
        {
            Assert.Equal(run1.CarTrace[i].X, run2.CarTrace[i].X, precision: 12);
            Assert.Equal(run1.CarTrace[i].Y, run2.CarTrace[i].Y, precision: 12);
        }

        Assert.Equal(run1.WalkerTrace.Length, run2.WalkerTrace.Length);
        for (var i = 0; i < run1.WalkerTrace.Length; i++)
        {
            Assert.Equal(run1.WalkerTrace[i].X, run2.WalkerTrace[i].X, precision: 12);
            Assert.Equal(run1.WalkerTrace[i].Y, run2.WalkerTrace[i].Y, precision: 12);
        }

        Assert.Equal(run1.AlighterTrace.Length, run2.AlighterTrace.Length);
        for (var i = 0; i < run1.AlighterTrace.Length; i++)
        {
            Assert.Equal(run1.AlighterTrace[i].X, run2.AlighterTrace[i].X, precision: 12);
            Assert.Equal(run1.AlighterTrace[i].Y, run2.AlighterTrace[i].Y, precision: 12);
        }
    }

    private static ScenarioResult RunScenario()
    {
        var engine = ParkingScenarioFixture.NewEngine();
        var vType = ParkingScenarioFixture.DefineCarType(engine);
        var handle = ParkingScenarioFixture.SpawnAndSettleCar(engine, vType);

        var controller = new ParkingController(engine, ParkingScenarioFixture.ManeuverArriveRadius);
        var carId = controller.Track(handle);

        var pedNet = ParkingScenarioFixture.LoadPedNetwork();
        var entryPos = ParkingScenarioFixture.EntryPoi(pedNet);
        var exitPos = ParkingScenarioFixture.ExitPoi(pedNet);

        var carTrace = new List<Vec2>();

        // ----- drive -> park -----
        controller.EnterLot(carId, entryPos, ParkingScenarioFixture.Slot, ParkingScenarioFixture.ManeuverMaxSpeed);
        for (var i = 0; i < 60 && controller.RegimeOf(carId) != CarRegime.Parked; i++)
        {
            engine.Step();
            controller.Step(1.0);
            if (controller.TryGetManeuverPosition(carId, out var p))
            {
                carTrace.Add(p);
            }
        }

        var parkedPos = controller.ParkedPositionOf(carId)!.Value;
        carTrace.Add(parkedPos);

        // ----- board -----
        var ride = new PersonRideController(BoardRadius);
        var walkStart = new Vec2(parkedPos.X - 5.0, parkedPos.Y - 10.0);
        ride.AddWalking(walkStart, PedRadius, PedMaxSpeed, goal: parkedPos);

        var walkerTrace = new List<Vec2>();
        var parkedCars = new (int CarId, Vec2 Position)[] { (carId, parkedPos) };
        var now = 0.0;
        IReadOnlyList<int> boarded = Array.Empty<int>();
        for (var i = 0; i < 40 && boarded.Count == 0; i++)
        {
            ride.Crowd.Step(1.0);
            now += 1.0;
            walkerTrace.Add(ride.Crowd.Position(0));   // sole agent until boarded
            boarded = ride.TryBoard(parkedCars, now);
        }

        // ----- alight -----
        var offset = new Vec2(2.0, 0.0);
        var alighterId = ride.Alight(parkedPos, offset, PedRadius, PedMaxSpeed, parkedPos + new Vec2(15.0, 0.0), now);

        var alighterTrace = new List<Vec2>();
        for (var i = 0; i < 10; i++)
        {
            ride.Crowd.Step(1.0);
            now += 1.0;
            alighterTrace.Add(ride.PositionOf(alighterId));
        }

        // ----- depart -----
        var exitRoute = new[] { ParkingScenarioFixture.ExitEdge };
        controller.Depart(
            carId, exitPos, ParkingScenarioFixture.ManeuverMaxSpeed, vType,
            exitRoute, departPos: 5.0, departSpeed: 0.0, departLane: ParkingScenarioFixture.VehicleLaneIndex);

        for (var i = 0; i < 60 && controller.RegimeOf(carId) != CarRegime.LaneTravel; i++)
        {
            engine.Step();
            controller.Step(1.0);
            if (controller.TryGetManeuverPosition(carId, out var p2))
            {
                carTrace.Add(p2);
            }
        }

        var newHandle = controller.EngineHandleOf(carId)!.Value;
        for (var i = 0; i < 10; i++)
        {
            engine.Step();
            if (engine.TryGetVehicle(newHandle, out var vs))
            {
                carTrace.Add(new Vec2(vs.X, vs.Y));
            }
        }

        return new ScenarioResult(
            carTrace.ToArray(),
            walkerTrace.ToArray(),
            alighterTrace.ToArray(),
            controller.Events.Select(e => e.Kind).ToArray(),
            ride.Events.Select(e => e.Kind).ToArray());
    }
}
