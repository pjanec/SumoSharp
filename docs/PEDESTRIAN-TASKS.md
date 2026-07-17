# PEDESTRIAN-TASKS.md — production task breakdown

**Status: planning, for review.** The POC phase (0–7) validated every mechanism of the pedestrian design
(see `PEDESTRIAN-OVERVIEW.md`, `PEDESTRIAN-DESIGN.md`, and the `PEDESTRIAN-POC*-FINDINGS.md` files). This
document converts the validated design into an **executable production work queue**. `PEDESTRIAN-TRACKER.md`
is the at-a-glance checklist over these task IDs.

Each task names its **design reference** (a section, not a copy), the **files** it touches, its
**dependencies**, and — mandatory — **success conditions** (specific, measurable: tests / assertions /
benchmark numbers). A task closes only when its success conditions pass, verified first-hand.

**Invariants that hold for every task below** (from the POC phase; do not regress):
- The SUMO **parity lane core stays untouched** — determinism hash `909605E965BFFE59` unchanged; full
  `Sim.ParityTests` green. Pedestrians are a separate engine (Principle 6).
- Crowd changes are **behaviorally validated**, and any parallel/perf change is **bit-identical to serial**
  (the POC-7a gate style) or gated behind an explicit fast-mode flag.
- **Coordinate on `Engine.cs`/routing** with the parallel lane-engine session — Stage P4 is the only stage
  that touches the lane Engine, and it must be scheduled with that session.

The current POC code (`src/Sim.Pedestrians/{Lod,Navigation,Crossing,Obstacles,Parking,Density}`,
`src/Sim.Pedestrians.Nav.DotRecast`) is proof-of-concept quality: correct and tested, but built for
demonstration. Production hardening is folded into the stages below, not treated as a separate pass.

---

## Stage P0 — Crowd-store `Add`/`Remove` (PRIORITY)

**Why first:** POC-7c measured churn (moving interest sources continuously promoting/demoting) at **3.6×**
the stable step cost at 100k, entirely because `OrcaCrowd`/`MixedTrafficCrowd` have no agent removal and
`PedLodManager` rebuilds the whole high-power crowd on every membership change (O(high) per switch). A
real `Add`/`Remove` (O(1) per switch) removes most of that gap and is the single highest-value follow-up.
Design ref: `PEDESTRIAN-DESIGN.md` §3(d), `PEDESTRIAN-POC7C-FINDINGS.md` Q2.

### P0-1 — `OrcaCrowd` stable-handle `Add`/`Remove`
- **Design ref:** §3(d). **Files:** `src/Sim.Core/Orca/OrcaCrowd.cs` (+ a handle type). **Deps:** none.
- Add a real removal path: agents addressed by a **stable handle** (index + generation), a **free-list** of
  vacated slots recycled on `Add`, and a `Remove(handle)` that vacates a slot without disturbing others'
  handles. Keep the SoA/plan-execute/spatial-hash design; removed slots are skipped (like `_active`) and
  reused. Preserve deterministic iteration order (iterate live slots in a fixed order; document it).
- **Success conditions:**
  1. A dedicated test adds N, removes an arbitrary subset, adds more, and asserts surviving agents' handles
     still resolve to the correct positions and stepping is unaffected.
  2. **Determinism gate:** a fixed add/remove script produces bit-identical trajectories run-to-run, and
     (with `UseParallelStep`) parallel == serial bit-identical (extend `OrcaParallelStepTests`).
  3. Full `Sim.ParityTests` + `Sim.Pedestrians.Tests` green (existing OrcaCrowd behavior unchanged when
     `Remove` is never called — byte-identical to today for the no-remove path).

### P0-2 — `MixedTrafficCrowd` `Add`/`Remove` + dynamic external-disc input
- **Design ref:** §3(d), §6; `PEDESTRIAN-POC6*`. **Files:** `src/Sim.Core/Mixed/MixedTrafficCrowd.cs`.
  **Deps:** P0-1 (share the handle/free-list pattern).
- Mirror P0-1's `Add`/`Remove` on `MixedTrafficCrowd`, and add a **`SetExternalObstacles(WorldDisc[])`**
  equivalent (POC-6b found it has only permanent `AddWall`/`AddBlock`) so a maneuvering car can avoid
  *moving* pedestrians without a per-step crowd rebuild.
- **Success conditions:**
  1. Remove/re-add a maneuvering agent mid-run; survivors unaffected; deterministic.
  2. `SetExternalObstacles` makes a car avoid a moving disc with velocity awareness; a test shows the car
     yields to a *moving* ped better than the POC-6b per-step-`AddBlock` approximation (measurable earlier
     avoidance), no overlap.
  3. Parity + ped suites green.

### P0-3 — `PedLodManager` / `LotCoupling` use `Add`/`Remove` (retire the rebuild)
- **Design ref:** §5. **Files:** `src/Sim.Pedestrians/Lod/PedLodManager.cs`,
  `src/Sim.Pedestrians/Parking/LotCoupling.cs`. **Deps:** P0-1, P0-2.
- Replace `RebuildHighCrowd` (and `LotCoupling`'s per-step car-crowd rebuild) with incremental
  `Add`/`Remove` on promotion/demotion. Keep the demotion-reroute-from-current-position behavior.
- **Success conditions:**
  1. All existing POC-3 / POC-6 tests still pass unchanged.
  2. **Churn benchmark (`Sim.BenchPedLod`) Scenario B is now near-flat vs Scenario A** — the per-switch cost
     is O(1), not O(high). Target: at 100k, B's parallel ms/step within ~1.3× of A's *for equal high-power
     counts* (isolate the "more high-power agents" effect from the rebuild effect — the finding to erase is
     the rebuild overhead). Record before/after in `PEDESTRIAN-POC7C-FINDINGS.md`.

---

## Stage P1 — Consolidate `Sim.Pedestrians` into a production API + interest-source system

### P1-1 — Interest-source field (movable, multi-source, spatial)
- **Design ref:** §5 (movable interest sources; use cases 1–4). **Files:** `src/Sim.Pedestrians/Lod/`.
  **Deps:** P0-3.
- Promote the POC-3 single-source promotion into a production **interest-source field**: an updatable set of
  movable sources (entity/avatar-attached, static AoI, intrinsic crosswalk/parking/incident), queried per
  low-power ped via the crowd spatial hash (cheap because sources are few). Deterministic, hysteretic.
- **Success conditions:** a test with several independently-moving sources promotes exactly the peds inside
  any promote-radius, demotes correctly with hysteresis (no flap), and the per-step interest scan is
  sub-linear in ped count (measured: adding sources or peds does not blow up the stable-scenario ms/step).

### P1-2 — API surface + packaging
- **Design ref:** §10. **Files:** `src/Sim.Pedestrians/*`, new `README.md`, `.csproj` packaging. **Deps:** P1-1.
- Consolidate the POC namespaces (`Lod`/`Navigation`/`Crossing`/`Obstacles`/`Parking`) into a coherent,
  documented public API; make `Sim.Pedestrians` a packable NuGet library (like `SumoSharp.Evac`), with the
  DotRecast provider staying in its own optional package.
- **Success conditions:** the package builds/packs; a short sample app drives a pedestrian scenario through
  the public API only (no internals); README documents the seams; hermetic `dotnet test` unaffected.

---

## Stage P2 — Navigation productionization (behind the seam)

### P2-1 — Harden the SUMO-geometry bake
- **Design ref:** §4; POC-1a notes. **Files:** `src/Sim.Pedestrians/Navigation/Bake/`. **Deps:** none.
- Replace the per-segment sidewalk quad approximation with **whole-polyline mitred/rounded strips**, and add
  **vertex-proximity adjacency** for bent sidewalks (POC-1a disabled it to fix a corner bug; do it correctly)
  so multi-segment sidewalks connect. Add a **static-obstacle spatial index** to `OrcaCrowd` (obstacle
  queries are O(n) — §3(b)) for many buildings/parked cars.
- **Success conditions:** a network with bent multi-segment sidewalks routes correctly (path stays walkable,
  no false disconnection); a scene with hundreds of static boxes/buildings steps at a rate that scales
  sub-linearly in obstacle count (benchmark before/after the index).

### P2-2 — Dynamic blockers + reroute in the navigation API
- **Design ref:** §4, §6; POC-5. **Files:** `src/Sim.Pedestrians/Navigation/`. **Deps:** P2-1.
- Generalize POC-5's blocked-polygon reroute into the production `IPedNavigation` (dynamic obstacle
  registration → affected agents reroute), with hysteresis so a transient blocker doesn't thrash routes.
- **Success conditions:** registering/unregistering a blocker reroutes only affected peds; no route thrash
  when a blocker flickers; deterministic.

### P2-3 — Production navmesh integration contract + OD demand
- **Design ref:** §4, §10. **Files:** `src/Sim.Pedestrians/Navigation/`. **Deps:** P1-2.
- Document and example the `IWalkableSpace`/`IPedNavigation`/`ILocalSteering` contract the **owner's
  production navmesh** implements; provide an adapter template. Add pedestrian **origin→destination demand**
  (spawn/route/despawn at scale) so a scenario populates itself.
- **Success conditions:** a second real `IWalkableSpace` implementation (or a documented adapter stub for
  the owner's navmesh) drives the same pedestrian layer unchanged; an OD-demand scenario sustains a target
  population with continuous spawn/arrival.

---

## Stage P3 — Networking productionization (DDS multicast, end-to-end)

### P3-1 — Crowd + PathArc DDS topics
- **Design ref:** §7 (multicast, one stream); POC-7b. **Files:** `src/Sim.Replication.Dds/`,
  `src/Sim.Replication/`. **Deps:** none (records landed in POC-7b).
- Wire the quantized `PedFreeKinematicRecord` onto a **multicast crowd topic** and the `PathArcRecord` onto a
  **durable/transient-local** topic (path sent once), plus **regime lifecycle events** (promote/demote,
  board/alight, park) on the keyed lifecycle topic. Extend `DdsReplicationSink`/`DdsSubscriber`.
- **Success conditions:** a **loopback multicast round-trip test** (CycloneDDS, as `Replication.Dds` already
  supports) publishes a crowd frame + a PathArc record + a promote lifecycle event and a subscriber
  reconstructs them; `git`-hermetic gate unaffected (DDS stays out of `Traffic.sln`, per its convention).

### P3-2 — Publisher + global bandwidth governor
- **Design ref:** §7. **Files:** `src/Sim.Replication/PublishPolicy.cs` (+ a ped publisher). **Deps:** P3-1, P0-3.
- Productionize the POC-3 `PedPublisher`: DR-error-gated `FreeKinematic` for high-power, `PathArc`-once +
  heartbeat for low-power, DR-model switch on promotion, all on the single stream. Add a **global bandwidth
  governor** `IPublishPolicy` that throttles high-power send-rate under spike load (there is no per-channel
  culling — multicast).
- **Success conditions:** at the target mix, the measured single-stream rate stays under 500 Mbit/s even in a
  mass-promotion spike (governor engages); low-power peds emit zero per-step samples (POC-3 invariant holds
  at scale).

### P3-3 — IG-side reconstruction
- **Design ref:** §7; POC-3 `HeadlessIg`. **Files:** `src/Sim.Viewer.Motion/` (or a ped viewer consumer).
  **Deps:** P3-1.
- Add the `FreeKinematic` extrapolator and the `PathArc` follower to the viewer DR pipeline
  (`DrClock`/`PoseResolver`), applying regime lifecycle + DR-switch events at their event time.
- **Success conditions:** an end-to-end test/demo streams a mixed crowd and the IG reconstructs low-power
  peds from the one-time path (server==IG within render tolerance, the POC-3 invariant, over DDS) and
  high-power peds from the position stream, switching correctly on promotion.

---

## Stage P4 — Engine coordination seams (Core; schedule with the lane-engine session)

### P4-1 — Engine TLS-crossing signal projection
- **Design ref:** §6; POC-2 finding. **Files:** `src/Sim.Core/Engine.cs` (a new read-only projection).
  **Deps:** coordinate with the parallel Engine session.
- POC-2 found `Engine.TlStates` excludes pedestrian crossings (internal→internal links). Add a read-only
  projection exposing each crossing's controlling `tlLogic` + link state so the crossing gate reads the
  **live** Engine signal instead of re-deriving phase timing from the net XML.
- **Success conditions:** the crossing gate, fed the live projection, opens/closes exactly with the Engine's
  crossing signal; parity hash unchanged (Step-only projection, off the golden path); coordinated merge with
  the Engine session.

---

## Stage P5 — Evac generalization

### P5-1 — `Sim.Evac` consumes `Sim.Pedestrians`
- **Design ref:** §6 (evac = specialization). **Files:** `src/Sim.Evac/*`. **Deps:** P1-2, P2-3.
- Refactor evac so panic = a **forced high-power promotion** + the flee param override, and destination =
  nearest safe zone via `IPedNavigation` — replacing `FleeGoalFor` radial steering and `ExitsFarthestFirst`.
  `FearField`/`LineOfSight`/`BlockedDetector` are reused unchanged.
- **Success conditions:** existing evac demos/tests (`EvacOrganicDemoTests`, etc.) still pass (or are updated
  with justified new goldens); evac now routes peds to safe zones along real walkable space, not radially.

---

## Stage P6 — Scale hardening + on-target validation

### P6-1 — On-target benchmark run
- **Design ref:** §9; POC-7 findings (all measured on a 4-core VM). **Files:** benchmarks. **Deps:** P0-3.
- Run `Sim.BenchCrowd` / `Sim.BenchPedLod` / `Sim.BenchPedNet` on the **16+‑core Windows target** and record
  real steps/s + Mbit/s; update the findings docs with on-target numbers replacing the 4-core estimates.
- **Success conditions:** measured 100k-ped stable steps/s is interactive on the target box; the churn case
  (post-P0) is within the interactive band; bandwidth confirmed under budget on real DDS multicast.

### P6-2 — Region decomposition (only if P6-1 shows the flat parallel `Step` plateaus)
- **Design ref:** §9; `DOMAIN-DECOMP.md`. **Files:** `src/Sim.Core/Orca/`. **Deps:** P6-1.
- If flat `Parallel.For` plateaus below target on the 16-core box, region-decompose the crowd (the
  `ComputeLaneRegions` pattern) with free cross-region handoff.
- **Success conditions:** measurable speedup over flat parallel at 100k on the target box, still bit-identical.

### P6-3 — Full property-test hardening
- **Design ref:** §8. **Files:** `tests/Sim.Pedestrians.Tests/`. **Deps:** all above.
- Consolidate the per-POC property tests into a maintained production suite: no-overlap, arrives-within-N,
  never-leaves-walkable, no-flap, server==IG, determinism — run across the promoted-to-production scenarios.
- **Success conditions:** the suite covers every requirement (1–7) with a named test; all green; parity
  untouched.

---

## Sequencing summary

**P0 first (Add/Remove — the priority).** Then P1 (API/interest-source) and P2 (navigation) can proceed in
parallel; P3 (networking) depends on P0-3; P4 (Engine TLS seam) is scheduled with the lane-engine session
whenever convenient; P5 (evac) waits on P1–P2; P6 (on-target scale) waits on P0 and closes the loop with the
real hardware numbers. The design is fixed; any P-stage finding that contradicts it updates
`PEDESTRIAN-DESIGN.md` before that stage closes.
