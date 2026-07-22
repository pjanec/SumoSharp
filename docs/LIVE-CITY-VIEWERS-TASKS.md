# Live-city in the interactive viewers — tasks & success conditions

Design: `docs/LIVE-CITY-VIEWERS-DESIGN.md` (referenced by section, not restated). Tracker:
`docs/LIVE-CITY-VIEWERS-TRACKER.md`. Reference recipe: `src/Sim.Viz/SceneGen.cs` `BuildLiveCity`.

**Standing gate (every `src/`-touching task):** capture the baseline **fresh** at task start —
`dotnet test Traffic.sln -c Release` pass/skip counts + the `Sim.Bench` determinism hash (single +
parallel) — and confirm it is **unchanged** when the task is ticked. Record the numbers in the tracker.
Committed goldens never set `CrowdSource`; the wire/recording/demo changes are outside the golden path.

**Convention:** a task is closed only when Opus verifies its success conditions first-hand (read the diff,
read the test to confirm it asserts the real condition, re-run the gate/smoke) — not on the implementor's
report.

---

## Stage A — `SumoSharp.LiveCity` shared coupled-sim host  (design §1, §8)

### A1 — new project scaffold
- **Files:** `src/Sim.LiveCity/Sim.LiveCity.csproj` (`PackageId SumoSharp.LiveCity`, TFMs
  `net8.0;netstandard2.1`, `IsPackable=true`, refs `Sim.Core`, `Sim.Ingest`, `Sim.Pedestrians`); add to
  `Traffic.sln` only if it does not perturb the parity run (else keep out, like `CityLib`).
- **Success:** `dotnet build src/Sim.LiveCity` succeeds on both TFMs; `dotnet pack` yields
  `SumoSharp.LiveCity.0.1.0.nupkg`; standing gate unchanged.

### A2 — `LiveCitySim` + `LiveCityConfig` + `LiveCitySnapshot`
- **Files:** `src/Sim.LiveCity/{LiveCitySim,LiveCityConfig,LiveCitySnapshot}.cs`. Port the `BuildLiveCity`
  wiring (design §1): net parsed twice, navmesh bake, `CrosswalkSignals`, `CrossingOccupancySource`,
  `PedDemand`/`PedLodManager`, `InterestField` pocket, `Engine` (step-length 0.5, lanechange.duration 2.0,
  speeddev 0), `CrowdSource = Composite(HighPowerFootprints, crossingOccupancy)`. `Step(dt=0.5)` performs
  the exact per-tick order (a)spawn→(b)`demand.Step`→(c)gather walking low-power peds→(d)
  `crossingOccupancy.Update`→(e)`engine.Step`. `Sample()` returns cars (incl. `Z` from `Lane.ShapeZ`,
  `Name` from `Engine.VehicleIds`) + peds (id, x, y, z, regime, animTag) + diagnostics. Env knobs
  `LIVECITY_CARS/LCMIN/YIELD` respected via config defaults.
- **Success (test `src/Sim.LiveCity.Tests` or `tests/Sim.Pedestrians.Tests`):** over
  `scenarios/_ped/demo_city/box`, after ~120 steps: `PeakCars>0`, `PeakPeds>0`, `CarYieldObservations>0`
  (coupled-yield proof); running twice with the same config produces **identical** sampled trajectories
  (determinism); with `YieldEnabled=false`, `CrowdSource` is `HighPowerFootprints` only and
  `CarYieldObservations` drops materially (A/B). Standing gate unchanged.

---

## Stage B — Raylib 2D live-city, real-time  (design §1, §6-adjacent, §8)

### B1 — `DemoKind.LiveCity` + `LiveCityOverlay` (cars + peds in one frame)
- **Files:** `src/Sim.Viewer/DemoCatalog.cs` (+entry), `src/Sim.Viewer/DemoSession.cs` (+case),
  `src/Sim.Viewer/LiveCityOverlay.cs` (new, mirrors `EvacOverlay`'s shape). The overlay owns a `LiveCitySim`,
  steps it on the frame accumulator, feeds cars through the existing `Renderer` vehicle path
  (`RenderHelpers`/`KinematicReconstructor`), and draws peds regime-colored in `DrawWorldOver` (borrow
  `PedOverlay`'s draw). `Sim.Viewer.csproj` already refs `Sim.Pedestrians`; add a ref to `Sim.LiveCity`.
- **Success:** `dotnet run --project src/Sim.Viewer -- --mode local --demo "Live city"` opens a window
  (or `--demo-smoke`/headless smoke exits 0) showing **both** dense cars and a weaving ped crowd; a headless
  assertion logs `cars>0 && peds>0` in the same frame and non-zero crossing-yield. No golden sets
  `CrowdSource`; standing gate unchanged.

### B2 — click-select vehicle + identity label
- **Files:** `src/Sim.Viewer/LiveCityOverlay.cs` (`HandlesWorldClick=>true`, `OnWorldClick` nearest-vehicle
  search + selected-handle highlight ring + `Name` label), reusing `ViewerHost` screen→world.
- **Success:** a headless unit test of the hit-test picks the nearest car to a click point (and none when
  the click is far); interactive: clicking a car draws a ring + its SUMO id. Standing gate unchanged.

---

## Stage C — Shared record/replay + playback controls (Raylib)  (design §2, §4)

### C1 — `.simrec` format + `ReplicationRecorder` (cars + peds)
- **Files:** `src/Sim.Replication/Recording/{SimRecFormat,ReplicationRecorder}.cs` (+ ped-sink decorator).
  Reuse `FrameCodec`/`GeometryCodec` byte layouts; append typed timestamped records + a footer time-index.
- **Success:** recording a `LiveCitySim` run yields a non-empty `.simrec`; a round-trip unit test reads back
  the same geometry/lifecycle/frame/TL and ped records that were written (byte-exact). Standing gate
  unchanged (additive).

### C2 — `ReplicationFileSource` + `PedReplicationFileSource` (seekable)
- **Files:** `src/Sim.Replication/Recording/{ReplicationFileSource,PedReplicationFileSource}.cs` implementing
  the existing `IReplicationSource`/`IPedReplicationSource`, driven by a playback clock; `SeekTo(t)` per
  design §2.3.
- **Success (test):** replaying a recording reconstructs positions matching the live run at the same sim
  times within DR tolerance; `SeekTo(t)` for arbitrary `t` (incl. backward) yields the same state as playing
  linearly to `t`; consumed unchanged by `Reconstructor`. Standing gate unchanged.

### C3 — Raylib playback panel + live/replay mode switch
- **Files:** `src/Sim.Viewer/LiveCityOverlay.cs` + a controls panel (ImGui, `ViewerControlsPanels.cs`
  pattern); a `--record <file>` flag on the live demo and a `--replay <file>` entry/flag.
- **Success:** `--record` writes a `.simrec` during a live run; `--replay <file>` plays it back through the
  same overlay with a working **play/pause/restart/speed/frame-step + drag slider** (slider seeks to any sim
  time; dragging pauses then restores). Verified interactively + a headless check that a seek repositions the
  clock and source. Standing gate unchanged.

---

## Stage D — City3D local: live + replay, click-pick, Z-seated  (design §3.2, §4, §5, §6)

### D1 — drop cars-XOR-peds; `--live-city` local combined scene
- **Files:** `demos/City3D/CityLib/LiveCitySource.cs` (wraps `LiveCitySim`, exposes car+ped read-back +
  names + Z), `demos/City3D/Viewer/Main.cs` (replace the `if(_peds){...return;}` forks in `_Ready`/`_Process`
  with mode dispatch; split the per-domain accumulator/frame; render car **and** ped `MultiMesh`), pack
  `SumoSharp.LiveCity` into `demos/City3D/local-nuget` (build.sh + CityLib.csproj PackageReference).
- **Success:** `GODOT --path demos/City3D/Viewer -- --live-city` (headless Xvfb here) renders **both** cars
  and peds over `demo_city/box`; a screenshot shows cars on roads + a ped crowd + peds at kerbs; legacy
  `--scenario` (cars-only) and `--peds` (plaza) still work. `CityLib.Tests` green.

### D2 — honor Z on the local road/car meshes
- **Files:** ensure the live-city local path feeds `Lane.ShapeZ` (the Z-aware `NetworkLaneSource`) into
  `RoadMeshBuilder`/`CarTransform`/`Reconstructor` height; add a **synthetic elevated net** fixture for
  verification (committed nets are flat).
- **Success:** on the synthetic elevated net, road ribbon vertices and car Y follow the net's Z (assert in
  `CityLib.Tests` that a lane with non-zero `ShapeZ` produces non-zero mesh/instance Y, and flat → 0); a
  screenshot shows a ramp/height. Standing gate unchanged (demo-side).

### D3 — Godot playback controls + replay-file mode
- **Files:** `demos/City3D/CityLib/PlaybackClock.cs`, `demos/City3D/Viewer/Main.cs` (a Godot `Control`
  overlay: slider + play/pause/restart/speed; `--live-city --replay <file>` consuming
  `ReplicationFileSource`+`PedReplicationFileSource`).
- **Success:** `--live-city --replay <rec>` plays a recording; slider seeks; play/pause/restart/speed work
  (a scripted headless check drives the clock and asserts the rendered sim-time follows). Standing gate
  unchanged.

### D4 — click ray-pick vehicle → highlight + id
- **Files:** `demos/City3D/Viewer/Main.cs` (`_Input` camera ray-pick; stable `instance→VehicleHandle` map in
  `UpdateCars`; highlight the picked instance + a label `Control` showing `Name`).
- **Success:** a scripted pick (ray from a known screen point) returns the expected `VehicleHandle`/id; the
  picked car is visibly highlighted with its SUMO id in a screenshot. Standing gate unchanged.

---

## Stage E — City3D remote: combined DDS producer + dual subscriber + Z/name on wire  (design §3, §7, §8)

### E1 — Z on the replication wire
- **Files:** `src/Sim.Replication/GeometryCodec.cs` (LaneGeo `(x,y)`→`(x,y,z)`, format-version bump),
  `src/Sim.Host/ReplicationPublisher.cs` (populate Z from `Lane.ShapeZ`),
  `src/Sim.Replication.Dds/DdsTopics.cs` (Z in the geometry struct),
  `demos/City3D/CityLib/{ReplicationLaneShapeSource,RoadMeshBuilder}.cs` (stop hard-coding `null`).
- **Success:** a two-process inmem/DDS round-trip over the **synthetic elevated net** delivers non-zero Z to
  the subscriber; remote road/car Y matches the local path on that net (parity of the two `ILaneShapeSource`
  impls). Codec round-trip unit test for the new field. **Standing gate unchanged** (wire is outside the
  golden path — confirm counts + hash first-hand).

### E2 — vehicle name on the wire (once per spawn)
- **Files:** `src/Sim.Replication/IReplication.cs` (`Name` on `LifecycleRecord`),
  `src/Sim.Host/ReplicationPublisher.cs` (write `Name` from `Engine.VehicleIds` on spawn),
  `src/Sim.Replication.Dds/DdsTopics.cs` (`DdsVehicleLifecycle` name field); consumer builds a
  `Dictionary<VehicleHandle,string>` name table.
- **Success:** the per-frame `VehicleRecord`/`DdsWireFrame` is **unchanged** (assert no field added to the
  hot record); a subscriber reconstructs the correct SUMO id for a spawned vehicle from the lifecycle table;
  remote click-pick (D4 path) shows the real id, not `Vehicle#i.g`. Standing gate unchanged.

### E3 — combined cars+peds DDS producer (one process, one net)
- **Files:** `src/Sim.Host.App/Program.cs` (`--live-city` mode: run `LiveCitySim`, publish vehicle topics
  via `ReplicationPublisher`→`DdsReplicationSink` **and** ped topics via the ped publisher→`DdsPedReplicationSink`
  from the same net). Refs `Sim.LiveCity`, `Sim.Pedestrians`.
- **Success:** `dotnet run --project src/Sim.Host.App -- --live-city --transport dds` publishes both topic
  sets from one process (verify with an inmem self-consume that both vehicle frames and ped crowd frames are
  received). Standing gate unchanged (additive; `Sim.Host.App` not in `Traffic.sln`).

### E4 — dual subscriber in City3D remote
- **Files:** `demos/City3D/Viewer/Main.cs` (`--transport=dds --live-city`: attach **both** `DdsSubscriber`
  and `DdsPedReplicationSource` to the one participant; render both; drop the DDS-branch mutual exclusion),
  a `run-live-city-remote.sh` orchestrating producer + subscriber.
- **Success:** two-process DDS round-trip renders **cars + peds together** remotely, with Z-seated roads and
  clickable real vehicle ids (headless two-process check that the subscriber logs both vehicle geometry and
  ped crowd frames and reconstructs both). Standing gate unchanged.

---

## Out of scope (owner-confirmed, separate branch)
Dense multi-lane car overlaps (cooperative lane-change port, `docs/LANE-CHANGE-OVERLAP-SPEC.md`) and crossing
tunneling. Mitigated for demos by a lower `CarTargetConcurrent`; not fixed here.
