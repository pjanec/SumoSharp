using Sim.Core;
using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// SUMOSHARP-DEADRECKONING.md §6.3 — the opt-in production RenderMode on the engine read surface. It may
// change ONLY the derived render floats (PosX/PosY/Angle); the parity-exact lane-relative doubles
// (Pos/PosLat/LaneHandle/Speed) must be byte-identical regardless of mode, and it is Step()-only so
// Run()/goldens/determinism are untouched. Additive; off by default.
public class RungB21RenderModeTests
{
    private static string Net14 => Path.Combine(RepoRoot(), "scenarios", "14-external-obstacle", "net.net.xml");
    private static string DiamondNet =>
        Path.Combine(RepoRoot(), "scenarios", "_fixtures", "routing-diamond", "net.net.xml");

    // Default mode (ParityTangent): the published Angle equals the lane tangent from the same
    // PositionAtOffset the parity/FCD path uses -- i.e. behaviour is unchanged when the mode is off.
    [Fact]
    public void DefaultMode_AngleEqualsLaneTangent()
    {
        var e = new Engine();
        e.LoadNetwork(Net14);
        Assert.Equal(RenderRealism.ParityTangent, e.RenderMode);
        var h = e.SpawnVehicle(e.DefaultVType, new[] { "e0" });
        var net = NetworkParser.Parse(Net14);

        for (var k = 0; k < 5; k++)
        {
            e.Step();
            if (!e.TryGetVehicle(h, out var s)) continue;
            var lane = net.LanesByHandle[s.LaneHandle];
            var (_, _, tangent) = LaneGeometry.PositionAtOffset(lane.Shape, s.Pos, s.PosLat);
            Assert.Equal((float)tangent, s.Angle, 0.01f);
        }
    }

    // The parity-exact columns are invariant to RenderMode: two engines stepped in lockstep, one with
    // CornerCutCorrected, produce bit-identical Pos/PosLat/LaneHandle/Speed (only render floats may differ).
    [Fact]
    public void ParityColumns_InvariantToRenderMode()
    {
        var plain = new Engine();
        plain.LoadNetwork(DiamondNet);
        var hp = plain.SpawnVehicle(plain.DefaultVType, "SA", "DE");

        var render = new Engine();
        render.LoadNetwork(DiamondNet);
        render.RenderMode = RenderRealism.CornerCutCorrected;
        var hr = render.SpawnVehicle(render.DefaultVType, "SA", "DE");

        for (var k = 0; k < 60; k++)
        {
            plain.Step();
            render.Step();

            var pa = plain.TryGetVehicle(hp, out var sp);
            var ra = render.TryGetVehicle(hr, out var sr);
            Assert.Equal(pa, ra);
            if (!pa) continue;

            Assert.Equal(sp.Pos, sr.Pos);             // bit-exact parity columns
            Assert.Equal(sp.PosLat, sr.PosLat);
            Assert.Equal(sp.Speed, sr.Speed);
            Assert.Equal(sp.LaneHandle, sr.LaneHandle);
        }
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
