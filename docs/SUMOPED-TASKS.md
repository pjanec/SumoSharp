# SUMOPED — Tasks

**Status: PROPOSAL — awaiting owner sign-off.**

Work breakdown for the SUMO pedestrian port. The WHAT is `SUMOPED-REQUIREMENTS.md`; the HOW is
`SUMOPED-DESIGN.md`; **the method — the four verification ladders, the stage gate and the divergence
protocol — is `SUMOPED-PROCESS.md`, and every stage below is closed against its §5 gate.** Tasks
**reference** these docs, they do not restate them. Checklist: `SUMOPED-TRACKER.md`.

**Every task below states success conditions that are specific, measurable, and first-hand verifiable.**
Per CLAUDE.md §Subagents, a task is closed only when the reviewer has confirmed its success conditions
personally — read the diff, read the test to confirm it asserts the real condition, re-run the command.
An implementor's report is never sufficient.

## Standing rules for every task

- **S-a.** Every ported function carries a `/sumo/<path>:<line>` comment naming its C++ original.
- **S-b.** No `System.Random`, ever. Per-entity seeded streams only (design §12).
- **S-c.** Nothing may be committed with `Engine.Persons` non-null by default.
- **S-d.** After any task that touches `src/Sim.Core` or `src/Sim.Ingest`, **or that adds a committed
  net**, re-run the **full** gate (`dotnet test -c Release`, not just `Sim.ParityTests`) — CLAUDE.md
  §Measurement discipline item 9. ⚠ The "adds a net" half is not padding: four existing tests
  (`JunctionLinkLaneMapTests`, `JunctionIsLeaderTests`, `InternalJunctionFoeTests`, `InternalLinkFoeTests`)
  enumerate **every `*.net.xml` under `scenarios/` recursively** and assert structural invariants on all
  of them, so **Stage 0 is not gate-neutral** even though it writes no C#. (Risk is low, not zero:
  `scenarios/_ped/poc0-crossing-plaza` already puts crossing internal lanes `:c_c0_0 … :c_c3_0` in a
  junction's `intLanes` and those tests are green on it today — see design §3.1.)
- **S-e.** Commit before delegating; end a delegation at "compiles, verified, committed"; never delegate
  *waiting* for a long run (CLAUDE.md §Subagents).
- **S-f. ZERO HEAP ALLOCATION ON THE PERSON STEP PATH — mandatory, checked every stage.**
  "Step path" = `AdvancePersons` and everything it calls, after warm-up. Explicitly **not**: scenario
  load, person spawn (one-off), FCD/output writing (opt-in, off the hot path), and diagnostics behind a
  gate. Enforced by the design §4.3 gate 1 assertion (`Engine.ProfilePhases` → `PhaseBytes` reports
  **0 bytes/step**), re-run at every stage gate — not once at SP-3.0 and then forgotten. A stage that
  regresses it is not closed. The hazard is concrete: SUMO allocates ~6 `std::vector<Obstacle>` per
  pedestrian per step, which transliterated is ≈180 k allocations per simulated second on Tier C.
- **S-g. The default build is parity-exact.** Any optimisation that changes behaviour is a
  **performance deviation** and goes through design §4.4 — named `PD-n`, default OFF, ≥1.3× measured
  speedup, quantified behavioural delta on **both** surfaces, a visual A/B render, determinism
  preserved, and **owner sign-off recorded in the tracker**. Exhaust the exact optimisations first
  (§4.4.1 — the biggest wins available need no deviation at all). Widening a `tolerance.json` is never
  a deviation; it is `SUMOPED-PROCESS.md` §6.1, and the answer is no.

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

### SP-0.0b — Knob sensitivity sweep (DONE in the proposal session; re-run on the final scenario set)
`SUMOPED-ALGORITHM.md` §4. `scripts/sumoped-knob-sweep.py` exists and has been run on two bases.
**Success:** the sweep is re-run against the final committed `_sumoped` set with the RNG pinned
(`--pedestrian.striping.dawdling 0`, ped `speedDev="0"` — an unpinned baseline makes the whole table
noise, see §4 trap 1) and with `--lat-edge` set wherever a lateral knob is in scope (aggregate counters
are blind to stripe usage, trap 2). Every knob that comes back `NO CHANGE` on the whole set is either
given a witnessing scenario or listed in `SUMOPED-COVERAGE.md` §8 as an admitted hole with a reason.
Known-inert today and needing scenarios: `jamtime.narrow` (needs a 1-stripe lane),
`jmDriveAfterRedTime` (needs a TL), `legacy-departposlat`, `reserve-oncoming` (may be unreachable).

### SP-0.1 — Re-establish the oracle, and commit the recipe
Design §2. Clone `/sumo` at `v1_20_0`; `pip install eclipse-sumo==1.20.0`.
**Success:** `sumo --version` reports 1.20.0; `/sumo/src/microsim/transportables/MSPModel_Striping.cpp`
is 2725 lines; the recipe (including the "apt ships 1.18, do not use it" warning) is committed in
`scenarios/_sumoped/README.md`.

### SP-0.2 — Author the Tier A / Tier B scenarios
Requirements §6 + `SUMOPED-COVERAGE.md` §3–4. Each gets `nodes.nod.xml`/`edges.edg.xml` sources, a
`netconvert` regeneration line in `NOTES.md`, `net.net.xml`, `rou.rou.xml`, `config.sumocfg`.
Roughly 20 Tier A (1–4 peds, ≤80 steps, one mechanism each) and 8 Tier B (10–40 peds + vehicles,
120–200 steps). **All eight** axes of coverage §4 must each take every listed value somewhere in the set
— including the two found by rendering rather than by reading (**crossing priority** and **car
movement**), which is exactly why they are easy to skip —
in particular a **1-stripe** (`--default.crossing-width 0.64`) and a **12-stripe** (`8.00`) crossing,
a **pass-by** flow of peds who turn at the junction without crossing, **turning car flows** (left and
right — coverage §4.3; straight-through demand never exercises the exit-crossing yield), and
**counterflow on a single crossing**, which coverage §4.2 shows does NOT arise from the obvious
"peds both ways" demand and must be forced by routing across one arm in both directions with
depart/arrival positions pinned near the junction.
**Success:** each `config.sumocfg` explicitly sets `--pedestrian.model striping` **and**
`--pedestrian.striping.dawdling 0`; each ped vType sets `speedDev="0" speedFactor="1"`; no
`departPos="random"`/`departPosLat="random"` anywhere; each `NOTES.md` names the axis values it pins and
the SP-0.0 branch IDs it claims to fire. A test asserts the four pinning properties by parsing every
`_sumoped` config, so the pinning can never silently lapse. **And the full gate is re-run (S-d)** —
committing nets alone runs them through four repo-wide net-invariant tests before `Sim.Ingest` knows
anything about crossings.

### SP-0.2b — Author the Tier C saturated scenarios
`SUMOPED-COVERAGE.md` §3, §4.1. **Four** scenarios: a saturated multi-lane signalized junction (2-lane
arms, 4 car flows + 4 personFlows, 300 s — verified to reach steady state by t≈80 with ~110–140
concurrently walking), a **jam-regime** variant at `personFlow period="0.5"` that drives `jammed > 100`,
a **narrow-crossing** variant (1 stripe, `--default.crossing-width 0.64` — the only route to
`jamTimeNarrow`), and a **wide-crossing** variant (12 stripes, `8.00`). Width alone moves collisions
42 → 33 → 1, so these select different failure regimes rather than repeating one. Honest-SUMO flags:
`--time-to-teleport -1 --collision.action warn --collision.check-junctions true`.
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

### SP-0.3b — Render every golden, and look at it
Coverage §4.4. `scripts/render-ped-fcd.py` already exists and emits the real `Sim.Viz` payload schema
into the real `src/Sim.Viz/template.{html,js}`.
**Success:** every `_sumoped` scenario has a committed `replay.html` (an OUTPUT, regenerable — the same
status `scenarios/README.md` gives the existing `replay.html` files); each renders with **no JS errors**
(verify headlessly, not by assuming); the renders are listed in `scenarios/_sumoped/README.md` with what
each is meant to show. A scenario whose render does not visibly show the behaviour it claims goes back
to SP-0.2 — this is the cheapest possible check that a scenario is not vacuous.

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
lane of **every committed `*.net.xml` under `scenarios/`** (141 files today, not the 91 scenario
directories — the four repo-wide invariant tests use the file enumeration, and so must this); **and** the full gate is unmoved (S-d).

### SP-1.2 — Walkingarea foes for vehicle links
Design §3.1, §6.3.
**Success:** on `walkingarea-shared`, the set of `(vehicleLink → walkingareaEdge)` foe pairs matches what
SUMO computes, verified by a one-off dump comparison recorded in the scenario NOTES.

### SP-1.3 — ⚠ Resolve the begin-of-timestep ordering **by trace**
Design §5.1 and **§6.6.2**, which sharpens what this task must answer. `MSLink::opened` tests
`haveRed()` against the link's **CURRENT** state (`MSLink.cpp:754`) — the `t − DELTA_T` a pedestrian
passes is only the *arrival time* used in the foe-conflict comparison (`:770`), not a lagged read of
the light. So pedestrians see the current phase, and the placement of `AdvancePersons` relative to
SumoSharp's actuated-TLS advance (`Engine.cs:3234`) is **behaviourally load-bearing**.
The question is therefore not "what is the event order" but: **on a step where the light switches, does
a pedestrian see the new phase or the old one?**
**Success:** a committed trace from a real SUMO run on `xwalk-tls-release` showing, for one step across
a phase boundary, whether the ped saw the old or the new phase — plus a one-paragraph finding in the
design doc. **Reasoning from the event-queue source is explicitly not acceptable evidence** (CLAUDE.md
§Measurement discipline item 2).

### SP-1.4 — Static precompute
Design §3.2: `WalkingAreaPaths`, `WalkingAreaFoes`, `MinNextLengths`, `NumStripes`.
**Success:** on `walk-junction-turn`, every `(from,to)` path's length matches SUMO's own
`WalkingAreaPath.length` to 1e-9, dumped from a debug build or via the `<personinfo>` `routeLength`
cross-check; `NumStripes` matches `floor(width/0.64)` on every crossing/WA lane in every committed `_sumoped` net.

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

### SP-2.1d — ⭐ The single-step replay harness (L2)
`SUMOPED-PROCESS.md` §3. **This is what makes staged porting possible** — it gives a green signal on
the step function long before any scenario can run end-to-end, and it localises a divergence to one
pedestrian on one lane with one obstacle array instead of "diverged at step 340".
Reconstruct the full per-lane `PState` population at step `t` from the goldens (§3.1 recovery table),
run exactly ONE step, compare against `t+1`, then discard our state and re-seed from the golden.
**Success:** (a) the reconstruction self-checks pass — derived `pos` agrees with the FCD's own `pos`
to 1e-6, and the two independent `myAmJammed` recoveries (the stderr `is jammed` event and the exact
`vMax/4 = 0.3472` speed signature, measured at 1983 samples in the counterflow-jam golden) **agree with
each other**; (b) replaying a ped's whole trajectory re-seeded each step reproduces `myWaitingTime`
exactly; (c) the harness reports a **replayable step count** and it is written to the tracker — this is
the stage-gate metric (§5.1); (d) at this stage every step throws `NotPortedInThisStage`, so the count
is 0 and the harness fails honestly.

### SP-2.1e — Fail-loudly staging
`SUMOPED-PROCESS.md` §4. Every not-yet-ported branch throws
`NotPortedInThisStageException(branchId)` naming its `SUMOPED-BRANCH-INVENTORY.md` ID — never a
fallback, a default, or a `TODO` comment.
**Success:** a test enumerates the inventory and asserts every unimplemented ID has a throwing site;
running the `_sumoped` suite at any stage produces, for each scenario, either a result or an exception
naming the exact branch that put it out of scope — so "which scenarios are in scope at stage N" is
answered by running the suite, not by judgment. A `NotPortedInThisStage` surviving to SP-7.5 is a
release blocker.

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

### SP-3.0 — ⭐ The lane-bucketed SoA store and the scratch pools
Design §4.1–4.3. **Do this before any stepper code**, because it is the one decision that cannot be
retrofitted cheaply: persons are greenfield, so they are built at the data-oriented destination rather
than at the vehicle layer's current AoS position.
Lane-bucketed contiguous runs sorted by `dir·relX` with the ordinal id tie-break; hot/cold field split;
vType scalars denormalised into the hot arrays; per-worker pooled `Obstacle` scratch with a
`stackalloc` fast path under `MaxStripes` and a rented-buffer fallback above it; hot `Obstacle` struct
carrying `foeIndex`, not a description string.
**Success:** (a) a test asserts the hot arrays contain no managed references (reflection over the
store's field types) — that is what keeps it chunk-storable; (b) the per-lane run for any lane is
**contiguous** and sorted, asserted directly on the store; (c) `MaxStripes` is derived from the loaded
net (measured range across our scenarios: **1–12**) and exceeding it takes the rented path rather than
throwing; (d) **the allocation gate** — with `Engine.ProfilePhases` on, the person phase reports
**0 bytes** allocated per step after warm-up on the Tier C scenario. Gate (d) is the one that matters:
a transliterated port allocates ~6 `Obstacle` arrays per ped per step, ≈180 k allocations per simulated
second on Tier C, and nothing else in the plan would notice.

### SP-3.1 — `PersonRuntime`, `StripingParams`, stripe math
Design §4.1, §5; rationale and per-constant sensitivity in `SUMOPED-ALGORITHM.md` §4.
Field set fixed by SUMO's `saveState` enumeration.
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
**Success:** `walk-straight-1`, `walk-oncoming-2` and **both sidewalk-counterflow scenarios**
(`counterflow-sidewalk-4m` at 75 peds each way on 6 stripes, `counterflow-sidewalk-6m` at 214 concurrent
on 9) reach **exact parity** (SP-2.2's tests flip to green with no tolerance widening). The counterflow
pair is what proves the oncoming-reserve and `ONCOMING_CONFLICT_PENALTY` folds rather than just the
free-walk path: the oracle self-organises into two stable lanes (measured at y=-6.72 / y=-3.52, held for
the whole run) and our output must reproduce that lane split, not merely the per-ped positions. The
x-position sort's id tie-break is present and covered by a test with two peds at identical `RelX`.

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
**Success:** `xwalk-priority-queue`, `xwalk-priority-horde`, `walk-passby-queue`,
**`counterflow-crossing`**, **`counterflow-crossing-jam`**, **`ped-turners-through-bunch`** and
**`ped-turners-gridlock`** at exact parity, and the **same** derived assertions SP-0.4 ran against the
oracle now pass against SumoSharp's output — same helper, same thresholds. Distinct-stripe counts must
match the golden exactly, not merely clear the ≥3/≥4 bar.
Three of those seven are here because nothing else reaches their branches: crossing counterflow does
**not** arise from "peds both ways" demand (coverage §4.2 — every crossing runs one-way; it must be
forced), its jam variant is the only head-on-deadlock-plus-squeeze case, and the ped-turner pair is the
owner's R3d at both ends of its range — 29% stopped at moderate density, degrading **continuously** to
76% and corner gridlock at 2.4× car flow. ⚠ Assert the turner metric **unconditioned**: conditioning
"turner stopped %" on steps that already have ≥3 stopped peds reports 79–95% and reads as total failure
because the condition selects the congested moments (tracker §Render session).

---

## Stage 5 — Vehicle coupling

### SP-5.1 — `BlockedAtDist` + phantom-leader injection
Design §6.1, §6.2, risks #5 and #6. Inject a null-vehicle `JunctionLeaderCandidate` into the existing
junction-leader path. Gated on `Persons != null`.
**Success:** `xwalk-priority-1v1` at exact parity **including the `13.89 → 11.11 → 6.61` profile**; a
dedicated test asserts the 2 s standing-ped clause by holding a ped stationary at the curb and showing
the vehicle proceeds after — not before — 2 s; and the full gate is unmoved (S-d), re-run in this task,
not deferred to Stage 7.

### SP-5.1b — ⭐ R4b: the ped-priority zebra (the opposite yield regime)
Requirement **R4b**, coverage §4.5. ⚠ This is the regime `--crossings.guess` **never** produces:
`NBNode.cpp:2788` creates a guessed crossing with `priority = isTLControlled()`, so at an uncontrolled
node it is always `priority="false"` (the *pedestrian* gives way). Declaring
`<crossing … priority="true"/>` flips the link from state `m` to `M` and inverts who yields. Without
this task the port would be validated on only half of R4.
**Success:** the A/B pair `zebra-1v1-yields` / `xwalk-1v1-noprio` — identical except for that one
attribute — both at exact parity. The `priority="true"` arm must reproduce the measured trace: the car
decelerates `13.89 → … → 2.15 → 0.00`, holds a **full stop for 3 s** on the internal lane, and the ped
crosses at an unbroken `1.39 m/s` without ever stopping at the curb. The `priority="false"` arm must
reproduce the ped stopping dead at the curb while the car dips to 6.28 and proceeds. Plus the flow-density
pair at exact parity: `zebra-flow-balanced` (peds stopped on the curb **0%** of walkingarea steps, ≥60
distinct vehicles reaching a full stop) and `zebra-flow-pedheavy` (cars starved — 45 of 69 still queued).
The 0%-vs-91% curb-stopping split is asserted by the **same helper** on both arms, so the two regimes
cannot both pass by accident.

### SP-5.2 — `AddCrossingVehs` + `AddVehicleFoe`
Design §6.4.
**Success:** `xwalk-priority-queue` (vehicles enabled) **and `turning-vs-crossing-peds`** at exact
parity; a test asserts the "fully blocked ⇒ pin every stripe" second pass by constructing a crossing
where every non-reserved stripe is vehicle-occupied and showing no ped enters.
`turning-vs-crossing-peds` is not optional coverage: a turning car yields to peds on the crossing over
its **exit** edge and is held *on the internal lane inside the junction*, not at the stop line —
straight-through demand never fires that path at all (coverage §4.3, measured: 80 vehicle-steps of
blocked RIGHT turns, 57 of blocked LEFT). Assert both turn directions.

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

### SP-5.6 — ⚠ The cross-population data-flow contract, asserted
Design §6.6. The coupling is correct only because of *ordering*, and ordering that is merely "how the
calls happen to be sequenced today" breaks silently under a later refactor — with no test failing,
because everything still runs, just one step stale.
**Success, four assertions, each failing loudly if the ordering is disturbed:**
(a) **`AdvancePersons` runs before `neighbors.Refill`** (`Engine.cs:3241`) — so if persons are folded
into the frozen start-of-step snapshot they are folded *after* moving. Asserted by a phase-order test,
not by reading the call sequence.
(b) **Peds read the PREVIOUS step's junction approach index.** `BuildFoeApproachIndex`
(`Engine.cs:3273`) is rebuilt after the ped slot; SUMO's equivalent (`setJunctionApproaches`,
`MSNet.cpp:787`) runs after `planMovements`, so the index a ped reads at *t* was built at the end of
*t−1*. Either the prior index survives until the ped pass completes or the rebuild moves — and a test
asserts which, because reading the freshly-built one silently gives peds information SUMO's peds do not
have.
(c) **Persons are immutable for the whole vehicle phase** — a debug-gated write barrier asserts no
person state is written between `AdvancePersons` returning and the end of `ExecuteMoves`.
(d) **The ped→vehicle query is allocation-free and side-effect-free** — it runs inside a possibly
parallel `PlanMovements` (`UseParallelPlan`), so a lazily populated cache there is a data race that
surfaces only as nondeterminism under load. Covered by S-f's 0-bytes gate plus the par == single hash.

## Stage 6 — Traffic lights

### SP-6.1 — Crossing link state, `IgnoreRed`, `GetImpatience`
Design §7, and whatever SP-1.3's trace determined.
**Success:** `xwalk-tls-release` at exact parity across at least one full red→green→red cycle, with the
ped's release step matching to the tick, **and** — R6's second half, currently asserted nowhere — at
least one vehicle held to a stop by a ped's green phase, matching the golden's stop step and its release
step. If the scenario as authored contains no vehicle demand conflicting with the ped phase, it is
mis-authored under SP-0.4's own rule and goes back to SP-0.2.

---

## Stage 7 — API, visualization, production regime, final gate

### SP-7.1 — Public API
Design §10 (readiness assessment + the exact 8-item delta), Requirement R7.
Add `PersonHandle`, `PersonState`, `PersonEvent`, `PersonReadBuffer` + `Engine` person spans,
`SimulationSnapshot` person columns, `SimulationRunner.TryInterpolatePerson`, the spawn/despawn/lifecycle
methods, and the replication decision from §10.3. Persons go on the concrete `Engine`, not `IEngine`
(matching where the vehicle API actually lives).
**Success:** a sample in `docs/TUTORIAL-SUMO-PEDESTRIANS.md` compiles as part of `Traffic.sln` and drives
`xwalk-priority-1v1` end-to-end through public API only — verified by the sample project having no
`InternalsVisibleTo`. `docs/SUMOSHARP-API.md` gains a person section in the same style as §5/§9/§10.
**And three invariants that must hold by construction, each with its own assertion:**
(a) **no existing vehicle type is edited** — `VehicleHandle`, `VehicleState`, `SimEvent` and the vehicle
columns of `SimulationSnapshot` are byte-identical in the diff, so the vehicle gate cannot move;
(b) `SimulationSnapshot.Count` still means **vehicle** count (a test asserts it against
`engine.VehicleCount` with persons present) — `PersonCount` is the new field, `Count` is never repurposed;
(c) `PersonHandle` and `VehicleHandle` are **not interchangeable** — a test asserts a `PersonHandle` with
the same `(Index, Generation)` as a live `VehicleHandle` resolves to a different entity.

### SP-7.1b — Person dead-reckoning: REUSE the vehicle path, and prove it
Design §10.5 (measured; supersedes an earlier draft of §10.2 item 6 that claimed the opposite).
Persons are tagged `DrModel.FreeKinematic`; `DrModel` gains **no new member**; the interpolation code
path is shared with vehicles, parameterised by the DR model — not a bespoke `TryInterpolatePerson`
extrapolator.
**Success:** a test over the `_sumoped` goldens asserts mid-step chord-interpolation error for persons
is **no worse than** the same metric for vehicles on the same scenario, reproducing the measured
envelope (ped walkingarea p95 ≤ 0.20 m, ped normal edge p95 ≤ 0.32 m, versus vehicle p95 ≈ 0.39 m). If
a future change makes persons need a bespoke extrapolator, this test is what will say so — with a
number, not an argument.

### SP-7.2 — Coordinate contract (the Phase 2 hinge)
Design §9.
**Success:** `WorldOf(edge,pos,posLat)` round-trips through `TryResolveToEdge` to within 1e-6 for 1000
sampled points across every `_sumoped` net; `SpawnPersonAt` mid-edge produces a ped whose next 10 steps
match a ped that walked there naturally (the no-pop handover property).

### SP-7.3 — Sim.Viz scenes + parity overlay
Design §11, Requirement R8.
**Success:** `--sumoped-<scene>` renders for every committed `_sumoped` scenario; the overlay mode draws golden
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
IDs (out of 148) is recorded in the tracker. `PerScenarioClaimTest` green.

### SP-7.4d — Performance-deviation ledger close-out
Design §4.4. Only if any `PD-n` was taken.
**Success:** every `PD-n` in the tracker ledger has all seven gate items filled — default-OFF test,
measured speedup ≥1.3×, quantified behavioural delta (position RMS/max, collisions not increased, R3
assertions still holding, `<personinfo>` aggregate + KS, jam count), both surfaces measured, a
committed visual A/B render, determinism preserved, and owner sign-off. The **stack** of all enabled
deviations is measured too, not only each in isolation (§4.4.3). If no deviation was taken, the ledger
says so explicitly — an empty table with no statement is indistinguishable from an unfilled one.

### SP-7.4c — Person-scale benchmark
Design §4.3 gate 3. `Sim.Bench`'s determinism hash covers **vehicles only** and cannot see persons.
**Success:** a `Sim.BenchPed` reports person-steps/second on the Tier C saturated scenario and its
number is committed to the tracker; the person trajectory hash is stable across two single-threaded
runs; the zero-allocation gate from SP-3.0(d) still holds at the end of the port, not just when it
was introduced.

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
**Success:** `docs/SUMOSHARP-API.md` §12b flips from **PROPOSED** to **STATUS: landed** and its
D19–D27 entries lose the PROPOSED banner; `docs/PEDESTRIAN-OVERVIEW.md` §3's "we do NOT port MSPModel_Striping" is rewritten to
describe two coexisting tiers; `docs/PEDESTRIANS.md` gains a pointer to the new subsystem;
`docs/README.md` indexes the SUMOPED doc set; `scenarios/README.md` documents `_sumoped` as a
**golden-bearing** group (unlike `_ped`); `docs/TASKS-TODO.md` gains the new test counts.

---

## Suggested batching for the Opus→Sonnet loop

CLAUDE.md §Subagents' orchestration loop. Batches are sized so each ends at a verifiable gate:

| Batch | Tasks | Ends at |
| --- | --- | --- |
| B0 | SP-0.0 | branch inventory reviewed and corrected (a first pass of 148 rows exists) |
| B1 | SP-0.1 … SP-0.6 (incl. SP-0.3b renders) | Tier A/B/C goldens committed; oracle proven to contain the R3 behaviours; coverage matrix mapped and holes signed off |
| B2 | SP-1.1, SP-1.2, SP-1.4 | net model extended, vehicle gate unmoved |
| B3 | SP-1.3 | the ordering trace (Opus does this one — it is a judgment call) |
| B4 | SP-2.1 … SP-2.4 | all seven comparators + the stripe helper + coverage counters; every test failing honestly |
| B5 | SP-3.0 … SP-3.3 | store + scratch pools with the allocation gate green; `Walk()` unit-proven in isolation |
| B6 | SP-3.4, SP-3.5 | two scenarios at exact parity |
| B7 | SP-4.1 … SP-4.4 | junctions + the owner's crowd behaviours at parity |
| B8 | SP-5.1 … SP-5.5 | vehicle coupling; gate re-run inside the batch |
| B9 | SP-6.1 | TL crossings |
| B10 | SP-7.1 … SP-7.6 (incl. SP-7.4b) | API, viz, production regime, final gate, docs |
