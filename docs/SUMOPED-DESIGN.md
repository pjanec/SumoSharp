# SUMOPED — Design (the HOW)

**Status: PROPOSAL — awaiting owner sign-off. No implementation has started.**

The WHAT is `SUMOPED-REQUIREMENTS.md`; this document does not restate it. **What the model does and
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
  elements. Unlike the vehicle harness there is no init cross-check; the vType cross-check role is taken
  by the `walk-straight-1` scenario instead.

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

### 4.1 Storage

`List<PersonRuntime>` of a mutable class, indexed by a stable `EntityIndex`, plus
`SortedDictionary<int laneNumericalId, List<PersonRuntime>> _activeLanes` mirroring SUMO's `myActiveLanes`.

This deliberately mirrors what vehicles actually are today (`List<VehicleRuntime>`, `Engine.cs:23`) rather
than the aspirational SoA in CLAUDE.md's framing — persons should not be more ECS than vehicles are.

`PersonRuntime`'s field set is fixed by SUMO's own `saveState` enumeration
(`MSPModel_Striping.cpp:1765`), which is the authoritative list of everything that must be reproducible:
`myLane, myRelX, myRelY, myDir, mySpeed, mySpeedLat, myWaitingToEnter, myWaitingTime,
walkingAreaPath(from,to), myAmJammed, myNLI(lane, link.from, link.to, dir)`.

---

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

**The port must do the same**: inject a phantom `JunctionLeaderCandidate` (`Engine.cs:9988`) with a null
vehicle into the existing junction-leader path consumed by `AdaptToJunctionLeader`.

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

Needs one change: `_sumoped` scenarios must be regenerated with their own `config.sumocfg` carrying
`--pedestrian.model striping` and `--pedestrian.striping.dawdling 0`. Since those live in the config, the
script's generic walk already picks them up; the script change is only to ensure `_sumoped` is included
in the walk and that person-FCD attributes are not masked out by an `--fcd-output.attributes` list.

---

## 9. The Phase 2 seam (build the hinge, not the door)

Phase 2 (Requirement R-N2) promotes a low-power ORCA ped to a SUMO-model ped near a crossing and demotes
it afterwards. Phase 1 must not build it, but must not foreclose it. Two things suffice:

1. **A bidirectional coordinate contract**, `(edge, pos, posLat) ↔ (x, y)`, exposed as a pure public
   function on the person model. The forward direction is already required for FCD output; the inverse
   (world → nearest walkable edge + offset) is the piece the ORCA layer lacks — it is exactly the
   "world→edge resolver" that `PersonFcdWriter.cs:14-16` defers as backlog item P8-5. Phase 1 should
   build the inverse anyway (it is small, and it makes `PersonFcdWriter` complete as a side effect).
2. **Spawn/despawn at an arbitrary `(edge, pos, posLat, speed)`**, not only at a route start — so a
   promotion can hand over mid-stride with no pop. This is the `SpawnPerson` overload set in §10.

Nothing else about Phase 2 is decided here.

---

## 10. Public API — readiness assessment and the exact delta

**Verdict: the API is well prepared for a second agent type on the `Engine`, and not at all prepared
for unifying with the ORCA pedestrian layer.** Nothing blocks the port. The work is ~6 new small types
plus additive edits to 3 existing files, and **no edit to any existing vehicle type** — so the 782-test
gate structurally cannot move. There is exactly one real design decision (§10.3).

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
| 6 | `SimulationRunner.TryInterpolatePerson` | edit | Persons need their **own** DR model: they do not follow a lane arc the way a vehicle does, and on a walkingarea they follow a Bezier `WalkingAreaPath` (§3.2). Do not reuse `TryInterpolateVehicle`'s extrapolator. |
| 7 | `Sim.Host/ReplicationPublisher` | edit or route | ⚠ It has **zero** occurrences of "person"/"ped" — it is vehicle-only. See §10.3. |
| 8 | `DefinePersonType`, `SpawnPerson`, `SpawnPersonAt`, `Despawn(PersonHandle)`, `ActivePersons()` | edit `Engine` | `SpawnPersonAt(type, edge, pos, posLat, speed, rest)` is the Phase-2 hinge (§9.2) |

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

**The cost of this decision, named up front:** Phase 2's LOD hybrid becomes a bridge between two
*different* APIs (`PersonHandle`/lane-relative ↔ `int id`/`Vec2`), not a promotion within one. §9's
coordinate contract is exactly that bridge, which is why Phase 1 must build it even though Phase 1 does
not use it.

### 10.4 The resulting surface

```csharp
public readonly struct PersonHandle { public uint Index; public ushort Generation; }

PersonTypeHandle DefinePersonType(in PersonTypeParams p);   // width, length, minGap, desiredMaxSpeed,
                                                            // speedDev, jmCrossingGap, impatience
PersonHandle SpawnPerson(PersonTypeHandle t, ReadOnlySpan<int> routeEdges,
                         double departPos, double departPosLat);
PersonHandle SpawnPersonAt(PersonTypeHandle t, int edge, double pos, double posLat,
                           double speed, ReadOnlySpan<int> rest);      // Phase-2 hinge, SS9.2
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

Generation command (honest-SUMO flags + the full person output set):
```bash
sumo -n net.net.xml -r rou.rou.xml --begin 0 --end 300 --step-length 1 \
  --pedestrian.model striping --pedestrian.striping.dawdling 0 \
  --time-to-teleport -1 --collision.action warn --collision.check-junctions true \
  --fcd-output golden.fcd.xml --fcd-output.attributes id,x,y,speed,pos,edge --precision 4 \
  --device.fcd.begin 200 \
  --person-summary-output golden.personsummary.xml \
  --personinfo-output     golden.personinfo.xml \
  --statistic-output      golden.statistic.xml \
  --collision-output      golden.collisions.xml \
  --no-step-log true 2> golden.warnings.txt
```
