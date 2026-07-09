namespace Sim.Core;

// D7 (FastDataPlane ECS readiness -- the FDP-shaped seam / adapter, TASKS.md line ~603): the
// interface FDP's own `view.GetCommandBuffer()` surface returns (deferred `AddComponent`/
// `DestroyEntity` -- see FastDataPlane Docs/architectural-rules.md, "Structural changes via
// command buffer"). D5's `CommandBuffer` class already IS exactly this shape (ChangeLane/
// ReplaceRoute/Destroy/Flush); this interface is the pure extraction that turns it into a real
// seam a system can be WRITTEN AGAINST instead of the concrete type, so a later `Fdp.Core`-
// backed `ICommandBuffer` could be substituted for `World`'s instance WITHOUT touching any
// `Engine` call site (see `IWorld.cs`'s header comment for the seam's full rationale, and
// `World.cs` for the one in-house backend this rung actually ships).
//
// Byte-identical (CLAUDE.md rule 3 / this rung's done-condition): same four methods, same
// signatures, same bodies as `CommandBuffer` already had -- calling them through this interface
// instead of the concrete class changes nothing about what runs, when, or in what order; it is
// purely a compile-time indirection (a vtable dispatch through an interface reference, not a
// behavioral difference).
internal interface ICommandBuffer
{
    // Discrete lane-index snap (lanechange.duration=0) -- see CommandBuffer.ChangeLane's own
    // comment for the full mapping.
    void ChangeLane(VehicleRuntime v, int newLaneHandle, string newLaneId);

    // C10-i: start a continuous lane-change maneuver (lanechange.duration > 0) -- see
    // CommandBuffer.StartLaneChangeManeuver's own comment.
    void StartLaneChangeManeuver(VehicleRuntime v, int targetLaneHandle, string targetLaneId, int totalSteps);

    // Route/lane-sequence-slice swap (reroute) -- see CommandBuffer.ReplaceRoute's own comment.
    void ReplaceRoute(VehicleRuntime v, int laneSeqStart, int laneSeqLen);

    // Marks the vehicle arrived (the "DestroyEntity" analog) -- see CommandBuffer.Destroy's own
    // comment.
    void Destroy(VehicleRuntime v);

    // Applies every recorded command, in record order, then clears the buffer for reuse.
    void Flush();
}
