using Sim.Core.Orca;
using Sim.Pedestrians.Parking;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests.Parking;

// POC-6b (docs/PEDESTRIAN-POC-PLAN.md POC-6, success condition 3): the CORE mutual-avoidance property
// test. A car maneuvers across LotCouplingScenario's lot toward its slot while three pedestrians cross
// its path (two of them weaving through gaps in a row of parked-car boxes). Over the whole run:
//   (a) no pedestrian ever overlaps the car's TRUE oriented-box footprint;
//   (b) no pedestrian ever overlaps any parked-car box;
//   (c) the car never drives over a pedestrian -- the SAME distance-relationship as (a), just narrated
//       from the car's side (there is only one car-vs-ped-centre distance to violate).
// Also asserts progress: the car gets meaningfully closer to its slot and every pedestrian reaches its
// goal -- avoidance did not deadlock anyone.
public class LotCouplingMutualAvoidanceTests
{
    private const double OverlapEps = 1e-2;   // 1 cm tolerance on the "no overlap" bound
    private const double PedArriveRadius = 1.0;

    private readonly ITestOutputHelper _output;

    public LotCouplingMutualAvoidanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void CarAndPedestrians_NeverOverlap_AndBothMakeProgress()
    {
        var coupling = LotCouplingScenario.NewFullScenario(out var carId, out var pedIds);

        var carInitialDist = (coupling.CarGoal(carId) - coupling.CarPosition(carId)).Abs;
        var pedInitialDist = new double[pedIds.Length];
        for (var i = 0; i < pedIds.Length; i++)
        {
            pedInitialDist[i] = (coupling.PedGoal(pedIds[i]) - coupling.PedPosition(pedIds[i])).Abs;
        }

        var minPedCarSep = double.MaxValue;
        var minPedBoxSep = double.MaxValue;
        var carArrived = false;

        for (var step = 0; step < LotCouplingScenario.MaxSteps; step++)
        {
            coupling.Step(LotCouplingScenario.Dt);

            var carFootprint = coupling.CarFootprint(carId);
            var boxFootprints = coupling.ParkedCarFootprints;

            for (var i = 0; i < pedIds.Length; i++)
            {
                var pedPos = coupling.PedPosition(pedIds[i]);
                var pedRadius = coupling.PedRadius(pedIds[i]);

                var distToCar = LotCoupling.DistanceToBox(pedPos, carFootprint);
                if (distToCar < minPedCarSep)
                {
                    minPedCarSep = distToCar;
                }

                foreach (var box in boxFootprints)
                {
                    var distToBox = LotCoupling.DistanceToBox(pedPos, box);
                    if (distToBox < minPedBoxSep)
                    {
                        minPedBoxSep = distToBox;
                    }
                }

                Assert.True(
                    distToCar >= pedRadius - OverlapEps,
                    $"step {step}: pedestrian {i} at {pedPos.X:F3},{pedPos.Y:F3} overlaps the car footprint " +
                    $"(distance {distToCar:F3} < radius {pedRadius:F3}).");
            }

            foreach (var box in boxFootprints)
            {
                for (var i = 0; i < pedIds.Length; i++)
                {
                    var pedPos = coupling.PedPosition(pedIds[i]);
                    var pedRadius = coupling.PedRadius(pedIds[i]);
                    var distToBox = LotCoupling.DistanceToBox(pedPos, box);
                    Assert.True(
                        distToBox >= pedRadius - OverlapEps,
                        $"step {step}: pedestrian {i} at {pedPos.X:F3},{pedPos.Y:F3} overlaps a parked-car box " +
                        $"(distance {distToBox:F3} < radius {pedRadius:F3}).");
                }
            }

            if (!carArrived && (coupling.CarGoal(carId) - coupling.CarPosition(carId)).Abs <= 1.0)
            {
                carArrived = true;
            }
        }

        _output.WriteLine($"min ped-car separation: {minPedCarSep:F3} m (ped radius {LotCouplingScenario.PedRadius:F2} m)");
        _output.WriteLine($"min ped-box separation: {minPedBoxSep:F3} m");

        // ----- progress: avoidance did not deadlock anyone -----
        var carFinalDist = (coupling.CarGoal(carId) - coupling.CarPosition(carId)).Abs;
        Assert.True(
            carFinalDist < carInitialDist * 0.25 || carArrived,
            $"car made insufficient progress toward its slot: {carInitialDist:F2} -> {carFinalDist:F2}.");

        for (var i = 0; i < pedIds.Length; i++)
        {
            var finalDist = (coupling.PedGoal(pedIds[i]) - coupling.PedPosition(pedIds[i])).Abs;
            Assert.True(
                finalDist <= PedArriveRadius,
                $"pedestrian {i} did not reach its goal (final distance {finalDist:F2} > {PedArriveRadius}).");
        }

        // Sanity: the run actually put the populations close enough to matter (a vacuous "they never
        // came near each other" would not exercise avoidance at all).
        Assert.True(minPedCarSep < 5.0, $"pedestrians never came near the car (min separation {minPedCarSep:F2}).");
        Assert.True(minPedBoxSep < 5.0, $"pedestrians never came near a parked box (min separation {minPedBoxSep:F2}).");
    }
}
