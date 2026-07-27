# NEED — same-lane interpenetration: SAME-STEP DOUBLE PLACEMENT, then perfect symmetry makes it permanent

**Found by:** cause-attribution measurement over the full demo hour, all three junction gates ON
(`LongHorizonGridlockDiagTests`, 7200 steps; 696 same-normal-lane overlap events / **146 episodes**,
reproduced exactly ⇒ deterministic).
**Status:** root cause **identified by measurement**. Supersedes the framing in
`NEED-colocated-vehicles.md` (which recorded the *symptom*) and **falsifies** the candidate fix in
`LANE-CHANGE-OVERLAP-DESIGN.md` §3 Stage 3.
**Severity:** HIGH — this is the **top remaining violation** of
`CONSTRAINT-high-realism-artefact-ladder.md` rung 3 (cars overlapping during normal, non-unblocking
manoeuvres — never permitted in the high-realism area), and it is **4× worse in-zone** with the gates on
(28 → 115 in-zone events).

## ⚠ First: the leading hypothesis is FALSIFIED

`LANE-CHANGE-OVERLAP-DESIGN.md` §3 Stage 3 proposed that the residual overlaps are *"the second emerging
vehicle overshoots its cross-junction leader"*, with the fix being to *"clamp an emerging vehicle behind
the target lane's rearmost occupant"*. I adopted that as the leading hypothesis and asked for it to be
attacked. **It does not hold:**

| Emergence gap to the target lane's rearmost prior occupant | Measured |
| --- | --- |
| samples with a measurable prior occupant | 103 of 180 |
| **negative gaps (i.e. an actual overshoot)** | **0 / 103 (0%)** |
| minimum gap | **+4.05 m** |
| median gap | +113 m |

An emerging vehicle **never** overshoots an already-established leader; when a genuine prior occupant
exists it lands comfortably behind. **So the Stage-3 clamp would fix nothing.** Do not implement it on the
strength of that document.

## The actual mechanism

Three *entry mechanisms* place a vehicle onto a normal lane — junction emergence (H-E), insertion (H-INS),
and lane change (H-LC). Each computes its placement from the **same frozen start-of-step snapshot**, and
**none cross-checks against another placement being made in the same step.** So two vehicles arriving by
*different* mechanisms — or by the same one from different source lanes — can land at the **same slot in
the same step**, each correctly seeing an empty slot in the pre-step world.

Then the second half of the defect: **once two vehicles are byte-identical (same lane, same pos, same
speed), Krauss/IDM applies identical forces to both forever.** They are perfectly symmetric, so nothing
in the model can separate them. A one-step placement collision becomes a ~100-step visible artefact.

Worked examples from the trace:

- **H-INS (clean, reproducible):** a freshly-inserted vehicle appears at a fixed default depart offset
  (~5.65 / 6.95 / 8.90 m) **directly on top of a vehicle already queued/stopped near the lane start** —
  insertion does not check for a backed-up queue at the insertion point.
- **H-LC (pure):** two vehicles lane-change from *adjacent source lanes into the same target lane and
  position in the same step* — e.g. `…4_1 → …4_2` and `…4_3 → …4_2` both landing at pos 27.83, spd 16.67.
- **H-E ∩ H-INS (21.3% of events):** a car emerging from a junction and a freshly-departing car both land
  near the same lane-start slot in the same step.

## Why the event count is dominated by a few incidents

| Episode length | Episodes | Events | Share of 696 |
| --- | --- | --- | --- |
| 1 step | 53 | 53 | 7.6% |
| 2 steps | 47 | 94 | 13.5% |
| 3–5 steps | 28 | 95 | 13.6% |
| 6–10 steps | 5 | 32 | 4.6% |
| **> 10 steps** | **13** | **422** | **60.6%** |

**13 episodes produce 60.6% of all events.** One incident alone — `__veh56`/`__veh84`, triggered by an
H-LC double-lane-change collision at step 191 — stays byte-identical for 28 consecutive steps, persists
across **three consecutive lanes/edges for ~100 steps**, and contributes **91 events (13.1%) by itself**.

**Consequence for prioritisation:** *persistence*, not *onset frequency*, is what generates the visible
volume. Making episodes self-resolve would remove ~60% of events even if not one onset were prevented.

## Attribution (categories overlap; per-episode onset is the causally meaningful basis)

| Hypothesis | per-event (696) | **per-episode onset (146)** |
| --- | --- | --- |
| H-E emergence | 168 (24.1%) | **90 (61.6%)** |
| H-LC lane change | 184 (26.4%) | **77 (52.7%)** |
| H-INS insertion | 171 (24.6%) | **83 (56.8%)** |
| H-CF 1-step car-following | 1 (0.1%) | 1 (0.7%) |
| unexplained | 386 (55.5%) | **6 (4.1%)** |

Per-event "unexplained" is inflated by a 3-step lookback: 239 of the 386 are later events inside an
episode whose *first* event **was** explained. **147 events (21%) belong to episodes unexplained from
onset** — either an older trigger than the window, or a mechanism these four hypotheses miss. Recorded as
genuinely open, not force-fitted.

**H-CF is effectively ruled out** (1 event). The "ECS frozen-snapshot car-following reaction" the
`LaneChangeOverlapDiagTests` skip banner blames is *real but negligible* here.

## Clustering

63 distinct lanes; top-10 ≈ **54%** of events; top-2 ≈ **23%**. Moderately clustered on specific
merge/turn lanes at a few junctions — not uniform, but not a single hotspot either.

## Fix options, mapped to the artefact ladder

All three are **rung 1 (prevention)**, not rescues — so all are admissible in the high-realism area.

1. **H-INS occupancy check at insertion — cheapest and SUMO-native.** SUMO's `MSLane::isInsertionSuccess`
   refuses an insertion that would not fit. We do not check for a queue at the depart offset. Smallest
   change, clearly faithful, and it removes one of the three onset mechanisms outright.
2. **Same-step arrival arbitration — the structural piece.** Two placements in one step must not claim the
   same slot. SUMO gets this free by being sequential (`MSLaneChanger` processes vehicles in order); our
   frozen-snapshot parallel plan phase does not. Needs a reservation/claim in the command buffer, i.e.
   exactly the *"timing of structural mutations"* deviation CLAUDE.md permits — and it is where the
   owner's *"check for imminent overlap and pause one of the cars"* idea belongs: deferring one of two
   simultaneous arrivals **is** the arbitration.
3. **Symmetry break so co-location self-resolves — highest leverage per unit of work.** Perfect symmetry
   is what turns a 1-step glitch into ~100 steps (60.6% of events). A deterministic tie-break — e.g. the
   ordinal vehicle-id rule already used by `IsLeaderByEntryOrder`, never `EntityIndex` — would let one car
   yield and separate. This does **not** fix the onset, so it must not be shipped *instead of* (1)/(2);
   but it bounds the damage of any residual onset, including the 147 unexplained events.

**Recommended order: (1) → (3) → (2).** (1) is cheap, native and removes a whole mechanism; (3) collapses
the visible volume regardless of onset cause; (2) is the correct but largest piece, and its blast radius
is the whole plan phase.

## Success conditions

- Same-normal-lane overlap **episodes > 10 steps: 0** (from 13). This is the believability-critical metric,
  not the raw event count.
- In-zone same-lane events (high-realism pocket): **≤ 28**, i.e. no worse than the gates-OFF baseline, with
  a target of 0.
- No episode in which two vehicles hold a byte-identical pose for more than 1 step.
- All 661 goldens byte-identical, `Sim.Bench` hash `D96213B7BB4021A7` par == single, five gridlock
  diagnostics green, `LongHorizonGridlockDiagTests` still green on its existing assertions.
- Throughput must not regress: completed trips over the hour ≥ 2709.
