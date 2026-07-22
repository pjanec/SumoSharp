# Live-city in the interactive viewers — design (HOW)

**WHAT (from the session request):** bring the coupled **cars + pedestrians + crossing-yield** "live
city" scene — which today exists only as the 2D `Sim.Viz --live-city` HTML export — into the three real
interactive viewers, and make it a first-class, demonstrable mode:

1. **All three viewers show the coupled scene:** Raylib 2D (`src/Sim.Viewer`), City3D local in-process
   (`demos/City3D`), and City3D remote over DDS.
2. **Two viewing modes in the local in-process viewers:** run the sim **live** (real-time), and **replay
   from a recording** as if it were streaming live — with a **timeline slider** (jump anywhere in the sim
   range), **play/pause/restart/speed/frame-step** controls (the UX `Sim.Viz`'s HTML player already has).
3. **Click a vehicle** → highlight it and show its **identity** (the real SUMO id, so a human can refer to
   "vehicle X").
4. **Honor the net's height coordinate (Z) end-to-end**, including across DDS. Current committed nets are
   flat, but future nets are 3-D (tunnels, multi-level roads); the wire must never drop Z.

This doc is the mechanism. It does **not** restate the coupled-sim recipe itself — that is
`src/Sim.Viz/SceneGen.cs` `BuildLiveCity` (the reference implementation) and the frozen behavioural
decisions in `docs/SUMOSHARP-LIVE-CITY-DECISIONS.md` / status in `docs/LIVE-CITY-STATUS.md`. It also does
not restate the City3D packaged-consumer architecture (`docs/DEMO-CITY3D-DESIGN.md`), the DR reconstruction
pipeline (`docs/SUMOSHARP-VIEWER-DR-SMOOTHING.md`), or the kinematic smoothing facade
(`docs/VIEWER-KINEMATIC-SMOOTHING-DESIGN.md`). Task breakdown + success conditions:
`docs/LIVE-CITY-VIEWERS-TASKS.md`; tracker: `docs/LIVE-CITY-VIEWERS-TRACKER.md`.

---

## Design tenets

1. **One coupled-sim recipe, not three.** The `BuildLiveCity` wiring (net parsed twice → ped navmesh baked
   → `Engine` + `PedLodManager` via `PedDemand` → `Engine.CrowdSource = CompositeFootprintSource(
   HighPowerFootprints, CrossingOccupancySource)` → strict per-tick order) is extracted **once** into a
   reusable, real-time-steppable host package, `SumoSharp.LiveCity`. All three viewers and the DDS producer
   consume that host; none re-ports the recipe.
2. **Live vs. replay differ only in the source.** Exactly as local-vs-DDS already differs only in the
   `IReplicationSource` (`docs/DEMO-CITY3D-DESIGN.md` tenet 3), replay differs from live only by swapping a
   **file-backed replication source** in for the in-memory/live one. Reconstruction, rendering, motion
   smoothing, controls, and click-pick are identical across both modes.
3. **Parity is the iron law; every engine-side change is additive and inert for goldens.** No committed
   golden scenario sets `CrowdSource` (stays null → byte-identical). The only `src/` edits are **additive
   fields on the replication wire** (a Z component on lane geometry, a name string on the spawn lifecycle) —
   neither is in the parity golden path (goldens are engine FCD/state XML, not the replication wire). The
   standing gate (`dotnet test Traffic.sln` counts + `Sim.Bench` determinism hash) is captured fresh at the
   start of each `src/`-touching task and must be unchanged when it is ticked.
4. **Identity and elevation are cheap, one-time, or already-local — never hot-path.** The vehicle **name**
   travels **once per entity** on the spawn lifecycle event (a handle→name dictionary the consumer builds),
   never in the per-frame vehicle record — at 100k entities the frame payload is untouched. The **Z**
   coordinate is static lane geometry, published once with the network, not per frame.
5. **Determinism preserved.** `SumoSharp.LiveCity` reuses `BuildLiveCity`'s seeded SplitMix64 car PRNG and
   per-entity ped seeding; no `System.Random`. A recording is deterministic; replaying it reproduces the
   recorded trajectory exactly. Two live runs with the same seed/knobs are byte-identical.
6. **Respect the packaging boundaries.** `Sim.Viewer.Raylib` / `Sim.Viewer.Core` stay free of domain
   dependencies (`Sim.Pedestrians`/`Sim.Evac`/`Sim.Core.Bridge`) — the new combined overlay lives in the
   `Sim.Viewer` exe (like `EvacOverlay`). City3D consumes `SumoSharp.LiveCity` and the extended
   `SumoSharp.Replication` from its **local NuGet feed**, never a `ProjectReference` into `src/`.

---

## Component map

```
NEW  src/Sim.LiveCity/                     SumoSharp.LiveCity  (net8.0;netstandard2.1, IsPackable)
       LiveCitySim.cs                      the coupled recipe as a real-time Step(dt) host
       LiveCityConfig.cs                   dataset dir, crop, car cap, yield on/off, seed, LC-min-speed
       LiveCitySnapshot.cs                 per-frame read-back: cars[], peds[], crossing-occupancy
     refs: Sim.Core, Sim.Ingest, Sim.Pedestrians          (mirrors SceneGen.BuildLiveCity's deps)

ADD  src/Sim.Replication/                  (additive to SumoSharp.Replication)
       GeometryCodec.cs        + Z on LaneGeo.Points  (x,y) -> (x,y,z); format-version bump
       IReplication.cs         + Name on LifecycleRecord (spawn only)
       Recording/
         ReplicationRecorder.cs            IReplicationSink decorator: forwards + appends to a .simrec file
         ReplicationFileSource.cs          IReplicationSource over a .simrec file (seekable)
         PedReplicationFileSource.cs       IPedReplicationSource over the ped side of the recording
         SimRecFormat.cs                   the on-disk container (typed, timestamped records + time index)

ADD  src/Sim.Host/ReplicationPublisher.cs  populate LaneGeo.Z from Lane.ShapeZ; Name from Engine.VehicleIds
ADD  src/Sim.Replication.Dds/DdsTopics.cs  Z on the geometry struct; Name on DdsVehicleLifecycle

VIEWERS
  Raylib 2D  src/Sim.Viewer/
       DemoCatalog.cs / DemoSession.cs     new DemoKind.LiveCity
       LiveCityOverlay.cs                  drives LiveCitySim (live) or a file source (replay);
                                           draws cars (Renderer path) + peds (regime-colored) in one frame;
                                           nearest-vehicle click-pick + highlight/label; playback panel
  City3D     demos/City3D/
       CityLib/LiveCitySource.cs           wraps LiveCitySim; exposes car+ped read-back + names + Z
       CityLib/PlaybackClock.cs            shared live/replay clock (play/pause/seek/speed)
       Viewer/Main.cs                      drop the `if(_peds) return` forks; LiveCity local+replay+dds paths;
                                           ray-pick vehicle; Godot Control playback UI; Z-seated meshes
  Producer   src/Sim.Host.App/            combined cars+peds DDS producer over ONE net (LiveCitySim)
```

---

## 1. `SumoSharp.LiveCity` — the shared coupled-sim host

`LiveCitySim` is `BuildLiveCity`'s wiring turned into a real-time object. Constructor takes a
`LiveCityConfig` (dataset dir defaulting to `scenarios/_ped/demo_city/box`, crop rect, `CarTargetConcurrent`,
`LaneChangeMinSpeed`, `YieldEnabled`, seed). It performs, once, the **port checklist** already extracted
from the reference (see `docs/LIVE-CITY-STATUS.md` "Key files" and the `BuildLiveCity` walkthrough):

- `NetworkParser.Parse(netPath)` + `PedNetworkParser.Load(netPath)` (same `net.xml`, two readers).
- `WalkablePolygonBaker.Bake` → `SumoNavMesh`.
- crossing polygons split by `CrosswalkSignals.FromNet`; `CrossingOccupancySource(cropCrossingPolys, 0.3)`.
- `PedPublisher` + `PedLodManager` + `PedDemand(config, nav, manager)`; `InterestField` promotion pocket.
- `Engine` loaded with **step-length 0.5**, `lanechange.duration 2.0`, `default.speeddev 0.0`;
  `LaneChangeMinSpeed` from config; `DefineVType(passenger, sigma 0)`.
- `engine.CrowdSource = YieldEnabled ? new CompositeFootprintSource(manager.HighPowerFootprints,
  crossingOccupancy) : manager.HighPowerFootprints`.

**`Step(double dt = 0.5)`** performs the reference's exact, load-bearing per-tick order:
`(a)` spawn cars up to the cap on crop drivable edges (deterministic PRNG) → `(b)` `demand.Step(now, dt,
field, none)` → `(c)` gather this tick's **walking** low-power ped positions → `(d)`
`crossingOccupancy.Update(those)` → `(e)` `engine.Step()`.

**Read-back** (`LiveCitySnapshot Sample()`), consumed by every viewer:
- `Cars`: `{ VehicleHandle Handle, double X, Y, Z, AngleDeg, double Length, Width, string Name }` — `Z`
  interpolated from `Lane.ShapeZ` at the car's lane position (0 on flat nets), `Name` from
  `Engine.VehicleIds` (the real SUMO id).
- `Peds`: `{ int Id, double X, Y, Z, PedRegime Regime, string AnimTag }` from `demand.LiveIds` +
  `manager.PositionOf/ModelOf/AnimTagOf`.
- diagnostics: `PeakCars`, `PeakPeds`, `CarYieldObservations`, `OccupiedCrossings` (the crossing-yield proof).

`LiveCitySim` **does not** publish or render — it only steps and samples. It is the single source of truth
for the coupled scene; publishing (DDS/recording) and rendering layer on top.

> **Parity note.** `LiveCitySim` is a fresh host that *reuses the same components and ordering* as
> `SceneGen.BuildLiveCity`; `SceneGen` is left untouched (it is the committed 2D HTML tool with its own
> determinism check). A unit test asserts the coupled invariants (cars>0, peds>0, `CarYieldObservations`>0,
> byte-identical double-run) so the recipe is verified independently, not by trusting the port.

---

## 2. Record / replay (shared, transport-neutral)

### 2.1 The `.simrec` container
An append-only, typed, timestamped stream capturing **both** domains so replay reconstructs the coupled
scene:

```
header:  magic, format-version, dt, dataset-id
records (time-ordered):
  GEOMETRY   (once)            lane geos incl. Z            }
  VLIFECYCLE (per spawn/despawn) handle, spawn?, vtype, L/W, name   } vehicle side
  VFRAME     (per step)        step, time, VehicleRecord[]  }
  TL         (per change)      step, time, TlEntry[]        }
  PGEOM/PLIFE/PFRAME (ped side) crowd frame + leg + lifecycle bytes  } ped side (FrameCodec-encoded)
footer:  time index  (time -> byte offset of the frame at/after it, sparse keyframe stride)
```
Vehicle records reuse the existing `FrameCodec`/`GeometryCodec` byte layouts; ped records reuse the
`InMemoryPedReplicationBus`'s existing `FrameCodec` serialization verbatim (it already produces `byte[]`).
So the recorder is mostly framing, not new serialization.

### 2.2 `ReplicationRecorder`
An `IReplicationSink` **decorator**: it forwards every call to the real sink (in-mem or DDS) *and* appends
the framed bytes to the `.simrec`. A parallel `IPedReplicationSink` decorator captures the ped side into the
same file. The producer/viewer wraps its sink(s) in the recorder to capture a run; unwrapped, zero overhead.

### 2.3 `ReplicationFileSource` / `PedReplicationFileSource`
Implement `IReplicationSource` / `IPedReplicationSource` over a `.simrec`, driven by a **playback clock**
(see §4) rather than wall time. `Pump()` advances an internal cursor to all records with `time <= clock.Now`
and applies them (geometry once, lifecycle spawn/despawn, frames into the same bounded History the live
source uses, TLs). Because these implement the **existing** source interfaces, `Reconstructor` / `DrClock` /
`SimSource` / the Raylib render path consume them unchanged — this is tenet 2 made concrete.

**Seek** (`SeekTo(double t)`): forward seek just advances the cursor and pumps. Backward (or arbitrary
slider) seek: `ResetVehicles()`, rewind the cursor to the last keyframe ≤ `t` (geometry + all lifecycle from
`0..t` are sparse and cheap to re-apply), replay lifecycle/frames up to `t`, leaving History primed so the
next reconstruction produces the correct in-between pose. The footer time-index makes the rewind O(log n).

---

## 3. Vehicle identity and elevation on the wire (additive)

### 3.1 Name — once per entity
`LifecycleRecord` (spawn/despawn, `src/Sim.Replication/IReplication.cs`) gains a `string Name` populated on
spawn from `Engine.VehicleIds`. `ReplicationPublisher.PublishLifecycle` writes it; the DDS
`DdsVehicleLifecycle` struct gains a name field. The consumer accumulates a `Dictionary<VehicleHandle,
string>` on spawn events — the "dictionary table sent once" the request asked for. **The per-frame
`VehicleRecord` is unchanged**, so the 100k-entity hot path pays nothing. Local/live viewers can also read
the name directly from `LiveCitySim`/`SimulationSnapshot.VehicleId` without the wire.

### 3.2 Z — static lane geometry
`GeometryCodec.LaneGeo.Points` changes from `(float X, float Y)[]` to `(float X, float Y, float Z)[]`; the
packed codec format-version is bumped (older readers reject rather than mis-parse). `ReplicationPublisher`
populates Z from `NetworkModel`'s `Lane.ShapeZ`; the DDS geometry struct carries the extra floats.
`ReplicationLaneShapeSource.LaneShapeZ` and the wire overload of `RoadMeshBuilder.BuildAll` **stop
hard-coding `null`** and thread the real Z through. Geometry is published once with the network → no
per-frame cost. `CoordinateTransform.SumoToGodot` already maps SUMO-Z → Godot-Y, so seated meshes follow the
net's height with no transform change. Verified on a **synthetic elevated net** (committed nets are flat).

---

## 4. Playback clock + controls (mirrors the `Sim.Viz` HTML player)

A `PlaybackClock` (shared idea, one impl per viewer host) exposes `Now`, `Playing`, `Speed`,
`Duration`, `Play/Pause/Restart/StepFrame(±1)/SeekTo(t)`. In **live** mode it tracks wall time (as `DrClock`
does today); in **replay** mode it is the slider's authority and drives the file source's `Pump`/`SeekTo`.

Control surface to reach parity with `template.js`'s player (play/pause, drag-scrub slider that pauses while
dragging, restart, speed selector, arrow-key frame-step):
- **Raylib:** an ImGui panel (the viewer already hosts ImGui — `ViewerControlsPanels.cs`).
- **City3D:** Godot `Control` nodes (a slider + buttons overlay), scriptable for headless verification.

---

## 5. Click-to-identify

The algorithm mirrors `template.js`'s hit-test: transform the click to world space, find the nearest car
within a small radius, latch its `VehicleHandle`, draw a highlight ring + its `Name`.

- **Raylib:** reuse the existing `IRenderOverlay.OnWorldClick` plumbing (`ViewerHost` already converts
  screen→world); add the nearest-vehicle search + a selected-id highlight/label in `LiveCityOverlay`.
- **City3D:** add `_Input` ray-picking (none exists today). Maintain a **stable `instance-index →
  VehicleHandle` map** in `UpdateCars` (the loop already has `v.Handle` in hand); a camera ray → nearest
  instance → handle → name. Selection highlights the instance and shows a label `Control`.

A viewer-side `Dictionary<VehicleHandle,string> Names` (from the live host or the wire lifecycle table)
resolves handle → human-readable id for the label in every mode/transport.

---

## 6. City3D: dropping cars-XOR-peds and the dedicated live-city path

`Main.cs` today hard-forks on `_peds` in `_Ready` and again in `_Process`, and the vehicle path demands the
strict `net.net.xml`/`rou.rou.xml`/`config.sumocfg` trio. The live-city path **bypasses that contract**: a
new `--live-city` mode builds `LiveCitySource` (over `LiveCitySim`, which parses `net.xml` directly like the
reference), and renders **both** a car `MultiMesh` and a ped `MultiMesh` in one scene. The `if (_peds)
{...return;}` short-circuits are replaced by explicit mode dispatch (`cars` | `peds` | `live-city` × `local`
| `dds` | `replay`); the shared per-frame accumulator/frame counter are split per-domain so both advance in
the same tick. The legacy cars-only and plaza-peds modes remain for regression.

---

## 7. City3D remote: single combined DDS producer + dual subscriber

- **Producer:** `Sim.Host.App` gains a `--live-city` mode that runs `LiveCitySim` and publishes **both** the
  vehicle topic set **and** the ped topic set (both already exist in `Sim.Replication.Dds`) from **one
  process over one net**. The crossing-yield gate is server-side inside `LiveCitySim`, so the wire just
  carries the resulting car + ped poses — no coupling logic crosses the wire.
- **Subscriber:** City3D `--transport=dds --live-city` attaches **both** `DdsSubscriber` (vehicles, now with
  Z + names) and `DdsPedReplicationSource` (peds) to the one `DdsParticipant`, and renders both — removing
  the DDS-branch mutual exclusion. Elevation and identity arrive via §3.

---

## 8. Determinism & parity argument (the gate)

- **No behavioural `src/` change.** `SumoSharp.LiveCity` is a new additive project, not in `Traffic.sln`'s
  parity path. `CrowdSource` is null for every committed golden → engine byte-identical.
- **Wire additions are outside the golden path.** Goldens are engine `golden.fcd.xml`/`golden.state.xml`;
  the replication codec (Z, name) and the recording format are render/transport-side. The `Sim.Bench`
  determinism hash and `dotnet test Traffic.sln` counts must be **unchanged** on every task that edits
  `src/` — baseline captured fresh per task (other sessions may be editing the engine).
- **Recording round-trips deterministically:** replaying a recording reproduces the recorded poses; a live
  run repeated with the same config is byte-identical (SplitMix64 car PRNG + per-entity ped seeds, no
  `System.Random`).
- **Known engine blockers are out of scope** (owner-confirmed, fixed on a separate branch): dense
  multi-lane car overlaps (cooperative lane-change not yet ported) and crossing tunneling. The demo tunes
  `CarTargetConcurrent` down for presentability; this design neither depends on nor fixes them.

---

## 9. Staging

A. `SumoSharp.LiveCity` host (the shared recipe).
B. Raylib 2D live-city, real-time (cheapest end-to-end proof of the coupled scene in a real viewer).
C. Shared record/replay + playback controls + click-select, wired into Raylib.
D. City3D local: both modes (live + replay), click-pick, Z-seated meshes.
E. City3D remote: combined DDS producer + dual subscriber + Z-on-wire + name-on-wire.

Each stage is independently demoable and independently gated. Details + success conditions:
`docs/LIVE-CITY-VIEWERS-TASKS.md`.
