using Sim.Core.Orca;
using Xunit;

namespace Sim.Pedestrians.Tests.Parking;

// POC-6b (docs/PEDESTRIAN-POC-PLAN.md POC-6, success condition 4 -- carried into condition 3's brief as
// a determinism requirement too): LotCouplingScenario's full car+peds+parked-boxes run, run twice from a
// fresh LotCoupling each time, yields IDENTICAL car AND pedestrian trajectories. No System.Random
// anywhere in LotCoupling/MixedTrafficCrowd/OrcaCrowd, so this is a property test of that fact -- not a
// tuned coincidence. Every step rebuilds the underlying MixedTrafficCrowd (see LotCoupling's class
// remarks); this test is exactly what proves that rebuild is still fully deterministic (fixed replay
// order of parked boxes / pedestrians / cars).
public class LotCouplingDeterminismTests
{
    [Fact]
    public void FullScenario_CarAndPedestrianTrajectories_AreIdenticalAcrossIndependentRuns()
    {
        var (carTrace1, pedTraces1) = RunScenario();
        var (carTrace2, pedTraces2) = RunScenario();

        Assert.True(carTrace1.Length > 10);
        Assert.Equal(carTrace1.Length, carTrace2.Length);
        for (var i = 0; i < carTrace1.Length; i++)
        {
            Assert.Equal(carTrace1[i].X, carTrace2[i].X, precision: 12);
            Assert.Equal(carTrace1[i].Y, carTrace2[i].Y, precision: 12);
        }

        Assert.Equal(pedTraces1.Length, pedTraces2.Length);
        for (var p = 0; p < pedTraces1.Length; p++)
        {
            Assert.True(pedTraces1[p].Length > 10);
            Assert.Equal(pedTraces1[p].Length, pedTraces2[p].Length);
            for (var i = 0; i < pedTraces1[p].Length; i++)
            {
                Assert.Equal(pedTraces1[p][i].X, pedTraces2[p][i].X, precision: 12);
                Assert.Equal(pedTraces1[p][i].Y, pedTraces2[p][i].Y, precision: 12);
            }
        }
    }

    private static (Vec2[] CarTrace, Vec2[][] PedTraces) RunScenario()
    {
        var coupling = LotCouplingScenario.NewFullScenario(out var carId, out var pedIds);

        var carTrace = new List<Vec2>();
        var pedTraces = new List<Vec2>[pedIds.Length];
        for (var i = 0; i < pedIds.Length; i++)
        {
            pedTraces[i] = new List<Vec2>();
        }

        for (var step = 0; step < LotCouplingScenario.MaxSteps; step++)
        {
            coupling.Step(LotCouplingScenario.Dt);
            carTrace.Add(coupling.CarPosition(carId));
            for (var i = 0; i < pedIds.Length; i++)
            {
                pedTraces[i].Add(coupling.PedPosition(pedIds[i]));
            }
        }

        var pedTracesArr = new Vec2[pedIds.Length][];
        for (var i = 0; i < pedIds.Length; i++)
        {
            pedTracesArr[i] = pedTraces[i].ToArray();
        }

        return (carTrace.ToArray(), pedTracesArr);
    }
}
