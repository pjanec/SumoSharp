using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// X1 acceptance (docs/HIGH-DENSITY-X1-DESIGN.md §4): attention-aware selective popping. These are
// FUNCTIONAL / statistical checks, NOT parity -- vanilla SUMO has only GLOBAL teleport/insertion
// controls and cannot express a per-edge realism mask, so there is no golden. Fixture: the committed
// scenarios/47-teleport-jam -- a single-lane eA->eB where a parked <stop> blocker jams the "follower",
// which the engine teleports off eA onto eB at t=200 (baseline TeleportCount==1, verified by the P1-F
// parity test). X1 gates that pop -- plus the more-eager off-camera de-jam despawn and the on-lane
// spawn -- on the RealismMask the host sets from the camera. Every gate is inert with no mask set, so
// the whole feature is byte-identical for the parity suite (asserted separately by that suite staying
// green); here we drive the gates ON and assert the attention-aware behaviour.
public class RungHDx1RealismMaskTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "47-teleport-jam");
    private static string Cfg => Path.Combine(ScenarioDir, "config.sumocfg");

    // ---- Unit: the immutable mask itself ----
    [Fact]
    public void RealismMask_ForbidsVisibleEdges_PermitsOthers()
    {
        var mask = new RealismMask(new[] { "eA" });
        Assert.False(mask.MayTeleport("eA"));
        Assert.False(mask.MayPop("eA"));
        Assert.True(mask.MayTeleport("eB"));
        Assert.True(mask.MayPop("eB"));

        // Empty visible set == fully permissive.
        var empty = new RealismMask(System.Array.Empty<string>());
        Assert.True(empty.MayTeleport("eA"));
        Assert.True(empty.MayPop("eA"));

        // The flags select which cheating action the visible zone forbids.
        var teleOnly = new RealismMask(new[] { "eA" }, forbidTeleport: true, forbidPop: false);
        Assert.False(teleOnly.MayTeleport("eA"));
        Assert.True(teleOnly.MayPop("eA"));
    }

    // ---- Teleport gate ----
    [Fact]
    public void TeleportGate_NoMask_TeleportsAsBaseline()
    {
        var engine = new Engine();
        engine.LoadScenario(Cfg);
        engine.Run(300);
        Assert.Equal(1, engine.TeleportCount); // baseline: the jam teleports off eA (P1-F behaviour)
    }

    [Fact]
    public void TeleportGate_VisibleJamEdge_HoldsTeleport()
    {
        var engine = new Engine();
        engine.LoadScenario(Cfg);
        // Forbid TELEPORT on eA but ALLOW pop, so the vehicles still insert normally and jam -- we are
        // isolating the teleport gate (forbidding pop too would block insertion, a separate gate).
        engine.SetVisibleEdges(new[] { "eA" }, forbidTeleport: true, forbidPop: false);
        var traj = engine.Run(300);

        Assert.Equal(0, engine.TeleportCount); // (1) zero teleports on a visible edge
        // The follower is HELD jammed on eA -- it never jumps to eB (where the baseline lands it at t=200).
        Assert.True(traj.TryGet("follower", 200.0, out var p));
        Assert.Equal("eA_0", p.Lane);
    }

    // (4) popping migrates: the held pop fires only once the edge leaves the visible zone.
    [Fact]
    public void TeleportGate_Migration_FiresOnlyAfterEdgeLeavesCamera()
    {
        var engine = new Engine();
        engine.LoadScenario(Cfg);
        engine.SetVisibleEdges(new[] { "eA" }, forbidTeleport: true, forbidPop: false); // allow spawn, block teleport
        engine.Step(250);                          // past the t=200 teleport point, but eA is visible
        Assert.Equal(0, engine.TeleportCount);     // held while visible

        engine.ClearVisibleEdges();                // camera pans away from eA
        engine.Step(10);
        Assert.Equal(1, engine.TeleportCount);     // the held jam now teleports (migrated off-camera)
    }

    // ---- Off-camera de-jam despawn ----
    [Fact]
    public void DejamDespawn_OffCamera_RemovesBlockerBeforeTeleport()
    {
        var engine = new Engine();
        engine.LoadScenario(Cfg);
        engine.DejamDespawnTime = 60.0; // more eager than time-to-teleport (120)
        engine.Run(300);

        Assert.Equal(1, engine.DejamDespawnCount); // the jammed follower is despawned off-camera
        Assert.Equal(0, engine.TeleportCount);     // removed before it could reach the teleport threshold
    }

    [Fact]
    public void DejamDespawn_VisibleEdge_Held()
    {
        var engine = new Engine();
        engine.LoadScenario(Cfg);
        engine.DejamDespawnTime = 60.0;
        // Let both vehicles insert and the follower begin approaching the jam while OFF-camera (depart
        // t=0/t=5, WaitingTime still 0 well before the 60 s de-jam threshold)...
        engine.Step(40);
        // ...then the camera pans ONTO eA. From here the de-jam despawn is held (MayPop(eA) == false):
        // the follower jams and waits past 60 s but is NEVER despawned on the visible edge.
        engine.SetVisibleEdges(new[] { "eA" });
        engine.Step(260);

        Assert.Equal(0, engine.DejamDespawnCount);
    }

    [Fact]
    public void DejamDespawn_DisabledByDefault_Inert()
    {
        var engine = new Engine();
        engine.LoadScenario(Cfg);
        // DejamDespawnTime defaults to 0 -> the phase returns immediately.
        engine.Run(300);

        Assert.Equal(0, engine.DejamDespawnCount);
        Assert.Equal(1, engine.TeleportCount); // unchanged baseline (teleport valve still fires)
    }

    [Fact]
    public void DejamDespawn_BudgetZero_SuppressesDespawn()
    {
        var engine = new Engine();
        engine.LoadScenario(Cfg);
        engine.DejamDespawnTime = 60.0;
        engine.DejamDespawnBudgetPerStep = 0; // pop budget exhausted -> nothing is despawned
        engine.Run(300);

        Assert.Equal(0, engine.DejamDespawnCount);
        Assert.Equal(1, engine.TeleportCount); // falls through to the (later) teleport valve instead
    }

    // ---- On-lane spawn gate ----
    [Fact]
    public void SpawnGate_VisibleDepartEdge_HoldsInsertion()
    {
        var engine = new Engine();
        engine.LoadScenario(Cfg);
        // eA visible for the whole run, popping forbidden -> neither vehicle (both depart on eA) inserts.
        engine.SetVisibleEdges(new[] { "eA" }, forbidTeleport: false, forbidPop: true);
        var traj = engine.Run(300);

        Assert.DoesNotContain("blocker", traj.VehicleIds);
        Assert.DoesNotContain("follower", traj.VehicleIds);
    }

    [Fact]
    public void SpawnGate_NoMask_Inserts()
    {
        var engine = new Engine();
        engine.LoadScenario(Cfg);
        var traj = engine.Run(300); // no mask -> normal insertion
        Assert.Contains("blocker", traj.VehicleIds);
        Assert.Contains("follower", traj.VehicleIds);
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
