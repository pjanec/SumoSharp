# JUNCTION-REALISM — session journal (append-only)

**Purpose: survive interruption.** Every step gets a **BEFORE** entry (what I expect, and the exact
next command) written *and committed* before the work, and an **AFTER** entry with what actually
happened. If this session is compacted or dies, a fresh session reads the last entry and continues
without re-deriving anything.

**Read order for a fresh session:** this file's last entry → `JUNCTION-REALISM-TRACE-FINDINGS.md`
(what is established, §5 lists what is NOT) → `JUNCTION-APPROACH-ARM-{DESIGN,TASKS,TRACKER}.md`.

**Branch:** `claude/sumosharp-traffic-bugs-g1y9hl`.

---

## Entry 0 — state at the start of the ordered task run

**Banked and pushed.** Repro net (`scenarios/_diag/junction-realism-L{1,2}{,-light}`) + generator;
two instruments (`scripts/analyze-junction-realism-fcd.py`, `scripts/run-net-regression.py`) + a
26-net baseline; root cause traced to the omitted `myInternalLinkFoes` half of
`MSLink::setRequestInformation`; T1 (parse) + T3/T4 (the arm) implemented, parity **776/4**, all 661
goldens byte-identical; three hypotheses tested, two refuted and recorded.

**Best measured configuration** (`arm ON, occ OFF`), repro L2: arrived **139 → 320**, overlap events
**12 751 → 313** (−97.5%). `Engine.InternalJunctionApproachArm` is nonetheless **default OFF** — one
committed net (`city-organic`) regresses, and that is task 1.

**Not fixed, and no measurement has moved it: the GRIDLOCK.** Best cell still leaves 130 (L2) / 226
(L1) vehicles permanently stopped against SUMO's 0. It is a *separate* defect from the overlaps.

**Agreed task order** (owner, this session):
1. `city-organic` residual — the only blocker on shipping the arm default-ON.
2. The gridlock — the main event. Clean surface is **L1**, which the arm does not help.
3. Stopped lane-change — minimal repro + the 61-vs-17 frequency finding.
4. Pedestrians as wedge amplifier — needs a `LiveCitySim` harness; held until 2 has a mechanism.

**Open proposal, NOT actioned (outside the task order).** The hand-copied gate numbers (`775/4`, the
bench hash) have rotted — docs say 775/4, measured baseline is 773/4. Do **not** simply delete them:
a test count catches tests that silently stop running, which a green run does not, and the bench hash
catches drift no assertion covers. Instead **single-source** them the way `EnvGateDocumentationTests`
already single-sources the env-gate table: one committed value, an executable assertion, and every
prose copy replaced by a pointer. A rotted tripwire is worse than none — it makes a correct run look
wrong and trains readers to discount it (the `F3-SESSION-LOG.md` §7.9 lesson).

---

## Entry 1 — BEFORE — task 1: trace the `city-organic` overlap residual

**The question.** With the arm ON, `city-organic` junction-interior overlap **events** rise 255 → 296
(+16%) and peak simultaneous pairs 3 → 5, while the same arm cuts the repro's events by 97.5%. Why
does one real net get worse?

**What I expect (recording it so a wrong prediction is visible, not quietly revised).** The arm holds
vehicles stationary in cont bays; a held vehicle is then a stationary target for cross traffic that has
no physical-occupancy check against it. I expect the *new* pairs to be predominantly
**one-stopped-one-moving**, with the stopped vehicle on a bay lane. Prior partial evidence: the arm-ON
dump already showed `veh165 [:359_11_0] spd=0.00 x veh212 [:359_10_0] spd=10.25`.

⚠ **This expectation has a bad track record in this repo and must not be assumed.** The alternative,
which the data must be allowed to show, is that the extra overlaps are *both-moving* transient
crossings on internal lanes that overlap by construction — in which case they are largely cosmetic,
SUMO has them too, and the "regression" is a metric artifact rather than a behaviour change.

**Immediate next step (exact commands).**
```
for A in 0 1; do
  SUMOSHARP_APPROACHARM=$A SUMOSHARP_PHYSOCC=0 dotnet run --project src/Sim.Run -c Release --no-build -- \
    scenarios/_bench/city-organic --parity --steps 1200 --fcd-out <tmp>/org-arm$A.fcd.xml
done
```
then diff the overlapping-pair SETS between arms (not just the counts) to isolate pairs present ONLY
with the arm on, and classify each by (both stopped / one stopped / both moving) and by whether the
stopped vehicle sits on a cont bay lane.

**Decision this unblocks.** If the new pairs are held-vehicle-driven-through, the fix is a zone-scoped
occupancy guard (global is already refuted four times over — §9) and the arm can ship default-ON
outside the zone. If they are both-moving transients, the metric is over-reporting and the arm ships
default-ON as-is.

**Definition of done for task 1:** the classification table, a named cause, and either the arm's
default flipped or a written reason it cannot be.
