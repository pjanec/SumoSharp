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
- [x] **CY-6** demo-scale close-fast-pass invariant test (baseline 200 -> fixed 70 at 800 peds; NOT zero -- see corrections)
- [x] **CY-7** extend `CrosswalkCrossingPedTests`
- [x] **CY-8** no-new-gridlock / throughput (173 -> 175 at 800 peds; `DenseFlow...NoGridlock` green)
- [x] **CY-9** parity 664/4 + bench `D96213B7BB4021A7` + `Sim.LiveCity.Tests` green

## Stage 6 — `QueryNear` nearest-k follow-up (design §8)
- [x] **CY-10** failing repros for all three `QueryNear` implementations (`CrowdQueryNearTests`)
- [x] **CY-11** `WorldDiscQuery.InsertNearest` + nearest-k in `OrcaCrowd` / `CompositeFootprintSource` /
      `CrossingOccupancySource`; contract tightened on `ICrowdFootprintSource`
- [x] **CY-12** demo re-measure at 800 peds: in-zone **70 -> 27**, HEAD-ON **7 -> 0**
- [x] **CY-13** test-isolation fix: `LiveCityConfig.PedYieldEnabled` replaces the process-global env flip

## Measurements log
| what | baseline | after |
|---|---|---|
| repro worst clearance while > 2 m/s | 0.70 m | **2.00 m** |
| repro speed at that moment | 3.90 m/s | 3.67 m/s |
| repro max abs posLat (the weave) | 1.41 m | **0.00 m** |
| repro holds while ped in lane / resumes | no / n-a | yes (Speed 0.00) / 1 tick |
| demo in-zone close-fast-passes @ **800 peds / 600 steps** | **207** | **27** (-87%) |
| ...of which HEAD-ON (ped ahead, in corridor) | 8 | **0** |
| demo net-wide close-fast-passes | 4253 | 3867 |
| `ArrivedTotal` (demo, 800 peds, 600 steps) | 175 | 174 |
| (before the §8 `QueryNear` fix: in-zone 200 -> 70, HEAD-ON 10 -> 7) | | |
| demo in-zone close-fast-passes @ 160 peds / 300 steps | 7 | 0 |
| demo worst case @ 160 peds | body overlap -0.30 m @ 5.30 m/s | 1.79 m @ 2.4 m/s |
| `DenseFlow...NoGridlock` | green | **green** (guard armed) |
| parity | 664/4 | **684/4** (= 664 + exactly the 20 tests this branch adds) |
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
- **The demo measurement was UNDERPOWERED and its headline "0" was WRONG.** At 160 peds / 300 steps the
  baseline showed 7 in-zone events and the fixed arm 0, which read as "the guard eliminates close-fast-
  passes". Re-run at the demo's real 800-ped density over 600 steps: **200 -> 70 (-65%), not zero.** The
  committed test now runs at the real density and asserts a >= 40% cut instead of zero.

## Remaining defects this session did NOT fix (measured, with the reason)
- **`OrcaCrowd.QueryNear` is now a full scan** (no early exit -- a late slot holding a close agent must be
  able to displace an early slot holding a distant one). Measured cost: ~28 s -> ~31 s per 600-step
  800-ped arm (~10%). `OrcaCrowd` already has an opt-in spatial hash (`UseSpatialHash`); wiring `QueryNear`
  onto it removes the scan and is the obvious next step if that 10% ever matters.
- **Out-of-zone cars cannot see pedestrians at all**, so no yield-zone radius helps them. The car feed is
  `Composite(HighPowerFootprints, CrossingOccupancy)` and peds promote to HighPower only inside the
  LC-realism zone. Measured cross-tab: every `HighPower` event is in-zone, every `LowPowerWalking`/`Paused`
  event is out-of-zone. A third probe arm with the yield armed NET-WIDE confirmed it barely helps
  (net-wide 3739 -> 3458). This is a ped-LOD feed question. (Mitigating context: ~85% of net-wide events
  are `offside` -- ped beside the road, not in the car's path -- i.e. largely not defects.)
- **Peds do not avoid cars** (C5, owned by the ped-vehicle session), so a ped can still walk into a
  stopped/creeping car. The car side no longer approaches fast.
