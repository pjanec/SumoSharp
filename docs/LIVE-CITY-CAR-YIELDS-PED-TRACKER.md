# TRACKER — cars yield to pedestrians in their path (Task B-guard)

Checklist over `LIVE-CITY-CAR-YIELDS-PED-TASKS.md`. A box is ticked only when its success conditions have
been verified FIRST-HAND (diff read, test read for non-vacuity, command re-run) — not on an implementor's
report.

## Stage 0 — Repro
- [x] **CY-0** authoritative world-space repro trace — **0.70 m @ 3.90 m/s** (t=5), posLat 0→1.41 at 5 m/s
- [x] **CY-0b** control experiment (unconditional swerve suppression) — 2.05 m / 2.6 m/s, resumes in 1 step

## Stage 1 — Zone gate
- [x] **CY-1** `SetCrowdYieldZone` + `InCrowdYieldZone`

## Stage 2 — L1 (behaviour)
- [x] **CY-2** suppress the crowd swerve in-zone

## Stage 3 — L2 (guarantee)
- [x] **CY-3** `CrowdYieldConstraint` (binder 14) — anticipatory in-path yield
- [x] **CY-4** world-space proximity cap

## Stage 4 — Host wiring
- [x] **CY-5** `LiveCitySim` pushes the yield zone (`LIVECITY_PEDYIELD`)

## Stage 5 — Proof
- [x] **CY-6** demo-scale close-fast-pass invariant test (baseline > 0, fixed == 0)
- [x] **CY-7** extend `CrosswalkCrossingPedTests`
- [x] **CY-8** no-new-gridlock / throughput within 5%
- [x] **CY-9** parity 664/4 + bench `D96213B7BB4021A7` + `Sim.LiveCity.Tests` green

## Measurements log
| what | baseline | after |
|---|---|---|
| repro worst clearance while > 2 m/s | 0.70 m | **2.00 m** |
| repro speed at that moment | 3.90 m/s | 3.67 m/s |
| repro max abs posLat (the weave) | 1.41 m | **0.00 m** |
| repro holds while ped in lane / resumes | no / n-a | yes (Speed 0.00) / 1 tick |
| demo close-fast-passes in-zone | **7** | **0** |
| demo worst case | body overlap -0.30 m @ 5.30 m/s | 1.79 m @ 2.4 m/s |
| `ArrivedTotal` (demo, pinned, 300 steps) | 42 | **44** (+4.8%) |
| `DenseFlow...NoGridlock` | green | **green** (guard armed) |
| parity | 664/4 | **680/4** (= 664 + exactly the 16 new tests) |
| bench hash | `D96213B7BB4021A7` | **`D96213B7BB4021A7`** (par == single) |
| `Sim.LiveCity.Tests` | green | **48/48 green** |

## Corrections made during implementation (recorded, not hidden)
- **CY-3 success condition 2 was wrong as written** ("brakes a tick earlier, peak decel below 3.7 m/s^2").
  Term (a) shares Krauss's safe-speed curve with binder 13, so it binds at the same tick on a 1 s step.
  Measured, then replaced with the condition that is both true and load-bearing: term (a) fires in three
  geometries where binder 13 fires **zero** times and the un-guarded car holds 5.00 m/s through the crossing.
- **The proximity cap needed a look-ahead.** Keyed on the current clearance alone it was met one step late
  (one demo sample at 2.70 m/s / 1.36 m while braking at the emergency limit -> demo count 7 -> 1, not 0).
  Evaluating it on the worse of current and `CrowdYieldCapHorizon`-predicted clearance, plus a full stop at
  contact, took the demo count to 0 with throughput unchanged.
- **Residual not fixed here, by design:** a ped can still walk into a stopped/creeping car, because peds do
  not yet avoid cars (C5, owned by the ped-vehicle session). The car side no longer approaches fast.
