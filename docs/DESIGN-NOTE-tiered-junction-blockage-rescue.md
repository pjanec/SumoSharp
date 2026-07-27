# DESIGN NOTE — tiered junction-blockage rescue (owner's idea, 2026-07-26)

**Status:** IDEA CAPTURED + assessed. **Not designed, not scheduled.** Recorded so it is not lost, with an
honest feasibility read and the one measurement that decides whether it is needed for the cases we actually
have.
**Origin:** owner, in response to `docs/NEED-arm5-mutual-junction-deadlock.md` (SUMO breaks a mutual
junction deadlock after 5 s via `JUNCTION_BLOCKAGE_TIME`; we have no equivalent).
**Related:** `NEED-arm5-mutual-junction-deadlock.md`, `docs/F3-SESSION-LOG.md` §6 T1.11.

---

## 1. The idea, as stated

A junction blockage should be rescued in **tiers**, differentiated by realism zone:

- **Low-realism areas:** SUMO's approach is fine — after ~5 s, just let the stuck cars go
  (`JUNCTION_BLOCKAGE_TIME`). Simple, cheap, and already the parity-faithful behaviour.
- **High-realism areas:** attempt something more lifelike first. Pedestrians are already ORCA in
  high-realism zones, so temporarily switch the *blocked cars* into ORCA mode too, let them physically
  avoid each other for a short while, then switch back to SUMO lane-based mode. Not guaranteed to succeed,
  but more realistic than teleport-or-release.
- Because this is a **special blockage-rescue mode**, it can carry more complex logic than the normal path —
  e.g. **cooperation** between the involved cars: two overlapping cars both attempt a short turn to unblock;
  then one **waits** while the other passes and opens a gap.
- **Trigger condition (the sharp part):** this applies **only when cars are physically overlapping
  slightly.** It must NOT apply to a car that is stuck for no visible reason and not overlapping anything.
- **Last resort:** if the ORCA attempt does not resolve it, "just let go" is acceptable even in a
  high-realism area.

## 2. Why the trigger condition is the best part of this

The overlap-vs-no-overlap split is exactly the right discriminator, and this session already has evidence
for why it matters:

- `__veh127` froze for **95 steps** inside a junction with `GapAhead = +Inf` **and** `NextMouthGap = +Inf` —
  nothing overlapping it, nothing ahead of it. That was the "stuck for no visible reason" class, and the
  correct fix was a **mis-gated predicate** (`NEED-contturn-stuck-in-junction.md`), not avoidance. An ORCA
  rescue there would have **masked a real engine bug** — and we would likely never have found it.
- So the trigger is not just a scoping nicety; it is what keeps a rescue mechanism from becoming a bug
  concealer. **Any rescue must be gated on a demonstrable geometric conflict.**

Corollary worth writing into whatever design follows: a "stuck but not overlapping" car should **raise a
diagnostic**, not be rescued. That is a bug signature, and we now have two confirmed instances of it.

## 3. Feasibility read

### Good news — more infrastructure exists than expected

`src/Sim.Core/Orca/OrcaCrowd.cs` is genuinely well-suited on several axes:

- **Deterministic and order-independent by construction.** Its header: *"a strict PLAN/EXECUTE double
  buffer — exactly the discipline the lane engine uses … That makes a step order-independent and trivially
  parallelisable, and keeps the result deterministic (fixed agent order, no RNG, no wall-clock)."* That
  satisfies `CLAUDE.md`'s determinism rule without new work.
- **The cross-regime bridge already exists in both directions.** ORCA exposes its agents as world discs via
  `ICrowdFootprintSource` (so lane vehicles avoid them) and consumes external discs via
  `SetExternalObstacles` (so its agents avoid vehicles). So "cars and ORCA agents mutually avoid" is not
  new — only "a *car* becomes an ORCA agent" is.
- Agents are `int`-indexed SoA with `Add(position, radius, maxSpeed, goal, velocity)` — no string ids, so
  adding/removing a handful of cars for a few seconds is cheap.

### The two real obstacles

**(a) Direction-of-data-flow — the architectural one.** The engine's authoritative state is lane-relative
(`Kinematics.Pos` along a lane); world `(x, y)` is a *derived output*. `src/Sim.Ingest/LaneGeometry.cs:7-9`
is explicit: *"Lane-relative (lane id, pos) stays the source of truth; this is purely an output-side
derivation and **must never feed back into the kinematic state**."*

Handing a car to ORCA and taking it back **inverts exactly that**: ORCA integrates in world space, so
re-entry means projecting `(x, y, heading)` back onto `(lane, pos, posLat)`. That is the load-bearing
design problem, not the avoidance maths. It needs:
- a well-defined re-entry projection (nearest lane of the *intended* route, clamped, with a legality check);
- a rule for what happens if the car ends up somewhere with no valid lane (mid-junction, wrong side);
- a guarantee the round-trip cannot teleport or duplicate the vehicle.

**(b) Cars are not discs, and are not holonomic.** `OrcaCrowd` is described as an *"open-space **holonomic**
crowd driver"* over `radius`-based discs. A car is a ~5.0 × 1.8 m oriented box that cannot translate
sideways and has a turning radius. Plain ORCA would slide a car laterally — visually worse than the freeze
it is fixing. This needs either a car-like (non-holonomic, box-footprint) variant, or velocity sampling
constrained to feasible car motions. That is a real piece of work, not a config change.

**(c) "Both try a short full turn" may be geometrically infeasible.** Two cars nose-to-nose in a junction
interior often cannot resolve without one **reversing** — which is what a real driver does. Our engine has
no reverse. So of the two halves of the proposal, **"one waits while the other passes and makes a gap" is
the workable half** (it is pure right-of-way sequencing, no new kinematics); "both turn to unblock" likely
needs reverse to be more than cosmetic. Worth prototyping the sequencing half first — it may be sufficient,
and it is far cheaper.

### Parity boundary (non-negotiable)

Any ORCA-mode-for-cars path must be **structurally unreachable** in parity scenarios: gated so no golden and
no `Sim.Bench` run can enter it, default-off, and — per this session's Lesson 1 — verified against the
**non-golden** behavioural scenarios too (the live-city demo and the five gridlock diagnostics), not just the
661 goldens. Note also that "realism zone" is presently more a config notion (`CooperativeLaneChange` = high
realism) than a spatial one; a genuinely *spatial* high-realism zone may need to be introduced first, or the
tier keyed off the existing config flag.

## 4. The measurement that decides whether this is needed *here*

The proposal triggers only on **physically overlapping** cars. Our one confirmed mutual deadlock is
vehicles **95** (`:2336_42_0`, pos 1.90) and **102** (`:2336_3_0`, pos 15.99) at junction 2336.

**MEASURED: they do NOT overlap. This is a stopped-at-a-distance mutual-yield case.**

| quantity | value |
| --- | --- |
| OBB penetration | **0.0000 m** (separating axis found) |
| box-to-box gap | **2.9866 m** clear |
| centre-to-centre | 8.2995 m |
| veh95 front bumper vs crossing point | **1.387 m short of it** |
| veh102 front bumper vs crossing point | **2.733 m short of it** |
| L × W | 5.000 × 1.800 m (`DEFAULT_VEHTYPE`) |

The lanes *do* genuinely cross (`JunctionConflict EgoLink=3, FoeLink=18`,
`CrossingPoint = (359.245, 619.732)`), but **both cars stopped short of the conflict point**, so there is no
geometric conflict to resolve — each is simply car-following the other at a distance.

⚠ This measurement also uncovered the **wrong-forward-axis bug** (see
`NEED-obb-anchor-halflength.md`): the committed convention reported a **false positive 0.328 m overlap** here.
The 0.0000 m figure is from the corrected, cross-validated basis.

- ~~If **overlapping** → this is precisely the ORCA-tier case.~~ **Not the case here.**
- **CONFIRMED: stopped at a distance** → the ORCA tier is **not triggered by any case we have measured**, and the
  correct fix for both known deadlocks is right-of-way arbitration (`isLeader()`) plus the 5 s blockage
  timeout. The ORCA tier would then be speculative machinery awaiting a demonstrated trigger — worth keeping
  as a recorded idea, not worth building yet.

Given this session's record (four hypotheses refuted by measurement), **this measurement should gate any
implementation work.**

## 5. Recommended sequencing, if it does go ahead

1. **Port the 5 s `JUNCTION_BLOCKAGE_TIME` escape first, unconditionally.** It is the parity-faithful
   baseline, it is small, and it is the "last resort" tier the proposal itself accepts. Everything else is an
   *optional upgrade layered above a working floor* — which also means a failed ORCA attempt degrades safely.
2. **Then `isLeader()`** — prevents the mutual state forming at all, and is independently required by T1.6.
   Between (1) and (2), most blockages should never reach a rescue tier.
3. **Only then, and only if the trigger measurably fires:** the cooperative-sequencing tier ("one waits,
   the other passes"). Pure sequencing, no new kinematics, no world-space round trip.
4. **Last:** true ORCA-mode-for-cars, which requires solving (a) re-entry projection and (b) non-holonomic
   box agents. Prototype behind a hard-off flag with a bounded duration and a mandatory fallback to (1).

The ordering matters because each step reduces how often the next one is needed, and (1)+(2) are both
SUMO-faithful ports we owe anyway.
