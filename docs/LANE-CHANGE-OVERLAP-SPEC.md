# SPEC — dense multi-lane car overlaps (port SUMO cooperative lane-changing)

**Status:** open, needs a focused independent session. **Type:** parity-sensitive engine port (design-first).
**Owner ask:** cars in dense multi-lane traffic change into occupied space and end up **overlapping**
(sitting on top of each other) at busy junctions. This is the blocker for a *serious* high-density
live-city demo. A prior session diagnosed it fully but did **not** fix it (correctly — it needs the
cooperative-lane-change feature, which is a real multi-stage engine task). This document is the complete
hand-off: reproduce → understand → fix → verify.

---

## 1. The bug, in one line
SumoSharp lets a vehicle complete a lane change even when the target-lane **follower** (and, in dense
queues, effectively any occupant) has no safe gap, because it ports only the **leader**-gap veto and does
**not** model SUMO's **cooperative lane-changing** (the follower slows to make room). Vanilla SUMO never
overlaps on the same net; SumoSharp does.

## 2. Reproduce (deterministic, committed, ~1 min)
Substrate: the already-committed dense diagnostic `scenarios/_diag/willpass-saturation`
(a saturated multi-lane grid; 1 s step; seed 42; teleport off).

```
# SumoSharp — emit engine FCD and count same-lane overlaps
dotnet run -c Release --project src/Sim.Run -- scenarios/_diag/willpass-saturation --steps 200 --fcd-out /tmp/ss.fcd.xml
#   -> 258 overlap events (243 with BOTH cars stopped) in 200 steps      [measured this session]

# vanilla SUMO 1.20.0 — same net/demand/config
cd scenarios/_diag/willpass-saturation
sumo -c config.sumocfg --end 200 --fcd-output /tmp/sumo.fcd.xml --fcd-output.attributes x,y,speed,lane
#   -> 0 overlap events                                                   [measured this session]
```

**Overlap metric** (the acceptance measure): parse an FCD file; per timestep, group vehicles by `lane`
(skip internal `:` lanes); on each lane sort by position; count consecutive pairs whose Euclidean
distance `< 5.5 m` (shorter than one vehicle) — that is a physical overlap. The committed acceptance test
`tests/Sim.ParityTests/LaneChangeOverlapDiagTests.cs` implements this directly on `Engine.PosX/PosY/LaneIds`
(no FCD needed) and is **`[Fact(Skip=…)]`** — the fix's job is to unskip it and make it assert `0`.

Live-city analog: `dotnet run --project src/Sim.Viz -- --live-city out.html` also prints an overlap-adjacent
picture; `LIVECITY_CARS=N` scales density (110→3493, 60→1487, 30→903 overlaps — scales but never 0).

## 3. Root cause — exact engine sites
All lane-change decisions run post-move in `Engine.DecideSpeedGainChanges` (Engine.cs ~9140), which calls
`ApplyKeepRightDecision` then `TryStrategicLaneChange`; speed-gain and `TryGiveWayLaneChange` also commit
through `CommitLaneChange` (~9814) → `AdvanceLaneChanges` (~9833, the continuous-maneuver stepper).

- **`IsTargetLaneSafe(ego, neighLead, neighFollow, dt)`** (Engine.cs ~10124) — the secure-gap veto. It DOES
  check both a leader gap and a follower gap (via `SecureGap`) **when both are passed**.
- **Keep-right passes `null` for the follower** — `ApplyKeepRightDecision` (~9469) calls
  `IsTargetLaneSafe(v, neighLead, null, dt)` (~9610). This is a **documented deliberate reduction**
  (`docs/HIGH-DENSITY-P2G-DESIGN.md` §4.1/§7): the follower half was omitted because a follower veto
  **without** cooperative LC over-brakes the grid into gridlock (see §5 trap below). So a keep-right that
  SUMO would block on the follower proceeds → the ego cuts into an occupied slot → overlap.
- **`TryStrategicLaneChange`** (~9864) passes both leader+follower, but its own comment (~10057) says the
  target-lane-traffic case is *"future work (LCA_URGENT's real blocker-cooperation machinery … is not
  ported)"*; with `lanechange.duration>0` a change decided at step T lands at step T+k with **no re-check**,
  so even a check that passed at decision time can land on a leader/follower that has since stopped.
- Net effect: no source models the follower cooperatively **making room**, so dense changes overlap.

Confirmed **pre-existing** (not from the realism pass that added `LaneChangeMinSpeed`/`lanechange.duration`):
overlaps are ~identical with `LaneChangeMinSpeed=0` and `lanechange.duration=0`.

## 4. SUMO reference — the algorithm to port (v1.20.0, `/sumo` must be vendored at `v1_20_0`)
`src/microsim/MSLaneChanger.cpp`:
- `checkChange(laneOffset, …, neighLead, neighFollow, …)` (line 744) sets a `blocked` bitmask.
  - **Follower block (798–838):** compute `secureBackGap = neighFollow.first->getCarFollowModel().getSecureGap(
    follower, ego, vNextFollower, vNextLeader, ego.maxDecel)`; if `neighFollow.second <
    secureBackGap * safetyFactor` → `blocked |= blockedByFollower`.
  - **Leader block (843–870):** symmetric on `neighLead`.
  - The change executes only if unblocked: `execute = dir != 0 && ((state & LCA_BLOCKED) == 0)` (line 430).
- SumoSharp's `IsTargetLaneSafe` already mirrors *both* halves — the gap is that **keep-right doesn't pass
  the follower**, and there is no cooperative resolution when a wanted change IS follower-blocked.

`src/microsim/lcmodels/MSLCM_LC2013.cpp` — **cooperative lane-changing** (the missing piece):
- When a wanted/urgent change is blocked, the ego **reserves blocker length** and **informs** its
  neighbours to make room: `updateBlockerLength` (via `MSLCHelper`, ~1484), `informLeader` (464),
  `informFollower` (645), `saveBlockerLength` (2057) → `myLeadingBlockerLength`. `informFollower` returns a
  reduced speed the **follower** adopts (`getCarFollowModel().stopSpeed(...)`) so the back-gap opens over the
  next steps; then the change is no longer blocked and proceeds safely. See the orchestration at ~1484–1513.

## 5. The trap (why the naive follower veto fails — read before Stage 1)
`docs/HIGH-DENSITY-P2G-DESIGN.md` §4.1 / §7 measured it: gating on **both** leader+follower gaps **without**
the cooperative machinery regressed `willpass-saturation` from **0 → 30 stuck** (vehicles that can never
find a gap deadlock). The leader-only veto keeps it at 0 stuck. **Conclusion: the follower veto and
cooperative LC must land together.** A follower veto alone will trade overlaps for gridlock.

## 6. Parity / determinism invariants (the iron law)
- **Every committed golden must stay byte-identical.** The current low-density parity scenarios have no
  follower-blocked changes, so a faithful port of `checkChange`+cooperative-LC should leave them unchanged.
  Verify with the full `dotnet test Traffic.sln` (ParityTests 654/+3 today) — any golden that moves is a
  port bug, not an accepted change, unless re-generated from SUMO 1.20.0 with a recorded provenance bump.
- **No new `System.Random`**; deterministic; two runs byte-identical (the messaging/reservation state must
  be per-vehicle and order-independent, matching SUMO's deterministic changer order).
- **`willpass-saturation` stuck-count must remain 0** (the §5 trap) — this is a hard gate, not a nicety.

## 7. Staged plan (independent multi-stage session)
- **Stage 0 — reproduce & instrument.** Vendor `/sumo` at `v1_20_0`. Run §2; confirm 258 vs 0. Unskip-locally
  the acceptance test to watch the number. Add a `stuck`-count read (Engine already tracks a stuck/teleport
  analog — see `WillPassSaturationDiagTests`) so Stage 1/2 can watch both overlaps AND stuck.
- **Stage 1 — universal follower veto.** Make **every** change type (keep-right included) run the full
  leader+follower `IsTargetLaneSafe`; add a re-check at maneuver *completion* (`AdvanceLaneChanges`) so a
  change that becomes unsafe mid-maneuver aborts. *Expected:* overlaps fall sharply, **stuck rises** (the
  §5 trap). This stage alone is NOT acceptable — it's the setup for Stage 2.
- **Stage 2 — cooperative lane-changing.** Port `informFollower`/`saveBlockerLength`/`updateBlockerLength`
  (MSLCM_LC2013): a vehicle that wants a change but is follower-blocked reserves blocker length and makes
  the target-lane follower adopt the informed (reduced) speed, opening the gap over subsequent steps; the
  change then proceeds. *Expected:* overlaps → 0 **and** stuck → 0.
- **Stage 3 — parity + acceptance.** Full `dotnet test` byte-identical (regenerate goldens only if a
  provable SUMO-faithful change moves one, with provenance bump); determinism check; `willpass-saturation`
  overlaps 0 & stuck 0; live-city overlaps ≈ 0 at `LIVECITY_CARS=110`. Unskip
  `LaneChangeOverlapDiagTests` asserting `== 0`.

## 8. Acceptance criteria (the gate)
1. **`LaneChangeOverlapDiagTests`** (unskipped): `willpass-saturation`, 200 steps → **0** same-lane overlaps
   (`< 5.5 m`), matching vanilla SUMO's 0.
2. **`willpass-saturation` stuck-count = 0** (no gridlock regression).
3. **Full `dotnet test Traffic.sln` green with every golden byte-identical** (or provenance-bumped with a
   documented SUMO-1.20.0 regeneration). Determinism: two runs byte-identical.
4. **Live-city:** the overlap count in `--live-city` at default density drops to ≈ 0 (currently ~3400).
5. No new `System.Random`; the cooperative messaging is deterministic and thread-order independent.

## 9. Pointers
- Root sites: `src/Sim.Core/Engine.cs` — `IsTargetLaneSafe` (~10124), `ApplyKeepRightDecision` (~9469),
  `TryStrategicLaneChange` (~9864), `DecideSpeedGainChanges` (~9140), `TryGiveWayLaneChange` (~5228),
  `CommitLaneChange` (~9814), `AdvanceLaneChanges` (~9833), `SecureGap`.
- Prior design context: `docs/HIGH-DENSITY-P2G-DESIGN.md` (the leader-only decision + §7 deferral).
- SUMO source: `MSLaneChanger.cpp` `checkChange` (744, follower 798–838), `MSLCM_LC2013.cpp`
  `informFollower` (645) / `saveBlockerLength` (2057) / `updateBlockerLength` (~1484).
- Repro scenario: `scenarios/_diag/willpass-saturation` (committed). Acceptance harness:
  `tests/Sim.ParityTests/LaneChangeOverlapDiagTests.cs` (committed, `[Fact(Skip)]`).
- Live-city knobs: `LIVECITY_CARS`, `LIVECITY_LCMIN`, `LIVECITY_DUMP="x,y[,r]"` (see `SceneGen.BuildLiveCity`).
