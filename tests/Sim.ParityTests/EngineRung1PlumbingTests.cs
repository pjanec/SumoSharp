using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// Task 2 plumbing test: proves ingest + engine skeleton are wired end-to-end for rung 1.
// This is NOT a parity assertion -- the Krauss/MSCFModel speed law is an intentional stub
// (see Engine.ComputeConstrainedSpeed), so with departSpeed=0 the vehicle is expected to hold
// position for the whole run. Full lane/pos/speed parity against golden.fcd.xml is Task 3.
public class EngineRung1PlumbingTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "01-single-free-flow");

    [Fact]
    public void Run_PlacesVehicleAtDepartAndEmitsTrajectoryOverTime()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var trajectory = engine.Run(80);

        Assert.Contains("veh0", trajectory.VehicleIds);

        Assert.True(trajectory.TryGet("veh0", 0.0, out var atZero));
        Assert.Equal("e0_0", atZero.Lane);
        Assert.Equal(0.0, atZero.Pos, precision: 6);
        Assert.Equal(0.0, atZero.Speed, precision: 6);

        var points = trajectory.PointsFor("veh0");
        Assert.True(points.Count > 1, "expected veh0 to be present at multiple timesteps");
        Assert.True(trajectory.TryGet("veh0", 79.0, out _));
    }

    // Fixture files (net.net.xml/rou.rou.xml/golden.*) are committed scenario inputs, not test
    // assets copied to bin/ -- resolve the repo root by walking up from the test assembly's
    // location until Traffic.sln is found (mirrors `git rev-parse --show-toplevel` without
    // depending on git being present at test time).
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
