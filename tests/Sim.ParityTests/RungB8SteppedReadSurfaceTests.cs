using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// SUMOSHARP-API.md §5: the host-facing stepped read surface (Engine.Step + the columnar SoA spans +
// TryGetVehicle). Behavioural tests proving (1) Step() advances the simulation IDENTICALLY to Run() and
// publishes a faithful snapshot, (2) the columns are internally consistent and match random access, and
// (3) the VehicleHandle staleness/validation contract holds. Uses the free-flow follower in scenario 14.
public class RungB8SteppedReadSurfaceTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "14-external-obstacle");

    private static Engine Load()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));
        return engine;
    }

    // Step call k publishes the end-of-step-(k-1) state, which equals Run()'s trajectory point at the same
    // clock time (start-of-step-k). Comparing bit-exact proves Step() and Run() run the SAME simulation
    // (EmitTrajectory is pure) AND that the read projection reproduces the FCD geometry exactly.
    [Fact]
    public void Step_PublishesStateBitIdenticalToRun()
    {
        var a = Load();
        var trajA = a.Run(45);

        var b = Load();
        var matched = 0;
        for (var k = 1; k <= 40; k++)
        {
            b.Step();
            Assert.Equal(k, b.StepCount);

            if (!trajA.TryGet("follower", b.CurrentTime, out var pA))
            {
                continue; // follower not present at this clock time in Run's trajectory yet
            }

            Assert.True(TryFind(b, "follower", out var s), $"follower missing from read buffer at step {k}");
            Assert.Equal(pA.Pos, s.Pos);       // parity-exact double, bit-identical
            Assert.Equal(pA.Speed, s.Speed);
            Assert.Equal((float)pA.X, s.X);     // render float == float-cast of the same projection
            Assert.Equal((float)pA.Y, s.Y);
            matched++;
        }

        Assert.True(matched > 5, $"expected the follower to be comparable at many steps, got {matched}");
    }

    // The columnar spans are all the same length (== VehicleCount), same-index == same vehicle, PosZ is 0
    // on a 2-D net, and the columns agree with TryGetVehicle's random-access view.
    [Fact]
    public void ReadColumns_AreConsistent_AndMatchRandomAccess()
    {
        var e = Load();
        for (var k = 0; k < 15 && e.VehicleCount == 0; k++)
        {
            e.Step();
        }

        var n = e.VehicleCount;
        Assert.True(n > 0, "expected at least one active vehicle after stepping");
        Assert.Equal(n, e.VehicleHandles.Length);
        Assert.Equal(n, e.PosX.Length);
        Assert.Equal(n, e.PosY.Length);
        Assert.Equal(n, e.PosZ.Length);
        Assert.Equal(n, e.Angle.Length);
        Assert.Equal(n, e.Speed.Length);
        Assert.Equal(n, e.Pos.Length);
        Assert.Equal(n, e.PosLat.Length);
        Assert.Equal(n, e.LaneHandles.Length);

        for (var i = 0; i < n; i++)
        {
            Assert.Equal(0.0f, e.PosZ[i]); // 2-D net -> zero elevation
        }

        // Column index 0 and the handle at index 0 must resolve to the same vehicle.
        var h0 = e.VehicleHandles[0];
        Assert.True(e.TryGetVehicle(h0, out var s0));
        Assert.Equal(e.PosX[0], s0.X);
        Assert.Equal(e.PosY[0], s0.Y);
        Assert.Equal(e.Pos[0], s0.Pos);
        Assert.Equal(e.PosLat[0], s0.PosLat);
        Assert.Equal(e.LaneHandles[0], s0.LaneHandle);
        Assert.Equal((float)e.Speed[0], (float)s0.Speed);
    }

    // VehicleHandle validation: the None sentinel, an out-of-range index, and a wrong generation are all
    // rejected; a live handle round-trips.
    [Fact]
    public void TryGetVehicle_ValidatesHandles()
    {
        var e = Load();
        for (var k = 0; k < 15 && e.VehicleCount == 0; k++)
        {
            e.Step();
        }

        Assert.True(e.VehicleCount > 0);

        Assert.False(e.TryGetVehicle(VehicleHandle.None, out _));
        Assert.False(e.TryGetVehicle(new VehicleHandle(99999, 1), out _));

        var h = e.VehicleHandles[0];
        Assert.True(e.TryGetVehicle(h, out _));

        var wrongGen = new VehicleHandle(h.Index, (ushort)(h.Generation + 1));
        Assert.False(e.TryGetVehicle(wrongGen, out _));
    }

    private static bool TryFind(Engine e, string vehicleId, out VehicleState found)
    {
        foreach (var h in e.VehicleHandles)
        {
            if (e.TryGetVehicle(h, out var s) && s.VehicleId == vehicleId)
            {
                found = s;
                return true;
            }
        }

        found = default;
        return false;
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
