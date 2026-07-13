# PANIC-EVAC.md — organized traffic → panic evacuation

## 0. Goal

Organized urban traffic that, on a localized **security incident** (bomb, shooting, armed
strike), switches to a **panic evacuation**: cars flee, streets block solid, and once a car is
boxed in its occupants **abandon it and flee on foot**, off the lane onto the sidewalk/grass,
until they hit the edge of the known world (a ditch/fence/field) and pile up. Organized traffic is
a realistic-enough backdrop; the evacuation is the product. **Not** an "Indian traffic" model.

## 1. Consolidated requirements (authoritative)

- **R1 — organized backdrop.** Ordinary lane driving. **Sublane is an optional, separate** layer
  (value: scooters/cyclists filtering to the front at red lights, still stopping on red). Start
  **without** sublane.
- **R2 — external decides, the core drives (vehicles only).** The panic **decision** is external,
  applied **per vehicle** by setting that vehicle's **parameters** (`aggressiveness`, `mode`, flee
  route). The vehicle modes the core drives:
    - **Organized** — normal lane/sublane driving.
    - **Flee** — aggressive organized driving to a flee route (raised impatience / assertiveness /
      speed-factor, smaller gaps, relaxed right-of-way). Still on roads, still the SUMO model.
    - **Orca** *(later phase)* — free-space movement in the road+vicinity band when organized
      driving has nowhere to go (cars mounting the shoulder). The core owns it.
- **R3 — panic spread is a separate external layer.** Fear/contagion sits on top of the shared
  data; **local-information only** (direct LoS-gated proximity + contagion from panicking
  neighbours + mild jam-unease). No global broadcast → distant traffic stays organized, unaware,
  just jammed.
- **R4 — the port emits a `blocked` signal.** Per vehicle, when it can no longer make progress.
- **R5 — pedestrians are ALWAYS external.** The port does **not** simulate pedestrian movement; it
  only sees pedestrians as **external obstacles** (`ExternalObstacle`/`WorldDisc`) its vehicles
  avoid. The **external system drives them** (fake-navmesh + ORCA).
- **R6 — driver→pedestrian conversion is external, at the `blocked` boundary.** On `blocked` **and**
  the vehicle already in flee/panic (tracked by the external layer), the external layer: (a) creates
  a pedestrian entity (external obstacle) and starts driving it; (b) commands the port to **stop the
  vehicle for good → static obstacle**. The entity *migrates* from port-simulated vehicle to
  externally-simulated pedestrian at this instant.
- **R7 — known world = road + close vicinity.** Lanes + an immediately-adjacent band
  (sidewalk/grass). Its **outer edge is a hard boundary** (ditch/fence/soft soil) where actors
  **block** — matching reality (cars pile at the road edge). Beyond = future real navmesh, out of
  scope; actors mostly block before reaching it.
- **R8 — fake-navmesh from SUMO data only (external).** Navigable region = lane+junction geometry
  buffered outward by a vicinity width; outer edge = wall; cars (incl. abandoned) = interior
  obstacles. Replaceable later by a real 3D-world navmesh behind the same interface — out of scope.
- **R9 — parity.** The driving core stays parity-exact; with no panic params set and no external
  obstacles, it is byte-identical to today (hash `909605E965BFFE59` unchanged). The panic layer is
  parity-exempt.
- **R10 — reuse the frozen seams.** Everything rides the issue #4 interfaces: `WorldDisc` /
  `ExternalObstacle` (pedestrians-as-obstacles), `ObstacleStore` (frozen cars), `DrModel`/stuck
  (basis for `blocked`), plus per-vehicle param + stop-vehicle inputs. Reinforces the freeze.

## 2. Two systems, one seam

```
  DRIVING CORE (SUMO port) — VEHICLES ONLY            EXTERNAL EVAC SYSTEM
  ┌───────────────────────────────────┐              ┌──────────────────────────────────┐
  │ per-vehicle mode:                  │  vehicle     │ panic decision (fear + contagion, │
  │   Organized → Flee → (Orca later)  │  state,      │   local-information only)          │
  │ lane car-following (+opt sublane)  │  DrModel,    │──► sets per-vehicle PARAMS ───────►│ (into core)
  │ avoids EXTERNAL OBSTACLES (peds)   │  `blocked`   │                                    │
  │ stop-vehicle → static obstacle     │ ───────────► │ on `blocked` + panic:              │
  └───────────────────────────────────┘              │   • spawn PEDESTRIAN (ext obstacle)│
        ▲ params / stop-cmd / ext-obstacles           │   • stop the vehicle (cmd core)    │
        │                                             │ drives ALL pedestrians:            │
        └───────── pedestrians as ext obstacles ◄──── │   ORCA + fake-navmesh (road+vicin.,│
                   (fed back each step)               │   cars as obstacles, hard edge)    │
                                                      └──────────────────────────────────┘
   KNOWN WORLD = road+vicinity; hard outer edge (ditch/fence) → actors BLOCK.
   BEYOND = real 3D navmesh, OUT OF SCOPE.
```

- **Driving core** — simulates vehicles only. Modes are param-selected (R2); parity-exact with no
  panic. Consumes: per-vehicle params, a stop-vehicle→obstacle command, and external obstacles
  (pedestrians). Emits: vehicle state, `DrModel`/stuck, and the per-vehicle `blocked` signal (R4).
- **External evac system** — owns the panic decision (R3), all pedestrians (R5, driven by
  fake-navmesh + ORCA), and the driver→pedestrian handoff at `blocked` (R6). Feeds pedestrian
  positions back to the core as external obstacles each step. Replaceable by a real-navmesh system
  later — the seam is the contract.

## 3. The fake-navmesh (external; road-network-derived; replaceable)

- **Navigable region** = union of lane+junction polygons, **buffered outward** by a close-vicinity
  width — derived entirely from the SUMO `NetworkModel` geometry, no external world data.
- **Hard outer edge** = boundary of that region → a wall pedestrians cannot cross (ditch/fence).
- **Interior obstacles** = the cars (moving, stuck, abandoned) the core exposes.
- **Movement** = ORCA within the region, bounded by the edge, avoiding cars + each other.
- **Replaceable:** a real navmesh (building footprints, walkable/blocked polygons, elevation) drops
  in behind the same "navigable region + obstacles" interface. Out of scope now.

## 4. Per-entity lifecycle

```
 VEHICLE (driving core)
   Organized ─(external sets panic params)─► Flee ─(core, aggressive organized flee)─►
      │  [core emits `blocked`]   ── (later phase: Orca free-move in vicinity, then `blocked`) ──
      ▼
   external, on `blocked`+panic:  stop vehicle → STATIC OBSTACLE (core)  +  spawn PEDESTRIAN
                                                                                   │
 PEDESTRIAN (external evac system: ORCA + fake-navmesh; a moving external obstacle to the core)
      flee within road+vicinity toward the away-edge, avoiding cars + walls + peds; block at edge
      │  reaches away-edge
      ▼
   Escaped (leaves the simulated area)
```

## 5. Believability / correctness bar

- **Hard invariants:** no panic params + no external obstacles ⇒ core byte-identical to today
  (hash unchanged); no vehicle interpenetration on-road; no pedestrian crosses the hard edge;
  deterministic runs.
- **Behavioural targets:** panic front propagates outward (never teleports); distant traffic stays
  organized/jammed and unaware; jam → `blocked` → driver→pedestrian → foot-exodus cascade
  **emerges**; cars pile at the road edge; evacuation drains toward the away-edges.
- **Acceptance = the viz** (organized traffic, incident marker, fear overlay, fleeing cars,
  abandoned-car obstacles, externally-driven pedestrians, the known-world edge), backed by the
  invariants.

## 6. Phased roadmap (panic-first; each phase watchable)

1. **Spine** — non-sublane organized traffic; external incident + radius fear; external sets Flee
   params on nearby cars; core drives aggressive on-road flee; core emits `blocked`; external
   converts (stop vehicle → obstacle + spawn pedestrian) and drives pedestrians (ORCA +
   fake-navmesh, cars as obstacles) to the away-edge. Both rendered. *Done:* full transition plays
   once. **(Vehicle Orca-mode not needed yet — Flee → blocked → pedestrian.)**
2. **Panic as local information** — contagion + LoS + jam-unease; distant traffic oblivious.
3. **Vehicle Orca-mode / off-road** — cars push into the vicinity (shaped NH free-space model, in
   the core) before blocking; richer fake-navmesh buffer.
4. **Sublane realism (optional, separate)** — filter-to-front at lights in the organized phase.
5. **Scale** — hundreds → thousands; spatial indexing; far side stays jammed and unaware.

## 7. Open decisions (pin at Phase-1 kickoff)

- The `blocked` signal definition (stuck-duration + no feasible progress) and whether it's a new
  explicit per-vehicle signal or derived from `DrModel.Stationary` + a dwell timer.
- The per-vehicle **param surface** the core exposes (`aggressiveness` → which SUMO params;
  `mode`; flee route) — existing vs. small additive opt-in fields — and the stop-vehicle→obstacle
  command.
- Vicinity buffer width; hard-edge representation (buffered-polygon boundary as an ORCA obstacle
  loop vs. explicit fence).
- Away-direction flee-goal from the road graph; what counts as an away-edge "escape".
- Fear constants (θ_panic, decay, contagion kernel) — tuned against the viz.

## 8. Relationship to other docs

- `INDIA-TRAFFIC.md` — shaped / non-holonomic / ORCA **machinery** (`Sim.Core.Mixed`,
  `ShapedVoSolver`, bicycle model): reusable substrate (Phase 3 vehicle Orca-mode + the external
  pedestrian ORCA), not the headline.
- `LANELESS-DIRECTION.md` — the open-space ORCA regime + cross-regime bridge (`ExternalObstacle` /
  `WorldDisc` / `SetExternalObstacles`) the seam is built on.
- `SUMOSHARP-DEADRECKONING.md` (NuGet branch) — the `DrModel` seam the `blocked` signal rides.
