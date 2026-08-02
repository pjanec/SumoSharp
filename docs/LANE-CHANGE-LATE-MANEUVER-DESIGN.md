# LANE-CHANGE-LATE-MANEUVER — design: queue-tail swerve-through & misaligned stops (Entry 59 Class A)

**Status: DESIGN — awaiting owner review. No implementation yet.**
Trail: `JUNCTION-REALISM-SESSION-JOURNAL.md` Entry 59 (owner report), Entry 60 (the trace +
measurement, BEFORE entry pending implementation approval).

## 1. The owner's report (2026-08-02, 3D session)

Cars arriving at the back of a queue drive TIGHT up to the standing tail car, only then start
a change to the free left lane; the IG renders the 2 s maneuver sweeping THROUGH the leader's
body, and cars often stop misaligned, half in each lane. "Drivers think ahead — in our
simulation this emergency-shaped swerve is the norm."

## 2. What the trace found (all instrument-proven, exemplar `__veh320` t=243–250, deterministic)

The failure is a three-stage chain, and the stages have DIFFERENT owners:

1. **The wish forms EARLY and correctly.** The speed-gain accumulator crosses its 0.2
   threshold at t=245.5 — 11 m/s, ~27 m upstream of the eventual stop point. Decision timing
   is NOT the bug.
2. **The commit is deferred by SUMO's own usability gate** `neighDist / speed > 20`
   (MSLCM_LC2013.cpp:1857). veh320's left lane is TURN-ONLY at the light, so its
   continuation is ~80–100 m; the gate legitimately refuses a 20 s stay at 11 m/s and opens
   only once braking-for-the-queue decays speed to ~2.3 m/s. **Measured across the capture:
   187 of 208 executed late swerves (90%) have neighDist < 150 m** — turn-lane targets, where
   vanilla SUMO's identical gate would also commit at crawl speed. The deferral is
   SUMO-faithful; no decision-side change is designed.
3. **The execution then freezes into the artifact.** SUMO commits INSTANTLY (discrete snap) —
   no window exists in which anything can render mid-lane. Our live-city realism knob
   (`lanechange.duration = 2.0` s, 4 steps) opens that window, and `AdvanceLaneChanges` HOLDS
   any in-progress maneuver while `speed < LaneChangeMinSpeed` (1.5 m/s). veh320 commits at
   2.3 m/s, drops below 1.5 the next step while closing the last metres, the sweep freezes
   against the leader's bumper, resumes on queue-creep in body contact, and any re-freeze
   past the midpoint parks the car half-in-half-out. **The artifact is wholly owned by the
   beyond-SUMO continuous-maneuver mechanism** — so the fix belongs there, gated exactly like
   it (parity untouched by construction).

Prevalence (standard Geneva capture, 1200 s): 208 executed late swerves (~1 per 6 s), 842
distinct vehicles standing at a tail with a suppressed change wish.

## 3. Design — two execution guards, no decision change

Both live entirely inside the `LaneChangeDuration > 0` / `LaneChangeMinSpeed > 0` realism
path (0/0 on every golden ⇒ byte-identical by construction).

### E1 — runway guard at maneuver start (`CommitLaneChange`)

Do not START a continuous maneuver whose forward travel cannot clear the same-lane leader:

```
runwayNeeded = v.speed * LaneChangeDuration * 0.5 + margin   (margin = MinGap)
start only if sameLaneLeadGap > runwayNeeded, else DEFER (keep wish, no accumulator reset)
```

`0.5 *` because the car is braking through the sweep (mean of start/end speed, end ≈ 0 at a
tail). Deferral reuses the existing veto semantics (the accumulator is NOT reset — the change
retries every step and fires the moment the queue moves and opens runway). The instant-snap
path (duration 0) is untouched — SUMO semantics need no guard.

Effect at a queue tail: the car stops IN LANE behind the tail (today's stop position, minus
the swerve), and performs the change only once the queue creeps and runway opens — which is
exactly what the owner describes a human doing when they missed the early gap.

### E2 — no frozen misaligned poses (`AdvanceLaneChanges`)

Replace the unconditional below-min-speed HOLD with:

- **Before the midpoint** (`2*elapsed <= total`): ABORT cleanly — recenter to the source lane
  over the remaining lateral offset (reuse `ClearLaneChangeManeuver` + let the render's
  lateral interpolation walk back). The car appears to "tuck back in", never parks diagonal.
- **Past the midpoint**: COMPLETE the lateral slide at reduced lateral speed even while slow —
  a driver already halfway into the target lane finishes the wedge while crawling; the old
  behavior (freeze indefinitely) is the misaligned-stop norm the owner flagged.

The hold's original purpose (no full-lane sideways step at standstill) is preserved: E1 means
maneuvers only START with runway, and E2's two arms both progress toward a lane center
instead of freezing between them.

## 4. Success conditions (measured, both surfaces)

1. Standard Geneva capture (`LIVECITY_LCLOG=1`): executed `[lclate]` events with a
   sweep-through (leadGap < forward travel during maneuver) → **0**; total executed
   late-swerve count reported for the journal (expect large drop; residual = legitimate
   post-creep changes).
2. `LIVECITY-OVERLAP` queue/lateral classes not increased; junction class unchanged (±10%).
3. Arrivals within ±3% of the pre-change arm (same env, RINGBREAK=0 and =1 arms).
4. Full sln suite green; goldens byte-identical; bench hash `A134ED3716DDE7BC` par==single
   (E1/E2 are `LaneChangeDuration>0`-gated; every golden runs duration 0).
5. Owner 3D verdict on the queue-tail rendering (the deciding surface for a render-visible
   artifact).

## 5. Explicitly out of scope

- The 20 s usability gate and the speed-gain accumulator (SUMO-faithful, measured 90% case).
- The 17/208 long-continuation late commits (accumulator dynamics, also SUMO-shaped).
- SUMO's `REACT_TO_STOPPED_DISTANCE` urgent arm — it reacts to SCHEDULED stops
  (`isStopped()` = bus/parking stops), NOT queue tails; porting it would not touch this class.
- The suppressed-wish population (842 standing cars) — E1/E2 change when maneuvers run, not
  who wants one.
