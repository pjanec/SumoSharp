# SUMOPED — Tasks

**Status: PROPOSAL — awaiting owner sign-off.**

Work breakdown for the SUMO pedestrian port. The WHAT is `SUMOPED-REQUIREMENTS.md`; the HOW is
`SUMOPED-DESIGN.md` — tasks **reference** design sections, they do not restate them. Checklist:
`SUMOPED-TRACKER.md`.

**Every task below states success conditions that are specific, measurable, and first-hand verifiable.**
Per CLAUDE.md §Subagents, a task is closed only when the reviewer has confirmed its success conditions
personally — read the diff, read the test to confirm it asserts the real condition, re-run the command.
An implementor's report is never sufficient.

## Standing rules for every task

- **S-a.** Every ported function carries a `/sumo/<path>:<line>` comment naming its C++ original.
- **S-b.** No `System.Random`, ever. Per-entity seeded streams only (design §12).
- **S-c.** Nothing may be committed with `Engine.Persons` non-null by default.
- **S-d.** After any task that touches `src/Sim.Core` or `src/Sim.Ingest`, re-run the **full** gate
  (`dotnet test -c Release`, not just `Sim.ParityTests`) — CLAUDE.md §Measurement discipline item 9.
- **S-e.** Commit before delegating; end a delegation at "compiles, verified, committed"; never delegate
  *waiting* for a long run (CLAUDE.md §Subagents).

---

## Stage 0 — Oracle, coverage inventory, and fixtures (no C# yet)

### SP-0.0 — Build and commit the branch inventory
`SUMOPED-COVERAGE.md` §1. Read `MSPModel_Striping.{h,cpp}` in full plus the ped-relevant parts of
`MSLink.cpp`/`MSLane.cpp`/`MSStageWalking.cpp`; enumerate **every** behavioural branch — every decision
whose outcome changes a pedestrian trajectory — as `docs/SUMOPED-BRANCH-INVENTORY.md`.
One row per branch: stable ID, `file:line`, the quoted C++ predicate, what changes when it fires,
FCD-observability (`DIRECT` / `LATERAL` / `INDIRECT` / `HIDDEN`), and the minimal net+demand to trigger it.
**Success:** the inventory covers, at minimum, every `if` in `walk()` and its utility folds; every branch
of `getNextLane`, `moveToNextLane`, `arriveAndAdvance`, `getNeighboringObstacles`, `getVehicleObstacles`
(incl. the "vehicle still behind while overlapping" case), `getNextLaneObstacles`, `addCrossingVehs`
(incl. the fully-blocked pin-all-stripes pass), `addVehicleFoe`, the three walkingarea sub-branches of
`moveInDirection`, both `mergeObstacles` overloads, `distanceTo`, `stripe`/`otherStripe`, `getReserved`,
the whole jam family, `MIN_STARTUP_DIST`, the step-aside branch, `ignoreRed`, `getImpatience`, both
clauses of `blockedAtDist`, and `checkWalkingAreaFoe`. Every `HIDDEN` row names the cheapest oracle
signal that would witness it. A reviewer spot-checking ten random `if`s in the `.cpp` finds all ten in
the table.

### SP-0.1 — Re-establish the oracle, and commit the recipe
Design §2. Clone `/sumo` at `v1_20_0`; `pip install eclipse-sumo==1.20.0`.
**Success:** `sumo --version` reports 1.20.0; `/sumo/src/microsim/transportables/MSPModel_Striping.cpp`
is 2725 lines; the recipe (including the "apt ships 1.18, do not use it" warning) is committed in
`scenarios/_sumoped/README.md`.

### SP-0.2 — Author the Tier A / Tier B scenarios
Requirements §6 + `SUMOPED-COVERAGE.md` §3–4. Each gets `nodes.nod.xml`/`edges.edg.xml` sources, a
`netconvert` regeneration line in `NOTES.md`, `net.net.xml`, `rou.rou.xml`, `config.sumocfg`.
Roughly 20 Tier A (1–4 peds, ≤80 steps, one mechanism each) and 8 Tier B (10–40 peds + vehicles,
120–200 steps). The six axes of coverage §4 must each take every listed value somewhere in the set —
in particular a **1-stripe** (`--default.crossing-width 0.64`) and a **12-stripe** (`8.00`) crossing,
and a **pass-by** flow of peds who turn at the junction without crossing.
**Success:** each `config.sumocfg` explicitly sets `--pedestrian.model striping` **and**
`--pedestrian.striping.dawdling 0`; each ped vType sets `speedDev="0" speedFactor="1"`; no
`departPos="random"`/`departPosLat="random"` anywhere; each `NOTES.md` names the axis values it pins and
the SP-0.0 branch IDs it claims to fire. A test asserts the four pinning properties by parsing every
`_sumoped` config, so the pinning can never silently lapse.

### SP-0.2b — Author the Tier C saturated scenarios
`SUMOPED-COVERAGE.md` §3, §4.1. 2–3 scenarios: a saturated multi-lane signalized junction (2-lane arms,
4 car flows + 4 personFlows, 300 s — verified to reach steady state by t≈80 with ~110–140 concurrently
walking), plus a **jam-regime** variant at `personFlow period="0.5"` that drives `jammed > 0`, and a
narrow-crossing variant. Honest-SUMO flags: `--time-to-teleport -1 --collision.action warn
--collision.check-junctions true`.
**Success:** the saturated scenario produces ≥25,000 person FCD rows; the jam variant reports
`jammed > 100` in `--statistic-output`; two independent SUMO runs give **byte-identical** FCD bodies
(re-verify determinism per scenario, do not assume it); the committed footprint per Tier C scenario is
≤2 MB using the windowed shape in coverage §3.

### SP-0.3 — Generate and commit the goldens (seven output kinds, not one)
Design §2.3, §8.2; coverage §2. Extend `scripts/regen-goldens.sh` to cover `_sumoped` with the full
person output set.
**Success:** every `_sumoped` scenario has, as applicable to its tier: `golden.fcd.xml` (windowed via
`--device.fcd.begin` for Tier C — verified to start the FCD at exactly that time),
`golden.personsummary.xml`, `golden.personinfo.xml`, `golden.statistic.xml`, `golden.collisions.xml`,
`golden.netstate.xml` (Tier A/B only — it is large), a normalised/sorted `golden.warnings.txt` capturing
the `is jammed` / `collision with person` stderr lines, plus `tolerance.json` and `provenance.txt`
recording `sumo_version=1.20.0` + input sha256s. Re-running the script twice produces byte-identical
goldens. Total added golden bytes are reported in the tracker (budget context: the repo's existing FCD
goldens total 5.1 MB, largest single 1.26 MB).

### SP-0.4 — Assert the R3 behaviours **on the oracle**
Requirements R3, design §8.1. Before requiring anything of SumoSharp, prove the goldens contain it.
**Success:** a test reads the committed goldens and asserts: `xwalk-priority-queue` has ≥4 peds
simultaneously stopped on one walkingarea on ≥4 distinct stripes; `xwalk-priority-horde` has ≥3 peds
entering the crossing within 2 s on ≥3 distinct stripes; `walk-passby-queue` has a ped with `speed > 0`
every step while ≥3 others are stopped beside it; `xwalk-priority-1v1` reproduces the deceleration
`13.89 → 11.11 → 6.61`. **A scenario that fails this is mis-authored and goes back to SP-0.2.**

### SP-0.5 — Collision baseline on the oracle
Requirements R5a/R5b, coverage §6. **Not** a zero-overlap invariant — that is false of the oracle.
**Success:** a shared helper computes world-space vehicle-body-to-ped clearance per step from a golden;
run over every `_sumoped` golden it reports, per scenario: minimum clearance, `<collision>` count,
distinct `(collider, victim)` pair count, and max `colliderSpeed`. Tier A and Tier B must report
**zero** collisions. Tier C's numbers are recorded in the tracker as the R5c improvement baseline.
Reference measurement from this session's jam-regime run: 80 collisions / 29 distinct pairs / max
`colliderSpeed` 2.60 with only 1 of 80 above 0.1 m/s.

### SP-0.6 — Coverage matrix, first pass
`SUMOPED-COVERAGE.md` §4, §8. Map every SP-0.0 branch ID to the scenario(s) that fire it and the oracle
signal that witnesses it.
**Success:** the matrix is committed; every ID is either mapped or listed in coverage §8 as an admitted
hole **with a reason**; the count of unmapped IDs is reported to the owner for sign-off **before**
Stage 1 begins. Holes discovered here become new scenarios in SP-0.2/SP-0.2b, not deferred silently.

---

## Stage 1 — Network model

### SP-1.1 — Ped elements in `Sim.Ingest`
Design §3.1. Additive only: `Lane.Permissions`, `Edge.Function`, `Edge.CrossingEdges`, crossing/WA lane
shape+width+length, ped `<connection>` chain, crossing TL link index.
**Success:** parsing every `_sumoped` net yields the same crossing count, walkingarea count, and
per-crossing width that `sumo --net-dump`-equivalent inspection of the net file reports; **and** the
equivalence test `LaneAllowsRoadVehicle(allow) == Permissions.AllowsAnyRoadVehicle()` passes over every
lane of all 91 committed scenarios; **and** the full gate is unmoved (S-d).

### SP-1.2 — Walkingarea foes for vehicle links
Design §3.1, §6.3.
**Success:** on `walkingarea-shared`, the set of `(vehicleLink → walkingareaEdge)` foe pairs matches what
SUMO computes, verified by a one-off dump comparison recorded in the scenario NOTES.

### SP-1.3 — ⚠ Resolve the begin-of-timestep ordering **by trace**
Design §5.1. Establish where `MovePedestrians` sits relative to the TLS switch command in SUMO's
begin-of-timestep event queue, and whether SumoSharp's actuated-TLS advance (`Engine.cs:3234`) must move
relative to the new `AdvancePersons` slot.
**Success:** a committed trace from a real SUMO run on `xwalk-tls-release` showing, for one step across
a phase boundary, whether the ped saw the old or the new phase — plus a one-paragraph finding in the
design doc. **Reasoning from the event-queue source is explicitly not acceptable evidence** (CLAUDE.md
§Measurement discipline item 2).

### SP-1.4 — Static precompute
Design §3.2: `WalkingAreaPaths`, `WalkingAreaFoes`, `MinNextLengths`, `NumStripes`.
**Success:** on `walk-junction-turn`, every `(from,to)` path's length matches SUMO's own
`WalkingAreaPath.length` to 1e-9, dumped from a debug build or via the `<personinfo>` `routeLength`
cross-check; `NumStripes` matches `floor(width/0.64)` on every crossing/WA lane in all eight scenarios.

---

## Stage 2 — Harness (fail first)

### SP-2.1 — Person FCD parse + compare
Design §8. New `PersonFcdParser`, `PersonTrajectoryPoint/Set`, `PersonTrajectoryComparator`; extend
`ToleranceConfig` with `comparedPersonAttributes`.
Compared attributes: `edge` (exact string), `pos`, `speed`, `x`, `y`, and **`angle`** — `angle` is not
cosmetic, it is the lateral-velocity witness (coverage §2.1) and gets a tight tolerance.
**Success:** parsing a committed `golden.fcd.xml` yields the exact person row count found by
`grep -c "<person"`; comparing a golden against **itself** reports zero mismatches; comparing it against
a golden with one row perturbed by `2 × tolerance` reports exactly one attribute failure naming the right
person, time, and attribute. `ToleranceConfig` throws for an unconfigured compared person attribute.

### SP-2.1b — The other six comparators
Coverage §2. Person FCD alone under-uses the oracle. Add parsers + comparators for:
`golden.personsummary.xml` (per-step time series — exact integer match on every column, `jammed`
included), `golden.personinfo.xml` (per-person aggregate), `golden.statistic.xml`
(`<persons>`/`<personTeleports>`/`<pedestrianStatistics>`), `golden.collisions.xml` (R5a — exact set
match), `golden.netstate.xml` (per-edge person membership + `stage`), and `golden.warnings.txt`
(normalised/sorted stderr lines, for the FCD-`HIDDEN` branches).
**Success:** each comparator, run against its own golden, reports zero differences; run against a
golden with one field perturbed, reports exactly that field. The person-summary comparator is exact
(integers, no tolerance). The warnings comparator normalises away timestamps only where the golden says
so, and is order-insensitive by sorting.

### SP-2.1c — The lateral-state recovery helper
Coverage §2.1. Person FCD emits no `posLat`, but lateral state **is** fully recoverable: `myRelY` by
projecting `(x, y)` onto the lane centreline, and `mySpeedLat` by inverting `angle`
(`MSPModel_Striping.cpp:2342-2349` — `angle = laneRotation ± atan2(mySpeedLat, max(mySpeed, eps))`).
**Success:** a shared helper yields `(pos, posLat, stripe, speedLat)` from an FCD row + lane geometry.
Three self-validating checks, all on committed goldens: (a) derived `pos` agrees with the FCD's own
`pos` attribute to 1e-6 — the FCD attribute is the cross-check that validates the projection;
(b) derived stripe index is integral to within 1e-9 of a half-open bin boundary; (c) derived
`|speedLat|` never exceeds `min(max(vMax*LATERAL_SPEED_FACTOR, vMax-xSpeed), stripeWidth)`, and on the
saturated scenario its maximum equals **0.6401 m/s** (the `stripeWidth` clamp) with a mode at
**0.5556 m/s** (`vMax * 0.4`) — both measured this session, so a regression in the inversion is caught
by a number, not by inspection. Used by **both** arms of every derived assertion, so a bug in it cannot
create a false pass.

### SP-2.2 — One failing parity test per scenario
One test class per scenario, following the `Rung1ParityTests.cs` pattern.
**Success:** all compile and **fail** with a clear "no persons produced" diagnostic. A test that passes
at this stage is vacuous and must be fixed before Stage 3 starts.

### SP-2.4 — Coverage counters and the two coverage tests
Coverage §5.
**Success:** `BranchCounters` has one counter per SP-0.0 branch ID; `AllBranchesCoveredTest` reports
which IDs the `_sumoped` suite fails to hit (initially: all of them — it must fail honestly at this
stage); `PerScenarioClaimTest` compares each scenario's `NOTES.md` claim list against the counters it
fires. Both tests name the specific IDs, never just a count.

### SP-2.3 — Person trajectory hash
Requirement R10, mirroring `Sim.Bench`'s `TrajectoryHash`.
**Success:** hashing the same run twice gives the same value; the value is recorded in the tracker.

---

## Stage 3 — The stepper, straight sidewalk only

### SP-3.1 — `PersonRuntime`, `StripingParams`, stripe math
Design §4.1, §5. Field set fixed by SUMO's `saveState` enumeration.
**Success:** every constant in the design's table is present with SUMO's exact value and its
`.cpp:line` anchor; `Stripe`/`OtherStripe`/`GetStripeOffset` unit tests cover the boundary cases
(`offset == ±threshold`, `numStripes == 1`, `width < stripeWidth`).

### SP-3.2 — `Obstacle`, `ObstacleType`, `DistanceTo`, `MergeObstacles`
Design §5.4. Note `ObstacleType`'s numeric ordering is load-bearing (`>= OBSTACLE_END` tests).
**Success:** unit tests assert the `DIST_OVERLAP` / `DIST_BEHIND` / normal-gap trichotomy, the min-gap
asymmetry (`getMinX`/`getMaxX` put the gap on the leading edge only), and that a Ped obstacle displaces
a topological one at exactly equal distance.

### SP-3.3 — `Walk()`
Design §5.3. Engine-free, in its own file, all seven utility folds in SUMO's order.
**Success (this is the pivotal one):** a unit suite feeds hand-built `Obstacle[]` arrays and asserts the
exact resulting `(RelX, RelY, Speed, SpeedLat, AmJammed)`. It must include at minimum: free walk;
blocked-ahead → stop; blocked-ahead with a free adjacent stripe → **lateral move, not stop** (R3b);
overlapping neighbour → the `OBSTRUCTED_PENALTY` shadow prevents sidestepping past them (R3a);
`MIN_STARTUP_DIST` refusal; jam→squeeze at `jamTimeCrossing`; un-jam on reopening.
Each case cites the `.cpp` line whose behaviour it pins.

### SP-3.4 — `GetNeighboringObstacles`, `MoveInDirectionOnLane`, `MoveInDirection`, `ArriveAndAdvance`, `MoveToNextLane`
Design §5.2, §5.4. Single-threaded; FORWARD pass fully, then BACKWARD.
**Success:** `walk-straight-1` and `walk-oncoming-2` reach **exact parity** (SP-2.2's tests flip to
green with no tolerance widening). The x-position sort's id tie-break is present and covered by a test
with two peds at identical `RelX`.

### SP-3.5 — Person demand + stage + FCD output
`<person>`/`<walk edges=>` parsing, `MSStageWalking` equivalent, person FCD writer emitting
`edge`/`pos` (completing what `PersonFcdWriter.cs:14-16` defers).
**Success:** SumoSharp's own person FCD for `walk-straight-1` is byte-comparable in schema to SUMO's
(same attributes, same order, same precision), and `<personinfo>` `routeLength`/`duration`/`timeLoss`
match the golden tripinfo.

---

## Stage 4 — Junctions

### SP-4.1 — `WalkingAreaPath` geometry
Design §3.2, risk #1. Straight line + extrapolation + Bezier at `walkingarea-detail=4`; reverse-path
aliasing; cross-path projection as obstacle-only clones.
**Success:** `walk-junction-turn` at exact parity — **with no vehicles in the scenario**, so a
divergence has exactly one possible cause.

### SP-4.2 — `JunctionPedRouter`
Design §5.5. Junction-restricted Dijkstra, cost `length/speed + TL_RED_PENALTY + crossing penalty`.
**Success:** on a net with **two** viable crossings, the chosen crossing matches SUMO's on every step of
the golden; and with the first crossing's link forced closed, the ped re-routes to the second (the
`prohibited` re-run path), matching SUMO.

### SP-4.3 — `GetNextLane` + `GetNextLaneObstacles`
Design §5.5, §5.4. All branches: normal, internal, crossing, walkingarea, loop.
**Success:** `walk-junction-turn` and `xwalk-priority-queue` (peds only, vehicles suppressed) at exact
parity, including the `stripeEnd` walls where the next lane is narrower.

### SP-4.4 — R3 crowd behaviours, at parity
Requirements R3a/R3b/R3d.
**Success:** `xwalk-priority-queue`, `xwalk-priority-horde` and `walk-passby-queue` at exact parity, and
the **same** derived assertions SP-0.4 ran against the oracle now pass against SumoSharp's output — same
helper, same thresholds. Distinct-stripe counts must match the golden exactly, not merely clear the ≥3/≥4
bar.

---

## Stage 5 — Vehicle coupling

### SP-5.1 — `BlockedAtDist` + phantom-leader injection
Design §6.1, §6.2, risks #5 and #6. Inject a null-vehicle `JunctionLeaderCandidate` into the existing
junction-leader path. Gated on `Persons != null`.
**Success:** `xwalk-priority-1v1` at exact parity **including the `13.89 → 11.11 → 6.61` profile**; a
dedicated test asserts the 2 s standing-ped clause by holding a ped stationary at the curb and showing
the vehicle proceeds after — not before — 2 s; and the full gate is unmoved (S-d), re-run in this task,
not deferred to Stage 7.

### SP-5.2 — `AddCrossingVehs` + `AddVehicleFoe`
Design §6.4.
**Success:** `xwalk-priority-queue` (vehicles enabled) at exact parity; a test asserts the "fully
blocked ⇒ pin every stripe" second pass by constructing a crossing where every non-reserved stripe is
vehicle-occupied and showing no ped enters.

### SP-5.3 — `CheckWalkingAreaFoe`
Design §6.3.
**Success:** `walkingarea-shared` at exact parity, including the 75° `IsInFront` bearing test and the
oncoming discount.

### SP-5.4 — `HasPedestrians` / `NextBlocking` on shared lanes
Design §6.5.
**Success:** `sidewalk-shared-lane` at exact parity.

### SP-5.5 — Collision-set parity and the collision baseline
Requirements R5a/R5b, coverage §6.
**Success:** `golden.collisions.xml` matches **exactly** on `(time, type, lane, pos, collider, victim,
colliderSpeed, victimSpeed)` for every `_sumoped` scenario — empty for Tier A and Tier B, and an exact
set match for the Tier C jam variant. SP-0.5's clearance helper, run over our own output, reports the
same minimum clearance per scenario as it does over the golden. The tracker's collision-baseline table
is filled from our output and agrees with the oracle's. Note this is the R5c improvement baseline; do
**not** "fix" a collision here — reproducing SUMO's is the requirement.

---

## Stage 6 — Traffic lights

### SP-6.1 — Crossing link state, `IgnoreRed`, `GetImpatience`
Design §7, and whatever SP-1.3's trace determined.
**Success:** `xwalk-tls-release` at exact parity across at least one full red→green→red cycle, with the
ped's release step matching to the tick.

---

## Stage 7 — API, visualization, production regime, final gate

### SP-7.1 — Public API
Design §10, Requirement R7.
**Success:** a sample in `docs/TUTORIAL-SUMO-PEDESTRIANS.md` compiles as part of `Traffic.sln` and drives
`xwalk-priority-1v1` end-to-end through public API only — verified by the sample project having no
`InternalsVisibleTo`. `docs/SUMOSHARP-API.md` gains a person section in the same style as the vehicle one.

### SP-7.2 — Coordinate contract (the Phase 2 hinge)
Design §9.
**Success:** `WorldOf(edge,pos,posLat)` round-trips through `TryResolveToEdge` to within 1e-6 for 1000
sampled points across every `_sumoped` net; `SpawnPersonAt` mid-edge produces a ped whose next 10 steps
match a ped that walked there naturally (the no-pop handover property).

### SP-7.3 — Sim.Viz scenes + parity overlay
Design §11, Requirement R8.
**Success:** `--sumoped-<scene>` renders for all eight scenarios; the overlay mode draws golden
ground-truth rings alongside live peds; stripe lines render on crossings; all registered in
`scripts/gen-demos.sh` under a **SUMO pedestrians** category. Verified by opening the HTML, not just by
the command exiting 0.

### SP-7.4 — Production regime
Requirement R11, design §12.
**Success:** with `dawdling=0.2` and `speedDev=0.1`, a ≥10-ped horde crossing shows a measured spread of
per-ped crossing speeds (min/median/max reported in the tracker); **and** a test asserts that flipping
either knob changes **no** `_sumoped` golden, because every one of them sets both explicitly.

### SP-7.4b — Coverage close-out
Requirement R12, coverage §1, §8.
**Success:** `AllBranchesCoveredTest` is green, or every remaining miss is listed in coverage §8 as an
admitted hole with a reason and explicit owner sign-off. The number of covered vs. admitted-hole branch
IDs (out of 149) is recorded in the tracker. `PerScenarioClaimTest` green.

### SP-7.5 — Final gate
Requirement R9.
**Success, all re-run first-hand:**
```
dotnet test -c Release                          # full Traffic.sln green
dotnet test tests/Sim.ParityTests -c Release    # 782 + new, 661 goldens byte-identical
dotnet run -c Release --project src/Sim.Bench   # A134ED3716DDE7BC, par == single
```
plus `Sim.LiveCity.Tests` 92/92 and `Sim.Pedestrians.Tests` 324/324 (the ORCA layer unregressed).

### SP-7.6 — Doc reconciliation
Requirement R-N3.
**Success:** `docs/PEDESTRIAN-OVERVIEW.md` §3's "we do NOT port MSPModel_Striping" is rewritten to
describe two coexisting tiers; `docs/PEDESTRIANS.md` gains a pointer to the new subsystem;
`docs/README.md` indexes the SUMOPED doc set; `scenarios/README.md` documents `_sumoped` as a
**golden-bearing** group (unlike `_ped`); `docs/TASKS-TODO.md` gains the new test counts.

---

## Suggested batching for the Opus→Sonnet loop

CLAUDE.md §Subagents' orchestration loop. Batches are sized so each ends at a verifiable gate:

| Batch | Tasks | Ends at |
| --- | --- | --- |
| B0 | SP-0.0 | branch inventory reviewed and corrected (a first pass of 149 rows exists) |
| B1 | SP-0.1 … SP-0.6 | Tier A/B/C goldens committed; oracle proven to contain the R3 behaviours; coverage matrix mapped and holes signed off |
| B2 | SP-1.1, SP-1.2, SP-1.4 | net model extended, vehicle gate unmoved |
| B3 | SP-1.3 | the ordering trace (Opus does this one — it is a judgment call) |
| B4 | SP-2.1 … SP-2.4 | all seven comparators + the stripe helper + coverage counters; every test failing honestly |
| B5 | SP-3.1 … SP-3.3 | `Walk()` unit-proven in isolation |
| B6 | SP-3.4, SP-3.5 | two scenarios at exact parity |
| B7 | SP-4.1 … SP-4.4 | junctions + the owner's crowd behaviours at parity |
| B8 | SP-5.1 … SP-5.5 | vehicle coupling; gate re-run inside the batch |
| B9 | SP-6.1 | TL crossings |
| B10 | SP-7.1 … SP-7.6 (incl. SP-7.4b) | API, viz, production regime, final gate, docs |
