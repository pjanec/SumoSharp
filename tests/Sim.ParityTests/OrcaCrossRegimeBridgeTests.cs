using Sim.Core;
using Sim.Core.Orca;
using Sim.Core.Bridge;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// THE CROSS-REGIME BRIDGE (docs/LANELESS-DIRECTION.md): lane-derived vehicles (the 1D lateral
// feasible-interval regime) and open-space crowd agents (the 2D holonomic ORCA regime) entering one
// another's neighbourhoods so SUMO traffic and non-SUMO agents MUTUALLY avoid. Validated
// behaviourally (no SUMO counterpart), each direction anchored on the case it robustly delivers:
//   - Direction B (a car swerves for a person): a stationary pedestrian standing in the lane forces a
//     laneless-RVO vehicle to swerve around them -- proven end-to-end through the real Engine via
//     CrossRegimeCoupling (Engine.CrowdSource + the world<->lane projection).
//   - Direction A (a person walks around a car): a pedestrian routes around a parked vehicle disc it
//     receives through OrcaCrowd.SetExternalObstacles -- the crowd consuming the other regime's movers.
//   - Byte-identity: with no coupling, Engine.CrowdSource stays null and both regimes are unchanged;
//     the full suite + determinism hash (run as gates) confirm the engine seam is inert when unused.
//
// Honest scope: this is a conservative behavioural coupling at the lane sim's native step. Both sides
// yield fully (a safe mutual double-yield); it is NOT a continuous cross-regime collision guarantee --
// a fast one-sided obstacle a slow agent cannot out-run can still graze (ORCA's best-effort LP3), and
// a perpendicular crosser at coarse dt relies on lateral prediction (as SUMO's B6 evasion does). A
// hard guarantee would need sub-stepping / a shared spatial solve -- the documented next refinement.
public class OrcaCrossRegimeBridgeTests
{
    private static readonly string ScenarioDir =
        Path.Combine(RepoRoot(), "scenarios", "_fixtures", "bridge-crossing");

    // The bridge-crossing vehicle: passenger 5.0 m x 1.8 m (half-width 0.9), maxSpeed 5.
    private const double VehHalfWidth = 0.9;
    private const double VehLength = 5.0;
    private const double PedRadius = 0.35;

    private readonly ITestOutputHelper _out;

    public OrcaCrossRegimeBridgeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void DirectionB_VehicleSwervesAroundPersonStandingInLane_NoOverlap_Completes()
    {
        var engine = new Engine { LanelessRvo = true };
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        // A person standing DEAD-CENTRE in the lane at x=30 (lane centre-line y=-3.6). Their goal is
        // where they stand, so they hold position -- the vehicle cannot dodge by timing, it must go
        // around. (SymmetryBreak 0: nothing symmetric here to break.)
        var crowd = new OrcaCrowd();
        var ped = crowd.Add(new Vec2(30, -3.6), PedRadius, maxSpeed: 1.5, goal: new Vec2(30, -3.6));

        var coupling = new CrossRegimeCoupling(engine, crowd, dt: 1.0, _ => (VehHalfWidth, VehLength));

        var pedStart = crowd.Position(ped);
        double peakVehLat = 0.0, minSurfaceGap = double.PositiveInfinity, lastVehX = 0.0, pedMoved = 0.0;

        for (var step = 0; step < 25; step++)
        {
            coupling.Step();
            var p = crowd.Position(ped);
            pedMoved = Math.Max(pedMoved, (p - pedStart).Abs);

            foreach (var v in coupling.LastFrame)
            {
                peakVehLat = Math.Max(peakVehLat, Math.Abs(v.PosLat));
                lastVehX = v.X;
                var gap = CapsuleDiscDistance(v.X, v.Y, v.Angle, VehLength, p.X, p.Y) - (VehHalfWidth + PedRadius);
                minSurfaceGap = Math.Min(minSurfaceGap, gap);
            }
        }

        _out.WriteLine($"Direction B: peakVehLat={peakVehLat:F2} minSurfaceGap={minSurfaceGap:F3} " +
                       $"pedMoved={pedMoved:F2} lastVehX={lastVehX:F1}");

        // Direction B: the vehicle swerved substantially to clear the standing person.
        Assert.True(peakVehLat > 1.5, $"vehicle did not swerve around the person (peak |posLat| = {peakVehLat:F2})");
        // No footprint overlap: the vehicle body (capsule) and the person (disc) never interpenetrate.
        Assert.True(minSurfaceGap > 0.0, $"vehicle overlapped the person (min surface gap = {minSurfaceGap:F3})");
        // Direction A is live in the SAME coupling: the passing car nudges the person (the crowd
        // received the vehicle as an avoidance disc). Small (the person holds their goal) but non-zero.
        Assert.True(pedMoved > 0.0, "the passing vehicle did not influence the person at all (Direction A inert)");
        // The vehicle got past the person and kept going.
        Assert.True(lastVehX > 40.0, $"vehicle did not drive past the person (ended at x={lastVehX:F1})");
    }

    [Fact]
    public void DirectionB_VehicleBrakesForBlockerItCannotSwerveAround_StopsSafely()
    {
        // The longitudinal SAFETY NET (CrossRegimeCoupling + Engine.CrowdLongitudinalConstraint): a
        // lane vehicle SWERVES around a crowd agent when it can, but BRAKES when it cannot clear
        // laterally in time. A WIDE stationary blocker (radius 2.5 m -> 5 m footprint) on a 7.2 m lane
        // cannot be swerved around (ego would need ~4 m of lateral room, the lane offers ~2.7), so the
        // vehicle must stop behind it -- and never overlap it. This is what makes the bridge
        // collision-safe when a swerve alone is insufficient ("stop for what you can't go around").
        var engine = new Engine { LanelessRvo = true };
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        const double blockerRadius = 2.5;
        var crowd = new OrcaCrowd();
        crowd.Add(new Vec2(30, -3.6), blockerRadius, maxSpeed: 0.0001, goal: new Vec2(30, -3.6));   // stationary wall

        var coupling = new CrossRegimeCoupling(engine, crowd, dt: 1.0, _ => (VehHalfWidth, VehLength));

        double minSpeedApproaching = double.PositiveInfinity, minSurfaceGap = double.PositiveInfinity;
        for (var step = 0; step < 30; step++)
        {
            var blockerBefore = crowd.Position(0);
            coupling.Step();
            foreach (var v in coupling.LastFrame)
            {
                if (v.X > 10 && v.X < 30)
                {
                    minSpeedApproaching = Math.Min(minSpeedApproaching, v.Speed);
                }

                var gap = CapsuleDiscDistance(v.X, v.Y, v.Angle, VehLength, blockerBefore.X, blockerBefore.Y)
                          - (VehHalfWidth + blockerRadius);
                minSurfaceGap = Math.Min(minSurfaceGap, gap);
            }
        }

        _out.WriteLine($"Longitudinal yield: minSpeedApproaching={minSpeedApproaching:F2} minSurfaceGap={minSurfaceGap:F3}");

        // It braked essentially to a stop behind the un-swerve-able blocker.
        Assert.True(minSpeedApproaching < 0.5, $"vehicle did not brake for the blocker (min speed {minSpeedApproaching:F2})");
        // And stayed safely behind it (never overlapped).
        Assert.True(minSurfaceGap > 0.0, $"vehicle overlapped the blocker (min surface gap {minSurfaceGap:F3})");
    }

    [Fact]
    public void DirectionA_PedestrianWalksAroundParkedVehicleDisc_NoOverlap_ReachesGoal()
    {
        // A pedestrian walking straight up (0,-6) -> (0,6) meets a PARKED vehicle disc sitting on its
        // path at the origin (radius 1.2 ~ a car's footprint). It receives the car through the crowd's
        // external-obstacle channel (Direction A) and must route around it. Finer dt (0.1) so the
        // discrete integration hugs the ORCA boundary cleanly.
        const double dt = 0.1;
        const double vehR = 1.2;
        var crowd = new OrcaCrowd { SymmetryBreak = 0.02 };   // break the head-on symmetry with the disc
        var ped = crowd.Add(new Vec2(0, -6), PedRadius, maxSpeed: 1.5, goal: new Vec2(0, 6));

        var parked = new[] { new WorldDisc(0, 0, 0, 0, vehR) };
        double minSurfaceGap = double.PositiveInfinity, peakDeflection = 0.0;

        for (var step = 0; step < 600 && !crowd.AllArrived(0.1); step++)
        {
            crowd.SetExternalObstacles(parked);
            crowd.Step(dt);
            var p = crowd.Position(ped);
            minSurfaceGap = Math.Min(minSurfaceGap, p.Abs - (PedRadius + vehR));   // |p - origin| - radii
            peakDeflection = Math.Max(peakDeflection, Math.Abs(p.X));
        }

        _out.WriteLine($"Direction A: minSurfaceGap={minSurfaceGap:F3} peakDeflection={peakDeflection:F2} " +
                       $"finalY={crowd.Position(ped).Y:F2}");

        // Direction A: the pedestrian clearly stepped AROUND the car rather than through it.
        Assert.True(peakDeflection > 1.0, $"pedestrian did not route around the car (peak |x| = {peakDeflection:F2})");
        // No overlap: it hugs the combined-radius boundary (ORCA optimum) but does not interpenetrate.
        Assert.True(minSurfaceGap > -0.02, $"pedestrian overlapped the car (min surface gap = {minSurfaceGap:F3})");
        // And it still got to the far side.
        Assert.True(crowd.Position(ped).Y > 5.5, $"pedestrian did not reach the far side (y={crowd.Position(ped).Y:F2})");
    }

    // Distance from a point to the vehicle's footprint SPINE (a capsule's axis): the segment from the
    // front (fx, fy) back along the heading (naviDeg) for `length`. Point-to-segment distance; the
    // caller subtracts (halfWidth + pedRadius) to get the surface gap.
    private static double CapsuleDiscDistance(double fx, double fy, double naviDeg, double length, double px, double py)
    {
        var navi = naviDeg * Math.PI / 180.0;
        var hx = Math.Sin(navi);
        var hy = Math.Cos(navi);
        var bx = fx - hx * length;
        var by = fy - hy * length;
        var dx = bx - fx;
        var dy = by - fy;
        var l2 = dx * dx + dy * dy;
        var t = l2 > 0.0 ? Math.Clamp(((px - fx) * dx + (py - fy) * dy) / l2, 0.0, 1.0) : 0.0;
        var cx = fx + dx * t;
        var cy = fy + dy * t;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
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
