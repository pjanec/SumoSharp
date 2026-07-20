# PEDESTRIAN-PLANNING-INTENTS.md — intents & ways forward (cross-session sync)

**Status: intents / thinking, not a committed mechanism.** A shared thinking doc for the SumoSharp
pedestrian track and the SumoData sub-area/calibration session, to align on the pedestrian-planning model
BEFORE committing code. Prototypes decide the specifics (Section 9). Supersedes nothing; feeds the eventual
PED-REALISM-1 design (`PEDESTRIAN-LOWPOWER-AVOIDANCE-DESIGN.md`).

## 1. The problem, reframed

We started at "low-power peds pass through each other" (PED-REALISM-1). It is really a corner of a bigger
problem: **pedestrian trajectory planning is a multi-objective compromise, coupled across both sessions.**
The realization driving this doc: peds are only *partially independent* of each other, so the planner is not
purely per-ped — yet each ped's realized trajectory must still be reconstructable by the headless IG from a
compact broadcast. Reconciling those two is the whole design.

## 2. The objectives (and where they fight)

| Objective | Owner-ish | Pulls toward |
|---|---|---|
| **Density** (max believable peds+cars; automatic calibration) | SumoData | more peds, but **road crossings are the binding constraint** (crossings must stay car-safe-sparse, and they gate car flow) |
| **Realism** (no pass-throughs, believable weave, live-behaviors) | SumoSharp | lateral spread, keep-right, organic weave, activity stops |
| **Performance** (thousands of peds) | SumoSharp | O(1)/ped low-power bulk; ORCA only where it pays |
| **Live-behaviors** (stop-to-talk, enter/exit building, restaurant) | SumoSharp | `ActivityTimeline` segments interrupting the walk |
| **IG-predictability** (server == IG) | SumoSharp | trajectory = pure function of the ped's own broadcast (+ shared, reconstructable data); no per-frame neighbour state on the wire |

The tensions that matter:
- **Density ↔ crossings.** Ped road-crossings limit achievable density. Fewer/again-concentrated crossings
  → higher density.
- **Realism ↔ performance.** True reciprocal avoidance (ORCA) is realistic but doesn't scale to the bulk.
- **Coupling ↔ IG-predictability.** Peds influencing each other (lanes forming, crossings queueing) reads as
  real, but naive neighbour-reactivity breaks the pure-function reconstruction the IG needs.

## 3. The load-bearing idea: partial coupling via SHARED DETERMINISTIC FIELDS

The way to make peds *partially coupled* without breaking IG-predictability or O(1) cost:

> Each ped's trajectory is a pure function of **(its own route, its own seed, a set of shared deterministic
> fields)**. The **fields** are the coupling; they are either **net-derivable** (both server and IG compute
> them from the same net + a global seed) or **broadcast-once** (sent as durable state, not per-frame). No
> ped reads another ped's live position.

Candidate shared fields:
- a per-edge / per-area **lateral flow/density field** (keep-right bias strength, how much of the width the
  bands use) — makes lanes emerge consistently for everyone without neighbour queries;
- a **crossing schedule / crossing-permeability field** (where/when crossing is cheap vs discouraged);
- a **hotspot / area-of-interest map** (where the crowd is dense enough — or on-camera enough — to warrant
  ORCA).

Because the fields are deterministic and shared, two peds "respond to the same conditions" (emergent lanes,
emergent crossing patterns) while each stays individually reconstructable. This is the compromise between
"fully independent per-ped plans" (cheap, IG-trivial, but uncoordinated/fake) and "reactive multi-agent"
(coordinated/real, but O(n) and IG-hostile).

## 4. The levers (candidate mechanisms)

1. **Lateral weave profile (realism, low-power)** — the PED-REALISM-1 core: `pos = centerline(s) +
   rightNormal(s)·offset(s, seed)`, a keep-right-biased lane plan that changes every ~tens of metres with
   smooth transitions, clamped to a broadcast corridor half-width. Sample-time + one scalar `W` keeps the
   wire tiny; server==IG by recomputing the same `offset(s)`. (Full mechanism + knobs in
   `PEDESTRIAN-LOWPOWER-AVOIDANCE-DESIGN.md`.) Kills the head-on centerline pile-up AND the rigid-car-lane
   look; does **not** guarantee zero same-point crossings — the accepted "reachable level".
2. **Crossing-averse routing (density lever)** — a routing-cost term that penalizes road crossings, so peds
   prefer same-side, building-to-building paths and cross only when the O/D genuinely requires it. Raises
   achievable ped+car density (fewer crossing conflicts) *and* is arguably **more** realistic (real peds
   avoid mid-block crossings). A per-edge/junction crossing-cost is a shared field (Section 3).
3. **Concentrated crossings at areas-of-interest + hotspot ORCA** — where crossings are unavoidable or the
   crowd is genuinely dense, coincide them with the zones where **ORCA is already promoted** (camera /
   hotspot). The expensive reactive avoidance and the crossing conflicts then share the same small
   high-power budget; the bulk elsewhere stays low-power and crossing-light. Extends "areas of interest"
   beyond camera to include density hotspots + crossing zones.
4. **Up-front vs on-the-go replanning (the IG-predictability contract).** Two regimes, and a middle:
   - *Up-front:* plan the whole trajectory at spawn — a pure function of (route, seed, fields). Trivially
     IG-reconstructable, but can't adapt to emergent congestion.
   - *Replanning:* adjust mid-route. Keep it IG-predictable by modelling a replan as a **re-broadcast of a
     compact plan leg** — exactly the pattern the dynamic-blocker reroute already uses (a fresh PathArc leg
     on the wire). The IG stays in sync because it receives the new leg; predictability = "the trajectory is
     always a pure function of the broadcast state *at each instant*," and a replan is just a broadcast
     event.
   - *Middle (preferred default):* plan up-front from the shared fields so **most** peds never replan;
     replan only rarely (genuine local surprise) and re-broadcast when they do. Low wire, high predictability,
     some adaptivity.

## 5. LOD layering (unchanged spine, extended triggers)

- **Low-power bulk:** deterministic weave (lever 1) + crossing-averse routing (lever 2), no reaction, O(1),
  server==IG. Serves ~90%+ of the crowd.
- **High-power ORCA subset:** promoted in areas of interest — now defined as **camera ∪ density hotspots ∪
  active crossing zones** (lever 3). Reactive, handles dense crossings and pinch points. Already driven by
  `InterestField`; this just enriches what registers a source.

## 6. IG-predictability contract (must hold for every lever)

- A ped's pose at time *t* is a pure function of: its own broadcast route/legs, its own seed, and the shared
  fields (net-derivable or broadcast-once). Never another ped's live state.
- Shared fields are versioned/broadcast durably (or net-derivable), so the IG evaluates the identical field.
- Replanning = a broadcast leg event; between events the trajectory is closed-form. No hidden mutable
  per-frame state crosses the wire.
- Determinism: per-entity seeded `VehicleRng` only; results independent of thread order.

## 7. Coordination with the SumoData session

SumoData is doing **automatic calibration**: tuning parameters of a given road net to hit a target
density + believability. The pedestrian planner's levers change what density is *achievable*, so the two
models must agree. Proposed split:

- **SumoData owns:** the road net + crop; automatic density calibration (dial, ceilings, mean-trip); crossing
  **classification** + the **per-crossing vehicle-knee seam** (P8-4b input); per-edge **sidewalk width**
  (already promised — the honest input for the weave clamp + per-class ceiling); the **synthetic demo mesh**
  (clean geometry so ped realism + live-behaviors can be shown without real-net fragmentation).
- **SumoSharp owns:** the ped planner (lateral weave, crossing-averse routing, LOD promotion policy); the
  IG-predictability contract; the live-behaviors (`ActivityTimeline`: talk/enter-exit/restaurant); the
  per-class (sidewalk vs crossing) density ceiling once the data exists.

**Synthetic-mesh data schema — to agree (the concrete sync ask):** what per-edge / per-area / per-POI fields
the ped planner needs on the mesh, beyond geometry:
- per-edge **sidewalk width** (weave clamp, per-class ceiling);
- **crossing** locations + a class (signalized / unsignalized / discouraged) + the vehicle-knee hook;
- **areas of interest / density-hotspot** markers (ORCA promotion zones);
- **building entrances** (enter/exit-building activities) and **venues** (restaurant/stop dwell spots, with
  capacity) — the live-behavior anchors;
- optionally a coarse **desired-lateral-flow / keep-side** hint per edge (or we derive it).

**Density-calibration coupling — to agree:** how ped crossing-rate (a function of the crossing-averse routing
bias) feeds SumoData's calibration, so raising the ped density doesn't silently break the car-safe crossing
sparsity, and vice versa. This is the "dense sidewalks + safe crossings simultaneously" decoupling both
sessions flagged (needs SumoSharp per-class ceilings + P8-4b + SumoData crossing classification).

## 8. Open questions / decisions

- Weave: sample-time + corridor-`W` scalar (preferred, wire-minimal) vs bake-the-weave-into-path (no width on
  wire, but ~2× path vertices)? — decide with a prototype.
- Crossing-aversion strength: how hard to penalize crossings before routes look unnatural (over-detouring)?
- Replanning: is the up-front-only regime enough for the demo, or do we need the re-broadcast-leg regime from
  day one? (Lean: up-front-only for v1; design the leg-rebroadcast contract but don't wire it until a
  scenario needs it.)
- Shared fields: net-derivable vs broadcast-once — which fields can both sides compute from the net + a global
  seed (no wire), and which must be broadcast?
- Per-class density ceiling split (sidewalk vs crossing) — needs the crossing classification data.

## 9. Prototypes to decide (we need these to choose, not argue)

1. **Lateral-weave look** — prototype `offset(s)` and render an **animated** `--ped-*` replay on an existing
   clean box (uniform-grid `subarea-box`) so we can *see* whether the weave reads as believable and tune the
   wavelength / keep-right / width knobs. Motion is what a static description can't settle. (De-risks lever 1
   independently of everything else; can start now, before the synthetic mesh lands.)
2. **Crossing-averse routing** — add the crossing-cost term to the ped router on a box with crossings; measure
   crossing-rate reduction vs route-length inflation; eyeball naturalness.
3. **Composed demo on the synthetic mesh** — once SumoData delivers it: weave + crossing-aversion + hotspot
   ORCA + live-behaviors (talk/enter-exit/restaurant) together, at the calibrated density. The believability
   acceptance.
4. (Later, if needed) **replan-by-rebroadcast** — a ped that replans mid-route and stays IG-exact across the
   re-broadcast.

Prototype 1 is the immediate next step and is not blocked on anything.

## 10. Schema decision + field-ownership split (agreed with SumoData)

SumoData accepted the §7 split and committed to the schema. Decisions locked:

- **Two files, kept separate** (both broadcast-once, same net-XY frame, `schema` tag): keep `pois.json` for
  point/area anchors (building entrances, venues + capacity, `aoi` markers); add a sibling
  **`edge_fields.json`**: `{ "<edgeId>": { "sidewalk_width_m": …, "keep_side": "R", "crossing": {"class":
  "signalized|unsignalized|discouraged", "veh_knee": …} | null, "aoi": 0..1 }, … }`. This is the shape we
  parse.
- **Net-derivable vs broadcast-once:** SumoData EMITS the static fields (per-edge width, crossing class, AOI,
  POIs) as broadcast-once companion data; SumoSharp COMPUTES the dynamic ones (weave `offset(s)`, LOD
  promotion, crossing-averse route) from (route, seed, those fields). **Consequence worth noting:** because
  `edge_fields.json` is broadcast-once and the IG ingests it, the corridor half-width `W` is *derivable* from
  (route, edge_fields) — so the weave costs **zero per-ped bytes on the wire** (no per-ped `W` scalar needed;
  the IG recomputes it). That is the shared-fields model paying off.
- **Owed back to SumoData (sequenced with P2/P3, not now):** the per-class (sidewalk vs crossing) density
  ceiling model; `crossing_rate(ped_density, bias, class)` (coarse for v1); the micro-scenario registry +
  live-behavior anchors consuming their POIs. v1 density coupling is "crossing-averse routing keeps
  crossing-rate low → dense sidewalks + safe crossings by construction," which needs no full co-calibration.
- **Prototype 1 input:** SumoData offered a per-edge sidewalk-width file for the existing `subarea-box`. We
  accept it for schema-shakedown, but Prototype 1 is not blocked on it — the bake already exposes
  `PedLane.Width`, so the weave clamp can bootstrap from that immediately.
