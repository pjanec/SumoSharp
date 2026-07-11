# RUNGA2.md — Cold-start briefing for A2 (overtaking / speed-gain lane change)

> **📄 Reference documentation — not an active task list.** A2 (speed-gain / overtaking lane change)
> has **landed** (engine port DONE, see the "Status update" section below). This file is kept as the
> design & decision record for that rung. For work that is still open, see `TASKS.md`.

Self-contained plan for **A2: speed-gain (overtaking) lane change** — the LC2013 branch rung 8b
(keep-right) did NOT port. Read `CLAUDE.md`, `DESIGN.md`, `TASKS.md` (Group A / A2), and the rung-8b
notes (`ComputeKeepRightDecision` in `src/Sim.Core/Engine.cs`) first. Mirrors `RUNG9B.md`'s role.

## Status: scenario + goldens committed; engine port NOT done (one open subtlety, below)

- **`A2 [net]` DONE.** `scenarios/12-overtake/` committed with the SUMO golden: a fast follower
  (passenger default) overtakes a slow leader (`maxSpeed=5`) on a 2-lane edge. It changes **left at
  t11→t12** (speed dips to the CF speed 12.253 during the change step), passes, then keep-right
  returns it **right at t19→t20**. `lanechange.duration=0`, `collision.action=none`, `sigma=0`,
  `dotnet test = 43` green (adds 2 vType cross-check cases; NO trajectory test yet — that is the
  target for the engine port).
- **Engine port NOT started.** No `RungA2ParityTests` yet.

## What A2 delivers
The overtaking half of `MSLCM_LC2013::_wantsChange`: a vehicle held up by a slower leader accumulates
`mySpeedGainProbability` and changes **left**; keep-right (rung 8b, already ported) brings it back
after passing. First LC rung with the maneuver driven by a *leader*, not an empty road.

## Reverse-engineered so far (TraCI accumulator dump — the rung-8b de-risking method)
Dumped via `vehicle.getParameter(v, "laneChangeModel.speedGainProbabilityLeft" / ".keepRightProbability")`
(keys exposed at `MSLCM_LC2013.cpp:2115-2120`; prefix `laneChangeModel.`). For the follower:
```
t   lane  pos      speed    sgpLeft   keepRightP(=-myKeepRightProbability)
..  e0_0  ...      13.890   0.00      0.00
    e0_0  122.340  13.890   0.12      0.00     <- one step before the left change
    e0_1  134.593  12.253   0.00      0.00     <- changed left; accumulator reset to 0
    e0_1  ...      13.890   0.00      0.40 .. 2.00  (t16..t20, keep-right builds up)
    e0_0  245.713  13.890   0.00      0.00     <- returned right; reset
```
- **speed-gain (the NEW port):** `mySpeedGainProbability += actionStepLength * relativeGain` when the
  neighbor (left) lane is "better" (`MSLCM_LC2013.cpp:1829-1831`); change fires when
  `mySpeedGainProbability > myChangeProbThresholdLeft` (`=0.2/mySpeedGainParam = 0.2`), AND
  `relativeGain > NUMERICAL_EPS`, AND `neighDist/max(.1,speed) > 20` (`:1857-1859`); on a successful
  change it resets to 0 (`:1063/1080`). There is a `ceil(x*1e5)*1e-5` normalization (`:1020`).
- **relativeGain** (`:1682`): `(neighLaneVSafe − thisLaneVSafe) / MAX2(neighLaneVSafe, RELGAIN_NORMALIZATION_MIN_SPEED=10)`.
  - `neighLaneVSafe = MIN2(neighVMax, anticipateFollowSpeed(neighLead, neighDist, neighVMax, …))`
    (`:1548`). Left lane EMPTY here → `neighLead=null` → `neighLaneVSafe = neighVMax = 13.89`.
  - `thisLaneVSafe = MIN2(vMax, anticipateFollowSpeed(leader, currentDist, vMax, …))` (`:1549`).
    `anticipateFollowSpeed` (`:1893`) with a non-accelerating leader and `mySpeedGainLookahead=0`
    reduces to `MIN2(vMax, maximumSafeFollowSpeed(gap, egoSpeed, leaderSpeed, leaderDecel,
    onInsertion=true))` — **the engine already has `KraussModel.MaximumSafeFollowSpeed(..., onInsertion:true)`.**
  - CONFIRMED: with the follower at pos 122.34, slow leader at 152.60 (gap 22.76), `maximumSafeFollowSpeed`
    = **12.253** (equals the golden's speed dip) → `thisLaneVSafe = 12.253`, `relativeGain =
    (13.89−12.253)/13.89 = 0.1178 ≈ 0.12`. This is the `sgpLeft=0.12` seen in the dump.
- **keep-right return:** reuses the rung-8b block; the dump shows `myKeepRightProbability` reaching
  `−2.0` and firing the right change (`< −myChangeProbThresholdRight = −2.0`). VERIFY the existing
  `ComputeKeepRightDecision` reproduces this once the follower is placed on `e0_1` by the (new) left
  change — the slow leader is now BEHIND on `e0_0`, so `neighDist`/fullSpeedGap (a look-AHEAD term)
  should be unaffected, but this interaction is untested until the left change works.

## THE OPEN SUBTLETY (what still blocks exact parity)
The threshold is `0.2` and each accumulating step adds `≈0.1178`, so the change needs **~2** steps of
accumulation. But with the RAW car-following leader gap, `relativeGain` is 0 until the follower is at
pos 122.34 (gap 22.76) — only **ONE** step before the observed change. So the raw-CF gap under-counts
the accumulation. SUMO's `_wantsChange` does not use the raw CF gap: it uses the **`getBestLanes`
leader + a look-ahead** (`myLookAheadSpeed`/`laDist`, and the `leader`/`neighLead` pairs are built with
a lookahead in `informedSpeed`/`getBestLanes`), so `relativeGain` becomes positive a step or two
EARLIER and the accumulator crosses 0.2 exactly at the observed step. **To close A2 you must pin the
LC leader-gap/look-ahead convention** — either:
  (a) read `MSLCM_LC2013::_wantsChange`'s leader/neighLead construction + `myLookAheadSpeed`/`laDist`
      (search `getBestLanes`, `getLeaders`, `informedSpeed`, `myLookAheadSpeed` in `MSLCM_LC2013.cpp`
      / `MSLane.cpp`) and reproduce the exact gap fed to `anticipateFollowSpeed`; OR
  (b) build SUMO with `#define DEBUG_WANTS_CHANGE` (`MSLCM_LC2013.cpp`) to print
      `mySpeedGainProbability / thisLaneVSafe / neighLaneVSafe / relativeGain` per step — the fastest
      way to nail the per-step accumulation. NOTE the vendored `sumo/` tree is source-only (no build
      system, per RUNG9B.md); the debug build needs a full `eclipse-sumo@v1_20_0` clone +
      `libxerces-c-dev`. (The pip `sumo` binary is a release build with the prints compiled out.)

## Decomposition when finishing (each review-gated, commit green)
- **A2-i — extend `LaneNeighborQuery`** to return the adjacent lane's **leader AND follower**
  (`GetNeighborLeader`/`GetNeighborFollower(ego, targetLaneIndex)`), from the same frozen
  start-of-step snapshot. Needed by both `relativeGain` (neighLead) and the safety veto (neighFollow).
- **A2-ii — speed-gain decision** in `ComputeMoveIntent` alongside `ComputeKeepRightDecision`: add
  `SpeedGainProbability` to `VehicleRuntime` + `MoveIntent` (accumulator, written back in
  `ExecuteMoves` like `KeepRightProbability`); compute `thisLaneVSafe`/`neighLaneVSafe`/`relativeGain`
  with the CORRECT LC gap (the open item above); accumulate + threshold-check; on fire set
  `TargetLaneId = leftLane`. Keep it inert (no left lane, or left lane not faster → no-op) so prior
  rungs are byte-identical.
- **A2-iii — target-lane safety veto** (`checkChangeBeforeCommitting`/`blocked`): with the EMPTY
  target lane in this scenario the veto is non-binding (neighLead/neighFollow null → safe), so a
  faithful-but-minimal veto (null neighbors → allowed; else secure-gap check) suffices here; the full
  blocker-gap veto wants its own scenario WITH traffic on the target lane (a later rung).
- Verify the keep-right RETURN still fires (reuses rung 8b) once the left change places the follower on
  `e0_1`. Target: `RungA2ParityTests` `IsMatch` on `scenarios/12-overtake/golden.fcd.xml` (exact,
  `[lane,pos,speed]`, 1e-3): left change at t11→t12, right return at t19→t20, speeds exact.

## Determinism
Same policy as the other rungs: decide from a frozen start-of-step neighbor snapshot; the LC accumulator
is per-vehicle state advanced only in `ExecuteMoves`. No `System.Random` (sigma=0).

## Status update: engine port DONE (`dotnet test` = 44 green)

The open subtlety above (raw CF gap under-counting the accumulation) was resolved exactly as
option (a) suggested: `_wantsChange`'s `thisLaneVSafe`/`neighLaneVSafe` need the **POST-move**
leader gap, because real SUMO's `_wantsChange` runs once per vehicle from `MSLaneChanger`'s
post-`executeMovements` `changeLanes()` pass (`MSNet.cpp:784/790/796`), never from
`planMovements`. No look-ahead/`getBestLanes` machinery was needed -- a plain post-move gap
(`Engine.DecideSpeedGainChanges`, a brand-new phase run after `ExecuteMoves` in `Engine.Run`)
reproduces the golden's `t11 sgp=0.1179` / `t12` left-change-with-speed-dip-to-12.253 exactly,
matching `scratch-verify-a2.py`.

**Second finding (not anticipated by this file's "keep-right RETURN reuses rung 8b unchanged"
assumption, flagged above as "VERIFY... untested"):** it does NOT reuse it unchanged. The passed
slow leader is still briefly *ahead* of the follower on the (now-right) lane `e0_0` for a couple
of steps after the left change (`follow` doesn't actually pass `lead` in x-position until between
t14 and t15), which binds `MSLCM_LC2013.cpp:1743-1748`'s neighLead adjustment to keep-right's
`fullSpeedGap`/`fullSpeedDrivingSeconds` -- an adjustment that was correctly un-exercised (and
so, correctly unported) by every scenario up to and including rung 8b's own empty-right-lane
scenario. Porting that adjustment while still evaluating it in the pre-move Plan phase (as rung
8b originally did) fires the keep-right return ONE STEP LATE (t21 instead of t20) because Plan
sees the PRE-move neighbor gap; keeping it entirely unported fires TWO STEPS EARLY (t18) because
the adjustment silently never applies. Both were caught by diffing against `golden.fcd.xml`
(`lane` attribute only mismatched; `pos`/`speed` matched to float epsilon in both broken
versions, confirming the physics were right and only the keep-right FIRE TIMING was off).

The fix: **rung 8b's keep-right decision moved from the pre-move Plan/MoveIntent path into the
same post-move phase as speed-gain** (`Engine.ApplyKeepRightDecision`, called from
`DecideSpeedGainChanges` before the speed-gain-left check, against the SAME frozen post-move
`LaneNeighborQuery`). This is not a scope creep -- it is what real SUMO always did (`_wantsChange`
is one function covering both the keep-right and speed-gain sub-blocks, called once from the
post-move `changeLanes()` phase); rung 8b's original placement in Plan gave the right answer only
by coincidence, because every scenario through 07-keep-right-change had an empty right lane
(no `neighLead`-gap dependence, and every other term in the keep-right formula is position- and
pre/post-move-read independent). Verified this move does not perturb rung 8a/8b: `dotnet test`
stayed at 44/44 green (43 prior + `RungA2ParityTests`), with `Rung8aParityTests` and
`Rung8bParityTests` individually re-run and still passing byte-for-byte.

Files touched: `src/Sim.Core/LaneNeighborQuery.cs` (`GetNeighborLeader`/`GetNeighborFollower`),
`src/Sim.Core/VehicleRuntime.cs` (`SpeedGainProbability`), `src/Sim.Core/MoveIntent.cs`
(`TargetLaneId`/`KeepRightProbability` removed -- both lane-change decisions now write
`VehicleRuntime` directly from the post-move phase, not through `MoveIntent`), `src/Sim.Core/
Engine.cs` (`DecideSpeedGainChanges`, `ApplyKeepRightDecision`, `AnticipateFollowSpeed`,
`IsTargetLaneSafe`, `SecureGap`), `tests/Sim.ParityTests/RungA2ParityTests.cs` (new, 200-step run
against `scenarios/12-overtake/golden.fcd.xml`).
