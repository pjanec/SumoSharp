# SUMOPED — Tracker

**Status: PROPOSAL — awaiting owner sign-off. Nothing below is started.**

At-a-glance checklist over `SUMOPED-TASKS.md`. A box is ticked **only** when its success conditions have
been verified first-hand by the reviewer — diff read, test read for non-vacuity, command re-run — never
on an implementor's report (CLAUDE.md §Subagents).

Docs: `SUMOPED-REQUIREMENTS.md` (WHAT) · `SUMOPED-DESIGN.md` (HOW) · `SUMOPED-COVERAGE.md` (coverage
plan) · `SUMOPED-BRANCH-INVENTORY.md` (the 148-branch denominator) · `SUMOPED-ALGORITHM.md` (what the
model does + measured knob sensitivity) · `SUMOPED-TASKS.md` (tasks). **Method (ladders, stage gate, divergence protocol): `SUMOPED-PROCESS.md`.**
**API decisions D19–D27: `SUMOPED-DESIGN.md` §10.0**, mirrored into
`docs/SUMOSHARP-API.md` §12 + §12b (the API doc of record).

---

## Stage 0 — Oracle, coverage inventory, and fixtures
- [ ] **SP-0.0** branch inventory reviewed (a **148-row** first pass is committed; needs review, not authoring)
- [ ] **SP-0.0b** knob sweep re-run on the final scenario set (RNG pinned + `--lat-edge`); inert knobs given scenarios or admitted as holes
- [ ] **SP-0.1** oracle re-established (`/sumo` @ `v1_20_0`, `sumo` 1.20.0 via pip), recipe committed
- [ ] **SP-0.2** ~20 Tier A + 8 Tier B scenarios; all six coverage axes take every value; pinning test
- [ ] **SP-0.2b** 2–3 Tier C saturated scenarios (incl. a jam-regime and a narrow-crossing variant)
- [ ] **SP-0.3** goldens for all seven output kinds + provenance; regeneration byte-reproducible
- [ ] **SP-0.3b** every scenario rendered to `replay.html`, no JS errors, indexed in the scenarios README
- [ ] **SP-0.4** R3 behaviours asserted **on the oracle** (stripe counts, abreast entry, no-stall pass-by)
- [ ] **SP-0.5** collision baseline on the oracle (count, distinct pairs, max colliderSpeed, min clearance)
- [ ] **SP-0.6** branch→scenario matrix mapped; unmapped IDs reported for owner sign-off

## Stage 1 — Network model
- [ ] **SP-1.1** ped elements in `Sim.Ingest`; `AllowsRoadVehicle`↔`Permissions` equivalence test; gate unmoved
- [ ] **SP-1.2** walkingarea foes for vehicle links
- [ ] **SP-1.3** ⚠ begin-of-timestep ordering **by trace** — does a ped see the new or old TL phase on a switch step? (§6.6.2: peds read the CURRENT light; `t−DELTA_T` is an arrival time, not a lagged read)
- [ ] **SP-1.4** static precompute: `WalkingAreaPaths`, `WalkingAreaFoes`, `MinNextLengths`, `NumStripes`

## Stage 2 — Harness (must fail first)
- [ ] **SP-2.1** person FCD parser + comparator + tolerance extension
- [ ] **SP-2.1b** the other six comparators (person-summary, personinfo, statistic, collisions, netstate, warnings)
- [ ] **SP-2.1c** stripe-projection helper (x/y → pos/posLat/stripe), cross-checked against FCD `pos`
- [ ] **SP-2.1d** ⭐ single-step replay harness (L2) — reconstruction self-checks + replayable-step count
- [ ] **SP-2.1e** fail-loudly staging: every unported branch throws, naming its inventory ID
- [ ] **SP-2.2** one parity test per scenario, all failing honestly ("no persons produced")
- [ ] **SP-2.3** person trajectory hash; value recorded below
- [ ] **SP-2.4** coverage counters + `AllBranchesCoveredTest` + `PerScenarioClaimTest` (failing honestly)

## Stage 3 — Stepper, straight sidewalk
- [ ] **SP-3.0** ⭐ lane-bucketed SoA store + pooled `Obstacle` scratch; **0 bytes/step** allocation gate green
- [ ] **SP-3.1** `PersonRuntime`, `StripingParams` (every constant + `.cpp:line`), stripe math
- [ ] **SP-3.2** `Obstacle`/`ObstacleType`/`DistanceTo`/`MergeObstacles`
- [ ] **SP-3.3** `Walk()` — the utility fold, unit-proven in isolation ⭐ pivotal
- [ ] **SP-3.4** `MoveInDirection*`/`ArriveAndAdvance`/`MoveToNextLane`; `walk-straight-1`, `walk-oncoming-2` GREEN
- [ ] **SP-3.5** demand + stage + person FCD writer with `edge`/`pos`

## Stage 4 — Junctions
- [ ] **SP-4.1** `WalkingAreaPath` geometry; `walk-junction-turn` GREEN (no vehicles present)
- [ ] **SP-4.2** `JunctionPedRouter`; correct crossing chosen with 2 viable options + reroute on closure
- [ ] **SP-4.3** `GetNextLane` + `GetNextLaneObstacles`
- [ ] **SP-4.4** ⭐ **the owner's crowd behaviours at parity** — `xwalk-priority-queue`,
      `xwalk-priority-horde`, `walk-passby-queue` GREEN, oracle assertions now passing on our output

## Stage 5 — Vehicle coupling
- [ ] **SP-5.1** `BlockedAtDist` + phantom-leader injection; `xwalk-priority-1v1` GREEN incl. 13.89→11.11→6.61; gate re-run
- [ ] **SP-5.1b** ⭐ **R4b ped-priority zebra** — the A/B pair GREEN; car reaches a FULL STOP (0.00) and holds while the ped crosses at unbroken 1.39 m/s
- [ ] **SP-5.2** `AddCrossingVehs` + `AddVehicleFoe`; fully-blocked pin asserted
- [ ] **SP-5.3** `CheckWalkingAreaFoe`; `walkingarea-shared` GREEN
- [ ] **SP-5.4** `HasPedestrians`/`NextBlocking`; `sidewalk-shared-lane` GREEN
- [ ] **SP-5.6** ⚠ cross-population data-flow contract asserted (refill ordering, lagged approach index, person immutability, race-free query)
- [ ] **SP-5.5** collision-set parity (`golden.collisions.xml` exact); baseline table agrees with the oracle

## Stage 6 — Traffic lights
- [ ] **SP-6.1** crossing link state, `IgnoreRed`, `GetImpatience`; `xwalk-tls-release` GREEN

## Stage 7 — API, viz, production regime, gate, docs
- [ ] **SP-7.1** public `PersonHandle` API + tutorial sample; no existing vehicle type edited; `Count` still means vehicles; handle id spaces provably distinct
- [ ] **SP-7.1b** person DR **reuses** the vehicle path (`DrModel.FreeKinematic`, no new enum member); interpolation error proven no worse than vehicles'
- [ ] **SP-7.2** coordinate contract round-trip + `SpawnPersonAt` no-pop handover (the Phase 2 hinge)
- [ ] **SP-7.3** `Sim.Viz` scenes + golden ground-truth overlay + stripe lines; in `gen-demos.sh`
- [ ] **SP-7.4** production regime: measured speed spread; goldens provably unaffected
- [ ] **SP-7.4c** `Sim.BenchPed` person-steps/s committed; allocation gate still green at the end
- [ ] **SP-7.4d** performance-deviation ledger closed out (or explicitly recorded as empty)
- [ ] **SP-7.4b** coverage close-out: `AllBranchesCoveredTest` green or every miss an owner-signed hole
- [ ] **SP-7.5** final gate: full sln, 782+/661 byte-identical, `A134ED3716DDE7BC`, LiveCity 92, Peds 324
- [ ] **SP-7.6** doc reconciliation (`PEDESTRIAN-OVERVIEW.md` §3, `PEDESTRIANS.md`, `README.md`, `scenarios/README.md`, `TASKS-TODO.md`)

---

## Numbers to keep pinned here as they land

| what | value |
| --- | --- |
| `Sim.ParityTests` before this work | **782 pass / 0 fail / 5 skip**, 661 goldens byte-identical |
| `Sim.ParityTests` after | _(not yet)_ |
| `Sim.Bench` hash (must not move) | **`A134ED3716DDE7BC`** (par == single) |
| `Sim.LiveCity.Tests` / `Sim.Pedestrians.Tests` | **92/92** / **324/324** |
| person trajectory hash (SP-2.3) | _(not yet)_ |
| branch IDs in the inventory | **148** (first pass) |
| branch IDs covered / admitted holes (SP-0.6, SP-7.4b) | _(not yet)_ |
| added golden bytes (SP-0.3) | _(not yet)_ — budget context: existing FCD goldens total 5.1 MB, largest single 1.26 MB |
| production-regime crossing-speed spread, min/median/max (SP-7.4) | _(not yet)_ |
| person phase allocation per step, Tier C (SP-3.0d) | _(not yet)_ — **must be 0 bytes** |
| person-steps/second, Tier C (SP-7.4c) | _(not yet)_ |

### Performance-deviation ledger (design §4.4)

**Empty — no deviation taken.** The default build is parity-exact. A row here means the port
deliberately differs from SUMO for a measured performance win; it is not accepted until every column is
filled **and** the owner has signed off. Exhaust the exact optimisations first (§4.4.1): lane-bucketed
SoA, pooled `Obstacle` scratch, incremental sort maintenance and a same-first-blocker index are all
**exact**, and are where the order-of-magnitude is.

| PD | what it changes | speedup (≥1.3× req.) | pos RMS / max | collisions vs baseline | R3 assertions | personinfo + KS | both surfaces | visual A/B | determinism | owner sign-off |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| _(none)_ | | | | | | | | | | |

Stack row (all enabled deviations together, §4.4.3): _(n/a — none enabled)_

### Replayable step count — the stage-gate metric (`SUMOPED-PROCESS.md` §5.1)

Must rise monotonically; a stage that adds code without adding replayable steps has not been shown to
do anything. Denominator = total person-steps across the committed goldens.

| stage | replayable / total person-steps | branch IDs covered / 148 |
| --- | --- | --- |
| S3 straight sidewalk | _(not yet)_ | _(not yet)_ |
| S4 junctions | _(not yet)_ | _(not yet)_ |
| S5 vehicle coupling | _(not yet)_ | _(not yet)_ |
| S6 traffic lights | _(not yet)_ — **must reach 100%** | _(not yet)_ |

### Collision baseline (R5b) — the denominator for the later R5c improvement

| scenario | collisions | distinct (collider,victim) | max colliderSpeed | min clearance |
| --- | --- | --- | --- | --- |
| all Tier A / Tier B | **0** (measured on the oracle) | 0 | — | _(not yet)_ |
| Tier C saturated (300 s, 2-lane TL) | **0** (measured) | 0 | — | _(not yet)_ |
| Tier C jam regime (`period=0.5`) | **80** (measured) | 29 | 2.60 m/s (only 1 of 80 above 0.1) | _(not yet)_ |
| Tier C narrow crossing (1 stripe, 200 s) | **42** (measured) | _(not yet)_ | _(not yet)_ | _(not yet)_ |
| Tier C wide crossing (12 stripes, 200 s) | **1** (measured) | _(not yet)_ | _(not yet)_ | _(not yet)_ |

## Verified first-hand before any of this was written (session of 2026-08-02)

These are established facts, not assumptions — the commands are in `SUMOPED-DESIGN.md` §2.

- SUMO 1.20.0 installs via `pip install eclipse-sumo==1.20.0`; **apt ships 1.18 — wrong version**.
- `--pedestrian.striping.dawdling 0` + `speedDev="0"` ⇒ ped speed **1.388889 m/s exactly**, every step.
  Only two RNG sites exist in the striping model and only one is on the default path.
- Person FCD emits `<person id x y angle speed pos edge slope/>`; `edge` carries internal ids
  (`:c_w1`, `:c_c1`) — the curb wait is a checkable golden fact.
- `--save-state` writes **zero** person elements ⇒ no `golden.state.xml` cross-check for persons.
- The full target behaviour (car brakes for curb ped → ped yields → ped crosses when clear) reproduces
  deterministically in a 40-step fixture on a `--crossings.guess` priority junction.
- `Sim.Harness/FcdParser.cs:24` filters on `Elements("vehicle")` — `<person>` rows are silently dropped
  today, so a person harness is new code, not a tolerance change.
- `Sim.Ingest/NetworkParser.cs` never reads `<crossing>`; `Sim.Viz/Payload.cs:66` already has ped discs.

### Coverage session (same day), all measured

- Vanilla SUMO exposes **seven** person-bearing outputs, not one — `--person-summary-output` (per-step
  time series **including a `jammed` column**, 54 KB for 300 s) and `--statistic-output` (2.4 KB) make
  large scenarios affordable. `--collision-output` is the vehicle↔ped oracle and
  `intermodal-collision.action` already defaults to `warn`, so it is armed.
- **Exact parity holds at saturation**: 300 s, 2-lane TL junction, 10,068 vehicle + **30,549 person** FCD
  rows, two runs **byte-identical**. Windowing is a storage decision, not a determinism one.
- **`posLat` is not emitted for persons** even when explicitly requested — but lateral state is fully
  recoverable: `PState::getAngle` encodes `mySpeedLat` into the FCD `angle`
  (`MSPModel_Striping.cpp:2342-2349`). Inverting it over 1805 saturated-crossing samples recovered a max
  of **0.6401 m/s** (exactly the `stripeWidth` clamp) with a mode at **0.5556 m/s**
  (`vMax * LATERAL_SPEED_FACTOR`) — both theoretical caps on the nose. Only `myAmJammed`,
  `myWaitingTime`, `myWaitingToEnter`, `myNLI`, `myWalkingAreaPath` stay unobserved.
- **Crossing width is independent of road lanes** (`--default.crossing-width`, default 4.00 m ⇒ 6
  stripes) and strongly selects the failure regime: 1 stripe ⇒ 42 collisions, 6 ⇒ 33, 12 ⇒ 1.
- ⚠ **At jam density vanilla SUMO collides vehicles with pedestrians by design** (175/370 jammed ⇒ 80
  collisions). R5 was restated: parity with SUMO's collision set now, improvement later under the
  artefact-ladder constraint.
- The owner's crowd behaviours are **present and measurable in the oracle**: 6 of 6 stripes occupied
  abreast, 25 peds stopped on one walkingarea — and speed spread (min 0.000 / median 1.198 / max 1.389)
  appears with `dawdling=0` **and** `speedDev=0`, so it is largely golden-checkable rather than
  RNG-dependent.

### Render session (same day) — three coverage holes found by looking at the renders

- ⚠ **`--crossings.guess` never produces a ped-priority zebra.** `NBNode.cpp:2788` creates a guessed
  crossing with `priority = isTLControlled()`, so at an uncontrolled node it is always
  `priority="false"` — the PEDESTRIAN gives way. Every scenario built the obvious way shows peds
  waiting for a gap, and the car-stops-for-ped regime is silently absent. Declaring
  `<crossing ... priority="true"/>` flips the link from state `m` to `M`. A/B measured on one car +
  one ped: false ⇒ ped stops dead at the curb, car dips to 6.28 and goes; true ⇒ ped never breaks
  stride at 1.39 m/s and the car decelerates to **0.00** and holds 3 s. At flow density: peds stopped
  on the curb **91%** vs **0%**; cars fully stopping 18 vs 68 distinct vehicles. Coverage §4.5.

- ⚠ **Crossing counterflow does not occur by default.** On a dense uncontrolled 4-arm junction with peds
  crossing both ways on every arm, *every* crossing is traversed in one direction only (`:c_c0` 0/56,
  `:c_c1` 5/31, `:c_c3` 0/5, `:c_c2` unused; ZERO steps with both directions on one crossing) — the
  junction ped router sends opposing streams over different crossings. Forced version measured: 36/36,
  43 steps with both directions, busiest 50 peds (24 vs 26 opposing). Coverage §4.2.
- ⚠ **Turning cars vs exit-crossing peds** needs turning demand; straight-through never fires it.
  Measured: **80 vehicle-steps of blocked RIGHT turns, 57 of blocked LEFT** (e.g. `eRIGHT.15` `ec->cn`
  held on internal lane `:c_4_0`, t=84..89, 9 peds on `:c_c0`). Coverage §4.3.
- Sidewalk counterflow works with the obvious demand and self-organises: two lanes at y=-6.72 / y=-3.52
  held for the whole run (4 m sidewalk, 75 peds each way); same at 214 concurrent on 6 m.
- **Ped turners threading the waiting bunch (R3d)** works at moderate density: stopped 29% of their
  walkingarea time vs 30% for crossers, and only 2-4% of the ground they use is ever used by a waiter --
  they route AROUND the cluster. At 2.4x car flow it degrades to corner gridlock (76% stopped, 77%
  shared ground on `:c_w3`), continuously rather than as a switch.
- ⚠ **Metric warning:** conditioning "turner stopped %" on steps that already have >=3 stopped peds on
  the walkingarea reports 79-95% and looks like total failure -- the condition selects the congested
  moments. Unconditioned it is 29%. This nearly shipped as a wrong conclusion.
