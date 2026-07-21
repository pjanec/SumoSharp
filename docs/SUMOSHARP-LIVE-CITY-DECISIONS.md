# Live-city demo — locked decisions (closed contract)

Record of the decisions agreed between the SumoSharp ped/engine session and the SumoData session, from
`SUMOSHARP-LIVE-CITY-DATA-REQUEST.md` (6614766) → SumoData's response. This is the frozen contract the
design docs build against. Source of truth for the "why"; the two design docs
(`LIVE-CITY-2D-BUILDER-DESIGN.md`, and later the crossing-yield + City3D designs) hold the "how".

## Headline decisions
- **Option B accepted and supersedes the spec's §2 Option A.** Cars yield to **any** ped on a crossing,
  low-power included. Mechanism = our **deterministic per-crossing occupancy** (treat an occupied crossing
  as a closed gate / virtual blocking leader via the existing `CrossingGate` + `Engine.CrowdSource` braking
  seam; parity-inert), *not* a low-power footprint source. The spec's ORCA high-realism **zone**
  (W-A/W-B/W-C) survives as the LOD-contrast + reactive-crowd showcase — it is no longer the crossing-yield
  mechanism.
- **v1 behavior set (Q8):** enter/exit buildings + dine at restaurant (`waiter_v1`) + meet/talk
  (dwell/park) + crossing-yield + weave + dense cars. **Deferred:** R3 walk-to-car & drive-off, R4
  hidden-garage birth/death, v3 transit board/alight (data exists; pull in later only if the owner asks).

## Q1 — crossing `class` → yield rule (Feature 1)
Classes `{signalized, unsignalized, discouraged}` (box counts 180 / 102 / 49), derived from the crossing's
junction type; class = intent, net `<crossing priority=…>` + `<tlLogic>` = mechanism (they agree).

| class | net origin | yield rule |
|---|---|---|
| **signalized** | crossing at a `traffic_light` node | signal-controlled: ped waits for walk phase; car yields only when its own approach is red. Occupancy-block a lane only when the ped legitimately has the walk signal. |
| **unsignalized** | `priority` node (marked zebra, ped priority) | **cars MUST yield** to any ped on/entering the crossing — the core Option-B "must stop" case. |
| **discouraged** | `right_before_left` / minor node | car has right-of-way; ped crosses on gaps. Safety fallback only: a car still **brakes rather than drive through** a ped physically in its lane, but it is not a RoW stop. |

## Q2–Q9 (Feature 2 + data)
- **Q2 coordinate frame — CONFIRMED single SUMO-metre frame, one origin, no per-file offset.** SumoData
  will add a top-level `coordinate_frame` to `manifest.json` (reference-only; no coordinate changes).
- **Q3 `venue.service_door` → `building_entrance` — CONFIRMED** the contract; build-validated (0 dangling).
- **Q4 `table_cluster` placement — currently INDOOR; SumoData will move to OUTDOOR terraces** (just outside
  the `service_door`, off the footprint edge) so seated peds + waiter render in view. Same frozen schema,
  values change once. → treat tables as **outdoor + visible** after the regen.
- **Q5 venue R2 block — FROZEN** (`venue_type` / `building` / `service_door` / `scenario_template` /
  `table_cluster[]{id,pos,capacity}`). Safe to build the un-parked consumer against it; only additive
  changes henceforth.
- **Q6 coverage — HERO SET for v1** (a few restaurants + the mall; everything else generic massing).
- **Q7 staging crop — hero venues are split today; SumoData will regenerate a co-located hero block inside
  the downtown district** (dense TL grid + signalized crossings + wide sidewalks + car traffic) and give
  the exact crop bbox. **Zero-wait fallback to start against now:** the **dining district**, crop
  `[3100, 1900, 3900, 2700]` (~800×800 m; 3 restaurants + entrances + a plaza meet-spot; lighter car
  traffic, tables indoor until Q4 lands).
- **Q9 appearance palette (optional, for cross-surface consistency):** buildings by `type` — mall
  `#f59e0b`, office `#3b82f6`, residential `#2dd4bf`, restaurant `#f87171`, garage `#6b7280`; zones tinted
  (downtown slate, retail amber, dining pink, residential blue, park green, arterial faint-grey).

## SumoData deliverable (small regen, non-blocking)
One `compose.py` regen: (1) tables → outdoor terraces; (2) co-located downtown hero block + exact crop
bbox; (3) manifest `coordinate_frame`. They ping us with the updated box. **Nothing here blocks starting:**
Feature 1 needs only Q1 (answered); the 2D builder + City3D consumer can wire against the current box now
(dining-district fallback crop) and pick up the hero block when it lands.
