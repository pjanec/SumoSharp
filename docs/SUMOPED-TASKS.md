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

## Stage 0 — Oracle and fixtures (no C# yet)

### SP-0.1 — Re-establish the oracle, and commit the recipe
Design §2. Clone `/sumo` at `v1_20_0`; `pip install eclipse-sumo==1.20.0`.
**Success:** `sumo --version` reports 1.20.0; `/sumo/src/microsim/transportables/MSPModel_Striping.cpp`
is 2725 lines; the recipe (including the "apt ships 1.18, do not use it" warning) is committed in
`scenarios/_sumoped/README.md`.

### SP-0.2 — Author the eight Phase-1 scenarios
Requirements §6. Each gets `nodes.nod.xml`/`edges.edg.xml` sources, a `netconvert` regeneration line in
its `NOTES.md`, `net.net.xml`, `rou.rou.xml`, `config.sumocfg`.
**Success:** each `config.sumocfg` explicitly sets `--pedestrian.model striping` **and**
`--pedestrian.striping.dawdling 0`, and each ped vType sets `speedDev="0" speedFactor="1"`; no
`departPos="random"` or `departPosLat="random"` anywhere; a test asserts these four properties by
parsing every `_sumoped` config, so the pinning can never silently lapse.

### SP-0.3 — Generate and commit the goldens
Design §2.3, §8.2. Extend `scripts/regen-goldens.sh` to cover `_sumoped` with tripinfo.
**Success:** every `_sumoped` scenario has `golden.fcd.xml` containing `<person>` rows,
`golden.tripinfo.xml` containing `<personinfo>`, `tolerance.json`, and `provenance.txt` recording
`sumo_version=1.20.0` + input sha256s. Re-running the script twice produces byte-identical goldens
(the determinism proof for the oracle itself).

### SP-0.4 — Assert the R3 behaviours **on the oracle**
Requirements R3, design §8.1. Before requiring anything of SumoSharp, prove the goldens contain it.
**Success:** a test reads the committed goldens and asserts: `xwalk-priority-queue` has ≥4 peds
simultaneously stopped on one walkingarea on ≥4 distinct stripes; `xwalk-priority-horde` has ≥3 peds
entering the crossing within 2 s on ≥3 distinct stripes; `walk-passby-queue` has a ped with `speed > 0`
every step while ≥3 others are stopped beside it; `xwalk-priority-1v1` reproduces the deceleration
`13.89 → 11.11 → 6.61`. **A scenario that fails this is mis-authored and goes back to SP-0.2.**

### SP-0.5 — Zero-overlap invariant helper
Requirement R5.
**Success:** a shared helper computes world-space vehicle-body-to-ped clearance; run over every
`_sumoped` golden it reports `> 0` at every step. If SUMO's own golden shows an overlap, that is
recorded as a known-oracle-artefact in the scenario NOTES (CLAUDE.md item 11: SUMO's defaults include
cheating), not silently tolerated.

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
**Success:** parsing a committed `golden.fcd.xml` yields the exact person row count found by
`grep -c "<person"`; comparing a golden against **itself** reports zero mismatches; comparing it against
a golden with one row perturbed by `2 × tolerance` reports exactly one attribute failure naming the right
person, time, and attribute. `ToleranceConfig` throws for an unconfigured compared person attribute.

### SP-2.2 — Eight failing parity tests
One test class per scenario, following the `Rung1ParityTests.cs` pattern.
**Success:** all eight compile and **fail** with a clear "no persons produced" diagnostic. A test that
passes at this stage is vacuous and must be fixed before Stage 3 starts.

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

### SP-5.5 — Zero-overlap invariant, on SumoSharp
Requirement R5.
**Success:** SP-0.5's helper, run over SumoSharp's own output for all eight scenarios, reports clearance
`> 0` at every step; plus the saturated non-golden scenario reports the same. Report the **minimum**
clearance observed, per scenario — a bare "no overlap" is not a measurement.

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
| B1 | SP-0.1 … SP-0.5 | goldens committed, oracle proven to contain the R3 behaviours |
| B2 | SP-1.1, SP-1.2, SP-1.4 | net model extended, vehicle gate unmoved |
| B3 | SP-1.3 | the ordering trace (Opus does this one — it is a judgment call) |
| B4 | SP-2.1 … SP-2.3 | harness in place, eight tests failing honestly |
| B5 | SP-3.1 … SP-3.3 | `Walk()` unit-proven in isolation |
| B6 | SP-3.4, SP-3.5 | two scenarios at exact parity |
| B7 | SP-4.1 … SP-4.4 | junctions + the owner's crowd behaviours at parity |
| B8 | SP-5.1 … SP-5.5 | vehicle coupling; gate re-run inside the batch |
| B9 | SP-6.1 | TL crossings |
| B10 | SP-7.1 … SP-7.6 | API, viz, production regime, final gate, docs |
