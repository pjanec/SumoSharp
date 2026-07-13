using Sim.Core.Mixed;
using Sim.Core.Orca;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// Correctness anchor for the novel polygonal velocity obstacle (docs/INDIA-TRAFFIC.md section 4):
// in the limit where each vehicle footprint is a many-gon approximation of a circle, the shaped
// solver MUST reproduce the disc OrcaSolver's chosen velocity (the disc VO is the circle special
// case of the polygon VO). This is how we trust the geometry before wiring it into a crowd.
//
// Parity-exempt module: nothing here touches the lane engine, goldens, or the determinism hash.
public class MixedTrafficShapedVoTests
{
    private readonly ITestOutputHelper _out;

    public MixedTrafficShapedVoTests(ITestOutputHelper output) => _out = output;

    private const double MaxSpeed = 2.0;
    private const double TimeHorizon = 10.0;
    private const double Dt = 0.25;

    // Disc-solver new velocity for `self` avoiding a single disc neighbour.
    private static Vec2 DiscVelocity(
        Vec2 selfPos, Vec2 selfVel, double rs, Vec2 otherPos, Vec2 otherVel, double ro, Vec2 pref)
    {
        var self = new OrcaSolver.Agent(selfPos, selfVel, rs);
        Span<OrcaSolver.Agent> nb = stackalloc OrcaSolver.Agent[1];
        nb[0] = new OrcaSolver.Agent(otherPos, otherVel, ro);
        Span<OrcaLine> scratch = stackalloc OrcaLine[4];
        return OrcaSolver.ComputeNewVelocity(
            self, nb, ReadOnlySpan<ObstacleSegment>.Empty, pref, MaxSpeed, TimeHorizon, 5.0, Dt, scratch);
    }

    // Shaped-solver new velocity for the SAME setup, footprints = regular many-gons approximating the
    // discs. A regular n-gon of circum-radius r has inradius r*cos(pi/n) < r, so it slightly
    // under-approximates the circle; using a fine n keeps the gap tiny.
    private static Vec2 ShapedVelocity(
        Vec2 selfPos, Vec2 selfVel, double rs, Vec2 otherPos, Vec2 otherVel, double ro, Vec2 pref, int gon)
    {
        var selfShape = ConvexShape.RegularPolygon(gon, rs);
        var otherShape = ConvexShape.RegularPolygon(gon, ro);
        var self = new ShapedVoSolver.ShapedAgent(selfPos, selfVel, selfShape);
        var nb = new[] { new ShapedVoSolver.ShapedAgent(otherPos, otherVel, otherShape) };
        Span<OrcaLine> scratch = stackalloc OrcaLine[4];
        return ShapedVoSolver.ComputeNewVelocity(self, nb, pref, MaxSpeed, TimeHorizon, Dt, scratch);
    }

    [Theory]
    // head-on closing, off-centre so a real avoidance turn happens
    [InlineData(0.0, 0.0, 1.0, 0.0, 6.0, 0.3, -1.0, 0.0)]
    // perpendicular pass
    [InlineData(0.0, 0.0, 1.0, 1.0, 5.0, -5.0, 0.0, 1.0)]
    // overtaking (same direction, self faster, catching up)
    [InlineData(0.0, 0.0, 1.5, 0.0, 4.0, 0.6, 0.0, 0.8)]
    // oblique approach
    [InlineData(0.0, 0.0, 1.2, 0.8, 5.0, 3.0, -0.9, -0.6)]
    public void ShapedVo_ManyGon_MatchesDiscSolver(
        double svx, double svy, double prefx, double prefy, double opx, double opy, double ovx, double ovy)
    {
        const double rs = 0.5;
        const double ro = 0.5;
        var selfPos = new Vec2(0, 0);
        var selfVel = new Vec2(svx, svy);
        var otherPos = new Vec2(opx, opy);
        var otherVel = new Vec2(ovx, ovy);
        var pref = new Vec2(prefx, prefy);

        var disc = DiscVelocity(selfPos, selfVel, rs, otherPos, otherVel, ro, pref);
        var shaped = ShapedVelocity(selfPos, selfVel, rs, otherPos, otherVel, ro, pref, 64);

        var err = (disc - shaped).Abs;
        _out.WriteLine($"disc=({disc.X:F4},{disc.Y:F4}) shaped=({shaped.X:F4},{shaped.Y:F4}) err={err:F4}");
        // 64-gon: inradius deficit ~ r*(1-cos(pi/64)) ~ 0.0006, plus discretization -> a few mm/s.
        Assert.True(err < 0.05, $"shaped VO diverged from disc VO by {err:F4} (>0.05)");
    }

    // Convergence: as the polygon is refined, the error must shrink toward zero (it is a genuine
    // circle approximation, not a coincidental match at one resolution).
    [Fact]
    public void ShapedVo_ConvergesToDisc_AsPolygonRefines()
    {
        var selfPos = new Vec2(0, 0);
        var selfVel = new Vec2(1.0, 0.0);
        var otherPos = new Vec2(6.0, 0.4);
        var otherVel = new Vec2(-1.0, 0.0);
        var pref = new Vec2(2.0, 0.0);
        const double rs = 0.5, ro = 0.5;

        var disc = DiscVelocity(selfPos, selfVel, rs, otherPos, otherVel, ro, pref);
        var prevErr = double.PositiveInfinity;
        foreach (var gon in new[] { 8, 16, 32, 64, 128 })
        {
            var shaped = ShapedVelocity(selfPos, selfVel, rs, otherPos, otherVel, ro, pref, gon);
            var err = (disc - shaped).Abs;
            _out.WriteLine($"gon={gon,3}  err={err:F5}");
            Assert.True(err <= prevErr + 1e-6, $"error grew from {prevErr:F5} to {err:F5} at gon={gon}");
            prevErr = err;
        }

        Assert.True(prevErr < 0.02, $"128-gon still off by {prevErr:F5}");
    }
}
