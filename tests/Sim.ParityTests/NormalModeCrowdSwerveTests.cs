using System;
using System.IO;
using Sim.Core;
using Sim.Core.Bridge;
using Sim.Core.Orca;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// Q6 (option b): a NORMAL (phase-1) lane-mode vehicle -- NOT laneless, NOT sublane -- swerves around a
// CrowdSource crowd agent it can clear, instead of hard-stopping. Braking for a crowd agent already
// worked in normal mode (CrowdLongitudinalConstraint); this adds the SWERVE, by making CrowdSource
// agents first-class dodgeable threats in ComputeLateralEvasion and preferring the swerve for them.
//
// Byte-identity: the whole crowd path is gated on Engine.CrowdSource != null, which no committed golden
// sets and only a CrossRegimeCoupling attaches -- so the determinism hash and every golden are
// unaffected (the full suite + hash gate confirm it). This fixture (bridge-crossing-normal) has NO
// lateral-resolution, so the vehicle is a plain phase-1 vehicle whose lateral intent comes from
// ComputeLateralEvasion (the non-sublane path).
public class NormalModeCrowdSwerveTests
{
    private static readonly string ScenarioDir =
        Path.Combine(RepoRoot(), "scenarios", "_fixtures", "bridge-crossing-normal");

    private const double VehHalfWidth = 0.9;   // passenger 1.8 m wide
    private const double VehLength = 5.0;
    private const double PedRadius = 0.35;

    private readonly ITestOutputHelper _out;

    public NormalModeCrowdSwerveTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void NormalModeVehicle_SwervesAroundCrowdPedestrian_InsteadOfHardStopping()
    {
        var engine = new Engine();   // NORMAL mode: no LanelessRvo, fixture has no lateral-resolution
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        // A person standing dead-centre in the 7.2 m lane at x=30 (centreline y=-3.6), holding position
        // (goal == start). The vehicle can clear them laterally, so option (b) makes it go AROUND.
        var crowd = new OrcaCrowd();
        var ped = crowd.Add(new Vec2(30, -3.6), PedRadius, maxSpeed: 1.0, goal: new Vec2(30, -3.6));

        var coupling = new CrossRegimeCoupling(engine, crowd, dt: 1.0, _ => (VehHalfWidth, VehLength));

        double peakVehLat = 0.0, minGap = double.PositiveInfinity, lastX = 0.0;
        for (var step = 0; step < 25; step++)
        {
            coupling.Step();
            var p = crowd.Position(ped);
            foreach (var v in coupling.LastFrame)
            {
                peakVehLat = Math.Max(peakVehLat, Math.Abs(v.PosLat));
                lastX = v.X;
                var gap = RectDiscDistance(v.X, v.Y, VehLength, VehHalfWidth, p.X, p.Y) - PedRadius;
                minGap = Math.Min(minGap, gap);
            }
        }

        _out.WriteLine($"normal-mode swerve: peakVehLat={peakVehLat:F2} minGap={minGap:F3} lastX={lastX:F1}");

        // Swerved substantially rather than staying centred and stopping.
        Assert.True(peakVehLat > 1.0, $"normal-mode vehicle did not swerve for the crowd agent (peak |posLat| = {peakVehLat:F2})");
        // Never overlapped the person.
        Assert.True(minGap > 0.0, $"vehicle overlapped the person (min gap = {minGap:F3})");
        // Drove on past the person instead of stopping short of it.
        Assert.True(lastX > 40.0, $"vehicle did not drive past the person (ended at x={lastX:F1})");
    }

    // Distance from a disc centre to the vehicle's axis-aligned footprint rectangle [X-Length, X] x
    // [Y-HalfWidth, Y+HalfWidth]. The lane runs along +X, so the footprint is axis-aligned.
    private static double RectDiscDistance(double x, double y, double length, double halfWidth, double px, double py)
    {
        var dx = Math.Max(Math.Max((x - length) - px, px - x), 0.0);
        var dy = Math.Max(Math.Max((y - halfWidth) - py, py - (y + halfWidth)), 0.0);
        return Math.Sqrt(dx * dx + dy * dy);
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
