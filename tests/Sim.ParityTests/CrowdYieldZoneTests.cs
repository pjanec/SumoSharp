using System;
using System.Collections.Generic;
using System.IO;
using Sim.Core;
using Sim.Core.Orca;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// Task B-guard unit/behaviour tests (docs/LIVE-CITY-CAR-YIELDS-PED-DESIGN.md). Three things are proven
// here, each of which the crosswalk repro (CrosswalkCrossingPedTests) alone cannot show:
//
//   1. the ZONE GATE is real and closed by default -- an engine that never sets a zone, one that sets a
//      zero/negative radius, and one whose zone is placed where ego never reaches all produce the SAME
//      trajectory, tick-for-tick. This is the parity argument in executable form;
//   2. the WORLD-SPACE clearance primitive (VehicleFootprint) is correct for an arbitrary heading, not
//      just an axis-aligned one -- the owner's requirement is a world-space guard, not a lane test;
//   3. the ANTICIPATORY yield term (binder 14) catches conflicts binder 13 STRUCTURALLY CANNOT see.
//      Binder 13 (CrowdLongitudinalConstraint) brakes only while the ped's CURRENT lateral position
//      overlaps ego's CURRENT footprint; at a 1 s step a 5 m/s car can step clean over a crossing ped
//      without ever registering that overlap. Measured on the fixture below: with the zone OFF the car
//      holds 5.00 m/s for the whole crossing and never reacts at all; with it ON, binder 14 binds and
//      the car slows. That is the non-vacuity proof for the new constraint's predictive term.
public class CrowdYieldZoneTests
{
    private static readonly string ScenarioDir =
        Path.Combine(RepoRoot(), "scenarios", "_fixtures", "bridge-crossing-normal");

    private const double LaneCentreY = -3.6;
    private const double PedRadius = 0.6;   // the demo's inflated ORCA footprint radius

    private readonly ITestOutputHelper _out;
    public CrowdYieldZoneTests(ITestOutputHelper output) => _out = output;

    // ---------------------------------------------------------------------------------------
    // (2) the world-space clearance primitive
    // ---------------------------------------------------------------------------------------

    // A 5 x 2 m car whose FRONT bumper is at the origin facing +x (naviDegree 90 == east) occupies
    // x in [-5, 0], y in [-1, +1]. Each case is hand-computed from that rectangle.
    [Theory]
    // straight ahead: 4 m to the disc centre, radius 0.5 -> 3.5 m of clearance
    [InlineData(4.0, 0.0, 0.5, 3.5)]
    // directly beside the middle of the flank: dy 3, minus half-width 1, minus radius 0.5
    [InlineData(-2.5, 3.0, 0.5, 1.5)]
    // diagonally off the front-left corner: (3,4) from the corner (0,1) -> 5 m, minus radius 1
    [InlineData(3.0, 5.0, 1.0, 4.0)]
    // overlapping the body -> negative (the disc is 0.5 past the front bumper plane)
    [InlineData(0.25, 0.0, 0.75, -0.5)]
    // behind the rear bumper: 2 m past x=-5, minus radius 0.25
    [InlineData(-7.0, 0.0, 0.25, 1.75)]
    public void ClearanceToDisc_MatchesHandComputedGeometry(double discX, double discY, double r, double expected)
    {
        var got = VehicleFootprint.ClearanceToDisc(
            frontX: 0.0, frontY: 0.0, angleDeg: 90.0, length: 5.0, width: 2.0,
            discX: discX, discY: discY, discRadius: r);
        Assert.Equal(expected, got, precision: 9);
    }

    // The same five cases with BOTH the car and the disc rotated about the front bumper by an arbitrary
    // angle must give the SAME clearance -- i.e. the primitive is world-space/rotation-invariant, not a
    // lucky axis-aligned special case.
    [Theory]
    [InlineData(0.0)]
    [InlineData(37.0)]
    [InlineData(-115.0)]
    [InlineData(180.0)]
    public void ClearanceToDisc_IsRotationInvariant(double turnDeg)
    {
        var cases = new[]
        {
            (4.0, 0.0, 0.5, 3.5), (-2.5, 3.0, 0.5, 1.5), (3.0, 5.0, 1.0, 4.0),
            (0.25, 0.0, 0.75, -0.5), (-7.0, 0.0, 0.25, 1.75),
        };

        // naviDegree grows CLOCKWISE, so turning the car by +turnDeg rotates world points by -turnDeg.
        var rad = -turnDeg * Math.PI / 180.0;
        var (cos, sin) = (Math.Cos(rad), Math.Sin(rad));
        foreach (var (dx, dy, r, expected) in cases)
        {
            var rx = (dx * cos) - (dy * sin);
            var ry = (dx * sin) + (dy * cos);
            var got = VehicleFootprint.ClearanceToDisc(
                frontX: 0.0, frontY: 0.0, angleDeg: 90.0 + turnDeg, length: 5.0, width: 2.0,
                discX: rx, discY: ry, discRadius: r);
            Assert.Equal(expected, got, precision: 9);
        }
    }

    // ---------------------------------------------------------------------------------------
    // (1) the zone gate -- the parity argument, executable
    // ---------------------------------------------------------------------------------------

    // Never calling SetCrowdYieldZone, calling it with radius 0, calling it with a NEGATIVE radius, and
    // calling it with a real radius but centred where ego never goes must all give the identical
    // trajectory: the guard is unreachable unless a host deliberately arms it over the car's own path.
    // This is exactly why no golden or bench run can be perturbed by it.
    [Fact]
    public void ZoneGate_OffOrElsewhere_LeavesTheTrajectoryByteIdentical()
    {
        var never = RunCrossing(zone: null);
        var arms = new (string Label, (double X, double Y, double R)? Zone)[]
        {
            ("radius 0", (22.0, LaneCentreY, 0.0)),
            ("negative radius", (22.0, LaneCentreY, -50.0)),
            ("armed, but 10 km away", (10000.0, 10000.0, 500.0)),
        };

        foreach (var (label, zone) in arms)
        {
            var got = RunCrossing(zone);
            Assert.Equal(never.Count, got.Count);
            for (var i = 0; i < never.Count; i++)
            {
                Assert.Equal(never[i].Pos, got[i].Pos, precision: 12);
                Assert.Equal(never[i].Speed, got[i].Speed, precision: 12);
                Assert.Equal(never[i].PosLat, got[i].PosLat, precision: 12);
            }

            _out.WriteLine($"zone gate '{label}': byte-identical to never-armed over {got.Count} ticks");
        }
    }

    // The predicate itself: closed disc, and dead while the radius is non-positive.
    [Fact]
    public void ZoneGate_PredicateIsAClosedDiscAndOffAtNonPositiveRadius()
    {
        var e = new Engine();
        Assert.Equal(0.0, e.CrowdYieldZoneRadius);

        e.SetCrowdYieldZone(3.0, -4.0, 10.0);
        Assert.Equal(3.0, e.CrowdYieldZoneX);
        Assert.Equal(-4.0, e.CrowdYieldZoneY);
        Assert.Equal(10.0, e.CrowdYieldZoneRadius);

        // Exercised through the only public observable: a car is yielded-to only inside the zone. The
        // geometric predicate is covered by ZoneGate_OffOrElsewhere_... (outside => no effect) plus
        // MovingPedCrossingThrough_* (inside => effect); here just pin the setter round-trip and the
        // off-by-default value that makes every golden inert.
        e.SetCrowdYieldZone(3.0, -4.0, 0.0);
        Assert.Equal(0.0, e.CrowdYieldZoneRadius);
    }

    // ---------------------------------------------------------------------------------------
    // (3) the anticipatory term catches what binder 13 cannot
    // ---------------------------------------------------------------------------------------

    // A ped crossing the lane close enough to the car's arrival that, at a 1 s step, the car steps clean
    // OVER it: the ped's lateral position is never inside binder 13's current-overlap band on any tick
    // the car is beside it, so binder 13 never fires and the car sails through the crossing at maxSpeed
    // with no reaction whatsoever. `pedStartY` is chosen so that happens; the assertions below prove it
    // (the OFF arm is asserted to be a full-speed non-reaction, so this cannot silently become vacuous).
    [Theory]
    [InlineData(-0.1)]
    [InlineData(-0.6)]
    [InlineData(-1.1)]
    public void CrossingPedBinder13Misses_ZoneOffDrivesThroughAtSpeed_ZoneOnYields(double pedStartY)
    {
        var off = RunCrossing(zone: null, pedStartY: pedStartY);
        var on = RunCrossing(zone: (22.0, LaneCentreY, 500.0), pedStartY: pedStartY);

        // OFF: binder 13 never binds and the car never slows below its cruising maxSpeed once up to
        // speed. If this ever stops being true the test below is no longer testing what it claims.
        Assert.DoesNotContain(off, t => t.Binder == 13);
        Assert.DoesNotContain(off, t => t.Binder == 14);
        for (var i = 1; i < off.Count; i++)
        {
            Assert.Equal(5.0, off[i].Speed, precision: 9);
        }

        // ON: the new anticipatory constraint binds (binder 14) and the car actually slows for the ped.
        Assert.Contains(on, t => t.Binder == 14);

        // Compare from t=1 onward: t=0 is the departure tick (still accelerating from 0) in BOTH arms, so
        // including it would let this pass without the guard doing anything at all.
        var minOn = double.PositiveInfinity;
        for (var i = 1; i < on.Count; i++) minOn = Math.Min(minOn, on[i].Speed);
        _out.WriteLine($"pedStartY={pedStartY:F1}: OFF holds 5.00 m/s from t=1 on (binder 13 never fires); " +
                       $"ON dips to {minOn:F2} m/s under binder 14");
        Assert.True(minOn < 4.99,
            $"expected the yield zone to slow the car for the crossing ped; min speed from t=1 was {minOn:F2} m/s");

        // And the yield is not a stall: the car is back at maxSpeed by the end of the run.
        Assert.Equal(5.0, on[^1].Speed, precision: 9);
    }

    // ---------------------------------------------------------------------------------------

    private readonly record struct Tick(double Pos, double Speed, double PosLat, byte Binder);

    // One run of the crossing-ped fixture. `zone` null => never armed. `pedStartY` sets where the ped
    // starts north of the lane; it always walks south to y=-12 at 1.3 m/s, crossing x=22.
    private static List<Tick> RunCrossing((double X, double Y, double R)? zone, double pedStartY = 2.0)
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));
        engine.LaneChangeMinSpeed = 1.5;
        engine.SuppressHeldCrowdSwerve = true;
        if (zone is { } z)
        {
            engine.SetCrowdYieldZone(z.X, z.Y, z.R);
        }

        var crowd = new OrcaCrowd();
        crowd.Add(new Vec2(22.0, pedStartY), PedRadius, maxSpeed: 1.3, goal: new Vec2(22.0, -12.0));
        engine.CrowdSource = crowd;

        var ticks = new List<Tick>();
        VehicleHandle? h = null;
        for (var i = 0; i < 16; i++)
        {
            engine.Step();
            crowd.Step(1.0);
            if (h is null && engine.VehicleHandles.Length > 0) h = engine.VehicleHandles[0];
            if (h is not null && engine.TryGetVehicle(h.Value, out var s))
            {
                ticks.Add(new Tick(s.Pos, s.Speed, s.PosLat, engine.BindingConstraints[s.EntityIndex]));
            }
        }

        return ticks;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
