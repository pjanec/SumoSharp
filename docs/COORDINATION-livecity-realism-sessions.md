# COORDINATION — live-city realism sessions (three-way boundary)

One source of truth for who owns what across the concurrent live-city realism sessions, so no two
sessions edit the same mechanism. Keep this short; update it when a boundary moves.

**Shared iron law (all sessions), post-PR#13 baseline:** `dotnet test tests/Sim.ParityTests -c Release` =
**755 pass / 4 skip (759 total)** with all **661 goldens byte-identical**; `Sim.Bench` hash
**`BF3794A4704BCD79`** (par==single — moved from `D96213B7BB4021A7` when the seven junction gates defaulted ON,
PR#13; re-pinned tripwire, no SUMO reference); `dotnet test tests/Sim.LiveCity.Tests` all green (run WITHOUT
`--no-build` — it is not in `Traffic.sln`; **50/50** post-PR#13, **53/53** once car-yields-ped merges);
`tests/Sim.ParityTests` becomes **775/4** once car-yields-ped merges (+20 tests, none of main's perturbed);
`tests/Sim.Pedestrians.Tests` all green (277
on the ped-LOD-lifecycle branch); no `System.Random`; demo/goldens byte-identical (new behaviour gated on
`CrowdSource != null` or a demo-only flag no golden sets); netstandard2.1 + `LiveCitySim` consumer contract
preserved.

---

## Ownership map

| Session | Branch | Owns | Brief |
|---|---|---|---|
| **realism-A/B** | `claude/livecity-realism-fixes-vr4k4b` | **Task A** — stopped-car lateral wobble → **DONE**: demo-gated `SuppressHeldCrowdSwerve` (held static-ped crowd-swerve suppression in `ComputeLateralEvasion`; the earlier blanket `FreezeLateralWhenStopped` clamp was reverted+removed). | `LIVE-CITY-REALISM-AB-DESIGN.md` §Task A |
| **car-yields-ped** | `claude/live-city-car-yields-ped-i4rczr` — **DONE, PR open to main** | **car→ped YIELD (Task B-guard)** delivered: `Engine.SetCrowdYieldZone` + L1 crowd-swerve suppression in `ComputeLateralEvasion` + `CrowdYieldConstraint` (**binder 16** — 14/15 are PR#13's junction constraints) + `VehicleFootprint`. **Also fixed, and this is a SHARED SEAM every session touches:** `ICrowdFootprintSource.QueryNear` now returns the **nearest** movers, not an arbitrary enumeration-order subset (`OrcaCrowd`, `CompositeFootprintSource`, `CrossingOccupancySource`, all via `Sim.Core.Bridge.WorldDiscQuery`). Demo @800 peds: cars-driving-AT-a-ped **11 → 0**. | `LIVE-CITY-CAR-YIELDS-PED-{DESIGN,TASKS,TRACKER}.md` |
| **ped–vehicle avoidance** | `claude/livecity-ped-vehicle-avoidance` *(to be started)* | **car↔ped coupling minus the yield**: B-api (`ExternalObstacle` string→`WorldDisc`/handle) + **C5** (ped-avoids-car disc feed, realism #5). *(B-guard moved to car-yields-ped — the car→ped yield is that session's mechanism; #4 moved to ped-LOD-lifecycle.)* | `LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md` |
| **ped-LOD-lifecycle** | `claude/livecity-ped-lod-lifecycle-bylitj` *(STARTED — parallel-safe)* | **ped LOD promote/demote switching** (low↔high power): #3 promote handoff (ped vanishes), #4 demote trigger + route restore (wandering ORCA), #6 idle clustering / randomize destinations. Edit surface `src/Sim.Pedestrians/Lod/` (+ demand + viz snapshot). Only *produces* `ICrowdFootprintSource`; consumes nothing car-side. | `LIVE-CITY-PED-LOD-LIFECYCLE-HANDOFF.md` |
| **F3 junction overlap** | `claude/f3-junction-overlap-handoff-okf5nu` *(STARTED)* | pre-existing junction car–car overlap (into-occupied / keep-clear) + F4b zero-overlap invariant. Edits `Engine.cs` **junction** methods (`JunctionYieldConstraint` ~6642–7134, `AdaptToJunctionLeader`, `KeepClear`). | `F3-JUNCTION-OVERLAP-HANDOFF.md` |
| **arbitrary-net** | `claude/discussion-eqp53m` | net import, `SumoRouteGraphNav`/`IPedNavigation`, single realism zone (`_lcZone*`/`SetLcRealismZone`/one `InterestSource`), `Engine.RegionPlan` enablement. Delivers seams; later road-net-enables + zone-bounds the C5 feed. | `LIVE-CITY-ARBITRARY-NET-DESIGN.md`, `LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md` |
| *W4 multi-camera zones* | *unallocated* | N `InterestSource`s, N-zone car LC-realism, `SetLcRealismZones`, C5 union re-point, optional bit-identical `OrcaCrowd` disc index. Deferred — ped–vehicle session or a later dedicated one. | `LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md` |

## No-touch lists (edit surface, by owner)

- **realism-A/B — DONE & MERGED (Task A).** Its `SuppressHeldCrowdSwerve` gate + the lateral commit apply
  (~9604) are on `main`; the car-yields-ped session inherits and widens the crowd-swerve gate (below). Other
  sessions still don't touch `ComputeSublaneLateral`/`ComputeRvoLateral` (~8654–8712).
- **NEW SHARED CONTRACT (car-yields-ped, affects everyone consuming the crowd seam):**
  `ICrowdFootprintSource.QueryNear` must fill the caller's span with the **NEAREST** movers, ordered
  nearest-first, ties broken by enumeration order — use `Sim.Core.Bridge.WorldDiscQuery.InsertNearest`.
  Any NEW `ICrowdFootprintSource` implementation must honour it or a vehicle can be blind to the pedestrian
  directly in front of it at density. `Engine.MaxCrowdDiscs` (256) is now a fidelity knob on top of that
  contract, not the safety mechanism — see `LIVE-CITY-CAR-YIELDS-PED-DESIGN.md` §8.2.
- **car-yields-ped owns → others don't touch:** the `ComputeLateralEvasion` **crowd-swerve prefer-gate**
  (~9253–9310) + the `SuppressHeldCrowdSwerve` flag/gate (~9270, inherited from Task A), `CrowdLongitudinalConstraint`
  (~8582), the B6 swerve (~9198). (i.e. the entire car→ped longitudinal/lateral reaction.)
- **ped–vehicle avoidance owns → others don't touch:** the `WorldDisc`/`ICrowdFootprintSource`/
  `CrossRegimeCoupling` seam, `ExternalObstacle` public API, `OrcaCrowd` **external-disc** handling
  (`SetExternalObstacles`). *(The car→ped yield reaction moved to car-yields-ped; this session is the API +
  ped-side C5 feed.)*
- **ped-LOD-lifecycle owns → others don't touch:** `src/Sim.Pedestrians/Lod/` promote/demote internals
  (`PedLodManager`, `InterestSource`, route re-derivation, dwell/demote timing), ped demand/idle-destination
  assignment. May use `OrcaCrowd` **agent lifecycle** (`Add`/`Remove`) — a different surface from the
  ped–vehicle external-disc feed. **Must NOT** change the `ICrowdFootprintSource` contract or
  `PedLodManager.HighPowerFootprints` semantics without pinging the car sessions (they consume it).
- **arbitrary-net owns → others don't touch:** net import, `SumoRouteGraphNav`/`IPedNavigation`, the
  single realism-zone surface, `Engine.RegionPlan`/`RegionGrid` (538–635).

## Shared seams (coordinate before editing)

- **`LiveCitySim.cs`** — all three add wiring here, in *different methods*. Additive; keep edits local to
  your own method to avoid merge churn.
- **C5 / `WorldDisc` feed** — ped–vehicle session owns the mechanism + behaviour; arbitrary-net owns the
  road-net enable + zone-bound of *which* discs are fed. Agree the seam signature between you.
- **The high-realism zone** — arbitrary-net owns the single-zone surface; ped–vehicle reads it (guard is
  zone-scoped); W4 generalizes it to N zones. Read, don't fork.

## Status notes

- **ped-LOD-lifecycle (`claude/livecity-ped-lod-lifecycle-bylitj`) — #3/#4/#6 DONE.** #3 promote flicker fixed
  (seed-on-switch in `HeadlessIg` + crowd-frame de-fragmentation in `PedLodManager.Step`: emit samples
  contiguously so the wire isn't fragmented by interleaved heartbeats). #4b off-graph route recovery
  (`PedLodManager.RecoverRoute`). #4a leaky-dwell/watchdog **dropped** — trace evidence showed no server-side
  stuck-ORCA; the wander was #3. #6 idle clustering fixed via a crosswalk-wait BLOB + diagonal cross in
  `PedDemand` (opt-in `CrosswalkWaitSpreadRadius`, demo-only). Added a headless `--live-city-pedtrace` harness +
  `PedLodManager.DiagnosticSnapshot`/`LiveCitySim.PedLodDiagnostics`. All parity-inert. See
  `docs/LIVE-CITY-PED-LOD-LIFECYCLE-*`.
- arbitrary-net has marked its **C5 enablement BLOCKED** pending the ped–vehicle session.
- Task B was originally in the realism-A/B `AB-DESIGN` doc; it has been **reassigned** to the ped–vehicle
  session (car↔ped coupling belongs with one owner). AB-DESIGN §Task B remains the design reference.
- Live queue + this allocation are tracked in `docs/TASKS-TODO.md` → "Live-city realism".
