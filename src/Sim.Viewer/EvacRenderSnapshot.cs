namespace Sim.Viewer;

// docs/SUMOSHARP-VIEWER-DEMO-EVAC-DESIGN.md §3: an immutable per-tick capture of the evac layer's
// public read surface (Sim.Evac.EvacDirector), taken on the ENGINE thread inside
// SimulationRunner.OnAfterStep (where the director is quiescent, right after EvacDirector.Tick()) and
// published atomically for the UI thread to read -- the same discipline SimulationSnapshot already
// uses, so the renderer never touches the director off-thread and never locks in the draw loop.
public sealed class EvacRenderSnapshot
{
    public double Time;

    // director.PedestrianPosition(i) / PedestrianEscaped(i), i in [0, PedestrianCount).
    public (double X, double Y, bool Escaped)[] Peds = Array.Empty<(double X, double Y, bool Escaped)>();

    // director.AbandonedCar(i), i in [0, AbandonedCarCount).
    public (double X, double Y, double R)[] AbandonedCars = Array.Empty<(double X, double Y, double R)>();

    // director.ActivePushers() -- currently-active shoulder-pushing cars.
    public (double X, double Y, double HeadingRad)[] Pushers = Array.Empty<(double X, double Y, double HeadingRad)>();

    // director.Fear(handle) keyed by handle.Index, for every vehicle live in the engine this tick -- lets
    // the renderer look up fear for the exact vehicle it is already drawing from SimulationSnapshot
    // (both snapshots are captured within the same Tick, so they are mutually consistent).
    public Dictionary<uint, double> FearByVehicle = new();

    // director.Incident, plus the config's SafeRadius (the ring pedestrians must clear to be Escaped).
    public (double X, double Y, double Radius, double StartTime, double SafeRadius) Incident;

    // director.NavMesh's hard world-space boundary (MinX/MinY/MaxX/MaxY).
    public (double MinX, double MinY, double MaxX, double MaxY) Boundary;

    // HUD counters: director.PanickedCount / ConvertedCount / EscapedCount / AbandonedCarCount.
    public int Panicked;
    public int Converted;
    public int Escaped;
    public int Abandoned;
}
