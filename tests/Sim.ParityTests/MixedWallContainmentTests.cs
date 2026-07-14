using Sim.Core.Mixed;
using Sim.Core.Orca;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// docs/MIXED-WALL-CONTAINMENT.md W2: reproduce-then-fix test for the solver-level swept wall clip.
// Root cause (see the doc): a wall is a full-yield shaped obstacle in the ORCA solve, so it only
// constrains the HOLONOMIC target velocity's direction for the step; the kinematic-bicycle
// integration then commits the new centre with no check that the swept path c0->c1 stayed on the
// interior side.
//
// NOTE on the reproduction parameters: MixedTrafficCrowd.SteerNonholonomic's own accel/decel model
// caps the PHYSICAL speed at the vehicle CLASS's MaxSpeed (14 m/s for Car) regardless of
// maxSpeedOverride, and a car starting from rest ramps up gradually while the wall's ORCA half-plane
// is already visible within TimeHorizon -- so a car starting at rest well clear of the wall brakes
// smoothly in time (verified: it does NOT tunnel, converging to a stop short of the wall). The real
// tunnelling case (matching the Phase-3 B2 "pusher escaping" root cause) is a car already moving at
// near its class max speed with too little standoff left to shed speed within the class's bounded
// MaxDecel: even maximum braking can only reduce speed by MaxDecel*dt in one step, so if the
// remaining distance to the wall is less than that bounded braking distance, the swept centre path
// c0->c1 sails straight through a thin wall on pre-fix code. This test reproduces exactly that: the
// car is spawned already at its class max speed (14 m/s), close enough to the thin wall that even
// full-decel braking cannot stop it within one 1 s step.
public class MixedWallContainmentTests
{
    private readonly ITestOutputHelper _out;
    public MixedWallContainmentTests(ITestOutputHelper output) => _out = output;

    // ----- W2: a car driven hard at a thin wall must be stopped by it, never tunnel through -----
    [Fact]
    public void CarDrivenHardAtThinWall_NeverTunnelsPastIt()
    {
        var crowd = new MixedTrafficCrowd { Nonholonomic = true };

        const double wallX = 20.0;
        // Thin vertical wall spanning Y in [-50, 50] at X=20.
        crowd.AddWall(new Vec2(wallX, -50), new Vec2(wallX, 50), thickness: 0.2);

        // Car starts close to the wall (X=16, i.e. only 4 m of standoff) ALREADY at its class max
        // speed (14 m/s) heading straight at it (heading 0), with a goal far on the far side (X=200)
        // and a high maxSpeedOverride so the preferred velocity keeps demanding full speed ahead --
        // exactly the bounded-deceleration overshoot described above.
        var i = crowd.Add(
            VehicleClass.Car, position: new Vec2(16, 0), goal: new Vec2(200, 0),
            headingRad: 0.0, velocity: new Vec2(14, 0), maxSpeedOverride: 30.0);

        var maxX = double.NegativeInfinity;
        for (var step = 0; step < 40; step++)
        {
            crowd.Step(1.0);
            var p = crowd.Position(i);
            maxX = Math.Max(maxX, p.X);
            _out.WriteLine($"step={step} pos=({p.X:F3},{p.Y:F3}) maxX={maxX:F3}");
        }

        _out.WriteLine($"final maxX={maxX:F3} (wall at x={wallX}, thickness=0.2)");

        // The car's centre must never cross to the far side of the wall's interior face. Allow a small
        // skin/half-thickness tolerance (wall spans x in [19.9, 20.1] at thickness 0.2; the clip pulls
        // back a further 0.05 m skin on the near face), well short of tunnelling to the far goal (200).
        Assert.True(maxX <= wallX + 0.2,
            $"car tunnelled past the wall: maxX={maxX:F3}, wall at x={wallX}");
    }
}
