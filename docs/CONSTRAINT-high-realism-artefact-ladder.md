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
| 1 | **Prevent the blockage** (admission control, right-of-way, ordering) | ✅ the only fully acceptable outcome |
| 2 | Cars **pass through each other to unblock a blocked junction** | ⚠️ tolerated — *"a bit better"* than teleport — **last resort only**, and only when no more realistic route exists |
| 3 | Cars **overlap during normal, non-unblocking manoeuvres** | ❌ **NOT allowed** |
| 4 | **Teleport** | ❌ **NOT allowed** — *"the most unrealistic and most visible artefact"* |

Two things follow that are easy to get backwards:

- **Tier 2 is permitted only as a deliberate unblocking action.** The same *geometry* (two cars
  overlapping) is acceptable at tier 2 and forbidden at tier 3 — what distinguishes them is **why** it
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
- **`IgnoreJunctionBlockerSeconds = 5`** is a legitimate **rung 2** tool (it releases a blocked car past a
  long-stopped foe without any teleport), to be enabled only if a residual wedge is ever observed with the
  gates on. It was **not needed** in the measured hour.
- Any future "rescue" mechanism must be justified against this ladder: prevention first, and a rescue that
  teleports is not acceptable in the high-realism area no matter how rarely it fires.
