# PANIC-EVAC-TRACKER.md — Phase-1 spine checklist

At-a-glance status. Each item references a task in `PANIC-EVAC-TASKS.md` (which carries the detail +
success conditions); design is `PANIC-EVAC-DESIGN.md`; requirements are `PANIC-EVAC.md`. A box is ticked
only when the task's success conditions all pass.

> **Status note (2026-07-13).** An exploratory implementation of S1–S5 was written ahead of these docs
> and pushed to this branch (commits `baf5098` core, `0d7d721` module, `623b566` grid+tests; suite
> 368 pass / hash `909605E965BFFE59` unchanged). It is **not ticked below** — pending the owner's
> decision to either (a) adopt it as the implementation of S1–S5 after a review pass against these
> success conditions, then tick, or (b) reset the branch to the plan-only point and implement fresh
> against these docs. S6 (viz) is not implemented.

## S1 — Core seam
- [ ] **T1.1** `Engine.SetVehicleParams` + `VehicleParamOverride` (additive, hash unmoved)

## S2 — Evac primitives
- [ ] **T2.1** `Incident` + radius fear
- [ ] **T2.2** `BlockedDetector` (DrModel.Stationary + dwell)
- [ ] **T2.3** `FakeNavMesh` (net-bbox hard edge)

## S3 — Orchestration
- [ ] **T3.1** `EvacDirector` tick skeleton + observability
- [ ] **T3.2** panic decision + flee preset + reroute
- [ ] **T3.3** driver→pedestrian conversion
- [ ] **T3.4** pedestrian steering + escape + car-avoidance feed

## S4 — Demo scenario
- [ ] **T4.1** grid network authored + committed (parity-exempt)
- [ ] **T4.2** `EvacGridScenario` shared builder

## S5 — End-to-end validation
- [ ] **T5.1** cascade plays once
- [ ] **T5.2** evac run deterministic
- [ ] **T5.3** containment invariant (no pedestrian leaves the mesh)
- [ ] **T5.4** no-incident inertness
- [ ] **T5.5** suite green + hash gate

## S6 — Visualization
- [ ] **T6.1** payload overlays (incident + boundary)
- [ ] **T6.2** `SceneGen.BuildEvacGrid`
- [ ] **T6.3** `template.js` overlay drawing
- [ ] **T6.4** bundle wire + artifact
