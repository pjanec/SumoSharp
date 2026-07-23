# #15 — cooperative lane change (revive + extend informFollower) DESIGN

**Goal (owner):** eliminate the unrealistic pure-lateral "float" WITHOUT reintroducing gridlock, by making
lane sorting COOPERATIVE and mostly UP-FRONT. Two regimes:
- **Normal:** approaching a junction, a car that needs its turn lane signals; the target-lane FOLLOWER
  eases off (one gentle step of helpDecel) to open a gap; the car merges as a moving diagonal. No float.
- **Extreme:** a car that couldn't sort in time reaches the merge point, brakes/waits there (may briefly
  block its lane — realistic), and the follower cooperatively opens a gap; then it merges.

**Why this is the cure (measured context):** the demo's flow currently DEPENDS on an unrealistic
stopped keep-right swap that sorts cars into junction-compatible lanes while queued (docs
`LIVE-CITY-15-ATTEMPT-LOG.md` "FIX ATTEMPT 1"): removing it box-blocks (stuckInternal 0→42, arrivals
1025→458). Cooperative merging replaces that sort with a physical one, so the float can then be removed
without the box-block.

## Prior art to REVIVE (do not rebuild): git `afec614` retired the exact mechanism
Reverse-apply `afec614` for the CoopSpeedAdvice CHANNEL (verbatim, all parity-safe/inert-by-default):
- `VehicleRuntime.CoopSpeedAdvice` (double, `+Infinity` = none; MIN-composed).
- `CommandBuffer.SpeedAdvice(follower, speed)` + `Kind.SpeedAdvice` + `DoubleArg0`; Flush applies it as a
  commutative MIN into `follower.CoopSpeedAdvice` (order-independent → serial==parallel byte-identical).
- `ICommandBuffer.SpeedAdvice`.
- Consumption in `ComputeMoveIntent` (the removed block ~line 4696): `if (!+inf) { vPos =
  Min(vPos, CoopSpeedAdvice); if (!prePass) clear; }`. Reads on the willPass pre-pass but clears ONLY on
  the real pass.
- Gate `Engine.CooperativeInformFollower` (on top of the existing `Engine.CoordinatedLaneChange`).

## What to ADD (the retired version did SPEED-GAIN only; #15 needs STRATEGIC/turn-lane too)
1. **Strategic informFollower (NEW).** In `TryStrategicLaneChange` (`Engine.cs` ~11314), TODAY when the
   change is blocked it does a bare `return false`. When `CooperativeInformFollower` is on and the ONLY
   blocker is the target-lane FOLLOWER (`IsTargetLaneSafe(v, neighLead, null, dt)` passes but
   `IsTargetLaneSafe(v, null, neighFollow, dt)` fails) and ego is ahead of the follower (gap>0): write
   the SAME one-step helpDecel advice to `neighFollow` (`HELP_DECEL_FACTOR=0.5`;
   `adviceSpeed = max(0, followerSpeed - decel*0.5*dt)`), via `_commandBuffer.SpeedAdvice`, and still
   `return false` this step (ego waits; DeadLaneMergeBrakeConstraint already brakes it toward the merge
   point). Next step the gap has grown; retry — merge succeeds within a few steps. This is the up-front
   fluent sort AND the extreme stop-and-wait, same code (ego brakes if it runs out of road).
2. **Speed-gain informFollower (revive verbatim)** in `DecideSpeedGainChanges` — restore the removed
   `else if` block exactly.
3. **Then remove the float (part B):** re-apply the keep-right moving-only guard (suppress the inline
   keep-right swap below `LaneChangeMinSpeed`) — SAFE now because cooperative strategic merging sorts cars
   up-front, so the box-block the guard exposed before must not return.

## Wiring (demo-only, parity-safe)
- `LiveCityConfig.CooperativeLaneChange = true` (new; env `LIVECITY_COOP`), sets both
  `_engine.CoordinatedLaneChange` and `_engine.CooperativeInformFollower`. NB check whether the demo
  already sets `CoordinatedLaneChange`; the informFollower is inert unless it is on.
- Every parity/bench golden leaves both engine flags false ⇒ no `SpeedAdvice` is ever written ⇒
  `CoopSpeedAdvice` stays `+Infinity` ⇒ the consumption is a data no-op ⇒ byte-identical.

## Determinism / parity
- The advice is written in the LC phase and consumed as a `vPos` MIN the NEXT step; MIN is commutative so
  the region-parallel record order cannot change the result (the whole reason the channel is shaped this
  way). No `System.Random`. Reads only frozen start-of-step neighbor snapshots.
- Inert on every golden (gate off). **Gates that MUST hold:** `Sim.ParityTests` = 657/4 byte-identical;
  `Sim.Bench` `deterministic=True`, `parallel==single`, hash `D96213B7BB4021A7`.

## Success conditions (verify first-hand, headless, do not trust)
1. Parity 657/4 byte-identical; bench hash `D96213B7BB4021A7`, parallel==single. (Gate off.)
2. With coop ON in live-city + part-B keep-right guard: `LIVECITY-LCSWAP` shows `keepRight stop=0`
   (float removed) AND flow is NOT regressed — arrivals ≥ ~900 by t≈1380, `stoppedFrac` late ≤ ~0.5,
   `stuckInternal` small (NOT the 37-42 box-block the naive guard caused). i.e. cooperation replaced the
   stopped sort. If box-block returns, cooperation isn't sorting enough up-front — tune (earlier trigger /
   allow ego to also brake harder toward the merge), do NOT ship the regression.
3. A targeted probe shows the strategic informFollower actually FIRES and merges succeed (add a counter:
   coop-advice-issued, strategic-merge-after-coop).
4. No overlaps introduced (`LIVECITY-STUCKCLEAR overlaps` stays ~0-2, pre-existing noise).

## Risk / retired failure mode (scope accordingly)
The retired informFollower DEGRADED ORGANIC nets (over-braked followers → more congestion) but RESCUED
saturated grids. The live-city demo IS a saturated grid, so it is the good case — but keep the yield the
GENTLE one-step helpDecel (not the crude full-follow-speed cap that caused the organic regression), and
keep it demo-gated so no parity/organic path is touched.
