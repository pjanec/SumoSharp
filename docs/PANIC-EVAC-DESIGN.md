# PANIC-EVAC-DESIGN.md — Phase-1 spine: HOW it works

This is the **design** (the HOW) for the Phase-1 "spine" of the panic evacuation. The **WHAT** — goal,
requirements R1–R10, the two-systems architecture, the phased roadmap — lives in `PANIC-EVAC.md` and is
not restated here; every requirement reference below (e.g. **R4**) points there. The task breakdown and
success conditions live in `PANIC-EVAC-TASKS.md`; the checklist in `PANIC-EVAC-TRACKER.md`.

Scope of this document = **Phase 1 only** (`PANIC-EVAC.md` §6.1): organized grid traffic → central
incident → radius fear → per-vehicle flee → gridlock → driver→pedestrian conversion → contained
foot-exodus to a safe radius, all watchable once. Later phases (contagion/LoS, vehicle Orca-mode,
sublane, scale) are explicit non-goals — see §8.

---

## 1. Design principles

- **Two systems, one seam (`PANIC-EVAC.md` §2).** The **driving core** (SUMO port) simulates *vehicles
  only* and is never modified behaviourally. The **external evac layer** (`Sim.Evac`) owns the panic
  decision, all pedestrians, and the driver→pedestrian handoff. They communicate only through the core's
  existing public seams plus one small additive setter.
- **Parity by strict layering (R9).** The evac layer *drives* the core through public methods; the core
  never references the evac layer. Therefore no evac code can move the determinism hash
  (`909605E965BFFE59`). The single core addition (§3) is additive and inert-when-unused.
- **Determinism.** No `System.Random`, no wall-clock, fixed iteration order over insertion-ordered
  collections. Two identical runs must produce bit-identical end-states.
- **Reuse the frozen seams (R10).** `SetDestination`/`Reroute`, `Despawn`, `AddObstacle`, `GetDrModel`,
  `CrowdSource`/`WorldDisc`/`ICrowdFootprintSource`, `OrcaCrowd`. The only gap is a runtime per-vehicle
  knob setter (§3).

---

## 2. Component map & tick data-flow

```
  DRIVING CORE (Sim.Core.Engine)                 EXTERNAL EVAC LAYER (Sim.Evac)
  ┌────────────────────────────┐                 ┌──────────────────────────────────────┐
  │ vehicles (lanes/junctions) │                 │ Incident        (fear source)         │
  │ SetVehicleParams  ◄────────┼─── flee preset ─│ EvacConfig      (tunables + preset)    │
  │ SetDestination    ◄────────┼─── flee route ──│ BlockedDetector (DrModel + dwell)      │
  │ Despawn           ◄────────┼─── convert ─────│ FakeNavMesh     (hard-edge wall)       │
  │ AddObstacle       ◄────────┼─── abandoned ───│ EvacDirector    (orchestration)        │
  │ GetDrModel        ─────────┼──► blocked      │   owns an OrcaCrowd (pedestrians)      │
  │ CrowdSource=peds  ◄────────┼── peds as obst. │                                        │
  │ VehicleHandles/PosX/...    ─┼─► read state    │                                        │
  └────────────────────────────┘                 └──────────────────────────────────────┘
```

**One coordinated tick** (`EvacDirector.Tick()`), in order:

1. **PRE** — `FeedVehicleDiscsToPeds()`: build a `WorldDisc` for every live car + every abandoned car and
   push them to the pedestrian crowd (`OrcaCrowd.SetExternalObstacles`) so pedestrians avoid cars (R8).
   Then, if the incident is active, compute fear per tracked car; a newly-panicked car gets the flee
   preset (`SetVehicleParams`) and an away-reroute (`SetDestination`).
2. **STEP** — `engine.Step()`: vehicles drive one config step, avoiding pedestrians through
   `Engine.CrowdSource` (the crowd), and publish the read snapshot.
3. **POST** — update `BlockedDetector` per car from `GetDrModel`; a *panicked & blocked* car is converted
   (§4.6). Then drive all pedestrians one step (§4.7).

Cars avoid peds from this step's frozen state; peds avoid cars from the previous step's positions — a
one-step reciprocal lag, standard for cross-regime coupling.

---

## 3. The one core change: `Engine.SetVehicleParams`

**Signature.** `public bool SetVehicleParams(VehicleHandle handle, VehicleParamOverride ov)` — returns
false on a stale/inactive handle. `VehicleParamOverride` is a `readonly record struct` of **all-optional
(nullable)** knobs: `SpeedFactor`, `MaxSpeed`, `Tau`, `MinGap`, `Accel`, `Decel`, `EmergencyDecel`,
`JmIgnoreFoeProb`, `JmIgnoreFoeSpeed`, `JmIgnoreJunctionFoeProb`.

**Semantics.** Each non-null field is merged onto the running vehicle's resolved vType via a record
`with`-copy (`VehicleRuntime.VType` becomes a settable property); `SpeedFactor` writes the mutable
`VehicleRuntime.SpeedFactor` field. Unset fields keep their current value, so knobs are **independently
settable** (R2). `EmergencyDecel` is auto-lifted to stay ≥ `Decel` (SUMO invariant); `ApparentDecel`
tracks `Decel` only when `Decel` is overridden.

**Why this is the only core change and why it is parity-safe.** vType knobs are otherwise applied only at
spawn; nothing lets an already-running vehicle change them. Making `VType` settable and adding this method
is purely additive: no golden/parity/`Sim.Bench` path calls it, so the determinism hash is byte-identical
(proven: 365→368 tests pass, hash unchanged). "Flee mode" is **not** a core state — it is this setter
called with an aggressive preset that lives in `EvacConfig` (§4.5).

---

## 4. `Sim.Evac` components

New project `src/Sim.Evac` (net8.0, references `Sim.Core`, **not packaged**, parity-exempt).

### 4.1 `Incident` + fear (R3)
`readonly record struct Incident(double X, double Y, double StartTime, double Radius)`.
- `IsActive(t) = t >= StartTime`.
- `FearAt(x,y,t)`: `0` before start or at/beyond `Radius`; else `1 − dist/Radius` (linear, 1 at
  epicentre). Phase 1 is **radius-only** — no contagion/LoS/jam-unease (those are Phase 2, §8).

### 4.2 `BlockedDetector` (R4)
Per-handle dwell timer over `Engine.GetDrModel`. Each step: if `DrModel.Stationary` (the engine's
`RegimeOf`: speed ≤ 0.01 m/s, which a jam-stopped lane vehicle satisfies) add `dt`, else reset to 0.
`blocked ⇔ dwell ≥ BlockedDwellSeconds`. No core change — pure external read. Conversion only ever acts
on *panicked* vehicles, so an ordinary red-light stop is harmless.

### 4.3 `FakeNavMesh` (R7/R8 — minimal Phase-1 form)
Derived entirely from the loaded net geometry (`NetworkParser.Parse` → lane + junction `Shape`s): the
axis-aligned **bounding box** of all vertices, expanded outward by `VicinityWidth`. Its outer edge is one
**closed ORCA obstacle loop** (`OrcaCrowd.AddObstacle`) — the hard edge pedestrians cannot cross. Winding
is **clockwise** so RVO2 keeps agents on the interior side (validated by the containment test, §
PANIC-EVAC-TASKS T5.3; flip if it ever fails). Helpers: `Contains(p)`, `ClampInterior(p, inset)`.
*Deferred (Phase 3):* per-lane/junction buffered polygons so pedestrians flee **along** streets; drops in
behind the same "region + boundary loop" interface.

### 4.4 `EvacDirector` — orchestration
Owns the `OrcaCrowd` (pedestrians), the `FakeNavMesh`, the `BlockedDetector`, per-vehicle state
(`Panicked`/`Converted`/`Alive`) keyed by `VehicleHandle` in insertion order, per-pedestrian state
(`Escaped`), and the abandoned-car disc list. `Track(handle)` registers a spawned car. `Tick()` runs the
PRE/STEP/POST sequence of §2. Pedestrian crowd sub-stepped `CrowdSubSteps` times per engine step (the
engine runs at the coarse parity dt; ORCA wants a finer dt — same idea as `Engine.CrowdReactionSubSteps`).

### 4.5 Flee preset + reroute (R2)
The preset is a `VehicleParamOverride` in `EvacConfig.FleePreset` — first-cut aggressive, **deterministic**
values (`SpeedFactor 1.6`, `Tau 0.5`, `MinGap 0.5`, `Accel 4.0`, `Decel 6.0`; the `jmIgnoreFoe*` levers are
left null because they consult a per-vehicle RNG — the gridlock is meant to emerge from density +
aggression converging on few exits, not from stochastic gap-running). Reroute: pick the exit edge whose far
end is farthest from the incident and is routable (`SetDestination`); try farthest-first, keep the current
destination if none routes (e.g. mid-junction).

### 4.6 Driver→pedestrian conversion (R6)
On *panicked & blocked*: (a) append a static abandoned-car `WorldDisc` at the car's pose (pedestrians keep
avoiding it); (b) `Engine.Despawn(handle)` (removes it from the driving sim, `BlockedDetector.Forget`); (c)
spawn `PedestriansPerCar` pedestrians into the crowd at the car pose (a tiny deterministic golden-angle
offset avoids exact stacking — no RNG), each with a radial away-goal.

### 4.7 Pedestrian steering + escape (R5/R6b)
Each step, every pedestrian's goal is refreshed radially away from the incident
(`normalize(pos − incident) · FleeGoalDistance`, clamped inside the mesh so it lands on the hard edge). A
pedestrian becomes `Escaped` once `dist(pos, incident) ≥ SafeRadius`; an escaped pedestrian holds position
(goal = current point). The crowd then advances `CrowdSubSteps` × `subDt`.

### 4.8 Observability
`EvacDirector` exposes read-only counts and per-entity queries for tests + viz: `PanickedCount`,
`ConvertedCount`, `EscapedCount`, `PedestrianCount`, `PedestrianPosition(i)`, `PedestrianEscaped(i)`,
`AbandonedCarCount`, `AbandonedCar(i)`, `IsPanicked/IsConverted/IsAlive(handle)`, `NavMesh`, `Incident`,
`BoundaryLoop`.

---

## 5. Determinism & parity argument

- **Parity:** the only core touch is §3, additive and never on the golden path → hash unmoved; this is a
  *test* (T5.5), not a claim.
- **Evac determinism:** no RNG/wall-clock anywhere; `EvacDirector` iterates `_order` (insertion order) and
  the crowd is index-ordered; the reroute exit sort is total (distance then ordinal id); the spawn offset
  is a pure function of `(handle.Index, k)`. Two runs ⇒ identical → a *test* (T5.2).

---

## 6. Demo scenario + viz

- **Network:** `scenarios/evac-grid/net.net.xml` — a 4×4 priority-junction grid (nodes at {60,140,220,300},
  centre ≈ (180,180)) with outward boundary stubs as flee exits, authored once by `netgenerate`.
  **Parity-exempt: no golden, no rou.xml.** Committed net only; runtime-spawned demand.
- **Scenario builder** `EvacGridScenario` (shared by test + viz): loads the net, defines a `sigma=0`
  passenger vType, spawns 2 cars per boundary entry routed straight across the grid, tracks them, and wires
  the incident `(180,180, start 8s, radius 140)` + default config. Single source of truth so the viz shows
  exactly what the tests prove.
- **Viz overlays** (`Sim.Viz`): the incident danger zone (translucent ring, appears at `StartTime`) + a
  dashed safe-radius ring + epicentre marker; the known-world hard edge (dashed rectangle); disc kinds for
  fleeing pedestrian / escaped pedestrian / abandoned car, with per-scene legend labels. Cars stay the
  standard oriented boxes. Added to the bundle as the opening scene.

---

## 7. Tunables (defaults; calibrated against the viz, `PANIC-EVAC.md` §7)

| Tunable | Default | Meaning |
|---|---|---|
| `ThetaPanic` | 0.05 | fear ≥ this ⇒ panic |
| `VicinityWidth` | 8.0 m | band beyond road → hard edge |
| `BlockedDwellSeconds` | 3.0 s | Stationary dwell ⇒ blocked |
| `SafeRadius` | 120 m | pedestrian escaped beyond this |
| `PedRadius` / `PedMaxSpeed` | 0.25 m / 3.0 m/s | pedestrian footprint / jog |
| `FleeGoalDistance` | 200 m | radial goal distance (clamps to edge) |
| `VehicleDiscRadius` | 2.0 m | car footprint pedestrians avoid |
| `PedestriansPerCar` | 1 | occupants per abandoned car |
| `CrowdSubSteps` | 10 | ORCA sub-steps per engine step |
| `FleePreset` | see §4.5 | aggressive knob bundle |

---

## 8. Non-goals (deferred to later phases)

Contagion / line-of-sight / jam-unease fear (Phase 2); vehicle Orca-mode / off-road push (Phase 3);
per-lane buffered navmesh (Phase 3); sublane filter-to-front (Phase 4); hundreds→thousands + spatial
indexing (Phase 5). Panic-on-car *coloring* in the viz is out of scope (cars stay one colour; the danger
ring conveys the zone).

---

## 9. Risks / open questions

- **ORCA wall winding** (interior vs exterior) is empirical — the containment test (T5.3) is the guard.
- **Conversion yield** is modest on a small grid (a few cars box in; most flow out) — acceptable for the
  spine; density/exit count are tunable if a richer jam is wanted.
- **Coarse dt** (1 s) makes pedestrian ORCA coarse; mitigated by `CrowdSubSteps`. If pedestrian quality is
  insufficient, raise sub-steps or lower the engine step for the demo.
