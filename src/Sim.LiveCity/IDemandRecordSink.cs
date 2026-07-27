using System.Collections.Generic;

namespace Sim.LiveCity;

// docs/DENSITY-DIFF-HARNESS-DESIGN.md §2 ("record-at-spawn"), -TASKS.md B1: an OPTIONAL sink
// LiveCitySim reports every successful vehicle insertion to, in ascending depart-time order, so a
// caller can capture the demo's exact procedural demand as a SUMO-loadable .rou.xml and replay it
// through vanilla SUMO for an apples-to-apples comparison against our engine. Mirrors the
// established `IReplicationSink`/`IPedReplicationSink` tee shape exactly (see LiveCitySim's
// `_recordVehSink`/`_recordPedSink` field remarks): LiveCitySim owns no file handle and never
// disposes the sink -- the caller (Sim.DensityDiff) constructs and disposes whatever backs this
// interface. A null sink (the ctor default) costs nothing beyond the extra null checks in Step().
public interface IDemandRecordSink
{
    // Called once, before the first RecordVehicle call, with the single vType the demo's cars use
    // (vClass/sigma exactly as passed to Engine.DefineVType) -- so the emitted file is self-contained
    // and loadable without an external vType library.
    void RecordVType(string vTypeId, string vClass, double sigma);

    // Called once per successful insertion (the Engine.SpawnVehicle call did not throw), in the SAME
    // order Step() performs them -- i.e. non-decreasing depart time, satisfying SUMO's sorted-route-
    // file requirement with no caller-side sort. `departLane` is SUMO's departLane attribute value
    // ("best", or a numeric lane index as a string) exactly as the demo requested it. `routeEdges` is
    // the vehicle's FULL edge-id path at spawn (design §2's "record-at-spawn" contract) -- normal
    // (non-internal) edge ids only, never lane ids, in traversal order from the depart edge to the
    // arrival edge.
    void RecordVehicle(
        string id,
        double departSeconds,
        string departLane,
        double departPos,
        double departSpeed,
        string vTypeId,
        IReadOnlyList<string> routeEdges);
}
