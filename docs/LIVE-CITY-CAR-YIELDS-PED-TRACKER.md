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
- [x] **CY-3** `CrowdYieldConstraint` (binder 16) — anticipatory in-path yield
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
- [x] **CY-14** merged `origin/main` (F3 junction #13, ped-LOD #14, `MaxCrowdDiscs` 16->256); binder
      renumbered 14 -> **16** (main took 14/15); re-verified on the merged tree
- [x] **CY-15** unification check for the two same-symptom fixes: buffer-size sweep 16/32/64/256 shows the
      safety property (HEAD-ON = 0) comes from the nearest-first CONTRACT at every size, and the buffer is
      a fidelity knob that saturates at 64. Kept 256, documented the split (design §8.2).

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
| parity (post-merge, main's baseline 755/4) | 755/4 | **775/4** (= 755 + exactly the 20 tests this branch adds) |
| bench hash (post-merge, main's new baseline) | `BF3794A4704BCD79` | **`BF3794A4704BCD79`** (par == single) |
| `Sim.LiveCity.Tests` (post-merge) | green | **53/53 green** |
| demo in-zone close-fast-passes, POST-MERGE @ 800 peds | **203** | **14** (-93%) |
| ...of which HEAD-ON, POST-MERGE | 11 | **0** |

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

## Documents produced by this session
| doc | what it is |
|---|---|
| `LIVE-CITY-CAR-YIELDS-PED-DESIGN.md` | the mechanism of record: repro (§1), root cause (§2), the three pieces (§3), parity argument (§4), constants (§5), measured results (§6), out-of-scope (§7), and the `QueryNear` follow-up + the contract-vs-knob analysis (§8, §8.1, §8.2) |
| `LIVE-CITY-CAR-YIELDS-PED-TASKS.md` | the work breakdown with per-task success conditions, including the two conditions that were **wrong as written** and the measurements that replaced them |
| `LIVE-CITY-CAR-YIELDS-PED-TRACKER.md` | this file — checklist, measurements log, corrections, and the open items below |
| `archive/LIVE-CITY-CAR-YIELDS-PED-HANDOFF.md` | the incoming brief (pre-existing; §7's diagnostic list is stale — see below) |

## Code and tests this session owns
| file | role |
|---|---|
| `src/Sim.Core/Engine.cs` | `SetCrowdYieldZone`/`InCrowdYieldZone` + tuning consts + `ProximitySpeedCap` (realism-knob block); L1 gate in `ComputeLateralEvasion`; `CrowdYieldConstraint` (binder **16**) + its fold line |
| `src/Sim.Core/VehicleFootprint.cs` | world-space rectangle↔disc clearance primitive (new) |
| `src/Sim.Core/Bridge/WorldDiscQuery.cs` | shared nearest-first bounded accumulator (new) |
| `src/Sim.Core/Bridge/WorldDisc.cs` | `ICrowdFootprintSource.QueryNear` contract tightened |
| `src/Sim.Core/Bridge/CompositeFootprintSource.cs`, `src/Sim.Core/Orca/OrcaCrowd.cs`, `src/Sim.Pedestrians/Crossing/CrossingOccupancySource.cs` | nearest-first `QueryNear` |
| `src/Sim.LiveCity/LiveCityConfig.cs`, `LiveCitySim.cs` | `PedYieldEnabled` knob; yield zone armed on and following the LC-realism zone; `PedYieldZone{X,Y,Radius}` read-backs |
| `tests/Sim.ParityTests/CrowdYieldZoneTests.cs` | zone gate, world-space primitive, binder-16 non-vacuity (new, 13 cases) |
| `tests/Sim.ParityTests/CrowdQueryNearTests.cs` | nearest-first contract for all three sources (new, 4 cases; written as failing repros) |
| `tests/Sim.ParityTests/CrosswalkCrossingPedTests.cs` | + 2 yield tests (defect characterisation + the contract) |
| `tests/Sim.LiveCity.Tests/DemoPedYieldInvariantTests.cs` | demo-scale A/B at 800 peds (new) |
| `tests/Sim.LiveCity.Tests/PedYieldZoneWiringTests.cs` | host wiring: armed, follows the camera, opt-out holds (new) |

---

## Still worth doing (measured, with the reason — not speculation)

**Owned elsewhere, unchanged by this session:**
1. **C5 — pedestrians do not avoid cars** *(ped–vehicle avoidance session; = TASKS-TODO "Realism #5")*. This
   is what the **entire** residual is now made of: all 14 remaining in-zone events are **ABEAM** (a ped
   walking into the side of a stopped or creeping car), zero are HEAD-ON. The car side no longer approaches
   fast; the other half of the interaction is untouched.
2. **B-api — retire the string `ExternalObstacle` onto `WorldDisc`** *(ped–vehicle avoidance session;
   handoff §8 Q4)*. Deliberately not folded in here: it is an API refactor with its own parity surface, and
   this session stayed car-yield-only.

**New, found by this session, not owned by anyone yet:**
3. **Out-of-zone cars cannot see pedestrians at all.** `CrowdSource = Composite(HighPowerFootprints,
   CrossingOccupancy)`, and peds promote to HighPower only inside the LC-realism zone, so outside it a car
   sees a ped only if that ped is walking on a crossing. Measured cross-tab at 800 peds: **every** `HighPower`
   event is in-zone, **every** `LowPowerWalking`/`Paused` event is out-of-zone. Arming the yield **net-wide**
   was measured as a third probe arm and barely helped (3739 → 3458) — the cars have no data to react to.
   Fixing it is a **ped-LOD feed** decision with a real perf cost at 800+ peds, not a car-yield change.
   Mitigating context: ~85% of net-wide events are `offside` (ped beside the road, not in the car's path),
   i.e. largely ordinary traffic on a net with kerbside footways rather than defects.
4. **`OrcaCrowd.QueryNear` is now a full scan — and, measured, that is FINE.** An earlier version of this
   entry said it "will scale badly"; that was wrong and the measurement is below. The scan covers only the
   crowd a car can see, which is `HighPowerFootprints` = the **promoted** population, bounded by the zone
   and not by the total crowd (and `OrcaCrowd.Count` is a slot high-water mark):

   | total peds | promoted (live) | slots scanned | ms/step @160 cars | ms/step @320 cars |
   |---|---|---|---|---|
   | 800 | 7 | 67 | 12.1 | 12.4 |
   | 1600 | 38 | 132 | 15.6 | — |
   | 3200 | 123 | 275 | 35.0 | 34.6 |

   Doubling the CAR count is invisible, so the O(cars × agents) term is under the noise floor; the growth
   with ped count is ped-side ORCA/LOD cost. 35 ms/step at dt = 0.5 s is ~15× inside real time. At 67–275
   slots, a 121-cell grid lookup plus the order-preserving sort would probably be *slower* than the scan,
   so optimising now would be a pessimisation. It becomes worth doing only alongside **much larger/multiple
   zones (W4)** or **item 3** (feeding low-power peds), the latter of which turns `CrowdSource` into the
   whole population — the two are coupled. Vehicle if/when needed: the `UseSpatialHash` grid, already ON in
   `PedLodManager` and rebuilt every `Step` (so already paid for) — but not a flag flip: `GridCandidates`
   is agent-indexed with a hard-coded 3×3 ring at `NeighbourDist = 15 m` vs `QueryNear`'s ~66 m reach; the
   grid is rebuilt BEFORE the crowd commits its move while the engine queries AFTER, so a query must
   inflate the ring by `maxSpeed × dt` or reintroduce the silent-miss class just removed; and candidates
   must stay index-sorted to keep the nearest-k tie-break deterministic.
5. **`MaxCrowdDiscs` could drop 256 → 64.** Measured identical at 800 peds (see design §8.2) for 4× less
   stack per call site (10 KB → 2.5 KB), and with the nearest-first contract the degradation is graceful.
   Kept at 256 only to preserve the headroom f9c837c measured at 10× ped density. Low priority either way —
   wall time is flat across 16…256.
6. **One home for the vehicle-pose convention.** `Sim.Ingest/VehicleObb.cs` (box↔box) states, correctly,
   that the naviDegree + front-bumper conventions must have *exactly one* implementation living beside
   `LaneGeometry` which defines them. `Sim.Core/VehicleFootprint.cs` (box↔disc) is a second encoding of the
   same two conventions, in a different assembly. It is currently correct and rotation-tested, but the two
   should be consolidated before a third appears.
7. **The demo close-fast-pass metric over-counts.** The raw net-wide number treats "ped standing 1.4 m from
   a passing car" the same as "car driving at a ped". The committed test already reports the sharp HEAD-ON
   sub-metric separately; if anyone starts using the net-wide figure as a KPI it should be split properly
   (ahead-in-corridor vs abeam vs offside) rather than quoted whole.

**Stale documentation found:**
8. **`archive/LIVE-CITY-CAR-YIELDS-PED-HANDOFF.md` §7 references diagnostics that no longer exist.**
   `--live-city-orcatrace` and `--live-city-cartrace` are gone from `src/Sim.Viz/Program.cs` (the T1–T3 viz
   refactor). This session built the demo-scale check as a committed test instead
   (`DemoPedYieldInvariantTests`), which is deterministic and CI-runnable; the handoff text was not edited
   because it is the incoming brief, a historical record.
