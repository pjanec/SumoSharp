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

## Current compliance, measured — ALL GATES ON (supersedes earlier intermediate tables)

Full hour (7200 steps @ dt=0.5), demo, shipped default (all gates OFF) vs all gates ON. Deterministic:
two consecutive runs identical. Pocket centre **(2351.1, 2363.2)**, promote radius **70 m**.
Source: `LongHorizonGridlockDiagTests`, `F3-SESSION-LOG.md` §9.86.

| Rung | Violation class | OFF | OFF in-zone | ALL ON | ON in-zone | Verdict |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | blockages (stopped runs > 300 steps) | 161 | — | **0** | — | ✅ **compliant** |
| 2 | unblock-by-overlap events needed | — | — | **0** | — | ✅ never needed |
| 3 | same-target merge (2 dirs → 1 exit lane) | 4374 | 0 | **0** | **0** | ✅ **compliant** |
| 3 | same-lane overlap (normal driving) | 492 | 28 | **327** | **12** | ❌ **still violating** (but better than baseline) |
| 2/3 | fully co-located (pen ≥ vehicle width) | 83015 | 17015 | **330** | **13** | ⬇ −99.6% / −99.9% in-zone |
| 4 | teleports | 0 | — | **0** | — | ✅ compliant |
| 5 | stopped to horizon, no obvious reason | 156 | — | **52** | — | ⚠️ −67%, not cleared |

Worst penetration: **3.137 → 2.951 m** overall, **2.685 → 2.123 m** in-zone — both better.
Completed trips **1295 → 2684 (+107%)**.

**Four of five rungs compliant, achieved by rung 1 (prevention)** — zero teleports, zero pass-through.
**Nothing in the measured set is worse.** Earlier tables in this document showed same-lane 492 → 696 and
in-zone 28 → 115 as a regression; those were **intermediate states with only the three junction gates on**,
and fixes 1–3 reversed them. In-zone same-lane is now **better than the shipped baseline**.

**The one remaining violation** is rung 3's same-lane case: 327 events, 72 onsets, longest episode 64 steps
(~32 s), worst 1.800 m = exactly vehicle width. Its **cause is currently unknown** — the pre-fix-1
attribution no longer describes the surviving population, so the next step is re-attribution, not another
mechanism (`NEED-same-step-double-placement-colocation.md`, `F3-SESSION-LOG.md` §9.83/§9.87).

## Compliance at 3x DESIGN DENSITY (480 cars) — added session 4

The table above is the **design density** (160 cars). At **3x** the ladder was being violated at rung 1 in a
way the 1x measurement could not see, and it has now largely been fixed. One-variable A/B, all other gates
ON, 7200 steps, from `HeadOfQueueStallProbeTests`:

| Rung | Violation class | gates OFF | ON, entry-order OFF | **ON, entry-order ON** |
| --- | --- | --- | --- | --- |
| 1 | peak concurrent blockages (stopped > 300 steps) | 469 | 220 | **17** |
| 1 | stall **HEADS** (the cars actually stuck) | 57 | 39 | **7** |
| 1 | longest single blockage | — | **4890 steps** (≈ never moves) | **637 steps** |
| — | completed trips | 1583 | 3426 | **5381** |
| 4 | teleports | 0 | 0 | **0** |

**The 4890-step case was the worst rung-1 violation this project has measured** — four cars motionless in
four cont bays of junction `d_5_4` for essentially the entire simulated hour. It was **self-inflicted**: the
internal-junction admission gate blocked on bare foe-lane occupancy, which is symmetric, so a cycle of
mutually-conflicting bays had no way to resolve. Restoring SUMO's entry-time ordering fixed it
(`F3-SESSION-LOG.md` §9.110-114).

**It was fixed at rung 1 — prevention — with zero teleports and zero pass-through**, which is the only
acceptable general solution per the ladder. Nothing was concealed: no timer-triggered rescue, no overlap
permission, no teleport.

**Still open, and it is a rung-5 case:** **9** cars per simulated hour still stop on a cont bay for up to
637 steps. Per rung 5 these must be fixed at the cause and must **not** be rescued. The structural argument
(`F3-SESSION-LOG.md` §9.115) is that they are blocked by a foe standing on a plain internal lane which SUMO
would ignore once it has passed the conflict point — i.e. missing conflict-point geometry, a conservatism
rather than a deadlock. **That is an argument, not a measurement**, and the measurement is §6's next step.

**⚠ CAVEAT on every in-zone figure:** the pocket is **camera-driven**; the headless diagnostic places it at
the net centre. A viewer moves the camera, so any part of the city can become high-realism — these columns
describe *one placement, not a bound*, and out-of-zone violations still matter.

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
