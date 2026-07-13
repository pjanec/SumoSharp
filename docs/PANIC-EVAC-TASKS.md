# PANIC-EVAC-TASKS.md — Phase-1 spine: stages, tasks, success conditions

Task breakdown for the Phase-1 spine. **Design references** (e.g. *DESIGN §4.2*) point at
`PANIC-EVAC-DESIGN.md`; requirements (e.g. *R4*) at `PANIC-EVAC.md`. Nothing here restates those — read
them for the HOW/WHAT. The checklist is `PANIC-EVAC-TRACKER.md`.

Every task lists **success conditions**: the concrete, checkable outcomes (named tests / assertions /
measurements) that must pass before the task is considered done. A task is closed only when all of its
success conditions hold. Stages are ordered by dependency.

---

## Stage S1 — Core seam (the one additive change)

### T1.1 — `Engine.SetVehicleParams` + `VehicleParamOverride`
- **Design:** DESIGN §3. **Requirement:** R2, R9.
- **Files:** `src/Sim.Core/VehicleParamOverride.cs` (new), `src/Sim.Core/VehicleRuntime.cs` (`VType` →
  settable), `src/Sim.Core/Engine.cs` (new method).
- **Success conditions:**
  1. `VehicleParamOverride` has all-optional knobs per DESIGN §3; `SetVehicleParams` merges non-null
     fields onto a running vehicle and returns false on a stale/inactive handle.
  2. `EmergencyDecel ≥ Decel` always holds after an override.
  3. **Parity:** full offline suite green and the `Sim.Bench` determinism hash is **exactly**
     `909605E965BFFE59` for both `hashA` and `hashPar` (this is T5.5's gate; T1.1 must not regress it).
  4. A focused unit test: spawn a vehicle, call `SetVehicleParams` with a subset of knobs, assert the
     changed knobs took effect and the untouched ones are unchanged.

---

## Stage S2 — Evac primitives (pure, unit-testable in isolation)

### T2.1 — `Incident` + radius fear
- **Design:** DESIGN §4.1. **Requirement:** R3.
- **Files:** `src/Sim.Evac/Incident.cs`.
- **Success conditions:** unit tests for `FearAt`: 0 before `StartTime`; 1 at epicentre; ~0.5 at half
  radius; 0 at/beyond radius. `IsActive` toggles at `StartTime`.

### T2.2 — `BlockedDetector`
- **Design:** DESIGN §4.2. **Requirement:** R4.
- **Files:** `src/Sim.Evac/BlockedDetector.cs`.
- **Success conditions:** unit test with a stub/driven engine: a handle held `Stationary` for
  ≥ `BlockedDwellSeconds` reports blocked; a moving step resets the dwell to 0; `Forget` clears it.

### T2.3 — `FakeNavMesh`
- **Design:** DESIGN §4.3. **Requirement:** R7, R8.
- **Files:** `src/Sim.Evac/FakeNavMesh.cs`.
- **Success conditions:** given a parsed net, the bounding box encloses every lane/junction vertex + the
  vicinity margin; `Contains`/`ClampInterior` behave at the edges; `BoundaryLoop` is a 4-vertex closed
  loop. (Winding correctness is proven end-to-end by T5.3.)

---

## Stage S3 — Orchestration

### T3.1 — `EvacDirector` tick skeleton + observability
- **Design:** DESIGN §2, §4.4, §4.8.
- **Files:** `src/Sim.Evac/EvacDirector.cs`, `src/Sim.Evac/EvacConfig.cs`.
- **Success conditions:** `Track` registers handles; `Tick()` runs PRE→`engine.Step()`→POST in the
  documented order; the crowd is set as `Engine.CrowdSource`; the observability surface (§4.8) is present.
  Depends on S1, S2.

### T3.2 — Panic decision + flee preset + reroute
- **Design:** DESIGN §4.5. **Requirement:** R2, R3.
- **Success conditions:** a car with fear ≥ `ThetaPanic` is marked panicked exactly once, receives the
  flee preset via `SetVehicleParams`, and is rerouted to the farthest routable exit; cars below threshold
  are untouched.

### T3.3 — Driver→pedestrian conversion
- **Design:** DESIGN §4.6. **Requirement:** R6.
- **Success conditions:** a panicked+blocked car is `Despawn`ed, leaves exactly one abandoned-car disc,
  and spawns `PedestriansPerCar` pedestrians at its pose; a non-panicked blocked car is **not** converted.

### T3.4 — Pedestrian steering + escape + car-avoidance feed
- **Design:** DESIGN §4.7, §2 (disc feed). **Requirement:** R5, R6b, R8.
- **Success conditions:** pedestrians move away from the incident and become `Escaped` past `SafeRadius`;
  live + abandoned car discs are fed to the crowd each step; escaped pedestrians hold position.

---

## Stage S4 — Demo scenario

### T4.1 — Grid network (authored once, committed)
- **Design:** DESIGN §6. **Requirement:** R9 (parity-exempt).
- **Files:** `scenarios/evac-grid/net.net.xml`, `scenarios/evac-grid/README.md`.
- **Success conditions:** `netgenerate` produces the 4×4 grid with boundary stubs; only `net.net.xml`
  committed (no golden, no rou.xml); README states parity-exempt + how it was generated; the offline test
  loop needs **no SUMO** to consume it.

### T4.2 — `EvacGridScenario` shared builder
- **Design:** DESIGN §6.
- **Files:** `src/Sim.Evac/EvacGridScenario.cs`.
- **Success conditions:** `Build(netPath)` loads the net, defines a `sigma=0` vType, spawns the 2-per-entry
  routed traffic, wires the incident + config, and returns `(engine, director, handles)`; used by both the
  tests (S5) and the viz (S6) — single source of truth.

---

## Stage S5 — End-to-end validation (headless `PANIC-EVAC.md` §8 done-condition)

All in `tests/Sim.ParityTests/EvacSpineTests.cs` (add `Sim.Evac` project reference).

### T5.1 — Cascade plays once
- **Success conditions:** over a bounded run on the grid: `PanickedCount > 0`, `ConvertedCount > 0`,
  `PedestrianCount > 0`, `AbandonedCarCount == ConvertedCount`, and max pedestrian distance-from-incident
  ≥ 0.8·`SafeRadius` (real outward progress).

### T5.2 — Evac run is deterministic
- **Design:** DESIGN §5. **Success conditions:** two independent runs fold to a bit-identical signature
  (counts + every pedestrian pose + escaped flag).

### T5.3 — Containment invariant (ORCA winding)
- **Design:** DESIGN §4.3. **Success conditions:** at **every** step, **every** pedestrian satisfies
  `NavMesh.Contains(pos)` — no pedestrian ever leaves the known world.

### T5.4 — No-incident inertness
- **Requirement:** R9. **Success conditions:** with an incident that never fires,
  `PanickedCount == ConvertedCount == PedestrianCount == 0` after a full run.

### T5.5 — Suite + hash gate
- **Success conditions:** `dotnet test` fully green (only the pre-existing skips); `Sim.Bench`
  `hashA == hashPar == 909605E965BFFE59`.

---

## Stage S6 — Visualization (the watchable done-condition)

### T6.1 — Payload overlays
- **Design:** DESIGN §6.
- **Files:** `src/Sim.Viz/Payload.cs`.
- **Success conditions:** `ScenePayload` gains optional `Incident` (`[x,y,radius,startTime,safeRadius]`)
  and `Boundary` (flat closed loop); both default null; existing scenes serialize unchanged.

### T6.2 — `SceneGen.BuildEvacGrid`
- **Design:** DESIGN §6. **Files:** `src/Sim.Viz/SceneGen.cs`, `src/Sim.Viz/Sim.Viz.csproj` (ref
  `Sim.Evac`).
- **Success conditions:** drives `EvacGridScenario` and records per-frame car boxes + pedestrian/abandoned
  discs (typed kinds) + incident + boundary + legend labels; produces a valid `ScenePayload`.

### T6.3 — `template.js` overlay drawing
- **Design:** DESIGN §6. **Files:** `src/Sim.Viz/template.js`.
- **Success conditions:** draws the dashed hard-edge boundary; draws the incident danger ring + dashed
  safe-radius ring + epicentre marker, gated to appear at `StartTime`; extends the disc palette/labels for
  the evac kinds; **all existing scenes render unchanged** (additive, null-guarded).

### T6.4 — Bundle wire + artifact
- **Files:** `src/Sim.Viz/Program.cs`.
- **Success conditions:** the evac scene is the opening scene of `--bundle`; the bundle builds and the
  full transition (organized → incident → flee → gridlock → abandoned cars → foot-exodus → escape, inside
  the hard edge) plays coherently in the rendered replay.
