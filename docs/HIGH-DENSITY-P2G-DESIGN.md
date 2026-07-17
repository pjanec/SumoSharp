# HIGH-DENSITY-P2G-DESIGN.md — multi-lane keep-right safety veto

**Status:** design (owner-approved scope, 2026-07-17). Item **P2-G** from `HIGH-DENSITY-HANDOFF.md` §4.
Grounds in vendored `sumo/src/microsim/MSLaneChanger.cpp` + `sumo/src/microsim/lcmodels/MSLCM_LC2013.cpp`.

## 1. WHAT (the gap) — reference the handoff for context

On a 2-lane road under load SumoSharp diverged from vanilla SUMO 1.20.0 by ~7 m / ~2.6 m/s
(scenario `46-reroute-multilane` residual). Single-lane is bit-exact. The handoff framed this as a
"multi-lane car-following / lane-distribution gap"; the empirical localisation (this session) pins it
to **the lane-change model**, not car-following and not junction right-of-way.

### Localisation evidence (empirical, current tip)
- **Pinned-lane control** (a straight 2-lane edge, depart lanes fixed *identically* in engine and
  SUMO, `sigma=0`): SUMO keeps vehicle `v1` on lane 1 for the whole 120 s run; the engine
  **spuriously keep-rights `v1` into lane 0 at t=8 s**, cutting into the gap between two lane-0 cars
  and forcing itself (and the follower) to brake (13.89→11.52 m/s). The single wrong lane choice
  cascades into the full divergence (reproduced on scenario 46: max 7.09 m / 2.60 m/s, first
  divergence a *lane* mismatch, route identical).
- **Junction RoW is clean** on the current tip: a fresh 2-lane grid runs with 0 stuck (SUMO 0
  teleports) — the `willPass` pre-pass (C4-viii) already resolved the old gridlock. So the NEED-doc
  gridlock story is stale; component (C) is not the current gap.
- **Car-following is not independently broken**: in the pinned-lane control, positions/speeds track
  SUMO exactly *until* the erroneous lane change; all downstream error is a consequence of being on
  the wrong lane (different leader), not a CF-formula discrepancy.

## 2. Root cause (single site)

`Engine.ApplyKeepRightDecision` (`src/Sim.Core/Engine.cs`, the keep-right fire at the
`keepRightProbability * keepRightParam < -changeProbThresholdRight` branch): when the keep-right
accumulator crosses threshold, the engine swaps the vehicle to the right lane **with no
safety/blocker veto**. The in-code comment states the omission explicitly: *"No safety/blocker veto
ported here — every scenario reaching this fire has an empty target (right) lane."* That assumption
holds for every committed single-lane golden but breaks on a dense multi-lane road, where the right
lane carries a follower and/or leader.

The **speed-gain / left path already has** the veto: `IsTargetLaneSafe(v, neighLead, neighFollow, dt)`
(the `getSecureGap` leader+follower check) gates the left change. The keep-right / right path simply
never received the same gate.

## 3. SUMO reference (why the veto is symmetric across directions)

`MSLaneChanger::checkChange` (`MSLaneChanger.cpp:744-935`) computes `blocked` identically for both
directions (`checkChangeWithinEdge(-1, …)` right and `checkChangeWithinEdge(1, …)` left):
- **follower block** (`:798-837`): `blocked |= blockedByFollower` iff
  `neighFollow.second < neighFollow.getCarFollowModel().getSecureGap(…) * safetyFactor`.
- **leader block** (`:843-870`): `blocked |= blockedByLeader` iff
  `neighLead.second < vehicle.getCarFollowModel().getSecureGap(…) * safetyFactor`.
- the change executes only if `(state & LCA_BLOCKED) == 0` (`:430`).

`safetyFactor` defaults to 1.0 (no effect). So a keep-right (a RIGHT change) is subject to exactly the
same target-lane leader+follower secure-gap block as a speed-gain (LEFT) change. The engine's
`IsTargetLaneSafe` implements precisely that leader-gap + follower-gap `getSecureGap` test (the
"minimal-but-faithful" form already blessed for the left path; it omits the `vNext`/`gComputeLC`
refinement and `safetyFactor`, both non-binding at default config, consistent with the left path).

## 4. HOW (the fix) — minimal, consistent, gated

At the keep-right fire, before swapping lanes, gate the swap on the target-lane **leader** secure gap
using the same helper the left path uses (`neighLead` is already fetched for the incentive calc):

```
if (keepRightProbability * keepRightParam < -changeProbThresholdRight)
{
    if (IsTargetLaneSafe(v, neighLead, null, dt))   // leader-only (see 4.1)
    {
        v.LaneId = rightLane.Id;
        v.LaneHandle = rightLane.Handle;
        v.KeepRightProbability = 0.0;   // resetState() on a committed change (MSLCM_LC2013 :1063/1080)
        return;
    }
    // BLOCKED: fall through -> keep the decremented accumulator, stay on lane, retry next step.
}
v.KeepRightProbability = keepRightProbability;
```

**Blocked-change semantics (match SUMO + the left path):** a vetoed keep-right does NOT reset the
accumulator (SUMO resets only on a *committed* change); it stores the decremented value and retries
next step, so the vehicle keeps right the instant the leader gap opens.

### 4.1 Leader-only, not both halves — a deliberate, evidence-forced reduction

The initial design gated on BOTH the leader and follower secure gaps (`IsTargetLaneSafe(v, neighLead,
neighFollow, dt)`), mirroring `checkChange` exactly. Testing showed that **regresses a committed
saturation diagnostic** from 0 to 30 stuck (`scenarios/_diag/willpass-saturation`, a 3x3 -L2 grid;
SUMO drains it to 0). Root cause: in SUMO a change blocked by the target-lane FOLLOWER is resolved by
**cooperative lane-changing** (`MSLCM_LC2013::informBlocker`/`saveBlockerLength` — the follower slows
to make room), which this engine does not model. Porting the follower block *without* its cooperative
counterpart is **less** faithful to SUMO's flow, not more: it over-brakes into gridlock a net SUMO
drains freely. Empirically:

| Veto            | moderate 2-lane control (max pos err) | saturated -L2 diag (stuck) | full suite    |
|-----------------|---------------------------------------|----------------------------|---------------|
| none (pre-fix)  | 82.28 m (spurious keep-right at t=8)  | 0                          | 556 green     |
| leader+follower | ~0 (first div t=72)                   | 30 (REGRESSION)            | 555 (1 fail)  |
| leader-only     | 2.37 m (first div t=72)               | 0                          | 556 green     |

So the fix applies the **leader half only**. It captures the dominant divergence (82 m to 2.4 m) with
zero regression; the residual (a keep-right SUMO blocks on the follower) is accepted behaviourally per
the owner steer. The follower block + cooperative LC is the evidence-gated follow-up (§7).

Scope kept minimal on purpose (mirrors the left path): the external-obstacle veto
(`TargetLaneBlockedByObstacle`, a non-SUMO B5-ii extension needing `time` threaded in) is NOT added
here — this fix is the SUMO-parity secure-gap leader veto only.

## 5. Determinism / parity argument (additive · gated · byte-identical)

- The veto only ever **prevents** a change; it never creates one.
- It changes behaviour only when the right lane has a **leader** within the secure gap. Every
  committed single-lane scenario returns early (`RightNeighbor < 0`) and never reaches the fire;
  every committed multi-lane scenario that reaches the fire has an empty right lane (the scope note),
  so `IsTargetLaneSafe(…, null, …)` returns `true` and the swap still happens → **all 556 goldens
  byte-identical** (verified: full suite green after the fix). The full `dotnet test` suite is the
  gate (not a claim).
- Reads only the frozen `postMoveNeighbors` snapshot + immutable network; writes only ego's own
  fields. No new cross-vehicle read; the existing per-vehicle isolation (this phase's header comment)
  is preserved, so the region-parallel path stays byte-identical to serial.

## 6. Success conditions (the acceptance gate)

1. **New bit-exact anchor** `scenarios/49-multilane-keepright`: a straight 2-lane edge, `sigma=0`,
   numeric pinned depart lanes tuned so SUMO's keep-right is blocked (a vehicle SUMO keeps on lane 1
   that the pre-fix engine wrongly keep-rights). Golden from vanilla SUMO 1.20.0
   (`--precision 6`, `--fcd-output.acceleration`, `--time-to-teleport -1`), `tolerance.json` exact
   `lane`/`pos`/`speed` @1e-3. Engine matches within tolerance **only with the fix**; the test asserts
   the vehicle stays on lane 1 (no spurious keep-right) and the full trajectory matches @1e-3.
2. **No regression:** full suite stays green and byte-identical (556 → 558 with the two new anchor
   tests; every prior golden unchanged). `Sim.Bench` determinism hash unchanged.
3. **Dense case improves:** re-measure scenario 46 + a denser 2-lane grid engine-vs-SUMO; report the
   residual after the fix. Accept the dense case behaviourally/statistically per the owner steer
   (bit-exact anchor + statistical dense acceptance); if a material residual remains, report it and
   name the next-order suspect (cooperative LC / best-lanes continuation) rather than chasing it now.

## 7. Explicitly deferred (gated on evidence)

- **Cooperative lane changes (LCA_COOPERATIVE):** the engine has no "make room for a blocked vehicle"
  arm. Only pursue if the dense re-measure proves it binding.
- **General best-lanes continuation distance for keep-right:** currently approximated by the right
  lane's own length (`neighDist = rightLane.Length`); a multi-edge continuation length is deferred.
- **`departLane="free"`/`"random"` ingest:** throws today (only numeric/`"best"` supported). Separate
  small parser fix, tracked independently.
