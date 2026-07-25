# HANDOFF — live-city ped↔vehicle avoidance (high-realism zone)

**Self-contained brief for a SEPARATE session** that will own **bidirectional car↔pedestrian avoidance**
inside `LiveCitySim`'s high-realism zone. Read top-to-bottom; it assumes near-zero prior context.
**This is NOT your design.** Per `CLAUDE.md`, do your own design-first trio (`design → tasks → tracker`)
in `docs/`, get owner agreement, then implement. Facts marked **[verified]** were checked against source
(file:line given); treat the rest as a lead.

Suggested branch: `claude/livecity-ped-vehicle-avoidance`. Doc prefix: `LIVE-CITY-PED-VEHICLE-AVOIDANCE-*`.

---

## 0. Why this session exists (the split)

Three live-city sessions were separated to avoid two mechanisms fighting over the same code:

- **realism-A/B session** (`claude/livecity-realism-fixes-vr4k4b`) — owns **only** Task A (a stopped car's
  sublane lateral wobble). It does **not** touch the crowd/ORCA reaction paths. Leave those alone.
- **arbitrary-net session** (`claude/discussion-eqp53m`) — owns SUMO road-net import,
  `SumoRouteGraphNav`/`IPedNavigation`, the **single** realism zone (`_lcZone*` + `SetLcRealismZone`, one
  `InterestSource`), and `Engine.RegionPlan` enablement. It **delivers the seams** you build on and has
  marked its C5 enablement **BLOCKED** pending you.
- **YOU (this handoff)** — own the entire **car↔ped coupling**: cars hard-yield to peds *and* peds dodge
  cars, unified on the world-disc seam. One owner, one mechanism.

See `docs/COORDINATION-livecity-realism-sessions.md` for the full three-way boundary + no-touch lists.

## 1. Goal & owner intent (verbatim)

> "similar to what the engine already has for external agents (dodging / stopping in front of them) —
> nice to **unify** the mechanism. The external-agent API is maybe using **string names** which is
> performance-unfriendly and needs redesign." **Hard requirement: in the high-realism zone a car must
> NEVER crash into a ped, nor pass one at close distance / high speed.** And (realism #5): "**ORCA peds
> should avoid a car standing in jam on the crosswalk**" (peds walk around stopped/abandoned cars).

This is a **guarantee**, not best-effort tuning.

## 2. Scope — three items that are ONE mechanism

| Item | What | Prior brief |
|---|---|---|
| **B-guard** | High-realism-zone **world-space hard ped-safety guard**: a car inside the zone can never overlap or close-fast-pass a ped, computed in world space (NOT lane projection). | `docs/LIVE-CITY-REALISM-AB-DESIGN.md` §Task B |
| **B-api** | Retire the string-keyed public `ExternalObstacle` registration onto the neutral **`WorldDisc`/integer-handle** seam (the performance redesign the owner asked for). | AB-DESIGN §Task B "Fix design" |
| **C5** | Feed live vehicles as world discs into the demo's ORCA crowd (**ped-avoids-car**), zone-bounded, so peds dodge stopped cars. | `LIVE-CITY-ARBITRARY-NET-DESIGN.md` §5.8; realism #5 |

**Also inherit the overlap note:** realism **#4** (ORCA peds leaving the zone stay ORCA and wander
off-route) overlaps B's "wandering ORCA" residual — coordinate, but it's primarily a `PedLodManager`
demotion bug (task #25), not the collision guard.

## 3. The substrate already exists — read it first [verified]

The bidirectional coupling is **already built and parity-inert**; your job is to bring it into the demo
and add the hard zone backstop, not to invent it.

- **`src/Sim.Core/Bridge/CrossRegimeCoupling.cs`** [verified] — steps a lane `Engine` and an `OrcaCrowd`
  in lockstep so both populations mutually avoid:
  - **Direction A (crowd avoids vehicles)** = your C5: turns each vehicle into ≤`MaxDiscsPerVehicle`(=6)
    world discs and calls `OrcaCrowd.SetExternalObstacles` (`:101`). `SubSteps` (`:41`) dead-reckons the
    discs at `dt/K` **specifically to stop a fast vehicle teleporting `speed*dt` and grazing a ped** — read
    that comment; it *is* Task B's internal-lane close-fast-pass failure mode.
  - **Direction B (vehicles avoid crowd)** = the car-brakes-for-ped path: sets `Engine.CrowdSource`
    (`:62`); each vehicle projects nearby crowd agents (`Engine.ComputeRvoLateral` /
    `CrowdLongitudinalConstraint`).
- **`Engine.CrowdSource`** (`src/Sim.Core/Engine.cs:764`, `ICrowdFootprintSource`) [verified] — the car→ped
  reaction. `CrowdLongitudinalConstraint` (`:8582`) is **gated `if (CrowdSource is null) return +Inf`**
  (`:8584`) → byte-identical for every golden/bench (none sets `CrowdSource`). This gate is your
  parity-inertness guarantee — keep every new car-side reaction behind it (or a demo-only flag).
- **The string API is already half-retired** [verified]: `Engine.cs:268` — the per-step external-obstacle
  store `_obstacles` is now a **value-type SoA** that "replaced the former `Dictionary<string,
  ExternalObstacle>`". `ExternalObstacle` is a value type. What remains for **B-api** is the **public
  registration/update surface** (grep `ExternalObstacle`, `AddObstacle`/`UpdateObstacle`,
  `ObstacleHandle`) — move consumers off string names onto the integer `ObstacleHandle`/`WorldDisc` path.
- **Seam types** [verified]: `WorldDisc.cs` (+ `ICrowdFootprintSource` `:34`), `CompositeFootprintSource.cs`
  (fold multiple sources into `Engine.CrowdSource`), `src/Sim.LiveCity/InflatedFootprintSource.cs` (the
  shipped velocity-preserving ORCA disc inflate, r=0.6 — compose with it, don't fight it).
- **The B6 swerve** (`Engine.cs:9198`, synthesises an `ExternalObstacle` ~`:9223`) is the emergency
  lateral evasion. Fold it into the unified world-disc reaction pass if you consolidate.

**How `LiveCitySim` wires ORCA today is the thing to investigate first** — it uses `CrowdSource` +
`InflatedFootprintSource` for Direction B (car-avoids-ped) but may not feed Direction A (C5). Confirm and
decide whether to route the demo through `CrossRegimeCoupling` or a lighter zone-bounded feed.

## 4. Diagnoses — already pinned, do NOT re-derive

Read `docs/LIVE-CITY-REALISM-AB-DESIGN.md` §Task B and `docs/LIVE-CITY-REALISM-ATTEMPT-LOG.md` (A/B trail).
Summary: cars still close-fast-pass ORCA peds on **internal (`:`) junction lanes**; the crowd brake
projects the ped disc onto the lane frame (`LaneProjection.Project`), which **misjudges a diagonally
crossing ped on a short/curved internal lane**, so the ped slips the gate and the car re-accelerates. The
r=0.6 footprint inflate fixed head-on cases, not this diagonal-on-internal-lane one. → the fix must NOT
rely on lane projection; use a **world-space** proximity test inside the zone.

## 5. Repro & verification diagnostics (already built)

From `src/Sim.Viz` (`dotnet run --project src/Sim.Viz -c Release --no-build -- <mode>`):
- `--live-city-orcatrace <steps> [carId]` — every MOVING car whose bumper is over an ORCA ped anywhere
  (incl. mid-junction), split by internal-lane / fast / crowd-brake-engaged. **The Task-B repro tool.**
  **Extend it** to flag a *close-fast-pass* (moving car within, say, <1 m of a ped at >2 m/s inside the
  zone) — that's your primary success metric.
- `--live-city-demo <out.html> [steps]` — DR-smoothed replay for the owner to eyeball (10× density via
  `LIVECITY_PEDS=1600`; 30 MiB delivery cap — see AB-DESIGN §0 for the density/step math).
- Binder code **13** = `CrowdLongitudinalConstraint` engaged (car waiting on a ped/crowd).

## 6. Success conditions (yours to meet)

1. `--live-city-orcatrace 400` at `LIVECITY_PEDS=1600`: **0** ORCA drive-throughs **and 0** close-fast-passes
   for cars inside the high-realism zone; outside the zone unchanged.
2. **Peds visibly dodge a car standing on a crosswalk** (C5) — construct/point at a repro where a car is
   held on a crossing and assert ORCA peds route around it, not through it.
3. Throughput preserved: `DenseFlow_…NoGridlock` green; `carArrivedTotal` within noise of baseline. **Do
   NOT reintroduce the velocity-0 over-brake** (a moving ped must be *followed*, not treated as a dead
   stop — the `GateOrcaPedsOnCrossing` lesson cost 15% throughput; preserve ped velocity in every disc).
4. **Parity `657/4` byte-identical + bench `D96213B7BB4021A7` (par==single) + LiveCity `25/25`** — proves
   the guard, the C5 feed, and the API redesign are all inert on goldens (gated on `CrowdSource`/demo flag).
5. If you redesign the string→handle `ExternalObstacle` API, keep/port its existing tests (grep
   `ExternalObstacle` in `tests/`) and add coverage that the world-disc path reproduces the old dodge/stop.

## 7. Iron laws (inherited, non-negotiable)

- Parity `dotnet test tests/Sim.ParityTests -c Release` = **657/4** byte-identical; `Sim.Bench` hash
  **`D96213B7BB4021A7`** (par==single); `dotnet test tests/Sim.LiveCity.Tests` = **25/25** (run this one
  **without** `--no-build` — it is NOT in `Traffic.sln`).
- No `System.Random`; per-entity seeded RNG only.
- Demo/goldens byte-identical: every new reaction behind `CrowdSource != null` or a demo-only flag no
  golden sets. `MaxCrowdDiscs = 256` and the r=0.6 inflate are shipped — don't regress them.
- netstandard2.1 across the consume chain; the `LiveCitySim` consumer contract preserved.

## 8. Boundary — do NOT touch (owned by other sessions)

- **realism-A/B (us, Task A DONE):** the sublane lateral driver `ComputeSublaneLateral`/`ComputeRvoLateral`
  (~8654–8712), the lateral commit apply (~9604), and the crowd-swerve suppression gate +
  `SuppressHeldCrowdSwerve` flag in `ComputeLateralEvasion` (~9270). NOTE: the old blanket
  `FreezeLateralWhenStopped` clamp was **reverted and removed** (it caused car–car overlaps, §F2). The redo is
  targeted: when a car is HELD by a static crowd agent (`BindingConstraint == 13`) it **recentres** (does not
  swerve, does not freeze) — so don't add a competing lateral nudge on a car stopped for a ped; it already
  sits centred in-lane.
- **arbitrary-net:** net import, `SumoRouteGraphNav`/`IPedNavigation`, the single realism-zone surface
  (`_lcZone*`, `SetLcRealismZone`, one `InterestSource`), `Engine.RegionPlan`/`RegionGrid` (538–635). They
  will later road-net-enable + zone-bound your C5 feed behind their helper — coordinate the fed disc set
  with them.
- **Multi-camera zones (W4)** are a *separate* handoff (`docs/LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md`),
  not yours unless explicitly folded in.

## 9. Key files

- `src/Sim.Core/Bridge/` — `CrossRegimeCoupling.cs`, `WorldDisc.cs`, `CompositeFootprintSource.cs`.
- `src/Sim.Core/Engine.cs` — `CrowdSource` (`:764`), `CrowdLongitudinalConstraint` (`:8582`), the B6 swerve
  (`:9198`), the `ExternalObstacle` value store (`:268`) + public API (grep `ExternalObstacle`/`ObstacleHandle`),
  `MaxCrowdDiscs`/`MaxRvoNeighbors`.
- `src/Sim.Core/Orca/OrcaCrowd.cs` — `SetExternalObstacles`, the spatial hash / external-disc scan.
- `src/Sim.LiveCity/LiveCitySim.cs` — ORCA wiring, `HighRealismPocketX/Y`/`HighRealismPromoteRadius`,
  `SetLcRealismZone`, the per-area LOD block; `InflatedFootprintSource.cs`; `LiveCityConfig.cs`.
- `src/Sim.Viz/Program.cs` — `RunLiveCityOrcaTrace`, `--live-city-demo`.
- Prior context: `LIVE-CITY-REALISM-AB-DESIGN.md` §Task B, `LIVE-CITY-REALISM-ATTEMPT-LOG.md`,
  `LIVE-CITY-ARBITRARY-NET-DESIGN.md` §5.8/§5.9, `docs/LANELESS-DIRECTION.md` (the cross-regime bridge).

## 10. Open questions for your design phase

1. Route the demo through `CrossRegimeCoupling` (full bidirectional, sub-stepped) or a lighter demo-only
   zone-bounded feed? (Profile: sub-stepping the crowd K× per engine step has a cost.)
2. Is the world-space zone guard a hard speed-cap / emergency brake keyed on nearest-ped world distance,
   layered *over* the existing `CrowdLongitudinalConstraint`, or a replacement for it inside the zone?
3. How far do you take B-api this pass — full string→handle public redesign, or a thin handle adapter now
   and the full retirement later?
