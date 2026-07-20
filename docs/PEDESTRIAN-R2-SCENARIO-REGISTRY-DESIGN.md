# R2 — data-driven micro-scenario registry (design)

**Status: PARKED (design retained, not implemented).** Decision (2026-07-20): the micro-scenarios (waiter R2,
drive-away R3) demand tight cross-referenced input-data consistency (`venue → service_door → table_cluster`,
`parking_lot → boardable_car → exit_route`, every id resolving) that adds disproportionate difficulty right
now — and the delivered box's `venue` records don't yet even carry the `scenario_template`/`service_door`/
`table_cluster` fields. So R2/R3 are parked: the design below stands for when we unpark, but the near-term goal
is that **SumoSharp loads, bakes, and runs the demo-city with the already-landed live-behaviors (dwell, meet &
talk, enter/exit, crossing, weave) — WITHOUT the micro-scenario complexity.** §0 records the O/D data contract
we consume now (agreed with SumoData); the rest is the retained HOW.

**HOW** for `SUMOSHARP-DEMO-CITY-REQUIREMENTS.md` R2. The **WHAT** is that requirement + the `pois/v2` schema
in `SUBAREA-DEMO-CITY-DESIGN.md §3`; this doc does not restate them.

## 0. O/D + POI data contract consumed NOW (the un-parked part)

What SumoSharp reads from `pois/v2` today, so the box runs with the landed behaviors and nothing throws:

| record / field | consumed now? | by what |
|---|---|---|
| base POI `id/kind/pos/edge/weight/facing/capacity/land_use` (all kinds) | **yes** | `PedPoiReader` → O/D demand weighting + P8-2 legitimacy gate |
| kinds `building_entrance / venue / dwell_spot / transit_stop / parking_access` | **yes** | demand endpoints + enter/exit/dwell anchors (v1 behaviors) |
| new kinds `parking_lot / park` | **tolerated** — loaded as base POIs, `weight` defaulted 0 (not O/D-drawn) | `PedPoiReader` (loads, does not act) |
| `venue.venue_type / lateral_anchor` | not yet | (weave lateral anchor is a later refinement) |
| `venue.scenario_template / service_door / table_cluster` | **PARKED** | R2 registry (this doc) |
| `parking_lot.polygon / lane_seam / parked_cars / boardable_car / hidden` | **PARKED** | R3 drive-away / R4 garage |
| `park.polygon / path_edges / meet_areas / dwell_points` | **PARKED** | R5 park ped-only region |
| `edge_fields.json` (per-edge width / keep-side / crossing class) | **yes** (width → the weave, R7) | baked sidewalk width → `LateralWeave` |

**Contract with SumoData:** the box must remain loadable + bakeable with only the base-POI + `edge_fields`
path — i.e. a build may add/omit the parked cross-reference fields without breaking SumoSharp load or bake.
The parked fields resolve their ids within the box (SumoData's invariant) so that when R2/R3/R5 unpark, the
resolver in §3 can rely on it. **No SumoSharp change consumes the parked fields until its R unparks.**

## Retained design (below) — for when R2 unparks

*(Everything from here down is the original design, kept intact; implementation deferred.)*

## 1. Goal & scope

Replace the **hardcoded** waiter anchor (`SceneGen.BuildWaiter` / `BuildWaiterTimeline`, a fixed
`WaiterScenarioConfig` literal at `SceneGen.cs:989`) with a **registry keyed off data**: a `venue` POI record
carries `scenario_template` (e.g. `waiter_v1`), `service_door` (a `building_entrance` id), and `table_cluster`
(`[{id,pos,capacity}]`); a registry instantiates the named scenario **at that venue**, producing peds that
arrive via the door, sit at a table, are served, dwell, and leave — **IG-deterministic like everything else**
(seeded, pure, broadcast-once `ActivityTimeline`s).

In scope: the `pois/v2` ingestion of the venue-scenario fields, the template registry + director, and
`waiter_v1`. Out of scope (their own R's): the drive-away/parking records (R3), park/garage (R4/R5), City3D
(R6). This doc reuses the existing `WaiterScenario` generator, `PedPoi`/`PedPoiReader`, and the `AddPedLively`
spawn path — it is a composition + data-binding layer, not a new evaluator.

## 2. Current state (what exists, and one correction to the ask)

- **`WaiterScenario.Build(cfg)`** (`Lod/WaiterScenario.cs`) — a **pure generator** of the *waiter actor's*
  looping `ActivityTimeline` (hidden inside-dwell → `Loops` × Walk(door→table)→Dwell(serve)→Walk(table→door)→
  Dwell(inside)), table order a seeded closed-form rotation. No `System.Random`. Server==IG by reduction to an
  ordinary timeline. **Reused as-is**, just fed from data instead of a literal.
- **`PedPoi` / `PedPoiReader`** (`PedPoi.cs`, `PedPoiReader.cs`) — **POI reading already exists** (pois/v1 JSON:
  `id/kind/pos/edge/weight/facing?/capacity?/land_use?`). **Correction to R2's note:** the extension is to
  `PedPoiReader`/`PedPoi` → `pois/v2`, *not* the net parser — SUMO `<poi>`/`<param>` reading is not needed; the
  reader consumes the richer companion JSON directly (its own header says so).
- **`AddPedLively(id, timeline, …)`** (`PedLodManager`) — the spawn path a generated scenario ped enters
  through; `PedDemand` allocates ids via `_nextId++` and seeds per-ped with `VehicleRng.SeedFor(Seed, id, salt)`.
- **No micro-scenario director exists** — `SceneGen` hardcodes the one waiter for the Sim.Viz demo. That literal
  is what R2 replaces.

## 3. Data model — `pois/v2` additions to ingest

Extend `PedPoi` (additive, schema-gated on `"pois/v2"`; v1 files parse unchanged) with the venue-scenario
fields R2/§3 need. Proposed shape (names mirror the schema):

- `Venue` gains: `VenueType` (`mall|restaurant|cafe|office|retail`), `ScenarioTemplate` (string, e.g.
  `waiter_v1`), `ServiceDoorId` (a `building_entrance` id), `TableCluster` (`[{Id, Pos, Capacity}]`),
  `BuildingId`.
- New record `TableSlot(string Id, Vec2 Pos, int Capacity)` for the cluster entries.
- (Only the fields `waiter_v1` needs land in R2; the other v2 kinds — `parking_lot`, `park`, the
  `building_entrance`/`dwell_spot` extras — are ingested by R3/R4/R5, so `PedPoiReader` reads them lazily/
  ignorable now. R2 adds only the venue-scenario path.)

**Cross-reference resolution.** A small resolver builds an id→POI index and resolves `ServiceDoorId` →
`building_entrance` POI (for the door pose/edge) and validates every `TableCluster` id + `ServiceDoorId`
resolves within the catalog (the "every referenced id resolves within the box" invariant, §3). A dangling ref
is a load-time error, not a silent skip — so a malformed box fails loudly at ingest, not mid-sim.

## 4. Registry architecture

Three small pieces, all in `Sim.Pedestrians` (productized, not Sim.Viz):

1. **`IScenarioTemplate`** — the template contract:
   ```
   IReadOnlyList<ScenarioActor> Instantiate(in ScenarioInstanceContext ctx);
   ```
   where `ScenarioActor = (ActivityTimeline Timeline, double MaxSpeed, double Radius)` and
   `ScenarioInstanceContext` carries the resolved geometry + params: the venue POI, the resolved service-door
   pose/edge, the table slots, a base start time, a **per-instance seed**, and a max-speed/radius default.
   A template is a **pure function** of its context (no `System.Random`, no clock) — this is the load-bearing
   determinism property.

2. **`ScenarioRegistry`** — `name → IScenarioTemplate`. `Register("waiter_v1", new WaiterV1Template())`;
   `TryGet(scenarioTemplate)`. Extensible: later `cafe_v1`, `office_v1` register the same way. Unknown template
   name on a venue = a logged skip (forward-compat: a box may name a template a given build doesn't ship yet).

3. **`ScenarioDirector`** — the driver. Given the POI catalog + the registry + a scenario-global seed, it: finds
   every `venue` with a `ScenarioTemplate`, resolves its refs, derives a **stable per-venue seed**
   (`VehicleRng.SeedFor(globalSeed, hash(venue.Id), ScenarioSalt)` — keyed by venue *identity*, not spawn order,
   so it's reproducible and independent of any ambient-demand id stream), instantiates the template, and spawns
   each actor via `AddPedLively` with a director-allocated id from a **reserved id range** (see §7 open Q on id
   allocation). Runs once at scenario load for a static set-piece; a cadence variant (§6) re-instantiates for
   arrivals over time.

Data flow: `pois.json (v2)` → `PedPoiReader` → catalog + resolver → `ScenarioDirector` → `IScenarioTemplate`
→ `ActivityTimeline`s → `AddPedLively` → the ordinary low-power + replication path (server==IG already proven).

## 5. `waiter_v1` — the composed restaurant scene

R2's scene is more than the lone waiter: "peds arrive via the door, sit at a table, are served, dwell, leave."
So `waiter_v1` composes **two actor kinds**, both pure `ActivityTimeline`s:

- **1 waiter** — the existing `WaiterScenario.Build`, fed from data: `DoorPos = serviceDoor.Pos`,
  `Tables = tableCluster.Pos[]`, timing/loops/seed from the context. (Zero new waiter logic — just the
  data binding that replaces the `SceneGen` literal.)
- **N patrons** — a new pure generator `WaiterV1.BuildPatron(...)`: Walk(approachPoint → table) →
  Dwell(`sit`, seated, faces the table, `servedSeconds`) → Walk(table → approachPoint) → leave. Patron **table
  assignment respects `capacity`**: a deterministic seeded assignment fills each table up to its capacity
  before spilling to the next (the same "no double-book beyond capacity" property `TableIndexForLoop` gives the
  waiter, generalized to N patrons over capacitated tables). Patron **arrival times** are a seeded staggered
  schedule (so they don't all pop at once). `approachPoint` = the service door pose (they enter via the door,
  per R2) — or the venue's attached sidewalk edge point (see §7 open Q on patron origin).

Everything reduces to `ActivityTimeline`s → the whole scene is as low-power / server==IG-reconstructable as any
lively ped (this is exactly the LIVE-POC-3 property, now data-driven and multi-actor).

## 6. Determinism / server==IG

- **Per-instance determinism:** every template output is a pure function of `(resolved geometry, base time,
  per-venue seed)`. Same box + same global seed ⇒ bit-identical waiter + patron timelines, in a stable order.
  No `System.Random`; all seeded via the existing `VehicleRng.SeedFor` discipline (the same root that seeds cars
  and the weave — one scenario seed governs the whole run).
- **server==IG:** each actor is broadcast once as an `ActivityTimelineRecord` and reconstructed by the same
  `PoseAt` — the identity is inherited from the existing timeline path (P3-1/W3), not re-proven per template.
- **Ordering:** the director iterates venues in a deterministic order (venue id, ordinal) and allocates ids from
  the reserved range in that order, so the id↔actor mapping is reproducible.

## 7. Seams / files touched (for the implementation stage)

- `PedPoi.cs` — additive v2 fields (`VenueType`, `ScenarioTemplate`, `ServiceDoorId`, `TableCluster`,
  `BuildingId`) + `TableSlot`.
- `PedPoiReader.cs` — parse the v2 venue block when `schema == "pois/v2"`; a `PoiCatalog` + id resolver
  (index + ref validation).
- **New** `Scenarios/IScenarioTemplate.cs`, `Scenarios/ScenarioRegistry.cs`, `Scenarios/ScenarioDirector.cs`,
  `Scenarios/WaiterV1Template.cs` (patron generator + the waiter data-binding).
- `SceneGen.cs` (Sim.Viz) — the `BuildWaiter` demo path switches to instantiate `waiter_v1` from a small POI
  fixture, so the demo exercises the data path (the literal becomes a fixture).
- No change to `PathArcMotion`/`ActivityTimeline`/the wire/`PedLodManager` — R2 rides the existing spawn +
  replication path.

## 8. Open questions (please review)

1. **Patron origin.** Do patrons arrive **from the service door pose** (self-contained set-piece — simplest,
   fully deterministic) or **route in from the venue's sidewalk edge** (more realistic — needs a nav `FindPath`
   from an approach point, and couples R2 to routing)? Recommendation: **door pose for `waiter_v1` v1**, edge-
   routed as a later refinement.
2. **Static set-piece vs. cadence.** Instantiate the scene **once** at load (a fixed cast that loops), or a
   **cadence** that spawns fresh patrons over time (livelier, more ids, needs an arrival-rate param)?
   Recommendation: **static once for R2**, cadence as a follow-up knob.
3. **Id allocation.** The director needs ids that don't collide with `PedDemand`'s `_nextId` stream. Reserve a
   high id range for director-spawned scenario actors, or have the director share/advance the demand id counter?
   Recommendation: a **reserved range** (e.g. a configured base offset), so scenario ids are stable regardless
   of ambient-demand volume.
4. **Who owns the run.** Is the `ScenarioDirector` invoked by `PedestrianWorld`/the demand harness at load, or a
   standalone step? Recommendation: a **one-shot `Instantiate()` at scenario load** driven by whoever owns the
   `PedLodManager` (mirrors how `AddPedLively` is called today), plus an optional per-step cadence later.
5. **Waiter timing source.** `ServeSeconds`/`InsideSeconds`/`Loops` — from fixed template defaults, or new
   `pois/v2` venue fields (`scenario_template` params)? Recommendation: **template defaults for v1**, promote to
   data if a box wants to tune them.

## 9. Staged tasks (implementation later — success conditions now)

- **R2-1 — `pois/v2` venue ingestion + resolver.** `PedPoi` v2 fields + `PedPoiReader` venue-scenario parse +
  the id resolver. **Success:** a committed `pois/v2` fixture with a venue (`scenario_template`, `service_door`,
  `table_cluster`) parses to a `PedPoi` carrying resolved refs; a dangling `service_door`/table id throws at
  load; a v1 fixture still parses unchanged.
- **R2-2 — registry + director.** `IScenarioTemplate`, `ScenarioRegistry`, `ScenarioDirector` (deterministic
  per-venue seed, reserved-range ids). **Success:** the director, given a catalog + a registry with a stub
  template, spawns exactly the template's actors via `AddPedLively` in a deterministic order; two runs with the
  same seed are bit-identical.
- **R2-3 — `waiter_v1` (waiter + patrons).** The data-bound waiter + the patron generator with capacity-
  respecting table assignment + staggered arrivals. **Success:** instantiating `waiter_v1` at a fixture venue
  yields 1 waiter + N patrons; the waiter serves every table over a rotation (no skip/double-book); patrons
  never exceed a table's `capacity`; a served patron's timeline shows Walk→Dwell(`sit`,served)→leave; all
  deterministic; server==IG holds over the wire (extend the round-trip harness with one scenario actor).
- **R2-4 — wire into the demo + swap `SceneGen`.** `SceneGen.BuildWaiter` instantiates `waiter_v1` from a POI
  fixture (the literal → data). **Success:** the Sim.Viz waiter demo renders the same behavior from the data
  path; the `--ped-waiter` report still finds the hidden/serving windows; full gate + ped suite green.

Sequencing: R2-1 → R2-2 → R2-3 → R2-4. Each closes only on its stated success conditions. Blocked on nothing
except a `pois/v2` venue fixture — which I can author a minimal committed one for now, and swap for SumoData's
real `pois.json` venue records when the box lands.
