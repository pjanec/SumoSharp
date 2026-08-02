# PEDCROSS — how to get believable pedestrians at crossings: options, evidence, and what to measure next

**Status: OPTIONS ANALYSIS. No decision. The owner is leaning ORCA-first and has said explicitly
that we need to go deeper before deciding.** This document exists so the deeper look starts from
evidence rather than from re-derivation.

Companion to the SUMO-port doc set (`SUMOPED-*.md`), which is now **conditional on this decision** — it
remains a complete, reviewed plan, and §7 explains why it keeps its value whichever way this goes.

---

## 1. The acceptance criteria — believability, not parity

The owner's words, 2026-08-02:

> *"My ultimate goal is believability in the high realism zones. Cars must not go through peds and vice
> versa. Peds should not overlap much (unless in a very dense crowd). Cars must be able to yield to peds
> if peds are prioritized (so peds need to know how to wait at curb before jumping before the car so
> cars can respond and also cars must know about peds wanting to cross, as well as those who jumped
> there without waiting)."*

Plus, from the same exchange:

> *"current low-power peds switch to ORCA only in high realism zone which covers also sidewalks. I
> usually do not need orca at sidewalks at all… ORCA is only needed at junctions (go through crowd of
> waiting peds) and on crossings (bi-dir streams)."*

> *"Our current solution suffers from ORCA peds sometimes running away completely. Nothing holds them on
> the sidewalk or crosswalk. They are not afraid of jumping into the car lanes just because they need to
> avoid others — unrealistic."*

**Note what is not on this list: matching SUMO.** Every criterion is a believability property. That
matters enormously for the comparison below, because it means SUMO parity is a *means*, not the goal —
and it can be judged on whether it delivers these five properties.

Restated as testable criteria:

| # | criterion |
| --- | --- |
| **C1** | a car never passes through, or close-and-fast past, a pedestrian |
| **C2** | pedestrians do not interpenetrate, except in genuinely dense crowds — ⚠ **weak criterion**: the owner has since said overlaps at density are acceptable, so this is *not* an argument against either option. Recorded so it is not re-used as one. |
| **C3** | a ped waits at the kerb rather than stepping in front of a car |
| **C4** | a car sees a ped *intending* to cross, early enough to yield smoothly |
| **C5** | a car sees a ped who stepped out *without* waiting |
| **C6** | pedestrians stay on walkable ground — never in a traffic lane to dodge another ped |

C6 is the owner's runaway complaint, promoted to a first-class criterion because it is believability-fatal.

---

## 2. What already exists — measured, not assumed

Read from the code this session. This is the part most likely to be misremembered, in both directions.

### 2.1 The car side is largely BUILT, and it is stronger than SUMO's

`Engine.cs:11437`, `CrowdYieldConstraint` (binder 16), from `docs/LIVE-CITY-CAR-YIELDS-PED-DESIGN.md`:

> *"the term that makes 'a car never passes a ped at close distance and high speed' a **GUARANTEE**
> rather than an emergent behaviour"*

It has two terms from one sweep:

- **(a) anticipatory in-path yield** — the ped's lateral track is projected forward to ego's arrival
  (`predLat = latOff + latVel*tte`) and tested against a corridor around the **lane centre**, not ego's
  current offset. That is **C4**: yielding to where the ped *will be*. Its header records that binder 13
  alone fires *zero* times on three pinned geometries where an unguarded car holds 5.00 m/s straight
  through a crossing.
- **(b) a world-space proximity cap** — exact rectangle-to-disc clearance in ego's body frame, capping
  ego at a creep speed within a near distance. The hard backstop: **a close pass is a slow pass**.

Plus `CrowdLongitudinalConstraint` (binder 13, reactive, **C5**) and `CrossingOccupancySource`, which
makes cars brake for **low-power, un-promoted** peds on a crosswalk with an O(1) empty fast path.

So **C1, C4 and C5 are already designed, implemented and tracked.**

### 2.2 What is genuinely missing: the ped-side decision (C3)

`ICrossingSignal` is:

```csharp
bool WalkAllowed(double now);
```

A **pure function of the clock**. `CrossingSignalFactory` falls back to `AlwaysWalkSignal` for an
unsignalized crossing, and its own comment says such a crossing *"should never hold pedestrians."*

So at an uncontrolled crossing the ORCA ped **walks straight out and relies on the car braking**. C3 is
not unimplemented — it is currently *inexpressible*, because the interface has no traffic input to
condition on. Grepping both crossing-yield design docs for gap acceptance or kerb waiting returns
nothing: they are entirely car-side.

### 2.3 ⚠ C6 (runaways) looks like a WIRING GAP, not an ORCA limitation

This is the most consequential finding in this document, and it corrects an earlier, more pessimistic
reading of the same problem.

Everything needed for containment already exists:

| piece | where | state |
| --- | --- | --- |
| RVO2 static-obstacle constraint | `OrcaSolver.ComputeObstacleLines`, separate `timeHorizonObst` | implemented |
| obstacle geometry intake | `OrcaCrowd.AddObstacle(IReadOnlyList<Vec2>)`, with a lazily-rebuilt index | implemented |
| **obstacle lines survive the dense fallback** | `OrcaSolver.cs:164-166` — *"no velocity satisfies every constraint, so minimise the maximum penetration instead of failing hard"*, and `LinearProgram3` is passed `numObstLines` so it relaxes **agent–agent** lines preferentially and holds the obstacle ones | implemented |
| baked walkable polygons | `WalkablePolygonBaker`, `SumoWalkableSpace`, `BakedPolygon`, `NavmeshReachability` | implemented |
| **a subsystem already using it as a hard boundary** | `Sim.Evac/EvacDirector.cs:95` — `_peds.AddObstacle(_navmesh.BoundaryLoop); // R7 hard outer edge` | **shipped, against a stated requirement** |

And the gap: **`PedLodManager` never calls `AddObstacle`.** The high-power `OrcaCrowd` that the LOD
ladder promotes pedestrians into is constructed with **no static obstacles at all**. Nothing bounds the
walkable area, so under crowd pressure the reciprocal solve is free to push an agent into a traffic lane
— exactly the reported symptom, and exactly what you would predict from an unbounded solve.

**⚠ MEASURED — and the hypothesis is only half right.** `tests/Sim.Pedestrians.Tests/Navigation/
OrcaWalkableContainmentTests.cs`, on `poc0-crossing-plaza`, agents driven by the real
`PedRouteController` + `WaypointFollower` over `SumoNavMesh` routes (the same pair `PedLodManager.cs:220`
uses), 900 steps at dt = 0.1:

```
n= 20   off-walkable 0.000 %   depth p95 —        max —        routesCompleted 20.0 %
n= 40   off-walkable 0.000 %   depth p95 —        max —        routesCompleted 45.0 %
n= 80   off-walkable 1.094 %   depth p95 0.927 m  max 0.973 m  routesCompleted 40.0 %
n=160   off-walkable 1.361 %   depth p95 0.560 m  max 0.797 m  routesCompleted 42.5 %
n=240   off-walkable 2.619 %   depth p95 0.776 m  max 1.122 m  routesCompleted 30.0 %
```

**The runaway is real, it is density-triggered, and it is deep.** Nothing at all below ~40 agents; it
appears at 80 and grows monotonically. **Max excursion 1.12 m** — that is not a clipped kerb corner, it
is a pedestrian standing in a traffic lane. The owner's report is confirmed with a number.

**But the fix is NOT one wiring call.** Two naive union-boundary constructions, both measured:

| boundary | off-walkable | routes completed |
| --- | --- | --- |
| none (what ships today) | 0.00 % @n=40 · 2.62 % @n=240 | 45.0 % |
| **endpoint-dedupe** (drop edges seen from both sides) | **0.00 %** | **0.0 %** ← every route blocked |
| midpoint-filtered (drop edges whose midpoint is inside another polygon) | 2.89 % | 22.5 % |

Endpoint dedupe contains *perfectly* and walls off **every** portal — not one agent completes its route.
That is exactly the failure `SumoWalkableSpace`'s header predicts. The midpoint filter over-corrects,
deleting genuine outer walls too, because the bake produces **overlapping** polygons (a buffered sidewalk
strip overlaps the walkingarea it feeds) rather than merely abutting ones — so "midpoint inside another
polygon" is true of real boundary edges as well. 138 raw edges → 74 after dedupe → 23 after the midpoint
filter; the truth is somewhere between and neither construction finds it.

**So the actual work item is a real polygon-union boundary** over overlapping polygons — the "future
work" the header names, and materially harder than a call to `AddObstacle`. It is still far smaller than
the SUMO port, but it is geometry work with a correctness bar, not wiring.

⚠ **Methodology note, recorded because it flipped the result.** The first version of this harness gave
agents straight-line `SetGoal` targets with no router. They walked over the carriageway *because that is
where they were aimed*, reporting a spurious **37%** off-walkable, and adding walls "fixed" it purely by
stopping them dead. That is CLAUDE.md §Measurement discipline item 6 — a metric that selects its own
answer — and it would have fed a confident, wrong number into this decision. The numbers above are from
the corrected, route-following harness.

**Honest caveats, so this is not oversold:**
- RVO2 obstacle handling is *strong*, not *absolute*. An agent already on the wrong side of a line, or a
  concave corner, can still misbehave; the dense fallback minimises penetration rather than forbidding it.
  The lattice's containment is of a different kind — a SUMO ped **cannot** be off-lane because there is
  no coordinate in which to express it.
- Feeding every sidewalk edge in a city as obstacle lines has a cost and needs the spatial index to be on.
- This is a hypothesis from reading, and this repo's own history (CLAUDE.md §Measurement discipline
  item 2) says reasoned mechanisms have a bad track record here. **Test it before believing it.**

### 2.4 The LOD trigger is the wrong shape

`PedLodManager` promotes on `InterestField` radius: a ped is high-power iff its position is inside any
`InterestSource.PromoteRadius`, demoting after `dwellSeconds` continuously outside every `DemoteRadius`.
The interest sources today are realism zones — which cover **sidewalks**, where the owner says ORCA is
not needed at all: density is low and nothing accumulates.

The expensive solver is therefore running where it buys nothing. What is wanted is promotion keyed on
**geometry** — junction walkingareas and crossings — intersected with the high-realism zone, since
elsewhere the "low-power ped occupies the crosswalk and blocks cars" path (`CrossingOccupancySource`) is
already enough.

`InterestField` supports multi-source with stable ids (`Register`/`Move`/`Remove`), so registering one
source per junction/crossing is a **policy change, not new machinery**. The one real extension is that
sources are radii and crossings are elongated rectangles — a circle fits a 12.8 m crossing badly. Either
accept a generous circle or add a polygon/OBB source shape.

This is worth doing **regardless of which option wins**: it cuts high-power population, and it makes the
"where does the expensive model run" question explicit instead of incidental.

---

## 3. The decisive asymmetry: a faithful SUMO port FAILS C1 and C2

Measured this session on the jam-regime Tier C fixture:

- **80 `<collision>` records, 29 distinct (collider, victim) pairs.** Vanilla SUMO drives cars through
  pedestrians at density, by design.
- The mechanism: past `jamTimeCrossing` a ped latches `myAmJammed` and squeezes at `vMax/4` **ignoring
  collision gating** (`MSPModel_Striping.cpp:2200-2215`).
- A jammed ped is also **invisible to other pedestrians** — the guard `!p.myWaitingToEnter &&
  !p.myAmJammed` appears in both obstacle sources (`:777`, `:1291`), so jammed peds walk through each
  other. That is **C2 failing too**.

This is why R5 had to be restated from *"cars never cross a ped"* to parity-with-SUMO's-collision-set.

**So a faithful port delivers parity with a model that violates C1 and C2 — the two criteria the owner
listed first — and we would then have to deviate (R5c) to get back a guarantee binder 16 already
provides.** Porting faithfully and then deliberately un-porting the part that matters most is a strange
place to spend months.

---

## 4. Scoring

| criterion | ORCA today | ORCA + the additions in §5 | faithful SUMO port |
| --- | --- | --- | --- |
| **C1** car never hits / close-fast-passes a ped | **guaranteed** (binder 16) | guaranteed | **fails at density** (80 collisions, by design) |
| **C2** peds don't interpenetrate | reciprocal avoidance | reciprocal avoidance | worse — but **acceptable per the owner**; not a deciding factor |
| **C3** ped waits at kerb for a gap | ❌ inexpressible | ✅ new `ICrossingSignal` impl | ✅ native |
| **C4** car sees intent to cross | **built** (anticipatory corridor) | built | `blockedAtDist` + `jmCrossingGap` |
| **C5** car sees a ped who jumped out | **built** | built | yes |
| **C6** peds stay on walkable ground | ❌ **unwired** (§2.3) | likely fixed by wiring | ✅ **structural** — no coordinate exists for off-lane |
| falsifiable against an oracle | no | statistical only | **byte-exact** |
| open-space / plaza behaviour | good | good | poor (lattice only) |

Four of six criteria are already met by what is on disk. One is a wiring gap. One needs new work.

---

## 5. Option A — ORCA plus the missing pieces

**Work:**

1. **`GapAcceptanceSignal : ICrossingSignal`** — the same one-method seam, consulting approaching
   vehicles instead of only the clock. All the queueing, hold slots, containment and release-surge
   machinery behind that interface (`CrossingGate`) is already built and tested. Delivers **C3**.
2. **Curb-intent band in `CrossingOccupancySource`** — widen the trigger from "inside the crossing
   polygon" to include a kerb approach zone, so cars start braking as early as SUMO's do (in the §2.1
   oracle trace the car brakes at t=3 while the ped is still at `pos 0.08` on the walkingarea).
   Sharpens **C4** for the low-power path.
3. **Wire the walkable boundary into `PedLodManager`'s high-power crowd** (§2.3). Delivers **C6** if the
   hypothesis holds.
4. **Re-key the interest field on junction/crossing geometry** (§2.4).

**Cost:** roughly one new class plus a vehicle-approach query, one polygon widening, one wiring call,
one policy change — plus calibration. Weeks.

**The three real risks, in descending order of uncertainty:**

- **R-A1 — mid-crossing abort.** A ped commits, then a car appears. SUMO's per-step obstacle fold
  handles it naturally. In ORCA you move the goal, and `CrossingGate`'s measured reciprocal-shove
  problem reappears **mid-road with no kerb to buffer against**. This is the one outcome I cannot
  predict, and it should be prototyped first.
- **R-A2 — you must invent your own deadlock escape.** Gap acceptance deadlocks; that is *why* SUMO has
  the jam latch. SUMO's answer (squeeze through ignoring collisions) is exactly what C1/C2 forbid, so
  it cannot be copied. The likely shape is rising impatience that lowers the accepted gap until the ped
  steps to the kerb edge and the **car** yields — which binder 16 already guarantees. That is arguably
  *more* believable than SUMO's answer, but it is design work, not porting.
- **R-A3 — no oracle, so calibration is judgement.** Mitigated by §7.

---

## 5b. Option D — junction-scoped ORCA islands (the owner's proposal, and the current front-runner)

Proposed by the owner, 2026-08-02:

> *"switching from sidewalk low-power non-orca peds to junction-scoped orca peds for each junction that
> falls into the high realism zone(s) and keeping low-power peds and low-ped-lockable-crosswalks
> everywhere else; sidewalks do not really need ORCA, junctions only in high realism zones, and
> low-power peds should never be hit by car as long as they cross the street on crosswalk only."*

This is structurally stronger than Option A as written above, for a reason worth spelling out.

### 5b.1 It shrinks the containment problem from a city to a polygon

A low-power ped's pose is `PathArcMotion.PositionAt(path, startTime, speed, now)` — **a pure function**.
Arc length is `speed · max(0, now − startTime)`, clamped at the final vertex. There is no solver, no
force, no velocity negotiation. **A low-power ped cannot run away**: there is no mechanism by which it
could leave its path.

So C6 is not a property to be enforced over the whole city — it is automatically true everywhere except
*inside the ORCA islands*. And an island is one junction: a walkingarea plus its crossings. Small,
closed, and roughly convex — which is where RVO2's static-obstacle handling is at its most reliable, and
where feeding the boundary via `AddObstacle` is a handful of vertices rather than a city's worth of
sidewalk edges.

That reframes §2.3's finding. The question is no longer "can ORCA be kept on the pavement in general"
but the much narrower "can ORCA be kept inside one junction polygon" — and Q1 gets correspondingly
cheaper and more likely to succeed.

### 5b.2 Gap acceptance belongs on the LOW-POWER tier, where it is a schedule, not a steering decision

This is the part that makes the architecture cheap, and it is the opposite of where Option A put it.

`ActivityTimeline` already has a **`Pause`** segment kind: *"Stop in place for `Dur` seconds… Pause
carries NO position of its own — it holds at wherever the timeline reached when it started."*

So "wait at the kerb" on the low-power tier is **a pause inserted in a precomputed timeline** — a
time-warp on a pure function. No solver, no reciprocal shove, no containment risk, and the ped's
position while waiting is exactly the kerb point rather than "wherever the velocity solve left it".
Compare with doing the same thing inside ORCA, where `CrossingGate`'s header documents a measured
0.6–0.7 m overshoot that needed per-agent slots and a two-slot buffer to tame.

⚠ **One real wire implication.** `ActivitySegment` durations are *"fixed at construction time"*, and the
timeline replicates through `ActivityTimelineWire`. A traffic-conditioned kerb wait is **not known in
advance**, so it needs either a re-issued/extended timeline at hold time, or a new open-ended
"hold-until-released" segment kind that the wire and `PedRemoteReconstructor` understand. Small, but it
touches the replication protocol and is exactly the sort of thing that is cheap now and awkward later.

### 5b.3 Paused kerb-waiters can be static obstacles rather than ORCA agents

Follows from 5b.2 and is worth stating on its own, because it removes the failure mode the repo already
measured.

Inside an island, the waiting cluster is a set of peds **paused at fixed positions**. They do not need to
be solver agents at all — they can be static obstacles (or fixed discs). Two consequences:

- **The reciprocal-shove problem disappears for the waiting cluster.** `CrossingGate`'s overshoot exists
  because ORCA splits separating velocity between *both* parties, so an agent that has "arrived" is
  still a negotiating party. A static obstacle is not. The front waiter cannot be pushed into the road
  by the people behind it, structurally rather than by tuning `queueSpacing`.
- **Only the movers cost solver time** — the ped threading through the crowd, and the streams on the
  crossing. That is precisely the subset the owner says needs ORCA.

Care needed in two places: a released waiter must convert back to an agent cleanly (the same
adopt/release churn discussed for the hybrid, so the O(1) add/remove property matters), and a fully
static waiting cluster must not wall off the crossing entrance for the movers — obstacle *lines* would;
fixed discs would not. Prefer discs.

### 5b.4 The safety claim reduces to one checkable routing property

> *"low-power peds should never be hit by car as long as they cross the street on crosswalk only"*

The reasoning is sound and its load is carried entirely by the qualifier. A low-power ped on a crossing
becomes a virtual disc via `CrossingOccupancySource`, so cars brake (binder 13), and binder 16's
anticipatory corridor plus proximity cap make a close-fast pass impossible. That is C1 for low-power peds
**provided the ped is on a crossing when it is in the road**.

So the whole safety argument rests on: **does the baked low-power route graph only ever cross a road
inside a crossing polygon?** If the navmesh ever bakes a sidewalk-to-sidewalk shortcut across a
carriageway, a scripted ped walks into traffic on a pure-function path and no lock fires. That is a
checkable invariant, not an assumption — see **Q5**.

Note also that even with correct routing, a ped that *steps onto* the crossing in front of a car already
too close to stop is unsafe by physics. So the low-power tier needs the 5b.2 kerb pause conditioned on
traffic, not merely a crossing-only route. The two are complementary: routing keeps the ped out of the
carriageway, the pause keeps it off the crossing at the wrong moment.

### 5b.5 One crowd or one per junction?

Not required for correctness either way: `OrcaCrowd`'s spatial hash already means peds at different
junctions are not neighbours, so a single crowd with every island's boundary added is functionally
equivalent and simpler. Per-junction crowds buy **bounded cost per island, natural parallelism, and a
smaller obstacle set per solve**. Treat it as an optimisation decision after Q1/Q2, not an architectural
one.

### 5b.6 What this option still needs from the SUMO analysis

The gap-acceptance *policy* — what gap is acceptable, how impatience grows, when a ped gives up and
forces the car to yield — is exactly what `SUMOPED-ALGORITHM.md` and §7's calibration-oracle approach
supply. This option does not avoid that work; it relocates it from a steering model to a scheduling
model, where it is much easier to implement and to verify.

---

## 6. Option B — the full SUMO port

**Work:** the `SUMOPED-*` doc set. Stages 0–7, ~40 tasks, 148 branch rows, the stripe lattice, the
`WalkingAreaPath` Bezier (its own design's risk #1 and "hardest single item"), a junction-local router,
seven golden comparators, the single-step replay harness, ~30 scenarios across three tiers. Months.

**What it uniquely buys:**

- **Structural containment (C6) of a different kind.** Not "constrained so it does not happen" but "no
  coordinate exists in which it could be expressed." If §2.3's wiring hypothesis fails, this becomes the
  strongest argument in the document.
- **Byte-exact falsifiability.** 30,549 person-steps of reference per Tier C scenario, plus replay that
  localises a divergence to one ped on one lane. Against this repo's history of tuning-driven work, that
  is worth a great deal.
- **A proven deadlock answer** — even though it is the wrong answer for C1/C2.

**What it costs beyond time:** C1 and C2 regress to SUMO's level unless R5c is built too; open-space
behaviour gets worse; and the lattice is a poor fit for plazas.

---

## 7. What we do either way

- **Use SUMO as a *calibration oracle* without porting it.** Run it on the same net; extract
  distributions — accepted gap sizes, kerb wait times, fraction of cars fully stopping, throughput,
  jam counts — and calibrate whatever model we ship to match. Commit those as behavioural goldens. This
  is exactly `CONSTRAINT-high-realism-artefact-ladder.md`'s *target SUMO's flow, never its method*, and
  the tooling already exists (`scripts/sumoped-knob-sweep.py`, `scripts/render-ped-fcd.py`). Weaker than
  byte-exact, but genuinely falsifiable — and it removes most of R-A3.
- **The SUMOPED docs keep their value as a mechanism reference.** `SUMOPED-ALGORITHM.md` §2.4 (how
  avoidance actually works), the measured knob table, `blockedAtDist`'s two clauses including the
  2-second standing-ped rule, `jmCrossingGap`, the jam thresholds, the branch inventory. If we build gap
  acceptance in ORCA, **that is the spec for what it should do.** The port docs become a description of
  the behaviour to hit rather than of code to write.
- **Re-key the LOD interest field on geometry (§2.4).** Independent of the decision.

---

## 8. The four questions that decide this, and the cheapest experiment for each

Nothing below should be argued; all four are measurable in days.

| # | question | experiment | decides |
| --- | --- | --- | --- |
| **Q1** ⚠ **ANSWERED — problem confirmed, cheap fix refuted** | Does wiring the walkable boundary into the LOD crowd stop the runaways? | `OrcaWalkableContainmentTests`, §2.3. Runaways are **real, density-triggered and up to 1.12 m deep**. But endpoint-dedupe boundaries block **100%** of routes and the midpoint filter is worse on both axes: a correct polygon-union boundary is required. | **C6** — Option D still needs real geometry work, though far less than the port |
| **Q2** | Can an ORCA ped abort mid-crossing without the reciprocal shove throwing it into traffic? | Bridge-crossing fixture; ped commits, car appears, move the goal back to the kerb. Measure max excursion and whether it re-enters the lane. | **R-A1**, the biggest unknown in Option A |
| **Q3** | Does a gap-acceptance signal produce believable kerb behaviour at flow density? | Prototype `GapAcceptanceSignal`, render it, look. Compare kerb-wait distribution against a SUMO run on the same net (§7). | **C3**, and whether Option A is finishable |
| **Q4** | Does ORCA hold up in the two places it is actually needed — threading a waiting crowd, and bi-directional streams on one crossing? | The two scenarios the SUMO work already identified as hard, run against ORCA. Render both. | whether the lattice is needed *anywhere* |
| **Q5** ✅ **ANSWERED — PASS** | Does the baked low-power route graph **only ever cross a carriageway inside a crossing polygon**? | `LowPowerRouteContainmentTests`: 240 routed paths over every polygon-centroid O/D pair, **71,932 samples at 0.25 m, ZERO off-walkable**; 14,756 samples land on `Crossing` polygons, so roads genuinely are traversed and only there. Negative control: the containment predicate rejects **94.3%** of the net's bounding box, so the zero is discriminating rather than vacuous. | Option D's safety argument **holds** on this fixture |
| **Q6** | Can a kerb hold be expressed on the low-power tier and survive replication? | Insert an `ActivityTimeline.Pause` at the kerb whose duration is decided on arrival; check `ActivityTimelineWire` / `PedRemoteReconstructor` reproduce it, and measure the extra wire traffic | whether §5b.2 is as cheap as it looks, or needs a new open-ended segment kind |

**Q5 and Q1 first, in that order.** Q5 because it is a pure static analysis of committed data — no
simulation, no tuning — and it is load-bearing for Option D's entire safety story: if scripted peds can
route across a carriageway outside a crossing, nothing downstream saves them. Q1 second because it is
one wiring call following an existing precedent and it targets the loudest symptom.

Q2 remains the biggest *unknown* — but note §5b.3 changes it: if kerb waiters are static rather than
agents, the reciprocal-shove failure mode that makes Q2 worrying is largely removed, and Q2 narrows to
"can a ped already mid-crossing retreat cleanly", which is a smaller question.

**Q1 first.** It is the cheapest, it targets the owner's loudest complaint, and its answer moves the
decision more than any other single fact. If containment turns out to be one wiring call, Option A gets
much stronger. If it does not, Option B's structural argument becomes decisive and the rest of the
questions matter less.

---

## 8b. Current standing (2026-08-02)

**Q5 and Q1 have been run (2026-08-02). Option D survives, with one item promoted from "wiring" to
"real work".**

- **Q5 passed cleanly** — the low-power router never leaves the pavement, and it does cross roads, only
  at crossings. Option D's safety argument stands on this fixture. Caveat: **one fixture**
  (`poc0-crossing-plaza`). Re-run on a larger/organic net before treating it as general.
- **Q1 confirmed the runaways and refuted the cheap fix.** Excursions are density-triggered (nothing
  ≤40 agents, 2.6% of agent-steps at 240) and reach **1.12 m** — a traffic lane, not a kerb. But
  containment cannot be bought with one `AddObstacle` call: the naive boundary blocks 100% of routes.
  A genuine polygon-union boundary over *overlapping* polygons is needed. Smaller than the SUMO port by
  a wide margin, but geometry work with a correctness bar.

**Net effect on the decision: unchanged in direction, sharper in cost.** Option D is still the
front-runner, and it now has one more named work item.

**Option D is the front-runner.** Not because ORCA beats the lattice on merit, but because it changes
what has to be true:

- containment (C6) becomes automatic everywhere except inside small junction polygons, since a
  low-power ped is a pure function of its path and *cannot* run away;
- kerb waiting (C3) becomes a **pause in a precomputed timeline** rather than a steering behaviour, so
  the one genuinely missing criterion is implemented in the tier where it is easiest and safest;
- the waiting cluster becomes static, which removes the measured reciprocal-shove overshoot by
  construction rather than by tuning;
- C1/C4/C5 are already built and are stronger than SUMO's.

What remains genuinely uncertain after Q5/Q1: **Q2** (mid-crossing retreat), **Q4** (does ORCA thread a
dense waiting crowd believably), **Q6** (does a kerb hold survive replication), and whether Q5's clean
result generalises beyond one fixture. Plus the newly-sized work item: a correct walkable-union boundary.

Note the containment finding cuts **both** ways and should not be read as purely bad news for Option D.
Under Option D, ORCA runs only inside junction islands — so the union boundary that must be computed is
*one junction's*, not a city's, and the 1.12 m excursions matter only where a ped could reach a live
lane. That is a much smaller and better-posed geometry problem than the one this experiment measured.

**Nothing here retires the SUMO port.** If Q1/Q2/Q4 come back badly — if ORCA cannot be kept inside a
junction polygon under pressure, or cannot thread a dense waiting crowd believably — the lattice's
structural containment becomes the decisive argument and `SUMOPED-*` is ready to execute.

---

## 9. Where this leaves the SUMOPED doc set

Unchanged and still valid — a reviewed, gap-audited plan that can be executed if Option B wins. It is
**paused, not withdrawn**, and its tracker carries a banner pointing here. B0 does not start until this
decision is made.

Nothing in it is wasted under Option A either, per §7: the mechanism knowledge is the specification for
the ORCA additions, and the oracle tooling is what calibrates them.
