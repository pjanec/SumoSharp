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
| **C2** | pedestrians do not interpenetrate, except in genuinely dense crowds |
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

**Hypothesis (untested, cheap to test):** wiring the baked walkable boundary into the LOD crowd removes
most or all of the runaway behaviour. Evac already does this and treats it as a hard edge.

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
| **C2** peds don't interpenetrate | reciprocal avoidance | reciprocal avoidance | worse — jammed peds are mutually invisible |
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
| **Q1** | Does wiring the walkable boundary into the LOD crowd stop the runaways? | Call `AddObstacle(walkable boundary)` on `PedLodManager`'s high-power crowd, following `EvacDirector.cs:95`. Run the densest existing ped scenario. Count agent-steps outside the walkable polygon, before and after. | **C6**, and with it much of the case for the lattice |
| **Q2** | Can an ORCA ped abort mid-crossing without the reciprocal shove throwing it into traffic? | Bridge-crossing fixture; ped commits, car appears, move the goal back to the kerb. Measure max excursion and whether it re-enters the lane. | **R-A1**, the biggest unknown in Option A |
| **Q3** | Does a gap-acceptance signal produce believable kerb behaviour at flow density? | Prototype `GapAcceptanceSignal`, render it, look. Compare kerb-wait distribution against a SUMO run on the same net (§7). | **C3**, and whether Option A is finishable |
| **Q4** | Does ORCA hold up in the two places it is actually needed — threading a waiting crowd, and bi-directional streams on one crossing? | The two scenarios the SUMO work already identified as hard, run against ORCA. Render both. | whether the lattice is needed *anywhere* |

**Q1 first.** It is the cheapest, it targets the owner's loudest complaint, and its answer moves the
decision more than any other single fact. If containment turns out to be one wiring call, Option A gets
much stronger. If it does not, Option B's structural argument becomes decisive and the rest of the
questions matter less.

---

## 9. Where this leaves the SUMOPED doc set

Unchanged and still valid — a reviewed, gap-audited plan that can be executed if Option B wins. It is
**paused, not withdrawn**, and its tracker carries a banner pointing here. B0 does not start until this
decision is made.

Nothing in it is wasted under Option A either, per §7: the mechanism knowledge is the specification for
the ORCA additions, and the oracle tooling is what calibrates them.
