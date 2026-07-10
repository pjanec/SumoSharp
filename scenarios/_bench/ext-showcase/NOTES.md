# ext-showcase — five external-agent reactions in one combined replay

A single `Sim.ExtDemo` run in which SUMO cars exhibit **five** distinct external-agent
reactions (EXTERNAL-AGENTS-VIZ.md): **stop**, **within-lane swerve**, **spill to the adjacent
lane**, **follow**, and **junction-yield** — each staged against its own agent, each verified
from the combined FCD below. No golden here (same "no golden" status as `14-external-obstacle`
/ `ext-agents-demo` / `ext-swerve-demo`): external agents are never in any SUMO run.

## Net

A priority junction `J` with a 2-lane major road `WJ -> JE` (hosts the stop/swerve/spill/follow
demos, ~793 m) and a 1-lane minor road `SJ -> JN` (hosts the junction-yield demo). Built via
`netconvert` from `nodes.nod.xml`/`edges.edg.xml` (see `provenance.txt`). The major road runs
**vertically** and the minor road **horizontally** — a 90-degree transpose of the "obvious"
horizontal-major-road layout — purely so the net's ~400x1000 m portrait footprint fills a phone's
portrait viewport under `Sim.Viz`'s fit-to-view camera (which fits the *smaller* of width/height
ratio; a 1000x400 landscape net collapses to a thin horizontal strip on a 390x844 canvas). The
rotation does not change any lane length, the junction's `<request>` matrix, or any calibrated
`pos` (arc-length along a lane) — only which world axis is "lateral" for a given lane.

Junction `J`'s internal lanes: `:J_0_0` (WJ->JN, right), `:J_1_0`/`:J_1_1` (WJ->JE straight,
lanes 0/1 — the major through movement), `:J_3_0` (SJ->JN, straight — vMinor's own link, request
index 3), `:J_4_0` (SJ->JE, left). Request index 3's row `response="00111"` responds to links
0/1/2 (`:J_0_0`, `:J_1_0`, `:J_1_1`) — i.e. vMinor yields to anything on the major road's
approach to the junction.

## Demand (`rou.rou.xml`)

Five `passenger` cars (`sigma="0"`, deterministic). The four major-road demo cars are staged
**sequentially** (`car_stop` depart 0, `car_swerve` depart 35, `car_spill` depart 70,
`car_follow` depart 105), each solo on the 2-lane approach when its own agent is active. This
sidesteps an engine behavior observed during calibration: two `sigma=0` cars with *identical*
departure profiles, simultaneously free-flowing on a 2-lane edge, can have the following one's
keep-right lane-change land it at the exact same `pos` as the leading car the instant it merges
lanes (both cars' unconstrained trajectories are literally identical, so the merge's gap check
sees zero relative speed and accepts a zero gap) — not a case this showcase needs to exercise,
so it's simply avoided by never having two free-flowing major-road cars on the road at once.
`vMinor` (minor route, depart 0) runs on the physically separate `SJ`/`JN` edges the whole time,
so it never interacts with the major-road cars regardless of timing.

## External agents (`external-agents.json`) and calibration

Every agent's `startPos`/`startTime` was calibrated against its target car's own free-flow
baseline — a throwaway `Sim.ExtDemo` pass with no `external-agents.json` (`--agents
/nonexistent.json`) — the same `calPos + 8m` idiom
`RungB6LateralEvasionTests.SuddenPedestrianFillsLane_CarSpillsIntoSafeAdjacentLane` uses. Because
all four major-road cars share an identical accel-to-cruise profile (`pos(t) = 13.89*(t-depart) -
30.45` once cruising, i.e. `t-depart=15 -> pos=177.90`, `t-depart=13 -> pos=150.12`, matching
`RungB6`'s own `OneLaneDir` calibration numbers almost exactly since the vType/road speed are the
same defaults), the same relative-timing recipe (`localElapsed=15`, `+8m ahead`) was reused for
both the swerve and spill agents.

| id | behavior | laneId | startPos | window | notes |
|---|---|---|---|---|---|
| `ped_stop` | STOP (B1) | `WJ_0` | 150.0 | `[8,16)` | full-lane block (`width 0`); car has a comfortable ~40 m gap when it appears (car @ pos 108.45 at t=8) |
| `ped_swerve` | SWERVE (B6) | `WJ_0` | 185.9 | `[50,60)` | partial (`width 0.8`, `latPos -1.2`, hugging the right edge); 8 m ahead of `car_swerve` (@177.90) at t=50, too close to stop |
| `ped_spill` | SPILL (B6) | `WJ_0` | 185.9 | `[85,100)` | lane-filling (`width 2.8` on the 3.2 m lane); 8 m ahead of `car_spill` (@177.90) at t=85, too close to stop, adjacent lane `WJ_1` clear |
| `slowcar1` | FOLLOW (B5-i) | `WJ_0` | 100.0 | `[100,170)` | moving external car, constant 3.0 m/s, appears 5 s before `car_follow` departs (t=105) |
| `crossingAgent` | YIELD (B5-iii) | `:J_1_0` | 5.0 | `(-inf,20)` | full-lane block on the major through movement's internal lane; forces `vMinor` to hold at its `SJ_0` stop line |

Each agent's active window ends well before the *next* demo's car reaches that same position
(e.g. `ped_stop` clears at t=16, long before `car_swerve`/`car_spill`/`car_follow` — departing at
35/70/105 — would reach pos 150), so the five reactions never interfere with each other.

### A small `Sim.ExtDemo` fix this showcase required

`slowcar1`'s window (`startTime=100.0`) exposed a latent bug in
`src/Sim.ExtDemo/ExternalAgent.cs`'s `FrontPosAt` (the **render-only** helper
`CombinedFcdObserver` uses to draw a moving external car): it dead-reckoned
`StartPos + Speed*time` unconditionally from `t=0`, instead of from `StartTime` like
`LatPosAt` (and the real engine's `Engine.AdvanceObstacles`) correctly do. Every prior demo's
moving car used `startTime<=0` (e.g. `ext-agents-demo`'s `slowcar1`, `startTime=0.0`), for which
the two formulas coincide, so the bug was invisible until this showcase needed a car that jumps
in mid-run. Fixed to mirror `LatPosAt`'s elapsed-since-`StartTime` clamp — a small additive fix
to the demo tool's own rendering, not `Sim.Core`; every existing fixture is unaffected
(`dotnet test` stays at 174 passed / 1 skipped).

## Verification (from the combined FCD, `engine.fcd.xml`)

Lane centre for `WJ_0` is world **x = 204.80** (the major road now runs +y, so *lateral*
deviation shows up in world x, not y); half-lane width 1.6 m. All values below are read directly
from the combined FCD.

**STOP** (`car_stop`): free-flow at 13.89 m/s, brakes starting t=12, **fully stopped (speed
0.00) at pos 146.90 from t=14-16** (while `ped_stop` is active), resumes at t=17 (speed 2.60),
back to free-flow (13.89) by t=22. Lateral deviation **0.00 m the whole run** — a pure stop, no
swerve. No collision (checked against `ped_stop`'s full-lane footprint over its active window).

**SWERVE** (`car_swerve`): cruising 13.89 until t=50 (pos 177.90); ped appears 8 m ahead and the
car brakes to 4.89 m/s while swerving — **max |lateral deviation| = 0.60 m**, within the
0.3-0.7 m within-lane-swerve band and well inside the 1.6 m half-lane-width (it never leaves
`WJ_0`). Never stops (min speed 4.89 m/s > 0); recenters and is back to 13.89 m/s by t=55. No
collision (verified in the engine's own lateral frame — the car's footprint and the pedestrian's
`[-1.6,-0.8]` footprint are disjoint throughout the longitudinal-overlap window).

**SPILL** (`car_spill`): cruising 13.89 until t=85 (pos 177.90); the lane-filling ped appears 8 m
ahead and the car brakes hard (down to 0.11 m/s at t=87 — nearly stopped, but never exactly 0)
while spilling sideways — **max |lateral deviation| = 2.80 m**, clearly past the 1.6 m
half-lane-width threshold (it crosses fully into `WJ_1`, whose centre is 3.2 m from `WJ_0`'s).
Passes the pedestrian while still moving (pos 190.92 > ped front + car length, speed 5.31 m/s at
t=89) and is back to 13.89 m/s by t=93. No collision (verified the same way as SWERVE).

**FOLLOW** (`car_follow` vs. `slowcar1`): `car_follow` departs at rest at t=105, accelerates,
then brakes into the external car ahead and **settles at exactly 3.00 m/s from t=121 through
t=169** (every recorded step in that window is 3.00 m/s — the Krauss constant-speed-leader
equilibrium, matching `slowcar1`'s own constant speed). Minimum gap between `slowcar1`'s back and
`car_follow`'s front over the whole run: **5.50 m** (no collision).

**JUNCTION YIELD** (`vMinor` vs. `crossingAgent`): baseline (no agent) crosses into `:J_3_0` by
t=18 and reaches `JN_0` by t=19. With `crossingAgent` active on `:J_1_0`, `vMinor` instead
**halts (speed 0.00) at pos 195.90 on `SJ_0` from t=19 through t=20** (near its stop line — the
lane is 196.00 m long), held for as long as the agent occupies the foe lane. Once the agent
deactivates (t=20), `vMinor` **resumes at t=21** (speed 2.60) and **crosses into `JN_0` by t=23**
— a genuine ~5 s externally-forced hold vs. the baseline's clean t=18/19 crossing. **Fired
correctly** — not faked; this is the real `JunctionYieldConstraint`/B5-iii reaction.

## Reproduce

```
dotnet run --project src/Sim.ExtDemo -- scenarios/_bench/ext-showcase
dotnet run --project src/Sim.Viz -- scenarios/_bench/ext-showcase --fcd scenarios/_bench/ext-showcase/engine.fcd.xml
```

`engine.fcd.xml`/`replay.html` are regenerated on demand, gitignored (same pattern as
`ext-agents-demo`/`ext-swerve-demo`). Headless-Chromium sanity check (Playwright, 390x844 @
dpr 3): no `pageerror`, all 5 SUMO cars + all 5 external agents (`ext_pedestrian_*` /
`ext_car_*`) present in `REPLAY_DATA.vehicles` (10 total), and the fit-to-view camera fills the
portrait canvas (the rotated net's ~400x1000 m footprint spans most of the viewport height,
unlike an unrotated landscape layout which would collapse to a thin strip).
