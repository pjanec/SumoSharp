using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// Task 2 plumbing test: proves ingest + engine skeleton are wired end-to-end for rung 1.
// Task 3 landed the real Krauss/MSCFModel speed law (Engine.ComputeConstrainedSpeed /
// KraussModel), so this is folded forward to check basic wiring only (depart placement,
// multi-step emission) -- the full lane/pos/speed trajectory parity against golden.fcd.xml,
// including the vehicle's arrival/removal around t=75, is covered by Rung1ParityTests.
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

        // The vehicle accelerates from rest and reaches the 1000m route end well before t=79
        // (golden.fcd.xml: present t=0..74, absent t=75..79), so it is present at t=50 but no
        // longer present at t=79 -- that "arrived and stopped emitting" behavior is exactly
        // what Rung1ParityTests checks against the golden trajectory.
        Assert.True(trajectory.TryGet("veh0", 50.0, out _));
        Assert.False(trajectory.TryGet("veh0", 79.0, out _));
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
