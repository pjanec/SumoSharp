# URGENT-STRATEGIC-FOLLOW — design (HOW it will work)

**Status: DRAFT — awaiting owner sign-off.** No implementation beyond the committed, default-OFF probe
constraint may proceed until this is agreed (CLAUDE.md §Ways of working).
Companion docs: `URGENT-STRATEGIC-FOLLOW-TASKS.md` (the work), `URGENT-STRATEGIC-FOLLOW-TRACKER.md`
(the checklist). Trail: `JUNCTION-REALISM-SESSION-JOURNAL.md` Entries 24–25.

## 1. The defect this addresses (WHAT — by reference)

The owner's "lateral lane change while standing at a red" artefact, strategic form. Established in
Entries 20–24; the load-bearing trace (`junction-realism-L2-light`, left-turner `f_left_W00.0`):

| | first change to the required lane |
|---|---|
| SUMO | t=3, pos 30.94, **11.95 m/s** — 158 m before the junction, having decelerated to fit |
| ours (today) | t=45, pos 189.60, **1.00 m/s** — at the stop line, when the red releases the queue |

Root cause, traced (`[strategic-veto]`): the target lane's **leader** vetoes the change every step, and
both vehicles cruise at their shared maximum — **a gap between two equal-speed vehicles never opens**.
SUMO's mechanism is `MSLCM_LC2013::informLeader` (:464-560): an urgent strategic changer *brakes to slot
in behind* the target-lane leader — it creates the gap it needs.

## 2. Probe results (all committed as `UrgentStrategicLeaderFollowConstraint`, default OFF)

The faithful port of informLeader's cannot-overtake branch was prototyped and measured (Entry 25):

1. **Mechanism confirmed to two decimals.** With the flag on, `f_left_W00.0` changes at
   **t=3 / pos 30.94 / 11.95 m/s** — byte-for-byte SUMO's move on the light net.
2. **Goldens are inert in BOTH flag states.** All 661 byte-identical even with the flag ON: no golden
   vehicle is ever in the urgent-and-blocked state. The blast radius on the parity suite is zero.
3. **The naive global default COLLAPSES a saturated net.** `junction-realism-L2`:
   arrived 433 → **223**, running 17 → **226**, peak overlapping pairs 9 → **42**, `stuckDwell`
   0 → **824**. The four synthetic-junction2 behavioural tests fail the same way. **SUMO runs the same
   mechanism on the same net and drains 450/450 with 0 overlaps**, so the defect is in how our port
   interacts with the rest of our engine, not in the mechanism.

## 3. The design question: why does it collapse for us and not for SUMO?

**Leading hypothesis (H-A, untested — the diagnostic stage below settles it first):** the coupling
brakes ego, the gap opens, and the swap is then **still refused by vetoes SUMO does not have** —
`LaneChangeSlotContested`, `WouldCutInAheadOfStoppedFollower`, `IsTargetLaneOverlapped`, the
`deferStrategicCutIn` term. Ego is left permanently braking behind a leader it is never allowed to
follow: worst of both worlds, and at saturation the slowdown cascades.

**Alternative (H-B):** our "blocked" predicate (`!IsTargetLaneSafe(lead-only)`) is broader than SUMO's
`checkChange` leader test, so the coupling engages for vehicles SUMO would let change outright, braking
traffic that had no reason to brake.

**Alternative (H-C):** the coupling correctly slows ego but our follow-up change then happens a step
later than SUMO's (plan/execute split), and at saturation that one-step lag compounds.

The scoreboard on reasoned hypotheses is 0-for-16; **the diagnostic stage is not optional.**

## 4. Mechanism (agreed parts)

- The constraint stays exactly where the probe put it: a term in the `ComputeMoveIntent` Min fold
  (binder 18), reading only the frozen snapshot + ego's own last-step `LookAheadSpeed`. Deterministic,
  parallel-safe, one guard (it subsumes the dead-lane brake's `stopSpeed(usableDist)` half exactly as
  SUMO's `plannedSpeed` does — no second overlapping box-check).
- `informNeighLeader` messaging (leader-side cooperation) stays unported; the ego-side brake alone
  reproduced SUMO's traced move.
- Faithful formulas and the documented simplifications are in the constraint's header; the design does
  not restate them.

## 5. Acceptance gates (all must hold before any default flip)

| gate | requirement |
|---|---|
| goldens | 661 byte-identical (already proven in both flag states) |
| L2-light | `f_left_W00.0` changes at speed ≈ t=3 (already proven) |
| L2 (saturated) | arrived ≥ 433, peak overlaps ≤ 9, `stuckDwell` = 0 — **no worse than today on every column** |
| L2 stopped-LC rate | materially below 1.396, toward SUMO's 0.410, **with the denominator reported** (Entry 25's 0.450 was a denominator artefact) |
| 26-net battery | no `stuckDwell` regression anywhere; arrivals within noise elsewhere |
| behavioural tests | the 4 synthetic-junction2 tests green |

**Gridlock outranks this artefact** (owner priority, Entry 1). A scoped fix that leaves some stopped
lane changes but cannot deadlock beats a complete fix that can.

## 6. Non-goals

- No overtake-arm ballistic branch; no `myLeadingBlockerLength`; no leader-side messaging.
- No touching the speedGain or keepRight halves of the artefact (separate mechanisms, Entries 21–22).
