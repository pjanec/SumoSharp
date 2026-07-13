# PANIC-EVAC-TRACKER.md — Phase-1 spine checklist

At-a-glance status. Each item references a task in `PANIC-EVAC-TASKS.md` (which carries the detail +
success conditions); design is `PANIC-EVAC-DESIGN.md`; requirements are `PANIC-EVAC.md`. A box is ticked
only when the task's success conditions all pass.

> **Adoption review (2026-07-13, Opus, first-hand).** The exploratory implementation on this branch
> (commits `baf5098` core, `0d7d721` module, `623b566` grid+tests) was reviewed against each task's
> success conditions — code read, tests read, `dotnet test` + `Sim.Bench` hash re-run. Result: the
> code and the **integration** tests are solid and accepted; the tasks whose success conditions demand
> **dedicated unit tests** (T1.1 cond. 4, T2.1, T2.2, T2.3) do not yet have them, and **T4.2**
> (`EvacGridScenario`) was lost in a revert. Those four form **batch B1** (delegated to a Sonnet
> implementor, Opus-reviewed before ticking). Accepted tasks are ticked; gaps carry a note.

## S1 — Core seam
- [x] **T1.1** `Engine.SetVehicleParams` + `VehicleParamOverride` — *accepted (B1, Opus-reviewed):
      `SetVehicleParamsTests` proves the MaxSpeed cap reaches the car-following model + stale handle → false;
      EmergencyDecel≥Decel; suite green + hash 909605E965BFFE59 unmoved*

## S2 — Evac primitives
- [x] **T2.1** `Incident` + radius fear — *accepted (B1): `IncidentTests` (epicentre=1, half-radius=0.5, ≥radius=0, pre-start=0)*
- [x] **T2.2** `BlockedDetector` (DrModel.Stationary + dwell) — *accepted (B1): pure `Update` overload +
      `BlockedDetectorTests` (accumulate/reset/forget/Dwell)*
- [x] **T2.3** `FakeNavMesh` (net-bbox hard edge) — *accepted (B1): `FakeNavMeshTests`; reviewer replaced a
      vacuous margin assertion with a real check vs independently-recomputed raw geometry*

## S3 — Orchestration
- [x] **T3.1** `EvacDirector` tick skeleton + observability — *accepted via T5.1/T5.2/T5.3*
- [x] **T3.2** panic decision + flee preset + reroute — *accepted via T5.1 (32 panicked, reroute exercised)*
- [x] **T3.3** driver→pedestrian conversion — *accepted via T5.1 (3 converted, abandoned==converted)*
- [x] **T3.4** pedestrian steering + escape + car-avoidance feed — *accepted via T5.1 (escaped, contained)*

## S4 — Demo scenario
- [x] **T4.1** grid network authored + committed (parity-exempt) — *accepted (net committed, README, no golden)*
- [x] **T4.2** `EvacGridScenario` shared builder — *accepted (B1): single source of truth; `EvacSpineTests`
      refactored onto it, all names/assertions unchanged and still passing*

## S5 — End-to-end validation
- [x] **T5.1** cascade plays once — *accepted (Grid_IncidentTriggersPanicFleeBlockConvertAndFootExodus)*
- [x] **T5.2** evac run deterministic — *accepted (EvacRun_IsDeterministic)*
- [x] **T5.3** containment invariant (no pedestrian leaves the mesh) — *accepted (asserted every step)*
- [x] **T5.4** no-incident inertness — *accepted (NoIncident_LayerIsInert)*
- [x] **T5.5** suite green + hash gate — *accepted (368 pass / 3 skip; hashA=hashPar=909605E965BFFE59)*

## S6 — Visualization
- [ ] **T6.1** payload overlays (incident + boundary)
- [ ] **T6.2** `SceneGen.BuildEvacGrid`
- [ ] **T6.3** `template.js` overlay drawing
- [ ] **T6.4** bundle wire + artifact

---

### Batches
- **B1 — DONE (Sonnet implemented, Opus-reviewed & accepted).** T2.1/T2.2/T2.3 unit tests; T1.1 cond. 4
  test; T4.2 shared builder + `EvacSpineTests` refactor. 15 new unit tests; suite 383 pass / 3 skip / 0
  fail; hash 909605E965BFFE59 unmoved. Reviewer fixed one vacuous assertion in `FakeNavMeshTests`.
  → **S1, S2, S4 now fully green; S3, S5 already accepted. Phase-1 spine is complete except the viz.**
- **B2 (next):** S6 viz — T6.1 payload overlays, T6.2 `SceneGen.BuildEvacGrid`, T6.3 `template.js`
  incident/boundary drawing, T6.4 bundle wire + artifact.
