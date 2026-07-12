using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// SUMOSHARP-API.md §9: runtime demand -- LoadNetwork (no rou.xml), DefineVType, SpawnVehicle with
// SUMO-parity queued insertion, GetLifecycle, Despawn, SetDestination, Reroute. Behavioural tests
// exercising the new host-facing API end-to-end (the parity suite does not touch it). Scenario 14 is a
// single free-flow edge (e0); scenario 15-reroute is a diamond S->A->{B|C}->D->E with two paths.
public class RungB9RuntimeSpawnTests
{
    private static readonly string Dir14 = Path.Combine(RepoRoot(), "scenarios", "14-external-obstacle");
    private static readonly string Dir15 = Path.Combine(RepoRoot(), "scenarios", "15-reroute");

    private static string Net(string dir) => Path.Combine(dir, "net.net.xml");

    // LoadNetwork + DefineVType + SpawnVehicle: a spawned vehicle is Pending, then queued-inserts on the
    // next Step (empty lane -> immediate), becomes Active, appears in the read surface, and drives forward.
    [Fact]
    public void LoadNetwork_Spawn_InsertsQueuedAndMoves()
    {
        var e = new Engine();
        e.LoadNetwork(Net(Dir14));

        var t = e.DefineVType(new VTypeParams { Sigma = 0.0, MaxSpeed = 13.89 });
        var h = e.SpawnVehicle(t, new[] { "e0" }, departPos: 0.0, departSpeed: 0.0, departLane: 0);

        Assert.Equal(VehicleLifecycle.Pending, e.GetLifecycle(h)); // not inserted until a Step runs

        e.Step();
        Assert.Equal(VehicleLifecycle.Active, e.GetLifecycle(h));
        Assert.True(e.TryGetVehicle(h, out var s0));
        Assert.Equal("e0_0", s0.LaneId);

        for (var k = 0; k < 25; k++)
        {
            e.Step();
        }

        Assert.True(e.TryGetVehicle(h, out var s1));
        Assert.True(s1.Pos > s0.Pos, $"expected forward motion: {s1.Pos} > {s0.Pos}");
        Assert.True(s1.Speed > 0.0);
    }

    // DefaultVType works without an explicit DefineVType; Despawn removes the vehicle and invalidates the
    // handle (generation bump) so subsequent lookups/despawns are inert.
    [Fact]
    public void DefaultVType_Spawn_ThenDespawn_InvalidatesHandle()
    {
        var e = new Engine();
        e.LoadNetwork(Net(Dir14));

        var h = e.SpawnVehicle(e.DefaultVType, new[] { "e0" });
        e.Step();
        Assert.Equal(VehicleLifecycle.Active, e.GetLifecycle(h));

        Assert.True(e.Despawn(h));
        Assert.Equal(VehicleLifecycle.Unknown, e.GetLifecycle(h)); // stale generation
        Assert.False(e.Despawn(h));                                // already invalid

        e.Step();
        Assert.False(e.TryGetVehicle(h, out _)); // gone from the snapshot
    }

    // SpawnVehicle(from, to) routes via the shortest-path router and the vehicle traverses to its
    // destination edge.
    [Fact]
    public void SpawnFromTo_RoutesAndReachesDestination()
    {
        var e = new Engine();
        e.LoadNetwork(Net(Dir15));

        var t = e.DefineVType(new VTypeParams { Sigma = 0.0 });
        var h = e.SpawnVehicle(t, "SA", "DE", departPos: 0.0, departSpeed: 0.0, departLane: 0);

        var lanes = VisitLanes(e, h, 400);
        Assert.Contains("SA_0", lanes);
        Assert.Contains("DE_0", lanes);
    }

    // Reroute avoids a blocked edge while keeping the destination: on SA (before committing to a branch),
    // avoid BD -> the vehicle diverts onto the bottom detour (AC/CD) and never enters BD.
    [Fact]
    public void Reroute_AvoidEdge_TakesDetour()
    {
        var e = new Engine();
        e.LoadNetwork(Net(Dir15));

        var t = e.DefineVType(new VTypeParams { Sigma = 0.0 });
        var h = e.SpawnVehicle(t, new[] { "SA", "AB", "BD", "DE" }, departPos: 0.0, departSpeed: 0.0, departLane: 0);

        var lanes = new HashSet<string>();
        var rerouted = false;
        for (var k = 0; k < 400; k++)
        {
            e.Step();
            if (e.TryGetVehicle(h, out var s))
            {
                lanes.Add(s.LaneId);
                if (!rerouted && s.LaneId == "SA_0")
                {
                    rerouted = e.Reroute(h, new[] { "BD" });
                }
            }
            else if (e.GetLifecycle(h) == VehicleLifecycle.Arrived)
            {
                break;
            }
        }

        Assert.True(rerouted, "reroute should have applied while on SA_0");
        Assert.DoesNotContain("BD_0", lanes); // never entered the avoided edge
        Assert.Contains("CD_0", lanes);       // took the detour
        Assert.Contains("DE_0", lanes);       // still reached the destination
    }

    // SetDestination changes where the vehicle is heading: spawned to end at BD, redirected on SA to DE,
    // it now continues onto DE (which the original route never reached).
    [Fact]
    public void SetDestination_RedirectsToNewEdge()
    {
        var e = new Engine();
        e.LoadNetwork(Net(Dir15));

        var t = e.DefineVType(new VTypeParams { Sigma = 0.0 });
        var h = e.SpawnVehicle(t, new[] { "SA", "AB", "BD" }, departPos: 0.0, departSpeed: 0.0, departLane: 0);

        var lanes = new HashSet<string>();
        var redirected = false;
        for (var k = 0; k < 400; k++)
        {
            e.Step();
            if (e.TryGetVehicle(h, out var s))
            {
                lanes.Add(s.LaneId);
                if (!redirected && s.LaneId == "SA_0")
                {
                    redirected = e.SetDestination(h, "DE");
                }
            }
            else if (e.GetLifecycle(h) == VehicleLifecycle.Arrived)
            {
                break;
            }
        }

        Assert.True(redirected, "SetDestination should have applied while on SA_0");
        Assert.Contains("DE_0", lanes); // reached the new destination the original route stopped short of
    }

    private static HashSet<string> VisitLanes(Engine e, VehicleHandle h, int maxSteps)
    {
        var lanes = new HashSet<string>();
        for (var k = 0; k < maxSteps; k++)
        {
            e.Step();
            if (e.TryGetVehicle(h, out var s))
            {
                lanes.Add(s.LaneId);
            }
            else if (e.GetLifecycle(h) == VehicleLifecycle.Arrived)
            {
                break;
            }
        }

        return lanes;
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
