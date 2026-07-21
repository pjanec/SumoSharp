# Live-city demo — status & handoff (resume point)

Working state of the "live city" effort (dense cars + weaving pedestrians + cars yielding to crossing
peds) on branch `claude/pedestrian-dds-transport-c8w2gf`. Read this first after a context compaction, then
the phase design docs. Gate is green throughout unless noted.

## The deliverable
`Sim.Viz --live-city out.html` (also gallery slug `live-city`): a downtown block of the demo-city box with
dense car traffic + a large weaving low-power crowd, where **cars stop for pedestrians on crossings**.
Substrate: `scenarios/_ped/demo_city/box` (bakes to `components=1`). Crop pinned to SumoData's downtown
hero bbox **`[2055,2055,2895,2895]`** in `SceneGen.BuildLiveCity`.

## Contracts / decisions (frozen)
- `docs/SUMOSHARP-LIVE-CITY-DECISIONS.md` — Q1 crossing class→yield table (signalized / unsignalized /
  discouraged), Q2–Q9, hero crop bbox, v1 behavior set.
- **Option B** (cars yield to any crossing ped, no promotion) — accepted, supersedes the spec's Option A.
- **Option A ped signal compliance** (peds wait at red) — chosen; **all 51 box TLs are `type="static"`**
  (deterministic), so waits are analytic and server==IG holds. No actuated TLs desired.

## Phases
- **Phase 1 — builder** (`docs/LIVE-CITY-2D-BUILDER-DESIGN.md`): DONE. `SceneGen.BuildLiveCity` +
  `--live-city` + `RunLiveCity`; `PedLodManager.HighPowerFootprints` accessor; W-A/W-B/W-C.
- **Phase 2 — cheap crossing-yield** (`docs/LIVE-CITY-CROSSING-YIELD-DESIGN.md`): DONE.
  `CompositeFootprintSource` (Sim.Core/Bridge), `CrossingOccupancySource` (Sim.Pedestrians/Crossing).
  `CrowdSource = Composite(HighPowerFootprints, crossingOccupancy)`. Per-crossing promotion dropped; pocket
  anchored on the central intersection. **Vehicle sim not slowed** (engine.Step ≈ 5 ms/step, occupancy
  Update ≈ 0.3 ms/step; CrowdSource null for goldens → parity byte-identical).
- **Phase 2b — peds respect the crosswalk signal** (`docs/LIVE-CITY-CROSSWALK-SIGNAL-DESIGN.md`): IN
  PROGRESS. Fixes the observed "car drives over a grey ped crossing on the car's green" — peds jaywalk
  because the low-power routed crowd has no crosswalk-signal awareness.

## Phase 2b task state
- **P2b-T1 — `CrosswalkSignalSchedule`**: DONE (committed). `src/Sim.Pedestrians/Crossing/
  CrosswalkSignalSchedule.cs`. `NextWalkStart(crossingId, tArrive, crossTime)` = earliest time ≥ tArrive a
  ped may step on and clear within one green window, from the static `<tlLogic>`. `ForCrossing(id, program,
  linkIndex)`, `FromNet(netPath, ids, actuated?)`, `IsSignalized`. 5 tests green
  (`tests/Sim.Pedestrians.Tests/Crossing/CrosswalkSignalScheduleTests.cs`).
- **P2b-T2 — splice the kerb wait into the ped timeline**: DONE (committed). New
  `src/Sim.Pedestrians/Crossing/CrosswalkSignals.cs` (schedule + signalized-crossing polygons;
  `TryLocate`, `NextWalkStart(id,tArrive,speed)`, `FromNet(net, bakedPolys)`; maps the baked crossing
  **lane** id `:c_c0_0` → **edge** id `:c_c0` the net gates by). `PedDemandConfig.CrosswalkSignals`
  (default null → inert). `PedDemand.BuildLivelyTimeline` post-pass `InsertCrosswalkWaits` /
  `SplitWalkAtCrossings`: splits a WalkSegment at a signalized-crossing ENTRY (sub-segment midpoint in a
  crossing poly) and inserts a `PauseSegment(NextWalkStart−tKerb, "wait")` when the ped would arrive on
  red; no rng. 3 tests green (`tests/…/Crossing/CrosswalkSignalComplianceTests.cs`): signals-ON → **0**
  on-crossing-during-red samples (vs 3328 with signals OFF), byte-identical double-run.
- **P2b-T3 — gate class-awareness**: DONE (committed 107407b) — but with a **corrected design**. The
  original plan (skip signalized crossings in the gate) was WRONG: it removed Phase 2's protection against
  a **turning car** on a permissive movement crossing a legitimately-walking ped (the TL gives the ped 'G'
  and the turning car green; the engine doesn't model the yield). Corrected: keep the `CrossingOccupancySource`
  over **all** crop crossings but feed it only **WALKING** low-power peds (`AnimTagOf == WalkAnimTag`) — a
  ped WAITING at the kerb (paused, standing just inside the buffered polygon edge) raises no gate, so no
  phantom stop; a walking ped (incl. clearance-interval) still stops turning cars. `CrosswalkSignals.
  IsSignalizedLane` + `CrossingTlReader.LoadCrossingLinks` (one-pass net read; the per-crossing reload was
  prohibitive on the 1.5 MB box net).
- **P2b-T4 — wire into BuildLiveCity + verify**: DONE (committed 107407b). Verified A/B (`LIVECITY_YIELD`
  env, same seed): baseline peds-on-red **1400**/2063, jaywalk-into-car near-collisions **30**; full (2b on)
  peds-on-red **40**/1969, jaywalk-into-car near-collisions **3**. **The reported bug (ped crossing on red
  while cars have green) is essentially eliminated.** Two runs byte-identical; full gate green
  (ParityTests 654, Pedestrians 227, DotRecast 2, Host 1); vehicle cost unchanged (~4.2 ms/step).

## Phase 2b status: COMPLETE (T1–T4). Known-deferred residual + a tick defect found
- **Residual (deferred, pre-existing):** ~54 ped-on-**green** near-collisions remain = turning cars on
  permissive movements + **coarse-tick tunneling** (a 13 m/s car leaps ~13 m per 1 s engine step, straight
  past the 4 m-deep crosswalk, so a point-disc gate at the ped can't brake it in time). This is exactly the
  **tunneling-proof stop-line** the Phase-2 design deferred (needs the crossing→crossed-lane mapping + a
  virtual stopped leader placed BEFORE the crosswalk). Not introduced by 2b — Phase 2 had it too.
- **Tick defect found (not yet fixed):** `BuildLiveCity` loops at `Dt=0.5` but `engine.Step()` advances a
  fixed `StepLength=1.0` (the box `scenario.sumocfg` / `DefaultNetworkConfig`), so **cars advance 1.0 s of
  motion per 0.5 s frame — they render ~2× too fast**, and the 1 s car step is itself the tunneling driver.
  The engine's step length isn't settable without a sumocfg that pins it. Options for a follow-up: (a) align
  `Dt=1.0` (correct speed, choppier, tunneling persists); (b) a finer engine step (needs a sub-1 s sumocfg
  or an Engine step-length setter); (c) the stop-line (fixes tunneling regardless of tick). **Ask the owner
  which** before spending on it — it's beyond the P2b (compliance) ask.
- Diagnostics (printed by `--live-city`): crossing-class split; near-collision metric (car within 2.5 m of
  a walking crossing ped, split ped-red/green); ped-on-signalized-during-red compliance count; `LIVECITY_YIELD=0`
  A/B baseline.
- Test: `tests/Sim.Pedestrians.Tests/Crossing/CrosswalkSignalComplianceTests.cs` (3, on POC-0): signals-ON →
  0 on-red; signals-OFF → 3328 on-red; byte-identical double-run.

## P2b-T2 plan (resume here) + the code facts already gathered
Goal: a low-power ped waits at the kerb until its walk phase, then crosses. Behind an opt-in flag
(default off → every committed ped test byte-identical).

1. **New `CrosswalkSignals` provider** (`src/Sim.Pedestrians/Crossing/CrosswalkSignals.cs`): wraps a
   `CrosswalkSignalSchedule` + the **signalized** crossing polygons. API:
   `bool TryLocate(Vec2 p, out string crossingId)` (bbox + point-in-polygon over signalized crossings);
   `double NextWalkStart(string id, double tArrive, double speed)` (computes crossTime = crossLen/speed,
   where crossLen ≈ the crossing polygon's bbox diagonal, an over-estimate → ped waits conservatively);
   `int SignalizedCount`; `bool IsSignalized(id)`; `static FromNet(netPath, IEnumerable<BakedPolygon>)`.
   A baked `Crossing` polygon's `.Id` **is** the crossing edge id (e.g. `:d_0_0_c0`) that
   `CrossingTlReader.FindCrossingLink` keys on — confirmed.
2. **`PedDemandConfig.CrosswalkSignals`** (new `CrosswalkSignals?`, default null → inert).
3. **`PedDemand.BuildLivelyTimeline`** (`src/Sim.Pedestrians/Demand/PedDemand.cs` ~line 272–355): after the
   existing pause/segment build, if signals != null, run a post-pass `InsertCrosswalkWaits(segments, now,
   speed, signals, makeWalk)`:
   - Track clock `t = now`. Every `ActivitySegment` has `.Duration`; `WalkSegment.Duration =
     PathArcMotion.PathLength(Path)/Speed` — so the clock is exact (matches ActivityTimeline's own timing).
   - For each `WalkSegment`, walk its `Path` vertex-by-vertex; when a sub-segment ENTERS a signalized
     crossing (`TryLocate(b)!=null && TryLocate(a)==null`): emit the walk-to-kerb (advance `t` by its
     Duration), emit `PauseSegment(NextWalkStart(id, t, speed) - t, "wait")` (advance `t`), start a fresh
     walk at the kerb. Non-walk segments pass through (`t += seg.Duration`).
   - The post-pass draws NO rng → deterministic order preserved. Waiting peds render yellow (pause tag
     != Walk → `KindPedPaused`) — you can SEE them wait at the kerb, then cross.
   - Test (`tests/…/Crossing/…`): a ped path across a POC-0 signalized crossing gets a Pause before the
     crossing and its on-crossing interval lies within a walk window; an unsignalized crossing gets none;
     signals==null → byte-identical timeline.

## P2b-T3 plan
`BuildLiveCity` passes only **unsignalized** crossing polygons to `CrossingOccupancySource` (filter via
`signals.IsSignalized(poly.Id)`), because signalized crossings are now handled by ped compliance (peds only
on them during walk = car red). Point-disc gate kept for unsignalized; the full tunneling-proof stop-line
(needs `crossingEdges`→lane mapping) is a later refinement — note if cars still clip peds at unsignalized.

## P2b-T4 plan (verify)
Build a `CrosswalkSignals` from the net + crop crossing polys in `BuildLiveCity`; pass into the
`PedDemandConfig`. Verify: (1) peds visibly wait (yellow) at signalized kerbs; (2) sample cars vs
ped-occupied crossings → ~zero cars traverse a crossing while a ped is on it; (3) traffic still flows;
(4) two runs byte-identical + full `dotnet test` green + hash unmoved.

## Key files
- Scene: `src/Sim.Viz/SceneGen.cs` (`BuildLiveCity` ~1806, `BuildDenseCity` ~1578, helpers `ReadDrivable-
  Edges`, `CropNetwork`); `src/Sim.Viz/Program.cs` (`RunLiveCity`).
- Ped: `src/Sim.Pedestrians/Crossing/` (`CrosswalkSignalSchedule`, `CrossingOccupancySource`,
  `CrossingTlReader`, `CrossingGate`), `src/Sim.Pedestrians/Demand/PedDemand.cs`,
  `src/Sim.Pedestrians/Lod/{PedLodManager,ActivityTimeline,PathArcMotion}.cs`.
- Engine seam: `src/Sim.Core/Bridge/{WorldDisc(ICrowdFootprintSource),CompositeFootprintSource}.cs`;
  `Engine.CrowdSource` (Engine.cs:716), `CrowdLongitudinalConstraint` (~7790, gated/inert for goldens).
- Sidewalk fix (landed on main): `Lane.AllowsRoadVehicle` (Sim.Ingest) excludes ped lanes from
  neighbors; `LanePayload.Ped` + template.js "concrete" shading.

## Run / verify
```
dotnet run -c Release --project src/Sim.Viz --no-build -- --live-city <out>.html   # prints diagnostics + vehicle cost
dotnet test Traffic.sln -c Release                                                  # full gate
```
Determinism check: generate twice, `cmp -s`. Current gate: ParityTests 654/+3, Pedestrians 224 (incl. the
P2b-T1 5), DotRecast 2, Host 1 (recount after each add).

## Vehicle realism pass (post-2b) — DONE
Owner reported cars looking wrong at junctions (sliding onto the sidewalk / "entering on red" / changing
lanes while stopped). Diagnosed with an env-gated raw dump (`LIVECITY_DUMP="x,y"`) + a vanilla-SUMO FCD
comparison. Findings + fixes (all committed; **parity byte-identical** — engine additions default to old
behaviour, ParityTests 654/+3 green; two demo runs byte-identical):
- **Not a sim error:** 0 cars ever on a ped lane; stopped cars hold at the stop line; only moving cars
  (green) cross. The on-screen "sidewalk drift / enter-on-red" was viewer **Catmull-Rom overshoot** of the
  engine's lane-centre snaps + the coarse tick.
- **Fix 1 — tick:** `Engine.LoadNetwork(net, ScenarioConfig)` overload; demo runs step-length **0.5** (== ped/
  frame Dt) via `ScenarioConfigParser.ParseXml`. Cars were ~2× too fast (engine default 1.0 s).
- **Fix 2 — de-oscillate:** `lanechange.duration 2.0` (commit-and-hold; kills per-step lane flip-flop). NOTE:
  the engine emits lane-**centre** positions (no sublane lateral), so a change is still a one-step lateral
  snap — duration only de-oscillates.
- **Fix 3 — viewer:** clamp vehicle interpolation to its two real endpoints' bbox (no overshoot onto the
  sidewalk) + keep FCD heading on lateral-dominated steps (no sideways yaw). `src/Sim.Viz/template.js`.
- **Root over-production (owner's insight, DATA-CONFIRMED):** vanilla SUMO on the box net = 156 lateral
  changes/200s, **12%** at <0.1 m/s (median 13 m/s). Demo was 813/120s, **51%** at a dead stop. Fixes:
  - **A — `departLane="best"`**: `SpawnVehicle(..., departBestLane:true)` (exposes `ResolveBestDepartLane`);
    cars enter on the route-continuing lane, not always leftmost. (Alone: 813→731 — mid-route sorting
    remained.)
  - **`Engine.LaneChangeMinSpeed`** (0 = off = **parity-identical**; demo sets **1.0 m/s**): a car may not
    INITIATE a discrete change, and an in-progress maneuver is HELD, while below the threshold — so it never
    snaps sideways at a standstill; it sorts while moving (mirrors SUMO sublane `maxSpeedLatStanding=0`).
  - **Result:** 813→**264** changes, dead-stop snaps 412→**84** (−80%); residual = cars finishing a change
    mid-brake (diagonal, not a dead-stop teleport). `--live-city` prints the lane-change realism metric.
- **Still deferred (crossing-yield residual, unchanged by this pass):** ~80 ped-on-**green** near-collisions
  = turning cars + tunneling (13 m/s car > 4 m crosswalk per step); needs the tunneling-proof stop-line or a
  finer engine step. ped-on-RED near-collisions = **0** (2b compliance holds).
- **Open lever if owner wants fewer still:** raise `LaneChangeMinSpeed` (2.0) or the full **sublane model**
  (continuous lateral posLat) — the only thing that makes a lane change a genuinely smooth slide. Big port.

## KNOWN BLOCKER — cars OVERLAP in dense multi-lane traffic (pre-existing engine limitation)
Owner saw cars merging into an occupied lane / sitting on top of each other at busy junctions. Diagnosed
with a whole-crop overlap detector (`LIVECITY_DUMP="x,y,radius"`, then count same-lane car pairs <5.5 m
apart): **3360+ overlap events** in a 120 s run, ~3200 with both cars stopped, many at 0.0 m gap. Proven
example: veh18 stopped on lane `_1`; veh49 changes `_2→_1` and lands at the **exact same (x,y)** as veh18.
- **Pre-existing & not from this session's work:** overlaps are ~identical with `LaneChangeMinSpeed=0` AND
  `lanechange.duration=0` (the original settings). Confirmed A/B.
- **Root cause is DOCUMENTED and DELIBERATE** (`docs/HIGH-DENSITY-P2G-DESIGN.md` §7): the engine's
  lane-change safety applies only the **leader**-gap veto, NOT the **follower**-gap veto, because a
  follower block without SUMO's **cooperative lane-changing** (`MSLCM_LC2013::informBlocker` /
  `saveBlockerLength` — the follower slows to make room) over-brakes a saturated grid into gridlock
  (measured: both-halves veto regressed a saturation test 0→30 stuck). So leader-only was chosen and the
  residual (a change SUMO would block on the follower) was "accepted behaviourally per the owner steer."
  In the live-city's **saturated random multi-lane flow** that residual shows up as visible overlaps.
- **Density scales it** (LIVECITY_CARS): 110→3493, 60→1487, 30→903 overlaps — helps but does NOT eliminate.
- **The real fix = the §7 follow-up: port SUMO cooperative lane-changing** (follower-gap veto + the
  follower making room). Substantial, parity-sensitive engine feature → **design-first task**, not an
  ad-hoc patch. This is what gates a *serious* high-density multi-lane live-city demo.
- **HANDOFF PACKAGE WRITTEN** → `docs/LANE-CHANGE-OVERLAP-SPEC.md` (self-contained: repro on the committed
  `scenarios/_diag/willpass-saturation` = SumoSharp ~197–258 overlaps vs vanilla SUMO **0**; exact engine
  seams; the SUMO `checkChange`+`informFollower`/`saveBlockerLength` algorithm to port; the "both-halves
  veto without cooperation gridlocks 0→30 stuck" trap; 4-stage plan; acceptance criteria). Acceptance
  harness committed + skipped: `tests/Sim.ParityTests/LaneChangeOverlapDiagTests.cs` (unskip → assert 0).
  **Hand this spec to a fresh focused session.**
- **Interim options for a presentable demo now:** (a) lower `LIVECITY_CARS` (e.g. 40–60) — fewer but still
  some overlaps; (b) restrict the car flow to single-lane corridors (no lane changes → no overlaps);
  (c) keep it as a moderate-density showcase until the cooperative-LC port lands.
- Tuning knobs added to `BuildLiveCity` (env, default = production): `LIVECITY_CARS` (concurrent car cap),
  `LIVECITY_LCMIN` (LaneChangeMinSpeed), `LIVECITY_YIELD` (crossing-yield A/B), `LIVECITY_DUMP="x,y[,r]"`.

## Phase 3 (later): City3D combined cars+peds + semantic (enter buildings, dine at terraces, meet). Data is
in the box (buildings.json, entrances, venue table_cluster/service_door). Not started.
