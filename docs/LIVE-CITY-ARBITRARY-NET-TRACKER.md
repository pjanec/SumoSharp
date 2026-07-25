# Tracker — arbitrary SUMO road-net import with route-graph pedestrians

At-a-glance checklist for `LIVE-CITY-ARBITRARY-NET-TASKS.md`. Tick a box only when the task's stated success
conditions are verified first-hand (Opus gate for the reviewed tasks). Global gate on every tick: parity
parity byte-identical (657/4 on the rebased base) + bench hash unchanged.

## Stage A — dataset param, drivable edges, capability probe
- [x] **A1** `LiveCityConfig.ForDataset` + `PedNavMode`; `ForRepoRoot` delegates (demo identical)
- [x] **A2** drivable edges from `net.xml` fallback (demo edge set unchanged)
- [x] **A3** capability probe + graceful degrade + `PedestriansEnabled`/`CrossingsEnabled`

## Stage B — SumoRouteGraphNav
- [x] **B1** node/edge graph + nearest-lane spatial index
- [x] **B2** `FindPath` (A* + polyline assembly through crossings/walkingareas)
- [x] **B3** `HalfWidthsAlong` from real lane widths
- [x] **B4** determinism (no `System.Random`, repeat-identical)

## Stage C — road-net mode wiring
- [x] **C1** mode branch: build route-graph nav, skip sidewalk bake (demo path unchanged)
- [x] **C2** crossings-only bake + gate/signals + walk-only degrade
- [x] **C3** O/D sampling from sidewalk centrelines (deterministic)
- [x] **C4** `RerouteDriver`/concrete-`SumoNavMesh` not wired in road-net mode
- [ ] **C5** feed live vehicle discs to ped crowd (ped-avoids-car; **zone-bounded**; demo off) — ⚠ BLOCKED: sync world-disc ownership with ORCA-ped session
- [x] **C6** enable `Engine.RegionPlan` for large nets (parity-safe toggle; demo off) — required a Sim.Core `RegionPlan` gate fix (see coordination note in `-TASKS.md`)

## Stage D — config surfacing
- [x] **D1** ped-demand knobs promoted to config (demo `PedDemandConfig` byte-identical)

## Stage E — offline prep, fixture, tests
- [x] **E1** `scripts/prep-ped-net.sh` + recipe
- [x] **E2** committed synthetic road-net fixture (`scenarios/_ped/roadnet_min/`, no proprietary data)
- [x] **E3** unit + smoke/regression tests (green without SUMO) — SumoRouteGraphNav unit tests (B) +
  road-net smoke/bare-net/determinism (C, A3) + robustness (E4); whole suite green with no SUMO on PATH
- [x] **E4** coordinate robustness (large/negative/3-D)

## Stage F — final gate (Opus) — delivered scope (all tasks except C5)
- [x] **F1** parity 657/4 byte-identical + bench hash `8F1CD03232BA88ED` (deterministic + par==single) +
  demo liveness/scene green (LiveCity 43/43); netstandard2.1 + consumer contract intact. **One approved
  Sim.Core diff** (the `RegionPlan` gate fix, option A) — provably inert on parity/bench/demo, not the
  "zero Sim.Core diff" the original gate wording assumed. **C5 remains OUT** (owned by the ORCA-ped
  session); the feature is complete for everything except that coupling.
