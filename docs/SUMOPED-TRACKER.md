# SUMOPED — Tracker

**Status: PROPOSAL — awaiting owner sign-off. Nothing below is started.**

At-a-glance checklist over `SUMOPED-TASKS.md`. A box is ticked **only** when its success conditions have
been verified first-hand by the reviewer — diff read, test read for non-vacuity, command re-run — never
on an implementor's report (CLAUDE.md §Subagents).

Docs: `SUMOPED-REQUIREMENTS.md` (WHAT) · `SUMOPED-DESIGN.md` (HOW) · `SUMOPED-TASKS.md` (tasks).

---

## Stage 0 — Oracle and fixtures
- [ ] **SP-0.1** oracle re-established (`/sumo` @ `v1_20_0`, `sumo` 1.20.0 via pip), recipe committed
- [ ] **SP-0.2** eight scenarios authored; determinism-pinning test over every `_sumoped` config
- [ ] **SP-0.3** goldens + tripinfo + provenance committed; regeneration is byte-reproducible
- [ ] **SP-0.4** R3 behaviours asserted **on the oracle** (stripe counts, abreast entry, no-stall pass-by)
- [ ] **SP-0.5** zero-overlap helper; minimum clearance recorded per scenario

## Stage 1 — Network model
- [ ] **SP-1.1** ped elements in `Sim.Ingest`; `AllowsRoadVehicle`↔`Permissions` equivalence test; gate unmoved
- [ ] **SP-1.2** walkingarea foes for vehicle links
- [ ] **SP-1.3** ⚠ begin-of-timestep ordering resolved **by trace** (not by reading the event queue)
- [ ] **SP-1.4** static precompute: `WalkingAreaPaths`, `WalkingAreaFoes`, `MinNextLengths`, `NumStripes`

## Stage 2 — Harness (must fail first)
- [ ] **SP-2.1** person FCD parser + comparator + tolerance extension
- [ ] **SP-2.2** eight parity tests, all failing honestly ("no persons produced")
- [ ] **SP-2.3** person trajectory hash; value recorded below

## Stage 3 — Stepper, straight sidewalk
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
- [ ] **SP-5.2** `AddCrossingVehs` + `AddVehicleFoe`; fully-blocked pin asserted
- [ ] **SP-5.3** `CheckWalkingAreaFoe`; `walkingarea-shared` GREEN
- [ ] **SP-5.4** `HasPedestrians`/`NextBlocking`; `sidewalk-shared-lane` GREEN
- [ ] **SP-5.5** zero-overlap invariant on our output + saturated scenario; min clearance reported

## Stage 6 — Traffic lights
- [ ] **SP-6.1** crossing link state, `IgnoreRed`, `GetImpatience`; `xwalk-tls-release` GREEN

## Stage 7 — API, viz, production regime, gate, docs
- [ ] **SP-7.1** public `PersonHandle` API + tutorial sample compiling with no `InternalsVisibleTo`
- [ ] **SP-7.2** coordinate contract round-trip + `SpawnPersonAt` no-pop handover (the Phase 2 hinge)
- [ ] **SP-7.3** `Sim.Viz` scenes + golden ground-truth overlay + stripe lines; in `gen-demos.sh`
- [ ] **SP-7.4** production regime: measured speed spread; goldens provably unaffected
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
| min vehicle↔ped clearance, per scenario (SP-5.5) | _(not yet)_ |
| production-regime crossing-speed spread, min/median/max (SP-7.4) | _(not yet)_ |

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
