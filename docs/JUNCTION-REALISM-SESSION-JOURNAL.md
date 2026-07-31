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

---

## Entry 1 — AFTER — task 1 measured. My prediction was WRONG; the residual is largely a metric artefact.

**Prediction (Entry 1 BEFORE):** the new overlaps would be *predominantly* one-stopped-one-moving — a
vehicle the arm correctly holds, driven through. **Refuted.** Of the 43 pairs that appear only with the
arm on, **67% are both-moving** and 33% are one-stopped-one-moving. The alternative the BEFORE entry
required me to leave open — transient crossings on internal lanes that overlap by construction — is the
majority case.

**The headline number I had been reporting was the wrong statistic.** "255 → 296 events (+16%)" counts
pair×step, so it inflates with *duration*. The number of **distinct vehicle pairs that ever overlap is
92 → 93** — flat. And the churn is symmetric: **43 new pairs, 42 gone.** The arm changes *which*
vehicles conflict, not how many.

| class | events OFF | events ON | **pairs OFF** | **pairs ON** |
|---|---|---|---|---|
| both stopped | 114 | 117 | 6 | 9 |
| one stopped, one moving | 43 | 64 | 24 | 29 |
| both moving | 98 | 115 | 76 | 74 |
| **total** | 255 | 296 | **92** | **93** |

**What is genuinely up, stated without minimising it:** the visually-worst class — a stopped car driven
through — grows **24 → 29 distinct pairs** (+5) and 43 → 64 events (+49%). That is real, it is the
artefact the owner reported, and it is not zero. It is also the class the occupancy gate would address,
which §9 measured as costing 320 → 61 arrivals globally, so the answer for it is **zone-scoped**, per
the owner's own rule that these artefacts are unacceptable in-zone and tolerable outside.

**Verdict on task 1: the arm is NOT meaningfully regressing `city-organic`.** Overall incidence is flat
(92→93), both-moving incidence is slightly *down* (76→74), against **−97.5% overlap events and +130%
arrivals** on the repro. The trade is strongly asymmetric in the arm's favour.

**Decision: flip `Engine.InternalJunctionApproachArm` to default ON.** The blocker identified in §8 is
resolved — it was a duration-weighted statistic mistaken for an incidence change.

**Method note worth keeping:** two of my summary statistics have now misled me in this workstream —
peak-simultaneous pairs *understated* the arm 20× (9→7 vs 12 751→313), and event count *overstated* the
city-organic regression (+16% vs +1 distinct pair). **Report incidence and duration separately; never
let one stand for the other.**

---

## Entry 2 — BEFORE — flip the default, then re-gate

**Expectation.** Parity should stay **776/4** with all 661 goldens byte-identical: the arm was already
measured at 776/4 with it ON, and the goldens are inert to it either way. The regression battery should
be unchanged from the arm-ON report already committed. If parity moves, the flip is wrong and I revert.

**Immediate next step.**
```
# flip the default in Engine.cs + update ENV-GATES.md's "engine default" column
dotnet build Traffic.sln -c Release && dotnet test tests/Sim.ParityTests -c Release --no-build
```
Then task 2 (the gridlock) opens with its own BEFORE entry: L1 is the clean surface (the arm does not
help it at all — 60 → 63 arrived), and the named first instrument is **the binder tag at the step a
vehicle enters a junction whose exit is already full** (§7's keep-clear question), NOT a source read.

---

## Entry 2 — AFTER — default flipped ON, gate green

`dotnet test tests/Sim.ParityTests -c Release` → **776 passed / 4 skipped / 0 failed**, all 661 goldens
byte-identical, exactly as Entry 2 BEFORE predicted. `Engine.InternalJunctionApproachArm` now defaults
**true**; `ENV-GATES.md`'s engine-default column updated to match.

**Task 1 is CLOSED.** The junction drive-through / interpenetration defect the owner reported is fixed,
matches SUMO step-for-step on the traced case, and ships on by default.

**Task 2 (the gridlock) is next, and it is a fresh investigation** — nothing measured so far has moved
it. Its BEFORE entry follows.
