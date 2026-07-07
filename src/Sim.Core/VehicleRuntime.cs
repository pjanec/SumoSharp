using Sim.Ingest;

namespace Sim.Core;

// Per-vehicle mutable runtime state, plus the immutable spawn template (Def) it was created
// from. Kept as one record-per-vehicle for now rather than a struct-of-arrays split: with a
// single rung-1 vehicle there is nothing yet to gain from the SoA reshape, and DESIGN.md's
// struct-of-arrays push is about the *data layout* paying for itself once many vehicles/
// systems exist -- deferring it here blocks nothing (Kinematics/MoveIntent are already
// separable structs, so an eventual SoA split is a mechanical extraction, not a redesign).
internal sealed class VehicleRuntime
{
    public required VehicleDef Def { get; init; }

    public bool Inserted;
    public string LaneId = string.Empty;
    public Kinematics Kinematics;
    public MoveIntent Intent;
}
