namespace Sim.Core;

// SUMOSHARP-API.md §9: the lifecycle state of a spawned/loaded vehicle, queryable via
// Engine.GetLifecycle. SpawnVehicle uses SUMO-parity QUEUED insertion -- a spawned vehicle is `Pending`
// until the insertion machinery finds a safe gap at its depart position, then `Active`, then `Arrived`
// once it finishes its route (or is despawned). `Unknown` = a stale/never-issued handle.
public enum VehicleLifecycle
{
    Unknown = 0,
    Pending = 1,   // created, waiting for a safe insertion slot (or its depart time)
    Active = 2,    // on the road
    Arrived = 3,   // finished its route, or despawned
}
