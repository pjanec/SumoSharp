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
| **realism-A/B** | `claude/livecity-realism-fixes-vr4k4b` | **Task A** — stopped-car sublane lateral wobble → demo-gated `FreezeLateralWhenStopped` clamp. | `LIVE-CITY-REALISM-AB-DESIGN.md` §Task A |
| **ped–vehicle avoidance** | `claude/livecity-ped-vehicle-avoidance` *(to be started)* | **car↔ped coupling**: B-guard (world-space hard ped-safety in zone) + B-api (`ExternalObstacle` string→`WorldDisc`/handle) + **C5** (ped-avoids-car disc feed, realism #5); wandering-ORCA residual (#4) shared. | `LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md` |
| **arbitrary-net** | `claude/discussion-eqp53m` | net import, `SumoRouteGraphNav`/`IPedNavigation`, single realism zone (`_lcZone*`/`SetLcRealismZone`/one `InterestSource`), `Engine.RegionPlan` enablement. Delivers seams; later road-net-enables + zone-bounds the C5 feed. | `LIVE-CITY-ARBITRARY-NET-DESIGN.md`, `LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md` |
| *W4 multi-camera zones* | *unallocated* | N `InterestSource`s, N-zone car LC-realism, `SetLcRealismZones`, C5 union re-point, optional bit-identical `OrcaCrowd` disc index. Deferred — ped–vehicle session or a later dedicated one. | `LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md` |

## No-touch lists (edit surface, by owner)

- **realism-A/B owns → others don't touch:** `ComputeSublaneLateral`/`ComputeRvoLateral` (~8654–8712), the
  lateral commit choke (~9587), the `FreezeLateralWhenStopped` flag.
- **ped–vehicle avoidance owns → others don't touch:** `CrowdLongitudinalConstraint` (~8582), the B6 swerve
  (~9198), the `WorldDisc`/`ICrowdFootprintSource`/`CrossRegimeCoupling` seam, `ExternalObstacle` public
  API, `OrcaCrowd` external-disc handling.
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
