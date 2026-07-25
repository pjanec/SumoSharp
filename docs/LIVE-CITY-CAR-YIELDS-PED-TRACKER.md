# TRACKER — cars yield to pedestrians in their path (Task B-guard)

Checklist over `LIVE-CITY-CAR-YIELDS-PED-TASKS.md`. A box is ticked only when its success conditions have
been verified FIRST-HAND (diff read, test read for non-vacuity, command re-run) — not on an implementor's
report.

## Stage 0 — Repro
- [x] **CY-0** authoritative world-space repro trace — **0.70 m @ 3.90 m/s** (t=5), posLat 0→1.41 at 5 m/s
- [x] **CY-0b** control experiment (unconditional swerve suppression) — 2.05 m / 2.6 m/s, resumes in 1 step

## Stage 1 — Zone gate
- [ ] **CY-1** `SetCrowdYieldZone` + `InCrowdYieldZone`

## Stage 2 — L1 (behaviour)
- [ ] **CY-2** suppress the crowd swerve in-zone

## Stage 3 — L2 (guarantee)
- [ ] **CY-3** `CrowdYieldConstraint` (binder 14) — anticipatory in-path yield
- [ ] **CY-4** world-space proximity cap

## Stage 4 — Host wiring
- [ ] **CY-5** `LiveCitySim` pushes the yield zone (`LIVECITY_PEDYIELD`)

## Stage 5 — Proof
- [ ] **CY-6** demo-scale close-fast-pass invariant test (baseline > 0, fixed == 0)
- [ ] **CY-7** extend `CrosswalkCrossingPedTests`
- [ ] **CY-8** no-new-gridlock / throughput within 5%
- [ ] **CY-9** parity 664/4 + bench `D96213B7BB4021A7` + `Sim.LiveCity.Tests` green

## Measurements log
| what | baseline | after |
|---|---|---|
| repro min clearance | 0.70 m | — |
| repro speed at min clearance | 3.90 m/s | — |
| repro max posLat | 1.41 m | — |
| demo close-fast-passes in-zone | — | — |
| `ArrivedTotal` (demo, pinned) | — | — |
| parity | 664/4 | — |
| bench hash | `D96213B7BB4021A7` | — |
