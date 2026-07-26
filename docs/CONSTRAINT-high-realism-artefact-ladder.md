# CONSTRAINT — the high-realism artefact ladder (owner-stated, binding)

**Source:** the demo owner, stated directly. This is a **requirement**, not an inference, and it overrides
any convenience argument in the NEED/design docs.
**Applies to:** the live-city demo's **high-realism area** (the camera-driven circular pocket —
`LiveCitySim.HighRealismPocketX/Y` + `HighRealismPromoteRadius`). Low-realism areas are more permissive.
**Why it exists:** the demo's goal is **believability**. A parity-correct simulation that produces visible
impossible events fails that goal regardless of its numbers.

## The ladder, best to worst

| # | Behaviour | High-realism verdict |
| --- | --- | --- |
| 1 | **Prevent the blockage** (admission control, right-of-way, ordering) | ✅ the only acceptable *general* solution |
| 2 | Cars **pass through each other** | ⚠️ permitted **ONLY** when they are **ALREADY crashed into each other AND blocking the junction** — a recovery from an already-broken state. **Otherwise disallowed.** |
| 3 | Cars **overlap during normal, non-unblocking manoeuvres** | ❌ **NOT allowed** |
| 4 | **Teleport** | ❌ **NEVER allowed in high realism — no exception** |
| 5 | A car **blocked with no obvious reason** (not overlapped) | ❌ must **NOT** be "solved" by teleport or by allowing overlap. Requires finding and fixing the **real cause** — a different, believable fix. |

Four things follow that are easy to get backwards:

- **Tier 2 is a RECOVERY, not a TOOL.** Its precondition is that the cars are *already* interpenetrating and
  blocking the junction. It may **not** be used to free a car that is merely stuck. So the trigger must be
  **measured physical overlap**, never elapsed waiting time.
- **Rung 5 is the load-bearing rule for engineering discipline.** A car stopped for no visible reason is a
  *symptom of a bug*, and papering over it with a rescue **conceals** that bug. This repo has already been
  bitten by exactly that: `__veh127` sat frozen for 95 steps with **nothing overlapping it**, and the cause
  was a mis-gated predicate — *"an ORCA rescue there would have masked the mis-gate"*
  (`F3-SESSION-LOG.md` §9.26). The owner has been consistent on this from the start: a rescue is *"only for
  cases when cars are physically overlapping slightly (not the case when just stuck with no visible reason
  and not overlapping)"*.
- **Tier 2 and tier 3 are the same GEOMETRY** — two cars overlapping — distinguished only by **why** it
  happened. So an overlap metric alone cannot judge compliance; the cause must be attributable.
- **Teleport is worse than interpenetration**, which inverts the usual traffic-sim instinct that a
  collision is the worst outcome. Here a car vanishing and reappearing is more damaging to belief than two
  cars briefly clipping.

## Current compliance, measured

Full hour (7200 steps @ dt=0.5), demo, gates OFF vs all three junction gates ON
(`LongHorizonGridlockDiagTests`, `F3-SESSION-LOG.md` §9.58):

| Ladder item | gates OFF | all gates ON | Verdict |
| --- | --- | --- | --- |
| 4 · teleports | 0 | **0** | ✅ compliant (teleporting is disabled in the demo) |
| 1 · blockages prevented (stopped runs > 300 steps) | 161 | **0** | ✅ **now compliant** |
| 2 · unblock-by-overlap events needed | — | **0** | ✅ never needed — prevention sufficed |
| 3 · same-target-merge overlaps (two directions → one exit lane) | 4374 | **0** | ✅ **now compliant** |
| 3 · **same-lane overlaps during normal driving** | 492 | **696** | ❌ **VIOLATION — and worse** |

**The gates satisfy three of the four rungs.** The single outstanding violation is rung 3's same-lane case:
**696 events, worst penetration 1.800 m — exactly the vehicle width, i.e. two cars fully inside one
another** — occurring during ordinary driving, not as an unblock. That is categorically disallowed here.

→ The defect is the documented, still-open `NEED-colocated-vehicles.md` (two vehicles holding an
*identical pose* for 9 consecutive steps). It was previously parked as "independent"; under this
constraint it is **the top remaining believability defect** and the parking decision was wrong.

## ⚠ Open question, not yet measured

**How many of those 696 same-lane overlaps fall inside the high-realism pocket?** The pocket is a circle
(`HighRealismPocketX/Y`, `HighRealismPromoteRadius`) and every overlap event carries the two vehicles'
positions, so this is directly measurable — it just has not been done. It matters for urgency, not for the
verdict:

- if they cluster **inside** the pocket, this is the demo's most visible remaining flaw;
- if they occur almost entirely **outside** it, the violation is real but far less visible, and priority
  shifts accordingly.

Do not assume either answer. `LongHorizonGridlockDiagTests` already collects per-event positions, so adding
the classification is small.

## Consequences for existing recommendations

- **`TimeToTeleportSeconds = 300` is withdrawn** for the high-realism area — see the retraction in
  `NEED-livecity-teleport-safety-net-disabled.md`. It is rung 4.
- **⚠ `IgnoreJunctionBlockerSeconds = 5` is ALSO retracted as a general tool.** An earlier version of this
  document called it "a legitimate rung 2 tool". It is not, because **its trigger is elapsed waiting time,
  not physical overlap** (`Engine.cs` foe loop: `foe.WaitingTime >= IgnoreJunctionBlockerSeconds`). It
  therefore fires on cars that are merely *stuck* — rung 5, where a rescue is explicitly disallowed and
  actively harmful because it hides the real defect. It would only be admissible if re-gated on **measured
  overlap** of the two vehicles. It was **not needed** in the measured hour, so nothing depends on it.
  (It remains a faithful port of a real SUMO option and stays shipped at SUMO's own `-1` default; this is a
  statement about the *demo's* configuration, not about the port.)
- Any future "rescue" mechanism must be justified against this ladder: **prevention first**; a rescue that
  teleports is never acceptable here; and a rescue must be **triggered by measured physical overlap**, not
  by a timer, or it will fire on rung-5 cases and conceal the defect that caused them.
- **The 59 vehicles still stopped at the horizon with the gates on, and the documented
  `NEED-multilane-junction-passage.md` wedges, are rung-5 cases** — cars blocked with no obvious reason.
  Per this constraint they must be fixed at the cause, not rescued. (The 59 appear benign: all began in the
  final few hundred steps, i.e. ordinary queueing at the cut-off — but that is an observation, not a
  clearance.)
