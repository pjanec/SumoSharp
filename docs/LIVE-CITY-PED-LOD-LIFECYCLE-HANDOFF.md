# HANDOFF — live-city ped LOD lifecycle (low↔high power switching)

**Self-contained brief for a SEPARATE session** that owns the **pedestrian level-of-detail lifecycle** in
`LiveCitySim`: the promote (low-power → high-power ORCA) and demote (high-power → low-power) transitions and
the low-power destination/idle behaviour. Read top-to-bottom; it assumes near-zero prior context.
**This is NOT your design.** Per `CLAUDE.md`, do your own design-first trio (`design → tasks → tracker`) in
`docs/`, get owner agreement, then implement. Facts marked **[verified]** were checked against source
(file:line given); everything else is a **lead** — confirm before building on it.

Suggested branch: `claude/livecity-ped-lod-lifecycle`. Doc prefix: `LIVE-CITY-PED-LOD-LIFECYCLE-*`.

---

## 0. Why this session exists (the split)

Four live-city workstreams were separated so no two sessions edit the same mechanism
(`docs/COORDINATION-livecity-realism-sessions.md` is the source of truth):

- **realism-A/B** (`claude/livecity-realism-fixes-vr4k4b`) — owned Task A (stopped-car crosswalk wobble),
  **DONE**. Car-side lateral only. Doesn't touch peds.
- **ped–vehicle avoidance** (`claude/livecity-ped-vehicle-avoidance`, to be started) — owns **car↔ped
  coupling** (cars hard-yield to peds, peds dodge cars: B + C5). It **consumes** the high-power crowd you
  produce; it does not touch your promote/demote logic.
- **arbitrary-net** (`claude/discussion-eqp53m`) — **merged to main.** Owns net import, `IPedNavigation`/
  `SumoRouteGraphNav`, and the **single realism zone** (`SetLcRealismZone` + the one `InterestSource` whose
  `PromoteRadius`/`DemoteRadius` drive your transitions). You **read** that zone; you don't define it.
- **YOU (this handoff)** — own the **ped LOD lifecycle**: the promote/demote transitions inside
  `PedLodManager` and the low-power ped destination/idle behaviour. One owner, one mechanism.

**Why you're parallel-safe with all of them:** your entire edit surface is `src/Sim.Pedestrians/Lod/`
(+ ped demand + the viz snapshot). The one shared interface is `PedLodManager.HighPowerFootprints`
(`ICrowdFootprintSource`) → `Engine.CrowdSource`: a **produce/consume** seam. You *produce* the footprint
source; the car sessions *consume* it. Change promote/demote **internals** freely; do **not** change the
`ICrowdFootprintSource` contract or `HighPowerFootprints` semantics without pinging the car sessions.

## 1. Goal & owner intent (verbatim)

From the 2026-07 demo replay review, three pedestrian-LOD unrealisms the owner reported:

> "**low-power peds DISAPPEAR** when promoted into the pocket (they re-appear as ORCA a moment later)."
> "**ORCA peds leaving the zone STAY ORCA and wander** off-route — they don't switch back / don't rejoin
> the sidewalk." "low-power peds **merge to a single junction point and idle** there — randomize where they
> go / where they wait."

These are LOD-lifecycle bugs (switching + demand), **not** car↔ped collision issues.

## 2. Scope — three items, one subsystem

| Item | Symptom | Root (lead) |
|---|---|---|
| **#3 promote flicker** | a low-power ped **vanishes for a frame** on promotion, then appears as ORCA | one-sided handoff across the LOD switch instant: after `PublishSwitch(→FreeKinematic)` the low-power pose is retired but the first high-power sample may not be in the SAME rendered frame → a one-frame gap. Likely a **consumer/snapshot-side** fix. |
| **#4 stuck-ORCA / wander** | a ped that left the zone **stays high-power and walks off-route** | two sub-causes: (a) the **demote trigger never completes** (the `OutsideSince` dwell countdown keeps cancelling at the zone edge, or `ForcedHighPower` is stuck); (b) on demote, **route restore falls back to a straight line** when `IPedNavigation.FindPath` returns null → off-route wander. |
| **#6 idle clustering (LOW PRI)** | low-power peds **cluster at one junction and idle** | shared/'small destination set + a single idle beat → everyone routes to the same point. Randomize destinations / idle spots. |

## 3. The substrate — read it first [verified]

- **`src/Sim.Pedestrians/Lod/PedLodManager.cs`** — the whole LOD state machine. `PedEntry` holds
  `Model` (`PathArc` / `ActivityTimeline` low-power; `FreeKinematic` high-power), `HighIndex` (the
  `OrcaHandle` into the persistent high-power crowd, `Invalid` when low), `StateEnteredAt`, `OutsideSince`.
  - **`Step()` transition decisions** [verified `:369–391`]: **promote** when
    `stateAge >= dwellSeconds && interestField.Query(pos).AnyWithinPromote`; **demote** when
    `!ForcedHighPower && AllOutsideDemote`, but only after the `OutsideSince` countdown has held
    `dwellSeconds` continuously — and `:391` **resets `OutsideSince = NaN` the moment the ped is back inside
    ANY source's demote radius**. A ped loitering at the zone edge can therefore never complete the dwell →
    **#4a lives here.**
  - **promote apply** [verified `:399–420`]: re-routes `_navigation.FindPath(pos, Destination)` (**null →
    straight-line fallback** `{pos, Destination}`), sets `Model = FreeKinematic`, `_highCrowd.Add(...)`,
    `_highController.AddRoute(...)`, then `_publisher.PublishSwitch(id, PathArc, FreeKinematic, now)`.
  - **demote apply** [verified `:426–468`]: `_navigation.FindPath(pos, Destination)` (**same null →
    straight-line fallback → #4b**), `ReanchorAt` the resume leg at the frozen pose (no positional pop),
    `_highController.RemoveRoute` + `_highCrowd.Remove`, then `PublishPathArc`/`PublishActivityTimeline` +
    `PublishSwitch(FreeKinematic → …)`.
- **The produce/consume seam** [verified]: `PedLodManager.HighPowerFootprints => _highCrowd`
  (`:99`, an `OrcaCrowd : ICrowdFootprintSource`) is wired into `Engine.CrowdSource`. **This is the contract
  you must not break** — the car sessions read it. Your Add/Remove of high-power agents is the *agent
  lifecycle* surface of `OrcaCrowd`; the ped–vehicle session uses the *external-obstacle* surface
  (`SetExternalObstacles`) — different methods, coordinate if you both edit `OrcaCrowd.cs`.
- **The publish→consume chain for #3** [verified anchors, mechanism is a lead]:
  `PedPublisher.Publish*` → **`src/Sim.Pedestrians/Lod/PedReplicationReceiver.cs`** applies records in a
  fixed per-step order — "PathArc legs, ActivityTimeline legs, lifecycle (DR-switch) events, then the
  FreeKinematicSample batch" (`:29–31`); the switch is a `DrSwitchEvent` (`:61`), promote/demote map at
  `:78–79`. Then **`src/Sim.LiveCity/PedInterpolator.cs`** / `LiveCitySnapshot` produce the per-frame pose
  the demo renders. **Investigate whether, at the switch time `now`, a ped has BOTH its retired low-power
  leg AND its first high-power sample available** — if not, that's the one-frame disappearance.
- **Interest geometry (arbitrary-net's, you READ it)** [verified]: `InterestSource.cs` `PromoteRadius` /
  `DemoteRadius` (`:18–19`, `DemoteRadius > PromoteRadius` = spatial hysteresis); `InterestField.Query`
  returns `AnyWithinPromote` / `AllOutsideDemote`. The demo's single zone is set via `SetLcRealismZone`
  (arbitrary-net surface). **You own the dwell/timing/transition logic; you do NOT redefine the zone or its
  radii** without coordinating with arbitrary-net's zone owner.
- **Low-power destination/demand for #6** [lead]: `src/Sim.LiveCity/LiveCitySim.cs:175`
  `Destinations = odPoints` + `PauseAnimTag = "idle"` (`:188`) feed `PedDemand`
  (`src/Sim.Pedestrians/Demand/PedDemand.cs`); `AddPedLively` takes an `ActivityTimeline` (Walk + Pause/
  Dwell beats). Trace how `odPoints` and idle beats are chosen — the clustering is a demand-side choice.

## 4. Repro & verification diagnostics

- **`--live-city-demo <out.html> [steps]`** (`src/Sim.Viz`) — the DR-smoothed HTML replay the owner used to
  spot all three (crank density with `LIVECITY_PEDS=1600`). Your primary human-eyeball repro; watch a ped
  cross a promote boundary (#3), a ped leave the zone (#4), and the low-power idle spots (#6).
- **Add a headless LOD-lifecycle trace** (there isn't one yet — build it like `--live-city-cartrace`): per
  step, per ped, dump `Model`, `HighIndex.IsValid`, `StateEnteredAt`, `OutsideSince`, world pos. That makes
  #3 (a frame with no pose), #4a (a countdown that never completes), and #4b (post-demote heading != route)
  reproducible as assertions, not eyeballing. **Solid repro before fixing — non-negotiable (owner rule).**
- Unit surface already exists: **`tests/Sim.Pedestrians.Tests/Lod/PedLodManagerTests.cs`** — extend it for
  promote/demote invariants (a promoted ped is renderable every frame; a ped continuously outside every
  demote radius for `dwellSeconds` DOES demote; a demoted ped's path starts at its frozen pose AND follows a
  navigable route, not a straight line).

## 5. Success conditions (yours to meet — refine in your design)

1. **#3:** across any promote (and demote) transition, the ped has a valid rendered pose **every frame** —
   no one-frame gap. Assert via the LOD-lifecycle trace over the demo at `LIVECITY_PEDS=1600`.
2. **#4a:** a ped that leaves the zone and stays outside every `DemoteRadius` for `dwellSeconds` **demotes**
   (deterministically) — no permanently-stuck ORCA peds wandering the map.
3. **#4b:** a demoted ped **rejoins a navigable sidewalk route** to its destination (uses
   `IPedNavigation.FindPath`, not the straight-line fallback) — assert its post-demote path is multi-segment
   / on-graph, and it doesn't cut across off-route.
4. **#6:** low-power peds spread across **varied destinations/idle spots** (not one junction point) —
   a distribution/spread metric over idle positions, well above the current single-cluster baseline.
5. **Parity `661/4` byte-identical + bench `D96213B7BB4021A7` (par==single) + LiveCity `27/27`.** The whole
   ped/LOD path is gated on `CrowdSource != null` (no golden attaches one), so this is inert by construction
   — but re-run all three to prove it, and keep any determinism (per-entity seeded RNG only; **no
   `System.Random`** — #6's randomization must use a seeded per-ped stream).

## 6. Iron laws (inherited, non-negotiable)

- Parity `dotnet test tests/Sim.ParityTests -c Release` = **661/4** byte-identical; `Sim.Bench` hash
  **`D96213B7BB4021A7`** (par==single); `dotnet test tests/Sim.LiveCity.Tests` = **27/27** (run this one
  **WITHOUT** `--no-build` — it is NOT in `Traffic.sln`). Also run `tests/Sim.Pedestrians.Tests`.
- **No `System.Random`** — per-entity seeded RNG only, results independent of thread order.
- Solid repro before any fix; the DR player may misrender, so prefer authoritative/frame-level ground truth
  over eyeballing the HTML (a lesson from Task A / the demo-integrity findings).
- Determinism is exact in phase 1; #6's destination/idle randomization must be a **seeded, per-ped**
  deterministic draw (same seed → same layout), never wall-clock or `Random`.

## 7. Boundary — do NOT touch (owned by other sessions)

- **ped–vehicle avoidance:** `CrowdLongitudinalConstraint`, the B6 swerve, `CrossRegimeCoupling`,
  `ExternalObstacle` public API, `OrcaCrowd.SetExternalObstacles` (the external-**obstacle** feed). You may
  use `OrcaCrowd.Add`/`Remove` (agent lifecycle) — different surface; coordinate if you both edit
  `OrcaCrowd.cs`.
- **realism-A/B (us, DONE):** `ComputeLateralEvasion`'s crowd-swerve suppression + `SuppressHeldCrowdSwerve`
  (~9270), the lateral commit apply (~9604). Not your area (that's car lateral).
- **arbitrary-net (merged):** `IPedNavigation`/`SumoRouteGraphNav`, net import, the **single realism-zone
  surface** (`SetLcRealismZone`, the zone's `InterestSource` geometry/radii), `Engine.RegionPlan`. You call
  `IPedNavigation.FindPath`; you don't reshape the nav graph or the zone.
- **The `ICrowdFootprintSource` contract / `PedLodManager.HighPowerFootprints` semantics** — shared; don't
  change without pinging the car sessions.
- Shared files, edit your **own** method/region to avoid merge churn: `LiveCitySim.cs` (wiring),
  `OrcaCrowd.cs` (different methods than ped–vehicle).

## 8. Key files

- `src/Sim.Pedestrians/Lod/` — **`PedLodManager.cs`** (the state machine: `Step` `:369–468`, seam `:99`),
  `InterestSource.cs` / `InterestField.cs` (read-only geometry), `PedPublisher.cs`,
  `PedReplicationReceiver.cs` (`:29–79`, the #3 consume order), `PathArcMotion.cs`, `ActivityTimeline.cs`.
- `src/Sim.LiveCity/` — `LiveCitySim.cs` (LOD wiring + demand config `:175–188`), `PedInterpolator.cs`,
  `LiveCitySnapshot.cs` (the #3 per-frame pose), `LiveCityConfig.cs`.
- `src/Sim.Pedestrians/Demand/PedDemand.cs` — destination/spawn (for #6).
- `src/Sim.Core/Orca/OrcaCrowd.cs` — `Add`/`Remove` (your agent-lifecycle surface).
- `src/Sim.Viz/Program.cs` — `--live-city-demo`; add your headless LOD trace here.
- Tests: `tests/Sim.Pedestrians.Tests/Lod/PedLodManagerTests.cs`, `tests/Sim.LiveCity.Tests/`.
- Context: `docs/COORDINATION-livecity-realism-sessions.md` (boundary), `docs/TASKS-TODO.md`
  → "Live-city realism" (#3/#4/#6 one-liners + the parallel-safe note).

## 9. Open questions for your design phase

1. **#3** — is the disappearance a producer bug (no high-power sample emitted at the switch instant) or a
   consumer bug (`PedReplicationReceiver`/`PedInterpolator` drops the ped for one frame)? Localize with the
   LOD trace before choosing where to fix.
2. **#4a** — should the demote dwell be a strict continuous window (current), a leaky/accumulating one, or
   should the zone-edge hysteresis (`DemoteRadius`) be widened? (Widening the radius is arbitrary-net's
   surface — coordinate.) A stuck-ORCA watchdog (force-demote after N seconds outside) may be the pragmatic
   backstop.
3. **#4b** — when `FindPath` returns null from a wandered-off pose, do you re-route to the nearest sidewalk
   node first, or clamp the ped back onto the graph? Decide the "off-graph recovery" policy.
4. **#6** — randomize destinations, idle spots, or both? Where does the seeded per-ped draw live so it stays
   deterministic and parity-inert?
