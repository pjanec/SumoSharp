using Sim.Core.Orca;
using Sim.Pedestrians.Parking;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests.Parking;

// POC-6b (docs/PEDESTRIAN-POC-PLAN.md POC-6, success condition 3): proves the car<->pedestrian coupling
// is a REAL interaction, not just "they never came near each other". Runs the identical car (same start,
// goal, dt, step budget) twice -- once with a pedestrian crossing directly in front of it, once with an
// empty pedestrian crowd (the baseline) -- and shows the car's trajectory MEASURABLY differs: either it
// slows down (speed dip) or it swerves (lateral deviation), or both.
public class LotCouplingCarYieldsTests
{
    private const double PedRadius = 0.3;
    private const double PedMaxSpeed = 1.4;
    private const double CarMaxSpeed = 3.0;
    private const double Dt = 0.2;
    private const int Steps = 500;   // 100 s budget -- generous enough to include the detour-and-return



    private static readonly Vec2 CarStart = new(0.0, 0.0);
    private static readonly Vec2 CarGoal = new(30.0, 0.0);

    // A pedestrian crossing (15, +6) -> (15, -6): squarely on the car's straight-line path, timed to
    // reach y=0 around the same time the car reaches x=15 (car: ~5-6 s at cruise; ped: 6/1.4 ~ 4.3 s).
    private static readonly Vec2 PedStart = new(15.0, 6.0);
    private static readonly Vec2 PedGoal = new(15.0, -6.0);

    private readonly ITestOutputHelper _output;

    public LotCouplingCarYieldsTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Car_SlowsOrSwerves_WhenAPedestrianCrossesItsPath_VsNoPedBaseline()
    {
        var withPed = new LotCoupling();
        var carWithPed = withPed.AddCar(CarStart, CarGoal, CarMaxSpeed);
        withPed.AddPedestrian(PedStart, PedRadius, PedMaxSpeed, PedGoal);

        var baseline = new LotCoupling();
        var carBaseline = baseline.AddCar(CarStart, CarGoal, CarMaxSpeed);

        var maxSpeedDrop = 0.0;
        var maxLateralDev = 0.0;
        var withPedFinalDist = double.MaxValue;

        for (var step = 0; step < Steps; step++)
        {
            withPed.Step(Dt);
            baseline.Step(Dt);

            var speedDrop = baseline.CarSpeed(carBaseline) - withPed.CarSpeed(carWithPed);
            if (speedDrop > maxSpeedDrop)
            {
                maxSpeedDrop = speedDrop;
            }

            var lateralDev = Math.Abs(withPed.CarPosition(carWithPed).Y - baseline.CarPosition(carBaseline).Y);
            if (lateralDev > maxLateralDev)
            {
                maxLateralDev = lateralDev;
            }

            withPedFinalDist = (withPed.CarGoal(carWithPed) - withPed.CarPosition(carWithPed)).Abs;
        }

        _output.WriteLine($"max speed drop (baseline - withPed): {maxSpeedDrop:F3} m/s");
        _output.WriteLine($"max lateral deviation: {maxLateralDev:F3} m");
        _output.WriteLine($"withPed final distance to goal: {withPedFinalDist:F3} m");

        // The car still makes it to (near) its goal -- the interaction is a yield, not a deadlock.
        Assert.True(withPedFinalDist <= 2.0, $"car with a crossing pedestrian did not reach its goal (final distance {withPedFinalDist:F2}).");

        // The interaction is REAL: a measurable slowdown or lateral swerve relative to the no-ped run.
        Assert.True(
            maxSpeedDrop > 0.1 || maxLateralDev > 0.1,
            $"no measurable difference from the no-ped baseline (max speed drop {maxSpeedDrop:F3}, max lateral deviation {maxLateralDev:F3}).");
    }
}
