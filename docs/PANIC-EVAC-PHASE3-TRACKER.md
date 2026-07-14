# PANIC-EVAC-PHASE3-TRACKER.md — vehicle Orca-push checklist (Option A)

At-a-glance status for Phase 3 (Option A: external shaped-mover handoff). Each item references a task in
`PANIC-EVAC-PHASE3-TASKS.md`; design is `PANIC-EVAC-PHASE3-DESIGN.md`. A box is ticked only when Opus has
verified its success conditions first-hand.

> **Status:** owner signed off Option A (external shaped-mover handoff, reusing
> `Sim.Core.Mixed.MixedTrafficCrowd`; band first-cut = outer wall + obstacles; wedge/away-goal as
> tunables). Implementation follows the orchestration loop (Sonnet implements, Opus reviews hard).

## S1 — VehicleMover + config
- [x] **T1.1** `EvacConfig` Phase-3 tunables — *accepted (B1): EnableOrcaPush/OrcaWedgeSpeed/
      OrcaWedgeDwellSeconds/OrcaPushSafetyMargin/OrcaCrowdSubSteps*
- [x] **T1.2** `VehicleMover` wrapping `MixedTrafficCrowd` — *accepted (B1): Nonholonomic=true, ArmWalls/
      AddBlock/AddCar/SetGoal/Step(sub-stepped)/Deactivate + per-index wedge dwell; 4 unit tests
      (moves-to-goal, confinement, wedge before/after dwell, no-sideways-teleport)*

## S2 — Band walls
- [x] **T2.1** `FakeNavMesh.BandWalls` (outer hard edge) + confinement — *accepted (B1): 4 boundary
      segments; VehicleMover confinement test holds (maxX≈96.9 vs a 100 wall)*

## S3 — Integration
- [x] **T3.1** Orca-push stage in `EvacDirector` — *accepted (B2): EnterOrcaPush (Despawn→VehicleMover.AddCar,
      heading deg→rad), DriveOrcaPushers (re-aim away-goal, Step, wedge→pedestrian via shared
      SpawnPedestriansAt), composite CrowdSource (lane cars avoid pushers), pushers fed to ped crowd,
      FleeGoalForPusher (5 m inset avoids corner limit-cycle). Push-OFF ⇒ unchanged Phase-2 (backwards-compat).
      Carry-over fixes accepted: OrcaCreepSpeed (breaks the 90° deadlock — test proves reorientation),
      OrcaPushMaxSpeed=4, progress-based wedge, and a containment clamp (additive inert MixedTrafficCrowd.SetPose)
      backstopping the NH-wall-pierce case.*

## S4 — Behavioural / determinism / parity
- [x] **T4.1** push precedes foot exodus — *accepted (B2): peak OrcaPushCount 3 (ON) vs 0 (OFF); cascade completes both*
- [x] **T4.2** shaped confinement — *accepted (B2): every active pusher inside navmesh bounds each tick (via clamp backstop)*
- [x] **T4.3** wedge → pedestrian — *accepted (B2): peds produced via push→wedge; 0 pushers remain after 500 ticks*
- [x] **T4.4** no shaped interpenetration — *accepted (B2): min pusher separation 10.37 m ≥ 1.0 m*
- [x] **T4.5** determinism — *accepted (B2): signature incl. pusher poses bit-identical across runs*
- [x] **T4.6** parity / inertness + gate — *accepted (B2): no-incident ⇒ 0 pushers/panicked/fear0; 417 pass / 3 skip; hash unmoved*

> **Tech-debt — RESOLVED (2026-07-14, `docs/MIXED-WALL-CONTAINMENT.md`).** `MixedTrafficCrowd.Step` now
> applies a swept wall clip in the integration (clip `c0→c1` at the first wall crossing), so confinement
> is guaranteed at the SOLVER level. `VehicleMover`'s external clamp / nudge and `MixedTrafficCrowd.SetPose`
> were removed; the pusher-confinement tests (`VehicleMoverTests.BandWalls_Confine…`, `EvacPhase3Tests.
> ActivePushers_StayWithinNavmeshBounds`) now pass on the solver guarantee alone. New reproduce-then-fix
> test `MixedWallContainmentTests` (tunnels pre-fix 204.96 → contained 19.85). Suite 418 pass / 3 skip;
> hash unmoved; India mixed-traffic 13 tests unchanged.

## S5 — Viz: cars mounting the shoulder
- [x] **T5.1** emit pushing cars as oriented shaped boxes — *accepted (B3): `ActivePushers()` → kind-8
      shaped disc entries `[x,y,2.5,8,headingDeg,0,2.5,0.9]`; labels[8]="abandoning car (shoulder)"*
- [x] **T5.2** render + confirm — *accepted (B3, Opus rendered t=13/16/19/22): orange oriented shaped
      boxes appear post-incident at the ring edge, distinct from red/blue lane cars and cyan pedestrians;
      suite 417 pass; hash unmoved*

**Phase-3 COMPLETE — all of S1–S5 accepted.** A blocked, panicked car now mounts the shoulder (shaped
non-holonomic free-space push in the vicinity band) before its occupants abandon it on foot. Option A
(external shaped-mover via `MixedTrafficCrowd`) kept the parity core untouched. Next per PANIC-EVAC.md
§6.4 = Phase 4 (optional sublane filter-to-front) — its own design-first docs first.

---

### Batches
- **B1 — DONE (Sonnet, Opus-reviewed & accepted).** S1 + S2: config + `VehicleMover` + band walls, 4
  unit tests. 410 pass / 3 skip; hash unmoved.
- **B2 (next):** S3 (T3.1) + S4 (T4.1–T4.6) — integration + tests. **Watch-item (from B1):**
  `MixedTrafficCrowd.SteerNonholonomic` uses `targetSpeed = max(min(CreepSpeed, desired), desired·cos(headingErr))`;
  with the default `CreepSpeed=0`, a pusher whose away-goal is ~90° off its lane heading gets
  `targetSpeed≈0` and **deadlocks** (can't nudge-and-turn onto the shoulder). B2 must add a small
  `EvacConfig.OrcaCreepSpeed` (e.g. 0.5 m/s) and set `_crowd.CreepSpeed` in `VehicleMover`, plus a test
  that a pusher with a lateral goal actually reorients + makes progress.
- **B2 — DONE (Sonnet, Opus-reviewed & accepted).** S3 + S4: Orca-push lifecycle + composite source +
  CreepSpeed/maxspeed/progress-wedge/clamp fixes + 7 tests (6 EvacPhase3 + 1 VehicleMover reorient).
  417 pass / 3 skip; hash unmoved. Peak 3 pushers on the demo grid.
- **B3 (next):** S5 (T5.1, T5.2) — shaped-box viz (Opus renders to confirm the shoulder-push reads).
