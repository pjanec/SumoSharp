using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// SUMOSHARP-DEADRECKONING.md §5.2 — the traffic-light state read projection: Engine.TlLaneHandles (the
// static controlled approach lanes) + Engine.TlStates (their current signal char, refreshed each Step).
// For rendering junction signals (not otherwise published). Step()-only projection; the parity gate +
// determinism hash are unaffected (verified by the suite staying green).
public class RungB23TrafficLightStateTests
{
    private static string TlNet => Path.Combine(RepoRoot(), "scenarios", "09-traffic-light", "net.net.xml");

    [Fact]
    public void TrafficLightStates_ExposedAndCycle()
    {
        var e = new Engine();
        e.LoadNetwork(TlNet);
        e.Step();

        var lanes = e.TlLaneHandles;
        Assert.True(lanes.Length > 0, "the traffic-light network should have controlled approach lanes");
        Assert.Equal(lanes.Length, e.TlStates.Length);

        // Every published state is a valid SUMO signal char.
        const string valid = "GgyrouOs";
        foreach (var b in e.TlStates)
        {
            Assert.Contains((char)b, valid);
        }

        // Over a full-ish cycle the first controlled lane transitions through more than one state
        // (green -> yellow -> red ...), proving the projection tracks the live TL program.
        var seen = new HashSet<char>();
        for (var k = 0; k < 120; k++)
        {
            e.Step();
            if (e.TlStates.Length > 0)
            {
                seen.Add((char)e.TlStates[0]);
            }
        }

        Assert.True(seen.Count > 1, $"lane 0's signal should change over a cycle; saw only [{string.Join(",", seen)}]");
    }

    [Fact]
    public void NoRoadTl_EmptyProjection()
    {
        var e = new Engine();
        e.LoadNetwork(Path.Combine(RepoRoot(), "scenarios", "14-external-obstacle", "net.net.xml"));
        e.Step();
        Assert.Equal(0, e.TlLaneHandles.Length);
        Assert.Equal(0, e.TlStates.Length);
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
