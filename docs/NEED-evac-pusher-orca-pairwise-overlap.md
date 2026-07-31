# NEED — two evac ORCA pushers converge to 0.463 m (pairwise, at LOW density)

**Status:** ISOLATED, characterised, **deliberately parked** — the owner ranked gridlock, normal-traffic
junction overlaps and lateral lane-changes in high-realism zones as materially more important. Its test
is **skipped with an explicit reason**, not deleted and not weakened.
**Type:** `Sim.Evac` / `Sim.Core.Mixed` (the ORCA crowd solve). **NOT** junction logic, **not** the lane
engine's car-following — both ruled out by measurement, see §2.
**Found by:** `Engine.BayExitLaneKeepClear` (the bay-exit keep-clear gate) being defaulted ON.

## 1. The symptom

`tests/Sim.ParityTests/EvacPhase3Tests.ActivePushers_NeverInterpenetrate` asserts active evac pushers
stay ≥ 1.0 m apart — already a weak floor for ~5 m vehicles.

| arm | min pusher separation |
|---|---|
| `BayExitLaneKeepClear` OFF | **4.073 m** (passes) |
| `BayExitLaneKeepClear` ON | **0.463 m** (fails) |

0.463 m between 5 m vehicles is a gross overlap, not a marginal miss.

## 2. What has been RULED OUT, with the measurement that did it

Instrument: `tests/Sim.ParityTests/EvacPusherOverlapDiagTests.cs` (always-passing; reports, asserts
nothing about separation). `EVAC_DIAG_STEPS` sets its horizon.

| hypothesis | verdict | evidence |
|---|---|---|
| Placement/activation — two pushers appear already overlapping | **OUT** | pair (1190, 11512) starts **8.182 m** apart @step 18, closes to **0.463 m** @step 80 — genuine convergence |
| Lane car-following (`Sim.Core`) let them close | **OUT** | pushers are moved by `VehicleMover` → `MixedTrafficCrowd` (ORCA). The lane engine never governs their separation |
| Density — the gate overloads the solve | **OUT** | the overlap occurs at **6** simultaneously-active pushers; separation is 2.875 / 4.197 / 1.975 / 4.496 m at 8 / 9 / 10 / 11. No degradation with count |

⚠ **Two withdrawn claims, recorded so they are not resurrected.** An earlier note said "the gate triples
the pusher population, so this is density" — that compared **pairs ever co-active across the whole run**
(25 → 72, cumulative) against a per-instant quantity. Simultaneous count never exceeds 11 in either arm.
The `TASKS-TODO.md` **A19 `MaxNeighbours`** link that followed is therefore unsupported too, and
**capping `MaxNeighbours` is very likely the WRONG fix here** — it addresses overload, and this is not
overload.

## 3. What is actually established

Two specific agents converge to 0.463 m while only **six** are active: a **pairwise** failure in the
crowd solve at low density.

## 4. Where to start (three candidates, one favoured)

Dump both agents of pair (1190, 11512) — position, velocity, and ORCA neighbour set — per step from
~60 to ~85. Six agents is few enough to settle exhaustively rather than statistically.

1. **They are not in each other's neighbour set** → the solve never sees the conflict (lookup radius /
   indexing).
2. **They are, and the solve still returns a closing velocity** → the shaped/non-holonomic velocity-
   obstacle construction admits an unsafe velocity.
3. ⭐ **One of them is not being solved at all.** `VehicleMover` deactivates a mover it judges **wedged**
   (see its own header comment on wedge-dwell tracking, judged on *progress toward goal*, not speed). A
   wedged-and-frozen mover that has dropped out of the avoidance set is a stationary obstacle the other
   agent may drive straight into. **This explains convergence, low density, and input-sensitivity
   without requiring the ORCA maths to be wrong — check it first.**

## 5. The open question nobody has answered

**Why does the gate change which cars become pushers at all?** Plausibly: more stopped engine vehicles
⇒ more of them give up on the lane network and mount the shoulder. **This has not been measured.** Do
not assume it.

## 6. Constraints on any fix

- `Sim.Evac` is parity-exempt (`VehicleMover`'s own header: it never touches `Sim.Core`'s parity-critical
  Engine seams), so a fix here should not move goldens — verify, do not assume.
- Un-skip `ActivePushers_NeverInterpenetrate` as the success condition. **Do not weaken its 1.0 m
  threshold**: it is measuring a real overlap, and relaxing it would hide exactly the defect class this
  workstream exists to remove.
