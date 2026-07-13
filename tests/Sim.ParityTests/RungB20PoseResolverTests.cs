using Sim.Core;
using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// SUMOSHARP-DEADRECKONING.md §6 — the portable PoseResolver. Proves: (1) at dt=0 with ParityTangent it
// reproduces the engine's OWN read-surface projection (same PositionAtOffset math) exactly; (2) ChordHeading
// differs from the tangent for a long vehicle on a curved lane (the SUMO computeAngle back->front chord);
// (3) FreeKinematic extrapolates by the velocity vector. Additive / render-side only; no parity impact.
public class RungB20PoseResolverTests
{
    private static string Net14 => Path.Combine(RepoRoot(), "scenarios", "14-external-obstacle", "net.net.xml");

    // (1) dt=0, ParityTangent == the engine's published PosX/PosY/Angle for the same vehicle.
    [Fact]
    public void Resolve_AtDtZero_MatchesEngineProjection()
    {
        var e = new Engine();
        e.LoadNetwork(Net14);
        var h = e.SpawnVehicle(e.DefaultVType, new[] { "e0" });
        for (var k = 0; k < 4; k++) e.Step();
        Assert.True(e.TryGetVehicle(h, out var s));

        var src = new NetworkLaneSource(NetworkParser.Parse(Net14));
        Span<int> up = stackalloc int[16];
        var n = e.GetUpcomingLanes(h, up);
        Assert.True(n >= 1);

        var state = new DrState
        {
            Model = DrModel.LaneArc,
            LaneHandle = s.LaneHandle,
            Pos = s.Pos,
            PosLat = s.PosLat,
            Length = 5.0, // irrelevant for ParityTangent
        };

        var pose = PoseResolver.Resolve(src, state, up[..n], default, dt: 0.0, RenderRealism.ParityTangent);

        Assert.Equal((double)s.X, pose.X, 3);
        Assert.Equal((double)s.Y, pose.Y, 3);
        Assert.Equal(s.Angle, pose.HeadingDeg, 0.01f);
    }

    // (2) On a curved (right-angle-bend) lane, ChordHeading != ParityTangent for a long vehicle.
    [Fact]
    public void ChordHeading_DiffersFromTangent_OnCurve()
    {
        // Lane shape: (0,0) -> (10,0) -> (10,10). Length 20. Front at pos=11 (on the vertical leg),
        // back (Length=8) at pos=3 (on the horizontal leg).
        var lane = new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0) };
        var src = new FakeLaneSource(laneHandle: 0, shape: lane, length: 20.0);

        var state = new DrState
        {
            Model = DrModel.LaneArc,
            LaneHandle = 0,
            Pos = 11.0,
            Length = 8.0,
        };
        Span<int> up = stackalloc int[] { 0 };

        var tangent = PoseResolver.Resolve(src, state, up, default, 0.0, RenderRealism.ParityTangent);
        var chord = PoseResolver.Resolve(src, state, up, default, 0.0, RenderRealism.ChordHeading);

        // Front is on the vertical leg heading due north (navi 0); the chord back to the horizontal leg
        // points north-east, so it is materially different.
        Assert.Equal(0f, tangent.HeadingDeg, 0.05f);
        Assert.True(Math.Abs(chord.HeadingDeg - tangent.HeadingDeg) > 30f,
            $"chord {chord.HeadingDeg} should differ from tangent {tangent.HeadingDeg}");
        // Front position is identical under both (only the heading changes).
        Assert.Equal(tangent.X, chord.X, 6);
        Assert.Equal(tangent.Y, chord.Y, 6);
        Assert.Equal(10.0, chord.X, 6); // front on the vertical leg at x=10
        Assert.Equal(1.0, chord.Y, 6);  // pos 11 => 1 m up the vertical leg
    }

    // On a straight lane, chord == tangent (the correction only bites on curves).
    [Fact]
    public void ChordHeading_EqualsTangent_OnStraightLane()
    {
        var lane = new[] { (0.0, 0.0), (100.0, 0.0) };
        var src = new FakeLaneSource(0, lane, 100.0);
        var state = new DrState { Model = DrModel.LaneArc, LaneHandle = 0, Pos = 50.0, Length = 8.0 };
        Span<int> up = stackalloc int[] { 0 };

        var tangent = PoseResolver.Resolve(src, state, up, default, 0.0, RenderRealism.ParityTangent);
        var chord = PoseResolver.Resolve(src, state, up, default, 0.0, RenderRealism.ChordHeading);
        Assert.Equal(tangent.HeadingDeg, chord.HeadingDeg, 0.01f);
    }

    // (3) FreeKinematic extrapolates world position by the velocity vector; heading follows velocity.
    [Fact]
    public void FreeKinematic_ExtrapolatesByVelocity()
    {
        var src = new FakeLaneSource(0, new[] { (0.0, 0.0), (1.0, 0.0) }, 1.0);
        var state = new DrState { Model = DrModel.FreeKinematic, WorldX = 5.0, WorldY = 7.0, Vx = 2.0, Vy = 0.0 };

        var pose = PoseResolver.Resolve(src, state, default, default, dt: 1.5, RenderRealism.ParityTangent);
        Assert.Equal(8.0, pose.X, 6); // 5 + 2*1.5
        Assert.Equal(7.0, pose.Y, 6);
        Assert.Equal(90f, pose.HeadingDeg, 0.01f); // +x == east == navi 90
    }

    // A curved single lane, addressed by handle 0.
    private sealed class FakeLaneSource : ILaneShapeSource
    {
        private readonly int _handle;
        private readonly IReadOnlyList<(double X, double Y)> _shape;
        private readonly double _length;

        public FakeLaneSource(int laneHandle, (double, double)[] shape, double length)
        {
            _handle = laneHandle;
            _shape = Array.ConvertAll(shape, p => (p.Item1, p.Item2));
            _length = length;
        }

        public double LaneLength(int laneHandle) => Check(laneHandle) ? _length : 0.0;
        public IReadOnlyList<(double X, double Y)> LaneShape(int laneHandle) => _shape;
        public IReadOnlyList<double>? LaneShapeZ(int laneHandle) => null;
        private bool Check(int h) => h == _handle;
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
