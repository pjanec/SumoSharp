# SUMOPED — Requirements (the WHAT)

**Status: PROPOSAL — awaiting owner sign-off. No implementation has started.**

This is the WHAT for a faithful port of SUMO's pedestrian model (`MSPModel_Striping`) into SumoSharp,
held to the same exact-parity bar as the vehicle engine. The HOW is `SUMOPED-DESIGN.md`; the work
breakdown is `SUMOPED-TASKS.md`; the checklist is `SUMOPED-TRACKER.md`.

Read `CLAUDE.md` first. This document assumes its vocabulary (parity tolerance, committed-vs-ephemeral,
the offline test loop, design-first).

---

## 1. Why

SumoSharp today has **two** pedestrian stories and neither is the one the owner wants for street-level
realism:

- `src/Sim.Pedestrians` — a from-scratch ORCA crowd layer on the **live-reactivity** axis. Cheap,
  replicable (`server == IG`), great for ambient sidewalk population. It is validated by behavioural and
  property tests and **never** by golden FCD, and `docs/PEDESTRIAN-OVERVIEW.md` §3 lists *not porting
  `MSPModel_Striping`* as an explicit non-goal.
- The vehicle↔ped coupling that exists (`CrowdLongitudinalConstraint` binder 13, `CrowdYieldConstraint`
  binder 16) reduces a pedestrian to a **world-space moving disc** near a car. There is no notion
  anywhere in the engine of a pedestrian *claiming a crossing* and a vehicle *yielding to the link*.

SUMO does street-crossing pedestrians well, and it does them with an algorithm we can copy exactly. The
owner's target end state is a **two-tier pedestrian system**: low-power ambient peds on long sidewalk
runs, promoted to SUMO-exact peds in the vicinity of a crossing, demoted back afterwards. That hybrid is
Phase 2 and gets its own design. **This document scopes Phase 1: the SUMO-exact tier itself, standing
alone, with parity proof and a public API.**

### 1.1 This supersedes a stated non-goal

`docs/PEDESTRIAN-OVERVIEW.md` §3 says "We do NOT port SUMO's striping pedestrian model". That
non-goal was correct *for the crowd layer's own axis* and stays correct for it. It does not survive
the owner's new requirement. On acceptance of this document, `PEDESTRIAN-OVERVIEW.md` §3 must be edited
to say the port is a **separate, coexisting tier**, not a replacement — see R-N3 below.

---

## 2. Requirements

Each requirement is numbered and has an **acceptance condition** that a task in `SUMOPED-TASKS.md` must
discharge. "Exact" below always means *within the scenario's committed `tolerance.json`*, per CLAUDE.md
prime directive 3.

### R1 — Faithful port, not a lookalike
The pedestrian dynamics must be a **direct port of `/sumo/src/microsim/transportables/MSPModel_Striping.{h,cpp}`
at tag `v1_20_0`**, preserving SUMO's algorithms *and their calculation ordering*. Rebuild only memory
layout and the timing of structural mutations (CLAUDE.md prime directive 4). Where a deviation is
structurally forced, it must be named in the design, gated, and justified in writing.

*Acceptance:* every ported function carries a `/sumo/...:line` reference comment; the ordered
sub-steps of `PState::walk` appear in SUMO's order; a reviewer can diff C# against C++ side by side.

### R2 — Exact trajectory parity against real SUMO
Committed scenarios with person demand must reproduce SUMO 1.20.0's `<person>` FCD rows exactly.

*Acceptance:* for every scenario in `scenarios/_sumoped/*`, a person-FCD comparator reports zero
presence mismatches and every compared attribute within `tolerance.json`. Compared attributes at
minimum: `edge` (string, exact), `pos`, `speed`, `x`, `y`, `angle`. **`angle` is load-bearing, not
cosmetic** — SUMO encodes `mySpeedLat` into it (`MSPModel_Striping.cpp:2342-2349`), so it is the
lateral-velocity witness and person FCD carries no other. See `SUMOPED-COVERAGE.md` §2.1.

### R3 — Junction crowd behaviour (the owner's primary quality bar)
This is the requirement the port exists for. Four distinct behaviours, all of which must **emerge from
the ported algorithm**, never be scripted, and all of which are *deterministic* in SUMO (they come from
the stripe-utility computation, not from any RNG — see §3.1):

- **R3a — Accumulation without overlap.** Peds waiting at a crossing pack onto the walkingarea and fill
  it *laterally*, and no two ped footprints ever interpenetrate. Mechanism: `getNeighboringObstacles` +
  `OBSTRUCTED_PENALTY` (`-300000`) on an overlapping stripe **and on every stripe beyond it away from
  the ped's current one**, so a blocked ped cannot walk "through" a neighbour by sidestepping past them.
- **R3b — A group crosses abreast, not single-file.** When the crossing opens, the waiting cluster
  enters as a **horde spread across stripes**, not a queue. Mechanism: `LATERAL_PENALTY` is only `-1 m`
  per stripe of lateral displacement, while the utility gain of a free stripe is the full
  `expectedDist` (up to `vMax * LOOKAHEAD_SAMEDIR` = ~5.6 m) — so taking a free adjacent stripe
  massively outbids queueing behind someone. Getting this ratio and the ordered utility folds right
  *is* getting the look right.
- **R3c — Members of the crossing group move at different speeds.** This is the one RNG-fed part;
  it is handled by the two-regime split in **R11** below, not by the deterministic core.
- **R3d — Peds merely passing the junction avoid the waiting cluster.** A ped walking along the
  sidewalk past a crowd accumulated at the curb must route *around* it, not through or into a stall.
  Mechanism: the same obstacle fold on the sidewalk/walkingarea, plus the oncoming-conflict penalty
  (`-1000`) and `getReserved(...RESERVE_FOR_ONCOMING_FACTOR_JUNCTIONS)` keeping counterflow stripes
  open.

*Acceptance:* four committed scenarios (`xwalk-priority-queue`, `xwalk-priority-horde`,
`walk-passby-queue`, plus the saturated behavioural scenario), each at exact parity, asserting
specifically:
- (R3a) ≥4 peds simultaneously stopped on one walkingarea, each on a **distinct stripe**, with a
  standing zero-overlap invariant over every step of every `_sumoped` scenario;
- (R3b) on the release step, ≥3 peds enter the crossing within 2 s occupying **≥3 distinct stripes** —
  a golden that shows single-file entry is a *failed* port even if it matches, so this assertion is on
  the SUMO golden too (it must be true of the oracle before it can be required of us);
- (R3d) a pass-by ped's `pos` trace shows no stall (`speed > 0` every step) while ≥3 peds are stopped
  at the curb beside it, and its stripe differs from theirs.

These are **stripe-level** assertions, not "a ped waits" — the count, the stripe indices, and the
per-step positions must all match the golden to tolerance.

### R4 — Cars yield to a pedestrian waiting at a non-TL crossing
At an uncontrolled (priority) crossing, an approaching vehicle must brake for a pedestrian that is on,
or imminently entering, the crossing — **using the same braking machinery the vehicle engine already
uses for junction leaders**, not a bespoke pedestrian rule (this is how SUMO does it; see
`MSLink.cpp:1677-1680`, which injects the ped as a leader with `vehAndGap.first == nullptr`).

*Acceptance:* the fixture verified first-hand this session (`scenarios/_sumoped/xwalk-priority-1v1`,
below) reproduces to tolerance, including the car's deceleration profile `13.89 → 11.11 → 6.61`.

**R4b — the ped-priority zebra (car stops, ped never yields).** ⚠ `--crossings.guess` at an
uncontrolled node always produces `priority="false"` (`NBNode.cpp:2788`), i.e. the *pedestrian* gives
way — so this second, opposite regime is silently absent unless crossings are declared explicitly with
`priority="true"`. Both regimes are required.

*Acceptance:* an A/B pair of scenarios identical except for that flag, both at exact parity. The
`priority="true"` arm must reproduce the measured trace where the car decelerates
`13.89 → … → 2.15 → 0.00`, holds a **full stop** for 3 s on the internal lane, and the ped crosses at
an unbroken `1.39 m/s`; the `priority="false"` arm must reproduce the ped stopping dead at the curb
while the car proceeds. Plus the flow-density pair: `priority="true"` ⇒ peds stopped on the curb **0%**
of walkingarea steps and ≥60 distinct vehicles fully stopping; `priority="false"` ⇒ peds stopped
**91%**. Details: `SUMOPED-COVERAGE.md` §4.5.

### R5 — Vehicle↔pedestrian collisions: parity first, improvement later

⚠ **Restated after measurement. The original wording — "no vehicle body may overlap a pedestrian's
footprint at any step" — is not true of the oracle**, so it cannot be a parity requirement. At jam
density vanilla SUMO produces vehicle↔ped collisions by design: once `myWaitingTime > jamTimeCrossing`,
a ped sets `myAmJammed` and squeezes at `vMax/4` **ignoring the usual collision gating**
(`MSPModel_Striping.cpp:2200-2215`). Measured: 175 of 370 walking peds jammed ⇒ 80 collision records.
Full numbers and analysis in `SUMOPED-COVERAGE.md` §6.

Collisions are to be **minimized**, and improving on SUMO here is a real goal — but it comes *after*
parity, as a deliberate, gated deviation. Phase 1 splits the requirement three ways:

- **R5a — collision-set parity (Phase 1).** Reproduce SUMO's `<collision>` records exactly:
  `(time, type, lane, pos, collider, victim, colliderSpeed, victimSpeed)`. This is a *stronger* claim
  than "we never collide" — it pins the jam-squeeze behaviour to the tick.
  *Acceptance:* `golden.collisions.xml` matches exactly for every `_sumoped` scenario, and it is
  **empty** for every Tier A and Tier B scenario (verified: zero collisions at realistic density).
- **R5b — measurement (Phase 1).** Collision count, distinct (collider, victim) pair count, and max
  `colliderSpeed` are committed per scenario in the tracker. Without this baseline, "we made it better"
  is unfalsifiable.
  *Acceptance:* the tracker table is filled for every scenario, and a standing test computes world-space
  body-to-ped clearance per step, reporting the **minimum** per scenario — a bare "no overlap" is not a
  measurement.
- **R5c — reduce collisions below SUMO's (LATER, own design).** A deliberate deviation from parity,
  therefore governed by `docs/CONSTRAINT-high-realism-artefact-ladder.md` (target SUMO's flow, never its
  method) and gated behind an explicit opt-in so the parity goldens stay reachable. **Not Phase 1.**

Honest-SUMO flags apply throughout (`--time-to-teleport -1 --collision.action warn
--collision.check-junctions true`), per CLAUDE.md §Measurement discipline item 11. Note
`intermodal-collision.action` already defaults to `warn` (`MSFrame.cpp:382`), so ped/vehicle detection is
armed by default and an empty collision output is a real negative result.

### R6 — TL-controlled crossings
Peds obey the crossing's TL link state, including SUMO's `ignoreRed` grace window, and vehicles obey
theirs. Both directions must be parity-checked.

*Acceptance:* a signalized-crossing scenario (`xwalk-tls-*`) at parity, showing at least one full
red→green ped release and one vehicle stop for a green ped phase.

### R7 — Public API symmetry with vehicles
A host app must be able to spawn, query, and observe SUMO-model pedestrians the way it does vehicles:
a `PersonHandle` in its own id space, an active-person query, a per-step read projection, lifecycle
events, and runtime spawn/despawn. Documented in `docs/SUMOSHARP-API.md` alongside the vehicle API.

*Acceptance:* the tutorial-grade sample in `docs/TUTORIAL-*` compiles against `Traffic.sln` and drives
a crossing scene through the public API only, with no `internal` access.

### R8 — Development is driven by parity diffs, visible in the existing Sim.Viz HTML replay
The dev loop is: run the scenario in SumoSharp, run the same scenario in real SUMO, diff, and **look at
it**. Visualization reuses the existing `Sim.Viz` HTML replay (`Payload.cs` already carries disc
`kind: 2 = pedestrian` and crosswalk/ped-signal markers) — no new renderer.

*Acceptance:* `dotnet run --project src/Sim.Viz -- --sumoped-<scene> out.html` renders SumoSharp peds
**and** the SUMO golden's person positions as ground-truth rings in the same frame (the precedent is
`Sim.Viewer/RemotePedOverlay.cs`), so a divergence is visible, not just tabulated. Registered in
`scripts/gen-demos.sh`.

### R9 — Zero impact on the existing vehicle gate
Attaching the person subsystem is opt-in; not attaching it must leave the engine byte-identical.

*Acceptance:* after the port lands, `dotnet test tests/Sim.ParityTests -c Release` is still
**782/5 with all 661 goldens byte-identical**, `Sim.Bench` hash is still **`A134ED3716DDE7BC`**
(par == single), and the full `Traffic.sln` suite is green — including `Sim.LiveCity.Tests` (CLAUDE.md
§Measurement discipline item 9) and `Sim.Pedestrians.Tests` 324/324 (the ORCA layer must not regress).

### R10 — Determinism, and no `System.Random`
Runs are bit-reproducible and independent of thread order.

*Acceptance:* a person-trajectory hash computed twice must match, and must match under
`Engine.UseParallelPlan = true`. Phase 1 pins `--pedestrian.striping.dawdling 0` in every committed
`_sumoped` config — this is the pedestrian analogue of the vehicle `sigma=0` and is what makes exact
parity reachable (verified first-hand: ped speed is `1.388889 m/s` to the digit with it, jittering
`1.12–1.38` without).

---

### R11 (and R3c) — Speed heterogeneity within the crossing group (the two-regime split)

The owner wants the crossing horde's members to move at **visibly different speeds** — this is a large
part of why SUMO's crossings look right. That requirement collides head-on with exact parity, because
speed spread is the one part of the striping model that is RNG-fed. The collision is resolved by
running the model in **two regimes**, not by choosing between them:

| | **Parity regime** (the test harness) | **Production regime** (demos, Live City, hosts) |
| --- | --- | --- |
| `pedestrian.striping.dawdling` | **0** | 0.2 (SUMO default) |
| ped vType `speedDev` | **0** | 0.1 (SUMO default) |
| what it proves | the *mechanism* is a faithful port | the *look* the owner asked for |
| parity bar | **exact** — R2, R3a, R3b, R3d | statistical / behavioural only |

This works because the three behaviours in R3a/R3b/R3d are **deterministic** — they fall out of the
stripe-utility fold, with no RNG anywhere in the path. So the harness proves the crowd mechanics
exactly, and the production regime layers extra heterogeneity on top of an already-proven mechanism.

⚠ **Measured correction, in our favour.** Speed spread is *not* purely RNG-fed. On a saturated crossing
with `dawdling=0` **and** `speedDev=0`, the peds mid-crossing showed **min 0.000 / median 1.198 /
max 1.389 m/s** — the spread emerges from the interaction dynamics (peds slowing for each other), not
from randomness. So the "members move at different speeds" look is **substantially on the exact-parity
side and is golden-checkable**. The two-regime split below still stands for the *additional* spread
`dawdling`/`speedDev` provide, but it is a garnish on a deterministic effect, not the source of it.
Numbers: `SUMOPED-COVERAGE.md` §7.

Heterogeneity has two independent sources, and they are **not** equally hard:

- **Per-person `speedFactor`** — drawn *once at creation* from the vType's `speedDev` distribution.
  This is the dominant visual source (one ped is simply a faster walker than another for its whole
  trip) and it is **per-entity**, so SumoSharp can reproduce it with the existing
  `VehicleRng.SeedFor(seed, entityIndex, salt)` discipline — a distinct salt from any vehicle stream.
  No conflict with CLAUDE.md, and it is available in *both* regimes (pinned to `speedFactor=1` in
  parity scenarios only because the goldens pin it, not because we cannot do it).
- **Per-step `dawdling`** — `MSPModel_Striping.cpp:2179`, drawn from SUMO's single **process-global**
  stream, once per moving ped per step, in `by_xpos_sorter` order across a FORWARD-then-BACKWARD pass.
  Bit-exact reproduction would mean serializing that draw order, which is exactly what CLAUDE.md's
  per-entity-seeded rule forbids. Phase 1 implements dawdling with a **per-entity seeded** stream
  (same shape, same magnitude, different draw sequence) and declares it a named, documented deviation.

*Acceptance (R11):* the production-regime demo shows a measurable spread of per-ped crossing speeds
(report min/median/max over a horde of ≥10), the parity-regime goldens are unaffected because both
knobs are pinned, and a test asserts that flipping either knob to its SUMO default changes **no**
`_sumoped` golden (they must configure it explicitly, never inherit it).

---

## 4. Non-goals (Phase 1)

- **R-N1 — Bit-exact parity with nonzero `dawdling`.** Covered by R11 above: nonzero dawdling is
  *supported* in the production regime, but is not bit-exact against SUMO and is not golden-gated.
  A statistical-parity phase for it would need its own design, exactly as `sigma>0` did for vehicles.
- **R-N2 — The LOD hybrid** (low-power ↔ SUMO-ped promotion near crossings). That is Phase 2 and gets
  its own design doc. Phase 1 must *leave the seam open* — see design §9 — but must not build it.
- **R-N3 — Replacing the ORCA crowd layer.** `src/Sim.Pedestrians` stays, unchanged and green. The two
  tiers coexist; Phase 2 is what couples them. Nothing in Phase 1 may edit ORCA-layer behaviour.
- **R-N4 — Consolidating the three existing net.xml readers.** `Sim.Ingest/NetworkParser.cs`,
  `Sim.Pedestrians/PedNetworkParser.cs` and `Sim.Pedestrians/Crossing/CrossingTlReader.cs` are three
  independent reads today. Phase 1 extends only the first (see design §3) and leaves the other two
  alone; merging them risks 324 green tests for no parity gain.
- **R-N5 — TraCI/`moveToXY` remote control of persons**, containers, `personTrip` with public
  transport, ride/board stages, and `MSPModel_JuPedSim`. Walking only.
- **R-N6 — `--no-internal-links`** (SUMO's straight-line junction-distance fallback).
- **R-N7 — Sublane vehicle model interaction with peds** (`MSLCM_SL2015`'s ped checks).

---

## 5. The oracle, and what it already proves

SUMO 1.20.0 is installed in this environment (`pip install eclipse-sumo==1.20.0` →
`/usr/local/bin/sumo`; note Ubuntu's `apt` ships **1.18**, the wrong version) and the source is checked
out at `/sumo` (tag `v1_20_0`). Everything below was measured first-hand this session and is recorded
with its commands in `SUMOPED-DESIGN.md` §2.

The reference behaviour, from a 4-arm priority junction with `--sidewalks.guess --crossings.guess`, one
car `wc→ce` and one ped `<walk from="cn" to="cs"/>`, at `--pedestrian.striping.dawdling 0`:

```
 t   car                        ped
 0   wc_1 pos  5.10 spd 13.89   cn    pos 0.00 spd 0.00
 3   wc_1 pos 43.99 spd 11.11   :c_w1 pos 0.08 spd 1.39   car BRAKES for the ped nearing the curb
 4   wc_1 pos 50.59 spd  6.61   :c_w1 pos 0.00 spd 0.07   ped STOPS on the walkingarea
 5   :c_10_0      spd  9.21     :c_w1 pos 0.00 spd 0.00   ped waits, car crosses
 6   ce_1 pos  4.41 spd 11.81   :c_w1 pos 0.00 spd 0.00
 7   ce_1 pos 18.30 spd 13.89   :c_c1 pos 5.01 spd 1.39   ped ENTERS the crossing once clear
```

R4 and R5 are both visible in those seven steps, and it is deterministic. This is the first fixture
(`xwalk-priority-1v1`) and the first success condition in the task list.

---

## 6. Committed scenario set (Phase 1 target)

New group `scenarios/_sumoped/`, following the shape in `scenarios/README.md`. Unlike the ORCA layer's
`scenarios/_ped/` (behavioural-only, no SUMO reference), **these carry real goldens and gate
`dotnet test`.** Note that `--save-state` writes **no** person data (verified), so there is no
`golden.state.xml` cross-check for persons — the primary golden is `golden.fcd.xml`, the secondary
aggregate is `golden.tripinfo.xml` `<personinfo>`.

| Scenario | What it pins | Requirements |
| --- | --- | --- |
| `walk-straight-1` | one ped, one sidewalk, free walk — the vType/init cross-check | R1, R2, R10 |
| `walk-oncoming-2` | two peds head-on on one sidewalk — stripe choice, `LATERAL_PENALTY`, oncoming reserve | R1, R2 |
| `walk-junction-turn` | ped crosses a walkingarea diagonal, no vehicles — `WalkingAreaPath` + junction-local ped router | R1, R2 |
| `xwalk-priority-1v1` | **the fixture in §5** — car yields, ped waits at curb, ped crosses | R2, R4, R5 |
| `xwalk-priority-queue` | one car stream + a ped platoon — accumulation on the curb, distinct stripes | R3a, R4, R5 |
| `xwalk-priority-horde` | ≥6 peds released together — **abreast, ≥3 stripes, not single-file** | R3b |
| `walk-passby-queue` | a ped walks the sidewalk past a curb cluster — routes around, never stalls | R3d |
| `xwalk-tls-release` | signalized crossing, one full red→green ped release | R2, R6 |
| `walkingarea-shared` | vehicle drives across a bare walkingarea (no marked crossing) — `checkWalkingAreaFoe` | R2, R4, R5 |
| `sidewalk-shared-lane` | ped on a lane vehicles also use — `nextBlocking` leader path | R2, R5a |
| `counterflow-sidewalk-4m` | 75 peds each way on ONE 6-stripe sidewalk — lane self-organisation | R3d |
| `counterflow-sidewalk-6m` | same at 214 concurrent on 9 stripes | R3a, R3d |
| `counterflow-crossing` | peds both ways over the SAME crossing — **does not happen by default**, see coverage §4.2 | R3a, R3b, R3d |
| `counterflow-crossing-jam` | the same oversaturated → head-on deadlock + squeeze-through | R3a, R5a |
| `turning-vs-crossing-peds` | cars turning **left and right** held on the internal lane by peds on the **exit** crossing | R4, R5a |
| `ped-turners-through-bunch` | peds **turning at the corner** (sidewalk only, zero crossings) threading the queue waiting to cross | R3d |
| `ped-turners-gridlock` | the same at 2.4x car flow — corner gridlock, the degraded end of R3d | R3a, R3d |
| `zebra-1v1-yields` / `xwalk-1v1-noprio` | **the R4b A/B pair** — one car, one ped, differing only in `<crossing priority>` | R4b |
| `zebra-flow-balanced` | ped-priority zebra at flow density — 68 vehicles fully stop, peds never wait | R4b |
| `zebra-flow-pedheavy` | ped-priority zebra oversubscribed — cars starved (45 of 69 still queued) | R4b |

### R12 — Coverage must be demonstrated, not asserted

The ten scenarios above are **Tier A/B seeds, not the coverage claim**. The full plan — a branch
inventory of `MSPModel_Striping` derived from the source, a branch→scenario matrix, in-port coverage
counters, and the three-tier ladder that carries it up to saturated multi-lane crossings — is
`SUMOPED-COVERAGE.md`, and it is part of Phase 1, not a follow-up.

Headline shape (all sizes measured, §`SUMOPED-COVERAGE.md` §2–3):

| tier | scale | parity | golden shape | count |
| --- | --- | --- | --- | --- |
| **A — micro** | 1–4 peds, ≤80 steps | exact, full horizon | FCD 10–50 KB | ~20 |
| **B — meso** | 10–40 peds + vehicles, 120–200 steps | exact, full horizon | FCD 150–400 KB | ~8 |
| **C — macro** | 300+ persons, saturated multi-lane, 300 s | exact over a window + full-horizon aggregates | ~1.6 MB total | 2–3 |

Six axes must be varied deliberately: crossing **width** (1 / 6 / 12 stripes — independent of road
lanes, and a 1-stripe crossing is the only route to the `jamTimeNarrow` branch), crossing **length**
(1 / 2 / 3 road lanes), **control** (priority / TL / bare walkingarea), **ped demand** (single /
counterflow / platoon / saturated / jammed), **vehicle demand** (none / single / stream / saturated),
and **ped flow mix** (unidirectional / counterflow / pass-by).

*Acceptance:* every branch ID in `SUMOPED-BRANCH-INVENTORY.md` is either witnessed by a named scenario
plus a named oracle signal, or listed as an **admitted hole with a reason and owner sign-off**;
`AllBranchesCoveredTest` passes; each scenario's claimed branch set matches what it actually fires.

**On CLAUDE.md §Measurement discipline item 1.** That lesson says goldens are small and cannot contain a
saturated junction, so both surfaces must accept a change. Tier C narrows that gap but does not close
it: a saturated *golden* is now affordable and exact (determinism verified at 30,549 person rows), yet
the demo/`_bench` surfaces still have no SUMO reference. Both surfaces are still required.

---

## 7. Definition of done for Phase 1

All of R1–R10 accepted first-hand by the reviewer (not on an implementor's report, per CLAUDE.md
§Subagents), the tracker fully ticked, and:

```
dotnet test -c Release                      # full Traffic.sln, green, including LiveCity + Pedestrians
dotnet test tests/Sim.ParityTests -c Release   # 782 + new ped tests, 661 goldens byte-identical
dotnet run -c Release --project src/Sim.Bench  # A134ED3716DDE7BC, par == single
```
