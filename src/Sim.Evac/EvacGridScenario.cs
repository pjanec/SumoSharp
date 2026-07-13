using Sim.Core;
using Sim.Ingest;

namespace Sim.Evac;

// PANIC-EVAC.md §8: the single source of truth for the headless demo/test grid scenario -- the
// straight-across routes, the exit edges, the default incident/config, and the engine+director+car
// wiring. EvacSpineTests and (later) the viz both build from here, so the fixture only has one place
// to change.
public static class EvacGridScenario
{
    public const double StepLength = 1.0;

    // Straight-across routes: each boundary entry funnels to the opposite exit, so horizontal and
    // vertical flows cross at every junction (yield -> congestion under panic). Deterministic order.
    public static readonly (string From, string To)[] Routes =
    {
        ("left0A0", "D0right0"), ("left1A1", "D1right1"), ("left2A2", "D2right2"), ("left3A3", "D3right3"),
        ("right0D0", "A0left0"), ("right1D1", "A1left1"), ("right2D2", "A2left2"), ("right3D3", "A3left3"),
        ("bottom0A0", "A3top0"), ("bottom1B0", "B3top1"), ("bottom2C0", "C3top2"), ("bottom3D0", "D3top3"),
        ("top0A3", "A0bottom0"), ("top1B3", "B0bottom1"), ("top2C3", "C0bottom2"), ("top3D3", "D0bottom3"),
    };

    // Boundary edges a fleeing car reroutes toward (R2 flee route).
    public static readonly string[] ExitEdges =
    {
        "A0bottom0", "A0left0", "A1left1", "A2left2", "A3left3", "A3top0",
        "B0bottom1", "B3top1", "C0bottom2", "C3top2",
        "D0bottom3", "D0right0", "D1right1", "D2right2", "D3right3", "D3top3",
    };

    // Central incident covers the inner grid; corners fall just outside.
    public static Incident DefaultIncident => new(X: 180.0, Y: 180.0, StartTime: 8.0, Radius: 140.0);

    public static EvacConfig DefaultConfig() => new()
    {
        ThetaPanic = 0.05,
        VicinityWidth = 8.0,
        BlockedDwellSeconds = 3.0,
        SafeRadius = 120.0,
        PedMaxSpeed = 3.0,
        ExitEdges = ExitEdges,
    };

    // Build a fresh engine + director on the grid, spawn the organized traffic (two cars per route),
    // and track it. `incident`/`config` default to DefaultIncident/DefaultConfig() when omitted.
    public static (Engine Engine, EvacDirector Director, List<VehicleHandle> Handles) Build(
        string netPath, Incident? incident = null, EvacConfig? config = null)
    {
        var engine = new Engine();
        engine.LoadNetwork(netPath);   // deterministic default config: 1s Euler, sigma 0

        var vtype = engine.DefineVType(new VTypeParams { VClass = "passenger", Sigma = 0.0 });
        var net = NetworkParser.Parse(netPath);
        var director = new EvacDirector(engine, net, incident ?? DefaultIncident, config ?? DefaultConfig(), StepLength);

        var handles = new List<VehicleHandle>();
        foreach (var (from, to) in Routes)
        {
            foreach (var pos in new[] { 5.0, 45.0 })
            {
                var h = engine.SpawnVehicle(vtype, from, to, departPos: pos);
                director.Track(h);
                handles.Add(h);
            }
        }

        return (engine, director, handles);
    }
}
