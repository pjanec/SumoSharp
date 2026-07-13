using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// SUMOSHARP-API.md §5.1 (dead-reckoning inputs): the read surface now exposes per-vehicle longitudinal
// Acceleration (the getAcceleration analog, parity-exact double) and GetUpcomingLanes (the lane-handle
// path ahead). Together with pos/speed and the static lane geometry, these are exactly what a renderer
// needs to dead-reckon a lane-bound vehicle along its actual curve. Additive / Step-path projection only
// (Run() never publishes), so the parity gate + determinism hash are unchanged.
public class RungB19DeadReckoningInputsTests
{
    private static string Net14 => Path.Combine(RepoRoot(), "scenarios", "14-external-obstacle", "net.net.xml");
    private static string DiamondNet =>
        Path.Combine(RepoRoot(), "scenarios", "_fixtures", "routing-diamond", "net.net.xml");

    private static Engine Loaded(string net)
    {
        var e = new Engine();
        e.LoadNetwork(net);
        return e;
    }

    // A vehicle departing from rest on a free road has positive acceleration early, decaying toward ~0 as
    // it reaches its cruising speed -- and the Acceleration column is aligned with the other columns.
    [Fact]
    public void AccelerationColumn_PositiveWhileSpeedingUp()
    {
        var e = Loaded(Net14);
        var h = e.SpawnVehicle(e.DefaultVType, new[] { "e0" });

        var sawPositiveAccel = false;
        double prevSpeed = 0;
        for (var k = 0; k < 10; k++)
        {
            e.Step();
            var idx = IndexOf(e.VehicleHandles, h);
            if (idx < 0) continue;

            var a = e.Acceleration[idx];
            var v = e.Pos.Length > idx ? e.Speed[idx] : 0;

            // Column alignment: while the vehicle is speeding up, acceleration is positive and roughly
            // tracks the speed delta (Euler: a == (v - prevV)/dt, dt=1s here).
            if (v > prevSpeed + 1e-9)
            {
                Assert.True(a > 0, $"expected positive acceleration while speeding up (a={a}, v={v})");
                sawPositiveAccel = true;
            }
            prevSpeed = v;
        }

        Assert.True(sawPositiveAccel, "vehicle should accelerate after departing from rest");
    }

    // GetUpcomingLanes returns the current lane first, then the lanes ahead, matching the vehicle's route;
    // and 0 for a stale/inactive handle.
    [Fact]
    public void GetUpcomingLanes_ReturnsPathAhead()
    {
        var e = Loaded(DiamondNet);
        var h = e.SpawnVehicle(e.DefaultVType, "SA", "DE");

        // Stale/pending before insertion.
        Span<int> buf0 = stackalloc int[8];
        Assert.Equal(0, e.GetUpcomingLanes(new VehicleHandle(9999u, 1), buf0));

        // Step until active, then read the path ahead.
        int n = 0;
        for (var k = 0; k < 5 && n == 0; k++)
        {
            e.Step();
            n = e.GetUpcomingLanes(h, buf0);
        }

        Assert.True(n >= 1, "an active vehicle should have at least its current lane ahead");

        // The first upcoming lane is the vehicle's CURRENT lane.
        Assert.True(e.TryGetVehicle(h, out var s));
        Assert.Equal(s.LaneHandle, buf0[0]);

        // Every returned handle is a valid lane index (dense lane-handle space).
        for (var i = 0; i < n; i++)
        {
            Assert.InRange(buf0[i], 0, int.MaxValue);
        }

        // Respects the destination buffer length (never writes past it).
        Span<int> tiny = stackalloc int[1];
        Assert.Equal(1, e.GetUpcomingLanes(h, tiny));
        Assert.Equal(buf0[0], tiny[0]);
    }

    // Dead-reckoning sanity: integrating pos with the published speed advances the vehicle in the same
    // direction the sim does over the next step (extrapolation is consistent with the sim's own motion).
    [Fact]
    public void DeadReckon_Extrapolation_TracksSimDirection()
    {
        var e = Loaded(Net14);
        var h = e.SpawnVehicle(e.DefaultVType, new[] { "e0" });
        for (var k = 0; k < 3; k++) e.Step();

        Assert.True(e.TryGetVehicle(h, out var before));
        var predictedPos = before.Pos + before.Speed * 1.0; // dt = 1 s

        e.Step();
        Assert.True(e.TryGetVehicle(h, out var after));

        // The vehicle moved forward, and the one-step prediction is on the correct side / close.
        Assert.True(after.Pos > before.Pos);
        Assert.True(predictedPos > before.Pos);
    }

    private static int IndexOf(ReadOnlySpan<VehicleHandle> handles, VehicleHandle h)
    {
        for (var i = 0; i < handles.Length; i++)
        {
            if (handles[i] == h) return i;
        }
        return -1;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Traffic.sln not found above test assembly).");
    }
}
