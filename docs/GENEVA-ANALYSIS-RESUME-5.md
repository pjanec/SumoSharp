# GENEVA-ANALYSIS-RESUME-5 — state after Entries 65–71 (supersedes RESUME-4)

**Predecessors:** RESUME-4 (Entries 59–64; read its UPDATE header). Trail:
`JUNCTION-REALISM-SESSION-JOURNAL.md` Entries 65–71. Written mid-Entry-71 at the owner's request
(usage-rate-limit protection) — §2 says exactly where the in-flight work stands.

## 0. Engine state (verify before believing)

Branch **`claude/sumosharp-traffic-bugs-g1y9hl`** (`git log` is the authority; the LAST commit may
be the Entry-71 WIP — its message says whether gates ran). **main = `65a9d80`** (PR #21, ped-z fix;
before that `a6bf81f` = PR #20, Entries 60–66). CI determinism pin fixed to `A134ED3716DDE7BC`
(was rotted since Entry 54) — both PRs' checks green on it. Gates at every VERIFIED commit: full
`dotnet test -c Release` green (ParityTests **784**/5 goldens byte-identical, LiveCity 92/92
incl. hour-horizon, Peds 324, Host 6, Viewer.Motion 19, DotRecast 2), `Sim.Bench` hash
**`A134ED3716DDE7BC`** par==single — never moved. `demos/City3D/CityLib.Tests` 186/4 (NOT in sln;
builds against the PACKED NuGet — **repack before building it or you test stale code**).
Geneva cut: `D:\Work\GenevaCut\geneva_city.sumocfg`. 3D: repack (rm `~/.nuget/packages/sumosharp*`
→ `demos/City3D/build.sh --pack-only` → build `Viewer.csproj` -c Release → cp Release→Debug in
`.godot/mono/temp/bin`) → `LIVECITY_F3OCCUPANCY=1 cmd //c "D:\\Work\\BIG-master\\SumoSpectacle\\run-geneva-livecity.bat"`.

## 1. Shipped this round (all owner-verdict-driven, all gate-verified)

| Entry | What |
| --- | --- |
| 65 | DIAGSTOP witness (LcPhase plumbing, v2 end-speed split) + E1 closure widening (leader brakeGap runway): std-arm diagonal exposure 67→39 (−42%), ped-heavy flat (residual named: stop causes not leader-shaped). Owner: "diagonal reduced to acceptable state". |
| 66 | **HIREALISM pass-through gate** (owner ask): X1 `RealismMask.forbidPassThrough` on all six ignore-blocker sites (FOE-edge test), `LiveCitySim.SetHighRealismRegions` (circle→edge AABB), `LIVECITY_HIREALISM_RADIUS`, 3D host follows the camera LC zone (`CITY3D_HIREALISM=0` kill). OFF arm byte-identical; ON −3.7% arrivals (accepted honesty cost). |
| PEDZ | **Ped z=0 ROOT-CAUSED + FIXED** (PR #21, merged): wire drop — `SplitWalkAtCrossings`/`SubWalk` sliced Path+HalfWidths but DROPPED `WalkSegment.Elevations` (89% of walks flat on the wire; graph + engine bake were always correct). `[pedz]` instruments under `LIVECITY_PEDZLOG`. Owner-verified in 3D. |
| 67 | Crosswalk "dance" (stopped car dodging a crossing STREAM): Task A's gate keyed on a STATIC threat; identity churn re-aimed the vacating-side dodge. New arm: any crowd threat while ego <0.5 m/s ⇒ recentre, never dodge. Teeth-verified fixture `OpposingPedStream` (OFF pins +2.62→−2.00 at speed 0). |
| 68 | **ORCA runaways**: `RecoverRoute`'s splice keyed on `e.Path` (EMPTY for lively peds) → ~10% of promote/demote recoveries (FindPath failures on the clipped ped graph's islands) fell to the `[pos,destination]` BEELINE. Fix: splice source = `ElevationGeometryOf` (timeline geometry). A/B beeline 763→0. `[pedorca]` census + `LIVECITY_PEDORCALOG` / `LIVECITY_LCZONE_RADIUS` (widens the ORCA pocket headless). Owner: "nice, helped!". |
| 69 | **PED-AVOID-CARS** (owner GO, near-stopped scope): wires the always-empty `externalEntities` seam → `OrcaCrowd.SetExternalObstacles`; disc chains from new `Engine.Lengths`/`Widths` spans. **COURSE-CORRECTED by the hour-horizon gate**: sim-wide default measured 30 (all cars) / 104 (junction-only) long stalls → sim default **OFF** (`PedAvoidCarsInZone`), 3D host opts in (`CITY3D_PEDAVOIDCARS`), `LIVECITY_PEDAVOIDCARS` three-state. Junction-internal-lane scope kept. |
| 70 | Owner verdict fixes on 69: (a) **disc-flicker hysteresis** (qualify <1.5, release >3.0, sticky by EntityIndex) — cures peds locked inside / passing through cars (owner's flicker hypothesis CONFIRMED); (b) **in-zone keep-right** routed through the buffered continuous maneuver instead of the INSTANT LaneHandle flip (the pure-lateral glide; `ApplyKeepRightDecision` returns bool, caller skips same-step sg eval; `keepRightCont` tag). ORCA side-slide around cars parked as **PED-ROUTE-AROUND-CARS design candidate** (planner must see cars; deadlock questions). |

## 2. IN FLIGHT — Entry 71 (owner report: cars passing through stopped blockers on junctions IN the zone)

Owner rule: strictly prohibited, **unless the pair was already overlapping when the zone moved
over them** (grandfather). Diagnosis so far: the 20 s overlap census shows in-zone junction pairs
are mostly static grazes (depth 0.1–0.2) + one persistent 1.6 m pair — the MOVING pass-through
moment is under-sampled at census cadence, and exemplars span multiple binders ⇒ not one gateable
skip ⇒ implemented the owner's rule as a mechanism-independent catch-all:

**`Engine.ZoneNoClipGuard(dt)`** — SERIAL pre-execute pass (called right before `ExecuteMoves` in
the step loop; precedent `RescueStrandedVehicles`): with a RealismMask set, a strict-zone car
(edge fails `MayPassThrough`) advancing into body-penetration of a near-stopped (<0.5 m/s)
strict-zone car it was NOT already overlapping gets `Intent.NewSpeed = 0` this step. Old-bodies
overlap = the grandfather test (under the guard, any in-zone overlap is pre-zone by construction).
Foe body = disc-chain approx (≤4 discs, r = halfWidth) vs ego's exact rect via
`VehicleFootprint.ClearanceToDisc`; 16 m world grid over zone cars only; boundary-crossing steps
v1-exempt (documented gap); clamped cars accrue WaitingTime (recoveries keep counting → fire on
zone exit). Null mask ⇒ immediate return ⇒ byte-identical goldens/bench by construction.
Diagnostics: `Engine.ZoneNoClipClampCount` + `LIVECITY-NOCLIP` witness line; overlap exemplars
now carry `inZone=` + both members' binder/arm/speed (Entry 71 extension in
`ReportOverlapClasses`).

**Status at doc time:** code builds (Sim.Core/Sim.LiveCity/Sim.Viewer); committed as WIP —
**check the last commit's message: if it says gates-not-run, the FULL sln suite + bench hash +
guard-ON/OFF A/B (arrivals cost!) + repack + owner 3D verdict are ALL still owed.** Measurement
runs launched (out/noclip-on.log; capture with LIVECITY_HIREALISM_RADIUS=2500 +
LIVECITY_LCZONE_RADIUS=2500, WITNESS=1): read LIVECITY-NOCLIP clamps + LIVECITY-OVERLAP junction
trend + arrivals vs the no-guard twin (out/overlap-inzone.log, arrivals at t=1200: check both).
**MEASURED before the session ended: v1 REFUTED-as-scoped — 0 clamps fired while in-zone
junction overlaps grew 5→10 (see journal Entry 71 measurement note): the boundary-crossing
exemption is the hole (short internal lanes ⇒ nearly every sweep step crosses a boundary). Extend
the guard's new-pose walk across the lane boundary (route next lane geometry) + consider
mutual-mover pairs; REDO the A/B with matched demand (the first ON arm ran 2000 peds vs 8000).**
Success = in-zone junction overlap formation → ~0 (minus grandfathered), arrivals cost small,
hour-horizon green (no mask there ⇒ trivially), owner sees no on-camera drive-throughs.

## 3. Open queue after Entry 71

1. Owner 3D verdicts pending: Entry 70 fixes (trapped peds, lateral glide) + Entry 71 guard.
2. **veh1762 keepClear chain** (the 1306+ s wedge root; RESUME-4 §2 item 4 verbatim — trace to ITS
   root first, do NOT start with a skip).
3. `:34564` landed crossing standoffs (RESUME-3 family).
4. Design candidates awaiting owner: **PED-ROUTE-AROUND-CARS** (Entry 70), **crossing-lock**
   (owner's "car needn't care about the ped in front while the crossing is locked", Entry 67
   journal), owner's announced full ped-layer redesign (low-power + vanilla-SUMO ped port).
5. Postponed by owner: obstacle-store string→handle hygiene (`o.LaneId != v.LaneId` → LaneHandle;
   Id tie-break → handle order). The store itself is ALREADY handle-keyed SoA (D5) — only these
   two comparison sites remain.

## 4. Method notes this round

- The hour-horizon suite caught a real default-behaviour harm TWICE (Entry 69's 30/104 stalls) —
  "both surfaces" discipline is what turned a plausible feature into a correctly-scoped one.
- CityLib "build OK" can be a NO-OP against a stale NuGet (Entry 69: compile error surfaced only
  after repack). Repack BEFORE trusting any demos/City3D build result.
- Owner hypotheses scored well this round: disc-flicker (Entry 70) exactly right; ORCA-push
  (Entry 68) half-right (push was the trigger, the beeline was the cause).
- Deterministic repro envs: standard `LIVECITY_CARS=4000 LIVECITY_PEDS=2000` (ped-heavy 15000/8000
  variants), always `LIVECITY_F3OCCUPANCY=1 LIVECITY_WITNESS=1 LIVECITY_REROUTE=0`; ORCA work
  adds `LIVECITY_LCZONE_RADIUS=2500 LIVECITY_PEDORCALOG=1`; zone work adds
  `LIVECITY_HIREALISM_RADIUS=2500`; ped-z adds `LIVECITY_PEDZLOG=1`.
