using Sim.Core;

namespace Sim.Evac;

// PANIC-EVAC.md R4 / §8.3: derives the per-vehicle 'blocked' signal ENTIRELY from the frozen DR seam
// (Engine.GetDrModel) plus a dwell timer — no core change. A vehicle counts as blocked once it has
// been DrModel.Stationary (Engine.RegimeOf: speed <= 0.01 m/s, i.e. jam-stopped / halted) for a
// continuous dwell. Any step it is moving resets the dwell. Because RegimeOf classifies a jam-stopped
// lane vehicle as Stationary, this fires for a car boxed in by gridlock — which is precisely the
// boundary the driver→pedestrian handoff hangs off.
public sealed class BlockedDetector
{
    private readonly double _dwellSeconds;
    private readonly Dictionary<VehicleHandle, double> _dwell = new();

    public BlockedDetector(double dwellSeconds) => _dwellSeconds = dwellSeconds;

    // Advance this vehicle's dwell by dt and return whether it is now blocked. Call once per step for
    // each tracked, still-alive vehicle.
    public bool Update(Engine engine, VehicleHandle handle, double dt)
    {
        var stationary = engine.GetDrModel(handle) == DrModel.Stationary;
        var dwell = _dwell.TryGetValue(handle, out var cur) ? cur : 0.0;
        dwell = stationary ? dwell + dt : 0.0;
        _dwell[handle] = dwell;
        return dwell >= _dwellSeconds;
    }

    // Drop a vehicle's dwell bookkeeping (after it is converted/despawned).
    public void Forget(VehicleHandle handle) => _dwell.Remove(handle);

    public double Dwell(VehicleHandle handle) => _dwell.TryGetValue(handle, out var d) ? d : 0.0;
}
