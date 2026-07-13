using Sim.Core;
using Sim.Evac;
using Xunit;

namespace Sim.ParityTests;

// PANIC-EVAC.md R4 / §8.3 unit coverage for the PURE overload of BlockedDetector.Update (no Engine
// needed): the dwell-timer logic in isolation -- accumulate while stationary, reset the instant it
// isn't, and Forget() restarts the dwell from zero.
public class BlockedDetectorTests
{
    private static readonly VehicleHandle Handle = new(0u, 1);

    [Fact]
    public void Update_AccumulatesDwellAndCrossesThresholdAtCumulativeTime()
    {
        var detector = new BlockedDetector(dwellSeconds: 3.0);

        Assert.False(detector.Update(Handle, stationary: true, dt: 1.0)); // dwell = 1
        Assert.False(detector.Update(Handle, stationary: true, dt: 1.0)); // dwell = 2
        Assert.True(detector.Update(Handle, stationary: true, dt: 1.0));  // dwell = 3 >= 3.0
    }

    [Fact]
    public void Update_NonStationaryCallResetsDwell()
    {
        var detector = new BlockedDetector(dwellSeconds: 3.0);

        Assert.False(detector.Update(Handle, stationary: true, dt: 1.0)); // dwell = 1
        Assert.False(detector.Update(Handle, stationary: true, dt: 1.0)); // dwell = 2
        Assert.True(detector.Update(Handle, stationary: true, dt: 1.0));  // dwell = 3 -> blocked

        Assert.False(detector.Update(Handle, stationary: false, dt: 1.0)); // reset to 0

        Assert.False(detector.Update(Handle, stationary: true, dt: 1.0)); // dwell = 1, not blocked again yet
    }

    [Fact]
    public void Forget_RestartsDwellFromZero()
    {
        var detector = new BlockedDetector(dwellSeconds: 3.0);

        Assert.False(detector.Update(Handle, stationary: true, dt: 1.0)); // dwell = 1
        Assert.False(detector.Update(Handle, stationary: true, dt: 1.0)); // dwell = 2

        detector.Forget(Handle);

        Assert.False(detector.Update(Handle, stationary: true, dt: 1.0)); // dwell restarted at 0 -> 1
    }

    [Fact]
    public void Dwell_ReflectsAccumulatedValue()
    {
        var detector = new BlockedDetector(dwellSeconds: 3.0);

        Assert.Equal(0.0, detector.Dwell(Handle));

        detector.Update(Handle, stationary: true, dt: 1.0);
        Assert.Equal(1.0, detector.Dwell(Handle));

        detector.Update(Handle, stationary: true, dt: 1.5);
        Assert.Equal(2.5, detector.Dwell(Handle));

        detector.Update(Handle, stationary: false, dt: 1.0);
        Assert.Equal(0.0, detector.Dwell(Handle));
    }
}
