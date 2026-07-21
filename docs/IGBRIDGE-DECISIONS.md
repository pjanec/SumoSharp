# IgBridge ↔ IG binding — DECISIONS & design addendum

Records the owner's answers to the design's §12 open questions and the survey findings that refine
scope. This is the **HOW addendum**: it amends `IGBRIDGE-DESIGN.md` where the answers
narrow or correct it. Read the requirements (`IGBRIDGE-REQUIREMENTS.md`) and design first;
this doc is the delta, not a restatement.

> **Naming.** The binding — the external **.NET 6 host** that embeds SumoSharp, ticks the core at
> 10 Hz, and feeds the proprietary IG — is called **IgBridge** everywhere: docs (`IGBRIDGE-*` family)
> and code (`Sim.IgBridge` producer lib, `Sim.IgBridge.Host` verification console).

---

## 1. Locked decisions (owner answers to design §12 + the two confirmations)

| # | Question | Decision |
|---|---|---|
| **Q1** | Orientation on the wire | **`x, y, headingDeg` (navi-degrees).** No quaternion, no z, no pitch/roll from us. Real IgBridge converts to the IG-native quaternion later; the **IG does conformal ground-clamping itself**. FakeIg interpolates heading with **shortest-arc** angle interp (never a raw lerp). |
| **Q2** | Emit cadence & delay | The IG is a **standalone async network process** fed timestamped samples; it makes **no delay assumptions**. Its playout **delay is the IG's own config: ~500 ms–1 s** (longer harms human-in-the-loop perceived latency). Real IgBridge emits at its own (variable) frame rate. **IgBridge's job: emit samples whose timestamp correctly correlates with the position increment**, so the IG's DR never jumps. PoC emits at a **fixed, deterministic cadence** (default 20 Hz) for a reproducible trace; delay is a FakeIg replay knob swept over 0.5–1.0 s. |
| **Q3** | Despawn / teleport | The IG has a **lifecycle API**: `entity-created(id, model, initialPos)`, `entity-removed(id)`, `entity-updated(sample)`. The IG smooths **small** teleports via its DR and does an **immediate jump above a spatial threshold**. So IgBridge emits three record kinds, not a bare sample stream; genuine discontinuities need no special smoothing (let the IG threshold handle them, or re-key via remove+create). |
| **Q4** | Input stream | **(a) direct 10 Hz engine state.** DDS-DR wire (b) deferred (may later drive the City3D viewer off the full DR protocol). |
| **Q5** | Terrain | **The IG owns its terrain.** Production will use a road net matching the IG's terrain. **No City3D, no z, no 2D→3D.** IgBridge emits **planar `x, y, headingDeg` only.** (This also matches the survey finding that City3D has *no* procedural terrain field — ground-z there comes only from lane `ShapeZ`.) |
| **Q6** | Targeting | Producer lib **`Sim.IgBridge`** multi-targets `netstandard2.1;net6.0;net8.0` (proves the .NET 6 embed + Unity/IL2CPP reuse). Verification console **`Sim.IgBridge.Host`** is `net8.0`. **`Sim.Pedestrians` is retargeted `netstandard2.1;net8.0`** (same as the vehicle stack) so peds are reusable from .NET 6 / Unity too. |
| **C1** | Lane-change ease location | Lives in **`Sim.Viewer.Motion`**, not IgBridge — no binding-private smoothing math. Fixing it fixes the 2D/3D viewers too. Outside the parity path → goldens & `dotnet test` stay byte-identical. |
| **C2** | Metrics gate | Objective smoothness metrics (yaw-rate, yaw-jerk, lateral accel, C1 gap, lane-change duration) raw-vs-reconstructed, committed as a regression check. Concrete targets in `IGBRIDGE-TASKS.md`. |

### Scope changes these answers force (vs the original design)
- **R5 (2D→3D terrain) is dropped.** IgBridge emits planar pose + heading; the IG clamps to its own
  ground. Removes the terrain-height/pitch/roll work entirely.
- **Lifecycle is an explicit 3-verb protocol** (created/removed/updated), replacing the design §8
  "the 4-tuple has no teleport bit" framing. The IG's own jump-threshold handles genuine
  discontinuities; IgBridge just emits create/remove correctly and does **not** smooth a supra-threshold
  jump as motion.
- **Orientation is scalar heading**, so the "quaternion vs yaw-only" branch collapses to yaw-only.

### Revision (post-Q5): z IS on the wire, for multi-level disambiguation
The owner later clarified the IG **needs z** for **multi-level roads and tunnels**: where a bridge and a
tunnel share `(x, y)`, only z places the entity on the correct deck. This does **not** revive
terrain-following (still the IG's job) — z is for *disambiguation*, not ground contact. So the wire is
`[id, x, y, z, headingDeg, t]`. Sourcing (no engine-core change on the direct-10 Hz path):
- **Vehicles:** z = `PoseResolver.Resolve`'s `Pose.Z`, sampled from `NetworkLaneSource.LaneShapeZ` — real
  elevation on a 3-D net, `0.0` on a flat net (the box). Flows automatically; already available.
- **Peds:** `z = 0` for now — the crowd is 2-D (holonomic). Multi-level ped z needs a future surface-z
  mapping (see open items).

**Open items (do NOT touch engine core — other sessions are editing lane-change / teleport there):**
1. Pedestrian surface z on multi-level structures (needs a ped-position→deck-z map; possibly engine or
   ped-nav support).
2. Whether an elevated production net's lane z is fully carried by `NetworkLaneSource` (verify on a real
   3-D net), and whether the DDS-DR wire path (option b) needs z added (`ReplicationLaneShapeSource`
   currently returns null z).
3. Coordinate any engine API need for z with the in-flight engine-core sessions rather than editing it here.

---

## 2. Refined architecture

```
 [1] Engine @ fixed 10 Hz (StepLength=0.1)         Sim.IgBridge  (netstandard2.1;net6.0;net8.0)
     read SoA spans each step                       ├─ per-entity VehicleSampleHistory (ring)
       ▼                                            ├─ reconstruct: DrClock.ResolveAt + PoseResolver
 [2] per-entity ring buffers (sim-time)             │              + DrPoseSmoother   (REUSED, unchanged*)
       ▼                                            ├─ peds: PedestrianWorld → PedPublisher → HeadlessIg
 [3] resample @ fixed emit cadence (20 Hz)          │         + PedRemoteReconstructor (REUSED ped stack)
       ▼                                            └─ emit IgSample records → trace + in-memory ring
 [4] IG-native records: created / updated / removed
       ▼
 Sim.IgBridge.Host (net8.0, verification only)
     ├─ FakeIg: replay trace, 2-most-recent interp @ (clock − igDelay), threshold-jump (Q3)
     ├─ Sim.Viz side-by-side render (raw scene vs FakeIg-reconstructed scene)
     └─ metrics pass (raw vs reconstructed) + committed regression check
```
\* Two **non-behaviour-changing** additions to `Sim.Viewer.Motion` (see §5), both outside parity.

**Reuse split (both are shared-with-the-viewer, not binding-private):**
- **Vehicles** → `Sim.Viewer.Motion` (`DrClock`/`DrPoseSmoother`) + `Sim.Core.PoseResolver`. This is
  the "fix once, fix both" seam with the City3D viewer.
- **Pedestrians** → `Sim.Pedestrians/Lod` (`HeadlessIg` + `PedRemoteReconstructor`). Peds deliberately
  do **not** run through `DrClock` (they're holonomic, lane-free); their shared stack is the ped Lod
  reconstructor, which the native ped viewer uses. Peds are smooth by construction; only corner
  headings + LOD promote/demote route through smoothing.

---

## 3. The wire/trace record schema (PoC)

Three record kinds, one JSONL line each, ordered by `t` then by kind (created < updated < removed at
equal `t`). All positions planar; heading navi-degrees.

```jsonc
{"k":"new", "id":"veh0", "t":12.30, "model":"car",  "x":100.4, "y":22.1, "z":0.0, "h":271.3}  // entity-created
{"k":"upd", "id":"veh0", "t":12.35, "x":101.1, "y":22.0, "z":0.0, "h":271.0}                   // entity-updated
{"k":"del", "id":"veh0", "t":40.10}                                                    // entity-removed
```
- `t` is **sim time** (seconds). It is the sole timing authority; a sample's `(x,y)` is the pose **at
  `t`**. Temporal correctness (Q2) means: consecutive `upd` for an entity have `Δposition ≈ speed·Δt`,
  so the IG's linear DR between them is near-exact.
- `model` on `new` selects the IG 3D model (Q3). PoC values: `car`, `ped` (extend as needed).
- Vehicle ids are the engine `VehicleId` string; ped ids are `ped:<intId>` — globally unique, stable
  for the entity's life, never reused within a run.
- The **in-memory ring** carries the identical records (struct form) for a live consumer; the trace is
  the same stream flushed to disk, replayable/diffable.

---

## 4. Timing model (two distinct offsets — keep them separate)

1. **Reconstruction lookahead** (`Sim.IgBridge`, ~1 core tick ≈ 100 ms): the emit query time is held
   ~one 10 Hz tick behind the newest core sample so `DrClock.ResolveAt` always has a bracketing sample
   *ahead* (interpolate branch: arc-window turns, straddle lane-change, ChordHeading). The emitted
   sample's `t` = the query time. This is IgBridge-internal and fixed.
2. **IG playout delay** (`FakeIg` / real IG, 500 ms–1 s, Q2): applied by the **consumer**, not at emit.
   FakeIg plays at `tPlay = clock − igDelay` and interpolates the two most recent emitted samples that
   bracket `tPlay`. With 20 Hz emit (50 ms spacing) and ≥500 ms delay, `tPlay` is always bracketed →
   the IG always interpolates, never extrapolates a turn.

**End-to-end latency budget** = lookahead (~0.1 s) + igDelay (0.5–1.0 s). Explicit and tunable
(requirement §7). No auto-driven delay (DR doc §10.1 "auto-delay pulses speed" — we never do it).

---

## 5. Additions to `Sim.Viewer.Motion` (the only new code outside IgBridge; both outside parity)

### 5.1 Deterministic resolve seam — `DrClock.ResolveAt(history, sampleT, lanes)`
`DrClock.Resolve` computes `sampleT = _renderSim − delay`, where `_renderSim` is advanced by
`Pump(...)` from a **real `Stopwatch` wall clock** — correct for a live 60 Hz viewer, but non-deterministic
for an offline trace generator. Extract `Resolve`'s body into `ResolveAt(history, double sampleT,
lanes)` and make the existing `Resolve` call `ResolveAt(history, _renderSim − delay, lanes)`. This is a
**pure refactor** (byte-identical for the viewer, which keeps calling `Resolve`/`Pump`) that lets
IgBridge drive an **explicit sim-time query** — no `Stopwatch`, fully deterministic. IgBridge never
calls `Pump`, so the `Stopwatch` stays inert.

### 5.2 Lane-change ease over ~1.2–1.5 s (C1 — the one likely gap)
Today an instant lane change reconstructs as a lateral move over the **straddle bracket interval** only
(one packet gap), which at 10 Hz can be a single 100 ms slide. Spread it over a fixed **ease window**
`W ≈ 1.3 s` as an S-curve in the lateral axis. Placement and preference order:
- **Preferred (free) path:** if the sublane port lands (`LANE-CHANGE-OVERLAP-SPEC.md`),
  `TrajectoryPoint.PosLat` becomes real lateral offset and the ease is already in the data — no
  detector needed.
- **PoC path (add now):** a small stateful ease in `Sim.Viewer.Motion` keyed by entity handle. Detect a
  one-tick displacement that is **mostly perpendicular to heading** (the lane-snap signature — a full
  lane width ≈ 3.2 m sideways in one tick, not a turn). On detect, latch the lateral delta and release
  it as a **smoothstep over `W`**, so the emitted lateral offset ramps 0→Δ over ~1.3 s. Straight cruise
  and genuine turns (motion follows the lane) produce ~0 perpendicular delta → ease is inert.
- Lives in `Sim.Viewer.Motion` (new small `LaneChangeEaser` or folded into a resolve-side helper), so
  the 2D native/web viewers inherit it. **Not** in IgBridge. `W` is a tunable (§7).

### 5.3 Kinematic reconstruction — rear-axle drag (the "vehicle on rails" fix)
**Problem (owner-reported).** The reconstruction emits SUMO's **front reference point** as the position and
derives heading from the back→front chord *sampled on the lane polyline*. The renderer draws the body
backward from the front, so the **front is pinned to the polyline ("on rails") and the rear swings/jumps**
around the front pivot at every faceted internal-lane vertex / route-segment boundary — as if the rear
wheels steered. The same front-pivot rotation makes lane changes look like the rear slews sideways.

**Fix — a kinematic single-track (bicycle) model with the rear axle as a *towed* point.** The front
reference (reliable, follows the lane) tows a rear axle that **cannot slip sideways** — exactly a car's
unsteered rear wheels. Per vehicle, integrated at the emit cadence Δ:
```
Lwb        = wheelbaseFactor · Length            // ~0.6·Length; axles inset like an ordinary car
Fa         = Pfront − frontOverhang · dir(θ_prev) // front axle, inset from the front ref by ~0.15·Length
Ra(t)      = Fa − Lwb · unit( Fa − Ra(t−Δ) )      // rear axle DRAGGED, stays Lwb behind, no lateral slip
θ          = navi( Fa − Ra )                        // body heading = rear→front (forward)
Center     = (Fa + Ra) / 2                          // ≈ vehicle geometric center
```
Properties (why it's right): the rear follows a **smooth path and cuts inside the corner** (real
off-tracking) while the front rides the geometry — **no rear swing, no jump**; heading changes
*continuously* across polyline facets because the drag integrates them away; and on a lane change the
towed rear lags → the body **yaws into the change and back** (natural S-curve). 10 Hz core + 20 Hz emit is
ample — the drag integrates at 50 ms steps, far finer than the turn dynamics.

**Guards** (from the DR doc's hard-won lessons): below a small speed **hold heading** (no spin at a red
light); a supra-threshold front jump (teleport/handle reuse) **reseeds** `Ra` from the lane heading rather
than dragging across the gap; deterministic seeding at spawn (`Ra = Fa − Lwb·dir(laneHeading)`), no
`System.Random`, per-entity state → order-independent.

**Where it lives / reference point.** A new stateful per-handle component in **`Sim.Viewer.Motion`**
(`KinematicHeading`), so the 2D web/native and 3D City3D viewers inherit the identical model — fix once,
fix all. It **replaces** the chord-heading + motion-tilt heuristic (§10.3 of the DR doc) for this path,
kept selectable so the viewers can A/B. IgBridge emits the **vehicle Center + θ** (the owner's IG models
pivot at center; `z` = lane-surface/ground z, wheels-on-ground is the IG's job). The 2D `Sim.Viz` render
converts Center→front-ref only if the template anchors at the front (a fixed `±Length/2·dir(θ)` offset).

**Composition with §5.2 (ease).** Complementary: the **drag model fixes body orientation** (the rear
jump) for turns *and* lane changes; the **ease spreads the front's instant lateral jump** over ~1.3 s so
the front *path* is smooth. Together a lane change is a front sliding over ~1.3 s with the rear dragging
kinematically behind — a real-looking maneuver.

**Tunables:** `wheelbaseFactor` (0.6), `frontOverhangFactor` (0.15), `holdSpeed` (~0.5 m/s), `reseedJump`
(~7 m). All render-side; none affect parity.

Both additions are renderer-side, outside `Sim.Core`'s parity path; the offline `dotnet test` gate and
every golden stay byte-identical (§6).

---

## 6. Determinism & parity

- **Deterministic trace.** Core ticks a fixed `StepLength=0.1` (no wall dt); peds tick
  `PedestrianWorld.Step(now, dt)` with fixed `now`/`dt` from the sim clock; emit is a fixed cadence
  driven by sim time, not wall jitter; reconstruction uses `ResolveAt(sampleT)` (no `Stopwatch`);
  `DrPoseSmoother.Smooth` gets a fixed `frameDt` = emit interval. No `System.Random`. → two runs of the
  same (scenario, seed, 10 Hz schedule, tunables) produce **byte-identical** traces (acceptance §9.5).
- **Parity untouched.** IgBridge and both `Sim.Viewer.Motion` additions are **consumers** of engine
  output; nothing touches `Sim.Core`'s simulation/parity code. Committed goldens and `dotnet test` stay
  byte-identical. The `Sim.Pedestrians` retarget (Q6) is a TFM-only change — verified by re-running
  `dotnet test` green, not by trusting the edit.

---

## 7. Tunables (all IgBridge/FakeIg/Motion; none affect parity)

| Parameter | Where | Default | Effect |
|---|---|---|---|
| Core step | scenario config `StepLength` | `0.1` s (10 Hz) | source sample rate |
| Emit cadence | IgBridge | `20` Hz | trace density; IG interp error between samples |
| Reconstruction lookahead | IgBridge | ~`0.1` s (1 tick) | keeps ResolveAt in the interpolate branch |
| IG playout delay | FakeIg (IG-side in prod) | swept `0.5–1.0` s | interpolate vs extrapolate; latency vs smoothness |
| IG jump threshold | FakeIg (Q3) | TBD (~lane width) | above → immediate jump, no DR smear |
| Lane-change ease window `W` | `Sim.Viewer.Motion` | `1.3` s (range 1.2–1.5) | lane change reads as a smooth ease, not a slide |
| DR realism | PoseResolver arg | `ChordHeading` | heading model (avoids swing-wide artifact) |
| Heading low-pass τ | `DrPoseSmoother` | `0.18` s (existing) | eases heading; >100° snaps |

---

## 8. What the PoC proves without a real IG (acceptance mapping)

- **§9.1** IgBridge runs `scenarios/_ped/demo_city/box` (vehicles + peds) at 10 Hz, writes an IG-sample
  trace for all entities.
- **§9.2** FakeIg replay (2-most-recent @ `clock − delay`, 0.5–1.0 s) shows **no lane-change teleports,
  no junction yaw snaps** in the `Sim.Viz` side-by-side.
- **§9.3** Reconstructed max yaw-rate / yaw-jerk / lateral-accel bounded and far below the raw stream;
  lane changes reconstruct as a ~1.3 s ease (targets fixed in the tasks doc).
- **§9.4** Reuse proven: the smoothing is `Sim.Viewer.Motion`/`PoseResolver` (+ ped Lod) — a fix there
  changes both the IG trace and the City3D viewer.
- **§9.5** Deterministic: two runs → identical traces; latency budget (§4) documented and tunable.
