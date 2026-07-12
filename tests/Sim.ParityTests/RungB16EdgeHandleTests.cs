using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// SUMOSHARP-API.md §9 (dense edge handles): GetEdge(string)->int gives a host a stable int edge handle to
// resolve once at setup, then pass to the int-based Spawn/route overloads (mirroring GetLane for lanes).
// The handles are a facade over the string-keyed router/edge model; the int overloads must behave
// IDENTICALLY to their string counterparts. Additive / runtime-demand only -- no bearing on the parity path.
public class RungB16EdgeHandleTests
{
    private static string Net14 => Path.Combine(RepoRoot(), "scenarios", "14-external-obstacle", "net.net.xml");
    private static string DiamondNet =>
        Path.Combine(RepoRoot(), "scenarios", "_fixtures", "routing-diamond", "net.net.xml");

    private static Engine Loaded(string net)
    {
        var e = new Engine();
        e.LoadNetwork(net);
        return e;
    }

    [Fact]
    public void GetEdge_RoundTrips_AndHandlesAreDense()
    {
        var e = Loaded(DiamondNet);

        Assert.True(e.EdgeCount > 0);
        for (var h = 0; h < e.EdgeCount; h++)
        {
            var id = e.GetEdgeId(h);
            Assert.Equal(h, e.GetEdge(id));  // id -> handle -> id round-trips
        }

        // A known edge id resolves and reverse-resolves.
        var sa = e.GetEdge("SA");
        Assert.InRange(sa, 0, e.EdgeCount - 1);
        Assert.Equal("SA", e.GetEdgeId(sa));
    }

    [Fact]
    public void GetEdge_Unknown_Throws_AndHandleOutOfRange_Throws()
    {
        var e = Loaded(DiamondNet);
        Assert.Throws<ArgumentException>(() => e.GetEdge("NO_SUCH_EDGE"));
        Assert.Throws<ArgumentOutOfRangeException>(() => e.GetEdgeId(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => e.GetEdgeId(e.EdgeCount));
    }

    // Spawning on an explicit route via edge handles yields the same trajectory as spawning via edge-id
    // strings -- the int overload is a pure translation.
    [Fact]
    public void SpawnVehicle_RouteHandles_MatchStringRoute()
    {
        var strEng = Loaded(Net14);
        var hs = strEng.SpawnVehicle(strEng.DefaultVType, new[] { "e0" });

        var intEng = Loaded(Net14);
        var e0 = intEng.GetEdge("e0");
        var hi = intEng.SpawnVehicle(intEng.DefaultVType, stackalloc int[] { e0 });

        AssertTrajectoriesMatch(strEng, hs, intEng, hi, steps: 30);
    }

    // Spawning from->to via edge handles routes the same as the string from->to overload.
    [Fact]
    public void SpawnVehicle_FromToHandles_MatchStringFromTo()
    {
        var strEng = Loaded(DiamondNet);
        var hs = strEng.SpawnVehicle(strEng.DefaultVType, "SA", "DE");

        var intEng = Loaded(DiamondNet);
        var hi = intEng.SpawnVehicle(intEng.DefaultVType, intEng.GetEdge("SA"), intEng.GetEdge("DE"));

        AssertTrajectoriesMatch(strEng, hs, intEng, hi, steps: 40);
    }

    private static void AssertTrajectoriesMatch(
        Engine a, VehicleHandle ha, Engine b, VehicleHandle hb, int steps)
    {
        for (var k = 0; k < steps; k++)
        {
            a.Step();
            b.Step();

            var aHas = a.TryGetVehicle(ha, out var sa);
            var bHas = b.TryGetVehicle(hb, out var sb);
            Assert.Equal(aHas, bHas);
            if (aHas)
            {
                Assert.Equal(sa.Pos, sb.Pos);        // bit-exact
                Assert.Equal(sa.Speed, sb.Speed);
                Assert.Equal(sa.LaneId, sb.LaneId);
            }
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
