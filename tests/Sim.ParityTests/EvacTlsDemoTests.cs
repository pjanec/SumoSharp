using Sim.Core;
using Sim.Core.Orca;
using Sim.Evac;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// docs/EVAC-DEMO-TLS.md T4: smoke/invariant tests for the richer, signalized evac demo
// (EvacTlsScenario, scenarios/evac-grid-tls/net.net.xml) -- separate from EvacSpineTests/
// EvacPhase2Tests/EvacPhase3Tests, which pin the ORIGINAL (untouched) 4x4 evac-grid demo. This
// suite proves: (1) the denser cascade still emerges (panic/push/pedestrians/escape) on the TLS
// net; (2) the TLS programs actually hold organized traffic at reds BEFORE the incident fires,
// via Engine.GetDrModel on the LoadNetwork path (no engine change -- Engine.InitializeLoaded
// builds the phase machines from the net's <tlLogic> the same way for LoadNetwork as LoadScenario);
// (3) the containment invariant (no pedestrian/pusher ever leaves the navmesh); (4) determinism.
public class EvacTlsDemoTests
{
    private readonly ITestOutputHelper _out;
    public EvacTlsDemoTests(ITestOutputHelper output) => _out = output;

    private static readonly string NetPath =
        Path.Combine(RepoRoot(), "scenarios", "evac-grid-tls", "net.net.xml");

    private static readonly Incident TheIncident = EvacTlsScenario.DefaultIncident;

    private static (Engine Engine, EvacDirector Director, List<VehicleHandle> Handles) Build() =>
        EvacTlsScenario.Build(NetPath);

    // ----- cascade: panic -> push -> pedestrians -> escape, denser than the radius-60 demo -----

    [Fact]
    public void Cascade_PanicsPushesAndProducesEscapingPedestrians()
    {
        var (_, director, _) = Build();

        var peakOrcaPush = 0;
        var maxPedDistEver = 0.0;
        for (var step = 0; step < 300; step++)
        {
            director.Tick();
            peakOrcaPush = Math.Max(peakOrcaPush, director.OrcaPushCount);

            for (var i = 0; i < director.PedestrianCount; i++)
            {
                var p = director.PedestrianPosition(i);
                maxPedDistEver = Math.Max(maxPedDistEver, TheIncident.DistanceTo(p.X, p.Y));
            }
        }

        var cfg = EvacTlsScenario.DefaultConfig();
        _out.WriteLine($"panicked={director.PanickedCount} peakOrcaPush={peakOrcaPush} " +
                       $"pedestrians={director.PedestrianCount} maxPedDist={maxPedDistEver:F1} " +
                       $"(0.8*SafeRadius={0.8 * cfg.SafeRadius:F1})");

        Assert.True(director.PanickedCount > 0, "no vehicle ever panicked");
        Assert.True(peakOrcaPush > 0, "expected some cars to enter the Orca-push stage (peak OrcaPushCount > 0)");
        Assert.True(director.PedestrianCount > 0, "no pedestrian was ever spawned");
        Assert.True(maxPedDistEver >= 0.8 * cfg.SafeRadius,
            $"pedestrians made too little outward progress (max {maxPedDistEver:F1} m from incident)");
    }

    // ----- TLS actually holds traffic pre-incident -----

    // Proves the signals run on the LoadNetwork path: a tracked vehicle that has genuinely moved
    // away from its depart point (not merely "not yet inserted") is later observed DrModel.Stationary
    // (and ~zero speed) in the window strictly BEFORE the incident fires -- i.e. held at a red /
    // queued behind another car at a signalized approach, not idled by insertion.
    [Fact]
    public void PreIncident_OrganizedTraffic_StopsAtRedSignals()
    {
        var (engine, director, handles) = Build();

        // Strictly before StartTime=15.0: Director.Tick() advances _time by StepLength=1.0 BEFORE
        // stepping, so tick index i observes time=(i+1)*StepLength; time<StartTime <=> i<StartTime-1.
        var preIncidentTicks = (int)TheIncident.StartTime - 1;
        Assert.True(preIncidentTicks > 0, "expected a non-trivial pre-incident window");

        var everMoved = new Dictionary<uint, (double X, double Y)>();
        var provenHeld = false;
        string? provenHandleInfo = null;

        for (var step = 0; step < preIncidentTicks; step++)
        {
            director.Tick();

            foreach (var h in handles)
            {
                if (!engine.TryGetVehicle(h, out var v))
                {
                    continue;
                }

                if (everMoved.TryGetValue(h.Index, out var origin))
                {
                    var moved = Math.Sqrt((v.X - origin.X) * (v.X - origin.X) + (v.Y - origin.Y) * (v.Y - origin.Y));
                    if (moved > 25.0 && engine.GetDrModel(h) == DrModel.Stationary && v.Speed < 0.05)
                    {
                        provenHeld = true;
                        provenHandleInfo = $"handle={h.Index} step={step} movedFromOrigin={moved:F1}m pos=({v.X:F1},{v.Y:F1})";
                    }
                }
                else
                {
                    everMoved[h.Index] = (v.X, v.Y);
                }
            }
        }

        _out.WriteLine(provenHeld
            ? $"TLS hold proven pre-incident: {provenHandleInfo}"
            : $"TLS hold NOT observed within the {preIncidentTicks}-tick pre-incident window");

        Assert.True(provenHeld,
            "expected at least one tracked vehicle to have moved off its depart point and then be " +
            "observed Stationary (held at a red / queued) before the incident fires, proving the " +
            "net's <tlLogic> programs run on the LoadNetwork path");
    }

    // ----- containment: pedestrians AND active pushers never leave the navmesh -----

    [Fact]
    public void Containment_PedestriansAndPushersStayWithinNavmesh()
    {
        var (_, director, _) = Build();

        for (var step = 0; step < 300; step++)
        {
            director.Tick();

            for (var i = 0; i < director.PedestrianCount; i++)
            {
                var p = director.PedestrianPosition(i);
                Assert.True(director.NavMesh.Contains(p),
                    $"pedestrian {i} left the known world at ({p.X:F2},{p.Y:F2}) on step {step}");
            }

            foreach (var (x, y, _) in director.ActivePushers())
            {
                Assert.True(director.NavMesh.Contains(new Vec2(x, y)),
                    $"pusher at ({x:F2},{y:F2}) crossed the band wall at step {step}");
            }
        }
    }

    // ----- determinism -----

    [Fact]
    public void Run_IsDeterministic()
    {
        string Signature()
        {
            var (_, director, handles) = Build();
            for (var step = 0; step < 300; step++)
            {
                director.Tick();
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(director.PanickedCount).Append('|')
              .Append(director.ConvertedCount).Append('|')
              .Append(director.OrcaPushCount).Append('|')
              .Append(director.PedestrianCount).Append(';');

            for (var i = 0; i < director.PedestrianCount; i++)
            {
                var p = director.PedestrianPosition(i);
                sb.Append(p.X.ToString("R")).Append(',').Append(p.Y.ToString("R"))
                  .Append(director.PedestrianEscaped(i) ? 'E' : 'f').Append(';');
            }

            foreach (var h in handles)
            {
                sb.Append(director.Fear(h).ToString("R")).Append(';');
            }

            return sb.ToString();
        }

        Assert.Equal(Signature(), Signature());
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
