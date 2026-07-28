# SUMOSHARP-HANDOFF.md — session handoff

> **STATUS: ARCHIVED (2026-07-28)** — Session handoff for the NuGet-packaging / public-API branch, pinning 250->294 tests. Disconnected from the live board and its counts are far behind the current gate. **Kept rather than deleted because `docs/SUMOSHARP-API.md` still defers to "the handoff's Remaining work" for specifics it does not itself duplicate** (vehicle-slot recycling, the DR-layer design) -- deleting it would break that pointer.


Continuation notes for the **SumoSharp** library/packaging effort. Pairs with
`docs/SUMOSHARP-API.md` (the design of record + landed-status per section) and
`docs/LANELESS-DIRECTION.md` (the sibling laneless/RVO branch).

## TL;DR

- **Branch:** `claude/sumo-csharp-nuget-strategy-4vlkki` (all work pushed).
- **Gates (must stay true after every change):**
  - `dotnet test` → **0 failed, 1 skipped**; the pass count grows as new-surface tests are added
    (**250** at the start of the packaging work → **294** after this session's B13–B24 tests).
  - `Sim.Bench` determinism hash → **`909605E965BFFE59`** (single **and** parallel).
- **What exists now:** the whole Phase-1 public API + NuGet packaging + a working browser-live demo.
  Every addition is *additive / inert-when-absent*, so it is byte-identical where the new paths are
  unused (that is why the hash never moved).
- **Prime rule (unchanged):** parity is the iron law (`CLAUDE.md`). The vehicle SoA and the
  car-following / lane-change / junction math are **frozen**; everything below is either a new isolated
  subsystem, a facade over existing fields, or a projection produced in the Export/Step path.

## How to build / test / run

```bash
dotnet build Traffic.sln
dotnet test  Traffic.sln                      # the offline parity gate (no SUMO needed)
dotnet run --project src/Sim.Bench -c Debug   # prints the determinism hash (expect 909605E965BFFE59)

# NuGet packages
dotnet pack src/Sim.Core/Sim.Core.csproj -c Release -o ./nupkgs
dotnet pack src/Sim.Ingest/Sim.Ingest.csproj -c Release -o ./nupkgs

# Browser-live demo (then open http://localhost:5055, click the road to drop an obstacle)
dotnet run --project src/Sim.LiveHost
```

Environment note: the .NET 8 SDK is **not** committed. On a fresh VM install it with
`sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0` (the pinned point-release debs go stale,
so `apt-get update` first). SUMO is not needed for `dotnet test`.

## What landed this session (commits, newest first)

| Commit | What |
|---|---|
| `25fb343` | **`PublishScheduler`** (`Sim.Replication`): reusable per-step adaptive-rate loop (last-sent bookkeeping + `IPublishPolicy` + despawn pruning), keyed by `VehicleHandle`, extracted from the demo host so any transport (DDS/TCP) drives §7 the same way; `RungB24`. |
| `5932989` | **Adaptive publish rate wired live** (`Sim.LiveHost`): `DefaultPublishPolicy` run once per sim step; frame is a `{alive:[ids], vehicles:[published DR records]}` split; client tracks each vehicle independently and dead-reckons deferred ones. ~55–80 % sent/step; HUD shows the saving. Off the parity path. |
| `c86b6c5` | **Junction demo scenarios** `samples/junctions/{cross,tee,bend,acute}` (viewer inputs, not parity) + CA2014 fix (hoist the lane-window `stackalloc` out of the per-vehicle loop in `Engine.PublishReadState`). |
| *(this session)* | **Dead-reckoning layer** (`SUMOSHARP-DEADRECKONING.md`): shared `DrModel` seam; `PoseResolver` (chord + swept-path off-tracking, `RungB20`); opt-in `Engine.RenderMode` (`RungB21`); `SumoSharp.Replication` (blob codec + records + `IPublishPolicy`, `RungB22`); `SumoSharp.Replication.Dds` (CycloneDDS topics, out of `Traffic.sln`); `Sim.LiveHost` `chord`/`corner` arg. Coordinated with laneless via issue #3 (DR1–DR4) — **now closed**, contract frozen (see "Dead-reckoning seam — CLOSED" below). |
| *(this session)* | **Dead-reckoning inputs** (§5.1): `Acceleration` read column (getAcceleration analog) + `GetUpcomingLanes` (lane-handle path ahead); `RungB19`. Foundation for the networked DR layer (below). |
| *(this session)* | **Vehicle-slot recycling** (§9): `Despawn` frees the `EntityIndex`; next runtime `SpawnVehicle` reuses it (rebuild-in-place + reset idx-keyed side state + bumped generation). `CreateRuntime` split into `BuildRuntime`/append + `AllocateRuntime`. Inert for goldens; `RungB18`. |
| *(this session)* | **Sim.LiveHost**: verified builds/runs after the core changes (Playwright smoke); enabled the snapshot pool server-side + client-side entity interpolation for smooth 60 fps playback. |
| *(this session)* | **Publish + CI workflows** `.github/workflows/publish.yml` (tag-gated pack+push, version-from-tag, SourceLink fetch-depth 0) and `ci.yml` (build/test/determinism-hash on push/PR). |
| *(this session)* | **ns2.1 consumer sample** `samples/SumoSharp.GameHostSample` (multi-target net8.0/ns2.1; `GameHost` drop-in + runnable net8 demo; `RungB17`). |
| *(this session)* | **Dense edge handles** `GetEdge`/`GetEdgeId`/`EdgeCount` + int Spawn/route overloads (`0ceeaf0`, `RungB16`). |
| `3ac73c1` | **Async runner — snapshot pool (opt-in):** `EnableSnapshotPool(cap=3)` reuses backing arrays across Ticks (`RungB15`). Default off; contract unchanged when off. |
| `ce37400` | **Async runner — two-frame interpolation hook:** `PreviousSnapshot`, `InterpolationAlpha`, `TryInterpolateVehicle` → `InterpolatedVehicle` (`RungB14`). |
| `1a2d685` | **`netstandard2.1` multi-target** on `Sim.Core` + `Sim.Ingest` (Unity/Godot reach): polyfills (`src/Shared/NetstandardPolyfills.cs`), `System.Memory` on ns2.1, 4 net8-only sites guarded/rewritten, `RungB13` guard test. Gate unchanged (`909605E965BFFE59`; 253/1/0). |
| `958b5ad` | **Phase 2** browser-live demo (`src/Sim.LiveHost/`) |
| `e818cc6` | **Phase 0** NuGet packaging (`SumoSharp.Core` + `SumoSharp.Ingest`) |
| `e3756be` | Removed the transitional **string obstacle API**; handle-only + all callers migrated |
| `d1778dc` | Async **`SimulationRunner`** (command dispatcher + published snapshot) |
| `7699495` | Per-Step **lifecycle event buffer** (`Engine.Events`, Departed/Arrived) |
| `c439002` | **Geometry-3D** `z` ingestion + `PosZ` read column |
| `f08407b` | **Runtime demand**: `LoadNetwork`, `DefineVType`, `SpawnVehicle`, reroute, despawn |
| `f4ca712` | Stepped **SoA read surface** + public `Step()` |
| `c5aedaf` | Handle-based **struct-of-arrays obstacle store** |
| `2ec4062`–`def8d01` | Design doc + laneless-branch coordination |

## Public API surface (all on `Sim.Core.Engine` unless noted)

- **Obstacles (handle-only):** `int GetLane(string)`, `ObstacleHandle AddObstacle(int laneHandle, …)`,
  `AddMovingObstacle(...)`, `UpdateObstacle(ObstacleHandle, …)`, `RemoveObstacle(ObstacleHandle)`,
  `ClearObstacles()`. Store: `ObstacleStore.cs` (direct-mapped SoA + generational handle + dense active
  list + reserved `AvoidanceClass` byte). `ExternalObstacle` is now a `readonly record struct`.
- **Stepped read surface:** `Step()` / `Step(int)`, `VehicleCount`, `StepCount`, `CurrentTime`;
  columnar spans `VehicleHandles`, `PosX/PosY/PosZ`, `Angle`, `Speed` (float), `LaneHandles` (int),
  `Pos`/`PosLat` (double), `VehicleIds/VehicleTypes/LaneIds`; `TryGetVehicle(VehicleHandle, out VehicleState)`.
  Backing: `VehicleReadBuffer.cs`, populated only by `Step()` (Run() pays nothing).
- **Runtime demand:** `LoadNetwork(net[, cfg])`, `DefineVType(VTypeParams) → VTypeHandle` (+ `DefaultVType`,
  `TryGetVType`), `SpawnVehicle(...)` (edge-list and from→to), `GetLifecycle`, `Despawn`,
  `SetDestination`, `Reroute`. Backed by mutable `_vTypesById`/`_routesById` seeded from `_demand`.
  SUMO-parity **queued insertion** (spawned vehicle is `Pending` → `Active`).
- **Lifecycle events:** `ReadOnlySpan<SimEvent> Events` (`SimEvent.cs`), diffed each `Step()`.
- **Async runner:** `SimulationRunner` (`SimulationRunner.cs`) + immutable `SimulationSnapshot`
  (`SimulationSnapshot.cs`): `Post`/`Invoke`/`Tick`/`Start`/`Stop`/`Pause`/`Resume`/`SpeedMultiplier`.
- **Geometry-3D:** `Lane.ShapeZ` + `LaneGeometry.ElevationAtOffset`; `PosZ` is 0 on 2-D nets.

New handle/value types: `ObstacleHandle`, `VehicleHandle` (both 32-bit index + 16-bit generation, the
host game engine's convention), `VTypeHandle`, `AvoidanceClass`, `VehicleLifecycle`, `VTypeParams`,
`VehicleState`, `SimEvent`.

## Tests (new this session)

`RungB7`..`RungB12` cover the new surface; `RungB1/B3/B5/B6` were migrated to the handle obstacle API.
- B7 obstacle handle store (generational contract), B8 stepped read surface (Step == Run bit-for-bit),
  B9 runtime spawn/reroute/vType/LoadNetwork, B10 geometry-3D, B11 lifecycle events, B12 async runner
  (incl. a threaded smoke test).

## Remaining work (prioritized, none blocking)

1. ~~**`netstandard2.1` multi-target** on `Sim.Core` + `Sim.Ingest`~~ — **DONE** (see below / API §3).
   ~~Unity/Godot sample~~ → landed as `samples/SumoSharp.GameHostSample` (a ns2.1-consumable `GameHost`
   integration class + runnable net8 headless demo; `RungB17`). Per steer, this replaces an in-editor
   Unity/Godot project (neither engine can run in this environment).
2. ~~**Publish CI** to nuget.org (GitHub Actions: pack + push `.nupkg`/`.snupkg`, tag-gated)~~ — **DONE**
   (`.github/workflows/publish.yml` on `v*` tags + `ci.yml` build/test/hash on every push/PR; API §1).
   To actually publish: set the `NUGET_API_KEY` repo secret, bump `Version` in `Directory.Build.props`
   (or just tag — the workflow packs at the tag's version), then push a `vX.Y.Z` tag.
3. ~~**Async runner refinements (§7):** two-frame **interpolation hook** + **snapshot pool**~~ — **DONE**
   (commits `ce37400`, `3ac73c1`; see API §7). Both additive/async-only; pool is opt-in.
4. ~~**`GetEdge(string) → int`** dense edge handles (§9)~~ — **DONE** (commit `0ceeaf0`; API §9).
5. ~~**Vehicle-slot recycling** on `Despawn` (§9)~~ — **DONE** (API §9). `Despawn` frees the slot; the next
   runtime `SpawnVehicle` reuses it (rebuild-in-place + reset the idx-keyed side state + bumped generation).
   `CreateRuntime` was split into `BuildRuntime` + append-wrapper (golden path byte-identical) and a
   recycle-aware `AllocateRuntime`. Inert for goldens (free list only fills on `Despawn`). `RungB18`;
   `RecycleVehicleSlots=false` restores monotonic indices. Noted nuance: recycled slot reuses its RNG seed
   (inert at `sigma=0`; per-reuse salt is a future `sigma>0` refinement).
6. **Lifecycle events**: `InsertionFailed`/`Teleported` are defined but not emitted (no insertion
   timeout; teleport off). Wire if/when those engine behaviors exist.
7. **`VTypeParams` sublane fields** (`maxSpeedLat`/`latAlignment`/`minGapLat`) — added at the laneless
   merge, which owns those `VType` additions.

## Networked dead-reckoning layer — design (inputs landed §5.1; full layer pending scope confirmation)

**Full design doc: `docs/SUMOSHARP-DEADRECKONING.md`** (read that for the complete treatment; cross-branch
coordination ask is in `SUMOSHARP-API.md` §16). Summary below.

Motivation (from the user): 10k+ vehicles; reading location for render/replication must not block the sim
step (use the async runner's immutable snapshot — it already doesn't); renderer at 60–120 Hz over a ~10 Hz
sim, often on a **different machine**; rate-limit network updates to ~10 Hz (and lower for predictable
vehicles). Grounded facts: sim is 1-D arc-length; `Acceleration` + `GetUpcomingLanes` now exposed (§5.1);
SUMO `getPosition`=front on centerline (`MSVehicle.cpp:1265`), `computeAngle`=back→front **chord** (1515) —
our port emits front **tangent** (parity-passing but a fidelity gap for long veh on curves; Angle IS
compared, `TrajectoryComparator:179`).

Design to implement (once confirmed):
- **Lane-relative, handle-based, network-transferable packet** per vehicle per update: `{handle, laneHandle,
  pos, posLat, speed, accel, upcomingLaneHandles[k], drModel, (vx,vy for free model)}` (~20–30 B; **no
  strings** — send an id table once). Static lane geometry sent once (LiveHost already does).
- **Portable pose-resolver** (ns2.1-clean, mirrorable in JS): integrate `pos'=pos+v·dt+½·a·dt²`, walk shared
  lane polylines via `PositionAtOffset`; produce (x,y,z,heading). Reproduces SUMO's **chord heading**
  (place front at `pos`, back at `pos-length` along geometry) and can apply a **renderer-only** long-vehicle
  corner-cut correction from physical params (length/width) — sim Angle column untouched (parity-safe).
- **Per-vehicle DR-model tag:** `LaneArc` (lane-bound, curve-following) vs `FreeKinematic` (laneless/RVO or
  lateral-dodge `LatOffset≠0` → extrapolate by velocity vector, no lane path) vs `Stationary`. Vehicle
  switches model when it starts dodging / enters RVO. **Coordinates with the laneless sibling branch**
  (which produces the free-model vehicles) — likely a shared enum on the neutral seam.
- **Adaptive publish rate:** predictability/error-budget heuristic (accel magnitude, obstacle proximity,
  RVO-active) picks each vehicle's next-publish time — steady followers ~1 Hz, near-obstacle/braking/RVO at
  full 10 Hz. Renderer keeps extrapolating between packets; reconcile (snap/blend) on arrival.
- **Two consumption modes, one packet:** extrapolation (0 latency, clamp at a published stop-line) or
  interpolation (~1-frame delay, exact).
Open forks needing steer: packet wire format / where it lives (new `SumoSharp.Runtime`?); async
`SimulationSnapshot` gains `accel`+`drModel`+path columns; how the adaptive scheduler is exposed; the
laneless coordination for `drModel`. **Chord-heading sim-side fix stays out unless separately parity-gated.**

## Dead-reckoning seam — CLOSED (issue #3)

The DR cross-branch coordination (issue #3) is **resolved and closed**; the frozen contract, agreed by both
branches (hash `909605E965BFFE59` held on both):
- **Vehicles are `LaneArc`/`Stationary` only.** `FreeKinematic` comes solely from the laneless crowd source
  (`OrcaCrowd`/`WorldDisc`), never from a swerving *vehicle* (a straight world-line prediction would drift a
  car off a curved lane; `LaneArc` + a raised publish rate reconstructs the dodge, and it keeps world
  `(vx,vy)` off the vehicle wire — DR3).
- **`Engine.Manoeuvring` (span) / `Engine.IsManoeuvring(handle)`** is the separate per-vehicle rate signal
  the laneless branch added (commit `223580f`): true during a reactive lateral manoeuvre. The DR publisher
  feeds it into `PublishSignals.LaneChangingOrManoeuvring` (our `DefaultPublishPolicy` already consumes it).
- **`DrModel.cs` is byte-identical on both branches** (mirrored like `RvoNeighbor`); merge-order-independent.
- **At merge:** the publisher swaps its local `LaneArc`/`Stationary` classifier for the laneless `DrModels`
  column and reads `Manoeuvring` for the rate — **no packet-shape change**. The `Sim.LiveHost` demo currently
  uses a `posLat` stand-in for the manoeuvring bit (see `SimHost.BuildFrameJson`); that is the one line the
  merge replaces with `Engine.Manoeuvring[i]`.

## Coordination with the laneless/RVO branch (`claude/sumo-phase-2-planning-p3w7kh`)

See `SUMOSHARP-API.md` §15 for the full record. Key points for the merge:
- **`RvoNeighbor` is the sole seam.** This branch **owns** the obstacle store; the RVO layer consumes it
  via that neutral value abstraction. The store already carries the columns the Stage-3 adapter needs
  plus the reserved `AvoidanceClass` byte.
- **Merge order:** this branch's SoA store lands first, then their `ComputeRvoLateral` adapter retargets
  onto it. RVO Stages 1–2 are order-independent.
- **Heads-up:** this branch **removed** the string obstacle API — any laneless-branch test/demo that
  still calls `AddObstacle(string id, …)` must migrate to `GetLane` + handles at merge (mechanical).
- **Shared acceptance gate:** determinism anchor `909605E965BFFE59`, byte-identical goldens.
- Both branches additively edit the same 8 files (`Engine.cs`, `VehicleExportSnapshot.cs`,
  `DemandModel.cs`, `DemandParser.cs`, `VTypeDefaults.cs`, `ScenarioConfig.cs`, `TrajectoryPoint.cs`,
  `ToleranceConfig.cs`); whoever merges second reconciles the additive hunks.

## Gotchas / non-obvious decisions

- **`Step()` vs `Run()`:** `Step()` (host loop) publishes the read buffer + events; `Run()` (batch,
  returns `TrajectorySet`) does not — that is what keeps the parity/determinism path zero-overhead. They
  advance the sim identically (B8 proves bit-exact).
- **Snapshot precision:** render columns are `float` (`PosX/Y/Z`, `Angle`, `Speed`); parity-exact values
  are `double` (`Pos`, `PosLat`, and `SpeedExact` on the snapshot / `VehicleState.Speed`).
- **Handles are a distinct id space** from the host ECS; 16-bit generation wraps at 65 536 slot recycles
  (accepted, matches the host engine).
- **`Despawn`** surfaces as an `Arrived` event with a stale-generation handle (the host initiated it).
- **Obstacle `Id`** is now always empty (was only the string-API key); `ComputeLateralEvasion`'s
  tie-break among overlapping obstacles falls to insertion order — deterministic, and no committed
  scenario has such an overlap.
- **SourceLink warning** ("source control information is not available") appears only because this
  environment's git remote is a local proxy URL. On real GitHub it resolves; pin `RepositoryUrl` +
  commit in CI to silence it.
- **`LoadNetwork` default config:** Begin 0, End 1e9, 1 s Euler steps, teleport off, sigma-neutral,
  seed 42 (matches `Engine.Seed`).
- **`Sim.LiveHost`** parses the net itself (via `NetworkParser`) for drawing + the screen→lane
  projection; lane handles match the engine's because both parse the same file in the same order.

## File map (new/changed this session)

Core: `ObstacleHandle/ObstacleStore/AvoidanceClass/ExternalObstacle`, `VehicleHandle/VehicleState/
VehicleReadBuffer`, `VTypeHandle/VTypeParams/VehicleLifecycle`, `SimEvent`, `SimulationRunner/
SimulationSnapshot`, plus additions in `Engine.cs`/`IEngine.cs`.
Ingest: `NetworkModel.cs` (Lane.ShapeZ), `NetworkParser.cs` (ParseShapeZ), `LaneGeometry.cs`
(ElevationAtOffset).
Packaging: `Directory.Build.props`, `Sim.Core.csproj`, `Sim.Ingest.csproj`, package READMEs.
Demo: `src/Sim.LiveHost/` (Program/SimHost/HtmlPage + README).
Tests: `RungB7`..`RungB12`; `RungB1/B3/B5/B6` migrated.
Docs: `SUMOSHARP-API.md` (status per section), this file.
