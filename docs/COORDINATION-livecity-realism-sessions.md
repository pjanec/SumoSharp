# COORDINATION — live-city realism sessions (three-way boundary)

One source of truth for who owns what across the concurrent live-city realism sessions, so no two
sessions edit the same mechanism. Keep this short; update it when a boundary moves.

**Shared iron law (all sessions):** `dotnet test tests/Sim.ParityTests -c Release` = **657/4**
byte-identical; `Sim.Bench` hash **`D96213B7BB4021A7`** (par==single); `dotnet test
tests/Sim.LiveCity.Tests` = **25/25** (run WITHOUT `--no-build` — it is not in `Traffic.sln`); no
`System.Random`; demo/goldens byte-identical (new behaviour gated on `CrowdSource != null` or a demo-only
flag no golden sets); netstandard2.1 + `LiveCitySim` consumer contract preserved.

---

## Ownership map

| Session | Branch | Owns | Brief |
|---|---|---|---|
| **realism-A/B** | `claude/livecity-realism-fixes-vr4k4b` | **Task A** — stopped-car lateral wobble → **DONE**: demo-gated `SuppressHeldCrowdSwerve` (held static-ped crowd-swerve suppression in `ComputeLateralEvasion`; the earlier blanket `FreezeLateralWhenStopped` clamp was reverted+removed). | `LIVE-CITY-REALISM-AB-DESIGN.md` §Task A |
| **ped–vehicle avoidance** | `claude/livecity-ped-vehicle-avoidance` *(to be started)* | **car↔ped coupling**: B-guard (world-space hard ped-safety in zone) + B-api (`ExternalObstacle` string→`WorldDisc`/handle) + **C5** (ped-avoids-car disc feed, realism #5). *(#4 moved to ped-LOD-lifecycle — root is demotion, not coupling.)* | `LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md` |
| **ped-LOD-lifecycle** | `claude/livecity-ped-lod-lifecycle` *(to be started — parallel-safe)* | **ped LOD promote/demote switching** (low↔high power): #3 promote handoff (ped vanishes), #4 demote trigger + route restore (wandering ORCA), #6 idle clustering / randomize destinations. Edit surface `src/Sim.Pedestrians/Lod/` (+ demand + viz snapshot). Only *produces* `ICrowdFootprintSource`; consumes nothing car-side. | `LIVE-CITY-PED-LOD-LIFECYCLE-HANDOFF.md` |
| **arbitrary-net** | `claude/discussion-eqp53m` | net import, `SumoRouteGraphNav`/`IPedNavigation`, single realism zone (`_lcZone*`/`SetLcRealismZone`/one `InterestSource`), `Engine.RegionPlan` enablement. Delivers seams; later road-net-enables + zone-bounds the C5 feed. | `LIVE-CITY-ARBITRARY-NET-DESIGN.md`, `LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md` |
| *W4 multi-camera zones* | *unallocated* | N `InterestSource`s, N-zone car LC-realism, `SetLcRealismZones`, C5 union re-point, optional bit-identical `OrcaCrowd` disc index. Deferred — ped–vehicle session or a later dedicated one. | `LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md` |

## No-touch lists (edit surface, by owner)

- **realism-A/B owns → others don't touch:** `ComputeSublaneLateral`/`ComputeRvoLateral` (~8654–8712), the
  lateral commit apply (~9604), the crowd-swerve suppression gate + `SuppressHeldCrowdSwerve` flag in
  `ComputeLateralEvasion` (~9270).
- **ped–vehicle avoidance owns → others don't touch:** `CrowdLongitudinalConstraint` (~8582), the B6 swerve
  (~9198), the `WorldDisc`/`ICrowdFootprintSource`/`CrossRegimeCoupling` seam, `ExternalObstacle` public
  API, `OrcaCrowd` **external-disc** handling (`SetExternalObstacles`).
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

- arbitrary-net has marked its **C5 enablement BLOCKED** pending the ped–vehicle session.
- Task B was originally in the realism-A/B `AB-DESIGN` doc; it has been **reassigned** to the ped–vehicle
  session (car↔ped coupling belongs with one owner). AB-DESIGN §Task B remains the design reference.
- Live queue + this allocation are tracked in `docs/TASKS-TODO.md` → "Live-city realism".
