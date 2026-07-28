# Pedestrians — the current state of the subsystem

**This is the front door for the pedestrian subsystem.** It says what exists *today*, where the code
is, how to run it, and what is deliberately not built yet — for anyone (human or a future agent
session) asked to extend or fix a pedestrian feature. It is a status/index doc; the deeper "why"
lives in the design docs linked at the bottom.

> **Where it sits in the project.** SumoSharp's core is an *exact-parity* reimplementation of SUMO's
> **vehicle** microsimulation. Pedestrians are **not** a port of SUMO's person model (`MSPModel*`);
> they are a **from-scratch crowd layer** on the **live-reactivity axis**, validated by **behavioral /
> property tests, never golden FCD**. Every pedestrian feature is **default-off / inert-when-absent**,
> so the vehicle parity goldens and the determinism hash are byte-unchanged with pedestrians unused.
> The whole subsystem lives outside the hermetic parity gate's assertions but ships in the same repo.

The pedestrian test suite is **324 tests, all green** (`tests/Sim.Pedestrians.Tests`), plus a
cross-provider navmesh suite (`tests/Sim.Pedestrians.Nav.DotRecast.Tests`).

---

## The one idea that makes it all work: `server == IG`

A **low-power** pedestrian's pose is a **pure function of its inputs** — `(route, startTime, speed,
now)` for a plain walk, or `(timeline, now)` for a lively/weaving one. No neighbour state, no per-step
RNG, no hidden mutable state. The simulation server and every remote **image-generator (IG)** run the
*same* pure evaluator, so they cannot drift — they reconstruct **bit-for-bit identical** poses. That
is what lets the system broadcast a pedestrian's whole route **once** and have thousands of ambient
peds cost ~O(1) each and stream to many render channels within a tiny bandwidth budget.

The load-bearing pure evaluators (grep these first):

- `PathArcMotion` (`src/Sim.Pedestrians/Lod/PathArcMotion.cs`) — low-power walk pose vs. time.
- `ActivityTimeline` (`src/Sim.Pedestrians/Lod/ActivityTimeline.cs`) — Walk/Pause/Dwell/Interact pose vs. time.
- `LateralWeave` (`src/Sim.Pedestrians/Lod/LateralWeave.cs`) — the deterministic anti-overlap offset, applied *inside* the two evaluators above.
- `HeadlessIg` (`src/Sim.Pedestrians/Lod/HeadlessIg.cs`) — the reconstruction engine an IG runs; calls the exact same evaluators.

The guarantee is **executable**: `PedReplicationRoundTripTests` asserts `server == IG` bit-for-bit
across a real byte loopback for plain, lively, weaving, and promoted pedestrians.

---

## Capabilities (implemented + tested)

### Navigation & navmesh
- **Bake a walkable navmesh from a SUMO net** — sidewalks/crossings/walkingAreas → mitred walkable
  polygons with per-polygon half-width. `WalkablePolygonBaker`, `BakedPolygon`, `SumoWalkableSpace`,
  `SumoNavMesh : IPedNavigation` (`Navigation/Bake/`). A*-routing, `FindPath`, `HalfWidthsAlong`,
  connected-component analysis.
- **Connectivity stitching — three additive passes** in `Navigation/Bake/PolygonGraph.cs`, each
  preserving the "no shortcut" invariant: area-overlap/abutment (**P8-1b**), collinear sidewalk↔sidewalk
  continuation (**P8-1c**), and a non-geometric stitch from the net's declared ped `<connection>`s
  (**R1**, `AddNetConnectionAdjacency`). `NavmeshReachability` gives the reachable-component oracle.
- **Alternate provider** — a DotRecast-backed navmesh (`src/Sim.Pedestrians.Nav.DotRecast`), cross-checked
  to agree with `SumoNavMesh` on paths.

### Demand, LOD & motion
- **Poisson O-D demand** routed over the navmesh, seeded (SplitMix64 via `VehicleRng`, never
  `System.Random`) — `PedDemand` + `PedDemandConfig` (`Demand/`).
- **Density knob** — dial pedestrians-per-walkable-km, clamped to a LoS-C safe ceiling (`PedDensityKnob`).
- **Weighted endpoints** — spawns/arrivals drawn from POIs + walkable-fringe so every ped appears/vanishes
  at a legitimate edge, not mid-sidewalk (`SubareaDemand`, `PedPoi`/`PedPoiReader`).
- **Two-level LOD** — cheap **low-power** `PathArcMotion` dead-reckoning vs. reactive **high-power**
  `OrcaCrowd` (reciprocal collision avoidance); promote/demote with hysteresis driven by a movable,
  multi-source **interest field** (`InterestField`); `PedLodManager` is the engine, `PedestrianWorld`
  the facade.

### Liveliness (ambient realism, still low-power & server==IG)
- **Activity timelines** — Walk/Pause/Dwell/Interact; a ped can sip, sit at a table, or step inside a
  building (renders no disc while hidden) — `ActivityTimeline`.
- **Pre-scheduled two-ped meet / step-aside / talk** — authored into both timelines up front, no runtime
  negotiation (`SocialPlanner`).
- **Templated actors** — a looping waiter serving open-air tables (`WaiterScenario`).
- **Lively crowds** — seeded Pause beats spliced into routed O-D walks (`PedDemand` lively path).

### Deterministic lateral WEAVE (newest feature — PED-REALISM-1 "(b)", W1–W4)
The thing that stops low-power peds walking exact centrelines and passing *through* each other. Each
ped is offset onto its own deterministic half of the baked sidewalk width — a pure function of
`(route, seed, width, time)`, injected at the **shared** evaluator so **server == IG holds by
construction**. `LateralWeave` + `WeaveParams`.
- **W1** evaluator injection + per-ped/global seed (breathing counterflow interface); **weave-off is
  byte-identical** (default off, parity preserved).
- **W2** piecewise per-vertex half-width threaded from the bake; offset never exceeds the walkable half.
- **W3** verified **over the wire** (byte loopback → `HeadlessIg`), bit-for-bit.
- **W4** survives a **promote→demote** with **no pop** (re-anchored resume leg), still server==IG.
- Tests: `WeaveEvaluatorTests`, `LateralWeaveTests`, `PedDemandWeaveTests`, and the weave cases in
  `PedReplicationRoundTripTests`.

### Interaction with vehicles & the world
- **Crosswalk gate** — peds queue at the portal, surge on the walk phase; reads the net's `<tlLogic>` (or
  the live engine TL) — `CrossingGate`, `CrossingTlReader`, `CrossingSignalFactory`.
- **Cars stop for peds** — pedestrian footprints enter the lane engine as obstacles via
  `Engine.CrowdSource`; a car halts for a ped in its lane (emergent, not scripted).
- **Obstacle dodge + dynamic-blocker reroute** — local ORCA swerve around a box, and strategic
  recompute around an appearing blocker without thrashing (`BlockerRegistry`, `RerouteDriver`).
- **Parking coupling** — car↔lot maneuver mode switch, ped board/alight, mutual car/ped avoidance
  (`ParkingController`, `LotCoupling`, `PersonRideController`).

### Replication (server → wire → IG) & viewers
- **Transport-neutral wire** — `PedReplicationPublisher` → `IPedReplicationSink`/`Source` →
  `PedReplicationReceiver` → `HeadlessIg`. Routes/timelines sent **once** per leg; low-power peds emit
  **zero per-step bytes**. Codecs: `FrameCodec` (quantized crowd/PathArc), `ActivityTimelineWire`
  (lossless doubles). In-process byte loopback: `InMemoryPedReplicationBus`.
- **DR-error gating + global bandwidth governor** — opt-in; a high-power ped's position is sent only
  when the receiver's own dead-reckoning would drift out of tolerance, capped by a global Mbit/s budget
  (`PedPublishScheduler` + `PedDrErrorPublishPolicy`, `PedBandwidthGovernor`). Default path is
  byte-identical to ungated.
- **Reconstruction render clock** — playout-delay clock + capped-correction smoothing so a sparse/gated
  stream (and the promotion DR-switch) render with no visible pop (`PedRemoteReconstructor`).
- **Real DDS binding** — `src/Sim.Replication.Dds` (`DdsPedReplicationSink`/`Source`, topics
  `sumo/ped-{crowd,patharc,activity,lifecycle}`); bytes identical to the in-memory bus, proven over real
  CycloneDDS by `src/Sim.PedDdsLoopback`. **Caveat:** these DDS projects are **out of `Traffic.sln`**,
  so the offline `dotnet test` gate does not exercise them.
- **Viewers** — the native raylib viewer draws the crowd **reconstructed from the wire** with ground-truth
  outline rings so parity is *visible* (`src/Sim.Viewer/RemotePedOverlay.cs`); the HTML replay gallery
  renders many ped scenes (`src/Sim.Viz/SceneGen.cs`); a Godot City3D path consumes peds over DDS
  (`demos/City3D`).

### Panic evacuation on foot
- **External-obstacle exodus** (shipped, `Sim.Evac`): panicked drivers abandon cars and flee on foot as
  an `OrcaCrowd`; to the lane engine they are always obstacles. Demonstrated at 10k-vehicle-class scale.
- **Routed foot-evac on the real walkable net** (P5-1(B), `EvacDistrictDirector` over `PedestrianWorld`):
  a deterministic ambient population panics on incident proximity and **routes** to its nearest safe zone
  (bending around blocks, not straight-lining — measured routed-vs-radial). Scenario
  `scenarios/_ped/evac-district`.

---

## Code map

```
src/Sim.Pedestrians/            the crowd layer (all of the above except transport/viewers/evac)
  (root)     PedestrianWorld (facade), PedNetwork(+Parser), PedPoi(+Reader), PedDensityKnob,
             SubareaDemand, SubareaFcdRecorder, SubareaManifest, PersonFcdWriter, PedSpawnPolicy
  Lod/       PedLodManager, PathArcMotion, ActivityTimeline, LateralWeave, InterestField,
             HeadlessIg, PedPublisher, PedReplicationPublisher/Receiver, PedRemoteReconstructor,
             PedPublishScheduler, PedBandwidthGovernor/Meter, SocialPlanner, WaiterScenario
  Navigation/       IPedNavigation, BlockerRegistry, RerouteDriver
  Navigation/Bake/  SumoNavMesh, SumoWalkableSpace, WalkablePolygonBaker, BakedPolygon,
                    PolygonGraph (the 3 stitch passes), NavmeshReachability, PedRouteController
  Demand/    PedDemand, PedDemandConfig, PedLivelinessConfig
  Crossing/  CrossingGate, CrossingTlReader, CrossingSignalFactory, *CrossingSignal
  Obstacles/ BoxObstacle, MovingBlocker            Parking/ ParkingController, LotCoupling, PersonRideController
src/Sim.Pedestrians.Nav.DotRecast/   alternate DotRecast navmesh provider
src/Sim.Replication/            ped wire codecs + InMemoryPedReplicationBus (transport-neutral seam)
src/Sim.Replication.Dds/        real CycloneDDS binding (out of Traffic.sln)
src/Sim.PedDdsLoopback/         live DDS server==IG proof (out of Traffic.sln)
src/Sim.Evac/                   EvacDistrictDirector (routed foot-evac over PedestrianWorld)
src/Sim.Viewer/                 RemotePedOverlay / PedOverlay (peds rendered from the wire)
src/Sim.Viz/                    SceneGen ped scene builders + --ped-* CLI
src/Sim.BenchPedLod, Sim.BenchPedNet, Sim.BenchCrowd   ped micro-benchmarks
```

## Public API (what a host app calls)

```csharp
// Build the navmesh once, then feed it to any facade:
PedNetworkParser.Load(net) -> WalkablePolygonBaker.Bake(...) -> new SumoNavMesh(...)  // : IPedNavigation

// Simplest facade:
var world = new PedestrianWorld(nav);
world.AddWalker(id, origin, destination, maxSpeed, radius, now);   // or AddLivelyWalker(id, timeline, ...)
world.Step(now, dt);
Vec2 pos = world.PositionOf(id, now);   PedDrModel m = world.ModelOf(id);

// Or drive demand directly: new PedDemand(config, nav, lodManager).Step(now, dt, field, obstacles)
// Evac: new EvacDistrictDirector(...).Step(dt)
```

## How to run it

```bash
# Render any pedestrian scene to a self-contained HTML replay (no SUMO, no GPU):
dotnet run -c Release --project src/Sim.Viz -- --ped-dense-city out.html   # busy demo-city block
dotnet run -c Release --project src/Sim.Viz -- --ped-weave-city  out.html   # one intersection, cars+weave
dotnet run -c Release --project src/Sim.Viz -- --ped-remote      out.html   # crowd reconstructed from the wire
# other scenes: --ped-od-routing --ped-lively-crowd --ped-crossing-gate --ped-lod-promotion
#               --ped-dodge --ped-reroute --ped-parking --ped-liveliness --ped-social --ped-waiter
# sub-area person FCD (feeds the shared car+ped replay): --ped-subarea-fcd out.fcd.xml [--weave]

# The pedestrian test suite (part of the offline gate; no SUMO needed):
dotnet test tests/Sim.Pedestrians.Tests
```

All 14 pedestrian gallery demos are registered in `scripts/gen-demos.sh` under the **Pedestrians**
category and ship to the live gallery (https://pjanec.github.io/SumoSharp/).

## Committed scenarios (`scenarios/_ped/`)
- `poc0-crossing-plaza` — the foundational 4-arm signalized junction (sidewalks + drivable lanes); most ped demos run here.
- `demo_city/{kernel,box}` — the synthetic ~4.75 km demo-city; `box` bakes to **1** connected component and backs `--ped-dense-city`.
- `subarea-box`, `subarea-irregular` (`pedfrag_witness`), `subarea-pedfrag2` — sub-area + navmesh-fragmentation witnesses.
- `evac-district` — walkable district for routed foot-evac.

---

## Deliberately not built yet (parked / deferred)
- **R2 micro-scenario/venue registry** — PARKED (design retained). The hard-coded `WaiterScenario`/
  `SocialPlanner` exist and are tested; only the *data-driven* template registry is parked. R3 drive-away
  / R4 garage / R5 park-region POI fields load but are inert (weight 0). See `PEDESTRIAN-R2-SCENARIO-REGISTRY-DESIGN.md`.
- **P8-2 live-camera appearance gate** — the `PedSpawnPolicy` mechanism landed + tested, but per-tick
  visible-walkable-edge wiring is held until a host publishes that set (no-op on the by-construction path).
- **P8-4b dynamic per-crossing throughput guard** — deferred (needs the vehicle-yields-at-crossing seam); only the static LoS-C ceiling shipped.
- **P6-2 phase-2 SoA reorder** — region-task decomposition shipped (bit-identical, default-off), but the throughput target awaits a struct-of-arrays reorder.
- **Real-net (OSM/Geneva) navmesh re-validation** — the connectivity thresholds (135° continuation angle,
  reachable-area fraction) are **provisional, validated only on synthetic witnesses**; a genuine cropped
  net is the missing test. See `SUMOSHARP-P8-1-REAL-NET-NAVMESH.md`.

Full deferred ledger: `docs/PEDESTRIAN-P8-BACKLOG.md`.

## Deeper docs (read next, in this order)
1. `PEDESTRIAN-OVERVIEW.md` — the WHAT: goals, the 7 requirements, the live-reactivity-not-parity framing.
2. `PEDESTRIAN-TRACKER.md` — the authoritative done/parked map across every stage (P0–P8 + liveliness + weave).
3. `PEDESTRIAN-DESIGN.md` — the HOW: layered architecture, LOD state machine, the server==IG identity.
4. `PEDESTRIAN-WEAVE-PRODUCTION-DESIGN.md` + `PEDESTRIAN-LOWPOWER-AVOIDANCE-DESIGN.md` — the newest feature and why.
5. `PEDESTRIAN-DDS-TRANSPORT-DESIGN.md` — the real DDS binding and its loopback proof.
6. `PEDESTRIAN-SESSION-HANDOFF.md` — a fresh-session orientation (landed/parked, key files, build/test/run).
