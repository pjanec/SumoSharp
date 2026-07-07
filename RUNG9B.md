# RUNG9B.md — Cold-start briefing for priority-junction yielding

This is a self-contained plan for **rung 9b: unsignalized priority right-of-way (junction
yielding)** — the one deliberately-deferred rung. Read this fully, then `CLAUDE.md`, `DESIGN.md`,
and `TASKS.md` (rung 9 entry) before acting. It exists so a fresh session can pick 9b up cold
without the original chat history.

## Status going in

- Rungs 1–8, 9a, 10, 11 are committed **green** (`dotnet test` passes with no SUMO, no network).
  The engine faithfully matches SUMO on: Krauss free-flow, car-following (`maximumSafeFollowSpeed`),
  stops (`maximumSafeStopSpeedEuler`), gap-gated insertion, platoons, keep-right LC2013 + the
  command buffer, **multi-edge/internal-lane junction traversal (9a)**, and **traffic lights (10)**.
- 9b is the **only** missing piece. It is NOT started — no partial code is committed. There is no
  failing/skipped test; the gap is "capability absent," not "capability broken."
- 9b was investigated deeply once (that investigation produced this doc + the `TASKS.md` rung-9
  entry). The mechanism and geometry are reverse-engineered below; the blockers are known.

## What 9b delivers, and why it matters

Unsignalized right-of-way: a minor-road vehicle slows/yields for conflicting priority-road traffic
at a junction with no traffic light. This one subsystem unlocks a whole family — priority/give-way
junctions, right-before-left, stop signs, roundabouts, and on-ramp merges — all of which use the
same `MSLink` foe machinery. Until it exists, do **not** trust the engine on any network with
conflicting flows through an uncontrolled intersection (the minor won't yield → collision / non-
physical result, not just a parity miss).

## Why 9b is hard (so you don't underestimate it)

Unlike every prior rung, 9b does **not** reduce to a formula you can extract via TraCI and port:
1. **Inputs live inside netconvert, not the data.** The yield speed depends on the junction
   *conflict geometry* (`MSLink::myConflicts`: crossing points, `lengthBehindCrossing` per lane,
   conflict widths, `sagitta` for curves). SUMO computes these in `NBNode` at network-build time and
   holds them in memory — the `.net.xml` does **not** serialize them. You must recompute them from
   the internal-lane shapes/widths (the crossing point itself is derivable; widths/sagitta are more
   work). This is preprocessing DESIGN.md deliberately chose not to port, so it drags in new work.
2. **It's a two-vehicle negotiation, not a one-way read.** The major *publishes intent* via
   `MSLink::setApproaching` (arrivalTime / leaveTime / `willPass`); the minor *reads the major's
   registration* via `getApproaching` to decide. This needs a NEW coordination pass between plan and
   execute — the current clean two-phase loop (plan reads a frozen snapshot, writes only own intent;
   execute applies) has no phase where vehicles see each other's *plans*.
3. **No small formula.** `MSLink::getLeaderInfo` is ~396 lines of special cases (contLane,
   sameTarget/sameSource, inTheWay, pastTheCrossingPoint, indirect turns, opposite, `willPass`, …).
   The yield speed is the resultant of the whole apparatus. Hand-derivation gave 9.39 (via
   `followSpeed(0)`) or 11.57 (via the stop branch); the golden is **9.433** — proof there is no
   shortcut.
4. **Order-sensitivity / determinism policy (a design decision, not just code).** SUMO junction
   resolution can depend on processing order. Exact parity may require replicating SUMO's iteration
   order, which fights the ECS-parallel design. **Decide explicitly at the start of 9b:**
   "match SUMO trajectory-for-trajectory" vs. "be deterministic and plausible with a tie-break by
   stable id" (DESIGN.md "parallelization" / "parallelization policy"). Record the choice in the
   scenario notes. You probably cannot have both.

## The reference scenario (rebuild it; it is NOT committed)

Only `scenarios/08-junction-straight/` (major vehicle only, no conflict) is committed. Recreate the
CONFLICT scenario as `scenarios/10-priority-junction/` (next free number) and regenerate its golden.

Network (`netconvert` from these; J is a `priority` junction, major = higher edge priority):

`nodes` (`*.nod.xml`):
```
<nodes>
  <node id="W" x="-200" y="0"/>  <node id="E" x="200" y="0"/>
  <node id="S" x="0" y="-200"/>  <node id="N" x="0" y="200"/>
  <node id="J" x="0" y="0" type="priority"/>
</nodes>
```
`edges` (`*.edg.xml`):
```
<edges>
  <edge id="WJ" from="W" to="J" numLanes="1" speed="13.89" priority="10"/>
  <edge id="JE" from="J" to="E" numLanes="1" speed="13.89" priority="10"/>
  <edge id="SJ" from="S" to="J" numLanes="1" speed="13.89" priority="1"/>
  <edge id="JN" from="J" to="N" numLanes="1" speed="13.89" priority="1"/>
</edges>
```
Build: `netconvert --node-files n.nod.xml --edge-files e.edg.xml -o net.net.xml --no-turnarounds true`.

Route/demand (`*.rou.xml`): major `WJ JE` and minor `SJ JN`, both `<vType passenger sigma="0">`,
both `depart="0" departPos="0" departSpeed="0"` (symmetric — this forces the minor to yield). Add
`<collision.action value="none"/>` to the cfg (as rung 4/7 do) so SUMO won't teleport on any
transient overlap. Generate the golden with the committed pipeline:
`scripts/regen-goldens.sh` (or the per-scenario `sumo -c ... --fcd-output ... --precision 6 ...`
command each `provenance.txt` records) and `scripts/dump-scenario-vtypes.py` for `golden.vtype.json`.

**Target behavior to reproduce (from the committed investigation):** major cruises through at 13.89;
minor brakes **13.89 → 9.433 → 4.933 → 2.033** at t16–18 as the major approaches, enters the junction
just behind it (t18), then accelerates through. All at ≤ max decel until the release.

## The mechanism (reverse-engineered — port faithfully, verify against golden)

The minor yields by treating the crossing major as a virtual **link leader**:
`MSVehicle::checkLinkLeader` → `MSVehicle::adaptToJunctionLeader` (`sumo/src/microsim/MSVehicle.cpp`).
For a crossing foe with `gap = leaderInfo.second >= 0`:
```
vsafeLeader = followSpeed(gap, leaderSpeed, leaderApparentDecel)          // follow the foe
vStop       = stopSpeed(distToCrossing - minGap)                          // stop at the crossing
leaderDistToCrossing = distToCrossing - leaderInfo.second
leaderPastCPTime     = leaderDistToCrossing / max(leaderSpeed, SUMO_const_haltingSpeed)
vFinal = max(speed, 2*(distToCrossing - minGap)/leaderPastCPTime - speed)
v2     = speed + ACCEL2SPEED((vFinal - speed)/leaderPastCPTime)
vsafeLeader = max(vsafeLeader, min(v2, vStop))
v = min(v, vsafeLeader)                                                    // feeds the reducer
```
Geometry for the reference net: internal lanes cross at **(201.60, 198.40)** — `:J_1_0` (minor,
vertical at x=201.60) meets `:J_2_0` (major, horizontal at y=198.40), **5.60 m into each** (both
internal lanes are length 11.20). So `distToCrossing = 201.60 - pos` for both vehicles. The engine
already has `followSpeed`/`stopSpeed` (`KraussModel`); the missing inputs are `gap` and the foe set.

## Recommended decomposition (each its own review-gated step)

- **9b-i — junction data ingest + conflict geometry (green-able alone).** Parse each `<junction>`'s
  `<request index= response= foes= cont=>` matrix and map link indices to connections (the
  `<connection ... linkIndex=>` already gives the index; `tl` is null for a priority junction).
  Compute, for each pair of conflicting internal lanes, the crossing point and `lengthBehindCrossing`
  (per lane) from the internal-lane shapes. Reference: `MSLink::myConflicts` /
  `MSLink::getLeaderInfo` uses `myConflicts[i].getLengthBehindCrossing(this)` and
  `getFoeLengthBehindCrossing(foeExitLink)`; the build-time source is `NBNode`/`NBRequest` (not
  vendored-critical — you can compute crossings directly from the committed `.net.xml` internal-lane
  shapes). Validate this step by a unit test asserting the computed crossing point / behind-crossing
  distances for the reference net (e.g. crossing at 5.60 m into each of `:J_1_0`, `:J_2_0`).
- **9b-ii — approaching-vehicle registration pass (new infrastructure).** Add a pass where each
  vehicle, in plan, computes its arrival/leave time at each junction link on its path
  (`getArrivalTime`/`setApproaching` in `MSVehicle.cpp`/`MSLink.cpp`) and publishes it; the reducer's
  junction constraint then reads foes' registrations. This is the two-vehicle-coupled part — keep it
  faithful to the plan/execute contract by publishing from a frozen start-of-step snapshot (like the
  `LaneNeighborQuery`, but cross-lane and keyed by link/junction). Decide the **determinism policy**
  here.
- **9b-iii — foe gap + adaptToJunctionLeader.** Port the single-crossing-conflict path of
  `MSLink::getLeaderInfo` (the gap `leaderInfo.second` + `distToCrossing`) and
  `MSVehicle::adaptToJunctionLeader` (formula above), wiring the result into the existing
  multi-constraint reducer (DESIGN.md seam 1 — the junction constraint is just one more source).
  Priority (major is the leader) comes from the response/foe matrix + `MSLink::isLeader` /
  junction entry times. Iterate against the golden until `9.433/4.933/2.033` match to 1e-3.

## De-risking notes (learned the hard way)

- The intermediate junction quantities (`gap`, `distToCrossing`, `arrivalTime`, conflict geometry)
  are **not** exposed via TraCI getters the way keep-right's `keepRightP` was. So the "instrument via
  TraCI, read the exact number" trick that cracked rungs 8b/10 does not directly work here. Options:
  (a) reimplement + compare the resulting FCD speeds against the golden (slower iteration); (b) build
  SUMO from the vendored `/sumo/` source with the `DEBUG_PLAN_MOVE_LEADERINFO` / `DEBUG_PLAN_MOVE`
  `#define`s enabled (they print `gap`, `distToCrossing`, `leaderPastCPTime`, `vFinal`, `v2`, `vStop`,
  `vsafeLeader` per step) to get SUMO's exact intermediates — this is the fastest path to nailing the
  gap convention, and is worth the one-time build cost.
- Keep the existing 28 tests green throughout: the junction constraint must be inert (+inf) for any
  vehicle not approaching a junction with a foe, so all prior rungs are unaffected (same guard
  pattern as rung 8b's right-neighbor guard and rung 10's no-TL-connection guard).
- Gate each of 9b-i/ii/iii through `parity-reviewer` and commit green before the next, exactly as the
  rest of the ladder was built.

## Done-condition

`scenarios/10-priority-junction/` committed with golden; `Rung9bParityTests` asserts `IsMatch`
(the minor's `9.433/4.933/2.033` yield profile + both vehicles' presence/lane sequence) within
`tolerance.json` (exact, `[lane,pos,speed]`, 1e-3); `dotnet test` green with all priors; the
determinism policy recorded in the scenario notes and DESIGN.md. Gate through parity-reviewer.
