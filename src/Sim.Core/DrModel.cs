namespace Sim.Core;

// SUMOSHARP-DEADRECKONING.md §3 — the dead-reckoning MOTION REGIME of a mover, i.e. how a renderer (or a
// networked replication client) should extrapolate it between updates. This is the **shared cross-branch
// seam** for the networked dead-reckoning layer: the NuGet/packaging branch publishes it in the prediction
// packet, and the laneless/RVO branch (`claude/sumo-phase-2-planning-p3w7kh`) tags its crowd/ORCA agents
// with it (its `OrcaCrowd` / `WorldDisc` / `ICrowdFootprintSource` movers are `FreeKinematic`). Kept
// deliberately tiny and value-typed — the analogue of how `RvoNeighbor` is the frozen seam for the
// obstacle store (SUMOSHARP-API.md §15/§16, coordination ask DR1).
//
// `byte`-backed on purpose: it maps to a DDS `@bit_bound(8)` enum (CycloneDDS.NET) and packs into one byte
// on the TCP/UDP wire. Additive and inert for the parity path — nothing in the lane engine's Run()/golden
// path reads it; it is a classification consumed only by the (opt-in) render/replication layer.
//
// SHARED SEAM NOTE (laneless branch, DR1 confirmation, issue #3): this file is a byte-identical copy of the
// enum landed on the NuGet branch (`claude/sumo-csharp-nuget-strategy-4vlkki`). Both branches carry the
// same definition so each compiles standalone; the merge reconciles them as identical (exactly like
// `RvoNeighbor`). Confirmed sufficient at three members — see the DR coordination subsection in
// docs/LANELESS-DIRECTION.md for why a vehicle mid-swerve does NOT need a distinct member.
public enum DrModel : byte
{
    // Lane-bound vehicle (including a sublane / laterally-dodging one, posLat != 0). Predict by integrating
    // arc-length `pos` along the upcoming lane path and `posLat` by `latSpeed`; the renderer walks the
    // static lane geometry, so prediction follows the real curve.
    LaneArc = 0,

    // Holonomic, non-lane-predictable mover: an open-space crowd / navmesh / ORCA agent, OR a lane vehicle
    // mid-RVO/ORCA swerve whose short-horizon lateral is not lane-predictable. Predict by integrating world
    // position from a velocity vector (optionally acceleration). Carries a footprint radius.
    FreeKinematic = 1,

    // Not moving this frame (parked / arrived / not-yet-departed). No extrapolation; position only.
    Stationary = 2,
}
