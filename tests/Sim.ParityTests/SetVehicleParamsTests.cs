using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// PANIC-EVAC.md R2 / T1.1 cond.4: BEHAVIOURAL proof that Engine.SetVehicleParams actually reaches the
// car-following model (not just a struct mutation) -- a runtime MaxSpeed cap measurably caps the
// vehicle's cruising speed within a few steps, and a stale handle is rejected.
public class SetVehicleParamsTests
{
    private static readonly string NetPath =
        Path.Combine(RepoRoot(), "scenarios", "evac-grid", "net.net.xml");

    [Fact]
    public void SetVehicleParams_MaxSpeedOverride_CapsCruisingSpeed()
    {
        var engine = new Engine();
        engine.LoadNetwork(NetPath);
        var vtype = engine.DefineVType(new VTypeParams { VClass = "passenger", Sigma = 0.0 });
        var handle = engine.SpawnVehicle(vtype, "left1A1", "D1right1", departPos: 5.0);

        // Warm up: let the vehicle accelerate up to the grid's free speed (~13.9 m/s).
        for (var i = 0; i < 20 && !(engine.TryGetVehicle(handle, out var warm) && warm.Speed > 6.0); i++)
        {
            engine.Step();
        }

        Assert.True(engine.TryGetVehicle(handle, out var before));
        Assert.True(before.Speed > 6.0, $"expected cruising speed > 6.0 before override, got {before.Speed}");

        Assert.True(engine.SetVehicleParams(handle, new VehicleParamOverride { MaxSpeed = 3.0 }));

        for (var i = 0; i < 15; i++)
        {
            engine.Step();
        }

        Assert.True(engine.TryGetVehicle(handle, out var after));
        Assert.True(after.Speed <= 3.5, $"expected speed <= 3.5 after MaxSpeed=3.0 override, got {after.Speed}");
    }

    [Fact]
    public void SetVehicleParams_StaleHandle_ReturnsFalse()
    {
        var engine = new Engine();
        engine.LoadNetwork(NetPath);
        var vtype = engine.DefineVType(new VTypeParams { VClass = "passenger", Sigma = 0.0 });
        var handle = engine.SpawnVehicle(vtype, "left1A1", "D1right1", departPos: 5.0);

        engine.Step();
        Assert.True(engine.Despawn(handle));

        Assert.False(engine.SetVehicleParams(handle, default));
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
