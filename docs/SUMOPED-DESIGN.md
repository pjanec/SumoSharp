# SUMOPED — Design (the HOW)

**Status: PROPOSAL — awaiting owner sign-off. No implementation has started.**

The WHAT is `SUMOPED-REQUIREMENTS.md`; this document does not restate it. **How to build it in stages
without losing faithfulness: `SUMOPED-PROCESS.md`.** **What the model does and
what each knob is worth: `SUMOPED-ALGORITHM.md`** (read it before porting anything). Coverage plan:
`SUMOPED-COVERAGE.md` (+ `SUMOPED-BRANCH-INVENTORY.md`). Task breakdown: `SUMOPED-TASKS.md`.
Checklist: `SUMOPED-TRACKER.md`.

This is the mechanism, the data structures, the exact seams into the existing engine, and the
determinism/parity argument for porting SUMO 1.20.0's `MSPModel_Striping` into SumoSharp.

---

## 1. The one idea that makes the port tractable

**SUMO's pedestrian model is not a crowd simulator. It is a lane model.**

A pedestrian's authoritative state is `(lane, myRelX, myRelY, myDir)` — a longitudinal offset along a
lane plus a lateral offset measured from the lane's left edge, discretized into **stripes** of
`stripeWidth = 0.64 m`. World `x/y` is derived only for output. Every interaction — other peds,
vehicles, the lane end, a closed link — is folded into a **per-stripe `Obstacle` array**, and each step
is a single utility argmax over stripes.

This is the same shape as SumoSharp's vehicle engine (lane + `pos` + `posLat`, constraint fold, argmin
speed), and structurally *unlike* `src/Sim.Pedestrians`' continuous-2-D ORCA crowd. That is why the port
is a new subsystem beside the ORCA layer rather than an extension of it, and it is also why the port is
feasible at all: the hard part of a crowd model (continuous collision resolution) does not exist here.

Two consequences that shape everything below:

1. **Persons need the lane/edge network the vehicle engine already has**, extended with ped elements —
   not a navmesh. (§3)
2. **The crowd behaviours the owner cares about are deterministic consequences of the stripe utility
   fold.** Accumulation, abreast crossing and pass-by avoidance (Requirements R3a/R3b/R3d) come out of
   `walk()`'s ordered penalty sequence with no RNG in the path. Getting §5.3's fold order exactly right
   *is* getting the look right; there is no separate "make it look good" layer to tune.

---

## 2. The oracle, and how to use it

SUMO 1.20.0 is available in this environment and the source is checked out. Both are **ephemeral** and
must be re-established per VM:

```bash
# Source (read-only reference). CLAUDE.md expects it at /sumo.
git clone --depth 1 --branch v1_20_0 https://github.com/eclipse-sumo/sumo.git /home/user/sumo-src
ln -sfn /home/user/sumo-src /sumo

# Binary. NOTE: Ubuntu's apt ships 1.18 -- the WRONG version. Use pip.
python3 -m pip install eclipse-sumo==1.20.0     # -> /usr/local/bin/sumo
sumo --version                                   # must report 1.20.0
```

The offline test loop (`dotnet test`) must never invoke either. SUMO is for **regenerating goldens** and
for **direct engine-vs-SUMO diagnosis** — the trace-first discipline CLAUDE.md §Measurement discipline
item 2 mandates after five reasoned-from-source hypotheses in a row turned out inert.

### 2.1 The reference fixture, measured first-hand

```bash
netconvert --node-files=nodes.nod.xml --edge-files=edges.edg.xml \
           --sidewalks.guess --crossings.guess --no-turnarounds --output-file=net.net.xml
sumo -n net.net.xml -r rou.rou.xml --begin 0 --end 40 --step-length 1 \
     --fcd-output fcd.xml --precision 4 --no-step-log true \
     --pedestrian.model striping --pedestrian.striping.dawdling 0
```
4-arm priority junction, 1 lane/arm; one car `wc→ce` at `departSpeed="max"`; one ped
`<walk from="cn" to="cs"/>`; ped vType `speedDev="0" speedFactor="1"`.

```
 t   car                        ped
 3   wc_1 pos 43.99 spd 11.11   :c_w1 pos 0.08 spd 1.39   car brakes for the ped nearing the curb
 4   wc_1 pos 50.59 spd  6.61   :c_w1 pos 0.00 spd 0.07   ped stops on the walkingarea
 5   :c_10_0      spd  9.21     :c_w1 pos 0.00 spd 0.00   ped waits, car crosses
 7   ce_1 pos 18.30 spd 13.89   :c_c1 pos 5.01 spd 1.39   ped enters the crossing once clear
```

### 2.2 Determinism: what must be pinned

Exactly two RNG sites exist in the striping model, and only one is on the default path:

| site | when | pinned by |
| --- | --- | --- |
| `MSPModel_Striping.cpp:2179` — `dawdle = MIN2(xSpeed, RandHelper::rand() * vMax * dawdling)` | **every step, every moving ped** | `--pedestrian.striping.dawdling 0` (default **0.2**) |
| `MSPModel_Striping.cpp:1661` — random `departPosLat` | only if `departPosLat="random"` | don't use that value |

Plus `MSStageTrip.cpp:151` for `departPos="random"`, and the vType `speedDev` draw for `speedFactor`.

Measured: with `dawdling=0` + `speedDev="0" speedFactor="1"`, a walking ped holds **1.388889 m/s exactly**
every step; with SUMO's defaults it jitters 1.12–1.38. `dawdling` is the pedestrian analogue of the
vehicle `sigma`, and per CLAUDE.md it goes in each scenario's `config.sumocfg`, never in a script flag.

### 2.3 Golden format

⚠ **Superseded in part by `SUMOPED-COVERAGE.md` §2**, which enumerates **seven** person-bearing SUMO
outputs measured first-hand. The summary below is the FCD half; read coverage §2 for the rest
(`--person-summary-output`'s per-step `jammed` column and `--collision-output` in particular).

- **Primary — person FCD.** `<person id x y angle speed pos edge slope/>` nested under `<timestep>`.
  Note: `edge` (not `lane`), and it carries **internal ids** (`:c_w1`, `:c_c1`) while on a walkingarea or
  crossing — which is precisely what makes "the ped waited on the curb" a *checkable* golden fact.
  There is **no `type=`** attribute and no lateral attribute; the schema is sparser than the vehicle row.
- **Secondary — `<personinfo>` tripinfo.** `duration/waitingTime/timeLoss/routeLength/maxSpeed`, an
  aggregate tripwire. `regen-goldens.sh` already supports this via its `.wants-tripinfo` sentinel.
- **No `golden.state.xml` for persons.** Verified: `--save-state` writes zero person/transportable
  elements.
- ⚠ **But the vType init cross-check IS available, and an earlier draft wrongly gave it up.** The
  mechanism this repo actually uses is `golden.vtype.json`, dumped via libsumo/TraCI by
  `scripts/dump-scenario-vtypes.py` and consumed by `ParameterCrossCheckTests` — not `--save-state`. And
  **SUMO persons reuse `MSVehicleType`** (§10.5), so a person's resolved type dumps the same way. This
  matters because `Sim.Ingest`'s `VTypeDefaults.Resolve` **throws today** on `vClass="pedestrian"`
  (`VTypeDefaults.cs:240-243` — there is no such row), and because §4.1(c) denormalises
  `width`/`length`/`minGap`/`vMax` into the hot arrays, where a wrong default shifts every trajectory
  with no obvious cause. Task **SP-1.0** ports the `SVC_PEDESTRIAN` defaults and commits the per-scenario
  dumps, so a ped divergence can be triaged at the init rung — CLAUDE.md §Reporting a parity failure —
  rather than one rung higher.

---

## 3. Network model — extend `Sim.Ingest`, do not add a fourth reader

Three independent `net.xml` readers exist today: `Sim.Ingest/NetworkParser.cs` (the core, parity-gating
one), `Sim.Pedestrians/PedNetworkParser.cs`, and `Sim.Pedestrians/Crossing/CrossingTlReader.cs`. The core
one **never reads `<crossing>` at all** and collapses `allow`/`disallow` to a single boolean,
`LaneAllowsRoadVehicle` (`NetworkParser.cs:955`).

**Decision: extend the core reader; leave the other two untouched.**

Rationale: the vehicle↔ped coupling requires *shared identity* between a crossing edge and the vehicle
link that must yield to it (§6). Two independent net models cannot express that without a fragile id
join across parsers. Merging all three is explicitly out of scope (Requirement R-N4) — it would risk 324
green ORCA tests for no parity gain.

### 3.1 Additions to `NetworkModel`

All **purely additive**. `AllowsRoadVehicle` keeps its exact current semantics and derivation — a new
`Permissions` field is added *alongside* it, not in place of it, so no existing read path changes.

| addition | source | consumer |
| --- | --- | --- |
| `Lane.Permissions` (vClass bitmask) | `allow`/`disallow` | `GetSidewalk(edge, vClass)` |
| `Edge.Function` (`Normal / Internal / Crossing / WalkingArea`) | `<edge function=>` | every branch in `GetNextLane` |
| `Edge.CrossingEdges` (the crossed edge ids) | `<edge crossingEdges=>` | crossing↔link association |
| walkingarea + crossing lane `shape`, `width`, `length` | `<lane>` | `WalkingAreaPath`, stripe count |
| ped `<connection>` chain (sidewalk→WA→crossing→WA→sidewalk) | `<connection>` | `GetLinkTo`, `LogicalPredecessor` |
| `Link.WalkingAreaFoe` / `WalkingAreaFoeExit` | vehicle-link shape ∩ walkingarea polygon | `CheckWalkingAreaFoe` (§6.3) |
| crossing TL link index | `<tlLogic>` + `<connection linkIndex=>` | crossing link state (§7) |

**The core parser already survives crossing-bearing nets — checked, and it is a stronger claim than
the inertness argument below.** Four committed nets carry walkingareas and `function="crossing"` edges
(`scenarios/_ped/poc0-crossing-plaza`, `scenarios/_ped/evac-district`, `scenarios/_ped/georef_min`,
`scenarios/_bench/livecity-mega`), and `poc0-crossing-plaza`'s junction `c` lists crossing internal lanes
`:c_c0_0 … :c_c3_0` directly in its `intLanes`. Four repo-wide tests parse **every** `*.net.xml` under
`scenarios/` and assert that every `intLanes` entry resolves in `LinkIndexByInternalLane` and every
internal-link foe resolves to a real lane handle — and they are green on those nets today. So
`poc0-crossing-plaza` is the existing regression fixture for SP-1.1's additive parse, and adding
`_sumoped` nets is a known-survivable operation rather than an untested one. (It is still not free: see
standing rule S-d, which is why Stage 0 re-runs the gate.)

**Inertness argument.** New fields on existing records are invisible to the vehicle engine unless read.
The one genuine risk is `Permissions` accidentally changing lane-eligibility decisions — mitigated by
keeping `AllowsRoadVehicle` byte-identical in derivation and adding a test that asserts
`LaneAllowsRoadVehicle(allow) == Permissions.AllowsAnyRoadVehicle()` over every lane of every committed
scenario, so the two can never silently diverge.

### 3.2 Static precompute (once, at load)

Mirrors `MSPModel_Striping`'s own static state (`.cpp:349-460`):

- `WalkingAreaPaths` — one path per ordered `(fromLane, toLane)` pair incident to a walkingarea; shape =
  straight line between the two lanes' near endpoints, extrapolated, then Bezier-smoothed at
  `walkingarea-detail` (default 4). **The geometry-heaviest single item in the port** (survey difficulty
  rank 10) and the one most likely to need its own trace-driven debugging.
- `WalkingAreaFoes` — walkingarea edge → vehicle lanes crossing it.
- `MinNextLengths` — shortest path across each walkingarea, so a ped cannot overshoot the next lane.
- `NumStripes(lane)` = `max(1, floor(width / stripeWidth))`.

---

## 4. Where the code lives

```
src/Sim.Persons/                      NEW project. The SUMO-exact person model.
  PersonRuntime.cs                    <- PState (MSPModel_Striping.h:281)
  StripingModel.cs                    <- MSPModel_Striping: ActiveLanes, MoveInDirection, blockedAtDist
  StripingWalk.cs                     <- PState::walk -- the utility fold, isolated for unit testing
  Obstacle.cs, ObstacleType.cs        <- the per-stripe obstacle array
  ObstacleSources.cs                  <- GetNeighboringObstacles / GetVehicleObstacles /
                                         GetNextLaneObstacles / AddCrossingVehs / AddVehicleFoe
  WalkingAreaPath.cs, StripeMath.cs   <- geometry + stripe()/otherStripe()/getStripeOffset
  JunctionPedRouter.cs                <- the junction-local Dijkstra (§5.5)
  PersonStage.cs, PersonControl.cs    <- MSStageWalking / MSTransportableControl (walking only)
  PersonDemandParser.cs               <- <person>/<walk> demand
  StripingParams.cs                   <- the constants table, one place, all named as in SUMO

src/Sim.Core/
  IPersonModel.cs                     NEW. The seam. Engine holds `IPersonModel? Persons` = null.
  Engine.cs                           EDIT, narrowly: one call in AdvanceOneStep + one leader hook.

src/Sim.Harness/
  PersonFcdParser.cs, PersonTrajectory*.cs, PersonTrajectoryComparator.cs   NEW (§8)
```

`Sim.Persons` references `Sim.Core` and `Sim.Ingest`. `Sim.Core` never references `Sim.Persons` — it only
holds a nullable `IPersonModel`. No cycle.

**Why a new project and not `Sim.Pedestrians`, and not inside `Engine.cs`:** `Engine.cs` is already
17,010 lines; a separate assembly makes "inert when absent" structural rather than a discipline. And the
name must not suggest continuity with the ORCA layer — the two are different models of different things.

### 4.1 Storage — lane-bucketed struct-of-arrays

⚠ **Corrected.** An earlier draft of this section argued persons should mirror what vehicles *are
today* (`List<VehicleRuntime>` of a mutable class, `Engine.cs:23`) on the grounds that "persons should
not be more ECS than vehicles are". That reasoning is wrong. Vehicles are array-of-structs-of-references
because of **migration cost** — `VehicleRuntime`'s own header describes the in-progress trim toward
"chunk-storable" (unmanaged scalars, variable-length state moved to entity-keyed side storage).
Persons are **greenfield**: there is no migration cost, so building them AoS now is a self-inflicted
future migration and forfeits the performance the layout exists to buy. Design at the destination.

**The layout is derived from the algorithm's access pattern, not from a general preference for SoA.**
The hot loop (§5.2) is: *per lane, per direction, walk the pedestrians in `dir·relX` order, threading a
rolling `Obstacle[numStripes]` array, calling `Walk()` on each.* Four consequences:

**(a) Bucket by lane, contiguously.** Persons live in per-lane contiguous runs, kept sorted by
`dir·myRelX` with the ordinal person-id tie-break (§5.2). `MoveInDirectionOnLane` then becomes a linear
scan over contiguous memory instead of a pointer chase. This mirrors SUMO's own `myActiveLanes`
grouping — a case where *following the source's data structure* and *being cache-friendly* coincide,
which is not always true and is worth taking when it is. Note this is deliberately **not** the vehicle
layout (a flat list indexed by entity); the access patterns genuinely differ.

**(b) Hot/cold split.** `Walk()` touches only:

```
HOT  (every ped, every step)   relX, relY, speed, speedLat, dir, waitingTime,
                               amJammed, waitingToEnter
     + denormalised vType      width, length, minGap, vMax
COLD (lane transition/output)  person id string, route slice, stage, NLI,
                               walkingAreaPath reference
```

Hot fields are parallel arrays. Cold state goes in entity-keyed side tables — exactly the pattern
`VehicleRuntime`'s header already describes for lane sequences and stops.

**(c) Denormalise the vType scalars into the hot arrays.** `Walk()` reads `width`/`length`/`minGap`/
`vMax` per pedestrian per step. Chasing a `PersonType` reference for them is a dependent load in the
innermost loop. Vehicles still carry `VType` as a managed ref (a known, documented debt); persons
should not inherit it.

**(d) The `Obstacle[]` scratch is the real allocation hazard — and it is not small.** SUMO allocates
`std::vector<Obstacle>` freely: per pedestrian per step it builds `currentObs`, neighbouring obstacles,
next-lane obstacles, vehicle obstacles and crossing-vehicle obstacles, then merges them. A transliterated
port allocates roughly **six arrays per pedestrian per step** — on the Tier C golden that is ~180 k
allocations per simulated second. Unacceptable, and invisible until it is profiled.

The port instead uses **per-worker pooled scratch reused across pedestrians**. `numStripes` is bounded
by lane width (measured across our scenarios: **1 to 12**, with the netconvert default of 4.00 m giving
6), so a fixed `MaxStripes` scratch is `stackalloc`-able, with a pooled rented buffer as the fallback
for an unusually wide lane rather than a hard failure.

**(e) Churn is a hot-path operation, not a one-off — the hybrid makes spawn/despawn per-step.** Under
the Phase-2 hybrid (§9) a pedestrian is *adopted* into this store when it nears a crossing and *released*
when it leaves, so insertion and removal happen every step at every crossing boundary rather than once at
scenario load. The layout above is a **sorted contiguous per-lane run**, so an insertion is an O(bucket)
memmove — small buckets make that fine, but it must be *measured*, not assumed, and it must not allocate.

This repo has already paid for getting it wrong once: `PedLodManager`'s header records that the POC
version rebuilt the whole high-power crowd on every membership change, an O(current-high-power-count)
cost per switch, **measured at 100k as the dominant reason a churning world cost 3.6× a stable one**.
P0-1…P0-3 exist solely to make `OrcaCrowd`'s add/remove O(1). The person store must not reintroduce the
same cost in a new place. Standing rule S-f therefore does **not** exempt adoption/release from the
zero-allocation rule, and SP-3.0 carries an explicit churn condition.

`Obstacle` itself gets the same hot/cold treatment. SUMO's carries `{xFwd, xBack, speed, type,
description (string), vehicle (pointer)}`; the `description` is debug-only, and a string per stripe per
pedestrian per step would both allocate and destroy locality. The hot struct is
`{double xFwd, xBack, speed; byte type; int foeIndex}` — the id resolves through `foeIndex` only when
diagnostics are enabled.

### 4.2 Parallelism — what is actually available, and what is not

The parallelism story must be derived from SUMO's dependencies, not assumed. Checked first-hand:

| level | parallelisable? | why |
| --- | --- | --- |
| **within a lane** | **No — strictly sequential** | The rolling `obs` fold is a genuine loop-carried dependency: pedestrian *i*'s post-move position becomes an obstacle for pedestrian *i+1* in the same pass (§5.2). This is behaviour, not implementation detail. |
| **across directions** | **No — strictly sequential** | FORWARD over all lanes, then BACKWARD over all lanes. A FORWARD pedestrian's post-move position is visible to BACKWARD pedestrians in the same step but not the reverse; the asymmetry is observable. |
| **across lanes** | **Not freely** | ⚠ `getNextLaneObstacles` (`MSPModel_Striping.cpp:826-880`) reads `getPedestrians(nextLane)` **live — there is no frozen snapshot**. So lane *L*'s result depends on whether its successor lanes have already moved this pass, which SUMO fixes by iterating lanes in numerical-id order. Naive per-lane parallelism changes results. |

So the exploitable unit is **not** the lane. Safe parallelism requires partitioning lanes into sets with
no successor relation crossing the partition boundary, and processing partitions in lane-id order —
i.e. **junction-disjoint regions**. The engine already has exactly this shape for vehicles
(`RegionPlan` / `BuildRegionActive`, `Engine.cs:566`, `:3155-3164`), and that is the precedent to follow.

**Phase 1 ships the person pass single-threaded**, with the layout above so the regional decomposition
is available later without a store reshape — the same sequencing the vehicle engine used. What Phase 1
must *not* do is choose a layout that forecloses it.

### 4.3 The performance gates

Layout claims are worthless unassserted. Three gates, each a number:

1. **Zero steady-state allocation on the person step path.** The engine already has per-phase allocation
   accounting (`Engine.ProfilePhases` → `PhaseBytes`, `Engine.cs:803`, using
   `GC.GetTotalAllocatedBytes` deltas). A test asserts the person phase allocates **0 bytes** per step
   after warm-up on the Tier C scenario. This is what catches the §4.1(d) hazard the moment it appears,
   rather than at profiling time months later.
2. **`par == single`, and it is live in Phase 1 — not deferred.** An earlier draft said this gate waits
   for person-side parallelism. That is wrong: **vehicles query pedestrians from inside a possibly
   parallel `PlanMovements`** (§6.6.4), so the race is on the *read* side and exists the moment the
   coupling does. On a person-bearing scenario with vehicles, both the vehicle bench hash and the person
   trajectory hash must be identical with `Engine.UseParallelPlan` on and off — that is R10's acceptance
   condition, and SP-5.6(d) owns it. SP-2.3's two-single-threaded-runs check is necessary and not
   sufficient. When person-side regional parallelism is *later* enabled the same gate simply widens.
3. **A person-scale benchmark**, `Sim.BenchPed`, reporting person-steps/second on the Tier C saturated
   scenario, committed with its number so a regression is visible. Note the existing `Sim.Bench`
   determinism hash covers **vehicles only** and cannot see persons at all.

**Zero heap allocation on the person step path is MANDATORY**, not a target — standing rule S-f in
`SUMOPED-TASKS.md`. Gate 1 is how it is enforced, and it is checked every stage, not once.

**The default build is parity-exact, always.** The sort order, the ordinal-id tie-break and the
iteration order are *behaviour*, not implementation detail; an optimisation that reorders them has
changed the model. Per CLAUDE.md prime directive 3 and §Measurement discipline item 1, an optimisation
is accepted only when the goldens stay byte-identical **and** the behavioural surface agrees — neither
alone is sufficient.

A behavioural deviation may nonetheless be worth taking when it buys a large enough performance win.
That is allowed, and §4.4 is the protocol that keeps it honest.

### 4.4 The performance-deviation protocol

A deviation that is measured, gated, communicated and signed off is engineering. An unmeasured one
that creeps in because it made a benchmark look better is how a faithful port stops being faithful.
The difference is entirely process, so here is the process.

#### 4.4.1 First: most of the available wins do not require deviating at all

Before invoking this protocol, exhaust the **exact** optimisations. Anticipated candidates, classified:

| optimisation | exact? |
| --- | --- |
| lane-bucketed SoA, hot/cold split, denormalised vType scalars (§4.1) | **exact** — pure layout |
| pooled `Obstacle` scratch instead of per-ped allocation (§4.1d) | **exact** — the single biggest win available |
| maintaining the per-lane sort incrementally instead of re-sorting every pass | **exact** *if* the resulting order is identical, tie-break included |
| a spatial index for `blockedAtDist` instead of its O(n) per-lane scan | **exact** *if* it returns the same first blocker — note SUMO short-circuits on the first foe when `collectBlockers == nullptr`, so "same set" is not sufficient, it must be the **same first** |
| cross-lane parallelism ignoring the live successor read (§4.2) | **deviating** |
| `float` instead of `double` in the utility fold (SIMD-friendly) | **deviating** |
| capping the `LOOKAROUND_VEHICLES` 60 m vehicle scan | **deviating** |

The first four are where the order-of-magnitude is, and they cost no faithfulness. Reach for the last
three only after those are done and measured.

#### 4.4.2 The gate for a deviation

Each one gets an id **PD-n** and is not accepted until all of the following exist, in the tracker:

1. **Default OFF.** An opt-in flag on `Engine`, following the existing `FastMode`/`LanelessRvo`
   precedent (`Engine.cs:760`, `:771`) — whose own comment already states the standard: *"fully
   DETERMINISTIC (thread-count-independent), just not trajectory-identical to SUMO; validated
   BEHAVIORALLY, not byte-identically."* A test asserts **no committed scenario sets it**, so every
   golden stays on the exact path.
2. **A measured speedup**, on the Tier C saturated scenario, from `Sim.BenchPed`. The bar is
   **≥1.3× on the person phase** — below that the deviation is not worth its cost, and "slightly
   different for a few percent" is exactly what this protocol exists to refuse. The owner may move the
   bar; nobody else may.
3. **A measured behavioural delta**, quantified — "slightly different" is not a finding, it is a
   feeling. Mirroring the vehicle `--fast-gate`, a person deviation reports against the exact build:
   - per-step position delta: **RMS and max**, and the fraction of person-steps outside 0.1 m;
   - **collisions must not increase** (the R5b baseline table is the denominator);
   - the **R3 assertions still hold** — stripe-distinct curb accumulation, ≥3-stripe abreast entry,
     pass-by no-stall (`SUMOPED-COVERAGE.md` §7);
   - aggregate `<personinfo>` parity: routeLength / duration / timeLoss within a stated tolerance, plus
     a KS test on the trip-duration distribution;
   - the `jammed` count within a stated tolerance.
4. **Both surfaces.** Measured on the goldens **and** on the saturated/demo surface. CLAUDE.md
   §Measurement discipline item 1: a change once kept all 661 goldens byte-identical while moving the
   demo 61 → 94 overlaps, and another transformed the demo while breaking 14 goldens. Neither surface
   alone can accept a change.
5. **A visual A/B.** `scripts/render-ped-fcd.py --manifest` emits one multi-scene HTML with the exact
   and deviating runs as adjacent scenes, same scenario, same camera. This is cheap — the renderer
   already exists — and it is the check that catches "the numbers moved a little but it now looks
   wrong", which no aggregate can.
6. **Owner sign-off, recorded.** Deviations are the owner's call, not the implementor's. The tracker
   row is not ticked until the sign-off is in it.
7. **Determinism preserved.** A deviation may trade *fidelity to SUMO*; it may never trade
   *reproducibility*. Same inputs ⇒ same outputs, and thread-count-independent if it is a parallelism
   deviation. This is the one property with no exception.

#### 4.4.3 Deviations compose badly

Measure each alone **and** the stack. Two deviations each 0.05 m RMS can interact into something much
worse, and the stack is what actually ships. The ledger records both columns.

#### 4.4.4 What is not a deviation

Widening a `tolerance.json` to make a scenario pass is **never** a performance deviation — it is
`SUMOPED-PROCESS.md` §6.1, and the answer there is no. A deviation is an explicit, named, gated
behaviour change with the numbers above; a widened tolerance is an unexamined one.

## 5. The per-step algorithm

### 5.1 Ordering — the single most important thing to get right

SUMO (`MSNet.cpp:763-796`):

```
myBeginOfTimestepEvents->execute()   <-- MovePedestrians fires HERE: peds move FIRST
planMovements()                      <-- vehicles plan, seeing THIS step's ped positions
setJunctionApproaches()
executeMovements()
changeLanes()
```

Peds move on **previous-step** vehicle state; vehicles plan on **current-step** ped state. SUMO makes the
first half explicit by querying `link->opened(currentTime - DELTA_T, ...)`
(`MSPModel_Striping.cpp:1242-1243`, with a comment saying exactly why).

**Port:** insert `AdvancePersons(time)` into `Engine.AdvanceOneStep` in the Input phase, after
`AdvanceRailCrossings` (`Engine.cs:3214`) and **before** the neighbour-snapshot refill at
`Engine.cs:3241` — the precedent is `AdvanceObstacles` (`Engine.cs:3208`), which already documents this
"advance a foreign mover before the refill/PlanMovements" discipline. Persons are then fully committed
before `PlanMovements` (`Engine.cs:5170`) reads them.

⚠ **Two ordering facts must be established by trace before any code is written**, not reasoned about:
1. Where `MovePedestrians` sits relative to the **TLS switch command** inside SUMO's same
   begin-of-timestep event queue. Both are `myBeginOfTimestepEvents`; if TLS switches first, peds see the
   new phase in the same step. SumoSharp advances actuated TLS at `Engine.cs:3234`, which is *after* the
   proposed `AdvancePersons` slot — that may be wrong and is task **SP-1.3**.
2. The `t - DELTA_T` link evaluation. This requires the port to keep the **previous step's** link state
   readable during the person pass — a one-step-lagged snapshot, not a live read.

### 5.2 The two-pass structure

```
AdvancePersons(t):
    MoveInDirection(t, changedLane, FORWARD)     // ALL lanes
    MoveInDirection(t, changedLane, BACKWARD)    // ALL lanes
```
Direction-then-direction, **not** lane-then-lane. A FORWARD ped's post-move position is visible to
BACKWARD peds later in the same step, but not vice versa. This asymmetry is observable and must be
preserved.

`MoveInDirection` iterates `_activeLanes` in **lane numerical-id order**; within a lane, peds are re-sorted
before every use by `dir * myRelX` descending with a **tie-break on person id string**. That tie-break is
the only thing standing between this model and nondeterminism when two peds share an exact `myRelX`
(common at insertion) — it is not optional, and it must be an ordinal string comparison, not culture-aware.

### 5.3 `Walk()` — the utility fold (`MSPModel_Striping.cpp:2022`)

This is the heart. The ordered sub-steps are **non-commutative** — the lateral-penalty step reads
`distance[current]` computed earlier, and the reserved-stripe step must precede the oncoming-conflict
step. They go in a single method, in SUMO's order, each with its `.cpp:line` anchor:

```
distance[i] = DistanceTo(obs[i], includeMinGap: obs[i].Type == Ped)
utility[]   = 0, then in this order:
  (a) DIST_OVERLAP stripes -> OBSTRUCTED_PENALTY (-300000) on that stripe AND every stripe
      beyond it away from `current`     <-- R3a: you cannot sidestep THROUGH someone
  (b) oncoming-reserved stripes -> INAPPROPRIATE_PENALTY (-20000), half-weight if it is `current`
  (c) obstacle moving opposite to myDir -> -0.5 on the stripe one step further from its approach side
  (d) expectedDist = min(vMax * LOOKAHEAD_SAMEDIR, distance[i] + obs[i].speed * myDir * lookAhead)
      >= 0 ? utility[i] += expectedDist : utility[i] += ONCOMING_CONFLICT_PENALTY + distance[i]
                                          <-- R3b: a free stripe is worth up to ~5.6 m of utility
  (e) edge stripe in walking dir -> ONCOMING_CONFLICT_PENALTY if oncoming traffic present at all
  (f) if distance[current] > 0 AND myWaitingTime == 0: utility[i] += |i-current| * LATERAL_PENALTY (-1)
      <-- only -1/stripe, which is why (d) wins and the group goes abreast, not single-file;
          and NOT applied to an already-stalled ped, so a blocked ped can still escape sideways
  (g) shared space + BACKWARD -> keep-right bias
chosen = argmax(utility) subject to utility >= 0.5 * INAPPROPRIATE_PENALTY
next   = current +/- 1 toward chosen            // one stripe per step, never a jump
xDist  = min(distance[current], distance[other], distance[next])
xSpeed = clamp(xDist - NUMERICAL_EPS, 0, vMax); MIN_STARTUP_DIST guard; jam/squeeze; dawdle
ySpeed = ... LATERAL_SPEED_FACTOR ... (or the bodily step-aside for an oncoming vehicle)
```

Requirements R3a/R3b/R3d are *literally* steps (a), (d)+(f), and (b)+(e) of this fold. Any of them wrong
and the crossing looks wrong. This is why `StripingWalk.cs` is a separate file with no engine
dependencies: it must be unit-testable by feeding it a hand-built `Obstacle[]` and asserting the exact
`(myRelX, myRelY, mySpeed, mySpeedLat, myAmJammed)` transition — a golden-free, fast, surgical test bed
built **before** the surrounding machinery (task SP-4.1).

### 5.4 Obstacle sources

Eight, all folded into one `Obstacle[stripes]` per ped per step via `MergeObstacles`:
same-pass already-moved peds; `GetNeighboringObstacles`; same-lane vehicles; next-lane obstacles
(memoized per next-lane); walkingarea vehicle foes; a closed-link wall; an arrival-position wall;
crossing vehicles (`AddCrossingVehs`).

`MergeObstacles` keeps the closer obstacle, with a tie-break where a Ped/Vehicle obstacle **displaces** a
topological one at equal distance. That tie-break is behavioural, not cosmetic.

### 5.5 `GetNextLane` and the junction-local pedestrian router

Whenever a ped is on a **walkingarea**, `GetNextLane` (`MSPModel_Striping.cpp:573-638`) calls SUMO's
intermodal pedestrian router to decide which crossing to take. This is a real dependency — the ped's
`<walk edges=...>` lists *normal* edges only; the model inserts walkingareas and crossings itself.

It is however **much smaller than "port the intermodal router"**: `PedestrianEdge::prohibits`
(`utils/router/PedestrianEdge.h:58-67`) restricts the search to edges touching *this junction*, so it is
a Dijkstra over ~10–30 edges with cost

```
cost = partialLength / pedSpeed
     + (edge is crossing && its incoming link is TL_RED ? max(0, TL_RED_PENALTY - elapsed) : 0)
     + (edge is crossing ? edge.TimePenalty : 0)
```

`JunctionPedRouter.cs` implements exactly that and nothing else. It is re-run with the origin walkingarea
prohibited whenever a chosen link turns out closed — which is how a blocked ped picks a *different*
crossing, a behaviour worth its own scenario later.

---

## 6. Vehicle ↔ pedestrian coupling

### 6.1 The decision that matters: phantom leaders, not crowd discs

SUMO does **not** give pedestrians a bespoke braking rule. `MSLink::getLeaderInfo`
(`MSLink.cpp:1667-1688`) pushes the blocking ped into the **same `LinkLeaders` vector used for
vehicle-vehicle junction conflicts**, as an entry with `vehicle == nullptr`, `gap == -1`,
`distToCrossing = distToPeds`. Every downstream consumer — car-following adaptation, lane-change gap
acceptance, zippering — treats it as "something is blocking `distToCrossing` ahead" and brakes with the
machinery that is already parity-validated.

**The port must do the same** — inject a phantom entry into the existing junction-leader path consumed
by `AdaptToJunctionLeader`. That path is **live** (called from the plan at `Engine.cs:7827` and `:8011`;
the nearby *"NOT WIRED IN"* comment at `:10004` belongs to `IsLeader`, a different method), so CLAUDE.md
§Measurement discipline item 3 is satisfied: the mechanism has a live reader *and* a live caller.

⚠ **But "with a null vehicle" is the wrong description, and the difference is the actual work.**
`JunctionLeaderCandidate` (`Engine.cs:9988`) is
`(string LaneId, string Id, double Speed, long EntryTime, long EntryTimeNeverYield,
long ConflictEntryTime, double MinGap, double MaxAccel, double MaxDecel, double HeadwayTime,
double Length)` — there is **no vehicle field to null**. A phantom is just a synthetic `Id`, which is
easier than SUMO's `nullptr`. The real question is the *consumption* branch:

- SUMO pushes the ped in with `gap == -1`, a sentinel meaning **"no gap is known — brake to stop before
  `distToCrossing`"**, and it deliberately bypasses the arrival-time foe comparison.
- Our candidate instead carries `EntryTime`, `ConflictEntryTime`, `Length`, `MinGap`, `HeadwayTime` —
  the inputs to **car-following adaptation against a real vehicle foe**. A pedestrian has no meaningful
  value for any of them.

So this is not a drop-in, and the danger is specific: SP-5.1's `13.89 → 11.11 → 6.61` success condition
is reachable by *tuning a synthetic `Length`/`EntryTime`* until the numbers match — the exact failure
mode `SUMOPED-PROCESS.md` §6.1 forbids, and one that would pass its own test. SP-5.1's first success
condition is therefore a **reading** task: establish how SUMO's consumer branches on `gap == -1`
(`MSVehicle::adaptToJunctionLeader` / `adaptToLeaders`, reached from the ped block at
`MSLink.cpp:1667-1688`) and whether `AdaptToJunctionLeader` has an equivalent branch or needs one added.

**If it needs one added, that is an edit to the live vehicle plan path**, and Stage 5 stops being
"additive, gated on `Persons != null`". The S-d full-gate re-run inside SP-5.1 is then load-bearing, not
a formality. Risk #5 in §13 is scoped on that assumption.

This is deliberately **not** the existing crowd-disc route (`CrowdLongitudinalConstraint` binder 13,
`CrowdYieldConstraint` binder 16, `Engine.cs:11366/11475`). Those stay, unchanged, for the ORCA layer.
Reusing them for SUMO-model peds would reproduce SUMO's *outcome* through a different mechanism and would
diverge everywhere a vehicle brakes for something other than its immediate leader — exactly the failure
mode CLAUDE.md prime directive 4 exists to prevent.

### 6.2 `BlockedAtDist` (`MSPModel_Striping.cpp:223`)

```
for ped in PedestriansOn(crossingLane):
    leaderFrontDist = ped.Dir == FORWARD ? vehSide - ped.RelX : ped.RelX - vehSide
    leaderBackDist  = leaderFrontDist + ped.Length
    if leaderBackDist >= -vehWidth
       && (leaderFrontDist < 0
           || (leaderFrontDist <= oncomingGap && ped.WaitingTime < 2.0s)):
        -> blocked
```
Note the second clause: a ped that has been **standing for ≥2 s** stops blocking the vehicle. That is
what breaks the mutual deadlock in the §2.1 trace — it is why the car eventually goes and the ped waits,
rather than both freezing. Getting this constant wrong produces a plausible-looking gridlock.

`oncomingGap` is the vType's `jmCrossingGap`. Lookup is a direct per-lane list scan, no spatial index.

### 6.3 `CheckWalkingAreaFoe` (`MSLink.cpp:1748`) — the bare-walkingarea case

Where a vehicle crosses a walkingarea with no marked crossing, the test is 2-D: distance to the ped,
`IsInFront` (bearing < 75° **and** within `egoWidth + SAFETY_GAP` of ego's internal-lane path polyline),
and an oncoming discount `pedDist = pedMaxSpeed * max(sqrt(dist)/2, TS) * oncomingFactor`. Produces the
same phantom-leader entry.

### 6.4 The other direction: vehicles as obstacles to peds

`AddCrossingVehs` (`MSPModel_Striping.cpp:1320`) turns vehicles approaching/on the crossing into per-stripe
obstacles, with `prio`-dependent brake-gap assumptions, plus the **"fully blocked ⇒ pin every stripe"**
second pass that stops peds trickling one-at-a-time into a jammed crossing. `AddVehicleFoe` does the same
for a vehicle cutting across a walkingarea, injecting it as one-or-more fake pedestrians occupying stripes.

### 6.5 Shared lanes

`HasPedestrians(lane)` / `NextBlocking(lane, minPos, minRight, maxLeft)` feed
`MSVehicle::planMove` (`MSVehicle.cpp:2422`) and insertion (`MSLane.cpp:4440`) for peds walking on a lane
vehicles also use. Ported for the `sidewalk-shared-lane` scenario. The `MSLCM_SL2015` sublane hooks are
out of scope (Requirement R-N7).

---

### 6.6 The cross-population data-flow contract — who sees whom, and when

This is the part most likely to be got subtly wrong, because SumoSharp's vehicle phase is built on a
**frozen start-of-step neighbour snapshot** (`neighbors.Refill`, `Engine.cs:3241-3256`) for determinism
and parallel planning. Fold persons into the wrong buffer and vehicles silently brake for where
pedestrians were *last* step. The scenario still runs; it is just wrong, by one step, everywhere.

#### 6.6.1 The contract

| direction | reads | at what time |
| --- | --- | --- |
| **peds → see vehicles** | vehicle positions on the lane (`getVehicleObstacles`), walkingarea foes (`addVehicleFoe`), crossing vehicles (`addCrossingVehs`) | **end of the previous step** — automatic, because the ped pass runs before `PlanMovements`/`ExecuteMoves` |
| | the junction **approach registry** (`myApproachingVehicles`, via `link->opened` and `getLeaderInfo`) | **previous step's** — SUMO builds it in `setJunctionApproaches` (`MSNet.cpp:787`) *after* `planMovements`, so the one a ped reads at *t* was built at the end of *t−1*. ⚠ genuinely lagged; see 6.6.3 |
| | the **traffic-light state** (`haveRed()`) | **CURRENT** — not lagged; see 6.6.2 |
| **vehicles → see peds** | `blockedAtDist`, `nextBlocking`, `hasPedestrians` over `myActiveLanes` | **current step**, post-move — peds have already moved |

**The key property: there is no cycle.** Peds read vehicles at *t−1*, vehicles read peds at *t*. One
directional pass each, no iteration to convergence, no ordering ambiguity to resolve. That is what makes
the coupling implementable at all, and it is worth stating because a two-way *same-step* coupling would
need a fixed-point solve that neither engine has.

#### 6.6.2 ⚠ The `t − DELTA_T` is an arrival time, not a lagged light

Easy to misread, and an earlier draft of this design did. `walk()` calls
`link->opened(currentTime - DELTA_T, ...)`, which looks like "evaluate the link as it stood last step".
It is not. `MSLink::opened` (`MSLink.cpp:754`) tests `haveRed()` against the link's **current** state;
the `arrivalTime` argument is used only in the foe-conflict comparison against
`myApproachingVehicles[].arrivalTime` (`:770`). Backdating it by one step makes the pedestrian **lose
ties** to vehicles that registered a later arrival — which is exactly what the striping source's own
comment means by "they cannot rely on vehicles having passed the intersection in the current time step".

Two consequences:

1. **Pedestrians read the CURRENT traffic-light phase.** So the placement of `AdvancePersons` relative
   to SumoSharp's actuated-TLS advance (`Engine.cs:3234`, deliberately *before* Plan "so
   `RedLightConstraint` sees this step's phase") is **behaviourally load-bearing**, not cosmetic. If
   `AdvancePersons` sits before it, peds see the *old* phase.
2. This sharpens **SP-1.3** from "what is the begin-of-timestep event order" to a question a single
   trace can answer: **on a step where the light switches, does a pedestrian see the new phase or the
   old one?** Run the oracle across a phase boundary on `xwalk-tls-release` and read it off. Do not
   reason it from the event queue.

#### 6.6.3 What must be one-step-lagged, and what must not

Only one thing genuinely needs a retained previous-step buffer, and SumoSharp currently destroys it too
early:

- **The junction approach index.** `BuildFoeApproachIndex` (`Engine.cs:3273`) is rebuilt *after* the
  proposed `AdvancePersons` slot. Peds must read the **previous** step's index (6.6.1). So either the
  prior index stays alive until the ped pass has finished, or the rebuild moves. Naively reading the
  freshly-built one gives peds this step's vehicle claims — information SUMO's peds do not have, and a
  silent fidelity change with no test to catch it.

Everything else needs **ordering, not buffering**:

- Vehicle *positions* read by peds are correct simply because the ped pass runs before `ExecuteMoves`.
- Ped positions read by vehicles are correct because `AdvancePersons` runs **before**
  `neighbors.Refill` (`:3241`). If persons are folded into that snapshot, they are folded *after*
  moving — right by construction. ⚠ But only as long as the ordering holds: a later refactor that moves
  the refill earlier, or the ped pass later, breaks it silently. **Assert the ordering** rather than
  relying on the call sequence (task SP-5.6).

#### 6.6.4 Invariants for the parallel plan

`PlanMovements` may run parallel (`UseParallelPlan`, `Engine.cs:931`), and vehicles query pedestrians
from inside it. Therefore:

- **Persons are immutable for the whole vehicle phase.** All person mutation happens inside
  `AdvancePersons`; nothing in `PlanMovements`/`ExecuteMoves` may write person state.
- **The ped→vehicle query is read-only, allocation-free and thread-safe** — a lookup into the
  lane-bucketed store (§4.1), no scratch, no lazy caching, no memoisation that mutates on read. A lazily
  populated cache here would be a data race that only shows up as nondeterminism under load, which is
  the worst possible way to find it.
- **Person structural mutations are immediate, not command-buffered.** A ped moving between lane
  buckets happens inside the ped pass, matching SUMO's `arriveAndAdvance`. This is safe precisely
  because the pass is sequential and completes before any vehicle reads — it is *not* an exception to
  the engine's deferred-mutation discipline, it is a different phase.
- The determinism gate is the proof: the person trajectory hash must be identical single-threaded and
  parallel (§4.3 gate 2).

## 7. Traffic lights

There is **no separate TL code path** for the ped-blocks-vehicle direction — `BlockedAtDist` runs
identically. What differs is only whether the crossing *link* is open, which is standard link state.

Two things are ped-specific:
- `IgnoreRed(link)` (`MSPModel_Striping.cpp:2632`) — the ped honours a grace window of
  `jmDriveAfterRedTime` after the light turned red, using the same vType param vehicles use.
- `GetImpatience(t)` = `clamp(0, 1, vType.impatience + stageWaitingTime / MAX_WAIT_TOLERANCE)`, saturating
  at 120 s, passed into the link-opened query.

The crossing's TL link index comes from the net's `<tlLogic>` + `<connection linkIndex=>` (§3.1). Note
`Sim.Pedestrians/Crossing/CrossingTlReader.cs` already does a version of this read for the ORCA layer;
it is **not** reused (R-N4) but is worth reading for the linkIndex-association details it got right.

---

## 8. The parity harness

`Sim.Harness/FcdParser.cs:24-47` iterates `timestepEl.Elements("vehicle")` — a `<person>` row is silently
skipped, producing not even a presence mismatch. It is not extensible by adding a case; the element-name
filter is baked in.

**New, parallel to the vehicle types** (not modifications to them, so the vehicle gate cannot move):

```
PersonTrajectoryPoint(PersonId, Time, Edge, Pos, Speed, X, Y, Angle, Slope)
PersonTrajectorySet
PersonFcdParser.Parse(path)               // iterates Elements("person")
PersonTrajectoryComparator.Compare(actual, golden, tolerance)
```

`tolerance.json` gains an optional `comparedPersonAttributes` array + per-attribute tolerances.
`ToleranceConfig.ToleranceFor` throws for an unconfigured compared attribute — that is the desired
behaviour and the new attributes must be added to its switch explicitly.

`edge` is compared as an **exact string**, including internal ids. A mismatch there means the ped is on
the wrong lane of the junction, which is the failure mode most likely to be masked by a metric tolerance.

### 8.1 Derived assertions, on the oracle first

The R3 acceptance conditions (stripe counts, abreast entry, pass-by no-stall) are computed from the
golden **and asserted against the golden itself** before being required of SumoSharp. If SUMO's own
output does not show ≥3 peds entering abreast, the scenario is mis-authored and must be fixed — not
worked around. This follows CLAUDE.md §Measurement discipline item 1: a claim needs the surface that can
actually contain it.

Stripe index is not in the FCD; it is derived as `round(relY / stripeWidth)` from the ped's world
position projected onto the lane, in a shared helper used by both arms of the comparison.

### 8.2 `regen-goldens.sh`

**This is the single specification of what the script must do for `_sumoped`** — SP-0.3 references it
rather than restating it, so there is one place to change.

1. `_sumoped` is included in the generic walk. The striping options (`--pedestrian.model striping`,
   `--pedestrian.striping.dawdling 0`) live in each scenario's `config.sumocfg`, so the walk picks them
   up with no per-scenario logic in the script.
2. The person output set beyond FCD is emitted per tier — `--person-summary-output`,
   `--personinfo-output`, `--statistic-output`, `--collision-output` always; `--netstate-dump` for
   Tier A/B only (it is large); stderr captured, normalised and sorted to `golden.warnings.txt`.
3. Honest-SUMO flags on every `_sumoped` run: `--time-to-teleport -1 --collision.action warn
   --collision.check-junctions true` (CLAUDE.md §Measurement discipline item 11).
4. Tier C is windowed with `--device.fcd.begin`, and the script **verifies** the emitted FCD actually
   starts at that time rather than assuming it.
5. ⚠ **If an `--fcd-output.attributes` list is used at all, it must include `angle` and `slope`.**
   An earlier version of this section said only "do not mask attributes out", and Appendix B then
   shipped a mask that dropped `angle` — the one attribute R2 calls load-bearing. Prefer no mask; if
   bytes force one, the script asserts `angle` is present in the emitted rows rather than trusting the
   flag string.
6. `provenance.txt` records `sumo_version=1.20.0` plus input sha256s, and re-running the script twice
   produces byte-identical goldens.

---

## 9. The Phase 2 seam — a third tier on a ladder that already exists

Phase 2 (Requirement R-N2) promotes a low-power ambient ped to a SUMO-model ped near a crossing and
demotes it afterwards. Phase 1 must not build it, but must not foreclose it.

### 9.0 ⚠ Corrected: the switching machinery is already built

An earlier draft of this section described Phase 2 as inventing a promotion mechanism, and §10.3
described it as "a bridge between two *different* APIs". Both were written without looking at
`src/Sim.Pedestrians/Lod/PedLodManager.cs`, which **is** an LOD ladder with promotion and demotion,
already shipped and already tuned:

| the hybrid needs | `PedLodManager` already has |
| --- | --- |
| two tiers with different cost | low-power `PedDrModel.PathArc` (pose is a pure function of path/startTime/speed, O(1), no neighbour query) and high-power `FreeKinematic` (a real agent in a persistent `OrcaCrowd`) |
| a promotion trigger | `InterestField` — multi-source, stable per-source ids, grid-indexed bounded per-ped query |
| hysteresis | promote inside any `InterestSource.PromoteRadius`; demote only after continuously outside every (larger) `DemoteRadius` for `dwellSeconds`, plus a minimum dwell in each state |
| an override | `SetForcedHighPower(id, on)` |
| cheap churn | O(1) `Add`/`Remove` on both the crowd and the route controller (P0-1…P0-3) |
| replication across a switch | `PedReplicationPublisher`, which already publishes across a `PedDrModel` change |

**So Phase 2 is "add a third tier to that ladder, with crossings as the interest sources"** — not a
bridge between unrelated systems. That is a far smaller problem, and it means the Phase-1 seam is shaped
by an existing contract rather than by guesswork.

It also means Phase 1 inherits a lesson rather than having to learn it. `PedLodManager`'s own header
records that the POC version rebuilt the high-power crowd on every membership change — an
O(current-high-power-count) cost per switch, **measured at 100k as the dominant reason a churning world
cost 3.6× a stable one**. That is why `OrcaCrowd` was given real O(1) add/remove, and it is why §4.1(e)
below treats person spawn/despawn as a hot-path operation rather than a one-off.

### 9.1 What Phase 1 must build

**(a) A bidirectional coordinate contract**, `(edge, pos, posLat) ↔ (x, y)`, as a pure public function on
the person model. The forward direction is already required for FCD output; the inverse (world → nearest
walkable edge + offset) is the piece the ORCA layer lacks — exactly the "world→edge resolver" that
`PersonFcdWriter.cs:14-16` defers as backlog item P8-5. Phase 1 builds the inverse anyway: it is small,
and it completes `PersonFcdWriter` as a side effect.

**(b) Adoption at an arbitrary state, and it is a function Phase 1 is already writing.** SP-2.1d's
single-step replay reconstructs a complete `PState` from a golden FCD row plus lane geometry. **A
promotion is that same function with a different data source** — an ORCA ped's world position and
velocity instead of an FCD row. Building it twice guarantees the two drift, and the test-side one will be
the correct one because it is the one with 30,000 golden person-steps behind it.

So the reconstruction is **public and `Engine`-side**, and the replay harness is a *caller* of it:

```csharp
bool TryAdoptAt(in PersonAdoptionState st, out PersonHandle p);   // the one entry point
```

The Phase-1 payoff is immediate: the promotion path is exercised by every replayed golden step long
before Phase 2 exists.

**(c) The promotion state must be fully specified, including the fields nothing observes.** §10.4's
`SpawnPersonAt(type, edge, pos, posLat, speed, rest)` is not sufficient. `PState` also carries `myDir`,
`mySpeedLat`, `myWaitingTime`, `myWaitingToEnter`, `myAmJammed`, `myNLI` and `myWalkingAreaPath` — and
`SUMOPED-PROCESS.md` §3.1 lists **exactly those** as the fields the FCD does not observe. In the replay
harness they come from the golden's history. **A promoted ambient ped has no history at all**, so the
adoption must *define* them:

| field | on adoption | why this value |
| --- | --- | --- |
| `myDir` | sign of the incoming velocity projected on the lane | the only source available |
| `mySpeedLat` | lateral component of the incoming velocity | continuity of motion; do not zero it, that is a visible sideways snap |
| `myWaitingTime` | `0` | the ped was walking, by construction — it was ambient a step ago |
| `myWaitingToEnter` | **`false`** | ⚠ load-bearing, see below |
| `myAmJammed` | `false` | jam state is earned over `jamTime`; a fresh adoptee has not earned it |
| `myNLI`, `myWalkingAreaPath` | recomputed from `(net, route tail, current lane)` | deterministic functions of committed inputs (§3.2) |

⚠ **`myWaitingToEnter` is not a cosmetic default.** It gates `WALK-OBSTRUCT-SELF-WAITING-EXEMPT`
(`Striping.cpp:2046-2048`), which exempts a ped from self-penalising its own current stripe, and it
interacts with the `MIN_STARTUP_DIST` guard. Set it `true` and a promoted ped skips the startup guard;
leave the exemption logic thinking it is `true` and it self-blocks on its first tick. Either one is a
visible stutter **at exactly the moment the hybrid exists to make seamless**. This is asserted by
SP-7.2, not left to inspection.

**(d) One stable id across both tiers.** `PedestrianWorld.AddWalker(int id, …)` takes a
**caller-supplied** id; `PersonHandle` is engine-minted (D22). Under the hybrid a remote client watching
a ped cross the street would see it leave the ped channel and reappear in the person channel — **a pop in
the protocol even when the position is perfectly continuous.** Because the ORCA id is caller-chosen, the
fix is one optional parameter: a caller-supplied `externalId` on the person spawn/adopt API, so a host
can carry one id across both tiers.

This is the cheapest item in this section and the most expensive to retrofit: one parameter now, a
replication-protocol migration later. It does **not** mean Phase 1 builds person replication — see
§10.2 item 7 — only that the id contract is settled while it is still free.

Nothing else about Phase 2 is decided here.

### 9.2 Two things the hybrid makes better, recorded so Phase 2 does not re-derive them

- **The population shape matches the parallel unit.** §4.2 concludes the only parity-safe parallel unit
  is the junction-disjoint region, because `getNextLaneObstacles` reads successor lanes live. Under the
  hybrid, SUMO-peds **exist only near junctions** — the population is naturally partitioned by exactly
  that unit. The Phase-1 layout lines up with the hybrid's shape rather than fighting it.
- **DR classification survives the boundary.** D26 tags persons `DrModel.FreeKinematic`; the ORCA
  high-power tier is already `PedDrModel.FreeKinematic`. A ped keeps its DR classification across a
  promotion, so the interpolation path does not change under the switch — one less discontinuity for
  `PedReplicationPublisher` to encode.

### 9.3 What the hybrid takes OUT of the production path

A production SUMO-ped's whole lifecycle is **promote → cross → demote**. It is never inserted from
`<person>` demand and never reaches an `arrivalPos`. So the demand-insertion and arrival families are
**parity-harness paths, not product surface**: they must be ported to the extent the goldens exercise
them (SUMO's own runs do insert and arrive), but they are not on the hybrid's critical path and are
labelled that way in the task list rather than scoped as product features. See Requirements R-N8.


---

## 10. Public API — readiness assessment and the exact delta

**Verdict: the API is well prepared for a second agent type on the `Engine`, and not at all prepared
for unifying with the ORCA pedestrian layer.** Nothing blocks the port. The work is ~6 new small types
plus additive edits to 3 existing files, and **no edit to any existing vehicle type** — so the 782-test
gate structurally cannot move. There is exactly one real design decision (§10.3).

### 10.0 Decisions log

Mirrors the format of `docs/SUMOSHARP-API.md` §12 (D1–D18), continuing its numbering as **D19–D27**
so the two logs read as one series. All **PROPOSED** until this document is signed off. Rationale is
in the subsections below; this table is the index.

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| D19 | **SUMO persons live on the concrete `Engine`**, alongside vehicles | They are lane-based (`edge`/`pos`/`posLat`) — the same shape as vehicles, and structurally unlike the ORCA layer's world-space agents (§1). This is where they belong, not merely where they are convenient. | A third facade beside `Engine`/`PedestrianWorld` |
| D20 | **`src/Sim.Pedestrians` (ORCA) is untouched**; the two tiers coexist | R-N3. 324 green tests, a different axis (live-reactivity, never golden FCD). Phase 2 bridges them via §9's coordinate contract. | Replacing or extending the ORCA layer |
| D21 | Persons go on **`Engine`, not `IEngine`** | `IEngine` is 48 lines (`LoadScenario`/`Run`/obstacles); the entire rich vehicle API already lives on the concrete class and `SUMOSHARP-API.md` documents it there. Follow the precedent rather than inventing a second convention. | Widening `IEngine` |
| D22 | **`PersonHandle` in its own id space**, same 32+16 index/generation shape | D4 applies unchanged. `VehicleHandle`'s own header already establishes the two-distinct-id-spaces precedent with `ObstacleHandle`. | Sharing `VehicleHandle`'s id space |
| D23 | **`PersonState` mirrors `VehicleState`**, but carries **both** `EdgeHandle` and `LaneHandle`, plus `Stripe` and `SpeedLat` | SUMO person FCD reports `edge`, and it takes **internal** ids (`:c_c1`, `:c_w1`) — that is exactly what makes the curb wait a checkable golden fact (§2.3). D7's float/double precision split carries over unchanged. | A lane-only state record |
| D24 | **Parallel `PersonEvent`**, not a generalised `SimEvent` | `SimEvent` is `VehicleHandle`-typed. A parallel type keeps it byte-identical and keeps D13's drained-buffer shape; generalising would touch every existing consumer for no gain. | Making `SimEvent` carry a discriminated handle |
| D25 | **`SimulationSnapshot.Count` keeps meaning *vehicles***; add `PersonCount` + parallel columns | Every existing consumer reads `Count` as the vehicle count. Repurposing it is a silent breaking change with no compiler error. | Making `Count` the total agent count |
| D26 | **Share the dead-reckoning layer.** `DrModel` unchanged (3 members), persons tagged `FreeKinematic`, one interpolation path parameterised by DR model | **Measured** (§10.5): persons extrapolate ~6× more accurately than vehicles (p95 0.64 m vs 4.08 m) because `MSTransportable::getAcceleration()` is hard-coded `0.0`; and mid-step chord interpolation on a **walkingarea** has the lowest p95 of all four categories (0.175 m vs the vehicle's 0.390 m). | A bespoke person extrapolator following the `WalkingAreaPath` Bezier — **this was an earlier draft of this design and the measurement disproved it** |
| D27 | **Unify the read shape and the DR/replication layer; keep identity, spawn and step scheduling separate** | This is SUMO's own split, verified in its source: it unifies **internally** (`MSTransportable`/`SUMOVehicle` both derive from the ~30-method `SUMOTrafficObject`; persons even reuse `MSVehicleType`) but **not externally** (separate libsumo/TraCI `Person` and `Vehicle` domains). §10.5. | A generic `AgentHandle`/`AgentState` unification now — it would touch `SimEvent`, `SimulationSnapshot`, `ReplicationPublisher` and every consumer of `Count`, risking the 782-test gate for zero parity gain (same reasoning as R-N4) |

---

### 10.1 What already has the right shape (reuse the pattern, change nothing)

| existing | why it transfers |
| --- | --- |
| `VehicleHandle` (`VehicleHandle.cs`) | 32-bit index + 16-bit generation. Its own header already establishes the precedent: *"Same 32+16 shape as `ObstacleHandle`, but a DISTINCT id space; never interchange them."* `PersonHandle` is a mechanical third instance of a pattern the repo already runs twice. |
| `VehicleState` (`VehicleState.cs`) | Structurally **exactly** what a person needs: `LaneHandle`, `Pos`, `Speed`, `PosLat` as parity-exact doubles, plus derived render-facing `X/Y/Z/Angle` floats. The D7 precision split (double = what the sim computed, float = where to draw it) is right for persons unchanged. |
| `VehicleReadBuffer` (`VehicleReadBuffer.cs`) | A struct-of-arrays **projection** refreshed each `Step` from the live entities, explicitly *not* the source of truth. That is precisely the relationship a `PersonReadBuffer` needs to `_activeLanes`. |
| `DefineVType(VTypeParams)` → `VTypeHandle` | Runtime type definition with a handle. `DefinePersonType(PersonTypeParams)` is the same shape (width, length, minGap, desiredMaxSpeed, speedDev, jmCrossingGap — the knobs in `SUMOPED-ALGORITHM.md` §4.2). |
| `SpawnVehicle` ×4 overloads, `GetLifecycle` → `Pending/Active/Arrived`, `RecycleVehicleSlots` + generation bump | **SUMO-parity queued insertion**: a spawn returns a handle immediately in `Pending` and transitions on successful insertion. Persons need exactly this — a person also waits for its depart time and for room on the sidewalk. |
| `SimulationRunner` (`SimulationRunner.cs`) | Agent-agnostic already: thread-safe `Post`/`Invoke` command queue, `Start/Pause/Resume/Stop`, snapshot double-buffer, `InterpolationAlpha`. Needs one new method, nothing restructured. |
| `ISimExportObserver` | Zero-alloc, read-only per-frame hook. Add `OnPersonExported`; the interface already uses default-implemented methods so this is source-compatible. |
| `Engine.CrowdSource` (`Engine.cs:783`) | The **template** for attaching the person model: a nullable property whose null case skips the code path entirely, with the inertness argument written in its own comment. `Engine.Persons` copies that discipline. |

### 10.2 What must be added (all additive)

| # | change | file | note |
| --- | --- | --- | --- |
| 1 | `PersonHandle` | new | copy of `VehicleHandle`, own id space, `ToString` → `Person#i.g` |
| 2 | `PersonState` | new | mirror of `VehicleState`, but carries **both** `EdgeHandle` and `LaneHandle` — SUMO person FCD reports `edge`, and it takes **internal** ids (`:c_c1`, `:c_w1`), which is the whole reason the curb wait is a checkable golden fact (§2.3). Add `Stripe` and `SpeedLat`. |
| 3 | `PersonEvent` / `PersonEventKind` | new | ⚠ **`SimEvent` is `VehicleHandle`-typed** (`SimEvent.cs`) — it cannot carry a person. A *parallel* type keeps `SimEvent` byte-identical; generalising it would touch every existing consumer for no gain. |
| 4 | `PersonReadBuffer` + `Engine` person spans | new + edit | `PersonHandles`, `PersonPosX/Y/Z`, `PersonAngle`, `PersonSpeed`, `PersonEdgeHandles`, `PersonPos`, `PersonPosLat`, `PersonIds`, `PersonCount`, `TryGetPerson` |
| 5 | `SimulationSnapshot` person columns | edit | ⚠ **`Count` currently means *vehicle* count** and is read that way by every consumer — it must **not** be repurposed. Add `PersonCount` + parallel columns + `TryGetPerson`; extend `Capture(Engine)`. |
| 6 | `SimulationRunner.TryInterpolatePerson` | edit | ⚠ **Corrected — see §10.5.** An earlier draft claimed persons need their own extrapolator because of the walkingarea Bezier. **Measured false.** Persons interpolate *better* than vehicles; reuse the same code path, parameterised by `DrModel`. |
| 7 | `Sim.Host/ReplicationPublisher` | **not Phase 1** (R-N9) — but settle the id contract | ⚠ It has **zero** occurrences of "person"/"ped" — it is vehicle-only, and the ORCA layer has its own `PedReplicationPublisher`. **Decision: Phase 1 does not build person replication.** What Phase 1 *does* do is accept an optional caller-supplied **`externalId`** on the person spawn/adopt API (§9.1(d)), so a host can carry one id across both tiers. Without it, a Phase-2 promotion is a pop in the *protocol* even when the position is continuous — one parameter now, a protocol migration later. |
| 8 | `DefinePersonType`, `SpawnPerson`, `SpawnPersonAt`, **`TryAdoptAt`**, `Despawn(PersonHandle)`, `ActivePersons()` | edit `Engine` | **`TryAdoptAt(in PersonAdoptionState)` is the Phase-2 hinge (§9.1b/c)** — the same reconstruction SP-2.1d needs, made public so the replay harness is a *caller* rather than a second implementation. Every spawn/adopt overload takes an optional `externalId` (§9.1d). |

Note `IEngine` (`IEngine.cs`, 48 lines) carries only `LoadScenario`/`Run`/the obstacle API — the entire
rich vehicle API lives on the concrete `Engine`, and `docs/SUMOSHARP-API.md` documents it there.
**Follow that precedent**: persons go on `Engine`, not on `IEngine`.

### 10.3 The one real decision: there are two unrelated agent APIs today

| | cars | ORCA pedestrians |
| --- | --- | --- |
| entry point | `Engine` | `PedestrianWorld` (`src/Sim.Pedestrians/PedestrianWorld.cs`) |
| identity | `VehicleHandle` (32+16, generational) | plain `int id` |
| position | `Pos`/`PosLat` on a lane + derived world | `Vec2 PositionOf(id, now)` — pure world space |
| per-frame read | `SimulationSnapshot` (SoA columns) | `PositionOf` per agent, or `PedPublisher` |
| replication | `Sim.Host/ReplicationPublisher` | `Sim.Pedestrians/Lod/PedReplicationPublisher` + its own codecs |

They share **nothing** — no common handle, no common snapshot, no common publisher. A host today drives
cars and ORCA peds through two entirely separate APIs.

**Decision: put SUMO persons on the `Engine` API, mirroring vehicles. Leave `PedestrianWorld` alone.**

Rationale: SUMO persons are lane-based (`edge`, `pos`, `posLat`) — the same shape as vehicles and
*structurally unlike* the ORCA layer's continuous world-space agents (§1). Putting them on `Engine` is
not a convenience, it is where they actually belong. It also matches R-N3 (the two tiers coexist).

Alternative considered and rejected: unify now behind a generic `AgentHandle`/`AgentState`. That would
touch `SimEvent`, `SimulationSnapshot`, `ReplicationPublisher` and every consumer of `Count`, risking
the vehicle gate for zero parity gain — the same reasoning as R-N4 for the net readers.

**The cost of this decision, named up front — and it is smaller than an earlier draft claimed.** That
draft said Phase 2's hybrid becomes "a bridge between two *different* APIs", implying a mechanism built
from scratch. It is not: `PedLodManager` is **already** an LOD ladder with promotion, demotion,
`InterestField` hysteresis, a forced-high-power override, O(1) add/remove and a replication publisher
that crosses the tier boundary (§9.0). Phase 2 adds a **third tier** to that ladder with crossings as the
interest sources.

What genuinely does cost something is **identity**: `PersonHandle` is engine-minted while
`PedestrianWorld.AddWalker` takes a caller-supplied `int id`, so without a shared id a promotion is
visible to every replication client. That is why Phase 1 carries the optional `externalId` (§9.1d) —
one parameter, paid now. §9's coordinate contract and `TryAdoptAt` are the rest of the seam, and Phase 1
builds them even though Phase 1 does not use them.

### 10.5 Dead reckoning, and the unification question — measured

Four questions worth settling with evidence rather than assertion, because the answers decide how much
code the two populations can share.

#### Can persons use the same dead reckoning as vehicles? **Yes — they are strictly easier.**

`MSTransportable::getAcceleration()` returns a hard-coded **`0.0`** (`MSTransportable.h:103-105`).
Pedestrians have **no acceleration model at all**: `walk()` sets `xSpeed` directly from the available
distance each step. Combined with `vMax = 1.39 m/s`, per-step displacement is tiny and velocity is
piecewise constant. Measured on the dense uncontrolled junction (330 persons, 98 vehicles, 150 s):

```
STEP-TO-STEP |delta speed| per 1 s step
  vehicles (accel 2.6 / decel 4.5)          median 0.000   p95 4.076   max 5.200
  pedestrians (no accel model)              median 0.000   p95 0.065   max 1.389

CONSTANT-VELOCITY EXTRAPOLATION ERROR over one 1 s step, metres
  vehicles                                  median 0.000   p95 4.076   max 5.522
  pedestrians, normal edges + crossings     median 0.000   p95 0.640   max 2.566
  pedestrians, walkingareas                 median 0.000   p95 0.466   max 2.215
```

Pedestrians are ~**6× more accurate** to extrapolate than vehicles in absolute metres. The one thing
they can do that vehicles cannot — jump 0 ↔ 1.39 m/s in a single step — is bounded by `vMax`, so its
worst case (1.39 m) is still smaller than a vehicle's *routine* braking error.

#### Does the walkingarea Bezier break interpolation? **No — measured, and this corrects §10.2.**

The concern was that on a walkingarea `myRelX` runs along a Bezier `WalkingAreaPath`, not the lane
centreline, so chord interpolation between published frames would cut the corner. Measured by running
at `--step-length 0.5`, treating whole seconds as the published frames and the `.5` sample as truth:

```
MID-STEP CHORD-INTERPOLATION ERROR, metres
  vehicle                                   median 0.0000  p95 0.3901  p99 0.5625  max 1.6621
  ped normal edge                           median 0.0000  p95 0.2936  p99 0.3874  max 0.5915
  ped crossing                              median 0.0001  p95 0.3200  p99 0.4464  max 0.7255
  ped WALKINGAREA                           median 0.0000  p95 0.1749  p99 0.3790  max 1.2874
```

The walkingarea case has the **lowest p95 of all four**. A pedestrian covers at most 1.39 m per step,
over which the path's curvature is negligible. The earlier claim was a mechanism reasoned from the
source and never measured — precisely the failure mode CLAUDE.md §Measurement discipline item 2 exists
to catch, and it survived a commit before this check caught it.

**Consequence:** reuse the vehicle interpolation path. `DrModel`'s existing three members
(`LaneArc` / `FreeKinematic` / `Stationary`) are sufficient — its own header already records that they
were "confirmed sufficient at three members". Tag a person `FreeKinematic` (world position + velocity)
and nothing more is needed; that is what the 0.175 m p95 above measures. `LaneArc` would additionally
require publishing the `WalkingAreaPath` geometry, which the net file does not contain — the model
computes it — and the measurement says that buys nothing.

#### Does SUMO itself use different APIs for persons and vehicles? **Internally no, externally yes.**

- **Internally SUMO unifies.** `MSTransportable : SUMOTrafficObject` (`MSTransportable.h:59`) and
  `SUMOVehicle : SUMOTrafficObject` (`SUMOVehicle.h:60`). `SUMOTrafficObject` is not a marker — it is a
  ~30-method interface covering `getEdge`, `getLane`, `getPositionOnLane`, `getSpeed`,
  `getAcceleration`, `getPosition`, `getAngle`, `getWaitingTime`, `getVehicleType`, `getVClass`,
  `getRNG`, `isStopped`, `hasArrived`, … plus `isVehicle()`/`isPerson()` discriminators. Persons even
  reuse **`MSVehicleType`** for their dimensions. And SUMO uses this base exactly where cross-type code
  needs it: `blockedAtDist(const SUMOTrafficObject* ego, …)` and `MSLink::ignoreFoe`.
- **Externally SUMO does not.** libsumo/TraCI ships separate `Person` and `Vehicle` domains
  (`libsumo/Person.h`, `libsumo/Vehicle.h`) with no shared "TrafficObject" domain.

**Do we?** Today, neither: `Engine`/`VehicleHandle` and `PedestrianWorld`/`int id` share nothing at all
(§10.3) — we are *less* unified than SUMO at both levels.

#### Can we unify too? **Yes, and SUMO tells us exactly where the seam belongs.**

Adopt SUMO's own split: **unify the read shape, keep the domains separate.**

| level | SUMO | us (proposed) |
| --- | --- | --- |
| per-object accessors | unified (`SUMOTrafficObject`) | `PersonState` mirrors `VehicleState` field-for-field where the meaning matches; a small read-only interface over both if a host wants to be generic |
| dimensions / type | unified (persons use `MSVehicleType`) | `PersonTypeParams` mirrors `VTypeParams`; may share a backing record |
| dead reckoning | n/a (SUMO has no DR layer) | **share it** — `DrModel` unchanged, one interpolation path, measured above |
| identity | separate (`MSPerson*` vs `MSVehicle*`) | separate handle id spaces — as already decided |
| spawn / control API | separate TraCI domains | separate `SpawnPerson` / `SpawnVehicle` |
| step scheduling | separate (`MovePedestrians` is its own event) | separate (`AdvancePersons`, §5.1) |

So the unification that pays is the **DR/replication layer and the read shape** — precisely the parts
where the measurement shows persons and vehicles behave the same. The parts SUMO keeps separate
(identity, spawn, scheduling) are the parts where they genuinely differ, and we should keep those
separate too. That is a stronger argument for the §10.3 decision than the one I originally gave: it is
not just "don't risk the gate", it is "SUMO, having had 20 years to unify, drew the line in the same
place".

### 10.4 The resulting surface

```csharp
public readonly struct PersonHandle { public uint Index; public ushort Generation; }

PersonTypeHandle DefinePersonType(in PersonTypeParams p);   // width, length, minGap, desiredMaxSpeed,
                                                            // speedDev, jmCrossingGap, impatience
PersonHandle SpawnPerson(PersonTypeHandle t, ReadOnlySpan<int> routeEdges,
                         double departPos, double departPosLat, long externalId = 0);
PersonHandle SpawnPersonAt(PersonTypeHandle t, int edge, double pos, double posLat,
                           double speed, ReadOnlySpan<int> rest, long externalId = 0);

// The Phase-2 hinge (SS9.1b/c) AND the L2 replay harness's re-seed entry point -- one function,
// so the two can never drift. `externalId` is the cross-tier id (SS9.1d): 0 means "none".
bool TryAdoptAt(in PersonAdoptionState st, out PersonHandle p);
readonly record struct PersonAdoptionState(
    PersonTypeHandle Type, int Edge, double Pos, double PosLat,
    double Speed, double SpeedLat, int Dir,             // Dir from the incoming velocity
    double WaitingTime, bool WaitingToEnter, bool AmJammed,   // SS9.1c fixes the promotion defaults
    long ExternalId);                                   // NLI / walkingAreaPath are recomputed
void         Despawn(PersonHandle p);
PersonLifecycle GetLifecycle(PersonHandle p);                          // Pending / Active / Arrived
ActivePersonQuery ActivePersons();                                     // zero-alloc struct enumerator
bool TryGetPerson(PersonHandle p, out PersonState s);

ReadOnlySpan<PersonHandle> PersonHandles { get; }                      // SoA read projection,
ReadOnlySpan<float> PersonPosX { get; } /* ...Y, Z, Angle, Speed */    //   populated by Step()
ReadOnlySpan<int>   PersonEdgeHandles { get; }
ReadOnlySpan<double> PersonPos { get; }  ReadOnlySpan<double> PersonPosLat { get; }
ReadOnlySpan<PersonEvent> PersonEvents { get; }

(double X, double Y) WorldOf(int edge, double pos, double posLat);                          // SS9.1
bool TryResolveToEdge(double x, double y, out int edge, out double pos, out double posLat); // SS9.1
```

Documented in `docs/SUMOSHARP-API.md` as a new section in the same style as §5 (read API), §9 (runtime
spawn) and §10 (lifecycle events).

## 11. Visualization — extend `Sim.Viz`, no new renderer

`Sim.Viz/Payload.cs:66` already carries `d = discs as [x, y, radius, kind]` with `kind: 2 = pedestrian`,
and `:177` draws crosswalk and ped-signal markers. Additions:

- A `--sumoped-<scene>` family in `Sim.Viz/Program.cs`, one per committed scenario, registered in
  `scripts/gen-demos.sh` under a new **SUMO pedestrians** category.
- **A parity overlay mode.** The scene loads the scenario's `golden.fcd.xml` alongside the live run and
  draws the SUMO ped as a **ground-truth ring** at the same timestep, exactly as
  `Sim.Viewer/RemotePedOverlay.cs` does for the replication proof. A divergence becomes visible in one
  frame rather than surfacing as an RMSE number.
- Stripe lines drawn on crossings/walkingareas in debug scenes, so R3a/R3b are *inspectable* and not just
  asserted.

This is the development loop the owner asked for: run, diff, look.

---

## 12. Determinism and parity argument

**Why the vehicle gate cannot move.** `Engine.Persons` is `null` unless a host attaches a model.
No committed scenario under `scenarios/` (non-`_sumoped`) has person demand. The bench hash
(`Sim.Bench/Program.cs:122`) folds only vehicle `(id, time, lane, pos, speed)` and has no visibility into
persons at all. The only way to move it is to let a person write into vehicle state — which happens at
exactly one place, §6.1's phantom-leader injection, and is gated on `Persons != null`.

*Verification, not assertion:* run the full gate before and after and diff. `782/5` with 661 goldens
byte-identical, `Sim.Bench` `A134ED3716DDE7BC` (par == single), `Sim.LiveCity.Tests` 92/92,
`Sim.Pedestrians.Tests` 324/324.

**Person determinism.** No `System.Random`. `speedFactor` is a once-at-creation draw from a
`VehicleRng`-shaped per-entity stream with its own salt constant, distinct from every vehicle stream.

**The one named deviation.** SUMO's per-step `dawdling` draw comes from a single process-global stream in
`by_xpos_sorter` order across a FORWARD-then-BACKWARD pass. Reproducing that bit-for-bit requires
serializing draw order, which CLAUDE.md forbids. Phase 1 therefore:
- pins `dawdling = 0` in every committed golden ⇒ the deviation is **unreachable** on the parity path;
- implements dawdling with a per-entity seeded stream for the production regime ⇒ same distribution, same
  magnitude, different sequence, no bit-exact claim;
- documents this in `docs/ENV-GATES.md`-adjacent form and in the scenario `README`.

**Parallelism.** `MoveInDirectionOnLane` threads a rolling `obs` array through peds in sorted order — a
genuine sequential dependency within one lane-direction bucket. Phase 1 runs the person pass
**single-threaded**; buckets are independent across lanes, so a later parallelization over lanes is
available, but it is not attempted here and the design does not depend on it.

---

## 13. Risks, ranked, with the mitigation each gets

| # | Risk | Mitigation |
| --- | --- | --- |
| 1 | `WalkingAreaPath` geometry (Bezier smoothing, reverse-path aliasing, per-path vehicle-foe injection) is the hardest single item and is hard to unit-test in isolation | Land it against `walk-junction-turn` (no vehicles) before any vehicle coupling exists, so a divergence has exactly one possible cause |
| 2 | The `Walk()` fold order is non-commutative and a subtly wrong order still "looks fine" | `StripingWalk.cs` is engine-free and gets a hand-built-`Obstacle[]` unit suite (SP-4.1) **before** integration |
| 3 | Begin-of-timestep ordering vs the TLS switch (§5.1) | Resolve by **trace against SUMO**, not by reading the event-queue code — CLAUDE.md item 2 |
| 4 | The junction ped router changes which crossing a ped picks; a wrong cost function is invisible on a 1-crossing net | A scenario with ≥2 viable crossings, so the choice is observable |
| 5 | Phantom-leader injection touches the vehicle plan path and could move goldens | Gated on `Persons != null`; the gate re-run is a per-stage success condition, not just a final one |
| 6 | `BlockedAtDist`'s 2 s standing-ped clause: get it wrong and you get plausible gridlock | It is an explicit assertion in the `xwalk-priority-1v1` trace test, not just an emergent outcome |
| 7 | Scope creep into merging the three net readers | R-N4; the equivalence test in §3.1 is the guard |

---

## Appendix A — the §2.1 fixture, verbatim

The VM is volatile and this fixture was built by hand; it is recorded here so SP-0.2 does not have to
rediscover it. This is the exact input that produced the §2.1 trace.

`nodes.nod.xml`
```xml
<nodes>
    <node id="c" x="0"   y="0"   type="priority"/>
    <node id="w" x="-60" y="0"   type="priority"/>
    <node id="e" x="60"  y="0"   type="priority"/>
    <node id="n" x="0"   y="60"  type="priority"/>
    <node id="s" x="0"   y="-60" type="priority"/>
</nodes>
```

`edges.edg.xml` — all eight directions are needed; with only the four "through" edges,
`--crossings.guess` produces 2 crossings instead of 4 and the N→S walk has nothing to cross.
```xml
<edges>
    <edge id="wc" from="w" to="c" numLanes="1" speed="13.89"/>
    <edge id="cw" from="c" to="w" numLanes="1" speed="13.89"/>
    <edge id="ec" from="e" to="c" numLanes="1" speed="13.89"/>
    <edge id="ce" from="c" to="e" numLanes="1" speed="13.89"/>
    <edge id="nc" from="n" to="c" numLanes="1" speed="13.89"/>
    <edge id="cn" from="c" to="n" numLanes="1" speed="13.89"/>
    <edge id="sc" from="s" to="c" numLanes="1" speed="13.89"/>
    <edge id="cs" from="c" to="s" numLanes="1" speed="13.89"/>
</edges>
```

`rou.rou.xml`
```xml
<routes>
  <vType id="car" vClass="passenger" sigma="0" tau="1" length="5" accel="2.6" decel="4.5" maxSpeed="13.89"/>
  <vType id="ped" vClass="pedestrian" speedDev="0" speedFactor="1"/>
  <vehicle id="v0" type="car" depart="0" departSpeed="max" departLane="best">
    <route edges="wc ce"/>
  </vehicle>
  <person id="p0" type="ped" depart="0"><walk from="cn" to="cs"/></person>
</routes>
```

Yields crossings `:c_c0..:c_c3` over `cn nc` / `ce ec` / `cs sc` / `cw wc` and walkingareas
`:c_w0..:c_w3`. The ped's realised path is `cn → :c_w1 → :c_c1 → :c_w2 → :c_c2 → :c_w3 → cs`.

Note the demand uses `<walk from= to=>`, which invokes SUMO's intermodal router at insertion. SP-0.2
should convert this to an explicit `<walk edges="cn cs"/>` (normal edges only — the striping model
inserts the walkingareas and crossings itself, §5.5) so the committed input does not depend on the
router's route choice, only the model's junction-local one.


---

## Appendix B — the Tier C saturated fixture, verbatim

Same `nodes.nod.xml` as Appendix A but with `<node id="c" ... type="traffic_light"/>`, and
`numLanes="2"` on all eight edges. Regenerate with:

```bash
netconvert --node-files=nodes.nod.xml --edge-files=edges.edg.xml \
           --sidewalks.guess --crossings.guess --tls.guess --no-turnarounds \
           --output-file=net.net.xml
# crossing width axis (coverage §4): --default.crossing-width 0.64  (1 stripe)
#                                    --default.crossing-width 8.00  (12 stripes)
```
Yields four crossings at width 4.00 m (**6 stripes**) and length 12.80 m.

`rou.rou.xml` — saturated variant (460 persons loaded, steady state from t≈80, ~110–140 walking):
```xml
<routes>
  <vType id="car" vClass="passenger" sigma="0" tau="1" length="5" accel="2.6" decel="4.5" maxSpeed="13.89"/>
  <vType id="ped" vClass="pedestrian" speedDev="0" speedFactor="1"/>
  <flow id="fwe" type="car" begin="0" end="300" period="3" from="wc" to="ce" departLane="best" departSpeed="max"/>
  <flow id="few" type="car" begin="0" end="300" period="3" from="ec" to="cw" departLane="best" departSpeed="max"/>
  <flow id="fns" type="car" begin="0" end="300" period="4" from="nc" to="cs" departLane="best" departSpeed="max"/>
  <flow id="fsn" type="car" begin="0" end="300" period="4" from="sc" to="cn" departLane="best" departSpeed="max"/>
  <personFlow id="pns"   type="ped" begin="0" end="300" period="2"><walk from="cn" to="cs"/></personFlow>
  <personFlow id="psn"   type="ped" begin="0" end="300" period="2"><walk from="cs" to="cn"/></personFlow>
  <personFlow id="pew"   type="ped" begin="0" end="300" period="3"><walk from="ce" to="cw"/></personFlow>
  <personFlow id="ppass" type="ped" begin="0" end="300" period="5"><walk from="cn" to="cn"/></personFlow>
</routes>
```
`ppass` (`from="cn" to="cn"`) is the **pass-by** flow for R3d — peds who reach the junction and turn
back without crossing.

Jam-regime variant: drop to two car flows at `period="2"` and two personFlows at `period="0.5"`.
Measured: `<persons loaded="1200" running="365" jammed="175"/>`, 80 `<collision>` records.

Generation command (honest-SUMO flags + the full person output set). ⚠ **`angle` must be in the
attribute list** — R2 makes it a compared attribute and it is the *only* lateral-velocity witness person
FCD carries (§8, SP-2.1c). An earlier version of this recipe masked it out, contradicting §8.2; the
1.45 MB Tier C footprint in `SUMOPED-COVERAGE.md` §3 was measured against that narrower list and is now a
lower bound, to be re-measured at SP-0.2b.
```bash
sumo -n net.net.xml -r rou.rou.xml --begin 0 --end 300 --step-length 1 \
  --pedestrian.model striping --pedestrian.striping.dawdling 0 \
  --time-to-teleport -1 --collision.action warn --collision.check-junctions true \
  --fcd-output golden.fcd.xml --fcd-output.attributes id,x,y,angle,speed,pos,edge,slope --precision 4 \
  --device.fcd.begin 200 \
  --person-summary-output golden.personsummary.xml \
  --personinfo-output     golden.personinfo.xml \
  --statistic-output      golden.statistic.xml \
  --collision-output      golden.collisions.xml \
  --no-step-log true 2> golden.warnings.txt
```
