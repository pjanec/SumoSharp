using Sim.Core;
using Sim.Ingest;

namespace Sim.Evac;

// docs/EVAC-DEMO-TLS.md T2: a richer, signalized sibling of EvacGridScenario -- same wiring
// (Engine + EvacDirector + tracked vehicles), but on the 5x5 traffic-light grid
// (scenarios/evac-grid-tls/net.net.xml) with denser organized traffic, so the pre-incident phase
// shows real red-light stop-and-go before the incident triggers a bigger jam -> more
// shoulder-pushers -> a larger foot-exodus than the original sparse radius-60 demo.
// EvacGridScenario / scenarios/evac-grid / their tests are UNTOUCHED; this is a new, parallel
// fixture built the same way, reusing EvacDirector/EvacConfig unchanged (parity-exempt, no engine
// change).
public static class EvacTlsScenario
{
    public const double StepLength = 1.0;

    // Straight-across routes for the 5x5 grid (nodes A0..E4, stubs left{i}/right{i}/bottom{i}/
    // top{i}, i=0..4; column letters A..E map to i=0..4). Each boundary entry funnels to the
    // directly-opposite exit -- horizontal rows run left<->right, vertical columns run
    // bottom<->top -- so flows cross at every interior (signalized) junction, same idea as
    // EvacGridScenario.Routes but enumerated for the 5x5 net (20 entries vs the 4x4's 16).
    public static readonly (string From, string To)[] Routes =
    {
        // horizontal, forward (left -> right), rows i=0..4
        ("left0A0", "E0right0"), ("left1A1", "E1right1"), ("left2A2", "E2right2"),
        ("left3A3", "E3right3"), ("left4A4", "E4right4"),
        // horizontal, reverse (right -> left), rows i=0..4
        ("right0E0", "A0left0"), ("right1E1", "A1left1"), ("right2E2", "A2left2"),
        ("right3E3", "A3left3"), ("right4E4", "A4left4"),
        // vertical, forward (bottom -> top), columns i=0..4 (A..E)
        ("bottom0A0", "A4top0"), ("bottom1B0", "B4top1"), ("bottom2C0", "C4top2"),
        ("bottom3D0", "D4top3"), ("bottom4E0", "E4top4"),
        // vertical, reverse (top -> bottom), columns i=0..4 (A..E)
        ("top0A4", "A0bottom0"), ("top1B4", "B0bottom1"), ("top2C4", "C0bottom2"),
        ("top3D4", "D0bottom3"), ("top4E4", "E0bottom4"),
    };

    // Boundary edges a fleeing car reroutes toward (R2 flee route) -- all 20 grid->boundary exit
    // stubs (the "To" side of every route above).
    public static readonly string[] ExitEdges =
    {
        "A0left0", "A1left1", "A2left2", "A3left3", "A4left4",
        "E0right0", "E1right1", "E2right2", "E3right3", "E4right4",
        "A0bottom0", "B0bottom1", "C0bottom2", "D0bottom3", "E0bottom4",
        "A4top0", "B4top1", "C4top2", "D4top3", "E4top4",
    };

    // Central incident at junction C2 (220,220 -- the 5x5 grid's centre; verified from the net's
    // <junction id="C2" ... x="220.00" y="220.00">, grid.length=80 offset by attach-length=60 puts
    // nodes at x,y in {60,140,220,300,380}). StartTime is later than the 4x4 demo's 8.0 so the
    // organized TLS stop-go plays out visibly before the incident fires. Radius tuned smaller than
    // EvacGridScenario's 140 (the 5x5 grid is denser/tighter) so a strong core panics while
    // contagion still spreads outward from it instead of saturating the whole grid at once.
    public static Incident DefaultIncident => new(X: 220.0, Y: 220.0, StartTime: 15.0, Radius: 70.0);

    public static EvacConfig DefaultConfig() => new()
    {
        ThetaPanic = 0.05,
        VicinityWidth = 8.0,
        BlockedDwellSeconds = 3.0,
        SafeRadius = 120.0,
        PedMaxSpeed = 3.0,
        ExitEdges = ExitEdges,
    };

    // Build a fresh engine + director on the TLS grid, spawn denser organized traffic (three cars
    // per route at departPos 5/30/55 -- 60 cars total vs the 4x4 demo's 32), and track it.
    // `incident`/`config` default to DefaultIncident/DefaultConfig() when omitted.
    public static (Engine Engine, EvacDirector Director, List<VehicleHandle> Handles) Build(
        string netPath, Incident? incident = null, EvacConfig? config = null)
    {
        var engine = new Engine();
        engine.LoadNetwork(netPath);   // deterministic default config: 1s Euler, sigma 0; builds TLS
                                       // phase machines from the net's <tlLogic> (Engine.cs InitializeLoaded)

        var vtype = engine.DefineVType(new VTypeParams { VClass = "passenger", Sigma = 0.0 });
        var net = NetworkParser.Parse(netPath);
        var director = new EvacDirector(engine, net, incident ?? DefaultIncident, config ?? DefaultConfig(), StepLength);

        var handles = new List<VehicleHandle>();
        foreach (var (from, to) in Routes)
        {
            foreach (var pos in new[] { 5.0, 30.0, 55.0 })
            {
                var h = engine.SpawnVehicle(vtype, from, to, departPos: pos);
                director.Track(h);
                handles.Add(h);
            }
        }

        return (engine, director, handles);
    }
}
