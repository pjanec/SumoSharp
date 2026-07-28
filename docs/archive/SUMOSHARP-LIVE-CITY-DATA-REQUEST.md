# Live-city demo — data & decision requests for the SumoData session

> **STATUS: ARCHIVED (2026-07-28)** — The Q1-Q9 checklist of confirms and scope decisions sent to SumoData for the coupled cars+peds City3D demo. The **answers** are the frozen contract in `docs/SUMOSHARP-LIVE-CITY-DECISIONS.md` -- read that. Kept for the problem framing, which the decisions doc does not restate.


**From:** the SumoSharp pedestrian/engine session · **To:** the SumoData session
**Re:** `SUMOSHARP-LIVE-CITY-DEMO-SPEC.md` (your combined-demo spec) + two extensions the SumoSharp owner
has now made **hard requirements**. This doc closes information gaps *before* we design the implementation.

Everything below is checked against the committed box `scenarios/_ped/demo_city/box/` (net.xml, pois.json
`pois/v2`, buildings.json `buildings/v1`, zones.json `zones/v1`, edge_fields.json, manifest.json) — so we
ask only for genuine gaps, not things you already shipped.

---

## 0. The two new hard requirements (context for what we need)

1. **Cars MUST stop for pedestrians on crosswalks — including low-power (ambient) peds.** Your spec's
   §2 Option A ("cars yield to promoted/high-power peds only; ambient peds don't stop cars") is **rejected
   by the owner** as too unrealistic — a car visibly driving *through* a walking pedestrian is not
   acceptable. We are committing to **Option B**: cars yield to *any* ped on a crossing, low-power
   included. This is primarily **our** engine work (see §2); it needs almost nothing from you, but we want
   to confirm one thing.
2. **City3D must render cars AND pedestrians together, in a semantically-correlated "live city"** — peds
   entering/exiting buildings, stopping at restaurants and meeting places, with the **3D world matching the
   simulation** (a ped that enters "restaurant X" walks to X's real door and X is the building rendered
   there). This needs a data/scope agreement (see §3). Today City3D's `--peds` path is peds-only, plaza-only,
   and separate from the vehicle path — so this is real new work on our side, but the good news is **most of
   the data already exists in your box**.

We are **not** asking you to build anything large. We mostly need **confirmations, a couple of scope
decisions, and a schema freeze**.

---

## 1. What we verified you ALREADY deliver (no re-ask — thank you)

Confirmed present and usable in `demo_city/box/`:

- **Buildings** (`buildings.json`, 28): each has `footprint` (world-metre polygon), `height_m`, `levels`,
  `type` ∈ {mall, restaurant, office, residential, garage}, `zone`. → City3D footprint-accurate massing (R6).
- **Zones** (`zones.json`, 6): downtown / retail / dining / residential / park / arterial, each a polygon.
- **Building entrances** (`pois.json` kind `building_entrance`, 45): `pos`, `facing` (unit vector),
  `inside_dwell {min_s,max_s}`, `lateral_anchor`, `legitimate_sink`, and (on the annotated ones) a
  `building` id that resolves into buildings.json.
- **Hero restaurant venues fully R2-annotated** (4 of 25 `venue` records, e.g. `poi_v2_venue_restaurant_0`):
  `venue_type`, `building` (→buildings.json), `service_door` (→an entrance POI id), `scenario_template`
  (`waiter_v1`), `table_cluster[] {id,pos,capacity}`, `zone`. **This is exactly the "dine at a restaurant"
  data** — the R2 *data* exists even though our R2 *consumer* is parked.
- **Dwell / meet places**: `dwell_spot` (25; `duration_profile`, `meet_area`, `group_size`, `talk_duration`),
  `park.meet_areas[]`. → "stop and meet/talk" is data-backed.
- **Parking**: `parking_lot` (5; `polygon`, `capacity`, `lane_seam`, `parked_cars[]`).
- **Crossings**: the net encodes each crosswalk as an internal edge `function="crossing"` with
  `crossingEdges="<vehicle edge(s) it crosses>"` (331 of them), plus `edge_fields.json` per-edge `aoi` +
  crossing **class**. → we can map a crossing to the vehicle lanes it blocks **ourselves**, from the net.
- **One coordinate frame** (SUMO metres) shared across net/pois/buildings/zones (your R6 note "coordinate
  frames already match"). We rely on this for co-rendering — see the confirm in §3.

So the substrate is genuinely rich. The gaps are coverage/scope and a few semantics, below.

---

## 2. Feature 1 — cars yield to crossing peds (Option B). Mostly ours; ONE confirm.

**Our plan (for your awareness, no action needed):** a low-power ped's crossing traversal is a pure,
deterministic function of `(route, seed, speed, time)`, so we can compute crossing **occupancy cheaply**
without per-car/per-ped neighbour tests — e.g. a per-crossing "occupied" flag/count maintained only at
ped crossing-enter/exit events (or an analytic occupancy interval per ped per crossing), and the vehicle
engine treats an occupied crossing like a closed crossing-gate / virtual blocking leader on the crossed
lane. This reuses the existing `CrossingGate` + `Engine.CrowdSource` braking seam (already proven to stop a
car for a ped) and stays parity-inert (default-off, no golden touched).

**What we need from you — one confirmation:**

- **Q1. `edge_fields.json` crossing `class` taxonomy.** What are the possible crossing classes, and which
  imply **"cars must yield" (unsignalized zebra / priority-to-ped)** vs **"signal-controlled"** (ped waits
  for walk phase, car yields only on the ped's green)? We can derive priority from the net's
  `<crossing priority=…>` + `<tlLogic>`, but we want our yield rule to match your intended class semantics
  rather than guess. A one-line mapping (class → yield rule) is all we need.

That's it for Feature 1 — no new data.

---

## 3. Feature 2 — City3D combined cars + peds + semantic live-city. Confirms + scope decisions.

The **data is largely there**; the work is our consumer + a 3D render pass + the scope decisions below.

### Confirms
- **Q2. Coordinate-frame identity.** Confirm net.xml, pois.json, buildings.json, zones.json,
  edge_fields.json are all in the **same** SUMO-metre frame with the **same origin** (no per-file offset),
  so City3D can co-render peds, cars, buildings and props without registration. (manifest.json didn't
  surface a top-level `coordinate_frame`; pois/edge_fields carry one — please confirm they're identical.)
- **Q3. `venue.service_door` resolvability.** Confirm every enterable venue's `service_door` is the id of a
  `building_entrance` POI (with `pos` + `facing`), so a ped routes to a real door and City3D renders it
  entering the linked `building`. (We'll validate in code, but confirm it's the intended contract.)
- **Q4. `table_cluster` placement — outdoor or indoor?** Are restaurant `table_cluster` positions **outdoor
  terrace** tables (visible — seated peds render, waiter serves in view) or **inside** the building
  footprint (occluded)? This decides whether "dine at a restaurant" is a *visible* set-piece or a
  hidden-dwell. The `waiter_v1` template implies outdoor+visible — please confirm.
- **Q5. Schema freeze.** Is the `pois/v2` venue R2 block (`venue_type` / `building` / `service_door` /
  `scenario_template` / `table_cluster`) **frozen**? We'll build the un-parked consumer against it and don't
  want to chase a moving schema. If it may still change, tell us what and when.

### Scope decisions (pick with us)
- **Q6. Annotation coverage / hero set.** Today only **~5 of 28 buildings** have a labelled entrance and
  **4 of 25 venues** are fully R2-annotated (the restaurants). For the first live-city demo, is the intended
  scope the **annotated hero set** (a few restaurants + the mall in one district, everything else generic
  massing with no enter/exit), or do you want **broader coverage** (most buildings enterable, more venues
  with tables)? We're happy with the hero set for v1 — just confirm, or send an expanded pois/v2 if you
  want more.
- **Q7. Which district to stage in.** For a watchable combined block we want the crop that contains the
  hero venues + entrances + dense sidewalks + car traffic **together**. Do the annotated restaurants/mall
  cluster in one zone (e.g. `zone_dining` or `zone_downtown`)? If so, please give the **recommended crop
  bounds** (or confirm we should derive it from the `zone_dining` polygon). Right now our dense-city scene
  auto-picks the densest *sidewalk* block, which won't necessarily contain the hero buildings.
- **Q8. Behaviors in scope for v1.** From your behavior-status table (`SUMOSHARP-DEMO-CITY-REQUIREMENTS.md`):
  we propose v1 = **enter/exit buildings + dine at restaurant (waiter_v1) + meet/talk (dwell/park) +
  crossing-yield + weave + dense cars**, and **defer** walk-to-car-and-drive-off (R3), hidden-garage
  birth/death (R4), and transit board/alight (v3). Agree? If drive-off matters for the visual, say so and
  we'll pull `parking_lot.boardable_car` + `exit_route` (R3) into scope.
- **Q9. Optional appearance hints.** `type`+`zone` are enough for typed massing; if you have preferred
  materials/colours per building `type` or per `zone` we'll use them, otherwise we'll choose. Low priority.

### What we will build on our side regardless (so the split is clear)
- Co-host the vehicle `Engine`/SimSource **and** the ped source in one City3D viewer (today `--peds` skips
  vehicles) — cars + peds in one scene, over local and over DDS.
- Point the ped server (the `--mode ped-publish` DDS publisher and the local path) at the **demo-city box**
  net (today it's hardcoded to `poc0-crossing-plaza`), so 3D peds run the real city.
- Un-park a **slice** of the R2 consumer to *render* `service_door`/`table_cluster`/`scenario_template`
  (entrance walk-in + seated peds + waiter), reusing the already-tested `WaiterScenario`/`SocialPlanner`.
- Extrude `buildings.json`, tint `zones.json`, place `pois.json` props (tables, benches, parked cars) — R6.
- A smoke test for the combined + DDS ped path (it currently has none).

---

## 4. The one-screen checklist (what we need back)

| # | Ask | Type | Blocks |
|---|-----|------|--------|
| Q1 | crossing `class` → yield-rule mapping | confirm | Feature 1 design |
| Q2 | single shared coordinate frame across all files | confirm | Feature 2 co-render |
| Q3 | `venue.service_door` always → a `building_entrance` POI | confirm | dine-in routing |
| Q4 | `table_cluster` outdoor(visible) vs indoor(hidden) | confirm | restaurant set-piece |
| Q5 | pois/v2 venue R2 block frozen? | confirm | un-park R2 consumer |
| Q6 | hero-set vs broad annotation coverage | decide | demo scope |
| Q7 | recommended crop bounds / hero-venue district | provide | which block to stage |
| Q8 | v1 behavior set (defer R3/R4/v3?) | agree | scope |
| Q9 | material/colour hints per type/zone (optional) | optional | polish |

No large deliverable is requested. If Q6/Q7 reveal you'd rather ship an expanded/clustered `pois/v2`
(more entrances, a hero district with everything co-located) than have us derive it, that's a small
regenerate on your side and we'll consume it — your call.

---

## 5. Non-goals for this round
- We are **not** asking for a new net or a new city — the box is the substrate.
- Feature 1 (crossing-yield) is **ours**; we're only confirming Q1.
- R3 drive-off / R4 hidden-garage / v3 transit stay parked unless Q8 says otherwise.
- The 2D `sim_viz` live-city demo (your original spec) proceeds in parallel and depends on **none** of the
  City3D questions — only Q1 (and it can start immediately with Option B on the 2D scene).
