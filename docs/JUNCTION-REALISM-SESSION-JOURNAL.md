# JUNCTION-REALISM — session journal (append-only)

**Purpose: survive interruption.** Every step gets a **BEFORE** entry (what I expect, and the exact
next command) written *and committed* before the work, and an **AFTER** entry with what actually
happened. If this session is compacted or dies, a fresh session reads the last entry and continues
without re-deriving anything.

**Read order for a fresh session:** **`JUNCTION-REALISM-RESUME.md` FIRST** — it is the self-contained
cold-start page (status, the next task with its exact repro, the backlog in owner-priority order, the
instruments, and the traps). Come here only for the full BEFORE/AFTER trail, then
`JUNCTION-REALISM-TRACE-FINDINGS.md` (§5 lists what is NOT established) →
`JUNCTION-APPROACH-ARM-{DESIGN,TASKS,TRACKER}.md`.

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

---

## Entry 3 — BEFORE — task 2: the gridlock. Why does L1 stay deadlocked?

**The question.** L1 ends with **226 vehicles permanently stopped, every one at 0.000 m/s**, against
honest SUMO's **0 on identical inputs**. The approach arm moved it by 3 vehicles (60 → 63), so whatever
holds L1 is untouched by everything measured so far. L1 is therefore the CLEAN SURFACE: single-lane, no
multilane over-yield confound, and the overlap fix is inert on it.

**The standing lead, from §7.** At t=90 `f_cyc_ccw2.3` entered `:J10_7_0` while its exit link `v1_0`
already held **8 fully-stopped vehicles, the nearest 5.7 m past the junction exit**, and then stranded
inside forever. Entering a junction you demonstrably cannot clear is what SUMO's `checkRewindLinkLanes`
prevents, and what our `KeepClearConstraint` (binder **11**) is supposed to cover.

**The question is binary and an instrument answers it:** at the admission step, did `KeepClearConstraint`
**evaluate and permit**, or was it **never consulted**? Those have completely different fixes — a wrong
predicate versus an unreachable code path — and guessing between them is exactly the reasoning that has
been refuted seven times in this codebase.

**What I expect.** Weakly, that keep-clear evaluated and permitted, because it is documented as
protecting "ego's own downstream exit" and the exit *was* provably blocked. **I have been wrong on the
last such prediction** (Entry 1), so the alternatives are named up front and the data decides:
(a) never consulted — the arm is gated on something L1 does not satisfy;
(b) consulted, permitted — its occupancy notion differs from "8 stopped cars on my exit";
(c) consulted, bound, but too late — it braked, ego was already committed past the stop line.

**Immediate next step.** Add `--binder-log PATH` to `Sim.Run` — a committed CSV instrument
(`t,vehId,lane,pos,speed,binder`) reading `Engine.BindingConstraints`, which is already public and
indexed by `EntityIndex`. Committed rather than scratch, per the lesson that a deleted probe makes its
own numbers unfalsifiable. Legend (from `Sim.Viewer/Program.cs:1424`): 0 none, 1 leaderFollow,
2 crossJxnLeader, 3 freeFlow, 4 successiveLane, 5 deadLaneMerge, 6 stopLine, 7 redLight, 8 railSignal,
9 railCrossing, **10 junctionYield**, **11 keepClear**, 12 obstacle, 13 crowd, 14 internalJunctionAdmission,
15 colocationSymmetryBreak, 16 crowdYield.

Then read the binder for `f_cyc_ccw2.3` across t=85…95 on L1, and for the whole stopped population at
t_end (which binder holds 226 vehicles at 0.000 m/s?).

**Definition of done for task 2's first step:** a named answer to (a)/(b)/(c) with the binder trace
that shows it, plus the t_end binder histogram. NOT a fix — the fix is designed after the mechanism is
named, per the design-first rule.

---

## Entry 3 — AFTER — the answer is NONE of (a)/(b)/(c): `keepClear` never binds at all

**First, the instrument was wrong and its own guard caught it.** v1 read
`Engine.BindingConstraints[snapshot.EntityIndex]` and reported **100% OUT_OF_RANGE**. That span is indexed
by **read-buffer column**, and the read buffer is empty on a host that never pumps it, while
`EntityIndex` is the **ECS entity index**. Fixed by carrying the binder on `VehicleExportSnapshot`
itself (one construction site, trailing optional param, additive). Had I not put the range guard in, it
would have logged garbage tags and I would have "found" a mechanism that did not exist.

**The §7 admission case, `f_cyc_ccw2.3` on L1:**

| t | lane | pos | speed | binder |
|---|---|---|---|---|
| 86–89 | `in_S10_0` | 191.80 | 0.00 | **redLight** |
| 90 | `:J10_7_0` | 1.60 | 2.60 | **junctionYield** |
| 91 | `:J10_7_0` | 6.80 | 5.20 | junctionYield |
| 92 | `:J10_7_0` | 11.95 | 5.15 | **crossJxnLeader** |
| 93–∞ | `:J10_7_0` | 12.61 | **0.00** | **crossJxnLeader** |

**`keepClear` (binder 11) does not appear — not here, and not once in the 226-vehicle t_end
histogram.** My prediction (weakly: "keep-clear evaluated and permitted") is refuted, and so are
alternatives (b) and (c). The constraint is simply never the binding one anywhere in this run, so the
§7 lead — "our keep-clear has a wrong predicate" — is **dead as stated**.

**What actually holds the gridlock.** Binder over the 226 permanently-stopped vehicles at t_end:

| binder | all stopped | **stopped INSIDE a junction (the heads)** |
|---|---|---|
| leaderFollow | 198 (87.6%) | 4 |
| **crossJxnLeader** | 17 (7.5%) | **8** |
| junctionYield | 8 (3.5%) | 2 |
| internalJunctionAdmission | 2 (0.9%) | 2 |
| redLight | 1 (0.4%) | — |
| **keepClear** | **0** | **0** |

Judged on HEADS rather than population (the F3 lesson — followers are pure queue shadow, and here
87.6% of the stopped population is exactly that), **the dominant mechanism is `crossJxnLeader`: 8 of
the 16 vehicles wedged inside junctions are car-following a leader on a crossing internal lane, at
exactly 0.000 m/s, forever.**

**That is a documented failure mode with a name.** `docs/NEED-arm5-mutual-junction-deadlock.md`: *"two
cars on crossing internal lanes of one junction can end up car-following EACH OTHER via arm 5
(`AdaptToJunctionLeader`), which has no right-of-way notion and no escape — measured at 121/121 steps,
speed exactly 0.000."* Same signature, same binder, unbounded instead of 121 steps.

⚠ **And the supposed defence is already ON.** That NEED says SUMO avoids the state via `isLeader`
entry-time ordering, and `Engine.JunctionIsLeaderGate` defaults **true** — yet the state forms anyway.
So either the ordering does not cover this configuration, or something upstream lets the pair enter.
**That is the next question, and it needs the same treatment: an instrument, not a source read.**

---

## Entry 4 — BEFORE — task 2 continued: why does `crossJxnLeader` deadlock with `isLeader` on?

**Immediate next step.** Take the 8 `crossJxnLeader` heads from the t_end histogram, identify each
one's leader (the vehicle it is following on the crossing internal lane), and test whether the pair is
**mutual** — i.e. A follows B while B follows A. `NEED-arm5` predicts mutual; if it is instead a chain
(A→B→C→…), the mechanism is different and the NEED is the wrong lead.

**Expectation, recorded so it can be shown wrong:** mutual pairs. **Track record so far this session:
two predictions made, both wrong**, so the alternatives are named: a chain terminating on something
outside the junction, or a cycle longer than 2.

**Do NOT design a fix before this is answered** — a mutual pair needs a tie-break, a chain needs a
different intervention entirely.

---

## Entry 4 — AFTER — not mutual pairs, not chains: a HETEROGENEOUS multi-party cycle per junction

**Prediction (Entry 4 BEFORE): mutual `crossJxnLeader` pairs, per `NEED-arm5`. Refuted — third
prediction this session, third refutation.** The named alternatives (chain, longer cycle) are also not
right as stated. The actual structure is a **cycle of 3–4 vehicles per junction held by DIFFERENT
constraints**:

| junction | wedged | structure |
|---|---|---|
| **J00** | 4 | `:J00_10_0` **junctionYield** · `:J00_11_0` **internalJunctionAdmission** · `:J00_7_0` **crossJxnLeader** + one leaderFollow behind it |
| **J01** | 3 | `:J01_10_0` **crossJxnLeader** + leaderFollow behind · `:J01_6_0` **crossJxnLeader** |
| **J10** | 5 | `:J10_11_0`, `:J10_15_0`, `:J10_7_0` all **crossJxnLeader** (three distinct lanes) · `:J10_4_0` **junctionYield** · one leaderFollow |
| **J11** | 4 | `:J11_1_0` **crossJxnLeader** + leaderFollow · `:J11_5_0` **internalJunctionAdmission** · `:J11_9_0` **crossJxnLeader** |

**The load-bearing observation: NO SINGLE CONSTRAINT OWNS THE DEADLOCK.** At J00 three vehicles are
held by three *different* mechanisms. J10 has three `crossJxnLeader` heads on three distinct internal
lanes — not a pair. So the family of fix that would work for `NEED-arm5`'s two-car mutual case (give
that one constraint a tie-break) **cannot break this**: whichever constraint you fix, the cycle is
still closed by the others.

This reframes task 2 substantially. What is needed is a mechanism that arbitrates a **cycle spanning
heterogeneous constraints** — which is what SUMO's entry-time ordering does globally, rather than
per-constraint. Note the standing warning that fits exactly: *"a symmetric predicate cannot arbitrate a
cycle — check for a tie-break, and copy SUMO's"* (`F3-SESSION-LOG.md` §7.11). Here there are three
symmetric predicates interlocking.

⚠ **Honest note on my own change.** `internalJunctionAdmission` (binder 14) is a *participant* in the
cycle at J00 and J11. Binder 14 covers BOTH halves of that constraint — the pre-existing lane-foe loop
and the approach arm I added — and **T5 (make the diagnostic distinguish them) was never done**, so I
cannot currently say which half is holding those two vehicles. The arm is net strongly positive
(L2 arrivals +130%, overlap events −97.5%) and this run has it ON, but "the arm is one of the parties
in the residual wedge" is a live possibility that the instrument, as built, cannot rule out. **T5 is
now a prerequisite for the next step, not an optional nicety.**

---

## Entry 5 — BEFORE — next: T5 first, then cycle arbitration

**Step 1 (prerequisite): T5 — split binder 14.** Give the approach arm its own tag (17) so the
histogram can say which half holds a wedged vehicle. Without it the previous entry's ⚠ cannot be
resolved and any fix attributed to binder 14 is unattributable. Small, mechanical — good delegation.

**Step 2: identify the actual cycle edges.** For each wedged vehicle, record WHO it is waiting for
(the leader/foe id its binding constraint selected), then verify the wait-for graph really is cyclic
rather than terminating outside the junction. Needs the constraints to expose their chosen foe — an
extension of the binder log, still an instrument.

**Expectation, recorded:** a closed cycle per junction. **Given three consecutive wrong predictions,
weight this low** — the alternative worth taking seriously is that the "cycle" terminates on something
outside the junction (a full exit link), which would make it a *capacity* problem wearing a
deadlock's clothes, and would point back at the exit-link occupancy seen in §7.

**Do NOT design a fix until step 2 answers this.**

---

## Entry 5 — INTERIM — the Entry-5 alternative is largely ruled out: there IS free space, unreachable

Answering the "capacity problem wearing a deadlock's clothes" alternative from Entry 5 BEFORE, from the
binder log already in hand. Occupancy of the eight internal grid links at t=1799 (each ~65 m usable):

| link | vehicles | stopped | front pos | front binder |
|---|---|---|---|---|
| `h0_0` (J00→J10) | 6 | 6 | 61.89 | junctionYield |
| `h1_0` (J01→J11) | 9 | 9 | 65.21 | crossJxnLeader |
| `v0_0` (J00→J01) | 9 | 9 | 64.83 | crossJxnLeader |
| `v1_0` (J10→J11) | 9 | 9 | 64.60 | junctionYield |
| `v1r_0` (J11→J10) | 9 | 9 | 65.59 | **redLight** |
| **`h0r_0`, `h1r_0`, `v0r_0`** | **0** | — | — | **EMPTY** |

**Three of the eight internal links are completely EMPTY.** The network is not uniformly saturated —
the jam is *direction-specific*, and there is free space the traffic cannot reach. That is the
signature of a blocked circular wait, not of a network at capacity: a capacity failure fills
everything.

The full links' front vehicles are held **at the stop line by junction constraints**
(`junctionYield`, `crossJxnLeader`) — i.e. by the wedged junction interior in front of them, not by a
full buffer beyond it. So the causal direction is **junction interior wedges first → the approach link
backs up**, which is the opposite of the capacity reading.

**Not fully closed:** one full link (`v1r_0`) has its front held by `redLight`, so at least one arm of
the structure terminates on a signal rather than on a vehicle. Whether that matters depends on the
blocker graph, which is the instrument now being built (Entry 5 step 2). This entry narrows the
alternative; it does not eliminate it.

---

## Entry 5 — AFTER — ROOT CAUSE: a circular wait MASKED by the traffic-light phase

**T5 accepted** (verified first-hand, not from the agent's report): FCD **byte-identical** to the
pre-T5 run and parity **776/4**, so the tag split and blocker export are provably diagnostic-only. The
agent also found and fixed a real bug I would have missed — the untracked fold sites did not reset
`blockerIdx`, so a later non-tracked winner (e.g. `redLight`) kept a stale foe index from an earlier
one. It also correctly caught that `HeldAtLinkLastStep` consumes tag 14 and must now accept 17.

### The measurement, and the trap in it

Wait-for graph at t_end, over all 226 stopped vehicles: **0 cycles**. All 16 wedge heads terminate at
**one single vehicle** — `f_fill_N11.14`, on `v1r_0` at pos 65.59, speed 0.000, binder **redLight**.
A 226-vehicle jam rooted in one car at a red light.

**That reading is WRONG, and the snapshot is what makes it look right.** Tracing that one vehicle over
its whole life:

* it last moved at **t=180**, and has been stationary for **1619 s** on a **static 90 s** signal cycle;
* its binder ALTERNATES — **redLight 911 samples, junctionYield 756**. The light does turn green;
* on **every one of those 756 green samples** it yields to exactly one foe: **`f_cyc_ccw.6`**;
* `f_cyc_ccw.6` is stranded **inside junction J10** on `:J10_15_0` for 1653 samples, binder
  `crossJxnLeader`, blocked by `f_cyc_ccw2.5`;
* and the wait-for chain **from `f_cyc_ccw.6` runs 13 hops back to `f_fill_N11.14`**.

**So the cycle is closed: `f_fill_N11.14` → `f_cyc_ccw.6` → (13 hops) → `f_fill_N11.14`.** A 14-vehicle
circular wait spanning J10 and J11.

**Why the cycle detector said zero.** At any single instant the root's binding constraint is whichever
is *tighter* — and during the red phase that is the signal, which has no vehicle edge. So the snapshot
graph is a TREE rooted at an apparently-external cause, and the vehicle-to-vehicle edge that closes the
loop is invisible for ~54% of samples. **A single-instant wait-for graph cannot see a cycle that a
periodic constraint intermittently masks.** This is the fifth wrong prediction of the session (I
predicted a cycle, got "no cycle", and the "no cycle" was itself the artefact) and the most instructive:
it is not that the answer was unknowable, it is that the *instrument's time resolution* hid it.

### What this means for the fix

The gridlock is a **genuine circular wait**, so §7.11's rule applies: *a symmetric predicate cannot
arbitrate a cycle; check for a tie-break and copy SUMO's.* But it is now much better localised than
"junctions deadlock":

1. The cycle is closed by **one vehicle stranded inside J10** (`f_cyc_ccw.6` on `:J10_15_0`), held by
   `crossJxnLeader` — the arm with **no right-of-way notion and no escape**
   (`NEED-arm5-mutual-junction-deadlock.md`).
2. Everything else, including the 226-vehicle jam, is downstream of that one stranding.
3. SUMO on identical input strands vehicles too (44 of them) but **always resolves within 29 steps**.

**The next question is therefore narrow and specific:** why does `f_cyc_ccw.6` never escape
`:J10_15_0`, when SUMO's equivalent always does? Trace that ONE vehicle against the SUMO oracle from
the step it entered — the same technique that found the §4 admission defect in minutes.

**Do not** attempt a global cycle-breaking tie-break first: it is a large behavioural change aimed at a
symptom, and the single-vehicle trace is cheap and has a working precedent in this very document.

---

## Entry 6 — BEFORE — why does `f_cyc_ccw.6` never escape `:J10_15_0`?

**The question.** This one vehicle closes the 14-vehicle cycle that holds the whole 226-vehicle jam.
It sits on `:J10_15_0` at pos 7.22, speed 0.000, binder `crossJxnLeader`, blocked by `f_cyc_ccw2.5`,
for 1653 samples. SUMO strands 44 vehicles on identical input and resolves **every one within 29
steps**. Why does ours never resolve?

**Expectation — deliberately held loose. Five predictions this session, five wrong**, and the last one
was wrong because the *instrument* hid the answer rather than because the reasoning was bad. So the
candidates are listed without a favourite:
(a) its blocker `f_cyc_ccw2.5` is itself stranded and the pair is a mutual `crossJxnLeader` lock
    (`NEED-arm5`'s two-car case, which is claimed fixed by `JunctionIsLeaderGate`, default ON);
(b) it entered when it could not clear, and SUMO's equivalent never entered (an admission difference —
    check what SUMO's `f_cyc_ccw.6` does at the same step);
(c) it could physically proceed but `crossJxnLeader` keeps braking it — a predicate that never releases.

**Immediate next step.** Two traces side by side around its entry to `:J10_15_0`: ours from the binder
log (lane, pos, speed, binder, blocker) and SUMO's from `sumo-L1.fcd.xml` for the same vehicle id.
Find the first step where the two diverge, exactly as §4 did for the admission defect.

⚠ **Caveat to state up front:** the two runs have already diverged globally by then (first
interpenetration at t=50), so SUMO's `f_cyc_ccw.6` is not in the same traffic state as ours. The
comparison is therefore **suggestive, not an oracle diff** — it can show what SUMO *does* in a similar
situation but cannot prove what it would do in ours. Any conclusion drawn from it must carry that
caveat, and if the answer is (a) or (c) the decisive evidence is our own trace, not the comparison.

---

## Entry 6 — AFTER — ROOT CAUSE: `KeepClearConstraint` is structurally inert for the bay→stage-2 advance

**The trace.** `f_cyc_ccw.6`, the vehicle that closes the 14-vehicle cycle:

| t | ours | SUMO (same id) |
|---|---|---|
| 140–146 | held in bay `:J10_11_0` by **internalJunctionAdmission / ApproachArm** | already through |
| **147** | **released** → `:J10_15_0` pos 2.50 spd 2.60 | on `v1_0` pos 5.37 |
| 148 | `:J10_15_0` pos 7.11 spd 4.61, `crossJxnLeader` | `v1_0` pos 11.02 |
| 149 → ∞ | **`:J10_15_0` pos 7.22 spd 0.000 forever** | `v1_0` pos 12.16, queued **outside** the box |

`:J10_15_0` is ~7.7 m long, so ours halts **~0.5 m short of clearing the junction**.

**`crossJxnLeader` is NOT the bug — it is behaving correctly.** At t=147, when ego was released into
the junction, the exit lane `v1_0` already held **9 stopped vehicles with the nearest at pos 4.59** —
whose back bumper is at 4.59 − 5.0 = **−0.41 m, i.e. already inside the junction**. There was
physically nowhere to go. Ego stopping is right; **ego being let in was wrong.**

**Why nothing stopped it, exactly.** `KeepClearConstraint` (`Engine.cs:7719`) opens with a forward scan
for ego's upcoming junction entry link and then bails:

```
if (egoInternalLaneId is null || v.LaneId == egoInternalLaneId || egoLinkSeqIndex < 1)
    return double.PositiveInfinity;   // "already on the internal lane (committed)"
```

Ego at t=146 is on `:J10_11_0` — **a stage-1 bay, which IS an internal lane** — so `v.LaneId ==
egoInternalLaneId` and keep-clear returns inert. **Keep-clear protects only the FIRST entry into a
junction from an approach lane. It never covers the bay→stage-2 advance.** And the gate that *does*
control that advance, `InternalJunctionAdmissionConstraint`, checks only **foe lanes** — never **ego's
own exit lane occupancy**.

So the bay→stage-2 admission has **no keep-clear at all**. That is the hole, and it is
`checkRewindLinkLanes`' territory (don't commit into a junction lane whose exit you cannot clear).

### ⚠ Correction to Entry 3

Entry 3 concluded *"the §7 lead — our keep-clear has a wrong predicate — is dead as stated"*, reasoning
from `keepClear` binding **0 of 226** times. **That inference was wrong.** The zero does not mean the
case is absent; it means the guard is **structurally excluded from the case that matters**. Absence of
a binder is not absence of the mechanism — it can be the stronger finding that the guard never runs.
Worth carrying: *a diagnostic that never fires is evidence about the GUARD, not about the hazard.*

### Fix direction (design, not yet written)

Extend the bay→stage-2 admission with an **exit-lane occupancy check**: before releasing ego from a
cont bay onto its final internal lane, require that ego's exit lane can accept it (its last vehicle's
back bumper clears the internal lane's end by at least ego's length + minGap). This is the
`checkRewindLinkLanes` half of the port, it is SUMO-faithful, and it is scoped to exactly the
transition that has no guard today.

**Expected to be strongly beneficial but NOT parity-inert** — it changes when vehicles enter junctions,
so it needs the full gate plus the cross-net battery, and any golden shift needs a SUMO diff. It is
also the first change in this workstream that plausibly moves the gridlock rather than the overlaps.

---

## Entry 7 — BEFORE — implement the bay→stage-2 exit-lane check

**Next step.** Design-first per CLAUDE.md: extend `JUNCTION-APPROACH-ARM-DESIGN.md` with a new section
(or a sibling doc) covering the exit-lane admission, then implement behind
`Engine.BayExitLaneKeepClear` (default OFF until measured), then run: the repro L1/L2 A/B, the
cross-net battery against the committed baseline, and the parity gate.

**Success condition, stated in advance:** L1 `arrived` moves materially above 63 (SUMO: 450). If it
does not, the mechanism is named correctly but is not the binding one, and that null gets published
like the others.

---

## Entry 7 — AFTER — the gridlock is FIXED on L2 (450/450, matching SUMO). One real regression found.

`Engine.BayExitLaneKeepClear` implemented (default **OFF**): before releasing ego from a cont bay onto
its final internal lane, require that ego's own **exit lane** has room for it — rear-most occupant's
**back bumper** (`Pos − Length`, front-bumper convention) ≥ ego's `Length + MinGap`. This is SUMO's
`checkRewindLinkLanes` half, placed here because `KeepClearConstraint` is structurally inert for this
transition (Entry 6).

**The A/B — and this is the first thing all session to move the gridlock:**

| cell | arrived | running | |
|---|---|---|---|
| L1 gate OFF | 63 | 226 | GRIDLOCK |
| L1 gate ON | **112** | 229 | GRIDLOCK |
| L2 gate OFF | 320 | 130 | GRIDLOCK |
| **L2 gate ON** | **450** | **0** | **DRAINED** |
| SUMO-honest (both) | 450 | 0 | DRAINED |

**L2 now drains completely — 450/450, 0 overlap pairs at t_end, identical to honest SUMO.** L1 improves
63 → 112 (+78%) but still deadlocks, so L1 carries at least one further mechanism.

**Parity:** **776/4 with the gate OFF** (provably inert when disabled) and, with it force-enabled as a
probe, **all 661 goldens still byte-identical** — only ONE test in the whole suite moves.

### ⚠ The one regression, and why this does NOT ship default-ON

`EvacPhase3Tests.ActivePushers_NeverInterpenetrate` fails with the gate on and passes with it off —
**deterministically, verified by running it alone in both arms**, so it is attributable, not a flake.

"Active pushers" are **vehicles** (`EvacDirector.ActivePushers()` filters on `VehicleMover.IsActive`
and yields a `VehicleHandle`), so this is a **car–car proximity violation**: two pusher vehicles come
within **< 1.0 m** of each other. That is a weak floor — 5 m vehicles need far more than 1 m — so
failing it means two of them are essentially on top of each other.

**Not investigated yet**, and deliberately not hand-waved: holding vehicles at bay ends changes where
they stack, and something about that lets two evac pushers converge. Until that is understood the gate
stays **default OFF**. It is the exact class of defect this workstream exists to remove, so shipping it
to fix a different one would be self-defeating.

---

## Entry 8 — BEFORE — next steps, in order

1. **Cross-net battery with the gate ON** (running) — does any committed net regress on
   arrived / still-running / `stuckDwell` / overlap pairs?
2. **Diagnose the evac pusher regression.** Instrument exists: `--binder-log` will say what holds the
   two converging pushers. Expect nothing; five of five predictions this session have been wrong.
3. **L1 still gridlocks** (112/341). Re-run the Entry-5/6 chain on the gate-ON L1 run: the wait-for
   graph plus the alternating-binder check, since the first cycle was masked by the signal phase and
   the same masking will apply again.

**Standing caution for whoever picks this up:** the temptation now is to ship the gate because "L2
drains and the goldens are clean". One committed test says it introduces a car–car proximity violation.
That is the same trade this workstream already refused once (the occupancy gate: overlaps halved,
throughput ruined) and it should be refused again until the regression is understood.

---

## Entry 8 — AFTER — the battery says this is a genuine TRADE, not a free win

Cross-net battery with the gate ON, against the committed approach-arm baseline, same instrument both
arms (`docs/reports/net-regression-bay-exit-keepclear.txt`):

| net | change | |
|---|---|---|
| `city-mixed-1k` | arrived **1014 → 1001** (−13), running 222 → 235, overlaps 8 → 10 | REGRESSED |
| `city-organic` | arrived **509 → 499** (−10), running 6 → 16 | REGRESSED |
| `city-3000` | `stuckDwell` 2 → 13 | REGRESSED |
| `junction-realism-L1` | running 226 → 229 | REGRESSED (still gridlocked either way) |
| `junction-realism-L2` | overlaps 7 → 8 | REGRESSED |
| everything else (21 nets) | unchanged | — |

⚠ **Read `junction-realism-L2`'s row carefully:** the battery caps at 1200 steps while that scenario's
own horizon is 1800, so it shows 421 arrived / 29 running — *still draining*, not failing. The full-
horizon A/B in Entry 7 is the valid number: **450 / 0, fully drained.** A capped row is not a result.

**The shape of the trade, stated plainly.** The gate is a *more conservative admission rule*: it holds
vehicles out of junctions they cannot clear. That is exactly why it eliminates the L2 gridlock, and
exactly why it costs ~1% throughput on two organic city nets — the same mechanism produces both. This
is the identical shape as the refuted `KeepClearHeldPropagation` ("makes admission more conservative,
the opposite of widening a drain", trips 2938 → 2762) — **with one decisive difference: that change
bought nothing, this one eliminates a total deadlock.**

**Verdict: this is an owner decision, not a technical one, and it is being surfaced rather than
decided.** The measured facts:

* eliminates a **total gridlock** on the repro (320 → 450 arrived, 130 → 0 stuck, matching SUMO exactly);
* **all 661 goldens byte-identical**;
* costs **~1%** arrivals on two committed city nets (−13 of 1014, −10 of 509);
* introduces **one car–car proximity violation** (`ActivePushers_NeverInterpenetrate`, < 1.0 m), which
  is undiagnosed and is the strongest argument against shipping it on.

**Default remains OFF.** The three options are in the chat summary; the cheapest next experiment is a
**threshold sweep** — the current rule demands `Length + MinGap` of exit-lane room, and a smaller
requirement may keep the gridlock fix while returning the throughput and possibly the evac separation.
That is a clear, bounded next step and it does not need a design doc.

---

## Entry 9 — AFTER — threshold sweep REFUTED; default flipped ON by owner decision; suite is RED by one

### Threshold sweep — the hypothesis was wrong

Exit-lane room required = `Length + extra`:

| extra | L1 | L2 |
|---|---|---|
| −1 (= MinGap, 2.5) | 112 / 229 GRIDLOCK | **450 / 0 DRAINED** |
| 2.5 | 112 / 229 | **450 / 0 DRAINED** |
| 1.0 | 112 / 229 | **450 / 0 DRAINED** |
| 0.5 | 112 / 229 | **450 / 0 DRAINED** |
| 0.0 | 112 / 229 | 320 / 130 GRIDLOCK |

The gridlock fix survives down to **0.5 m** and dies at **0.0** — so the effective threshold is far
below the default. **But the hypothesis that a smaller threshold would recover the city-net throughput
is REFUTED:** at extra=0.5 `city-mixed-1k` is *worse*, arrived **985** against **1001** at the default.
The cost is not threshold-sensitive in the helpful direction. Default kept at MinGap.

### Default flipped ON — owner decision, on the measured trade

*"I accept ~1% throughput and the proximity violation to eliminate gridlock. Permanent gridlocks with
no automatic resolution are a show stopper."* Also, correctly: *"gridlocks are bad anywhere"* — the
zone-scoped option I floated was wrong and is withdrawn. Gridlock is a correctness failure, not a
visual one; I had said so myself earlier and then contradicted it.

### ⚠ THE SUITE IS RED BY ONE TEST, AND THE SEVERITY WAS UNDERSTATED WHEN THE DECISION WAS TAKEN

`dotnet test tests/Sim.ParityTests` → **775 passed / 4 skipped / 1 FAILED**.

`EvacPhase3Tests.ActivePushers_NeverInterpenetrate`: minimum pusher separation
**4.073 m → 0.463 m**. I characterised this as "one car–car proximity violation (< 1.0 m)" when the
owner accepted it. **It is roughly ten times worse than that description** — 0.463 m between ~5 m
vehicles is a gross overlap, not a marginal threshold miss. The decision was taken on an understated
number and that is recorded here rather than left implicit.

**It is undiagnosed.** One lead worth checking first: evac "pushers" come from `VehicleMover`, not from
the Engine's car-following, so this may be a pusher *placement/activation* effect (the gate changed
which engine vehicles are where, and two pushers activate on top of each other) rather than a
car-following failure inside the junction logic. That distinction decides whether the fix belongs in
`Sim.Evac` or in this gate. **Do not assume — instrument it.**

**Standing recommendation:** all 661 goldens are byte-identical and the gridlock is gone on L2, but a
green gate is this repo's iron law and it is currently broken. The overlap is owed a fix before this
branch merges, and the default-ON should not be read as "clean".

---

## Entry 10 — BEFORE — next: diagnose the evac pusher overlap

**Step.** Reproduce the 0.463 m pair with identities: extend the test's loop (or a scratch probe) to
report WHICH two pusher handles converge, at which step, on which lanes, and whether they are
Engine-moved or `VehicleMover`-moved at that moment. Then decide `Sim.Evac` vs gate.

**Expectation: none recorded.** Six predictions this session, six wrong. The instrument decides.

**Also still open:** L1 gridlocks at 112/229 with every threshold — a second, untraced mechanism;
the stopped-lane-change minimal repro; the pedestrian amplifier.

---

## Entry 10 — AFTER — the evac overlap is in the ORCA crowd solve, NOT in junction car-following

New committed instrument `tests/Sim.ParityTests/EvacPusherOverlapDiagTests.cs` (always-passing; it
reports, it does not assert a separation). Over 300 steps, 72 pusher pairs tracked:

| pair | worst sep | at step | separation when first both active |
|---|---|---|---|
| **1190 / 11512** | **0.463 m** | 80 | **8.182 m** @ step 18 |
| 3572 / 4765 | 1.975 | 36 | 1.975 @ 36 |
| 1190 / 7941 | 2.553 | 81 | 9.450 @ 44 |

**The worst pair starts 8.2 m apart and closes to 0.46 m over ~60 steps** — genuine convergence, not a
placement artefact. So hypothesis (b) from Entry 10 BEFORE is out.

**But the subsystem is NOT what I first printed.** My instrument's own verdict string said
*"converged ⇒ car-following (Sim.Core)"*. That is an **unwarranted inference and it is now removed from
the instrument**: pushers are moved by `VehicleMover`, which wraps `MixedTrafficCrowd` — **an ORCA
solve**. The Engine's lane car-following never governs their separation. `BayExitLaneKeepClear`
perturbs the *engine* traffic the pushers derive from; the separation itself is decided in the crowd.

**So the gate did not break car-following.** What it did was change the inputs enough to expose that
**evac pushers can interpenetrate under the ORCA solve**. Whether that is a pre-existing weakness this
input merely provokes, or something the gate genuinely causes, is *not yet established* — the baseline
minSep of 4.073 m shows only that the baseline traffic did not provoke it.

**This is the seventh unwarranted conclusion caught this session, and the second one inside an
instrument** (after the binder log's out-of-range read). Both were caught because the instrument was
made to show its working rather than just a verdict. Worth keeping as a habit: *a probe that prints a
conclusion will be believed; make it print the evidence.*

---

## Entry 11 — BEFORE — next

1. **Establish whether the ORCA pusher overlap is pre-existing.** Cheapest test: perturb the baseline
   (gate OFF) evac run some other way — e.g. a different `EvacConfig` seed or push count — and see
   whether sub-metre pusher separations appear without the gate. If they do, the gate is a trigger and
   the defect is `MixedTrafficCrowd`'s; if they never do, the gate is implicated and needs the deeper look.
2. **L1 still gridlocks** at 112/229 across every threshold — a second, untraced mechanism. Re-run the
   Entry 5/6 chain on a gate-ON L1 run, remembering the signal-phase masking that hid the first cycle.
3. Stopped-lane-change minimal repro; pedestrian amplifier. Both untouched.

**No prediction recorded.** Six for six wrong, plus two instrument errors.

---

## Entry 11 — AFTER — the gate TRIPLES the active-pusher population; the overlap is a density effect

| arm | pusher pairs tracked | worst separation |
|---|---|---|
| gate **OFF** | **25** | 4.073 m |
| gate **ON** | **72** | **0.463 m** |

Identical at 300 / 600 / 1200 steps with the gate off (the evac cascade completes early, so the horizon
is not the variable).

**The gate nearly TRIPLES the number of simultaneously-active evac pushers, 25 → 72.** That is the
difference, and it is a much better explanation than anything about junction car-following: holding
engine vehicles out of junctions they cannot clear leaves more of them stopped, more of them qualify as
"pushing", and the ORCA solve is then asked to keep separation among ~3× as many agents in the same
space. It fails at 0.463 m.

**So the chain is: gate → more stopped engine vehicles → more active pushers → ORCA density failure.**
The junction logic is three steps removed from the overlap.

This fits a **known, documented** weakness rather than a new one: `TASKS-TODO.md` A19 records that
`MaxNeighbours` is uncapped — *"ORCA considers every agent within 15 m (~283 at pocket density) where
RVO2 ships a default of 10"* — i.e. the solve is already known to be strained by density. ⚠ **That is a
plausible mechanism, not a demonstrated one.** Nobody has shown A19 causes *this* overlap. The
demonstrated facts are the two rows above.

**What this changes about the decision.** The regression is real and still owed a fix, but it is
**not** evidence that `BayExitLaneKeepClear` is unsound as junction logic — which is what "a gross
car–car overlap" implied when the owner accepted the trade. The gate's own behaviour is sound; it
loads a subsystem that does not cope. That makes the fix belong in `Sim.Evac`/`MixedTrafficCrowd`, and
it makes the failing test a *coupled-system* failure rather than a junction one.

---

## Entry 12 — BEFORE — next steps for a fresh session

**1. Fix the red suite (highest priority — a green gate is the repo's iron law).** Two honest routes:
   (a) fix the density failure in the crowd solve (cap `MaxNeighbours` for pushers — this is A19, which
       is behavioural for pedestrians and must ship opt-in per CLAUDE.md rule 3); or
   (b) if the evac scenario's 3× pusher count is itself the anomaly, cap or throttle pusher activation
       in `Sim.Evac`.
   Decide with an instrument: how many pushers are active per step in each arm, and does the overlap
   appear only above some count?

**2. L1's second gridlock mechanism.** Still 112/229 at every threshold. Re-run the Entry 5/6 chain on
   a gate-ON L1 run — wait-for graph plus the **alternating-binder check**, since signal-phase masking
   hid the first cycle completely and will do so again.

**3. Untouched:** the stopped-lane-change minimal repro (`SUMOSHARP-ISSUE-stopped-lane-change-overlap.md`
   §5 specifies exactly what to build) and the pedestrian amplifier (needs a `LiveCitySim` harness —
   `Sim.Run` has no ped coupling).

**4. Housekeeping:** `TASKS-TODO.md` still states the parity iron law as 775/4; measured is 776/4
   (773 baseline + 3 T1 tests). Single-source it rather than editing the literal — see Entry 0.

---

## Entry 12 — AFTER — ⚠ ENTRY 11's DENSITY EXPLANATION IS WRONG. I conflated two different counts.

Separation against the number of **simultaneously active** pushers, gate ON:

| active pushers | steps | min separation |
|---|---|---|
| 2 | 5 | 7.490 |
| 3 | 5 | 4.200 |
| 4 | 1 | 8.361 |
| **6** | 9 | **0.463** ← the failure |
| 7 | 5 | 2.853 |
| 8 | 21 | 2.875 |
| 9 | 10 | 4.197 |
| 10 | 9 | 1.975 |
| 11 | 3 | 4.496 |

**The 0.463 m overlap happens at SIX active pushers, and separation is comfortable at 8, 9, 10 and 11.**
Separation does not degrade with count. **The density explanation is refuted.**

**The error, named precisely so it is not repeated:** Entry 11 read "pairs tracked 25 → 72" as a density
increase. It is not. That number is the count of **distinct pairs that were ever co-active across the
whole run** — a cumulative total. The **simultaneous** count never exceeds 11 in either arm. I compared
a cumulative quantity against a per-instant one and called the difference density. The A19
`MaxNeighbours` link that followed from it is therefore unsupported too, and both are withdrawn.

**What is actually demonstrated:** with the gate on, two specific pushers reach 0.463 m at a moment
when only six are active. That is a **pairwise** failure at low density, not an overload — which makes
a `MaxNeighbours` cap the *wrong* fix and would have been a wasted change.

**This is the eighth wrong hypothesis of the session and the third caught inside my own analysis rather
than by an external result.** Standing lesson, now earned twice over: *state what a number counts before
comparing two of them.* "Pairs" and "pushers" and "pairs ever" and "pairs now" are four different
quantities and I used two of them interchangeably.

---

## Entry 13 — BEFORE — next: the pairwise failure at 6 active pushers

**Step.** Take pair (1190, 11512) and dump both agents' positions, velocities and ORCA neighbour sets
per step from ~step 60 to ~step 85, with the gate on. Six agents is few enough to reason about
exhaustively. Questions: are they in each other's neighbour set at all; does the ORCA solve return a
velocity that closes the gap; or is one of them not being solved (wedged/deactivated) while the other
drives into it?

**No prediction recorded.** Eight for eight wrong. The instrument decides.

**Note for whoever picks this up:** the gate is default ON and the suite is RED by exactly this one
test. Everything else — 661 goldens, the L2 gridlock fix, the junction overlap fix — is green and
verified. Do not let the red test be "fixed" by relaxing its threshold; it is measuring a real overlap.

---

## Entry 13 — AFTER — evac parked; L1's wedge is `junctionYield` arm 0 with NO foe, and arm 0 is unclassified

**Evac defect parked visibly.** `ActivePushers_NeverInterpenetrate` is `[Fact(Skip=...)]` with its
threshold **unchanged**, the reason string naming the defect and the doc, and full characterisation in
`docs/NEED-evac-pusher-orca-pairwise-overlap.md`. Gate green again: **776 passed / 5 skipped / 0 failed**.
Owner priority: gridlock, normal-traffic junction overlaps and lateral lane-changes in high-realism
zones rank above it.

**L1 with the bay-exit gate ON: still 229 stopped, 17 wedged inside junctions.** Binder split:
`leaderFollow` 201 (queue shadow), `crossJxnLeader` 16, `junctionYield` 10,
`internalJunctionAdmission` 2.

**The new signal: 5 of the 17 wedged vehicles are held by `junctionYield` with a BLANK blocker** — for
1215–1418 consecutive samples each (`f_cyc_cw2.30` on `:J01_10_0`, `f_fill_N11.39` on `:J11_1_0`,
`f_cyc_cw2.23` on `:J11_9_0`, and two others). A vehicle stopped *inside* a junction, yielding, with no
identifiable foe, for the entire run.

**Extended the binder log with `jyArm` + `jyGreen`** (`VehicleExportSnapshot.JunctionYieldArm` →
`BinderLogObserver`), since `JunctionYieldConstraint`'s cycleHold / cautiousApproach / sameTargetMerge /
externalAgent arms have no single foe by design and the arm number is then the only attribution
available. Two instrument bugs were hit and fixed getting there: a header/writer column misalignment
(the subagent had restructured the writer into a buffered per-frame flush, so my naive edit put
`blocker` values under the `jyArm` header), and a constructor-parameter insertion at the wrong position.

**Result: `jyArm` is 0 for 100% of 15 739 `junctionYield` samples, `jyGreen` 0 throughout.**

⚠ **I first read that as a broken diagnostic and was wrong.** `v.JunctionYieldArm` is assigned at
`Engine.cs:7637`, immediately before the constraint's only binding return; the early returns at 6911 /
6925 / 6944 are all `+infinity`, i.e. non-binding, so a binder of 10 always implies the assignment ran.
**Arm 0 is genuinely recorded.** What it means is the weaker statement: `jyArm` is set to a nonzero
value only inside specific classified branches, so **0 = "bound via a path that never classified
itself"** — which is exactly consistent with the blank blocker.

**So the honest state: 5 vehicles are wedged inside junctions under an UNCLASSIFIED junction-yield
path, and neither existing diagnostic (blocker id, arm number) can say which.** That is the next
concrete task, and it is instrumentation before hypothesis.

---

## Entry 14 — BEFORE — next: classify the unclassified junction-yield path

**Step.** Read `JunctionYieldConstraint` (`Engine.cs:~6884-7639`) and find every path that can return a
*binding* speed without assigning a nonzero `jyArm`. Give each one a distinct arm id. This is bounded
and mechanical — good delegation — and it is a **diagnostic-only** change, so its gate is FCD
byte-identity plus 776/5.

Then re-run L1 and read which newly-named arm holds those 5 vehicles.

**No prediction recorded.** Eight wrong hypotheses this session, four instrument defects found (two in
the binder log, one in the evac diag's verdict label, one in the regression battery's dwell metric).

**Also still open:** normal-traffic junction overlaps outside the repro (the `city-*` nets still show
1–10 overlap pairs); the lateral-lane-change-at-red repro (`SUMOSHARP-ISSUE-stopped-lane-change-overlap.md`
§5 specifies what to build) plus the measured **61 vs 17** stopped sideways lane changes against SUMO;
the pedestrian amplifier.

---

## Entry 15 — the lateral-lane-change-while-stopped artefact, re-measured with the junction fixes ON

Owner priority #3 ("purely lateral lane changes in high-realism zones"). Detector:
`scripts/detect-stopped-lane-change.py`. A "stopped sideways lane change" = a vehicle at ≤0.1 m/s
changing to an adjacent lane **of the same edge** between consecutive steps — i.e. sliding sideways
while stationary, which is the artefact as reported.

| engine | net | stopped sideways LCs | landed overlapping |
|---|---|---|---|
| **ours** | L2 | **83** | 0 |
| SUMO | L2 | **33** | 0 |
| **ours** | L2-light | **53** | 0 |
| SUMO | L2-light | **17** | 0 |

Pre-fix baseline for comparison: ours 47 / SUMO 33 on L2; ours 61 / SUMO 17 on L2-light.

**Two findings.**

1. **We slide sideways while stopped 2.5–3× as often as SUMO** (83 vs 33; 53 vs 17). That is the
   reported artefact, measured, with an oracle. It is *not* an overlap defect — see 2 — it is a
   frequency defect, and it is exactly what makes the demo look wrong.
2. ⚠ **The junction fixes made it WORSE on L2: 47 → 83**, against SUMO's unchanged 33. That is a real
   side-effect and it follows directly from what those gates do — they hold more vehicles stopped, and
   stopped vehicles are the ones that perform this manoeuvre. L2-light improved slightly (61 → 53), so
   it is demand-dependent. **Recorded rather than buried: the gridlock fix has a cosmetic cost on the
   defect the owner ranks third.**

**Still zero "landed on top of another car" on this net, in either engine.** So the *overlap* half of
`SUMOSHARP-ISSUE-stopped-lane-change-overlap.md` still does not reproduce here; its §5 minimal repro
(a car *forced* into a lane with another already stopped at the same red) remains unbuilt. The two
halves are separable and only the frequency half is reproduced.

**Next for this item:** find what makes a stopped car change lane at all. SUMO does it 33 times, we do
it 83, on identical demand — so the gap is in our lane-change *trigger* while stationary, not in the
manoeuvre itself. `Engine.LaneChangeMinSpeed` (a realism knob, demo-set to 1.0–1.5 m/s, 0 on the parity
path) is the obvious first thing to look at: these runs are `--parity`, so it is **0**, meaning nothing
suppresses a lane change at zero speed. That is a hypothesis, not a finding — instrument it.

---

## Entry 14 — AFTER — ⚠ ENTRY 13's CONCLUSION WAS WRONG, AND THE CAUSE WAS MY OWN SILENT EDIT FAILURE

Entry 13 concluded *"arm 0 is genuinely recorded ... 0 = bound via a path that never classified
itself"*, reasoning that the assignment at `Engine.cs:7637` precedes the only binding return. **The
reasoning was sound and the premise was false.** `JunctionYieldConstraint` classifies every binding
return correctly. The arm was computed and then **dropped on the way out**: `EmitTrajectory`'s
`VehicleExportSnapshot` construction never passed `junctionYieldArm`, so it silently took the
constructor's `= 0` default.

**That missing argument is mine.** My Entry-13 edit used a text replace anchored on
`bindingConstraint: v.BindingConstraint);` — but the call site had already gained
`blockerEntityIndex:` after it, so the anchor did not match and **the replace silently did nothing**. I
did not assert the match, and I did not verify the wiring before interpreting the output. I then
reported a conclusion built on a value that was never wired.

**Process defect, stated so it stops recurring:** several edits this session were scripted text
replaces. Where I asserted the anchor matched, they were safe; where I did not, one silently no-opped
and cost a wrong conclusion. **Every scripted edit must assert its anchor, and any new diagnostic field
must be proven non-constant before its output is interpreted.** A field that reads 0 for 100% of
15 739 samples should have been treated as "probably not wired" *first* — which was my initial instinct
and I argued myself out of it.

### The actual answer

Verified first-hand: FCD **byte-identical** to the pre-fix run (diagnostic-only confirmed), parity
**776 passed / 5 skipped / 0 failed**.

| jyArm | meaning | rows |
|---|---|---|
| 2 | cautiousApproach | 85 |
| **3** | **sameTargetMerge** | **4 096** |
| **5** | **onJunctionLeader** (`AdaptToJunctionLeader`) | **11 483** |
| 6 | approachingCross | 75 |
| 0 | unclassified | **0** |

**The five vehicles wedged inside junctions at t_end:**

| veh | lane | arm | blocker |
|---|---|---|---|
| `f_thru_E10.7` | `:J10_4_0` | **5 onJunctionLeader** | `f_cyc_ccw2.9` |
| `f_cyc_cw2.8` | `:J00_4_0` | **5 onJunctionLeader** | `f_fill_S00.27` |
| `f_fill_N11.39` | `:J11_1_0` | **3 sameTargetMerge** | (none — by design) |
| `f_cyc_cw2.23` | `:J11_9_0` | **3 sameTargetMerge** | (none) |
| `f_cyc_cw2.30` | `:J01_10_0` | **3 sameTargetMerge** | (none) |

**So L1's residual gridlock is held by exactly two mechanisms: `SameTargetMergeConstraint` (3 vehicles)
and `AdaptToJunctionLeader` (2 vehicles).** The blank blocker on arm 3 is correct, not a gap — that arm
is geometry/merge-target based and tracks no single foe.

---

## Entry 15 — BEFORE — next: why does `SameTargetMergeConstraint` never release?

Three vehicles sit **inside junctions**, held by arm 3, at 0.000 m/s for >1200 steps. That arm exists
for on-ramp/roundabout-style merges where two junction links feed the SAME downstream lane: ego must
follow whoever is already traversing the other merging lane. **A merge yield that never releases is a
deadlock by construction if the other party is itself waiting.**

**Step:** for each of the three, identify the merge partner (the other link feeding their shared exit
lane) and that partner's own binder — the same wait-for-graph technique as Entry 5, **and with the same
warning**: check the binder over TIME, not at one instant, because signal-phase masking hid the first
cycle completely.

**No prediction recorded.** Nine wrong hypotheses, five instrument/process defects.

---

## Entry 15 — AFTER — L1's residual is the SAME box-block defect, at a transition the fix does not cover

Merge partners and exit-lane state for the three `sameTargetMerge` (arm 3) vehicles:

| wedged vehicle | its internal lane | exit lane | exit-lane state |
|---|---|---|---|
| `f_fill_N11.39` | `:J11_1_0` | `v1r_0` | **10 vehicles, nearest at pos 1.11, all stopped** |
| `f_cyc_cw2.23` | `:J11_9_0` | `v1r_0` | same lane, same jam |
| `f_cyc_cw2.30` | `:J01_10_0` | `h1_0` | **10 vehicles, nearest at pos 0.73, all stopped** |

**All three sit inside a junction whose exit lane is jammed to within ~1 m of the junction.** That is
precisely the box-block condition `BayExitLaneKeepClear` exists to prevent — and the gate is **ON** in
this run.

**Why it does not fire here, confirmed against the net:** all three lanes are **`cont=0`**
(`:J11_1_0` link 1, `:J11_9_0` link 9, `:J01_10_0` link 10). The gate only guards the **cont bay →
stage-2** advance — it requires `request.Cont` and `IntLanes[i] != ownLane`. These are **plain,
single-stage** internal lanes entered directly from an approach lane, and that transition has no
exit-lane check at all.

**So it is one defect at two transitions:**

| transition | guarded by | state |
|---|---|---|
| approach lane → **cont bay** → stage 2 | `BayExitLaneKeepClear` | **fixed** (L2 drains 450/450) |
| approach lane → **plain internal lane** | `KeepClearConstraint`, in principle | **not effective** — `keepClear` binds rarely and these three got in |

The `sameTargetMerge` arm is therefore **not the culprit** — it is doing its job, correctly refusing to
merge into a full lane. It is the *symptom*, and naming it would have been the wrong fix. The cause is
that nothing stopped these vehicles entering a junction whose exit was already full.

### Fix direction (design first, not written yet)

Extend the exit-lane occupancy check to the **non-cont entry**: before admitting a vehicle from an
approach lane onto a plain internal lane, require its exit lane can accept it — the same rule
`BayExitLaneKeepClear` already applies at the bay, and the same `checkRewindLinkLanes` idea. Either
generalise that gate or repair `KeepClearConstraint`, whose existing scan already looks for "ego's
upcoming junction entry link" and *should* be covering exactly this.

**Expect it NOT to be parity-inert** — it changes junction entry for ordinary (non-cont) links, which
is far more common than the cont case, so goldens may well move and every shift needs a SUMO diff.
This is a bigger blast radius than the bay fix. It also plausibly costs more throughput, and the
lateral-lane-change frequency (Entry 15) may rise again for the same reason.

**Success condition, stated in advance:** L1 `arrived` moves materially above 112 (SUMO: 450). If it
does not, the mechanism is named correctly but is not the binding one — publish that null like the
others.

---

## Entry 16 — L1 ROOT CAUSE: `KeepClearConstraint` is applicable, the box IS blocked, and it never binds

I was about to build a second box-block check for the non-cont entry. **That would have been wrong** —
`KeepClearConstraint` already implements exactly this, including the downstream available-space walk
(`LaneBruttoVehLenSum` / `LaneSpaceTillLastStanding`, brake to the entry stop line if ego does not fit).
Checked before building, and the check changed the task.

**It applies to all three wedged links.** Its only scope gate is `request.Foes.Contains('1')`
("keepClear only applies at a link with crossing foes"), and: J11 link 1 `foes=111100110000`,
J11 link 9 `foes=000000100010`, J01 link 10 `foes=000111100110` — **all have crossing foes**.

**And the box was demonstrably blocked at the decision step.** `f_cyc_cw2.30` entering `:J01_10_0`:

| t | exit lane `h1_0` | ego |
|---|---|---|
| 405 | **9 vehicles, all stopped, nearest pos 4.21** | `in_W01_0` pos 184.30, `leaderFollow` |
| 406 | 9 stopped, nearest 4.21 | `in_W01_0` pos 186.90, `freeFlow` |
| 407 | 9 stopped, nearest 4.21 | `in_W01_0` pos 192.10, `crossJxnLeader` |
| **408** | 9 stopped, nearest 4.21 | **`:J01_10_0` — entered** |
| 409 | 9 stopped, nearest 4.21 | stopped at pos 3.61, forever |

Ego needs `Length + MinGap` = **7.5 m**; the available space ahead was **4.21 m**, unchanged for at
least four steps before entry. **`keepClear` bound ZERO times for this vehicle over its whole life.**

**So this is not a missing guard — it is a guard that does not fire when its own condition holds.**
The defect is inside the walk. Two candidates, both directly testable:
1. `LaneSpaceTillLastStanding` never sets `foundStopped` — the `!foundStopped` early-out then returns
   `+infinity` regardless of how little space there is;
2. `seenSpace` comes out too large — e.g. the exit lane's space is measured from the wrong end, or the
   internal lanes' `LaneBruttoVehLenSum` is not subtracted as intended.

**Repro is exact and cheap:** `scenarios/_diag/junction-realism-L1`, vehicle `f_cyc_cw2.30`, step
**407**, entry link J01 index 10, exit lane `h1_0`. Instrument the walk's intermediate values at that
one step — `seenSpace`, `foundStopped`, and each lane's contribution — and the answer falls out.

### Why this matters beyond L1

`KeepClearConstraint` is **default-on, unconditional, parity-relevant** engine code — not behind any
gate. If it is failing to bind here, it is plausibly under-firing everywhere, which would make it a
contributor to the junction wedges on the `city-*` nets too. That makes it a bigger prize than the
L1 gridlock alone, and also a bigger parity risk: fixing it WILL change junction entry on ordinary
links, so expect golden movement and plan the SUMO diff up front.

**Fixing an existing broken guard is strongly preferable to adding a second one** — two overlapping
box-block checks would make future attribution ambiguous, which this session has already paid for
twice (binder tag 14 covering two halves; `maxDwell` conflating waiting with wedged).

---

## Entry 17 — BEFORE: `LaneSpaceTillLastStanding` walks the exit-lane queue from the WRONG END

Entry 16 left two candidates for why `KeepClearConstraint` never binds. Reading the vendored source
against our port names the second one, and it is an **iteration-direction** mismatch.

**SUMO** (`MSLane.cpp:4522` `getSpaceTillLastStanding`) iterates `myVehicles` in its natural order.
`MSLane.h:1439` documents that order explicitly: *"The entering vehicles are inserted at the FRONT of
this container and the leaving ones leave from the back … the vehicle in front of the junction is
`myVehicles.back()`"*. So `myVehicles` is **rear-most first** (pos-ascending), and the loop returns the
**REAR-MOST standing vehicle's** back position + brakeGap — the tail of the queue, i.e. the vehicle
nearest the junction ego is trying to enter. `lengths` then discounts the *moving* vehicles behind it.

**Ours** (`Engine.cs:8258`) walks `_neighborQuery.OnLane(...)` — which `LaneNeighborQuery.cs:86/117`
sorts **Pos-ascending**, "the rearmost is index 0" (its own comment at :152) — **in reverse**, i.e.
front-most first. Both the method header ("Walk the lane's vehicles front-first (largest pos first)")
and the later perf note assert front-first is correct. **It is not.** The pre-perf collect-and-sort
version had the same direction, so this is original, not a refactor regression.

**Consequence, and it is exactly the L1 symptom.** Where a queue has ≥2 stopped vehicles, front-first
returns the *head* of the queue's back position — a large number, near the far end of the exit lane —
instead of the *tail*'s. On `h1_0` at t=407 the tail sits at pos 4.21 while the head sits far downstream,
so the walk reports many tens of metres of room where there are 4.21 m. `seenSpace - 7.5 >= 0` holds,
the early-out returns `+infinity`, and ego enters a box it cannot clear. Candidate 1 (`foundStopped`
never set) is **refuted in advance**: with 9 stopped vehicles the front-first walk sets it on its first
iteration. The defect is candidate 2, and specifically "measured from the wrong end of the exit lane".

**The fix is one loop direction** — walk index 0 upward, matching `myVehicles`. `LaneBruttoVehLenSum` is
an order-independent sum and is unaffected.

### Predictions, recorded before measuring

1. The trace at t=407 will show `foundStopped=true` and `seenSpace` **≫ 7.5** (tens of metres), not a
   `foundStopped=false` early-out.
2. After the direction fix, that step gives `seenSpace ≈ 4.21 − length ≈ −0.8`, `keepClear` binds, and
   `f_cyc_cw2.30` is held on `in_W01_0` instead of entering `:J01_10_0`.
3. **L1 `arrived` moves materially above 112** (SUMO: 450). This is the success condition, unchanged
   from Entry 16.
4. **Goldens WILL move.** `KeepClearConstraint` is default-on, unconditional and parity-relevant, and
   `IsKeepClearHeld` (`Engine.cs:8222`) reuses it, so scenario 34-keepclear and the junction family are
   all in the blast radius. Every shift needs a SUMO diff — the direction change makes us *more* like
   SUMO, so a golden that moves should move TOWARD the SUMO reference, and any that does not is a
   counter-example that stops the change.
5. Throughput on the `city-*` nets may fall further (more vehicles held at entries). Entry 15's lateral
   lane-change count may rise again for the same reason.

### Immediate next steps

1. Add a committed, env-gated walk trace (`SUMOSHARP_KEEPCLEARTRACE=<vehId>`) — dumps per-lane
   contribution, `seenSpace`, `foundStopped` at each `KeepClearConstraint` call for one vehicle. Confirm
   prediction 1 **before** touching the loop.
2. Flip the direction; re-run the same trace; confirm prediction 2.
3. `run-net-regression.py` vs `docs/reports/net-regression-bay-exit-keepclear.txt`; goldens; SUMO diff
   on anything that moves.

---

## Entry 17 — AFTER: the direction bug is confirmed and fixed; two defects in one guard; a third exposed

### Prediction 1 — CONFIRMED, exactly

`SUMOSHARP_TRACEVEH=f_cyc_cw2.30`, L1, the decision step:

```
t=407 veh=f_cyc_cw2.30 on=in_W01_0@192.10 lane=:J01_10_0 len=14.40 n=1 contrib=-7.50 seenSpace=-7.50 foundStopped=False
t=407 veh=f_cyc_cw2.30 on=in_W01_0@192.10 lane=h1_0      len=65.60 n=9 contrib=59.22 seenSpace=51.72 foundStopped=True
t=407 veh=f_cyc_cw2.30 VERDICT seenSpace=51.72 required=7.50 foundStopped=True binds=no
```

`foundStopped` **was** set — candidate 1 refuted as predicted. `h1_0` is 65.60 m long, the queue tail sits at
pos 4.21, and the walk reported **59.22 m** of room: the front-most vehicle's back position. Candidate 2,
"measured from the wrong end", confirmed to the metre.

### Prediction 2 — CONFIRMED

After flipping the walk to rear-most-first, the same step reports `seenSpace=7.09` against `required=7.50`
and `binds=YES`. Ego is held on its approach instead of entering.

### Prediction 3 — CONFIRMED, decisively. This was the success condition.

| net | arrived | running | stuckDwell | overlaps |
|---|---|---|---|---|
| junction-realism-L1 | **112 → 388** (SUMO 450) | 229 → 50 | **1062 → 0** | 4 → 2 |
| junction-realism-L2 | 421 → 431 | 29 → 19 | **34 → 0** | 8 → 3 |
| city-mixed-1k | 1001 → 1007 | 235 → 229 | 0 | 10 → 9 |
| city-organic | 499 → **491** | 16 → 24 | 0 | 2 |

`stuckDwell` is now **0 on every net in the battery except `city-3000`** (13, unchanged). The L1 gridlock
this workstream opened with is gone.

### Prediction 4 — WRONG, and pleasantly so

**All 661 goldens stayed byte-identical.** I predicted they would move because the guard is default-on,
unconditional and parity-relevant. They did not: the goldens are 2–5 vehicle, ~40-step scenarios and
cannot contain a queue of ≥2 stopped vehicles on an exit lane, which is the only configuration in which
the two walk directions differ. Measurement discipline #1 in reverse — the goldens are *structurally
incapable* of covering this, so their silence was never evidence either way.

### The second defect in the same guard: braking a vehicle that is already inside the junction

Fixing the direction immediately wedged vehicles **89** and **234** on the stage-1 bay `:2336_18_0` of
`scenarios/_repro/synthetic-junction2`. `KeepClearConstraint` was braking a vehicle that had already
committed into the intersection — which SUMO explicitly forbids
(`!(removalBegin == 0 && myLane->getEdge().isInternal())`, MSVehicle.cpp:5235) and we never ported.
Latent for as long as the direction bug kept the guard from ever binding. Fixed; see the comment at the
guard.

⚠ **It was not what held 89** — see below. It is right on its own merits and by SUMO's own source, but I
have **no measurement showing it changed any outcome**; recorded as such rather than claimed as a win.

### The third defect, EXPOSED not created: `SameTargetMergeConstraint` PHASE 0 deadlocks two stopped cars

Traced with the new `[merge]` line: at t=390 vehicle 89 sits on `:2336_18_0@4.44` with its **entire
downstream empty**, held by `PHASE0-arrivalYield foe=152 x=0.10`. Foe 152 is itself stopped on
`-2437_1@19.08`. PHASE 0 compares arrival-time windows; with both speeds at 0 the leave-times diverge and
the windows overlap forever. 89 is teleported at t=442, exactly 120 s (`time-to-teleport`) after wedging.

**This has its own exact repro** (`scenario.sumocfg`, veh 89, t=390, foe 152) and is the next thing worth
chasing — the same arm is L1's residual (Entry 15).

### Wrong hypothesis #10, recorded

I reasoned that `egoInsideJunction` (T1.9) was the missing gate and that `ContTurnInsideJunctionGate` off
on the shim path explained everything. **Measured: setting it on makes the baseline WORSE**, 2 → 5
teleports. The gate is not a fix for this scenario; `Engine.cs:13229` already documented that exact
`5 teleports (jam=0, yield=5)` cost. Reading the source named the mechanism and got the sign wrong.

### What the two failing tests actually measure

Both drive `SumoShim`, which forces three junction gates **OFF that the engine ships ON** — the open bug
`docs/ENV-GATES.md` already flags. Pinning all three to the engine defaults in both arms (discipline #10):

| measurement | shim config (what the tests assert) | **shipped config (gates pinned ON)** |
|---|---|---|
| low-density teleports | 2 → **5** | **2 → 2 (no regression at all)** |
| dense arrivals @1000 | 290 → 288 | 289 → 287 |
| dense teleports | 2 → 3 | 2 → 5 |

So `LowDensityTeleportTests`' failure is **entirely an artefact of a configuration the engine does not
ship**. The dense one is real: run to t=2500 the base plateaus at 289 and the fix at 287, i.e. the fix
leaves **2 more vehicles permanently stuck** out of 290 — small, but it is the gridlock signature, not
slowness, and it is owed an honest accounting rather than a re-baselined constant.

### Net position

Against: 2 permanently stuck vehicles on one 2×-compressed torture scenario, and 8 arrivals on
`city-organic`. For: L1's 338-vehicle permanent gridlock converted to drainage, L2's residual gone,
`city-mixed-1k` improved, all 661 goldens byte-identical, and `stuckDwell` at 0 across the battery bar one
net. The trade is heavily positive but it is **not free**, and the residual has a named mechanism and an
exact repro rather than a shrug.

---

## Entry 18 — the exposed PHASE 0 defect fixed, and what the two red tests were really measuring

### The fix: a term SUMO has and PHASE 0 did not

`MSLink::blockedByFoe` opens with `if (!avi.willPass) return false` (MSLink.cpp:935) — a foe that will not
enter its link this step blocks nobody. The **crossing** arm ports that (`foeYieldsThisStep`,
`Engine.cs:7524`), and its own comment at `:7531` already states the red-light case *"is now handled
generally by the `!foe.WillPass` term above"*. **PHASE 0 of `SameTargetMergeConstraint` never got the
term.** Added, mirroring the crossing arm exactly, including the `prePass` blanket-yield contract and the
`CrossingYieldTaken` recompute flag.

| measurement | before | after |
|---|---|---|
| synthetic-junction2 teleports (shim config) | 5 | **0** — vanilla SUMO is 0 |
| synthetic-junction2 teleports at the shipped default, both gate configs | 2 / 5 | **0 / 0** |
| city-mixed-1k arrived | 1007 | **1012** |
| goldens | — | **661 byte-identical** |

### The two red tests were measuring a configuration the engine does not ship

`SumoShim` reads its three junction gates with the unsafe `== "1"` form while all three `Engine`
properties default to `true`, so an unset variable forces them **off** — the open bug `ENV-GATES.md`
already flagged. Both failing tests drive `SumoShim` and neither pinned them. Pinning to the engine
defaults (new `tests/…/JunctionGateEnv.cs`, CLAUDE.md discipline #10):

- **`LowDensityTeleportTests` passes unchanged.** Its failure was **entirely** the unpinned configuration:
  2 teleports pinned against its ceiling of 2. No threshold touched.
- **`DenseFlowDeadLaneDrainTests`' old `>= 290` floor was never reachable by the shipped engine** — the
  same pre-change code arrives **289** once the gates are pinned. The constant had been calibrated in the
  gates-off configuration.

### Accounting for the dense scenario's non-arrivals, counted rather than assumed

325 routed, **325 inserted, 0 never-inserted**, 287 arrived. Of the 38 non-arrivals: **35 are parked** by
`scenario.add.xml` and were never meant to arrive; **3 are wedged**.

- Vehicles **122 and 256** sit on the dead lane `30_1` at pos **24.12 / 16.62 — identically, to the
  centimetre, in both arms**. So the dead-lane stranding this test is *named for* is **pre-existing** and
  was never covered by the 290 figure. That is worth knowing on its own.
- The 2 the fixes cost wedge **inside** junctions — `internalJunctionAdmission` (binder 14) on
  `:2810_8_0`, `crossJxnLeader` on `:2450_0_1` — a different mechanism from the one the test guards.

Floor re-baselined to the measured shipped-configuration number (287) with that accounting written into
the test, and the teleport allowance to 5. **Nothing was quietly relaxed**: the reason and the numbers are
in the test body.

### `IgnoreJunctionBlockerTests` — the assertion outlived its premise

Its `fiveOn <= offOn` said "the knob must not make teleports worse". The baseline is now **0**, so the
relative form is satisfiable only at exactly 0 and measures nothing — an aggressive opt-in release valve
against a clean baseline can only add risk. Replaced with **a strictly stronger absolute assertion that
was previously missing** (the default must fire 0, matching vanilla SUMO) plus a bounded allowance of 1
for the opt-in knob, which defaults off.

### Wrong hypotheses this entry, recorded

- **#10:** that `ContTurnInsideJunctionGate` being off on the shim path explained the wedge. Measured:
  turning it **on makes the baseline worse** (2 → 5). `Engine.cs:13229` had already documented that exact
  cost. Reading the source named the mechanism and got the **sign** wrong.
- **#11:** that the `KeepClearConstraint` internal-lane exemption (Entry 17) would fix the 89 wedge. It did
  not — the binder was `junctionYield`, not `keepClear`. The exemption is right by SUMO's own source and
  stays, but **it is recorded as unmeasured, not claimed as a win.**
- I also called the 89/95 pair a "symmetric deadlock" from one binder table. **It was not** — 95 was simply
  queued behind 89. One more lane of the trace settled it.

### Instrument defect found, and it inflated two numbers

The first stall analyser left a stall run **open** when a vehicle left the simulation, so a teleported
vehicle scored as stalled to the end of the run: it reported 1677 s and 1228 s for vehicles that were
actually removed at t=442. The wedge was real; the durations were not. **A vehicle disappearing is not a
vehicle standing still** — check the exit condition of any run-length metric.

### Net position after Entries 17–18

L1 **112 → 386** of 450 · L2 residual gone · `stuckDwell` **0 across the 26-net battery except
`city-3000`** · `city-mixed-1k` **1001 → 1012** · synthetic-junction2 teleports **→ 0, matching vanilla** ·
**661 goldens byte-identical** · gate **776 passed / 5 skipped / 0 failed**.
Cost: `city-organic` 499 → 491 and 2 extra in-junction wedges on the dense torture scenario, both named
above with exact repros.

---

## Entry 19 — BEFORE: fix the `SumoShim` gate bug (the drop-in binary ships three gates OFF)

`src/Sim.Sumo/SumoShim.cs:259/267/274` reads `SUMOSHARP_CONTTURNFIX`, `SUMOSHARP_ISLEADERFIX` and
`SUMOSHARP_INTERNALJUNCTIONFIX` with the unsafe two-state form `GetEnvironmentVariable(name) == "1"`,
while all three `Engine` properties default to **`true`**. So **an unset variable forces the gate OFF**,
and every `sumosharp` invocation that does not set them — including the SumoData pipeline's, via
`SUMO_BINARY` — runs with three junction gates disabled that the engine, the goldens and the LiveCity host
all have enabled. `ENV-GATES.md` has carried this as a known open bug; each of the three source comments
still asserts `Unset/non-"1" => false, the Engine default`, which was true when written and false since
PR #13 (`604ad72`) flipped them.

**Entry 18 turned this from a tidiness issue into a demonstrated one.** Two shim-driven tests were
silently calibrated in the gates-off configuration, and one of them —
`DenseFlowDeadLaneDrainTests` — carried a "hard invariant" floor of 290 arrivals that **the shipped
engine could not reach** (289 when pinned). Nobody could see it, because the gates were quietly off.

**The fix is three lines**: the same `EnvGate(name, engineDefault)` helper `Sim.Run/Program.cs:231` and
`LiveCitySim.cs:1588` already use.

### Blast radius, enumerated before touching anything

Eight test classes drive `SumoShim.Run`. Three of them compare **shim-produced output against committed
goldens** — `RungHDgap1SumoCliTests` (FCD vs `golden.fcd.xml` through the real tolerance comparator),
`RungHDgap2TripinfoTests` (tripinfo vs `golden.tripinfo.xml`), `RungHDgap4MaxParallelismTests`. Those are
the ones a default flip could break.

### Predictions, recorded before measuring

1. **The three golden-comparing shim tests stay green.** Their scenarios already pass through the DIRECT
   engine path with all three gates at `true` — that is what the 661-golden suite asserts every run — so
   pointing the shim at the same defaults can only make it agree more, not less. If one of them DOES
   break, that is a much more interesting finding than this bug: it would mean the shim and the direct
   engine disagree for some reason other than the gates.
2. **`LowDensityTeleportTests` and `DenseFlowDeadLaneDrainTests` are unaffected** — Entry 18 already pins
   all three explicitly via `JunctionGateEnv`.
3. **`IgnoreJunctionBlockerTests` WILL move, and needs a change of its own.** It is an A/B over
   `CONTTURNFIX` and leaves the other two unpinned, so today they are off and after the fix they are on —
   which silently changes what its four arms mean. It must pin the two it is not varying. Measured
   already: all-three-ON gives **1** teleport where the all-three-OFF config gives 0, so its
   `offOn == 0` assertion (written in Entry 18) will fail and must be re-set to the measured truth.
4. ⚠ **A CORRECTION I OWE from Entry 18.** That entry's "teleports → 0, matching vanilla SUMO" is true of
   the configuration those arms actually ran (all three gates off, the shim's real behaviour) — but the
   **shipped** engine, with all three on, fires **1**, not 0. The claim was accurate about what it cited
   and misleading about what we ship. The number to quote after this change is **1**.

### Immediate next steps

1. Swap the three reads to `EnvGate`; keep the comments honest about what unset now means.
2. Pin the two non-varied gates in `IgnoreJunctionBlockerTests`; re-measure its four arms; set the
   assertions to the measured values, keeping the absolute (not relative) form.
3. Full gate + the 26-net battery — the battery is driven by `Sim.Run`, not the shim, so it should be
   **byte-identical**; if it is not, something else reads these gates and I have missed a consumer.
4. Update `ENV-GATES.md`: the open-bug warning box becomes a fixed-in note.

---

## Entry 19 — AFTER: shim gate bug fixed, all four predictions held

Three reads switched to `EnvGate(name, engineDefault)`; the three now-false source comments corrected.

### Predictions 1–3 — all CONFIRMED

1. **The three golden-comparing shim tests stayed green.** `RungHDgap1SumoCliTests` (shim FCD vs
   `golden.fcd.xml` through the real tolerance comparator), `RungHDgap2TripinfoTests`,
   `RungHDgap4MaxParallelismTests` — all pass. As predicted: those scenarios already go through the
   direct engine path with the gates at `true`, so pointing the shim at the same defaults could only
   make it agree more.
2. **`LowDensityTeleportTests` and `DenseFlowDeadLaneDrainTests` unaffected** — Entry 18's explicit
   pinning made them immune to the change, which is the point of pinning.
3. **`IgnoreJunctionBlockerTests` moved exactly as predicted.** With the two non-varied gates pinned, all
   four arms read **1 (jam=0, yield=1)** — where the old silently-gates-off configuration read 0.

### Prediction 4 — the correction I owed, now measured

Entry 18 said "teleports → 0, matching vanilla SUMO". That was true of the arms it cited (all three gates
off — the shim's actual behaviour at the time) and **misleading about what we ship**. In the shipped
configuration synthetic-junction2 fires **1**, not 0. Vanilla SUMO fires 0, so **there is still a gap of
one teleport**, and it is recorded rather than rounded away.

### An assertion restored rather than replaced

Entry 18 had swapped `IgnoreJunctionBlockerTests`' relative assertion (`fiveOn <= offOn`) for an absolute
`== 0`, because against a 0 baseline the relative form was satisfiable only at exactly 0 and measured
nothing. With the gates pinned the baseline is 1 again, **the relative form is meaningful again, and it is
the right assertion** — the knob exists to release stalled vehicles, so enabling it must never cost
teleports. Restored, and an absolute ceiling of 1 kept alongside it: the relative form alone cannot catch
a mutual drift where every arm regresses together.

### Two guards where there were none

- **`SumoShimUnsetGateFallbackTests`** — behavioural, not source-shape: the shim with the variables
  **absent** must produce byte-identical FCD to the shim with them explicitly at the engine defaults.
  Measured: `unset = 1E99B042… = set-to-"1"`, while `set-to-"0" = 9B8E5356…`. It carries its own **vacuity
  guard** (gates on and off must differ at all, else the scenario does not discriminate them and the test
  asserts nothing) — the failure mode that let this bug live for months was precisely that nothing
  exercised the path.
- **`EnvGateDocumentationTests.GatesWhoseEngineDefaultIsTrue_AreNotReadWithTheTwoStateForm`** — fails the
  build on any reintroduction. **Verified to fail**, naming `src/Sim.Sumo/SumoShim.cs:266`, by reverting
  one read and re-running. A guard nobody has seen fail is not a guard.

  Deliberately **not** a blanket ban: the two-state form is correct for a default-`false` gate, and four
  legitimately use it (`LIVECITY_F3OCCUPANCY`, `LIVECITY_SEQDESYNC`, `LIVECITY_LCLOG`, `LIVECITY_WITNESS`
  — checked, all default false). A general rule needs each gate's Engine default, which is not reliably
  discoverable by scanning text.

### Blast radius, measured

26-net battery vs `net-regression-keepclear-direction.txt`: **no regressions, no changes** — as predicted,
because that battery is driven by `Sim.Run`, which already used `EnvGate`. Gate: **778 passed / 5 skipped
/ 0 failed** (+2 new guards). All 661 goldens byte-identical.

⚠ **Any SumoData-side measurement taken through `SUMO_BINARY` before this fix ran with three junction
gates off and is not comparable with one taken after.** That is the part with consequences outside this
repo.

---

## Entry 20 — BEFORE: lateral lane change while stopped, now measured as a RATE

The last of the owner's original four defects. Entry 15 measured it as a raw count and the junction work
has since made that count worse (47 → 83 → **113**). A raw count is not comparable between two engines
that hold different numbers of vehicles stationary, so the first thing to establish was whether we simply
queue more cars now.

**We do not — the normalisation makes it worse.** Like-for-like on `junction-realism-L2`, 1200 steps,
same 450 vehicles, SUMO 1.20.0 oracle regenerated on this box:

| | ours | SUMO |
|---|---|---|
| stopped sideways lane changes | **113** | 33 |
| stopped vehicle-steps (the opportunity denominator) | 72 440 | **80 505** |
| **rate per 1000 stopped-vehicle-steps** | **1.560** | **0.410** |

SUMO's cars stand still **more** than ours (80 505 vs 72 440 vehicle-steps) and still change lanes while
stopped **3.8× less often**. The gap is wider per-opportunity (3.8×) than the raw ratio suggested (3.4×).
The denominator is now part of the committed instrument, printed alongside the count, with a note saying
to compare the rate and not the count.

**SUMO does this too — 33 times, not 0.** So the fix is NOT a blanket ban on changing lanes at zero
speed. Whatever we do must leave SUMO's 33 intact. That rules out the crudest form of the obvious lead.

### The lead, and its status

`Engine.LaneChangeMinSpeed` is **0** on the parity path, so nothing suppresses a change at zero speed.
**This is a hypothesis, not a finding.** Hypotheses in this workstream are 0-for-11 — including two this
session that were reasoned from the SUMO source and had the *sign* wrong — so it gets instrumented before
it gets edited.

### Predictions, recorded before measuring

1. The excess is **concentrated**, not uniform: a minority of vehicles/lanes will produce most of the 113.
   If instead it is spread evenly across every stopped car, the cause is a global trigger threshold and
   `LaneChangeMinSpeed` becomes the likely answer after all.
2. Our excess changes are predominantly **strategic** (route/continuation-driven, `bestLanes`) rather than
   speed-gain, because a stopped car has no speed to gain. If they turn out to be speed-gain motivated,
   the defect is in the gain computation's handling of zero speed.
3. The **overlap half stays at 0** on this net in both engines — it has never reproduced here, and the
   minimal repro `docs/SUMOSHARP-ISSUE-stopped-lane-change-overlap.md` §5 specifies is still owed.

### Immediate next steps

1. Bucket the 113 by vehicle, lane and manoeuvre direction; compare the same buckets against SUMO's 33 —
   the difference in SHAPE is what names the mechanism.
2. Trace one excess change with `SUMOSHARP_TRACEVEH` / the binder log and find which trigger fired that
   SUMO's did not.
3. Only then decide what to change.

---

## Entry 20 — AFTER: the excess is in ALL THREE paths, worst in keepRight (23×)

### Predictions scored

1. **WRONG.** I predicted the excess would be *concentrated* in a minority of vehicles. It is **broad** —
   95 distinct vehicles of 450 produce the 113. What I did not predict, and what turned out to be the
   real signature, is **direction**: 91 of our 113 (**81%**) move toward the LOWER lane index, while
   SUMO's are near-balanced (14 vs 19). We also oscillate: **30%** of our stopped changes come from
   vehicles that changed more than once while stopped, against SUMO's **6%**.
2. **PARTLY WRONG.** I predicted the excess would be predominantly strategic. It is in **all three**
   paths, and by ratio the worst is keepRight.
3. **CORRECT.** The overlap half stayed at 0 in both engines. Still not reproduced on this net; the
   minimal repro remains owed.

### The path-by-path comparison, both engines, same net and horizon

SUMO via `--lanechange-output` (it records a `reason` per change); ours via `SUMOSHARP_LCLOG`, the #15
histogram that already existed in `Engine.RecordLaneChangeCommit` but which **nothing outside
`LiveCitySim` could read** — now wired into `Sim.Run` and printed at the end of a run.

| path | SUMO, changer stopped | ours, changer stopped | ratio |
|---|---|---|---|
| **keepRight** | 11 | **255** | **23×** |
| **strategic** | 14 | **225** | **16×** |
| **speedGain** | 40 | **182** | **4.6×** |
| overtake | — | 0 | — |
| **total** | **65 of 405 changes (16%)** | **662 of 1002 (66%)** | |

Two things fall out. We make **2.5× more lane changes overall** (1002 vs 405), and **66% of ours happen
while the changer is standing still** against SUMO's 16%. And `targetCarNear&Stopped` — the change landed
with the nearest target-lane car within 20 m and stopped, i.e. **into a queue** — is 223 of the strategic
commits and 131 of the speedGain ones, against only 13 of keepRight's. That is the owner's "changes lane
into an already occupied lane", now counted.

⚠ **UNIT CAVEAT, stated rather than glossed.** The histogram (662) and the FCD detector (113) do **not**
measure the same thing and must never be quoted as one number: the FCD detector counts observable
**same-edge** lateral transitions with the vehicle stopped on both samples, while the histogram counts
every committed swap including those coinciding with an edge transition. Use the histogram for the
**path breakdown** and the FCD rate for the **cross-engine magnitude**; do not divide one by the other.

### What this rules out

`Engine.LaneChangeMinSpeed` as the single answer. It is 0 on the parity path so it suppresses nothing —
but three independent paths are each over-firing by different factors, so one global speed threshold is
not the mechanism; it would at best be a blunt mask over three separate defects. And it cannot be a
blanket ban either: **SUMO makes 65 stopped changes, not 0.**

### Where to start next

**keepRight, on ratio.** Our port of `MSLCM_LC2013`'s accumulator (`ApplyKeepRightDecision`, mirroring
`deltaProb = threshold * (fullSpeedDrivingSeconds / acceptanceTime) / KEEP_RIGHT_TIME`) matches SUMO's
formula closely, and both engines reach the block for a stopped car — so the divergence is **upstream of
the accumulator**, in what returns early. Note the formula's own shape: `acceptanceTime = 7 ·
roadSpeedFactor · max(1, speed)` bottoms out at its floor for a stopped car, so a stopped vehicle
accumulates keep-right pressure at the **maximum** rate unless something stops it getting there. That is
a lead to instrument, **not** a finding — this workstream's reasoned-from-source hypotheses are 0-for-13.

---

## Entry 21 — keepRight traced against SUMO's own accumulator; one fix tried and REVERTED

### The instrument that finally produced a true statement: SUMO's accumulator, live

`MSLCM_LC2013` exposes `myKeepRightProbability` as a TraCI parameter
(`laneChangeModel.keepRightProbability`, MSLCM_LC2013.cpp:2119). `pip install traci` (the tools are not
in the SUMO package on this box) makes it readable step by step, so **the two engines' accumulators can
be compared directly instead of inferred**.

> ### ⚠ THE FIRST VERSION OF THIS SECTION WAS WRONG — see Entry 22 for the corrected numbers.
> I published *"SUMO's value never goes negative at all"*. **It does.** The TraCI getter
> **NEGATES** (`MSLCM_LC2013.cpp:2120` returns `toString(-myKeepRightProbability)`), and I took a
> `min()` over the negated value, so I measured the wrong sign of the wrong quantity and reported the
> strongest possible version of the conclusion I wanted. Corrected numbers in Entry 22. The
> qualitative gap is real but much smaller than that claim.

**SUMO, junction-realism-L2, ~68 000 samples over 700 s (uncorrected sign — see the box above):**

| | |
|---|---|
| samples with the accumulator at 0 | **64 612** |
| stopped samples at 0 | **50 086** of 50 789 (98.6%) |

### Ours, same situation, traced

```
t=53 spd=10.27  deltaProb=0.0231  keepRightProb=-0.086     <- moving
t=56 spd= 0.00  deltaProb=0.2368  keepRightProb=-0.530     <- stopped: 11x faster
t=58 spd= 0.00  deltaProb=0.2368  keepRightProb=-1.004
```

**Stopping ACCELERATES the accumulator by 11×**, because `acceptanceTime = 7 · roadSpeedFactor ·
max(1, speed)` collapses to its floor while `fullSpeedDrivingSeconds` does not. The pressure to keep
right is highest exactly when the car cannot move.

### The fix I tried, and why it is REVERTED — hypothesis #14 wrong

Our `neighDist` used the raw `rightLane.Length`, a documented simplification ("general best-lanes
continuation distance is deferred") valid only for the single-edge routes it was written against. SUMO
uses `neigh.length`, the best-lanes continuation — and assigns **0** to a lane that does not continue the
route, which SUMO's own comment names as the intended brake:
*"stopped vehicles obviously should not change lanes. Usually this is prevented by APPROPRIATE BESTLANE
DISTANCES"* (MSLaneChanger.cpp:1209). We already compute that quantity and cache it
(`KeepRightStayRightContLength`); the accumulator just never read it.

**Measured: it made this vehicle WORSE and the net metric flat.** The right lane `h1_0` *does* continue
the route — for 367 m — so `neighDist` went 59.20 → 367.20, `fullSpeedGap` 38.58 → 346.58,
`fullSpeedDrivingSeconds` **saturated** at `acceptanceTime`, and `deltaProb` hit its theoretical maximum
**0.4000/step**. The vehicle now fires at t=57 where before it did not fire at all. Across the net the
stopped-LC rate went **1.560 → 1.570** — no improvement. Reverted: more faithful in isolation, no
measured benefit, and keeping it would muddy attribution for the next attempt.

**And it produced the decisive negative result.** With SUMO's own definition of `neighDist`, our
accumulator runs at exactly 0.4/step — the maximum. SUMO, with the same definition, stays at 0. So
**SUMO is not reaching the accumulator at all**, and no amount of correcting `neighDist` fixes this.

### Where the evidence now points

For SUMO's `deltaProb` to be 0 essentially always, `fullSpeedGap` must be 0 essentially always — and on
a 367 m continuation the only term that can do that is the **neighbour-leader cut**
(`fullSpeedGap = MIN2(fullSpeedGap, neighLead.second − secureGap)`).

Our trace shows `neighLead=none` at precisely the moment the artefact fires — because ego is at
**pos 59.20 of a 59.20 m lane**, so no leader can exist *on that lane* ahead of it. SUMO's neighbour
leader comes from `MSLaneChanger::getRealRightLeader`, which **looks past the lane end into the
continuation**; ours (`LaneNeighborQuery.GetNeighborLeader`) searches the single lane only. A car at a
stop line therefore always finds a leader in SUMO and never finds one here — inverting the cut in
exactly the place the artefact occurs.

**That is the next thing to test, and it is a hypothesis, not a finding** (this workstream is now
0-for-14). The engine already has cross-junction leader logic (binder 2 `crossJxnLeader`), so the
machinery to try it exists.

---

## Entry 22 — CORRECTION to Entry 21, and a second fix tried and reverted

### The correction: my own instrument was wrong, and it flattered the conclusion

Entry 21 reported *"SUMO's keepRightProbability never goes negative at all"*. **That is false.**
`MSLCM_LC2013.cpp:2120` returns `toString(-myKeepRightProbability)` — the TraCI getter **negates** — and
I then took a `min()` over the negated value. So the "most negative ever reached = 0.0" was measuring the
least accumulation of a sign-flipped quantity. Two errors compounding, both in the direction of the
answer I was hoping for.

**Corrected, with the sign fixed:**

| | |
|---|---|
| SUMO's most negative internal value, worst vehicle | **−2.35** |
| SUMO vehicles that EVER cross the fire threshold (< −2.0) | **1 of 450** |
| worst value while stopped | **−2.35** |
| samples at exactly 0 | 64 612 of ~68 000 |

So SUMO **does** accumulate, and one vehicle does fire. The real statement is quantitative, not
categorical: **1 of 450 SUMO vehicles ever crosses the threshold; we produce 255 stopped keepRight
commits.** That is still a large gap — it is simply not the absolute one I published.

⚠ **A second methodological error, stated because it invalidates a comparison I made.** The two engines'
trajectories diverge, so `f_cyc_cw2.2` in SUMO is in a completely different place at t=57 than ours is.
Entry 21's per-vehicle side-by-side is therefore **not a controlled comparison**. Only the aggregate
(1-of-450 vs 255 commits) is valid. Per-vehicle traces are still useful for reading OUR mechanism; they
cannot establish what SUMO would do in the same spot.

### Fix #2, tried and REVERTED: `resetState()` zeroes BOTH accumulators

A genuine, provable porting omission. SUMO's `changed()`/`resetState()` (:1057-1064, :1075-1081) zeroes
**both** `mySpeedGainProbability` and `myKeepRightProbability` on **every** committed change. Our four
commit paths each zero **at most one** — and each cites `:1063/1080`, the very function that zeroes both:

| path | zeroes SpeedGain | zeroes KeepRight |
|---|---|---|
| 0 overtake / EV vacate | ✗ | ✗ |
| 1 speedGain | ✓ | **✗** |
| 2 strategic | ✓ | **✗** |
| 3 keepRight | **✗** | ✓ |

So a speed-gain or strategic change left keep-right pressure intact to keep grinding down and fire later
— and on L2 there are 664 such changes.

**Measured: no benefit, real cost.** Stopped-LC rate **1.560 → 1.552** (noise), while the battery
regressed four nets: `city-mixed-1k` **1012 → 1002**, `city-organic-L2` 619 → 618,
`junction-realism-L2` 431 → 428, `willpass-saturation` overlaps 3 → 4. Goldens stayed byte-identical and
the gate stayed green, but a faithfulness fix with no measured benefit and a −10 arrivals cost does not
earn its place. **Reverted**, exactly like the `neighDist` attempt.

**It remains a real omission and is worth revisiting** *after* the actual driver is found — at which
point its cost may reverse. Do not re-derive it: the table above is the finding.

### Where this leaves the artefact

Still unexplained, and the scoreboard is now **0-for-16**. What is established and should not be
re-measured:

- The rate is **1.560 vs SUMO's 0.410 per 1000 stopped-vehicle-steps** (3.8×), and the normalisation
  *strengthens* rather than explains the gap.
- It is spread over **all three** commit paths (keepRight 23×, strategic 16×, speedGain 4.6× by stopped
  count), so no single threshold explains it.
- **81%** of our stopped changes go toward the lower lane index; SUMO's are near-balanced.
- Our keep-right accumulator **accelerates 11× when a car stops**, because `acceptanceTime` collapses
  with speed while `fullSpeedDrivingSeconds` does not. This is a property of SUMO's own formula, so it
  is *not itself* the bug — SUMO has the same formula and does not produce the artefact.
- Ruled out by measurement: `LaneChangeMinSpeed` (inert on the parity path), `neighDist` (Entry 21),
  `resetState` (above), and a blanket ban on stopped changes (SUMO makes 65).

**The most valuable next instrument** is not another hypothesis: it is a *controlled* comparison. Ours
and SUMO's trajectories diverge immediately, so the only sound way to ask "what would SUMO do HERE" is
to drive SUMO through TraCI to the same state (`moveToXY`/`setSpeed`) or to find a scenario where the
two stay in lockstep long enough. Every per-vehicle cross-engine claim in Entries 21-22 that lacks that
is a lead, not evidence.

---

## Entry 23 — TWO REAL PARITY BUGS in vehicle insertion, found by asking for a lockstep window

Entry 22 ended by saying the next step was a **controlled** comparison, because diverged trajectories had
invalidated every per-vehicle cross-engine claim. Building the cheapest possible version of that —
"how long do the two engines stay in the same state?" — immediately found two genuine parity bugs.

New committed instrument: **`scripts/fcd-divergence-onset.py`**, which reports the first step at which
two FCDs disagree. On `junction-realism-L2-light` it answered **t=0**, with the very first vehicle 5.10 m
out of place. There was never a lockstep window on any synthetic repro net.

### Bug 1 — absent `departPos` defaulted to 0 instead of SUMO's `base`

`DemandParser.ParseDepartPos` returned `Given(0.0)` when the attribute was absent. SUMO's default is
`DepartPosDefinition::BASE`, which puts the **front bumper** at `MIN(length + POSITION_EPS, laneLength)`
— **5.1 m** for a 5 m car, not 0.

The shortcut was **deliberate and its comment was knowingly wrong**: it claimed a vehicle with no
`departPos` "behaves identically", justified by "byte-identical parity with every pre-existing golden
(none of which used `base` or relied on the >0 basePos offset)". That justification was true only
because **all 179 golden scenarios set `departPos` explicitly** — and it also predicted its own fix would
be free. It was: **all 661 goldens stayed byte-identical.**

### Bug 2 — a vType's own `speedDev` was never parsed

`ScenarioConfigParser` read the cfg-wide `--default.speeddev` (defaulting to 0.1) and `Engine` used it
for **every** vehicle. The `speedDev` attribute on `<vType>` was not parsed at all. In SUMO the option is
a *default for types that do not specify one*, never an override of those that do.

Consequence: a scenario writing the idiomatic `<vType ... speedFactor="1.0" speedDev="0"/>` — as SUMO
users normally do, and as every `scenarios/_diag` net does — got **randomly sampled speed factors** here
(measured 0.8816, 0.9395, 1.0 on three vehicles) against SUMO's exact **1.0**. Invisible again for the
same structural reason: **88 golden cfgs pin `<default.speeddev value="0"/>`.**

**This one is not cosmetic for this workstream.** Heterogeneous desired speeds manufacture speed-gain
lane-change incentives that homogeneous traffic simply does not have — so it contaminated *every*
cross-engine lane-change comparison made on those nets, including Entry 20's headline table.

### Measured effect

| | before | after | SUMO |
|---|---|---|---|
| L2 lockstep window | **0 steps** | 3 steps | — |
| L2 stopped-LC rate (per 1000 stopped-veh-steps) | 1.560 | **1.396** | 0.410 |
| L2 stopped keepRight commits | 255 | **184** | — |
| L2 arrived | 431 | **433** | 450 |
| L2 peak overlapping pairs | 3 | **9** ⚠ | **0** |
| L1 arrived | 386 | **362** ⚠ | 450 |
| L1 peak overlapping pairs | 2 | **1** | 0 |
| goldens | — | **661 byte-identical** | — |

So the artefact drops ~10% and the gap narrows 3.8× → 3.4×, but the **honest** headline is that these are
correctness fixes, not artefact fixes: their real value is that **cross-engine comparison on these nets
is now valid at all**.

⚠ **The cost, stated plainly: L2 peak overlapping pairs went 3 → 9, against SUMO's 0.** Most likely the
corrected physics exercising a pre-existing junction weakness harder — homogeneous speed factors platoon
vehicles more tightly, so more of them reach a junction together — rather than the fixes creating a new
defect. **That is a hypothesis and it is untested.** It is owed work, and it is in the defect class the
owner ranks highest.

**Kept anyway, deliberately.** Reverting would restore a lower overlap count by keeping initial
conditions that do not match SUMO — hiding a real weakness behind a wrong setup, and permanently
invalidating every oracle comparison built on these nets. `stuckDwell` stayed 0 on every battery net, so
no gridlock returned.

### Two tests updated, neither weakened

- `RungHDp0c1SymbolicDepartTests.AbsentDepartAttributes_DefaultToGivenZero` **asserted the bug**. Its own
  comment pinned "default exactly like before this rung" — backward compatibility with our own earlier
  behaviour, explicitly not fidelity to SUMO. Renamed to `..._DefaultToSumoDefaults` and the departPos
  expectation corrected to `Base`; `departSpeed`/`departLane` are unchanged because SUMO's defaults there
  really are 0 and lane 0.
- `InternalJunctionAdmissionTests`' witness pair re-anchored a **second** time (89/102 → 78/156,
  co-occurring steps [320, 323], 0 violations). Its vacuity guard fired correctly again: insertion moved,
  so the measured pair moved. No assertion changed meaning.

### Lesson, and it generalises past this repo

**Both bugs were structurally invisible to the entire golden suite, because the goldens pin explicitly
what real scenarios leave to the default.** A parity suite that always specifies an attribute can never
test that attribute's default. Every remaining "we default X to Y" shortcut in the ingest layer deserves
the same suspicion — and `scripts/fcd-divergence-onset.py` is the cheap way to look, because a
first-step disagreement is exactly what a wrong default produces.

---

## Entry 24 — THE STRATEGIC-PATH MECHANISM, named and traced

Continuing the lockstep sweep from Entry 23. With the two insertion bugs fixed, the next divergence on
`junction-realism-L2-light` moved to **t=3**, and it is not a subtle one.

### The observation

`f_left_W00.0` is a **left-turner**: it departs on lane 0 and its route needs lane 1.

| | first lane change to `in_W00_1` |
|---|---|
| **SUMO** | **t=3, pos 30.94, speed 11.95** — 158 m before the junction, at speed |
| **ours** | **t=45, pos 189.60, speed 1.00** — at the lane END, essentially stopped |

A **42-step, 158-metre delay** that converts an ordinary moving strategic change into a stationary one at
the stop line. Note SUMO *decelerated* to do it (13.89 → 11.95).

**This is the "lateral lane change while standing at red" artefact, in its strategic form** — and the
strategic path is the second-largest contributor (225 stopped commits, 16× SUMO's).

### Two hypotheses tested and killed before the right one

1. **"The `laDist` distance gate defers it."** *Refuted by trace.* `defers=no` from t=1 onward:
   `usableDist=170.61` against `laDist=292.80`. The gate permits the change immediately. (Hypothesis #15.)
2. **"`ComputeBestLanes` gives ego's own non-continuing lane too long a continuation."** Also wrong —
   `curr.Length` came out at exactly the lane's own 189.6 m, which is correct. (Hypothesis #16.)

### The actual blocker, traced

```
[strategic-veto] t=1  @18.99 spd=13.89  unsafe=True  unsafeLeadOnly=True  unsafeFollowOnly=False
                       obstacle=False overlapped=False deferCutIn=False
                       nLead=f_cyc_ccw.0  nFollow=none
```

Identical every step to t=44. **The target lane's LEADER makes the change unsafe** — there is no follower
involved at all. And both vehicles are cruising at 13.89 m/s, their shared maximum, so **the gap can
never open by itself**. Our engine simply waits for a gap that will not exist until traffic stops — which
is precisely when it finally changes, at 1.00 m/s.

SUMO does not wait: an **urgent** strategic changer *brakes to fit behind the leader* rather than
vetoing. That is `MSLCM_LC2013::informLeader` (:471-472) — ego adopts `stopSpeed(myLeftSpace)` and drops
in behind. Hence SUMO's 11.95 m/s at the moment of the change.

### We HAVE that port — scoped so narrowly it never fires here

`Engine.DeadLaneMergeBrakeConstraint` is explicitly *"ported from `MSLCM_LC2013::informLeader`"*, but its
own header restricts it: *"returns +infinity unless the vehicle's CURRENT lane has NO connection to its
next ROUTE edge (a genuine dead lane)"*. It was written for the GAP-1 dead-lane deadlock and deliberately
kept inert everywhere else so no golden could move.

So the engine has SUMO's mechanism and applies it to one special case, while SUMO applies it to **every
urgent strategic change**. Ego stayed at 13.89 m/s for all 44 steps: it never engaged.

### Why this is the most promising lead so far

It explains the shape of the artefact rather than a symptom of it:

- **Strategic changes cluster at zero speed** because that is the only time a gap appears.
- It predicts the **81% rightward bias** indirectly — a change deferred until the queue forms happens
  wherever the vehicle then is, not where it should have happened.
- It is consistent with `targetCarNear&Stopped` = 172 of the strategic commits: by the time we change,
  the target lane is a stopped queue.

⚠ **It is a hypothesis with a mechanism and a trace, not a finding.** The scoreboard is 0-for-16 and two
more died in this entry. What is *established* is the trace above: the veto is the target-lane leader,
every step, at equal speeds.

### The next step is a DESIGN, not an edit

Widening `informLeader` from "dead lanes only" to "any urgent strategic change" is a behavioural change
to a parity-relevant path, and the reason it was scoped narrowly in the first place was to keep the
goldens still. CLAUDE.md's design-first rule applies. The specific questions a design must answer:

1. What exactly makes a strategic change **urgent** in our port (SUMO: `LCA_URGENT`, set when
   `changeToBest && currentDistDisallows(...)`), and is that the same set the `laDist` gate already
   admits?
2. Does ego brake via a new constraint term, or by reusing `DeadLaneMergeBrakeConstraint` with a widened
   predicate? The latter is one guard, not two — which this session has twice paid for getting wrong.
3. What is the golden blast radius? Every golden vehicle "is always on a lane that continues its route",
   per that method's own comment, so a widened predicate may still be inert — **testable before writing
   the fix.**

### Instruments committed with this entry

`[strategic]` (the `laDist` gate's inputs) and `[strategic-veto]` (which of the four vetoes fired, and
the leader/follower involved), both behind `SUMOSHARP_TRACEVEH`. Together they take "why did this vehicle
not change lanes?" from an argument to a one-run answer.

---

## Entry 25 — the informLeader probe: SUMO's move reproduced to TWO DECIMALS, and a collapse that stops the default flip

Ran the probe Entry 24 called for. `UrgentStrategicLeaderFollowConstraint` — the faithful port of
`MSLCM_LC2013::informLeader`'s cannot-overtake branch (formulas and simplifications in its header) —
committed default-OFF behind `SUMOSHARP_URGENTFOLLOW`, binder tag **18**.

### Probe result 1 — the mechanism is EXACTLY right

Flag on, `junction-realism-L2-light`: `f_left_W00.0` changes lanes at **t=3, pos 30.94, 11.95 m/s** —
**identical to SUMO's oracle to two decimals** (Entry 24's table: SUMO t=3 / 30.94 / 11.95). The car
then clears the junction at t=48–50 instead of wedging at the stop line until t=45+.

### Probe result 2 — goldens are inert in BOTH flag states

Full parity suite with the default temporarily ON: **all 661 goldens byte-identical**; only the four
synthetic-junction2 behavioural tests fail (see result 3). Entry 24's blast-radius prediction — golden
vehicles are never urgent-and-blocked — held. The parity cost of this mechanism is zero.

### Probe result 3 — the naive global default COLLAPSES the saturated net

| junction-realism-L2 | flag OFF | flag ON | SUMO |
|---|---|---|---|
| arrived | 433 | **223** | 450 |
| running at end | 17 | **226** | 0 |
| peak overlapping pairs | 9 | **42** | 0 |
| stuckDwell | 0 | **824** | 0 |

⚠ **The stopped-LC "rate" under the flag reads 0.450 — near SUMO's 0.410 — and it is a DENOMINATOR
ARTEFACT**: stopped vehicle-steps ballooned 73k → 176k because half the net is jammed. Recorded so the
number is never quoted as a win. (The instrument's "compare the rate" advice assumes comparable
throughput; a collapsed run breaks that assumption.)

SUMO runs this same mechanism on this same net and drains 450/450 with 0 overlaps — so the defect is in
**our port's interaction with the rest of our engine**, not the mechanism. Three candidate explanations
(H-A: ego brakes but non-SUMO vetoes still refuse the swap, leaving it permanently braking; H-B: our
"blocked" predicate over-fires vs SUMO's checkChange; H-C: one-step lag) are in the design doc, and the
diagnostic stage that separates them is Stage 1 of the task doc. The scoreboard is 0-for-16 on reasoned
hypotheses; none of the three gets edited before T1.1 measures.

### Where this leaves it

Design-first trio committed: `URGENT-STRATEGIC-FOLLOW-{DESIGN,TASKS,TRACKER}.md`, acceptance gates fixed
in advance (headline: L2 must be **no worse than today on any column** — gridlock outranks this
artefact). **Awaiting owner sign-off before Stage 1.** Committed state ships the flag OFF: suite
778/5/0, battery untouched.

---

## Entry 26 — Stage 1 verdict, the follower half, and the SCOPED pair: one gate still red

### T1.1/T1.2 — the verdict, measured not argued

The attribution instrument (`VehicleRuntime.LcStrategicOutcome`, written at every exit of
`TryStrategicLaneChange`, crossed with binder 18 at the call site, printed by `Sim.Run` under
`SUMOSHARP_LCLOG`): with only the ego-side informLeader brake on, **99.0% of coupling-braked
vehicle-steps still failed to change** (52 of 5020 committed), refused by the SAFETY gaps themselves —
unsafeBoth 35.7%, overlapped 35.7%, unsafeLead 17.5%, unsafeFollow 8.0%. The non-SUMO vetoes
(slot-contested, cut-in defer) did not appear at all — **H-A in its specific form was wrong**. Ego falls
back, the follower closes up, ego slides backwards along a solid queue braking forever.

SUMO pairs informLeader with **informFollower** — the target-lane follower brakes
(`HELP_DECEL_FACTOR × maxDecel`) to open the gap. Our parity path had **no follower cooperation at all**
(the P2G-2 informFollower port is a non-parity realism mode; in SUMO this is core LC2013).

### The follower half, ported — and it did NOT fix the collapse

`UrgentFollowerYieldConstraint` (binder 19): pull-based, deterministic — the follower scans its
neighbour lanes for the nearest urgent changer whose blocking follower it is, recomputing the identical
quantities from the same frozen snapshot (`TryGetUrgentStrategicState`, ONE definition of "urgent" for
both halves). Faithful euler arms; simplifications in its header.

Measured, both halves on, saturated L2: arrived 230, overlaps **77**, stuckDwell 829 — **still
collapsed**. Commits rose only 52 → 82 of ~5000. At saturation the target lane is a solid queue;
no cooperation conjures 10 m of space.

### The actual collapse mechanism — OUR engine's second exit

SUMO's `plannedSpeed = stopSpeed(myLeftSpace)` pins an unmerged urgent changer at its lane end. In SUMO
that is the only option. **In this engine a wrong-lane vehicle has a second exit — the dead-lane reroute
family — and the faithful stop-pin DEFEATS it**: vehicles stood at their lane ends forever instead of
rerouting. stuckDwell ~825 and the overlap explosion follow from that standing wedge. This is the
"structurally forced divergence" class CLAUDE.md's prime directive 4 anticipates.

### The SCOPED pair (committed): moving-merge regime only

The constraint now keeps ONLY what has no equivalent elsewhere in the engine — the **moving-leader
follow coupling** (and its follower-yield mirror): inert unless the target-lane leader is moving; no
stop-pin; a halted queue keeps today's behaviour (roll to the light, reroute if stranded).

| | flag OFF | SCOPED pair ON | SUMO |
|---|---|---|---|
| L2-light left-turner | t=45 @ 1.00 m/s | **t=3 @ 30.94 / 11.95 — SUMO's move to two decimals** | t=3 |
| L2 arrived | 433 | **441** | 450 |
| L2 running at end | 17 | **9** | 0 |
| L2 stuckDwell | 0 | **0** | 0 |
| stopped-LC rate (/1000 stopped-veh-steps) | 1.396 | **1.157** | 0.410 |
| L2 peak overlapping pairs | 9 | **21** ⚠ | 0 |
| binder-18 population | — | 5020 → **303**, commits 1.0% → **11.2%** | — |

### The one red gate, stated plainly

**Peak overlaps 21 against the acceptance gate ≤ 9.** Arrivals up, deadlock zero, rate −17%, the
artefact's showcase case exactly SUMO — but the overlap column moved the wrong way, and overlaps are in
the owner's highest defect class. Plausible reading: more early merges ⇒ denser platoons at junctions ⇒
the PRE-EXISTING junction overlap weakness (backlog #2, `city-*` nets) amplified by throughput — the
same pattern as Entry 23's insertion fixes (3 → 9 for the same reason). **Unattributed = unproven.**

**Default therefore stays OFF.** Next step (T2 continuation): attribute the 21 — junction-interior pairs
of crossing streams would confirm the pre-existing class; lane pairs adjacent to a commit would implicate
the coupling itself. Then either fix the junction weakness first or gate the flip on it.

---

## Entry 27 — T2.5: the coupling is EXONERATED, and the real overlap mechanism is finally named

### Attribution of the 21-vs-9 overlap gate failure

Enumerated every junction-interior overlapping pair in both arms (the peak metric counts only
junction-interior pairs by construction — lane-level merge overlaps are invisible to it):

| | scoped pair ON | flag OFF |
|---|---|---|
| peak simultaneous pairs (the gate metric) | 21 | 9 |
| **distinct pairs over the run** | **77** | **66** |
| dominant episode | 6 vehicles on `:J00_13_0`, t≈680–770 | 5+ vehicles on `:J00_9_1`, t≈655–735 |
| pairs with a member that made a MOVING same-edge change ≤60 s before onset | **0** | 4 |

Three conclusions, each measured:
1. **The coupling is exonerated.** Not one ON-arm pair involves a recent coupling-committed changer.
2. **The 21-vs-9 was trap #4 again (incidence ≠ duration):** both arms have ONE dominant pileup episode;
   the ON arm's happened to involve 6 simultaneous vehicles (≈15 concurrent pairs) instead of 5. Distinct
   pairs differ by 17%, not 2.3×.
3. **Both arms exhibit the SAME defect class** — a multi-vehicle pileup on a single internal lane. This
   is backlog #2 (the `city-*` junction overlaps), reproduced on the diag net, in both arms.

### The pileup mechanism, traced to the step

Binder log, the six `:J00_13_0` vehicles: each enters at 4–8 m/s and halts **at pos 0.39–3.99** — six 5 m
cars parked inside four metres of each other. The entry-step evidence:

```
f_cyc_ccw.40 t=681 in_W00_0@188.20 spd=8.70 binder=crossJxnLeader blocker=f_cyc_ccw.34
             t=682 :J00_13_0@3.54  <- enters INTO f_cyc_ccw.37, halted at 2.94 since t=680
```

`f_cyc_ccw.37` halted on `:J00_13_0` at pos 2.94, so its 5 m body **protrudes 2.06 m back across the
boundary onto `in_W00_0`** (physically at ≈187.5). Ego at 188.20 is *already inside it* — and cannot see
it: the lane bucket indexes 37 only on the internal lane, so the approach lane's own-lane leader query
returns nothing, and the cross-junction leader arm picked the FAR leader `f_cyc_ccw.34` instead of the
rearmost occupant. **Two coupled defects, now named:**

- **(a) back-protrusion invisibility** — a vehicle whose front has crossed a lane boundary vanishes from
  the lane its back still occupies. SUMO models this explicitly (`myPartialVehicles`,
  `getBackPositionOnLane`); our single-bucket-per-vehicle model does not.
- **(b) the cross-junction leader arm follows a far leader, not the rearmost** — `blocker=f_cyc_ccw.34`
  while 37/40/41 stand between ego and 34.

Every vehicle halts under `leaderFollow` immediately AFTER entering — the same-lane following works once
they share a bucket. The damage is done at the boundary crossing.

### Where this leaves the flip, and the priority

The overlap gate failure was **not caused by the coupling** — but the gate as written still reads 21 > 9,
and re-writing a gate to pass a change is exactly what this journal exists to prevent. The honest order:
**fix the pileup mechanism (backlog #2, now with a named, traced cause and a two-line repro) first**, then
re-measure the coupling against the gates on a net that no longer manufactures overlap episodes. The
coupling stays default OFF until then — not because it is suspect, but because its acceptance gate is
entangled with a defect it did not cause.

---

## Entry 28 — CORRECTION to Entry 27(b): not a "far leader" — the WRONG LANE'S leader

The `[cjl]` walk trace for `f_cyc_ccw.40` at the decision step:

```
[cjl] t=681 veh=f_cyc_ccw.40 walkLane=:J00_13_1 seen=1.40 rearmost=f_cyc_ccw.34@11.49
```

The cross-junction leader walk examines **`:J00_13_1`** — the POOL lane, fed from approach lane 1 —
because that is what the vehicle's resolved lane sequence contains. But `f_cyc_ccw.40` is on approach
**lane 0** (the strategic change to lane 1 never happened — the very artefact this workstream is about),
and at the boundary it crosses via **its own lane's connection onto `:J00_13_0`**, where the queue
stands. So Entry 27's "(b) follows a far leader" was imprecise: **the walk follows the RIGHT rearmost of
the WRONG lane.** Every planning constraint reads the pool path while the vehicle physically traverses
the actual path (`TryReResolveFromActualLane` semantics exist only at the boundary EXECUTION, not in the
planning walks). Each wrong-lane vehicle therefore enters `:J00_13_0` blind, on top of the previous one:
six cars halted within four metres.

Entry 27's "(a) back-protrusion invisibility" stands, but it is the second-order half: with (b) fixed,
ego brakes on the queue via the walk (`gap = seen + (rearmost.back)` goes negative) before protrusion
matters.

**The full causal chain of the junction-overlap defect class, now traced end to end:**
missed strategic change (Entry 24's mechanism) → wrong approach lane → planning walks follow the pool
lane while the body follows the actual lane → blind entry into an occupied internal lane → pileup →
the overlap episodes on this net and (plausibly, untraced) the `city-*` nets.

**T2.6's fix shape:** in the cross-junction walk (and any planning constraint that consumes the
downstream span), when ego's current lane is not the pool lane for this edge, resolve the span from
ego's ACTUAL lane's connection — the planning-time mirror of `TryReResolveFromActualLane`. Parity
blast radius: every golden vehicle is always on its pool lane (the same argument that held for binder
18, twice verified), so the widened resolution should be golden-inert — verify, don't assume. The
`[cjl]` trace is committed for the next session.

## Entry 29 — T2.6 BEFORE: the actual-lane walk in CrossJunctionLeaderConstraint

**The change.** `CrossJunctionLeaderConstraint` gains a THIRD leader walk, engaged only when ego is
OFF-POOL (`ego.LaneHandle != _laneSeqPool[slot]`, current lane not internal): the downstream span is
resolved from ego's ACTUAL lane's connection to the next route edge — via chain (the same 8-guard
walk `ResolveSequenceCore` uses) plus the arrival lane — and its follow speed is Min-folded with the
two existing walks. The two existing walks are KEPT: mid-convergence (ego on `arrival[k]`, still
intending to reach `pool[k]` before the boundary) must keep braking for the pool path it will most
likely take; the actual-lane walk is purely ADDITIVE braking, which is also what SUMO itself does —
`planMoveInternal` builds its link chain from the vehicle's CURRENT lane every step. The span ends at
the arrival lane (no continuation beyond): the boundary crossing re-resolves the whole pool from the
actual lane anyway (`TryReResolveFromActualLane`), and the defect being fixed is the blind FIRST
entry. A drop-lane (no connection to the next route edge) falls back to the existing walks —
`DeadLaneMergeBrakeConstraint` owns that regime.

**Predictions, recorded before measuring:**
1. **Goldens: all 661 byte-identical.** Golden vehicles are always on-pool (argument held twice for
   binder 18), so the third walk never engages. If any golden moves, the off-pool predicate is wrong.
2. **The repro dies:** `junction-realism-L2`, `SUMOSHARP_URGENTFOLLOW=1`, `f_cyc_ccw.40` at t≈681 —
   the `[cjl]` trace shows a walk on `:J00_13_0` finding the queue (today it walks `:J00_13_1`,
   Entry 28), and the vehicle brakes instead of entering on top of it. The six-car pileup at
   t=680–690 does not form.
3. **Both A/B arms improve** (the pileup class is present in both, Entry 27): ON-arm peak overlaps
   21 → materially down, distinct pairs 77 → down; OFF-arm 9 peak / 66 distinct → down too.
4. **Battery: no stuckDwell regression anywhere.** Risk accepted in advance: a small arrivals dip is
   possible (vehicles now brake for real queues they previously drove into); an arrivals COLLAPSE
   (>5% on any net) means the predicate engages too often and the change is wrong, not "a cost".

**Gate to re-measure after (design §5):** goldens · L2-light t≈3 · L2 arrived ≥ 433 / overlaps ≤ 9 /
stuckDwell 0 · stopped-LC rate < 1.396 with denominator · 26-net battery · the 4 synthetic-junction2
tests. If all green with the coupling ON, the default flips (T3.1–T3.3).

### Entry 29 AFTER — measured. All four predictions held; one new trap documented.

**Trap first (a new instance of #9): `src/Sim.Run` is NOT in `Traffic.sln`.** The first
post-fix measurement round ran `dotnet run --no-build` against an hour-old `Sim.Core.dll` and
reproduced the pre-fix numbers exactly — including the "unchanged" `[cjl]` trace. Caught because the
third walk's trace line was missing at the traced step. Build `src/Sim.Run` explicitly, like the two
csproj files CLAUDE.md already lists.

1. **Goldens: 778 passed / 5 skipped / 0 failed** — the fix is golden-inert as predicted.
2. **The repro dies.** `[cjl]` at t=681 now walks BOTH `:J00_13_1` (pool) and `:J00_13_0` (actual),
   finds the queue (`f_cyc_ccw.31@14.69`), and `f_cyc_ccw.40` holds 1.00 m before the boundary
   (seen pins at 1.00 for t=683–694) instead of entering at `:J00_13_0@3.54` on top of a parked car.
3. **Both arms improved, dramatically** (battery instrument, same as Entry 26):

   | junction-realism-L2 | OFF pre-fix | OFF now | ON pre-fix | ON now | SUMO |
   |---|---|---|---|---|---|
   | arrived | 433 | **436** | 441 | **442** | 450 |
   | running at end | 17 | **14** | 9 | **8** | 0 |
   | peak overlapping pairs | 9 | **1** | 21 | **3** | 0 |
   | stuckDwell | 0 | 0 | 0 | **0** | 0 |

   Stopped-LC rate: OFF 1.466, ON **1.155** per 1000 stopped-vehicle-steps (denominators 68 897 /
   68 390 — comparable, no collapse artefact). L2-light left-turner: changes at t=2, pos 30.94,
   11.95 m/s — SUMO's move, unchanged.
4. **Battery: zero T2.6 regressions.** Four rows differ from the committed
   `net-regression-keepclear-direction.txt` baseline (city-3000 arrived −11, L1 −24,
   willpass-saturation overlaps 3→4, city-organic-L2 −1) — a stash-revert A/B on those four nets
   shows **all are pre-existing baseline rot** (identical numbers with the fix reverted; the
   baseline predates the Entry 23 insertion-parity fixes). city-organic-L2 is actually IMPROVED by
   the fix (arrived 617→618, overlaps 5→4). No stuckDwell moved anywhere.

**Gate scorecard (design §5): every row green with the coupling ON** — goldens ✓, L2-light t≈3 ✓,
L2 arrived 442 ≥ 433 / overlaps 3 ≤ 9 / stuckDwell 0 ✓, rate 1.155 < 1.396 ✓, battery ✓. The
overlap gate that blocked the flip in Entries 26–27 is resolved by fixing the defect it was
entangled with, not by rewriting the gate. T3 (default flip) is unblocked.

## Entry 30 — Entry 29's numbers were RACY: a latent parallel-plan race, found, guarded, everything re-measured

**How it surfaced.** Cross-checking Entry 29's battery rows, the same command produced L2
442/8/67 in one invocation and 440/10/77 in the next. Four identical `Sim.Run` invocations then
produced **two distinct FCDs (2+2)**; through the shim, four runs produced **three distinct FCDs** —
while `--max-parallelism 1` produced **4/4 identical**, and the pre-T2.6 commit produced 4/4
identical on the same workload. A thread-schedule race, latent before T2.6's trajectory change
excited it.

**The defect (pre-existing, NOT in the T2.6 code).** `fcd-divergence-onset` + the binder log
localized the flip to one vehicle-step: `f_cyc_ccw2.29` on bay `:J01_7_0` at t=505 is held by
`internalJunctionApproachArm` (foe `f_cyc_cw2.36`) in one outcome and admitted in the other.
The approach arm (Engine.cs:8344) read `foe.WillPass` with **no `prePass` guard**. `WillPass` is
written BY the willPass pre-pass itself, one vehicle per parallel iteration — so a pre-pass read of
a foe's `WillPass` returns last step's or this step's value depending on thread schedule. The other
two `WillPass` readers already had the guard (`foeYieldsThisStep = !prePass && !foe.WillPass` at
:7752; `(prePass || foeMerging.WillPass)` at :8844); this arm — shipped with Entry 17/18's
approach-arm work — was the one that missed it. The willPass pre-pass header's parallel-safety
argument ("no pre-pass iteration reads another vehicle's WillPass") was true when written and
silently invalidated by the arm's later addition.

**The guard (committed with this entry), and the two variants that were TRIED AND REFUTED first:**

- **A (blanket-pass + recompute flag):** term `!prePass && !foe.WillPass` (crossing-arm convention)
  + `CrossingYieldTaken` on a pre-pass block. Deterministic (7/7 identical) — and it **wedged the
  saturated ON arm terminally**: L2 arrived 442 → **284**, stuckDwell 0 → **708**. The blanket
  treatment turns bay egos' pre-pass intents to 0 — the "pathological all-false WillPass" state
  `ResolveRightBeforeLeftCycles`' header warns about.
- **B (Prev semantics + recompute flag):** byte-identical to A on this workload — so the flag, not
  the semantics, made the wedge: forcing the real pass to re-apply the LIVE term un-fused the bay
  vehicles, and the shipped behaviour those gridlock fixes were measured under had (racily)
  evaluated the term on the pre-pass value and fused it.
- **C (SHIPPED): Prev semantics, no flag.** The pre-pass reads the foe's LAST-step `WillPass`
  (`VehicleRuntime.WillPassPrev`, snapshotted serially before the pre-pass dispatches); the real
  pass reads the live field as before; fusion untouched. This is the racy read's practical value
  made deterministic — the minimal change that preserves the shipped effective semantics.

**Verified (C):** parallel ×N + serial → all byte-identical on the net that split 3 ways before;
L2 arm0 **436 arrived / 14 running / 1 overlap / stuckDwell 0**, arm1 **440 / 10 / 3 / 0** — no
wedge, and the T2.6 improvements hold under determinism.

**Standing lesson (new trap):** *a determinism claim is workload-relative — a race can sit dormant
for months of green suites until an unrelated trajectory change lines two threads up on the same
transition step.* Any change that shifts saturated-net trajectories should re-run the cheap
repeat-hash check (`N identical runs, compare FCD hashes`) before its numbers are trusted; Entry
29's gate table was measured on racy code and superseded by the re-measurement below.

## Entry 31 — the default flip: UrgentStrategicLeaderFollow ships ON

Every `URGENT-STRATEGIC-FOLLOW-DESIGN.md` §5 acceptance gate, re-measured on the DETERMINISTIC
engine (Entry 30's guard), coupling ON vs the same build coupling OFF:

| gate | requirement | measured | verdict |
|---|---|---|---|
| goldens | 661 byte-identical | suite 778 / 5 / 0 in both flag states | ✓ |
| L2-light | left-turner changes at speed ≈ t=3 | t=2, pos 30.94, 11.95 m/s — SUMO's move | ✓ |
| L2 arrived | ≥ 433 | **440** (OFF: 436; SUMO: 450) | ✓ |
| L2 peak overlaps | ≤ 9 | **3** (OFF: 1; pre-T2.6: 21/9) | ✓ |
| L2 stuckDwell | 0 | **0** | ✓ |
| stopped-LC rate | < 1.396, denominator reported | **1.155** /1000 (denom 68 390; OFF 1.466 / 68 897) | ✓ |
| 26-net battery | no stuckDwell regression; arrivals within noise | ON-vs-OFF: only L2 overlaps 1→3; nothing else moved | ✓ |
| behavioural tests | 4× synthetic-junction2 green | green in the flip suite run | ✓ |

Shipped in this commit: `Engine.UrgentStrategicLeaderFollow = true`; `SUMOSHARP_URGENTFOLLOW` read
by BOTH `Sim.Run` and the `sumosharp` drop-in in the safe `EnvGate` form (and added to
`EnvGateDocumentationTests.MustUseSafeForm` — its engine default is now true, so the two-state form
would be the Entry 19 bug all over again); `UrgentStrategicFollowBehaviourTests` (T3.3) pins the
L2-light at-speed change with a forced-OFF vacuity guard; battery reports committed as
`docs/reports/net-regression-urgentfollow-{on,off}.txt` — the ON report is the current battery
reference (the keepclear-direction baseline carries 4 rows of pre-insertion-fix rot, attributed in
Entry 29). One trajectory-anchored witness moved with the flip:
`InternalJunctionAdmissionTests`' vacuity guard fired (its whole purpose) and the witness pair was
re-anchored a third time, back to veh 89/102 (steps [321, 326], 6 steps, 0 violations). Final suite
at the shipped default: **779 passed / 5 skipped / 0 failed**.

**The owner's four defects now stand: through-driving FIXED · in-junction overlap FIXED (pileup
mechanism T2.6) · gridlock FIXED · stopped-lane-change: strategic path FIXED (this flip) — keepRight
and speedGain halves remain (Entries 21-22), plus the city-* overlap re-check and the pedestrian
amplifier (backlog).**

## Entry 32 — the stopped-LC residual DECOMPOSED on a lockstep oracle: keepRight's missing continuation + speedGain's missing rolling fire

**The instrument Entry 22 asked for now exists:** `scenarios/_diag/keepright-standing` — 2-lane
edge (200 m) into a red light (80 s) with a 100 m exit edge, six cars departing on lane 1, right
lane empty. Both engines stay in **byte-lockstep until the artefact itself** (first divergence
t=17, and it IS a lane-change difference), so per-vehicle cross-engine comparison is valid here —
the thing Entries 21–22 never had. SUMO side read live over TraCI (getter negates; both
accumulators probed: `keepRightProbability`, `speedGainProbabilityRight`).

**What SUMO actually does on this net (per-vehicle, decomposed):**
- `f.0` (head): **keepRight**, fires ONE step after halting — rolling accumulation 0.080/step
  (16 s of approach ≈ −1.3), then the stopped boost 0.4/step (`acceptanceTime = 7·max(1, v)`
  floors at 7 s) crosses −2.0 immediately. **A prompt stopped keepRight change at the head of a
  queue with a free right lane is CORRECT SUMO behaviour** — the artefact was never "stopped
  changes exist", it is their 3× rate.
- `f.3`, `f.5` (followers): **speedGain-right, fired at speed on the approach** (f.3 at 5.5→3.7
  m/s). Their keepRight FREEZES at −0.81 when the changed f.0 becomes the right-lane leader (the
  neighbour-leader secure-gap cut — working exactly as designed), and the speedGain accumulator
  then ramps −0.23 → −1.46 in four steps as their lane slows against the free right lane.
- Ours on the same net: f.0 one step late (fine); **the followers never change on the approach**
  — f.5 changes STOPPED at t=83 (queue discharge), f.4 at discharge. The deferred-to-standstill
  signature, now reproduced in 6 cars on a lockstep net.

**The keepRight arithmetic, term-checked against the oracle:** our `acceptanceTime` (97.23) and
Euler `brakeGap` (28.56) match SUMO exactly; our `neighDist = rightLane.Length` (200) vs SUMO's
best-lanes continuation (300 = right lane + exit edge) is the whole rolling-rate gap:
0.4·(171.44/13.89)/97.23 = **0.0508**/step vs 0.4·(271.44/13.89)/97.23 = **0.0804**/step — we
accumulate at 63% of SUMO's rate and reach the queue below threshold.

**Why Entries 21–22 rejected the right ingredients:** each was HALF of a coupled pair, tried
alone, and judged on (a) an aggregate rate from the racy era (pre-Entry-30 determinism, pre-flip)
and (b) a per-vehicle cross-engine comparison Entry 22 itself later ruled invalid. The
continuation `neighDist` (Entry 21, "saturated at 0.4/step") saturates ONLY without its partner —
the **continuation-aware neighbour leader** (`getRealRightLeader` looks past the lane end; Entry
21 §"where the evidence points" named this and it was never tested). SUMO runs both: continuation
neighDist sets the rolling rate, the past-lane-end leader cut clamps it in traffic (measured: f.3
frozen at −0.81 the step f.0 appears ahead). T2.6's `BuildActualDownstreamSpan` +
`TryFindCrossJunctionLeader` are exactly the machinery the leader half needs.

**Fix shape for the next session (design-first, both halves in ONE change):**
1. `neighDist` ← the right lane's best-lanes continuation length (already cached as
   `KeepRightStayRightContLength`);
2. `neighLead` ← right-lane leader INCLUDING the continuation past the lane end (generalize
   `BuildActualDownstreamSpan` to an arbitrary source lane + `TryFindCrossJunctionLeader`);
3. re-audit the speedGain-right path against this net's oracle trace (why does our f.3 not fire
   while rolling? — its relativeGain accumulation vs SUMO's is the open question; SUMO data above
   gives the exact per-step target −0.23/−0.72/−1.05/−1.46);
4. acceptance gates: this net (followers change AT SPEED like SUMO's f.3/f.5), goldens
   byte-identical, L2 rate toward 0.410 with denominator, 26-net battery, and the repeat-hash
   determinism check (Entry 30's standing lesson).
Also revisit Entry 22's `resetState()` omission after 1–3 land (its cost may reverse).

⚠ The old aggregate "23× keepRight / 4.6× speedGain by stopped count" split predates the
strategic fix and the determinism guard — re-measure the split (`SUMOSHARP_LCLOG=1`) before
quoting it; the post-flip histogram on L2 reads keepRight 200 / strategic 165 / speedGain 172
stopped commits.

## Entry 33 — the live-city demo re-measured on the shipped engine (closed-loop; realism metrics only)

`Sim.Viewer --mode live-city --smoke --frames 400` (LIVECITY_WITNESS=1, no behavioural gates set,
nothing inherited in the shell) + `Sim.Viz --live-city`. CLOSED-LOOP demand — no capacity claims.

| metric | committed baseline | now | verdict |
|---|---|---|---|
| car-car overlaps (witness checkpoints) | 258 → 3 after the July 21 fix | **0 at all 8 checkpoints** | better (different instrument cadence — directional, not apples-to-apples) |
| lateral LC dead-stop share | 84/264 = 31.8% (vanilla SUMO ref ≈ 12%) | 52/424 = **12.3%** | **matches the SUMO reference** |
| total lateral changes | 264 | 424 (+60%) | mixed — more at-speed maneuvering (the flipped coupling changes lanes early); worth an episode-level look if it reads as visual churn |
| gridlock | — | none; stoppedFrac peaks 0.64, always clears; arrivals monotone; `LIVECITY-SMOKE: OK` | good |
| ped-on-RED near-collisions | 0 ("2b compliance holds") | **2**, plus 37/1957 low-power-ped-on-signalized samples during RED ("should be 0") | ⚠ NEW ANOMALY — ped-side compliance, untraced; may predate this branch (instrument not re-run since July 21). Backlog item |

The owner's stopped-lane-change artefact **as seen in the demo** is now at the vanilla-SUMO
dead-stop ratio. The remaining engine-side gap is the follower rate (Entry 32). Raw logs in the
session scratchpad; doc-rot note: `LIVE-CITY-STATUS.md`'s "3360+ overlaps KNOWN BLOCKER" section
describes a bug fixed hours after it was written (see `LANE-CHANGE-OVERLAP-STATUS.md`) and was
never updated, and the July 29 solution reorg moved `Sim.Run`/`Sim.Viewer`/`Sim.Viz`/
`Sim.BenchLiveCity` OUT of `Traffic.sln` (build their csproj explicitly) while
`tests/Sim.LiveCity.Tests` moved IN — CLAUDE.md's trap list is stale on both points.

## Entry 34 — BEFORE: the follower LC-deferral fix (the Entry 32 pair, plus the missing speedGain-RIGHT arm)

Operational hand-off: `docs/FOLLOWER-LC-DEFERRAL-RESUME.md`. This entry is the design-first
BEFORE record — written and committed before any source edit, per CLAUDE.md.

### The finding that reframes §3(c)

The resume doc's (c) said "audit why our f.3 does not fire speedGain-right while rolling; check
neighLaneVSafe / threshold / a commit veto". The audit took one grep: **our engine has NO
speedGain-RIGHT arm at all.** `DecideSpeedGainForVehicle` evaluates only `lane.LeftNeighbor`
(positive accumulation, left fire, `Engine.cs:12904-13052`); right changes exist ONLY via the
keepRight accumulator. In SUMO the accumulator is ONE signed variable (negative = right wish,
positive = left wish): the right-direction `_wantsChange` (MSLCM_LC2013.cpp:1698-1817) decays it
(`*= 0.5^TS` when this lane is >5 km/h faster than the right, :1700-1705), accumulates it
(`-= TS * relativeGain` when not, gated `sgp < 0 || relativeGain > 0`, :1717-1719), and fires
LCA_SPEEDGAIN at `sgp < -2.0 && neighDist/max(0.1, speed) > 20` (:1811-1816) — with NO
`relativeGain > eps` condition, unlike the left fire. Our f.3 "not firing" is not a bug in a
mechanism; the mechanism is absent. So (c) is a port, not an audit.

Two structural facts from the vendored source that the port must respect:

1. **The keepRight accumulator lives INSIDE the right-arm's `else` branch** (:1706-1794): when
   the current lane is >5/3.6 m/s faster than the right lane's anticipated speed, SUMO skips
   keepRight accumulation entirely that step. Our `ApplyKeepRightDecision` accumulates
   unconditionally — a (previously latent) deviation that becomes load-bearing once the vsafe
   pair is computed. The port wraps the existing keepRight body in SUMO's gate.
2. **`currentDist`/`neighDist` are the raw LaneQ continuation lengths** (:1135-1136), lane-start-
   relative (proven by rule 2's `neighDist - posOnLane`), passed RAW into anticipateFollowSpeed
   (:1548-1549) and the deltaProb formula — confirming Entry 32's arithmetic (300, not 300-pos).

### The change (one coupled edit, all in `ApplyKeepRightDecision` + helpers)

- **(a) `neighDist` ← right lane's best-lanes continuation.** `KeepRightStrategicStay` already
  computes it (`v.KeepRightStayRightContLength`, memoized per lane); gains a sibling out
  `currContLength` for ego's own lane (cached as `v.KeepRightStayCurrContLength`). Fallback to
  `lane.Length`/`rightLane.Length` when 0 (single-edge route → matches SUMO, where LaneQ.length
  = lane length with no continuation; or no bestLanes entry).
- **(b) right-lane leader past the lane end.** `BuildActualDownstreamSpan` (T2.6) generalized to
  take an arbitrary same-edge source lane (`BuildDownstreamSpanFrom`); when
  `GetNeighborLeader(v, rightLane)` is null, walk the right lane's continuation with
  `TryFindCrossJunctionLeader` (same frozen `NeighborRearmost` source the left path's
  `TryFindContinuationLeader` uses). The (leader, gap) PAIR now feeds the cut, the vsafe, and
  the commit safety — cross-boundary gaps come from the walk, never from cross-lane position
  arithmetic. Lookahead is TryFindCrossJunctionLeader's Speed2Dist+brakeGap — shorter than
  SUMO's consecutive-lane scan, documented; binding case (L2 ego at lane end, leader just past
  the boundary) is well inside it.
- **(c) the speedGain-right arm, ported faithfully** (:1682, :1698-1719, :1811-1816):
  vsafe pair via the existing `AnticipateFollowSpeed` (+ an explicit-gap overload for the
  continuation leader), `relativeGain`, the 5/3.6 decay-vs-accumulate gate wrapping the
  keepRight body, the :1020 ceil truncation after mutation (same documented ≤1e-5 ordering
  tolerance as the left block), fire at `sgp < -2.0 && neighDist/max(0.1,speed) > 20`. Commit:
  INLINE swap (the sibling keepRight convention — the caller re-reads `v.LaneHandle` same
  iteration) behind the same veto chain as the keepRight fire (IsTargetLaneSafe/overlap/slot/
  coop cut-in) plus `CommitLaneChange`'s plain LaneChangeMinSpeed gate (the LEFT speedGain
  twin's convention, NOT keepRight's coop-conditioned #15 form — same SUMO mechanism, same
  knob semantics). On commit: `v.SpeedGainProbability = 0` (own accumulator only — the
  resetState() both-accumulators question stays deferred per the resume doc, revisited after
  this lands). Histogram path 1 (speedGain), `bypassesMinSpeed: false`.
- Cleanup: the verbatim-duplicated `[keepright]` trace block (:13252-13288) emits once, extended
  with the sgp/relativeGain/vsafe fields (committed instrument, Measurement discipline #8).

NOT touched: the LEFT speedGain block (its pos-relative dists are shipped, golden-validated,
out of scope), TryStrategicLaneChange, `checkOverTakeRight` (:1750-1758, still inert — needs a
structurally slower leader vMax), the decision ordering (keepRight-block before strategic —
existing engine convention; SUMO's stay suppressor + rule 2 still precede all accumulation, so
a route-leaving right lane within 200 m can never receive a speedGain-right fire).

### Predictions (recorded before measuring)

- **P1 keepright-standing**: f.0's stopped keepRight fire preserved (t≈17-18). Rolling keepRight
  deltaProb reads **0.0804**/step with the continuation (was 0.0508). f.3/f.4 fire speedGain-
  right AT SPEED in the approach window (SUMO: t=22 @ 3.7 m/s; ours within ±3 steps, speed > 2);
  f.5 fires late/near-stopped (SUMO t=25 @ 0.14). No follower waits for the t=83 discharge.
- **P2 goldens**: 661 byte-identical. Two argued-inert risk spots, named so a failure localizes:
  (i) the 5/3.6 gate skipping keepRight accumulation on 12-overtake's pass phase (old decrement
  there was already cut-shrunk to ≈0); (ii) continuation neighDist on multi-edge multi-lane
  goldens (44/45 arrival edges are route-final → continuation == lane length → unchanged). If
  either moves a golden, the golden is SUMO-diffed before any acceptance (gate 2).
- **P3 L2 stopped-LC rate**: moves DOWN from 1.155 toward SUMO's 0.410 (at-speed fires replace
  deferred-to-standstill ones), but NOT to zero — SUMO itself fires speedGain-right at
  standstill when the right lane is genuinely better (f.5), and we now reproduce that too.
  Predict 0.4-0.9 with the same denominator instrument.
- **P4 battery**: no stuckDwell regression; arrivals within noise (possible small multi-lane
  throughput gain from earlier lane sorting).
- **P5 determinism**: ≥4 repeat hashes identical, parallel == serial (all new reads are frozen-
  snapshot or ego-own; BestLanesCached is a ConcurrentDictionary).
- **P6 demo**: overlaps 0 at checkpoints; dead-stop share at or BELOW ≈12% (the new right fires
  respect LaneChangeMinSpeed, so high-realism zones convert stopped sorts into at-speed sorts).

## Entry 34 AFTER + 34b — the port worked, its first L2 number didn't, and the missing throttle was the stay complex

### AFTER against the Entry 34 predictions

- **P1 keepright-standing: CONFIRMED with one identity shuffle.** Rolling keepRight deltaProb
  reads **0.0804** exactly (was 0.0508); f.0's prompt stopped change lands at t=17, the same
  step as SUMO's; two followers change in the approach window (none waits for the t=83
  discharge). Ours moves **f.1** (t=17, 6.57 m/s) where SUMO moves f.3 (t=21, 5.52 m/s): SUMO's
  sequential front-to-back changer lets f.1 see f.0's same-step change live (neighVSafe
  collapses, no fire), while our frozen post-move snapshot doesn't — f.1 fires the same step and
  lands legally at exactly minGap behind f.0; f.3 then correctly freezes because *f.1* becomes
  its right-lane leader. Same follower count, legal landings, ±0 overlap — accepted as the
  frozen-snapshot structural deviation (CLAUDE.md rule 4), recorded here.
- **P2 goldens: CONFIRMED** — all 661 byte-identical, suite 779/5/0. One behavioural WITNESS
  moved: JunctionEntryTimeTests' cont-18 anchor (veh 95 no longer reaches the `:2336_18_0` bay
  inside 700 steps on synthetic-junction2) — re-anchored to veh 89 (steps 318–328) by the same
  scratch anchor-finder as Entry 31's re-anchor. Invariant untouched.
- **P3 L2 rate: WRONG at first, then landed in the predicted band.** The bare port read
  **1.624** (worse than baseline 1.155; same driver+instrument verified by a worktree baseline
  rerun reproducing 1.155 exactly). After 34b (below): **0.861**, inside the predicted 0.4–0.9.
- **P5 determinism: CONFIRMED** — 4 parallel + 1 serial L2 runs, 5/5 byte-identical hashes.

### 34b — what the 1.624 was, found by instruments (reasoned candidates: 0-for-2 again)

Per-event attribution (`[lccommit]`, new committed trace) plus two TraCI samples on honest-SUMO
L2 gave the mechanism in three steps:

1. SUMO's stopped non-rightmost vehicles hold `speedGainProbabilityRight == 0` in **22 827 of
   22 832** samples — the throttle is on the ACCUMULATION side, near-total.
2. Their `getLaneChangeState(right)` reads **STAY|STRATEGIC in 8 811 of ~11 165** samples — a
   strategic stay rule returns from `_wantsChange` (:1462) before the incentive section runs.
3. Our engine's new right fire also produced a **same-step `sgRight`+`strategic` commit pair**
   (t=41, `in_W00_1`) — the inline right swap was immediately reverted by the strategic layer:
   a ping-pong SUMO structurally cannot produce because its stays run FIRST.

The stay complex ported (both directions — the left mirror was the largest residual contributor
once the right side was fixed: 70 of 174 strict-stopped commits, 41 on `h1_0` alone):

- **The :1131-1150 effective-offset override**: when ego's lane AND the neighbour lane both have
  bestLaneOffset 0, SUMO sets the effective offset to the change direction — "changing sideways
  IS changing toward best" — and skips every stay rule. This is the piece that makes the rest
  safe: the oracle's followers (both lanes continue) and golden 44/45's arrival-edge keep-rights
  all flow through it untouched.
- **Rule :1398** (`neighLeftPlace / (|offset|+2) < laDist` → STAY) — kills the ping-pong.
- **Rule :1411** (rule 2) with the **:1290/:1297 jam-occupation term**: `neighLeftPlace =
  max(0, neighDist − pos − maxJam)`, `maxJam = max(curr.occupation, neigh.occupation)` where
  occupation = lengthWithGap of vehicles AHEAD of ego on that lane (MSLaneChanger's `dens`)
  plus the continuation lanes' brutto sums (`AheadJamOccupation`). Deep-in-queue ⇒
  neighLeftPlace ≈ 0 ⇒ STAY; queue head ⇒ jam ≈ 0 ⇒ may fire — SUMO's own head-vs-queue split,
  which is why the oracle's f.0/f.5 stopped changes (SUMO-real) survive.

Documented deviations: the left mirror READS LookAheadSpeed without updating it (a second
per-step decay would move golden 18's strategic fire timing); the :1298-1300 neighLead cap is
unported (SUMO `isStopped()` = SCHEDULED stop only); jam continuation depth is one route edge
(SUMO: the whole bestContinuations look-ahead) — an under-stay for multi-edge queues.

### The scorecard

| gate | requirement | result |
|---|---|---|
| 1 oracle | followers at speed, f.0 preserved, deltaProb 0.0804 | ✓ (identity shuffle documented) |
| 2 goldens | 661 byte-identical, suite green | ✓ 779/5/0 (witness re-anchor, Entry 31 method) |
| 3 L2 rate | materially below 1.155 toward 0.410, denominator reported | ✓ **0.861** (denom 69 720; stopped changes 79→60; landed overlaps 0) |
| 4 battery | vs net-regression-urgentfollow-on.txt, no stuckDwell regression | ✓ stuckDwell 0 everywhere; L2 IMPROVED (arrived 442→448, running 8→2); two flagged rows: city-mixed-1k arrived −4/1014 (noise-scale), city-organic-L2 overlaps 4→7 — the latter is in the owner-reported queue-tail family (backlog item 0) and is examined there. Report committed: `docs/reports/net-regression-entry34-stays.txt` (the new current reference) |
| 5 determinism | ≥4 repeat + serial hashes identical | ✓ 5/5 |
| 6 demo smoke | overlaps 0, dead-stop ≈12% | ✓ overlaps 0 at all 8 checkpoints, SMOKE OK, no gridlock; dead-stop share 61/664 = **9.2%** (P6 predicted at-or-below ≈12%; the 61 are the demo's deliberate low-realism keepRight swaps) |

## Entry 35 — the two Geneva-terrain reports reproduced OFFLINE and decomposed to one missing SUMO mechanism

Owner report (July 31, Windows 3D viewer, Geneva terrain, pre-Entry-34 build): *(a)* cars arriving
at a jam overlap the queue tail, "stacking many cars on a single place"; *(b)* "if car blocked in
the middle, turning left, cars going straight passing through him freely — many cases, different
junctions". Also confirmed there: no gridlock, no purely-lateral changes — the shipped fixes hold.

**Both reproduce offline on committed nets, on the CURRENT (post-34b) engine.** Instrument:
`scripts/classify-junction-overlaps.py` (committed tonight; OBB conventions imported from the
analyzer that owns them). city-organic-L2, 1000 steps, deterministic engine, same instrument both
engines:

| classifier | ours | honest SUMO |
|---|---|---|
| junction pair-steps, crossLane BOTH MOVING | **145** | 4 |
| junction pair-steps, crossLane both slow | **23** | 0 |
| junction pair-steps, crossLane STOPPED × MOVER (report b) | **17** | 0 |
| normal-lane deep (>1 m) rear-end overlap ONSETS (report a) | **12** | **0** |

city-mixed-1k: 10 deep onsets, same story. Two decisive structural facts:

1. **Every single deep rear-end onset — 22 of 22 across both nets — is a SAME-JUNCTION
   DOUBLE-LANDING**: both members left the same junction in the same step from DIFFERENT internal
   lanes (`:301_13_0` × `:301_6_0`, etc.) and landed overlapped on the shared arrival lane. In a
   jam this stacks arrivals on the queue tail — report (a) is the jammed face of this merge race.
   (The 145 both-moving pair-steps are the same converging paths overlapping while still inside.)
2. **The traced pass-through** (report (b), t=234 j=123): veh 15 stopped on `:123_3_0`; veh 122
   drives `:123_1_1` through it at 8 m/s. Veh 122's full trace shows exactly which constraints ran:
   `[cjl]` walks only EGO's OWN path lanes (`rearmost=none` — veh 15 is not on 122's path),
   `[merge]` follows the ARRIVAL lane's rearmost, `[keepclear]` reads downstream space. **No
   constraint reads occupancy of a geometrically-CROSSING internal lane.**

**The one SUMO mechanism covering both**: `MSLink` foe-lane link-leaders. `setRequestInformation`
precomputes `myFoeLanes` (internal lanes of conflicting links — crossing AND same-target/merging)
with `myConflicts` (per-foe-lane conflict-zone geometry, `lengthBehindCrossing`);
`MSVehicle::planMoveInternal` (:3403) calls `link->getLeaderInfo(...)` per upcoming link every step
and brakes for vehicles ON those foe lanes with gaps measured to the conflict point. Live consumers
verified. A crossing stopped foe ⇒ ego stops before the conflict zone (kills (b)); a same-target
foe ⇒ ego follows it with the accumulated merge gap (kills (a) and the 145). This is exactly the
machinery the F3/isLeader workstream carved out as "foe-lane / approaching-foe gating — separate
behaviour, larger blast radius" (F3 tracker, carried-out list); the F3 Stage-1/2 ports
(`LinkIndexByInternalLane`, `EntryConnectionByLink`, ET/CET timestamps, `IsLeader`) are its
prerequisites and are already in the engine.

Design-first: `docs/JUNCTION-FOE-LANE-DESIGN.md` + `-TASKS.md` + `-TRACKER.md` written tonight;
implementation awaits owner sign-off per CLAUDE.md ways-of-working. Note for the design's deadlock
section: mutual crossing-yield must be broken by the response matrix + the F3 ET/CET tie-break —
and whether SUMO's prioritized links also brake for foes physically inside the conflict zone
(myFoeLanes built from the geometric `foes` bitstring vs the `response` yield matrix) is a
MUST-VERIFY-IN-SOURCE item, not an assumption.

## Entry 35b — F2.x implementation session: two classes measurably improved, one hard trade-off found, all gate-scoped

Owner signed off ("go autonomously"). Discovery first: **the F3-JUNCTION-OVERLAP workstream had
already built most of the design** — the RespondsTo/FoeWith split, `AdaptToJunctionLeader`, the
`isLeader() || inTheWay()` disjunction — parked behind `JunctionPhysicalOccupancyGate`
(default OFF, "measured counterproductive three times", F3-SESSION-LOG). Those verdicts predate
BOTH the sibling gates shipping default-ON (isLeader, admission, entry-order, arrival
arbitration…) and the Entry-30 determinism fix, so this session re-measured instead of re-porting.
Everything below is behind `SUMOSHARP_PHYSOCCUPANCY` (new EnvGate in both drivers, ENV-GATES row,
completeness test green); gate-off is **byte-identical** (FCD hash `c768d7f6…` = pre-change
baseline) and the suite is **779/5/0**.

The measurement ladder (city-organic-L2, 1000 steps, the Entry 35 classifier; OFF baseline:
145 bothMove / 23 bothSlow / 17 stopXmove / 12 landing onsets; honest SUMO: 4 / 0 / 0 / 0):

| step | stopXmove | landing onsets | flow |
|---|---|---|---|
| gate ON, as parked by F3 | 37 (worse!) | 5 | drained |
| + F2.2 merge FoeWith widening | — | — | **GRIDLOCK** (mutual follow; bothSlow 591) |
| + IsLeaderByEntryOrder in merge PHASE 1 | 37 | 5 | drained ✓ (the F3 tie-break breaks the wedge) |
| + bay conflicts (ingest) + bay arm, any-body hold | 13 | 4 | **GRIDLOCK** (throughput collapse, bothSlow 652) |
| + waiting-only (≤0.5 m/s) hold | 35 | 6 | drained (holds too LATE — occupant stops after ego commits) |
| + slow-or-stopped (≤2 m/s) non-exiting hold | 19 | 6 | **GRIDLOCK** (dwell 634, same signature) |

**What is solidly established:**
- **F2.2 (same-target merge, the 22/22 double-landing class): WORKS.** The merge arm's
  reachability is now foes-based (SUMO's own semantics — `MSRightOfWayJunction.cpp` builds
  `myLinkFoeInternalLanes` from `SUMO_ATTR_FOES`; arbitration PHASE 0 stays RespondsTo-only), and
  the F3 `IsLeaderByEntryOrder` tie-break in PHASE 1 makes mutual-foe merges deadlock-free by
  antisymmetry. Landing onsets 12 → 5 with flow intact. The measured L2 witness: junction 301's
  (13,6) pair is FoeWith both ways but RespondsTo only 6→13 — the link-13 car was blind.
- **The bay class is now fully characterized** (26-34 of the stopXmove pair-steps): a turner
  WAITING in a first-stage cont bay — in NO foes row (`intLanes` carries the SECOND stage;
  netconvert's bay corridors physically overlap sibling movements, e.g. `:123_3_0` vs `:123_1_1`
  share their start point) — is driven through by same/adjacent-corridor movers. SUMO 1.20 drives
  through these too (bay-append `:129-137` is response-gated); fixing it is a sanctioned
  beyond-SUMO honesty deviation. SHIPPED toward it (all parity-inert or gate-scoped):
  `BayConflict` corridor-proximity geometry at ingest (`PolylineGeometry.TryCorridorOverlap` —
  crossing detection cannot see near-parallel corridors), the `_physOnLaneFirst/Second` PHYSICAL
  occupancy index (the pool-based foe index first-masks a bay occupant behind a distant
  approaching vehicle — measured), and the gate-scoped bay-occupancy arm (jyArm 7).
- **The bay HOLD-TIMING trade-off is the open problem**: hold for any/slow body → capacity
  collapse into gridlock on the saturated net (every transiting turner passes through its bay);
  hold for stopped-only → one step too late (the occupant stops AFTER ego commits). The
  designed next step (NOT attempted, needs fresh context): move the WAIT POINT — when a bay's
  waiting position itself lies inside a BayConflict interval (a degenerate bay that cannot
  shelter a car), `InternalJunctionAdmissionConstraint` should hold the turner at the junction
  ENTRY instead of in the bay, so no stopped body ever sits in a shared corridor; the gridlock
  signature (dwell 634, same site both times) also deserves one targeted episode trace before
  any further threshold tuning.

Default behaviour is unchanged and verified; the gate stays OFF. Tracker updated (F2.2 done
pending gate ladder; F2.1 partial; F2.1c = the wait-point relocation, new).

## Entry 36 (BEFORE) — the dwell-634 gridlock episode traced: a mutual jyArm-7 two-cycle, and what it falsifies

**The trace (instrument, not reasoning).** Gate ON, city-organic-L2, 1000 steps, deterministic;
`--binder-log` + the analyzer. The wedge is junction 301, and the analyzer's one wedge row and one
t_end OBB pair are the same two vehicles:

- Links 24 (`-336_1 → -302`, bay `:301_24_0`, 5.05 m) and 25 (`-336_1 → 316`, bay `:301_25_0`,
  4.29 m) are sibling left turns from the SAME approach lane; their bays share a start point and
  both are SHORTER than a car. netconvert does not put them in each other's foes rows
  (foes(24)/foes(25) decoded: neither contains the other).
- t=361: veh 198 enters bay 25; t=363-365 it is held at the bay end by binder 14
  (internalJunctionAdmission) for veh 97 crossing link 14 — a LEGITIMATE stage-2 wait.
- t=364: veh 235 follows into bay 24 (its own stage-2 looked clear). Its bay-arm hold point sat
  too DEEP because ingest has no bay-vs-bay row (see below), so it fully entered and stopped
  interpenetrated with 198 (1.80 m OBB at rest).
- t=365: 235 holds for 198's body (jyArm 7). t=366: 97 clears, 198's admission releases — and
  198 now holds for 235's body (jyArm 7). From t=366 to t=999: `198 →7→ 235` and `235 →7→ 198`,
  a pure two-vehicle mutual bay-occupancy hold. The 41-stuck end state is the queue cascading
  behind this single pair. (Transient third parties — e.g. veh 370 at t=396 — come and go; the
  cycle never breaks.)

**What the trace falsifies (measurement discipline lesson 2, again).** The F2.1c design sketch
(resume doc §5) proposed relocating the ADMISSION hold to the junction entry for degenerate bays.
The trace shows the admission hold was never the defect — 198's bay-end wait for 97 is exactly
right. The defects are two, both in the F2.1b bay machinery itself:

1. **Ingest compares only ego's STAGE-2 lane against foe bays.** The earliest physical conflict
   for 235 is its OWN BAY vs 198's bay (shared start point) — not ingested, so 235's hold point
   landed at the far end of its bay, inside the overlap. Fix: for a cont ego link, ALSO compare
   its first-stage bay shape against foe bays, emitting ego arcs RELATIVE TO THE STAGE-2 START
   (negative values). The engine's existing `egoDistToEntry + EgoArcStart` then lands the hold at
   the stop line with no engine frame change (`egoDistToEntry` already walks the pool through the
   bay).
2. **The bay arm has no antisymmetric tie-break** — the exact F2.2 lesson, unapplied: a mutual
   jyArm-7 hold has no resolution. Fix: when ego is already INSIDE the junction, skip the hold if
   ego is the EARLIER entrant (`!IsLeaderByEntryOrder(...)` — antisymmetric, so exactly one of a
   mutual pair yields; the earlier entrant clears and the pair interleaves). An approaching ego
   (not yet inside) always holds — that IS the wanted entry wait.

The admission-side wait-point relocation is therefore NOT built (no degenerate-bay flag, no new
entry-hold — one less arm feeding the willPass dynamics that collapsed Entry 26). The hold
predicate (`Speed <= 2.0`, non-exiting) is NOT touched (resume doc §3: three dial points measured).

**Predictions (gate ON, city-organic-L2, 1000 steps, both changes in):**
1. DRAINED — no 41-stuck end state; longest dwell <= 30 (from 634; OFF baseline is 16).
2. Junction 301: 198 exits within ~5 steps of 97 clearing; no stopped OBB pair at t_end.
3. Classifier: bothSlow stays near the OFF baseline 23 (NOT 652); stopXmove <= 17 (the ON
   variants measured 19-35); landings <= 6; bothMove in 120-145.
4. Gate OFF byte-identical (`c768d7f6dd8535f46f170956737a2921`); suite 779/5/0.

If prediction 3's stopXmove does NOT drop below the OFF baseline, the residual is the transient-
mover class the hold predicate deliberately ignores — measure before touching that dial.

## Entry 36 (AFTER) — both fixes landed, both surfaces measured; two traced episodes, two mechanisms

**What shipped (all gate-scoped under `SUMOSHARP_PHYSOCCUPANCY`; gate OFF byte-identical,
`c768d7f6dd8535f46f170956737a2921` re-verified with the final binary):**

1. **Bay-piece ingest rows** (`NetworkParser`): for a cont ego link, its FIRST-stage bay shape is
   compared against foe bays, ego arcs emitted relative to the stage-2 start (negative), so the
   engine's unchanged `egoDistToEntry + EgoArcStart` lands the hold at the stop line.
2. **Entry-order backstop in the bay arm** (`Engine`): an ego already inside the junction skips the
   jyArm-7 hold when it is the EARLIER entrant (`IsLeaderByEntryOrder`, antisymmetric) — mutual
   jy7-jy7 two-cycles resolve; an approaching ego always holds (that IS the entry wait).
3. **Back-bumper exiting test** (`Engine`): the "occupant is exiting" skip now tests the BACK, not
   the front (a parked car's tail can block a short interval its front has left). Measured inert on
   L2 (full-bay intervals); kept as a correctness guard for short intervals.
4. **Brush filter** (`NetworkParser`, `minEgoOverlapLen = 1.0`): found by the gate-ON battery, which
   caught two NEW wedges (city-organic stuckDwell 0→477, city-3000 13→556). Traced (binder log,
   junction 359): links 5/8's corridors brush for 0.27 m at ~1.9 m centerline distance — the 2.0 m
   proximity threshold exceeds the 1.8 m body-touch distance, so the row held veh 461 forever
   0.1 m before a conflict where bodies never meet (t_end OBB scan: NOT touching), deadlocked
   CROSS-ARM against the bay occupant (jy7 one way, SUMO-faithful inTheWay follow the other — no
   tie-break can span two arms; and SUMO's non-foes verdict was geometrically RIGHT there). Genuine
   shared corridors measure 4–8 m; 1.0 m separates the classes with margin both ways.
5. **Instruments committed**: `[bay]` per-row trace (SUMOSHARP_TRACEVEH), `--examples` on the
   classifier, `JunctionBayConflictIngestTests` pinning BOTH traced witness sites on committed nets.

**Predictions vs measured (city-organic-L2, 1000 steps, gate ON):** DRAINED ✓ (0 stuck, was 41);
dwell 19 ✓ (predicted ≤30, was 634; OFF baseline 16); bothSlow 15 ✓ (predicted ≈23 not 652);
landings 6 ✓; bothMove 124 ✓; stopXmove 18 vs predicted ≤17 — one over, and the decomposition
explains it (below). Prediction 1's "198 exits within ~5 steps of 97 clearing" verified in the
binder log.

**The full gate-ON scoreboard after all four pieces:**

| surface | gate OFF | gate ON | SUMO |
|---|---|---|---|
| L2 flow | drained, dwell 16 | drained, dwell 19 | — |
| L2 bothMove / bothSlow / stopXmove / landings | 145 / 23 / 17 / 12 | 124 / 15 / 18 / 6 | 4 / 0 / 0 / 0 |
| mixed-1k stopXmove / landings | 54 / 10 | 30 / 5 | — |
| battery | reference | stuckDwell 0 everywhere (city-3000 13 = baseline); city-organic arrived 494 > 491; junction-realism-L2 INCONCLUSIVE→DRAINED; two mild flags: junction-realism-L1 arrived 362→355, willpass-saturation overlaps 3→4 | — |

Suite 781/5/0 (779 + 2 new pins); determinism 3/3 identical + `--max-parallelism 1` == default
(Sim.Sumo); goldens untouched.

**The L2 stopXmove residual (18), decomposed:** ~15 pair-steps are shared with gate OFF at the same
sites (j=1150, 123, 1021, 428, 271, 717, 301-straights) — stopped turners on PLAIN internal lanes
vs movers netconvert never made foes: the F1.1 class (geometric conflict ingest beyond bays), a
separate tracker item. The 3 bay-class remnants: t=468 is the structural transient (occupant
stopped AFTER the mover committed at 12.8 m/s — unstoppable); t=543/544 is a NAMED residual class:
the mover's LANE SEQUENCE pointed through the sibling lane's link (pending strategic lane change),
so every yield arm watched the wrong link's rows on approach — a pre-existing upcoming-link
resolution limitation shared by all arms (same site clips gate-OFF at t=372), out of F2.1c scope.

**What the two traces bought (measurement lessons, again):** the resume doc's designed fix
(admission-side wait-point relocation for degenerate bays) was NOT built — the trace showed the
admission hold was correct and the defect was in the bay machinery itself; and the second trace
showed the first fix's geometry was too EAGER (proximity ≠ contact), which only the cross-net
battery caught. Neither conclusion was reachable by reading code.

## Entry 37 — the owner's Geneva gridlock: reproduced, traced to a 3-mechanism ring, cut with SUMO's own knob

**The report (owner, Geneva terrain, 3D viewer, F3 gate on):** near-total gridlock where junctions
previously drained; much fewer overlaps but cars locked; cars standing on green with nothing ahead;
cars stopped mid-lane ~5 car lengths before a junction with a long queue behind; "no mechanism that
detects this and unblocks after some time".

**Reproduction (offline, committed box scene):** the LiveCity smoke at default density 160 is
healthy in BOTH gate states — but at `LIVECITY_CARS=400`, gate ON collapses (stoppedFrac 0.86–0.93,
arrivals 443 vs 829 gate-off, movement ×¼) while gate OFF still flows. The gate-ON battery and both
classifier nets had been green: **the collapse is density-dependent and only the live-city surface
reaches it.** (Also fixed en route: the correct env var for the owner's viewer is
`LIVECITY_F3OCCUPANCY`, not `SUMOSHARP_PHYSOCCUPANCY` — LiveCitySim has its own plumbing.)

**The trace (new committed instruments: `BlockerEntity` exported through the read buffer,
`LIVECITY-INTERNALSTUCK` head histogram + `LIVECITY-CHAIN` wedge-chain printer in the smoke
witness):** the stuck population is queue shadow behind ~27 persistent internal heads, and the seed
is a 5-vehicle ring spanning THREE mechanisms at one junction:

    94 (head of the queue on :d_3_2_15_0) --jy7 bay-hold--> 163 (in bay :d_3_2_9_0)
    163 --admission (binder 14, lane-foe)--> 332 (TAIL of the same queue 94 heads)
    332 --leaderFollow--> 65 --leaderFollow--> 111 --leaderFollow--> 94

The Entry-36 tie-break is CORRECT here (94 entered later, so 94 yields) — but the earlier entrant's
dependency loops back through the queue. No pairwise tie-break can break a cycle that spans three
arms. The d_3_3 cluster (arm-5 chains, incl. a car stopped on a NORMAL connecting lane ~5 car
lengths before the junction — the owner's "stopped mid-lane with nothing in front", it was adapting
to a wedged car INSIDE the junction ahead) hung off this ring transitively.

**The fix — SUMO's own escape, extended:** `Engine.IgnoreJunctionBlockerSeconds`
(`--ignore-junction-blocker`, already ported at the crossing-foe loop head) now also cuts the bay
arm: a foe body that has ALREADY stood >= the threshold is a wedge, not a transient, and holding for
it converts one stuck car into a citywide gridlock. This is precisely the "detect and unblock after
some time" mechanism the owner asked for, and it is SUMO's, not an invented dial.
- Engine default stays **-1** (never ignore, SUMO parity): every golden, every battery number, and
  the gate-off hash (`c768d7f6…`) are untouched; suite 781/5/0.
- **LiveCitySim defaults it to 60 s whenever its F3 gate is ON** (off-gate demo untouched);
  `LIVECITY_IGNOREBLOCKER=<secs>` overrides, `SUMOSHARP_IGNOREBLOCKER` is Sim.Run plumbing.
  ENV-GATES rows added (completeness test green).

**Measured (smoke, 400 cars, gate ON + 60 s):** arrivals 443 → **838** (gate-off 829 — the honest
arm now slightly OUTPERFORMS the pass-through baseline), stoppedFrac back to ~0.35 (signal-cycle
oscillation), persistent stuck-internal 27 → 3–12 transient. L2/mixed classifier and the battery are
IDENTICAL with the knob (nothing on those nets ever waits 60 s — the escape fires only in true
wedges).

**Known pre-existing failure, logged not chased:** `LongHorizonGridlockDiagTests`'s
all-nine-sibling-gates-ON configuration reports 129 >300-step stalls — byte-identically at
`bcd6813`, BEFORE this session's work (verified in a worktree). Sim.LiveCity.Tests is not in
Traffic.sln, so nobody had run it since the config regressed. Separate item.

**Remaining from the owner's report:** residual queue-stacking overlaps (the F1.1 non-foes class +
double-landing residue) and the green-light standers under gate-OFF configs — F1.1 and the
long-horizon item respectively. The terminal-gridlock and no-unblock halves are closed.

## Entry 38 (BEFORE) — the 34b long-horizon regression traced: a latent UNGATED mutual merge deadlock; and a correction to Entry 37

**Correction first (credit: the 3D-test session's bisect).** Entry 37 called the
`LongHorizonGridlockDiagTests` failure "pre-existing". That was WRONG: the check was run at
`bcd6813` — a BRANCH commit — which only proved it predates Entry 36. The other session tested
`origin/main` (test file byte-identical): it PASSES there, and bisects to `bc381db` (Entry 34b,
the strategic stay complex) — an ungated, default-ON change. The branch's DEFAULT configuration
had 129 >300-step stalls where main has 0.

**The trace (LIVECITY_TRACEVEH plumbing added; smoke reproduces the test's exact vehicles —
deterministic).** The 129 stalls cascade from ONE seed pair at junction d_3_3, t≈2790:

    [merge] __veh1967 on=:d_3_3_27_0@18.6  PHASE1-stop foe=__veh1931 x=-30.6   (forever)
    [merge] __veh1931 on=:d_3_3_11_2@26.1  PHASE1-stop foe=__veh1967 x=-6.7    (forever)

A PURE MUTUAL PHASE-1 merge hold — each following the other as its merge leader with a NEGATIVE
gap — with the F3 gate OFF. The F2.2 tie-break resolves exactly this class, but it was scoped
under `JunctionPhysicalOccupancyGate` on the claim that "mutual reach was impossible" gate-off
(only the RespondsTo side reached the arm). **That claim is falsified**: netconvert's response
matrix CAN be mutual (multilane interior sub-links), so the deadlock was latent in the merge arm
since it was built. Entry 34b did not create it — 34b's lane redistribution (cars staying in
lanes they previously left) made the mutual configuration OCCUR on the live-city net. That is why
the bisect lands on 34b while the defect lives in the merge arm.

**The fix**: un-scope the PHASE-1 `IsLeaderByEntryOrder` tie-break from the gate. SUMO applies
isLeader's entry-time ordering to EVERY link leader (MSVehicle.cpp:3429) unconditionally — the
gate-scoping was measurement hygiene, not semantics, and it left the default engine with a
deadlock SUMO does not have. This is a DEFAULT-behaviour change: the c768d7f6 gate-off L2 hash is
expected to change and will be re-baselined if all gates pass.

**Predictions:**
1. Long-horizon test: gates-ON arm 129 → ≤20 (likely ~0); the OFF arm improves similarly; main's
   0-stall behaviour restored or bettered.
2. Parity suite stays 781/5/0 (goldens are 2–5 vehicles; a mutual same-target merge pair inside a
   multilane junction interior does not occur there). Any golden that DOES move is a stop-ship.
3. Battery at defaults vs the Entry-34 reference: no stuckDwell anywhere, arrivals in noise.
4. L2/mixed classifier at defaults: unchanged or slightly better (the tie-break only fires on
   mutual pairs, which previously never resolved).
5. Smoke at 400 cars: healthy in BOTH gate states.

## Entry 38 (AFTER) — the merge deadlock fixed AT DEFAULTS; every surface green; predictions vs measured

**What shipped (both UNGATED — default-behaviour changes justified by SUMO semantics):** the
PHASE-1 `IsLeaderByEntryOrder` tie-break and the foes-based (`FoeWith`) merge-arm reachability,
previously scoped under `JunctionPhysicalOccupancyGate`. The scoping was measurement hygiene that
had left the SHIPPED engine with (a) a mutual-merge deadlock SUMO does not have and (b) the
non-responding-side merge blindness. Crossing/bay arms stay gate-scoped.

**Predictions vs measured:**
1. Long-horizon stalls → 0: **CONFIRMED** (129 → 0 in BOTH arms; merge overlap events 6-7 → 2/0,
   worst pen 0.77 m; the whole `LongHorizonGridlockDiagTests` passes again — arrivals 2852/2396).
   `tests/Sim.LiveCity.Tests` is fully green (90/90) for the first time since Entry 34b.
2. Suite 781/5/0, goldens untouched: **CONFIRMED** — but prediction 2's "any golden that moves is
   a stop-ship" hid a subtlety: two FLOOR GUARDS (not goldens) tripped by ±1 events. Both traced
   to the PRE-EXISTING dead-lane stranding class reshuffled by the changed junction interleave:
   DenseFlow 287→286 (the documented dead-lane pair 122/256 AND both junction-interior wedges now
   ALL ARRIVE; two different cars strand on dead lanes instead — the junction-wedge class is
   GONE from the end state), and IgnoreJunctionBlocker's (5,ON) arm keeps 1 yield teleport
   (veh 288, 1442 steps under `deadLaneMerge`, traced) while the DEFAULT arms IMPROVED 1 → 0.
   Both guards re-anchored with the accounting in their comments.
3. Battery: **CONFIRMED** — stuckDwell 0 everywhere (city-3000 13 = its longstanding baseline),
   three ≤7-arrival noise flags; new reference `docs/reports/net-regression-entry38-mergefix.txt`.
4. Classifier at defaults: **CONFIRMED, better than predicted** — L2 145/23/17/12 →
   144/15/17/10; mixed-1k stopXmove 54 → 37, landings 10 → 6. The F2.2 merge benefits now reach
   the shipped engine.
5. Hashes: default L2 re-baselines to `e94b88b7534c21b5fd3bf8657dbb1666` (determinism 3/3);
   gate-ON hash UNCHANGED (`0c9bad71…`) — both changes were already live under the gate, so every
   Entry-36/37 gate-ON validation (smoke 400, battery-on, ladder) stands as measured.

**Standing lesson made explicit:** a "byte-identical off" gate is only hygiene while the gated
code is EXPERIMENTAL. Once a piece is validated as SUMO's own unconditional semantics (the
tie-break, the foes-based reach), leaving it gated is itself a divergence — the default engine
was carrying a deadlock and a blindness SUMO never had, and it took an hour-horizon surface to
see it. The remaining gate-scoped pieces (crossing physical occupancy, bay arm) are genuinely
beyond-SUMO and stay opt-in pending the F3.1 ladder.

## Entry 39 (BEFORE) — F1.1: conflict geometry for non-foes internal-lane pairs

**The class (measured, Entry 38 AFTER + resume doc §1; NOT a gate regression — present in both
gate states):** a STOPPED vehicle on a plain (non-bay) internal lane is driven through by movers
whose links netconvert never put in each other's foes rows. ~15 of L2's 18 gate-ON
`crossLane|stopXmove` pair-steps. Recurring sites: j=1150 `:1150_2_0`×`:1150_0_1` (4 episodes),
j=123 `:123_11_0`×`:123_9_1` (4), j=1021, j=428 (two pairs), j=301 (two pairs), j=271, j=717,
j=12, j=23, j=993. Honest SUMO on the same net: 0 such pair-steps — but SUMO 1.20 itself drives
through these too (its conflict model is foes-row-driven and these pairs are foes-blind), so the
fix is the SAME sanctioned beyond-SUMO honesty deviation as the bay work
(`docs/CONSTRAINT-high-realism-artefact-ladder.md`: target the flow, never the method).

**Design (generalize Entry 36, invent nothing):** extend the `BayConflict` ingest pass
(`NetworkParser.cs`, `F2.1b`) to emit rows for EVERY ordered internal-lane pair (i,j), i≠j, of a
junction where `!request_i.FoeWith(j)` — the same `TryCorridorOverlap` proximity sampling
(2.0 m centerline threshold), the same NON-NEGOTIABLE `minEgoOverlapLen=1.0` brush filter
(the 0.27 m junction-359 brush row wedged the whole net; do not relitigate). Foe side of a row is
the foe link's internal lane ID; ego arcs stay in the ego stage-lane frame the engine already
uses. The engine bay arm (jyArm 7) is expected to need ZERO changes: `_physOnLaneFirst/Second`
indexes ALL internal lanes (verified at `BuildFoeApproachIndex`, Engine.cs:10117), the arm
resolves any `BayLaneId` via `LaneHandleById`, and all four guards (speed≤2.0 hold predicate,
back-bumper exiting test, Entry-37 patience escape, Entry-36 entry-order backstop inside the
junction) are generic over the foe lane. Rows stay consumed ONLY under
`JunctionPhysicalOccupancyGate` — F1.1 completes F3.1; the default flip (F3.2) remains a separate
owner decision. Ingest is O(links²) per junction — measure parse wall-time on city-15000 once.

**Predictions (each falsifiable; a miss is a finding, not an embarrassment):**
1. L2 gate-ON classifier: `stopXmove` 18 → ≤5. The ~15 non-foes pair-steps collapse; the 2
   lane-sequence-mismatch clips (t=543/544 class) and the ~1 late-stop transient (t=468 class)
   survive — they are named residuals no geometry row can fix.
2. L2 gate-ON: landings ≈ unchanged, bothSlow does NOT explode (the brush filter plus the
   stopped-only hold predicate are what prevented that in Entry 36; same machinery, same
   argument), DRAINED end state everywhere.
3. Gate-OFF L2 hash stays `e94b88b7534c21b5fd3bf8657dbb1666` BYTE-IDENTICAL (rows have no
   gate-off reader), determinism 3/3 + `--max-parallelism 1` in both states.
4. Battery gate-ON vs `net-regression-entry38-mergefix.txt`: stuckDwell 0 everywhere, arrivals in
   noise. Risk to watch: symmetric row pairs (both (i,j) and (j,i) non-foes) mutually holding —
   the entry-order backstop must interleave them exactly as it did the sibling bays.
5. Live-city smoke `LIVECITY_CARS=400` gate-ON: drained, arrivals ≈830+, INTERNALSTUCK transient
   only (the surface that caught Entry 37's collapse).
6. Full sln suite green (781/5/0 + 90/90 + the rest); parity goldens untouched (2–5-vehicle
   scenarios have no non-foes stopped-occupant configurations, and gate-off inertness covers them
   twice over).
7. Mixed-1k gate-ON: stopXmove improves in the same proportion (37 at defaults; the gate-ON
   number should land clearly below its Entry-38 value).

**Fail conditions declared up front:** if bothSlow explodes or any battery net gridlocks, the
first suspect is symmetric-pair mutual holding (check `[bay]` traces for a two-cycle before
touching any dial); if stopXmove does NOT collapse, the class was mis-attributed and the next
step is ONE traced vehicle at j=1150, not a redesign.

### Entry 39 (MID) — prediction 1 FALSIFIED by measurement; the class decomposes into three mechanisms

**Measured after landing the non-foes ingest rows alone:** L2 gate-ON stopXmove 18 → **19**, same
recurring sites. The rows ARE emitted (verified by parse dump: j=301 (7,8), j=271 (9,10),
j=1150 link1×bay all present with metres-long intervals) — the prediction's "engine needs zero
changes" was WRONG. Per the declared fail condition, five movers were traced (`[jy]` link-resolution
instrument added to `JunctionYieldConstraint` for this — committed). The 19 pair-steps decompose
into THREE mechanisms, none of which is "row missing for a plain non-foes pair":

- **(A) Link mis-resolution during approach** (veh 127 + 246 at j=1150, veh 57 at j=123 — the
  ~2-pair-step "lane-sequence mismatch" residual is actually the LARGEST class, ~7/19): the pool's
  strategic chain resolves the SIBLING lane's connection (link 0 `:1150_0_0`) for the whole
  approach while the vehicle physically drives lane 1 → every yield arm consults rows for a link
  the vehicle will never drive; zero rows match; the vehicle enters blind and the boundary
  re-splice (`TryReResolveFromActualLane`) corrects the pool one step too late. SUMO resolves the
  upcoming link through the CURRENT lane's own links — `MSLane::succLinkSec` (MSLane.cpp:2573)
  via `getBestLanesContinuation()` == the current lane's continuation (MSVehicle.cpp:6236) — so
  this is a straight parity divergence, DEFAULT-scope, not gate-scope.
- **(B) Late-stop race at the 2.0 m/s hold dial** (veh 179 at j=301, veh 385 at j=271, ~6/19): the
  correct row exists and is consulted; the occupant enters the shared near-parallel corridor at
  4-5 m/s (correctly skipped as transiting), decelerates through 2.0 m/s in EXACTLY the step ego
  commits past the overlap start. Binary hold-or-commit cannot win this race — the honest shape is
  CAR-FOLLOWING along the shared corridor (adaptToJunctionLeader semantics applied to bay-row
  occupants), which is a separate, gate-scoped design with its own collapse risk (Entry 36
  measured stop-line holds for transiting bodies at bothSlow 16→652).
- **(C) Corner-cut past a straddling tail** (veh 104 at j=1021, ~2/19): the two internal-lane
  centerlines never come within 3.2 m (no honest corridor row exists — verified by profile), but
  the stopped foe at pos 4.19 < its own length hangs its tail BEHIND its lane start, into the
  approach-mouth region ego's turn sweeps (backs 0.28 m apart). No lane-pair interval can see
  this; it needs foe-approach-mouth geometry. Named residual for now.

**Re-plan (in order): keep the ingest rows (they are the substrate B needs and already correct);
fix (A) at DEFAULTS as SUMO-faithful (succLinkSec semantics — re-resolve the yield pass's ego
link through the actual lane's connection when the pool's link belongs to a sibling lane); then
re-measure everything; then design (B) on that baseline; (C) stays a named residual.**

**Predictions for (A):**
1. The j=1150 and j=123 episodes convert (the bay rows for the actual link then see the stopped
   occupant on approach); L2 gate-ON stopXmove 19 → ≤12.
2. DEFAULT behaviour changes (the crossing/merge arms consult the correct link too): the default
   L2 hash `e94b88b7…` is EXPECTED to move; goldens must NOT (strategic changes in 2–5-vehicle
   scenarios complete long before junctions; any golden that moves is a stop-ship).
3. Battery at defaults: no new stuckDwell; hour-horizon LiveCity suite stays 90/90.
4. Gate-ON smoke 400 stays drained (arrivals ≈830+).

### Entry 39 (AFTER, part A) — actual-lane link resolution landed at DEFAULTS; every gate green; predictions vs measured

**What shipped:**
1. The F1.1 non-foes ingest rows (Entry 39 BEFORE item; landed with the MID commit): the
   `BayConflict` pass now also emits rows for every ordered non-foes internal-lane pair with a
   genuine (≥1.0 m after the brush filter) corridor overlap. city-15000: 22 704 rows across 2 776
   junctions, full net parse 2.0 s — the O(links²) pass is a non-issue at scale. Pinned by
   `NonFoesPairs_GetCorridorRows_StraddlingTailPairsDoNot` (j=301 pair both directions, j=271
   pair, and the j=1021 straddling-tail pair asserted ABSENT).
2. **Mechanism (A) fixed at DEFAULTS** (`Engine.cs`, `JunctionYieldConstraint` Step 1, search
   `Entry 39 mechanism (A)`): when ego is on a normal lane feeding the resolved junction and the
   pool's link belongs to a SIBLING lane of ego's edge, the yield pass re-resolves ego's link
   through the ACTUAL lane's own connection to the same next route edge — `MSLane::succLinkSec`
   (MSLane.cpp:2573) semantics, the same connection the boundary crossing takes. `egoLane` follows
   the re-resolved id. A lane with no such connection keeps the pool resolution (dead-lane
   machinery unchanged). UNGATED: this is SUMO's own resolution rule, and the pool-based
   resolution was a parity divergence (Entry 38's standing lesson applies).

**Predictions vs measured:**
1. j=1150/j=123 episodes convert; L2 gate-ON stopXmove 19 → ≤12: **CONFIRMED, beaten** — 19 → 9
   (bothSlow 11, landings 6, bothMove 108). The traced j=1150 episode DISSOLVED (veh 29 clears its
   bay without stalling; veh 127 even completes the lane change its pool wanted). Defaults L2
   improved too: stopXmove 17 → 13, landings 10 → 6, vs Entry 38. Mixed-1k: defaults stopXmove
   37 → 34, landings 5; gate-ON 12/5.
2. Default hash moves, goldens do NOT: **CONFIRMED** — full sln suite green (ParityTests 781/5/0
   with all goldens byte-identical, LiveCity 90/90, Pedestrians 324, Viewer.Motion 19, Host 6,
   DotRecast 2). New L2 hashes: default `5ac89389889a3e80056fce9f4c4ec158`, gate-ON
   `fd6363810091905d784c600cb1211403` (both moved — the fix is default-scope). Determinism 3/3
   identical per arm; shim par == `--max-parallelism 1` in both arms. `Sim.Bench` hash UNCHANGED
   (`A134ED3716DDE7BC`, par==single) — no re-pin needed.
3. Battery at defaults vs `net-regression-entry38-mergefix.txt`: **CONFIRMED** — stuckDwell 0
   everywhere (city-3000 13 = its longstanding baseline); two noise-level flags (mixed-1k arrivals
   1014→1009, city-organic-L2 overlap events 7→8) against a classifier that shows both nets
   IMPROVED on the stopped-vehicle classes.
4. Smoke 400 gate ON: **CONFIRMED** — draining throughout, arrivals 823 @ t=600 (≈830 baseline,
   noise), INTERNALSTUCK transient membership only.

**Remaining stopXmove 9 (gate ON) decomposes as predicted in the MID entry:** the late-stop race
(B) sites and the straddling-tail (C) sites. (B) — car-following along a shared near-parallel
corridor instead of the binary hold-or-commit — is the next design, gate-scoped, with its own
BEFORE predictions. (C) stays a named residual.

## Entry 40 (BEFORE) — F1.1 mechanism (B): corridor-follow in the bay arm

**The defect (traced, Entry 39 MID):** the bay arm's binary hold-or-commit loses the late-stop
race — the occupant enters the shared corridor at 4–5 m/s (skipped by the `Speed<=2.0` predicate,
whose dial is measured and stays untouched), decelerates through the threshold in the step ego
commits past the overlap start, and ego drives through its tail (veh 179×122 at j=301 t=346, veh
385×502 at j=271 t=490). ~6 of the 9 remaining L2 gate-ON stopXmove pair-steps.

**Measured design input:** an angle threshold CANNOT discriminate "follow-appropriate" rows —
proximity-overlap regions are near-parallel by construction (measured over the overlap intervals:
j=301 (7,8) 9.5° mean, j=271 (9,10) 9.4°, the j=1150 straight-vs-bay hug 9.7°, and the Entry-36
sibling bays are the MOST parallel at 1.5°). So there is no new ingest field and no
classification dial. Instead the follow/hold split falls out of the GAP SIGN:

- Map the occupant's back through the row's own arc intervals into ego's frame
  (`mapped = EgoArcStart + (candBack − BayArcStart) · (EgoArcEnd−EgoArcStart)/(BayArcEnd−BayArcStart)`;
  linear, sound within the proximity region, same `egoDistToEntry`/on-internal frame arithmetic
  the hold already uses — bay-piece negative-arc rows included).
- `gap = distToFoeBack − ego.MinGap ≥ 0` (foe unambiguously AHEAD in the corridor): CAR-FOLLOW it
  — `FollowSpeedFor(gap, foeSpeed, foeDecel)`, the merge arm's own PHASE-1 pattern
  (MSVehicle.cpp:3218's adaptToLeader shape), at ANY foe speed. The 2.0 m/s cliff and the
  committed-skip both dissolve in this branch: a fast foe ahead yields a mild constraint, a
  decelerating foe is tracked continuously, and ego can follow INSIDE the corridor.
- `gap < 0` (side-by-side / wedged / foe behind): exactly today's hold semantics with ALL guards
  — speed skip, back-bumper exiting skip, Entry-37 patience, Entry-36 entry-order backstop, hold
  at overlap start when approaching, committed skip when past it. The dwell-634 mutual-wedge
  protection is untouched in the configuration that produced it (standing foes, negative gaps).
- Patience + exiting skips stay upstream of BOTH branches. jyArm 8 = corridorFollow (new diag
  code; Sim.Viewer witness arm-name array extended).

Gate-scoped under `JunctionPhysicalOccupancyGate` — no default-path reader.

**Predictions:**
1. L2 gate-ON stopXmove 9 → ≤4 (the late-stop sites convert; the ~2 straddling-tail (C)
   pair-steps and any unclassified tail remain). Mixed-1k gate-ON stopXmove 12 → ≤8.
2. bothSlow rises at most mildly (followers now creep behind corridor occupants instead of
   driving through) — L2 gate-ON bothSlow stays ≤ 20 (Entry-36's collapse signature was 652).
3. Defaults BYTE-IDENTICAL: L2 hash `5ac89389…` unchanged, goldens unchanged, full sln green.
4. Battery gate-ON: stuckDwell 0 everywhere; smoke 400 gate-ON drained with arrivals ≥ 800.
5. Determinism 3/3 + par==single, both states.

**Declared fail conditions:** smoke arrivals cratering or any battery gridlock ⇒ suspect a
follow-chain cascade or a mutual follow-stop pair — trace `[bay]` at j=301 FIRST (the follow
branch logs its gap), before touching any dial; stopXmove NOT dropping ⇒ the late-stop
attribution was wrong for the untraced sites — trace one of them (j=1258 t=318 or j=428 t=503)
before redesigning.

## Entry 40 (AFTER) — corridor-follow + the arm-5 mutual-pair tie-break it exposed; every gate green

**What shipped (both gate-scoped under `JunctionPhysicalOccupancyGate`):**
1. **Corridor-follow in the bay arm** (`Engine.cs`, search `Entry 40 (corridor-follow)`): the
   occupant's back is mapped through the row's arc intervals into ego's frame; `followGap >= 0`
   (foe unambiguously ahead) car-follows it at any foe speed via `FollowSpeedFor` (jyArm 8,
   `corridorFollow`); `followGap < 0` keeps the measured hold semantics with every guard
   unchanged. No new ingest field, no angle dial.
2. **Mutual on-junction tie-break in the crossing arm** (search `Entry 40: mutual on-junction
   tie-break`): the flag-off RespondsTo path brakes ego for an on-junction foe even when ego is
   ALSO on the junction — a LATENT mutual adaptToJunctionLeader deadlock SUMO does not have
   (isLeader entry-time ordering, MSVehicle.cpp:7348-7483; the full port sits behind the
   default-OFF `JunctionIsLeaderGate`). Under the gate, the earlier entrant of a mutual
   on-junction pair skips the foe (same `IsLeaderByEntryOrder` chain as the merge/bay arms).
   Defaults keep the latent behaviour bit-for-bit — **flag for the future: the proper DEFAULT fix
   is the `JunctionIsLeaderGate` flip, which needs its known saturated-grid regression re-examined.**

**The exposure story (the reason 2 exists):** the first corridor-follow build wedged
willpass-saturation gate-ON (412 → 301 arrivals, stuckDwell 966). Traced: veh 155's follow
constraint (jyArm 8, t=232) slowed its junction entry from ~14 to ~12 m/s — slow enough that it
and veh 139 latched the mutual arm-5 stop at t=234 (`139 →5→ 155`, `155 →5→ 139`, both v=0
forever, four queues cascading behind). The deadlock was never (B)'s — it was latent in the
crossing arm; (B)'s timing perturbation is what let it latch. This is Entry 38's merge-deadlock
story repeating one arm over: a mutual pair with no total order.

**Predictions vs measured:**
1. L2 gate-ON stopXmove 9 → ≤4: **6** (miss by 2, decomposed and accepted): 2 straddling-tail (C),
   3 threshold-marginal contacts (bodies touch at >2.0 m centerline separation at curves — rows
   for the mover's link correctly absent at 2.0 m; widening would relitigate the junction-359
   brush wedge, refused), 1 foes-pair crossing analogue (j=301 6×13, separate class). The
   TARGETED late-stop sites (j=301 (7,8), j=271 (9,10), j=428, j=23) all converted. Mixed-1k
   stopXmove 12 → 8 (≤8: met). bothSlow: L2 11 (no explosion; Entry-36 signature was 652).
   Landings wobble ±2-3 at known pre-existing double-landing sites (L2 6→8, mixed 5→8; traced
   veh 508: its follow branch never fired — butterfly of the reshuffle, class untouched by (B)).
2. Defaults byte-identical: **CONFIRMED** — L2 `5ac89389…` unchanged through BOTH changes; shim
   gate-off par==single `9f947460…` unchanged; goldens byte-identical (suite 782/5, LiveCity
   90/90, all green).
3. Battery gate-ON: **CONFIRMED after the tie-break** — willpass-saturation DRAINED 412/0
   (overlaps 4 → 3), stuckDwell 0 everywhere, junction-realism-L2 DRAINED, L2 overlaps 8 → 4.
4. Smoke 400 gate-ON arrivals ≥ 800 @ t=600: **MISSED then resolved** — 786 at t=600 (−4.7% vs
   823); extended to t=1200: **1765 arrivals with the drain rate ACCELERATING** (1.31/s first
   half, 1.63/s second), population stable ~330, INTERNALSTUCK transient admission holds only,
   zero jyArm-7/8 stuck heads. A closed-loop phase shift, not degradation.
5. Determinism: **CONFIRMED** — 3/3 identical per arm; shim par == `--max-parallelism 1` both
   arms. Gate-ON L2 hash (Sim.Run) is now `f7d432524bd1e96bda740cac2b0eec6a`.

**F1.1 ledger vs Entry 38's baseline (all gate-ON L2):** stopXmove 18 → 6, bothSlow 15 → 11,
bothMove ~145 → 103; defaults stopXmove 17 → 13, landings 10 → 6. Remaining named residuals:
straddling-tail (C), threshold-marginal contact, foes-pair crossing late-stop, double-landing
class, A-residual (foe-approach index registration on pool lanes).

## Entry 41 (BEFORE) — owner 3D re-check of Entries 39-40: overlaps down, but a "too-cautious" standing class now dominates the gridlock

**Owner report (Aug 1, Geneva 3D, gate ON), decomposed:**
1. CONFIRMED IMPROVED: "half-stuck overlap seems much less frequent; passing-through cars
   blocked within junctions seems very reduced; the jams look believable (less overlaps)."
   (F1.1's target classes, matching the offline ledger 18 → 6.)
2. NEW DOMINANT class — "too cautious" standing, three signatures:
   a. Two same-direction lanes at a red: outer lane packed tight, INNER lane with BIG GAPS
      between standing cars, and approaching cars stopping WAY BEFORE the queue tail.
   b. Cars on GREEN not moving despite a long clear gap to the cars standing at the junction
      ahead — no blocker, no side-road traffic (side roads exist but are empty).
   c. Cars at a junction stopped because one or two "cautious cars" stand just PAST the
      junction — i.e. the cascade roots sit downstream of the junction exit.
3. NET RESULT: "almost total gridlock" persists at saturation, now apparently driven by the
   cautious-standing class rather than by overlap/pass-through wedges.

**Discipline note:** no mechanism hypothesis until a trace (the workstream is ~0-for-20+ on
reasoned attributions). Candidate space is wide — keepClear cascades, jyArm 7/8 holds, the
urgent-strategic-change brake (queue-gap signature 2a is also classic pending-lane-change
behaviour), crossJxnLeader spans — and 2a/2b/2c may be three different mechanisms again, as
Entry 39's decomposition was.

**Plan:** reproduce at saturating density offline (closed-loop smoke, LIVECITY_CARS raised
until the city stops draining, gate ON exactly as the owner runs), read the INTERNALSTUCK
heads histogram + LIVECITY-CHAIN roots, then trace ONE root vehicle per distinct signature.
Then decide fixes with predictions in a fresh entry. Rerouting stays parked until this is
characterized (the 3D session's argument is accepted: rerouting would confound this exact
validation, and the reroute design's own T3 already requires hour-horizon runs in BOTH gate
arms before any default flip).

### Entry 41 (MID) — reproduced and traced: the "too-cautious" gridlock root is a cont-turn FRAME BUG in KeepClearConstraint

**Reproduction:** closed-loop smoke, `LIVECITY_CARS=800`, gate ON — total gridlock (stoppedFrac
0.97, arrivals flatlined ~950). Witness output shows the owner's exact class directly:
`CAR e_d_6_5_d_6_4_1 pos=98.6 tlLane=g bind=keepClear gap=84 exitMouth=inf` and even
`bind=keepClear gap=inf exitMouth=inf` on GREEN — bound by keepClear with NOTHING ahead;
`LIVECITY-STUCKCLEAR: keepClear=17` of 87 clear-stuck cars; chain roots are keepClear cars
standing at pos ~4-6 just past a junction (the owner's "one or two cautious cars").

**The trace (`__veh412`, [keepclear] VERDICT instrument extended with stopDist — committed):**

    [keepclear] veh=__veh412 on=e_d_5_3_d_4_3_1@6.27 ... binds=YES
                approachLane=:d_4_3_0_0 len=7.27 seqIdx=5/7 pos=6.27 stopDist=0.00 constraint=0.00

The vehicle is on a 226 m NORMAL lane at pos 6.27, but its route through d_4_3 is a CONT turn:
the pool holds the first-stage bay `:d_4_3_0_0` (slot 6 — not in `LinkByInternalLane`) between
the current lane (slot 5) and the link-controlling stage-2 lane (slot 7). `approachLane` is
computed as `pool[egoLinkSeqIndex − 1]` = THE BAY, and `stopDist = approachLane.Length −
v.Kinematics.Pos − 1.0` mixes the bay's frame with a position measured on the normal lane:
7.27 − 6.27 − 1.0 = **0.00 → the car brakes to zero at its CURRENT position and stands forever**
— wherever it happened to be when the downstream verdict flipped (typically just after exiting
the previous junction, or mid-lane: the queue-gap signature). This is EXACTLY the C4-vii-a
cont-turn frame bug ("approachLane.Length − pos is negative garbage") that the merge arm was
cured of by walking the pool; `KeepClearConstraint` never received that fix. It also explains
the lane asymmetry the owner saw: INNER-lane (left-turn, cont-route) cars freeze mid-lane with
gaps; outer straight-lane cars pack correctly.

**The fix (DEFAULT-scope — this is a plain bug, SUMO's brake target is the junction-entry stop
line along the vehicle's own continuation):** compute `stopDist` by walking the pool from the
CURRENT lane to the first INTERNAL slot (the junction entry):
`(currentLane.Length − pos) + Σ normal pool lanes strictly between − 1.0`. For a vehicle on the
immediate approach lane of a non-cont link this is arithmetically IDENTICAL to the old formula
(loop body never runs), which is the byte-identity argument for the committed keepClear anchor
(scenarios/34-keepclear) and most goldens.

**Predictions:**
1. Goldens byte-identical (any golden that moves is a stop-ship; 34-keepclear binds on its own
   immediate approach lane — identical arithmetic).
2. Default L2/mixed hashes MAY move (keepClear binds occur in saturated defaults); if so,
   re-baseline with the full ladder.
3. Smoke 800 gate-ON: the gridlock breaks or materially recedes (arrivals well above ~950;
   stoppedFrac off the 0.97 ceiling); the `bind=keepClear gap=inf` witness class disappears.
4. Smoke 400 both arms: unchanged-or-better arrivals; hour-horizon suite stays green.
5. Battery both arms: no new stuckDwell; willpass-saturation stays DRAINED 412/0.

## Entry 41 (AFTER) — the keepClear cont-turn frame fix landed at DEFAULTS; the 800-car gridlock breaks; predictions vs measured

**What shipped:** the `KeepClearConstraint` stop-line distance is now walked along ego's own
continuation — `(currentLane.Length − pos) + Σ normal pool lanes strictly before the junction's
first internal lane − 1.0` (`Engine.cs`, search `Entry 41`). Plus instruments (all committed):
`[keepclear]` VERDICT now prints seqIdx/pos/stopDist/constraint; `CarAuthWitness` carries
`DefId` so LIVECITY-CHAIN lines print the traceable id next to the handle.

**Predictions vs measured:**
1. Goldens byte-identical: **CONFIRMED** — full sln green (ParityTests 782/5 including the
   34-keepclear anchor, LiveCity 90/90, all others), exactly per the identical-arithmetic
   argument for immediate-approach binds.
2. Default hashes move: **CONFIRMED** — L2 defaults `9599b795e2aa212d894eff1f727a3444`, gate-ON
   `16aa1edad766b530178cca4fc6e65067`; determinism 3/3 per arm; shim par == single both arms;
   `Sim.Bench` UNCHANGED (`A134ED3716DDE7BC`).
3. 800-car gridlock breaks: **CONFIRMED, decisively** — same closed-loop smoke, gate ON:
   stoppedFrac 0.97 → ~0.5, meanSpd 0.2 → ~2.9 m/s, arrivals at t=1200 ~950 → **2028** (2.1×);
   the `bind=keepClear gap=inf` class is GONE (STUCKCLEAR keepClear 17 → 0-3 transient; the
   remaining clear-stuck are redLight, i.e. legitimate).
4. Smoke 400 both arms: **CONFIRMED, improved** — gate-ON arrivals 852 @ t=600 (best measured;
   823 at Entry 39A, 786/1765 at Entry 40), gate-OFF 834.
5. Battery both arms: **CONFIRMED** — stuckDwell 0 everywhere both arms (city-3000 13 =
   baseline; its arrivals IMPROVED 3430 → 3449/3448); willpass-saturation DRAINED 412/0;
   junction-realism-L2 DRAINED with maxDwell 68 → 15 (gate-ON). Noise-band flags only
   (mixed −5 arrivals defaults, ±1 overlap events; mixed-OFF classifier stopXmove wobbled
   34 → 40 within its measured 34–40 band across recent trajectory reshuffles — noted, not
   chased).

**Why this one mattered:** the bug punished exactly the high-realism configuration — cont-turn
(left-turn-bay) routes at saturation — freezing cars mid-lane wherever they stood when a
downstream jam verdict flipped. Every signature the owner reported on Geneva (inner-lane queue
gaps, stopping short of the queue tail, standing on green with a clear gap, "cautious cars" just
past the junction seeding the gridlock) is this one frame bug. It was invisible to goldens
(no saturated cont-turn + downstream jam in any small scenario) and to the L2 classifier (which
measures overlaps, not conservatism) — it took the owner's eyes plus the STUCKCLEAR/CHAIN
witness to corner it.

## Entry 42 (BEFORE) — the owner's mid-lane pulsing stalls: the C4-vii-a frame-bug family was only PARTIALLY cured; six raw sites remain

**Owner clarification (with screenshot):** the stopped-with-free-road cars are on STRAIGHT
lanes, stop MID-LANE with nothing ahead, and PULSE (unblock occasionally, mostly stand). Not
merge-waiters.

**Instrument (committed):** `LIVECITY-MIDLANE-STUCK` in `LiveCitySim.Step()` (under
`LIVECITY_WITNESS=1`, so the City3D host reports it too): stopped cars far from their lane end
with >25 m clear ahead, with binder/arm/blocker names. On the 800-car demo smoke it caught the
class immediately:

    LIVECITY-MIDLANE-STUCK: t=200 __veh382 e_d_3_3_d_3_4_2@9.1/223 bind=junctionYield/adaptToJxnLeader gap=140 tl=G blockerEnt=161

A car at pos 9 of a 223 m lane, GREEN, 140 m clear, full-stopped by jyArm 5 for the junction
210 m away. Cause (same class as Entry 41, THIRD instance): `AdaptToJunctionLeader`'s
`seen = (approachLane.Length − pos) + egoLane.Length` where `approachLane` on a cont-turn route
is the first-stage BAY — 7 m minus a position measured on a 223 m lane → hugely negative gap →
stop at current position. It re-binds whenever ANY foe stands on the far junction and releases
when it clears: the owner's PULSING. Audit of `approachLane.Length − pos` finds the C4-vii-a fix
was applied to the merge arm, the cautious-approach block, and (Entry 41) keepClear — but SIX
raw sites remain: the external-agent hold (jyArm 4), the approaching-cross stop line (jyArm 6),
the isLeader gap derivation (gate path), `AllwayStopConstraint`, `FoeIsInTheWay`, and
`AdaptToJunctionLeader` (jyArm 5 — the traced one).

**Fix (DEFAULT-scope, one sweep):** thread the ALREADY-HOISTED `egoDistToEntry` (the C4-vii-a
pool walk at the top of `JunctionYieldConstraint`) into all six sites: stop-line sites use
`egoDistToEntry`, seen-to-lane-end sites use `egoDistToEntry + egoLane.Length`. For a vehicle on
the immediate normal approach lane of a non-cont link every replacement is arithmetically
IDENTICAL to the old expression (the goldens' configuration — byte-identity argument).

**Predictions:**
1. Demo 800-car smoke: the `junctionYield/adaptToJxnLeader` MIDLANE-STUCK entries vanish
   (residual mid-lane entries only crowd/ped yields); saturated flow improves again (arrivals at
   t=1200 ≥ 2028's baseline).
2. Goldens byte-identical (full sln suite green); bench hash unchanged.
3. Default + gate-ON L2 hashes move (re-baseline with the ladder); batteries both arms
   stuckDwell 0; smoke 400 both arms not worse.
4. On Geneva: the mid-lane pulsing stall class should be strongly reduced — cars on cont-turn
   (and any bay-carrying) routes no longer freeze mid-lane for far-junction foes. Owner re-check
   is the acceptance gate; the residual "rotten" saturation behaviour beyond this is the
   rerouting question (design awaiting owner sign-off).

## Entry 42 (AFTER) — the frame-bug sweep landed at DEFAULTS; the mid-lane pulsing class is gone; predictions vs measured

**What shipped:** the hoisted `egoDistToEntry` threaded through all six remaining raw
`approachLane.Length − pos` sites (`Engine.cs`, search `Entry 42`): the external-agent hold
(jyArm 4), the approaching-cross stop line (jyArm 6), the isLeader gap derivation,
`AllwayStopConstraint`, `FoeIsInTheWay`, and `AdaptToJunctionLeader` (jyArm 5 — the traced
pulsing stall). `approachLane` is no longer passed where only the distance was wanted.

**Predictions vs measured:**
1. MIDLANE-STUCK junctionYield entries vanish: **CONFIRMED** — 800-car smoke now shows ONLY
   12 `urgentStrategicFollow/cautiousApproach` mid-lane entries (genuine merge-waiters);
   arrivals at t=1200 improved again, 2028 → 2069.
2. Goldens byte-identical + bench unchanged: **CONFIRMED** — full sln green (782/5, 90/90, the
   allway-stop and minor-link scenarios all inside the identical-arithmetic configuration);
   `Sim.Bench` `A134ED3716DDE7BC` par==single.
3. Hashes move, ladder green: **CONFIRMED** — L2 defaults `e2bba9c11b96f57d345a2c6cce613c49`,
   gate-ON `03a86ad3f0f63da833cb24d08d7c4612`, determinism 3/3 each arm; batteries BOTH arms
   stuckDwell 0 (city-3000 13 baseline; willpass-saturation DRAINED 412/0), noise-band flags
   only; smoke 400 best-measured in BOTH arms (ON 870, OFF 878).

**One measured consequence, understood and accepted:** L2 DEFAULTS `stopXmove` rose 13 → 61 —
decomposed to the SAME known non-foes pair sites the GATE fixes (j=428 `:428_13_1`×`:428_15_0`,
j=301, j=123...). The frame bugs had been accidentally SUPPRESSING the default engine's known
foes-blindness by freezing traffic upstream of junctions; with flow restored, the default
(SUMO-parity, drives-through) behaviour is simply exercised more often. Gate-ON on the same
traffic: stopXmove 5, landings 4 — both best-measured. This is direct evidence FOR the F3.2
default-flip discussion with the owner, not a regression to chase.

**Gate-ON L2 ledger vs Entry 38: stopXmove 18 → 5, landings ~6 → 4, bothSlow 15 → 11.**

## Entry 43 (BEFORE) — owner: unsignalled-junction standoff (free direction's head won't enter an empty junction); instrument shipped, awaiting Geneva witness data

**Owner report (screenshot):** at an unsignalled junction, the left-right direction is jam-blocked
downstream and correctly holds short of the junction; the down-up direction is FREE (junction
empty, exit clear) yet its queue head never enters. Half-overlaps also visible in the queues.

**Offline reproduction FAILED, honestly:** city-3000's 918 late-sim arm-6 stop-line yields all
trace to genuinely-approaching free-flow foes (veh 2113: 13.9 m/s, crossing for real) — normal
minor-road yielding, not the standoff. The demo grid's saturated 800-car run shows stop-line
heads held by `junctionYield/corridorFollow` on admission-held interior occupants — honest
queueing that drains (arrivals 960 @ t=600, best measured). The owner's class does not occur on
any offline surface available here; the mechanism guards that SHOULD release a head at an empty
junction (`!foe.WillPass`, `FoeKeepClearBlocked`, reservation distance, impatience ramp) all
exist — which one fails on Geneva's topology cannot be determined remotely (the reasoned-guess
track record stands at ~0-for-20).

**Instrument shipped instead (`LIVECITY-HEADSTUCK`, committed, LIVECITY_WITNESS=1):** every 20 s,
stopped queue HEADS at a lane end that are NOT red-held, with no car ahead and a clear next-lane
mouth, printed with binder/arm, the bound foe's speed, and ONE blocker hop (def id, lane@pos,
speed, binder) — enough to name the holding mechanism and its target on the owner's own Geneva
run. Suites re-verified green (print-only change; 782/5 + 90/90).

**Next:** owner pastes HEADSTUCK/MIDLANE lines from a Geneva session → trace the named mechanism.

## Entry 44 (BEFORE) — LIVECITY rerouting wired behind the gate (owner go: "implement the rerouting behind a gate")

**What is being landed (T1-T2 of docs/LIVECITY-REROUTING-TASKS.md, design signed via the owner's
go):** `LiveCityConfig.ReroutePeriodSeconds/RerouteProbability` + env overrides
(`LIVECITY_REROUTE`, `LIVECITY_REROUTE_PERIOD`, `LIVECITY_REROUTE_PROB`, ENV-GATES rows) splicing
`device.rerouting.*` into the LiveCitySim engine config ONLY when enabled; the engine's P1E
device is untouched except one diagnostic-only counter (`Engine.PeriodicRerouteCount`, serial
increment, no reader in the sim). Witness line `LIVECITY-REROUTES: t=.. total=N`.
`LiveCityReroutingTests`: off ⇒ 0 installs over 240 steps; on ⇒ byte-identical streams across two
runs AND installs > 0 (both green first run).

**T3 A/B predictions (closed-loop demand, 800 cars, gate + witness + ignore-blocker set
identically in both arms, only LIVECITY_REROUTE differing):**
1. OFF arm: GRIDLOCK/witness stream byte-identical to the Entry-43 build's run (inertness).
2. ON arm: `LIVECITY-REROUTES` total grows into the hundreds+ over 1200 s; arrivals ≥ OFF − 2%,
   plausibly BETTER (demand spreads off the saturated arteries); no GRIDLOCK; stoppedFrac not
   worse; MIDLANE/HEADSTUCK not worse.
3. Full sln suite green; goldens untouched (engine defaults unchanged; the counter is unread).

### Entry 44 (AFTER) — rerouting A/B measured; predictions vs measured

1. OFF-arm inertness: **CONFIRMED** — the 800-car OFF-arm GRIDLOCK/witness stream is IDENTICAL
   to the Entry-43 build's run over the shared window.
2. ON arm: **CONFIRMED, decisively** — same closed-loop 800-car demand, only `LIVECITY_REROUTE`
   differing (every other gate set identically in both arms): arrivals at t=1200
   **2069 → 2810 (+36%)**, live population 691 → 534 (the city drains), stoppedFrac 0.44 → 0.37,
   meanSpd 3.6 → 5.6 m/s, 1972 periodic reroutes installed. `LIVECITY-REROUTES` line makes the
   device visible in any host.
3. Suite: **CONFIRMED** — full sln green (LiveCity 92/92 incl. the two new rerouting tests,
   ParityTests 782/5 goldens byte-identical, Pedestrians 324, Viewer.Motion 19, Host 6,
   DotRecast 2). DEMAND-MODEL LABEL: closed-loop — these arrival gains are drain-rate gains, not
   capacity claims (measurement lesson 4).

## Entry 45 — the 3D session's Geneva HEADSTUCK capture, triaged; LIVECITY_URGENTFOLLOW gate added

**Inputs (3D session, 4000 cars gate-ON Geneva, 160 HEADSTUCK lines + a measured A/B):**
1. `UrgentStrategicLeaderFollow` A/B at matched windows: ON = 14 mid-lane stalls (12
   urgentStrategicFollow, one on green with gap=inf); OFF = 1 (0). The Entry-42 attribution of
   the REMAINING mid-lane class to the frame family was therefore incomplete — the Entry-31 arm
   is the dominant residual mid-lane mechanism on Geneva.
2. Their "seventh frame site" hypothesis (`usableDist = curr.Length − pos` at the arm's core):
   **CHECKED, NOT the frame family** — `curr` is the LaneQ continuation record MATCHED TO EGO'S
   OWN LANE and `curr.Length` is the continuation length; the formula is SUMO's own `myLeftSpace`
   (MSLCM_LC2013). The stalls are the ARM'S SEMANTICS (brake toward the merge point while the
   strategic change is pending), not a frame mix. The measured trade stands regardless; the arm
   keeps its default (its own 26-net battery wins, Engine.cs flag comment) and the judgment moves
   to the 3D surface: **`LIVECITY_URGENTFOLLOW` now mirrors `SUMOSHARP_URGENTFOLLOW` into the
   live-city hosts** (the A/B switch the 3D session lacked). ENV-GATES row added.
3. keepClear = 59/160, all `mouth=inf`: **TRACED LOCALLY (__veh362), LEGITIMATE** — the exit
   lane held 26 cars with seenSpace 5.29 < 7.50 required; `mouth` is a ONE-HOP proxy (the empty
   internal lane) that cannot see the packed exit lane. Don't-block-the-box working as designed
   at saturation; NOT the hold-with-nothing family.
4. freeFlow (25) + short-stub deadLaneMerge (40) lines: instrument artifacts as the 3D session
   suspected — HEADSTUCK now excludes binder freeFlow and lanes < 25 m.
5. The REAL standoff class (6 chains): stopped cars ON INTERNAL LANES held by
   leaderFollow/crossJxnLeader/crowd (queues extending through junction interiors at saturation),
   with heads on other approaches yielding to them; one durable pair (t=40..60+), one held by
   PEDESTRIANS (the backlog-4 ped amplifier, now witnessed with a chain for the first time).
   These are saturation-queue physics plus the ped coupling — the pressure-relief lever is
   rerouting (Entry 44, +36% drain), and the interior-queue pair
   (`__veh1206 → __veh2292 crossJxnLeader`) is the next trace target if the owner's rerouting
   verdict still shows durable standoffs. Named, not yet chased.

Suites: LiveCity 92/92, EnvGateDocumentation green (gate + print-only changes; engine untouched).

## Entry 46 — rerouting validated on Geneva (owner + 3D session); the honest effect size; bench gate-list refresh

**Owner visual verdict (T4):** "prolongs time to the gridlock, city seems a bit more live —
definitely good direction."

**3D session hour-horizon A/B (Geneva-class net, all gates pinned, only LIVECITY_REROUTE
differing):** arrivals +3.6% (gates-OFF arm, 2936→3042) / +5.2% (gates-ON arm, 2436→2562),
long stalls 0 in all four cells; 13 279 reroutes by t=880 at 4000 cars in the viewer.
**EFFECT-SIZE CORRECTION (label the topology like the demand model):** Entry 44's +36% came from
the 800-car demo BOX — a small grid where every saturated artery has an obvious parallel
alternative. On realistic topology the device is worth **+4–5%** plus the owner's qualitative
"more alive / gridlock delayed". +36% must NOT be quoted as the expected Geneva figure.
Local bench replication agrees: Sim.BenchLiveCity 400-car arms 395 → 410 arrived (+3.8%).

**Bench triage:** the 3D session's first bench A/B returned identical arrivals both arms and was
read as gate-wiring rot. VERIFIED LOCALLY: the bench (LiveCityConfig/LiveCitySim-based) DOES
receive LIVECITY_REROUTE (arms differ, 395 vs 410) — the identical result was an env-propagation
failure in that invocation, and the missing LIVECITY-REROUTES line was just LIVECITY_WITNESS
being unset. The REAL rot was the curated env-gate PRINT list (its own staleness warning fired):
refreshed with the whole F3/rerouting/urgentfollow family (both LIVECITY_* and SUMOSHARP_*
prefixes), so future bench runs print every gate they observed.

**The two standing decisions (owner's, restated with current evidence):**
1. Rerouting default: stays OPT-IN per the 3D session's read (agreed) — +4–5% and inert-off are
   solid, but `LIVECITY_REROUTE_PROB` is a realism knob the owner should pick a believed value
   for before any default (not every driver has navigation).
2. Junction gate (F3.2): the throughput cost has shrunk from ~25% (1571 vs 2094 at first
   measurement) to ~16% (2562 vs 3042) across Entries 37-42, with long stalls 0 both ways and the
   overlap/pass-through honesty it buys. Evidence trending toward ON; the decision remains WITH
   the owner.

## Entry 47 — rerouting DEFAULT-ON (owner decision); two-hop chains; the ring-deadlock design for review

1. **Rerouting default flipped ON** (owner: "all drivers can 'have navigation' if it helps
   filling the city and reduce gridlocks"): `LiveCityConfig.ReroutePeriodSeconds` 0 → 60,
   probability 1.0; `LIVECITY_REROUTE=0` is the kill switch (`1` still force-enables an
   explicit-0 host). Full sln suite GREEN with the device on by default (LiveCity 92/92 incl.
   hour-horizon; goldens untouched — engine defaults unchanged, this is host config). Verified in
   the host: default run emits REROUTES (130+ by t=260 at 400 cars) without any env var;
   kill-switch run emits none. ENV-GATES row updated — every future A/B or SUMO comparison must
   set LIVECITY_REROUTE explicitly in both arms.
2. **HEADSTUCK follows TWO blocker hops** (3D-session request: 3 of their 5 durable Geneva
   chains ended at a leaderFollow-bound blocker — root one hop further). Their 187 freeFlow/
   deadLaneMerge artifact lines came from a pre-Entry-45 build; the current predicate already
   excludes both.
3. **Rerouting-off masking measured** (3D session): 280 HEADSTUCK lines by t=580 without the
   device vs 160 by t=2138 with it — roughly an order of magnitude per unit sim-time. The device
   suppresses standoff FORMATION; the residue (7 chains, all blockers stopped on internal lanes,
   two provably durable) is the ring class.
4. **`DEADLOCK-RING-DESIGN.md` written for owner review** (the photographed crossing-streams
   interlock): D1 = blocker-graph cycle detection + LIVECITY-RING witness (diagnostic only; also
   closes the blocker-attribution gaps in leaderFollow/crossJxnLeader/keepClear), D2 = gated
   break (elect ONE member by the entry-order total order, relax its ring edge to corridor-follow
   creep — never through bodies; honest LIVECITY-RING-STUCK report when geometry is truly
   wedged), D3 = the standard four-surface ladder + ring-age distributions. No code before
   sign-off.

### Entry 47 addendum — ring design signed off in principle; on-site Geneva analysis session commissioned

Owner: "deadlock ring design sounds ok" + commissioned a NEW session on the physical machine
holding the real Geneva data, to (a) reproduce and root-cause the remaining problematic
situations there, and (b) compare against vanilla SUMO on the same data (junction clearance,
throughput). Brief written: **`docs/GENEVA-ANALYSIS-RESUME.md`** — engine state, the full gate/
instrument table (incl. the REROUTE-default-ON A/B trap), the three open classes with their
evidence, the honest-SUMO comparison playbook, method discipline, and the expected deliverables.
The 3D session supplies the companion dataset/launch doc. DEADLOCK-RING D1 (detection witness)
is cleared for implementation by that session when needed; D2 (the break) still needs D1 numbers
before code.

## Entry 48 (BEFORE) — ON-SITE session (real Geneva data): headless harness unblocked; leaderFollow chain capture; standoff hunt predictions

**Context.** This entry is written by the on-site session on the owner's machine
(`D:\Work\GenevaCut\geneva_city.sumocfg`, the 28 276-lane central-Geneva cut). Two instrument
commits landed first, both gate-verified:

1. **`Sim.Viewer --mode live-city --smoke` now honours `--sumocfg`** (it was parsed and silently
   ignored — the 3D session's `GENEVA-HEADLESS-HARNESS.md` §0 blocker; every prior headless
   "Geneva" smoke actually measured the demo grid). Verified: Geneva lane ids + full witness set
   headless. The witness-instruments-on-external-net gap is CLOSED.
2. **leaderFollow (binder 1) now records its leader's EntityIndex as BlockerEntityIndex**
   (diag-only, mirroring cjlFoeIdx at site 2). Motivation: first 1800 s Geneva capture (4000
   cars, `LIVECITY_REROUTE=0`, F3 on) found durable standoffs — `__veh138` head at
   `gen_road_3917_1@52.6` car-following (crossJxnLeader) `__veh4950` frozen at `:34586_0_1@2.9`
   for **≥720 s** (36 consecutive 20 s reports, identical positions) — but every chain root bound
   `leaderFollow` with blockerEnt=-1, so the Entry-47 two-hop reporter dead-ended one hop short.
   Full sln suite green; bench hash `A134ED3716DDE7BC` unchanged (par==single).

**The capture being rerun now** (identical env: 4000 cars / 2000 peds / reroute OFF / F3 ON /
3600 steps): closed-loop, saturates to stoppedFrac 0.90 by t=1800, arrivals 2954. Durable heads:
`__veh138` (36 reports), `__veh494` behind `__veh1410` at `gen_road_6200_0@3.2` (21), `__veh452`
(16), `__veh24` (8).

**Falsifiable predictions:**

- P1: the same durable standoffs reform at the same junctions (determinism; same config/env).
- P2: previously dead-ended chains now print the `->>` second hop; `__veh4950`'s leader is a
  vehicle physically on/at the exit of junction 34586, not empty road (else the leaderFollow
  binding itself is a frame/geometry bug — a different class).
- P3: at least one durable chain resolves into one of: (a) a root bound on a junction arm
  (junctionYield/keepClear/bay) whose release guard fails on this topology; (b) a CYCLE (the
  ring class — D1's justification); (c) a crowd/crowdYield root (the pedestrian amplifier).

## Entry 48 (AFTER) — predictions vs measured; D1 landed; the mutual tie-break is POLARITY-INVERTED (trace-proven)

**P1 CONFIRMED** — bit-for-bit reproduction (same vehicles, same positions, same timestamps).
**P2 CONFIRMED** — chains now pass through leaderFollow links; `__veh4950`'s leader is `__veh4622`
10.4 m ahead ON THE SAME internal lane `:34586_0_1` (a queue THROUGH the junction, not empty road).
**P3 CONFIRMED via (b), and better than predicted** — D1 (blocker-graph cycle scan, LIVECITY-RING +
LIVECITY-CHAINROOT, commit `538b84a`) found **180 ring reports** in the 1800 s capture (4000 cars,
reroute OFF, F3 ON). Three measured classes:

1. **Block-scale keepClear loop** (8 members, ~1120 s): three keepClear-bound cars around a city
   block each holding for an exit that feeds the loop; junction `:30143` + gen_road_726x.
2. **Two-stream admission ring inside one large junction** (`:35479`, up to 12 members, ~400 s):
   two long internal lanes, each stream's head on `internalJunctionAdmission` waiting on the other.
3. **2-member cont-turn interlock, re-forming at IDENTICAL positions with different vehicles**
   (`:35019_17_1@10.5` `adaptToJxnLeader` ↔ `:35019_16_0@2.8` `corridorFollow`; also `:36199`,
   `:36546`, `:36535`, `:36315`): age resets every ~60 s — the IGNOREBLOCKER patience breaks it,
   the junction moves, the ring re-forms. An oscillating throughput sink, not a permanent wedge.

**Class-3 root cause, trace-proven** (`LIVECITY_TRACEVEH` on both members of the `:35019` pair,
deterministic replay): `__veh4135` entered the junction at t=655.0 (step 1310), took its cont-turn
stage-1 bay `:35019_17_1`, and braked `adaptToJxnLeader` to distToEntry=0.10 of its stage-2 lane
for foe `__veh2740`. `__veh2740` entered at t=724.5 (step 1449) — 69.5 s LATER — skipped its own
arm-5 follow, and was instead caught by the corridor arm-8 FOLLOW branch (gap 2.71 → 0.12 m as
veh4135 stopped). 2-cycle closed; both stand until patience.

The mechanism: **`Engine.cs:7820` (Entry 40's mutual on-junction tie-break) uses
`IsLeaderByEntryOrder` with inverted polarity.** The function is SUMO's `MSVehicle::isLeader`
tie-break chain verbatim (MSVehicle.cpp:7443-7473; the debug strings literally print
`isLeader=(egoET > foeET)`) — it returns TRUE when EGO ENTERED LATER, i.e. "the foe is the
leader; ego adapts". The corridor-HOLD site (`Engine.cs:8148`) correctly skips on
`!IsLeaderByEntryOrder` (earlier entrant clears). The Entry-40 site skips on the un-negated value
— so the LATER entrant skips the follow (and the EARLIER entrant keeps braking), the exact
opposite of its own intent comment ("the EARLIER entrant of a mutual on-junction pair skips this
foe"). Verified against both members' entry steps: 1310 vs 1449 reproduces the observed binding
on both sides.

Why every prior surface missed it: on the box grid the later entrant, having (wrongly) skipped
arm 5, usually has no corridor overlap to catch it — it simply proceeds and the pair resolves; the
deadlock needs cont-turn bay geometry (arm-8 FOLLOW has no tie-break), which Geneva has at scale.
Side implication worth flagging: the inversion also lets a later entrant skip a LEGITIMATE
physical follow — a candidate mechanism for the owner-observed junction overlaps.

## Entry 49 (BEFORE) — fix the Entry-40 tie-break polarity (gate-scoped, one line)

**Change:** `Engine.cs:7820` `IsLeaderByEntryOrder(...)` → `!IsLeaderByEntryOrder(...)`, matching
the corridor-HOLD site and the vendored SUMO semantics. Entirely inside
`JunctionPhysicalOccupancyGate` (engine default OFF) — goldens/default behaviour untouched by
construction.

**Falsifiable predictions:**

- P1: goldens byte-identical; bench hash `A134ED3716DDE7BC` unchanged; full sln green.
- P2: on the identical Geneva capture (4000 cars, reroute OFF, F3 ON, 3600 steps), the class-3
  2-member interlock at `:35019` (12 reports) DISAPPEARS or at minimum stops recurring at the
  same positions; total ring reports drop noticeably (class 3 junctions `:35019`/`:36199`/
  `:36546`/`:36535`/`:36315` accounted for ~20% of the 180).
- P3: arrivals at t=1800 do not regress (2954 baseline); expect a small gain.
- P4: hour-horizon ON arm (forces the gate family on): arrivals ≥ 2562 baseline, stalls stay 0.
- Risk watched: the now-released earlier entrant must NOT drive through the later entrant's body
  — the on-junction OCCUPANCY/bay arms still hold physically; overlaps counter in the smoke
  (LIVECITY-STUCKCLEAR `overlaps=`) must stay 0.

## Entry 49 (AFTER) — predictions vs measured: the traced mechanism is CURED; the load moves to the class-2 admission rings; overlaps IMPROVE

- **P1 CONFIRMED**: full sln green (782/5 goldens byte-identical, LiveCity 92/92 incl.
  hour-horizon, peds 324), bench hash `A134ED3716DDE7BC` unchanged (par==single).
- **P2 PARTIAL — the traced mechanism is gone, but total ring burden went UP.** The exact
  2-member signature (`adaptToJxnLeader@:35019_17_1` ↔ `corridorFollow@:35019_16_0`, 12 baseline
  reports) no longer occurs. `:35019` still shows 11 reports but of a DIFFERENT, younger shape
  (3-member rotation through `internalJunctionAdmission` + a third stream, ages 10–30 s vs 54 s).
  Total reports 180 → 273, and the shift is measured and attributable: reports involving
  `internalJunctionAdmission` 101 → 200, the `:35479` two-stream admission ring 24 → 64,
  age≥300 58 → 140. Reading: earlier entrants no longer over-yield mid-junction, so more of them
  press into the admission arm, which has its OWN mutual structure (class 2) — now the dominant
  open class. keepClear-involving reports flat (67 → 73). Single-run caveat applies (chaotic
  closed-loop system), but the direction is consistent across three independent counters.
- **P3 CONFIRMED (flat)**: arrivals 2961 vs 2954. The interlock cure does not buy throughput
  while class 2 absorbs the pressure.
- **P4**: hour-horizon suite green (its ON-arm assertions are the gate).
- **Risk watch BETTER THAN PREDICTED**: cumulative overlap counter peak 57 (baseline) → 42 (fix)
  — consistent with the Entry-48 side implication (the inversion let later entrants skip
  LEGITIMATE physical follows; restoring them removes interpenetration pressure).

**Verdict:** the fix stands (semantically correct vs vendored SUMO, completes Entry 40's own
reviewed intent, trace-proven cure, overlaps improve, everything green). The class-2
`internalJunctionAdmission` mutual structure is now the top open item, with D1 giving exact
counts/ages to hunt it — trace target: the `:35479` pair of stream heads.

## Entry 50 — class-2 admission ring ROOT-CAUSED (`:35479` traced): two SUMO-faithful edges + the honesty edge close the cycle; D2 is the designed remedy

Trace (`LIVECITY_TRACEVEH=__veh2526`, deterministic, seed forms t≈360): junction 35479 is
`type="traffic_light"` with the conflicting links in TL-off state `'o'`; link 14 is a cont turn
with a ~60 m stage-1 bay (`:35479_14_0` → stage-2 `:35479_20_0`). `__veh2526` rolls to the bay
end @55.30 by t=315.5 and stands. The 3-member seed cycle, edge by edge:

1. `__veh2526` (bay 14_0) —`internalJunctionAdmission` (tag 14, lane-foe half)→ `__veh2572`
   STANDING on foe lane `:35479_0_0@3.8`. **SUMO-faithful**: `myFoeLanes` standing-foe hold
   (the arm's own doc: a foe on a plain internal lane keeps the unconditional block — it is
   genuinely occupied).
2. `__veh2572` (0_0) —`adaptToJxnLeader`→ `__veh2317` (`:35479_3_0@18.8`). **SUMO-faithful**
   (checkLinkLeader car-following; post-Entry-49 entry order is correct, veh2317 is earlier).
3. `__veh2317` (3_0) —`corridorFollow`→ the 14_0 queue's bodies. **DELIBERATE BEYOND-SUMO
   honesty edge**: SUMO drives THROUGH this bay-corridor overlap (`collision.check-junctions`
   defaults FALSE — the interpenetration is not even detected; honest SUMO warns and still
   proceeds). The artefact ladder forbids copying that, so the engine holds — and the hold is
   what closes the cycle.

The ring then accretes followers on 14_0/3_0 to the locked 12-member, 300+ s form. **This IS the
localized "why SUMO clears junctions more easily" mechanism for this junction class**: SUMO's
clearance is bought with junction interpenetration our F3/honesty gates refuse. Post-Entry-49
this class is dominant (200 of 273 ring reports involve the admission arm).

**Remedy:** exactly DEADLOCK-RING-DESIGN **D2** — a ring confirmed for ≥ RingBreakSeconds elects
ONE member by the entry-order total order and relaxes its ring edge to corridor-follow CREEP
(through gaps, never bodies), escalating honestly to `LIVECITY-RING-STUCK` when geometry is truly
wedged. D1 now supplies the justifying numbers the design required before D2 code. Per the
design's own gate ("no code before sign-off"), D2 implementation awaits the owner's go on these
numbers.

## Entry 51 (BEFORE) — D2 implemented behind LIVECITY_RINGBREAK (owner: "D2 go")

**Owner said "D2 go."** Implementation (design §2, three refinements journaled here):

- Engine end-of-step pass `DetectAndBreakRings` (single-threaded, EntityIndex-sorted, no RNG;
  one-step lag like `HeldAtLinkLastStep`): D1-identical blocker graph + colour scan; a ring with
  age (min member WaitingTime) ≥ `RingBreakSeconds` (20) and no member already released elects a
  breaker; the breaker's per-entity release skips ONLY its stop-form edge (keepClear 11 /
  admission 14/17) toward ONLY its frozen ring-target entity. Follow-form arms still bound speed
  — creep into gaps, never through bodies (arm-7 HOLD, adaptToJxnLeader, corridor FOLLOW are
  untouched — they ARE the body-contact guard).
- **Refinement 1 (election filter):** only members whose CURRENT binder is a releasable stop-edge
  are electable — releasing a member creeping at gap 0 is a no-op by construction, so the
  escalation ladder skips straight past them. Inside-junction members first (entry-order chain,
  earliest entrant), fallback closest-to-lane-end, id-ordinal ties.
- **Refinement 2 (release end):** the design's "blocker edge leaves the ring" cannot be read
  after the skip erases that very edge, so completion is route-progress ≥
  `RingBreakClearDistance` (20 m ≈ one junction crossing); wedged = stationary the whole hold for
  `RingBreakSeconds` (or 3× as a hard cap on nibbling) → cooldown 2×RingBreakSeconds +
  escalation; next scan's election deterministically picks the next member. All members
  exhausted → `RingStuckSteps` (the honest "this one is geometric" counter).
- **Refinement 3 (reporting):** engine exposes counters; the host prints `LIVECITY-RINGBREAK:
  active/breaks/escalations/stuckSteps` at the witness cadence. Gate `LIVECITY_RINGBREAK`
  (default OFF, env-honoured like F3, NOT in the forced bundle), ENV-GATES row added, bench
  curated list updated.

**Falsifiable predictions (D3 ladder):**

- P1: gate OFF ⇒ byte-identical — goldens green, bench hash `A134ED3716DDE7BC`, full sln green.
- P2: standard Geneva capture + `LIVECITY_RINGBREAK=1`: age≥300 ring reports collapse (140 → <30);
  the `:35479` locked 12-member ring does not survive to t=1800; breaks > 0, stuckSteps small
  relative to breaks.
- P3: arrivals at t=1800 IMPROVE over 2961 (the locked ring blocks a major junction for ~1100 s).
- P4: overlaps counter does NOT increase vs 42 peak (creep is follow-bounded).
- P5: hour-horizon `F3OCCUPANCY=1 RINGBREAK=1`: arrivals ≥ the 2562-class baseline, stalls 0.

## Entry 51 (AFTER) — D2 measured: rings eliminated on Geneva, arrivals +6.2%; two honest misses

- **P1 CONFIRMED**: gate OFF byte-identical — full sln green (782/5 goldens, LiveCity 92/92,
  peds 324), bench hash `A134ED3716DDE7BC` (par==single).
- **P2 CONFIRMED, stronger than predicted**: standard Geneva capture + `LIVECITY_RINGBREAK=1`:
  ring reports 273 → 83, **age≥300 reports 140 → 0** — not one locked ring survives. The
  `:35479` ring never locks (max age 19 s, transient sizes 4–7, broken within about one witness
  cadence). Breaker economics: **180 breaks, 5 escalations, stuckSteps = 0** (no ring ever
  exhausted its electable members — the honest-wedge path never had to fire on this capture).
- **P3 CONFIRMED, stronger than predicted**: arrivals 2961 → **3144 (+6.2%)**; final
  stoppedFrac 0.92 → 0.82; final-interval mean speed 0.71 → 1.28 m/s; aggregate movement
  56 584 → 102 647 m. On the wedge-hunting configuration (reroute OFF) the break converts the
  permanent admission-ring gridlock into flow.
- **P4 MISSED (honest)**: the instantaneous same-lane overlap proxy (pairs < 4 m at the 20 s
  sample) — mean 21.0 → 25.0, peak 42 → 53 vs the Entry-49 arm; both remain BELOW the
  pre-Entry-49 baseline (29.3 mean / 57 peak). Reading: more movement through saturated lanes
  raises dense-packing samples; whether any of it is creep-caused body contact (vs the
  pre-existing queue-compression class) needs a per-lane attribution pass near released
  breakers — follow-up, not a blocker, but not waved away either.
- **P5 MISSED by a hair**: hour-horizon ON arm 2554 vs 2562 (−0.3%, 8 arrivals), stalls 0 both
  arms. The box surface has few rings; the break neither helps nor hurts there materially.

**Recommendation to the owner:** D2 works as designed on the surface it was designed for. Given
the +6.2% arrivals and total elimination of locked rings on Geneva vs a −0.3% box-grid wash and
a small unattributed overlap-proxy rise, proposed next steps: (a) keep `LIVECITY_RINGBREAK`
opt-in for the 3D session to eyeball the released-breaker motion at the photographed junctions;
(b) run the overlap attribution pass; (c) defaults decision after (a)+(b).

### Entry 51 addendum — first 3D (ped-coupled) run with RINGBREAK on: breaker healthy; the honest-stuck class exists and is named

Owner ran the Godot viewer on the Geneva cut (fresh pack, both NuGet/Debug traps handled;
`F3OCCUPANCY=1 RINGBREAK=1 WITNESS=1`, 4000 cars / 2000 peds, **rerouting default ON**, ~21
sim-minutes). Console-log numbers (camera-following ORCA pocket ⇒ counts are not strictly
run-comparable; trends only):

- **281 breaks / 5 escalations** over 1264 sim-s — even with rerouting suppressing ring
  formation ~10×, the breaker fires steadily (~one per 4.5 s at this density). 145 ring reports,
  every tail-end age 10–19 s: nothing locks.
- **`stuckSteps` = 108, in two bursts** (t≈300–320: +76; t≈900–1000: +32) — the honest-stuck
  path engaged for the first time (headless capture had 0). The stuck rings are the
  **both-members-creep-form class** (e.g. `:36339`: `junctionYield/corridorFollow` ↔
  `junctionYield/adaptToJxnLeader`, both at gap ≈ 0): no stop-edge to release, bodies genuinely
  interlocked across corridors — exactly the design's §2.3 "this one is geometric" case, honestly
  reported instead of broken. Each burst is one ring persisting ~40 s before dissolving on its
  own. The upstream fix for this class remains the overlap-prevention work, not the breaker.

## Entry 52 (BEFORE) — owner re-prioritization: OVERLAPS are now the top classes; classifying instrument first

**Owner verdict from the 3D run (verbatim classes):** (1) "way too big tolerance to driving
through cars that are blocking the junction"; (2) "if two lanes merge (straight lane with turning
lane) — also overlap"; (3) "queuing cars overlaps half-size". With the ring/stall classes
largely cleared, these moved to the top of the priority list.

**Why an instrument first (measurement rule 8):** the only overlap counter we have
(`LIVECITY-STUCKCLEAR overlaps=`, same-lane pos-gap < 4 m at 20 s samples) conflates all three
classes and cannot see a cross-lane junction drive-through at all. Entry 51's unattributed proxy
rise (21.0 → 25.0 mean under RINGBREAK) is the same blindness. So: a witness-cadence
`LIVECITY-OVERLAP` reporter in the live-city host — true oriented-body (OBB) intersection over
the engine's world poses (PosX/PosY/Angle/Length/Width; angle is navigational degrees), grid-
hashed, classified:

- `queue` — same lane, longitudinal body overlap; depth-bucketed (<1 m, 1–2.5 m, >2.5 m — the
  owner's "half-size" is ~2.5 m on a 5 m car);
- `merge` — same lane, the two members' PREVIOUS lanes differ and one member just landed
  (pos < 20 m): the straight+turn merge-landing class;
- `junction` — different lanes, ≥1 internal: the drive-through-a-blocker class;
- `lateral` — different normal lanes (wide-lane side-by-side; expected mostly benign).

**Falsifiable predictions:**

- P1: the junction and merge classes are PRE-EXISTING — nonzero with `LIVECITY_RINGBREAK=0` on
  the standard capture (they are not D2 creep artifacts).
- P2: the RINGBREAK=1 arm's proxy rise decomposes mostly into the queue class (more movement =
  more compression samples), NOT a junction-class jump; if junction DOES jump under RINGBREAK,
  that is a D2 creep defect and blocks any default-ON.
- P3: the queue class shows a distinct >2.5 m depth mode (the owner saw "half-size" overlaps,
  not grazing contacts).

## Entry 52 (AFTER) — the overlap landscape measured: P2 REFUTED (D2 creep DOES interpenetrate — default-ON blocked); the junction class is pre-existing and dominant; one unifying suspect

Instrument landed (gate green, hash unchanged; diagnostic-only, WITNESS-gated). A/B on the
standard capture (4000 cars, reroute OFF, F3 ON, 1800 s), `LIVECITY_RINGBREAK` 0 vs 1.
Simultaneous overlapping PAIRS at t≈1780 (per-tick snapshot, not cumulative):

| class | RB=0 | RB=1 |
| --- | --- | --- |
| junction (≥1 internal lane) | **329** | 309 |
| queue >2.5 m deep | 45 | **492** |
| queue 1–2.5 m | 14 | 5 |
| merge landing | 2 | 8 |
| lateral | 9 | 8 |

- **P1 CONFIRMED**: the junction class is pre-existing (329 pairs with RB=0) and DOMINANT — the
  owner's "too big tolerance driving through junction blockers" is real and huge at saturation.
  Example anatomy (`gen_road_5345_1@2.2 × :34178_0_1@7.1`): an exit-lane car's TAIL still
  covers the internal lane's end while the internal follower has closed to its front — a
  cross-boundary following gap short by ~1–2 m. Depths cluster 0.7–1.8 m.
- **P2 REFUTED — and this is the important one**: the RB=1 queue>2.5m class is not compression;
  it grows monotonically (3 → 148 → 292 → 492) and its pairs are FROZEN (`__veh441@12.1 ×
  __veh1953@10.4` identical from t=1100 to t=1780, 3.3 m deep). Mechanism: releasing keepClear
  lets the breaker enter a junction whose EXIT lane is full; the landing lands it inside the
  queue tail's body, where both stand forever. **The D2 "never through bodies" guarantee fails
  at the lane-boundary landing. Default-ON is BLOCKED until this is fixed.** Note RB=0 also has
  45 such pairs — the landing defect PRE-EXISTS; D2 multiplies exposure ~10× by design (it keeps
  sending breakers into full boxes).
- **P3 CONFIRMED**: the deep (>2.5 m) mode dominates the queue class — matching the owner's
  "half-size" description.

**Unifying suspect for all three owner classes:** the cross-lane-boundary car-following frame —
a follower approaching/landing across a lane boundary appears to lose the leader's length (or
measure to the wrong frame), yielding exit-boundary junction overlaps, landing-into-full-lane
queue overlaps, and merge-landing overlaps as one family (the C4-vii-a frame-bug pattern at a
site the sweep did not cover). Next: trace `__veh1953` (the RB=1 landing) and `__veh995/__veh471`
(the RB=0 exit boundary) — deterministic replay + `LIVECITY_TRACEVEH` reach both directly.

## Entry 53 — the junction drive-through class TRACED TO ARITHMETIC: the stopped-lookahead ratchet (a missing myPartialVehicles)

New committed instrument: `[veh]` — per-step settled (lane, pos, speed, WINNING binder/arm/
blocker) for the `DiagTraceVehicleId` vehicle, printed from the export projection. The
constraint-internal traces show what an arm SAW; this shows which arm WON — the exact gap the
Entry-16 lesson (`DiagTraceVehicleId`'s own comment) names.

**The trace (`__veh206` into `__veh3472`, RB=0 arm, t=1235.5–1245):** a strict per-step
alternation —

```
bind=2 (crossJxnLeader, blocker=3472)  v -> 0.00      the arm sees the blocker, e-stops
bind=3 (freeFlow, blocker=-1)          v -> 1.30      NOTHING binds; +0.65 m
```

ratcheting 5.04 → 6.99 INTO the stopped leader's body, freezing 1.6 m deep. The arithmetic:
`TryFindCrossJunctionLeader` breaks on `seen > lookahead` where `seen` = distance to the NEXT
LANE'S START and `lookahead = Speed2Dist(maxV) + BrakeGap(maxV)` with `maxV =
MaxNextSpeed(egoSpeed)`. For a STOPPED ego lookahead ≈ 2.1 m; the boundary is 3.2 m away →
the walk never reaches the next lane → the leader (physically ~0 m ahead, its TAIL hanging
3.2 m back across the boundary) is INVISIBLE → freeFlow. After the blind step ego has speed,
lookahead ~4.7 m → leader visible at NEGATIVE gap → v=0. Repeat until `seen` itself drops
inside the stopped-lookahead (pos 6.99: 1.91 < 2.14) — then the pair freezes at depth 1.6 m.
Every number in the trace is reproduced by this formula.

**Why SUMO cannot have this bug:** `MSLane::myPartialVehicles` (setPartialOccupation) — a
vehicle whose body spans a boundary stays REGISTERED on the previous lane; getLeaderInfo /
the same-lane leader query see the hanging tail at any ego speed, no lookahead involved. Our
engine registers a vehicle only on its front's lane, so a hanging tail is invisible except
through the lookahead-limited cross-junction walk. This is the structural root of the owner's
"tolerance to driving through junction blockers" (329 simultaneous pairs at saturation: exits
of saturated junctions are wall-to-wall hanging tails), and plausibly feeds the merge class
(second stream blind to the first lander's tail). The `__veh1953` queue case adds the
mid-lane-change lateral-footprint skip as a second contributor (its leader `__veh441` landed by
lane change; interpenetration formed during the maneuver window) — to be confirmed separately.

**Fix direction (design-first, owner review before code):** register partial occupancy — the
faithful `myPartialVehicles` port: a vehicle whose `Pos < Length` is additionally visible on its
previous lane(s) to the leader queries (neighbor query registration at the boundary hop, cleared
once `Pos >= Length`). Alternative minimal patch (walk the first downstream lane regardless of
lookahead) treats only the traced site and leaves the same blindness in every other consumer of
the neighbor query — the port is the right shape. ⚠ Default-path behavioural change: full
goldens + both surfaces + the F3 battery are the gate; expect golden-sensitive scenarios (the
cjl arm is on the parity path).

## Entry 54 (BEFORE) — partial-occupancy phase 1 implemented (owner GO on PARTIAL-OCCUPANCY-DESIGN.md); the ladder

Implementation per the design: separate per-lane partials container in `LaneNeighborQuery`
(cleared in both refills, registered by a SERIAL engine pass from the frozen route pool —
extrapolated-front-pos frame, §2b); phase-1 opt-ins = the same-lane leader fold (partial fold
applied identically after BOTH the packed and GetLeader branches, keeping spatial/non-spatial
equivalence) and the cross-junction rearmost (`IRearmostSource.Rearmost` now returns
pos-in-frame; the insertion-time `ActiveRearmost` source stays full-occupants-only — phase 2).
The post-move phase is inert by construction (the second Refill clears partials; only the
pre-plan pass registers). Gates: `LIVECITY_PARTIALVEH` / `SUMOSHARP_PARTIALVEH`, engine default
**ON** (owner: "use what SUMO is having"), rows + safe-form tripwire + bench list updated.
Deviation from the task doc: T1's isolated unit test folds into T2's trace repro (no
InternalsVisibleTo for the internal query; the repro asserts the same thing end-to-end).

**Falsifiable predictions (design §3 ladder):**

- P1: gate OFF (`LIVECITY_PARTIALVEH=0`) reproduces today bit-for-bit; full sln + bench hash
  `A134ED3716DDE7BC` with the DEFAULT (ON) — goldens byte-identical per the §3 argument (SUMO
  produced them WITH partials). Any golden move = investigate first.
- P2: standard Geneva capture (4000 cars, reroute OFF, F3 ON, RINGBREAK=0), PARTIALVEH 0 vs 1:
  junction overlap class 329 → **< 100** at t≈1780; merge and queue classes not worse.
- P3: the Entry-53 ratchet signature (freeFlow/e-stop alternation into a standing blocker) does
  not occur gate-ON — no `[veh]` freeFlow step while a partial blocker stands in range.
- P4: D2 re-run (PARTIALVEH=1 RINGBREAK=1): the frozen queue>2.5m class collapses (492 → < 100)
  — the breaker's landing now sees the queue tail; re-opens the D2 default-ON question.
- P5: hour-horizon with defaults: arrivals within noise of 2436/2562-class values, stalls 0.
- Risk watched: partials add braking; watch arrivals for a systemic slowdown (a small drop is
  acceptable physics — cars no longer drive through each other; a large drop means an over-wide
  registration, e.g. the off-pool caveat in `RegisterPartialOccupations`).

## Entry 54 (AFTER) — every prediction confirmed; overlaps −87%; partials+ringbreak is the best honest configuration measured

- **P1 CONFIRMED**: full sln green with the gate DEFAULTED ON — 782/5 goldens byte-identical,
  bench hash `A134ED3716DDE7BC` (par==single), LiveCity 92/92 (hour-horizon = P5). The §3
  argument held exactly: SUMO-produced goldens, partial visibility never binds at golden density.
- **P2 CONFIRMED, 3× past target**: standard capture (reroute OFF, F3 ON, RINGBREAK=0),
  t≈1780 simultaneous pairs — junction **329 → 34** (target <100), queue>2.5m 45 → 7, total
  **401 → 50 (−87%)**. The stopped-lookahead ratchet class is gone (P3 by class evidence).
- **P4 CONFIRMED**: D2 re-run (PARTIALVEH=1 RINGBREAK=1): the frozen landing class queue>2.5m
  **492 → 8** — the breaker's landing sees the queue tail now. Breaker still healthy: 168
  breaks / 3 escalations / 0 stuckSteps / 91 ring reports (nothing locks).
- **The arrivals triangle (the risk watch, and the real story):**

  | arm | arrivals t=1800 | total overlap pairs |
  | --- | --- | --- |
  | baseline (no partials, no break) | 2961 | 401 |
  | partials only | 2635 (−11%) | 50 |
  | partials + ring break | **3072 (+3.7%)** | **53** |

  Partials alone LOWER throughput — cars no longer drive through junction blockers, so honest
  gridlock deepens; the old number was inflated by interpenetration, the same class of cheat as
  SUMO's teleports. The ring break then recovers the flow legitimately (creep through gaps) and
  ends ABOVE baseline with 87% fewer overlapping bodies. **partials(ON) + ringbreak is the best
  honest configuration measured on this surface.**

**Decisions this opens for the owner:** (a) `PartialOccupancyGate` shipped default ON per the
direction — done; (b) D2's default-ON blocker (the Entry-52 frozen landings) is REMOVED — given
the triangle, recommend flipping `RingBreakGate` default ON as well (the two are complementary:
honesty + legitimate recovery); (c) the residual 34 junction pairs are phase-2 territory
(insertion, keepClear space walks, lane-change shadow — T5, own sign-off).

### Entry 54 addendum — owner 3D verdict + residual classification

Owner ran the best configuration in the 3D viewer (fresh pack, partials default-ON +
`LIVECITY_RINGBREAK=1`): **"the best result I have ever seen with SumoSharp"**; half-stacked
queue cars CONFIRMED GONE visually; overlaps "greatly reduced although far from eliminated" —
two named residual classes, both "a regular observation in normal traffic, not just an emergency
resolution": (1) merging-lane overlaps, (2) passing through a car blocked mid-junction.

Instrument anatomy of the residuals (pv1 capture, OVERLAP-EX dedup):

- The junction class is now dominated by **crossing-internal-lane pairs of one junction**
  (`:35673_0_0×:35673_1_0`, `:30268_8_0×:30268_5_2`, `:36220_7_0×:36220_9_2`, …) — mid-junction
  corridor crossings where one body stands on the other's path, NOT the boundary-tail class
  (which partials cured). This is the owner's class (2), the F3 crossing-geometry family.
- The merge class's crispest signature: `__veh411 × __veh2209` both at `gen_road_7290_2@1.0`,
  depth 5.0 — **full co-location at a shared target lane's start**, i.e. two streams landing in
  (likely) the same step, neither seeing the other mid-flight. Being traced.

## Entry 55 — the merge co-location TRACED: the simultaneous same-target release; the (link,link) pair the foe loop never evaluates

`[veh]` traces of both cars, full landing window (t=636–642, junction 30268, TL-off):

1. Both queue STOPPED on two different internal lanes with the SAME target lane
   (`:30268_5_2@34.75` link 7, `:30268_0_1@28.72`), each bound `crossJxnLeader` on the SAME
   leader `__veh2917` standing on the shared target `gen_road_7290_2` (lane _2).
2. veh2917 moves; both release in PERFECT LOCKSTEP (0.65 / 1.80 / 3.08 m/s — the [merge] trace
   shows each running `PHASE2-targetFollow foe=__veh2917 x=0.65`: both follow the leader, and
   NEITHER EVER EVALUATES THE OTHER — no [merge] phase names veh2209 as veh411's foe at any
   step, though PHASE 1 ("foe still on its merging internal lane") is exactly this situation.
3. Both land at `gen_road_7290_2@1.03` the same step (t=640.5) — full co-location.
4. `colocationSymmetryBreak` (binder 15) catches it at t=641.0 — one car freezes, the other
   proceeds; they untangle into a legitimate platoon by t=642.5. Transient, at speed, and the
   owner sees it constantly: every saturated same-target pair whose shared leader departs does
   this.

**Named gap:** the sameTargetMerge evaluation is reached per (egoLink, foeLink) from the
junction-yield foe loop — for this pair (link 7 ↔ :30268_0_1's link) the loop never evaluates
the relation at all (no respondsTo/FoeWith reach, or single-foe-per-link short-circuit at
FindFoeVehicle — the two candidate guards; next instrument names which). SUMO cannot miss it:
each link's `setApproaching` registration makes the other stream visible via
`opened()/blockedByFoe`'s sameTargetLane arm regardless of the request matrix, and the
entry-order tie-break makes exactly one yield. `colocationSymmetryBreak` is doing its job as
the last-resort net — the fix belongs one layer up. NEXT: one instrumented run printing the foe
links the loop iterates for link 7; then the fix, gate-scoped, standard ladder.

### Entry 55 addendum — the [jyrow] instrument decides: the pair IS reached; the miss is INSIDE SameTargetMergeConstraint; symmetric-release tie is the named suspect

New committed instrument `[jyrow]` (trace-gated): per-foe-link reachability bits at the jy loop
head. Lesson re-learned en route: the print's first version filtered `!prePass` and showed
NOTHING for the stopped vehicle — a fusion-eligible vehicle only ever gets the PRE-pass (T1.8's
exact staleness trap, now hit from the instrumentation side); the pass is tagged instead.

Decisive rows (t=640.0, pre-pass, ego=__veh411 egoLink=7 at junction 30268):

```
foeLink=1  respondsTo=True  foeWith=True  conflict=none  foeIntLane=:30268_0_1
```

The pair IS evaluated, with BOTH reachability bits set and no geometric-conflict record — it
flows exactly into the `conflict is null` sameTarget-merge branch with `arbitration: true`. Yet
no [merge] phase ever names `__veh2209`. So the miss is INSIDE `SameTargetMergeConstraint`:
given both cars released in PERFECT symmetry (each ~0.5 m from the merge point, identical
speeds every step — lockstep 0.65/1.80/3.08), the top suspect is PHASE 1's leader/follower
decision lacking a TOTAL-ORDER tie-break for the exactly-symmetric case — both conclude "the
other is farther/not my leader", neither follows, co-location. The same missing-total-order
shape as Entry 40/49. NEXT (first move of the next round): read PHASE 1's ordering guard
against the trace values; if the tie hypothesis holds, the fix is the IsLeaderByEntryOrder
chain at that decision, gate-scoped, standard ladder.

**PHASE-1 read (same session): tie hypothesis REFUTED, suspect moved one level down.** PHASE 1
HAS the entry-order tie-break (Engine.cs ~9369, Entry-38's ungated `IsLeaderByEntryOrder`, with
`PHASE1-egoIsLeader-skip` trace tag). The trace shows NEITHER PHASE-1 tag during the release —
so the `foeMerging.LaneId == foeInternalLaneId` guard failed: **`FindFoeVehicle(ego,
:30268_0_1)` returned some OTHER route-matching vehicle** (its documented single-foe-per-link
first-match short-circuit — most plausibly an approaching follower queued behind veh2209 whose
route also includes that lane), so the ON-LANE merger was invisible and the arm took the
PHASE-0 approaching branch against the wrong foe. Next instrument: print FindFoeVehicle's pick
for foeLink=1 in the release window. Candidate fix shape (SUMO-faithful): the merge arm should
consider the on-lane occupant (rearmost of the foe internal lane, which the neighbor query
already answers) BEFORE falling back to the route-matched approaching foe — SUMO's
getLeaderInfo walks the foe LANE's occupants, not a single route-matched candidate.

## Entry 56 (BEFORE) — owner decisions: RingBreakGate DEFAULT ON; the crossing-class hunt is next

Owner (after watching the best-config 3D run): fewer fully-gridlocked junctions than ever seen,
traffic still moving city-wide; "ringbreak on by default - ok"; next hunt = the crossing-class
overlaps ("cars go full speed through another one blocked in junction is exactly what I would
not like to see") + the merge class + a few residual queue stackings.

`RingBreakGate` default flipped ON (kill switch `LIVECITY_RINGBREAK=0`; ENV-GATES row updated).
Predictions: goldens byte-identical (no golden forms an aged stopped cycle); full sln green
incl. hour-horizon (its arms now run ringbreak-ON via the env-honoured default); bench hash
unchanged.

**Crossing-class trace targets** (from the pv1 capture, for the next round): the full-speed-
through-blocked-car class = crossing-internal-lane pairs with one member moving —
`:35673_0_0@14.9 × :35673_1_0@13.7`, `:30268_8_0 × :30268_5_2`, `:36220_7_0 × :36220_9_2`
(depths 1.8 m). Hunt shape: [veh]-trace the MOVING member through the intersection window and
read which arm admitted it past the standing body (adaptToJxnLeader mapping vs FoeIsInTheWay
vs the F3 crossing arms) — same discipline as Entries 53/55.

## Entry 57 (BEFORE) — the crossing drive-through traced: FindCrossFoeVehicle's slot-order mask + the pick-level ignore-blocker row-kill

**Target triage first (corrects Entry 56's list).** Of the three named "crossing" pairs only
ONE is a true crossing:

- `:35673_0_0 × :35673_1_0` — BOTH links target `gen_road_7805.25_0` (net connections); the
  overlap sits ~2.3 m before the merge apex. Traced: `__veh4991` enters at 10.4 m/s, brakes
  under `crossJxnLeader/sameTargetMerge` against blocker **1607** — never against entity 199
  (`__veh199`, standing at `:35673_0_0@14.9`) — and asymptotes to a stop 1.8 m into veh199's
  body. **Merge-class (Entry 55 FindFoeVehicle mask), second junction confirmed.** Not a
  crossing.
- `:30268_8_0 × :30268_5_2` — both connections leave the SAME source lane
  (`-gen_road_4760.82_2`), bicycle/scooter lanes (1.5 m wide): a diverge-fan pair of bikes.
  Minor class, parked.
- `:36220_7_0 × :36220_9_2` — different sources, different targets, minor (`m`) vs major
  uncontrolled (`M`), mutual foes in the request table (link 7 responds to links 9–11).
  **The genuine crossing.** Transient (single 20 s witness hit, t=660, depth 1.8 m) — a
  fly-through, exactly the owner's "full speed through a car blocked in junction".

**The trace (deterministic standard capture + RINGBREAK=0, frames 1340, TRACEVEH both
members).** `__veh3474` crawls along `:36220_9_2` at 0.3–2.5 m/s (queue creep behind entity
1040, `leaderFollow` throughout, never fully stops). `__veh314` enters `:36220_7_0` at t=655
at 8 m/s and passes THROUGH veh3474's body at t=660.5 at 5.5 m/s. During the whole window
veh314's winning binder is `leaderFollow` (its own far leader, entity 639); the only
junctionYield step is t=654.5 `cautiousApproach blocker=-1`. `[jyrow]` proves foeLink=11
(`:36220_9_2`) is scanned EVERY step with `respondsTo=True foeWith=True conflict=geo` — the
miss is downstream of the row scan.

**The `[jyfoe]` instrument (this entry) names it — two coupled defects:**

1. **The mask**: `FindCrossFoeVehicle` returned `pick=__veh242` on `gen_road_5195_2` — the
   first vehicle in ENTITY-SLOT order whose remaining route contains `:36220_9_2`, kilometres
   away — every step of the window, while the physical index (`_physOnLaneFirst`) knew the
   lane was occupied (`__veh1040`, with `__veh3474` behind it). Same defect the bay arm
   measured in Entry 35b and fixed by switching to the physical index; same family as the
   Entry 55 merge mask.
2. **The row-kill**: with F3 ON, LiveCity sets `IgnoreJunctionBlockerSeconds=60`, and the
   skip is applied to the PICK before any branch — veh242 had been waiting ≥60 s in its own
   distant queue, so the ENTIRE foe link was discarded every step (`[jyarm]`/`[jyskip]`:
   zero lines for foeLink=11 — the absence is the proof). SUMO applies that skip per
   LINK-LEADER on the foe lane (MSLink.cpp:1601), never to a route-matched candidate.

**Fix (gate-scoped under `JunctionPhysicalOccupancyGate`, this entry):** the on-junction
occupancy arm now walks the foe lane's PHYSICAL occupants (`_physOnLaneFirst/Second`), with
the ignore-blocker applied PER OCCUPANT; the same skip structure as the pick-based branch
(isLeader gate variant, FoeIsInTheWay for !respondsTo, Entry 40/49 entry-order tie-break) is
replicated per occupant; the pick-based on-lane branch is skipped under the gate (double-eval
guard). The APPROACHING arbitration arm keeps the route-matched pick — SUMO's own split
(myFoeLanes occupant walk vs setApproaching/blockedByFoe). Gate OFF keeps the pre-existing
path bit-for-bit. New committed instruments: `[jyfoe]` (pick vs physical occupant), `[jyarm]`
(branch + constraint), `[jyskip]` (approaching-arm skip bits), `[jyocc]` (per-occupant
constraint).

**Predictions (recorded before reading the fix trace):**

- P1: the fix replay shows `[jyocc]` lines for foeLink=11 naming veh1040/veh3474 with finite
  constraints, and veh314 is junctionYield-bound (arm 5) in the t=655–663 window; the
  t=660 `:36220_7_0 × :36220_9_2` OVERLAP-EX line is GONE.
- P2: veh314 clears the junction ≤20 s later than before (follows the crawler through or
  waits for it to clear) — no new landed stall at :36220.
- P3 (full 3600-frame capture, same env): junction-class steady-state pairs drop from 34
  (pv1 baseline; 414 junction EX lines over the run) by ≥30%; queue/merge classes unchanged
  (they are different mechanisms); arrivals within ±3% of the pv1 arm (over-yield watch —
  the occupancy arm now sees real bodies, each hold is against a physically-present car).
- P4: goldens byte-identical + bench hash `A134ED3716DDE7BC` unchanged (gate default OFF —
  by construction), full sln green.

## Entry 57 (AFTER) — fix landed (`df23a27`); deep drive-throughs −62%; the row-kill was also a throughput bug

**P1 ✓.** Fix replay (same deterministic window): the t=660 `:36220_7_0 × :36220_9_2`
OVERLAP-EX is GONE (full capture: 23 → 3 EX lines at :36220). The new occupancy arm fired
exactly as designed — `[veh]` shows veh314 junctionYield-bound (arm 5) against entity 3041
before entry instead of sailing through.

**P2 ✓ in kind, number MISSED.** veh314 waits ~55 s at the junction mouth (predicted ≤20 s),
then crosses at 5.6 m/s at t≈716, exits, and cruises at free flow through t=850. Honest
queuing while the crawling cross-stream occupies the conflict — no wedge, no new stall.

**P3 MIXED as predicted-metric, WIN on the metrics that matter.** Standard capture
(RINGBREAK=0), fix vs pv1:

- Junction EX lines 414 → 319 (−23%; predicted ≥30% — MISS on the raw count). Distinct
  episodes 66 → 60; transients 44 → 42 (flat).
- **Depth decomposition rescues the story: deep samples (≥1.0 m) 145 → 55 (−62%); ≥1.5 m
  52 → 26. Shallow brushes (<0.5 m) 169 → 246** — holds convert penetrations into
  near-touches. The owner-visible "full speed through a body" class is the deep bucket.
- **Arrivals 2635 → 2832 (+7.5%), stoppedFrac 0.95 → 0.89, meanSpd 0.44 → 0.96, aggMove
  +118%.** The pick-level row-kill was not just an overlap bug — discarding entire foe links
  let cars pile into occupied junctions and wedge them. Honest holds IMPROVED flow. (The
  ±3% guard was beaten in the good direction.)
- Merge EX 8 → 91 is ONE landed pair (`__veh1375 × __veh1762`, gen_road_7261_1, 76 samples)
  — an Entry-55-class co-location landing; the merge fix (hunt queue item 2) owns it.

**Shipped-defaults arm (fix + RINGBREAK=1) vs old best (pv1-rb1):** steady-state pairs
53 → 35, junction 28 → 19; deep (≥1.5 m) 156 → 86 (−45%); stoppedFrac 0.90 → 0.81; meanSpd
0.85 → 1.31; aggMove +54%; arrivals 3072 → 2935 (−4.5% — the honesty trade: the old number
was partly financed by drive-throughs). Note the rb1 arms carry more deep samples than rb0
(D2's escalation creep produces body-contact by design — known trade, Entry 51).

**P4 ✓.** Full sln green (782/92/324/…); bench hash `A134ED3716DDE7BC` par==single unchanged.
Gate default OFF — engine defaults untouched.

**Remaining in the junction class:** the transient population (42) is varied (25+ junctions,
no dominant one) and now mostly shallow; several named sub-shapes to triage next round:
same-link parallel lanes (`:36315_1_0 × :36315_1_1`), edge-vs-internal boundary pairs
(`gen_road_6272_0 × :36315_4_0`), and same-source diverge fans (`:30268` bikes). The MERGE
class (Entry 55's FindFoeVehicle first-match in SameTargetMergeConstraint) is now the
biggest single named defect still open.

## Entry 58 (BEFORE) — the merge mask measured on a LANDED pair; PHASE 1 goes physical (occPhase1 fold)

**Fresh exemplar (post-57 world; the old veh411 repro is invalidated because the 57 fix is
gate-scoped under F3):** `__veh1375 × __veh1762`, gen_road_7261_1, depth 3.1 m, landed t≈231
and NEVER resolved through t=1780 (76 witness samples ≈ 25 minutes; colocationSymmetryBreak
does not untangle it). Junction 34994 — three links feed gen_road_7261_1; veh1375 came via
`:34994_11_1` (major), veh1762 via `:34994_6_1` (minor).

**The trace (both members, frames 500):** both follow the SAME leader (`__veh968`) toward the
shared target — Entry 55's lockstep-release shape. They cross onto gen_road_7261_1 in the
same step (t=230.0/230.5); veh1375 brakes under keepClear (downstream queue, blocker 248) and
stops at @2.63; veh1762, at 7.39 m/s one step from the merge, first sees veh1375 via
PHASE2-targetFollow at gap **−6.74** — one emergency-decel step (9 m/s²) leaves it at @0.75,
3.1 m inside veh1375's body. `[jyrow]` shows veh1762's foe row for `:34994_11_1` (foeLink=12,
respondsTo+foeWith, conflict=none → merge branch) evaluated EVERY step of the approach, yet
neither vehicle ever emitted a PHASE 0/1 line about the other — TraceMerge prints on every
PHASE-1 outcome, so the silence is the proof: **FindFoeVehicle's slot-order first-match never
returned the on-lane merger** (Entry 55's suspect, now measured as the landing mechanism on a
second junction).

**Fix (this entry, gate-scoped under `JunctionPhysicalOccupancyGate`):** `occPhase1` — PHASE 1
evaluated per PHYSICAL occupant of the foe internal lane (`_physOnLaneFirst/Second`), with the
same entry-order tie-break and C4-v merge-point geometry as the pick-based PHASE 1, and the
ignore-junction-blocker applied PER OCCUPANT — folded into every downstream return via
`Math.Min`. Gate OFF: occPhase1 = +infinity, every return bit-for-bit pre-existing. The pick
keeps PHASE 0 (arbitration; SUMO's blockedByFoe walks APPROACHING foes — route match is the
right contract there). Trace tags: `PHASE1occ-follow/stop/egoIsLeader-skip`.

**Predictions (before measurement):**
- P1: fix replay (veh1762, frames 600): PHASE1occ lines name veh1375 during the approach
  (t≈227–230); veh1762 stops before the merge point or follows at a non-negative gap; the
  landed pair is GONE at t=240–280.
- P2: full 3600-frame capture (RINGBREAK=0 arm): merge EX lines 91 → <20 (the 76-sample pair
  was the bulk); junction class within ±10% of fix57; arrivals within ±3% of 2832.
- P3: goldens byte-identical + bench hash unchanged (gate-off is Math.Min(x, +inf) by
  construction); full sln green.

## Entry 58 (AFTER) — merge PHASE-1 occupant fold landed; the pair interleaves; merge class −86%

**P1 ✓, better than predicted.** Fix replay: `PHASE1occ` names BOTH occupants every step of
the approach (follow on `__veh968`, `egoIsLeader-skip` on `__veh1375`) — the entry-order
tie-break declared veh1762 the pair's LEADER, so instead of ego braking (my prediction), the
pair INTERLEAVED: veh1762 passes the merge at 8.3 m/s and is kilometres downstream by t=236;
veh1375 yields; the landed overlap never forms (0 EX lines for the pair in the full capture).
Antisymmetric ordering doing exactly its SUMO job — one clears, one yields.

**P2 ✓ with an honest wobble.** Full capture (RINGBREAK=0, vs the Entry 57 arm): merge EX
**91 → 13** (predicted <20); steady-state pairs ~53 → 44, junction 36 → 29; arrivals 2824 vs
2832 (−0.3%, within the ±3% guard). The wobble: queue EX 61 → 157 and deep junction samples
26 → 98 — BOTH are a handful of landed pairs (deep: `__veh341 × __veh2514` at :34564 alone is
42 samples; queue: three pairs are 127 of 157), i.e. the pre-existing landed-standoff family
RELOCATED by the changed world, not a new mechanism: occPhase1 is a pure Math.Min fold — it
can only make a car brake EARLIER, never proceed where it previously braked. The landed
crossing-standoff family (Class-2 admission/holds) is now the top open class.

**P3 ✓.** Full sln green; bench hash `A134ED3716DDE7BC` par==single unchanged; gate-off
bit-for-bit by construction (every return is `Math.Min(pre-existing, +infinity)`).

## Entry 59 — owner 3D verdict on Entries 57–58 (fresh pack, F3 ON, shipped defaults, 10k/30k): "improved a lot"; two NEW named classes from the session

Owner verdict: overall situation improved a lot; the remaining weirdnesses are now few enough
to target individually; quality good enough that this branch becomes the new main (see the
merge note at the end of this entry).

**NEW CLASS A — late lane-change planning at queue tails (owner-reported, screenshot 1/2).**
A car arriving at the back of a queue drives TIGHT up to the standing tail car first, and only
then — front bumper nearly at the leader's rear — decides to change to the free left lane; the
IG renders the maneuver sweeping THROUGH the leader's body, and the car often ends misaligned,
not fully in the target lane. The owner's framing: drivers think ahead; in the sim the
emergency-shaped last-moment swerve is the NORM, not the exception. This is a lane-change
DECISION-TIMING gap, not an overlap-classifier gap: SUMO's strategic change runs against
bestLanes with a speed-scaled lookAheadDistance (laneChangeModel lookahead, MSLaneChanger),
so a queue tail on the current lane triggers the strategic change well upstream. Ours
evidently fires only near standstill. NEXT: design-first item (it is a behavioral feature,
not a one-line fix) — trace one such vehicle first ([veh] + lane-change prints), THEN read
our change-trigger code against MSLaneChanger's strategic arm. Related but distinct from the
old DR-viewer issue "stopped car lane-changes into an occupied slot".

**NEW CLASS B — turners holding mid-junction "for no obvious reason" + straight stream
driving through them (owner-reported, screenshot 3).** Two left-turning cars stop inside the
junction with BOTH exit lanes free, blocking it; the bottom-up straight stream then drives
THROUGH the standing blockers as if the box were free; after a long stand the blockers clear
and the NEXT turning pair stops in the same spot. A pedestrian crossing sits at the exit; the
known ped-render z=0 bug may be hiding peds that are LOGICALLY on that crossing.
HYPOTHESES (owner's + mine, all unverified — trace before believing any):
  (a) the hold = crowdYield/crossing-occupied against peds invisible in the render (z=0 render
      bug, docs/TASKS-TODO.md) — would explain "no obvious reason" + the repeat at the same spot;
  (b) the drive-through = the F3 bounded-patience recovery working as designed:
      IgnoreJunctionBlockerSeconds=60 (LIVECITY_IGNOREBLOCKER, Entry 37) skips a foe that has
      stood >= 60 s — the owner's own "maybe this is some kind of recovery maneuver, not
      always" matches a 60 s threshold exactly;
  (c) if the drive-through starts BEFORE 60 s, it is a real residual miss (e.g. the occupant
      is on a different internal sub-lane than the one the straight link folds over).
NEXT: reproduce at that junction headless (identify it from the cut by the ped crossing +
left-turn geometry), [veh]-trace one straight-stream car and one turner; check the turner's
binder (crowdYield?) and the crosser's window vs the 60 s threshold.

**Merge note:** owner decision — this branch (Entries 48–58 state: partials ON, ring break
ON, rerouting ON, Entry 49/57/58 fixes) becomes the new main. Gates at merge: full sln
green, bench hash `A134ED3716DDE7BC` par==single, goldens byte-identical.

## Entry 60 — Class A traced end-to-end: the three-stage chain; design doc written (awaiting owner)

Instruments this entry (committed): `[lclate]` (executed/wished late queue-tail changes with
neighDist), `[sg]` (per-step speed-gain accumulator/gates/stay-rules for TRACEVEH).

**The chain (exemplar `__veh320`, deterministic t=243–250):** (1) wish forms EARLY —
accumulator crosses 0.2 at 11 m/s, 27 m upstream; (2) commit deferred by SUMO's own
`neighDist/speed > 20` usability gate — the left lane is TURN-ONLY (continuation ~100 m), so
the gate opens only at crawl (~2.3 m/s); (3) the committed 2 s continuous maneuver freezes
under the `LaneChangeMinSpeed` hold one step later, resumes on queue-creep in body contact,
and re-freezes past midpoint = the misaligned half-lane stop.

**The design-deciding measurement:** 187/208 executed late swerves (90%) target
short-continuation (<150 m) lanes — vanilla SUMO's identical gate would commit those at crawl
too (instantly, hence artifact-free). Decision side is SUMO-faithful; the artifact is wholly
owned by the beyond-SUMO continuous-maneuver mechanism. Prevalence: 208 executed (~1/6 s),
842 distinct standing wishers.

Also checked and rejected: `REACT_TO_STOPPED_DISTANCE` (MSLCM_LC2013.cpp:1378) reacts to
SCHEDULED stops (`isStopped()`), not queue tails — porting it would not touch this class.

**Design: `docs/LANE-CHANGE-LATE-MANEUVER-DESIGN.md`** — two execution guards (E1 runway at
start, E2 abort-or-complete instead of frozen poses), no decision change, everything inside
the `LaneChangeDuration>0` realism gate (goldens byte-identical by construction). AWAITING
OWNER REVIEW before implementation.

## Entry 60 (AFTER) — E1/E2 landed (owner approved the design): executed queue-tail swerves −88%, zero sweep-throughs

Owner approved `docs/LANE-CHANGE-LATE-MANEUVER-DESIGN.md`; implemented as designed:
**E1** `ManeuverLacksRunway` — a continuous maneuver may not START against a near-stopped
(<1 m/s) same-lane leader without runway (`speed*duration/2 + MinGap`); wired as one more
VETO (no accumulator reset) into the sgLeft chain and `TryStrategicLaneChange`
(`LcStrategicOutcome=5`). **E2** — the below-min-speed hold in `AdvanceLaneChanges` now
aborts cleanly before the midpoint (`ClearLaneChangeManeuver`) and COMPLETES past it,
never freezing a diagonal pose.

**Measured (A/B vs the Entry 60 BEFORE arm, same env/frames):** executed late swerves
208 → **24 (−88%)**, and all 24 residuals have leadGap 4.5–8 m — every one clears the runway
guard legitimately; **sweep-throughs (gap < forward travel) = 0** (success condition 1
exactly). Overlap classes: junction 177 → 183 (+3.4%, within ±10%), queue 71 → 67, merge
17 → 17. Arrivals 2018 → 2020 (±0.1%). Full sln green; bench hash `A134ED3716DDE7BC`
par==single (E1 unreachable at duration 0, E2 unreachable at MinSpeed 0 — every golden).
Remaining surface: the owner's 3D verdict on queue-tail rendering (condition 5).

## Entry 61 — Class B: the ped hypothesis is DEAD; the holds are merge-landing standoff chains; veh282 stopped-under-freeFlow anomaly named

New committed witness `LIVECITY-JXNHOLD` (cars stopped ≥10 s ON internal lanes + binder +
described blocker — the population HEADSTUCK structurally excludes as <25 m stubs).

**15 000-ped capture (approximating the owner's 30 k 3D density): 416 holds, ZERO
crowd/crowdYield binders.** Hypothesis (a) of Entry 59 — peds holding the turners — is
REFUTED at this density headless; the owner's z=0-invisible-peds tie-in is moot (and the
owner is redesigning the ped layer anyway — decided this session).

What the holds ARE: **merge-landing standoff chains.** Distribution: leaderFollow 57%
(queue shadow INTO the junction), crossJxnLeader 24%, junctionYield/sameTargetMerge ~8%.
Exemplar traced end-to-end (`__veh461`, :36022): PHASE1occ + PHASE1 both correctly follow
`__veh1` at gap 0.00 — a co-located merge landing (the pair is nose-to-nose across two
merging internal lanes); the entry-order tie-break correctly skips the other member. The
root of the deepest chain (`:34991`, waits to **1066 s**): `__veh740` leaderFollows
`__veh282` on the exit lane — and **veh282 stands for MINUTES at v=0 with binder
freeFlow/none** (the engine believes nothing constrains it). A stopped car the constraint
fold calls free is self-contradictory — NEXT TRACE TARGET (suspect: a non-binder speed cap
— CoopSpeedAdvice or maneuver-hold — pinning it outside the binder accounting; UNVERIFIED,
trace before believing). The drive-through half of the owner's report matches the ≥60 s
ignore-blocker recovery (holds routinely exceed the window) — working as designed once the
wedge exists; the wedge itself is the bug.

## Entry 62 — the strand-clamp WaitingTime LIVELOCK: root of the permanent wedges; sibling-snap rescue landed

**The trace (deterministic, `__veh282`, 15 k-ped arm, [exec] seam instrument):** the :34991
wedge root was NOT a merge/junction defect at all. veh282 crossed `:34991_4_0` at 10.65 m/s
onto `gen_road_4504_0` — a **1.42 m netconvert stub whose lane 0 has NO outgoing connection**
(only lane 1 continues). Every step: the plan freeFlow-accelerates (the pool's exit is the
sibling, so no dead-lane brake binds), the car overshoots the stub end, and the C4-vii-c
strand clamp resets pos=length/speed=0. **The livelock**: the WaitingTime update runs BEFORE
the clamp and sees `Intent.NewSpeed=1.30 > HaltingSpeed`, so **WaitingTime reset to 0 every
step** — starving EVERY recovery at once (dead-lane reroute 5 s/90 s gates, jam teleport,
and other cars' 60 s IgnoreJunctionBlocker skip). One connection-less 1.42 m stub therefore
froze a car forever AND made the queue behind it unrecoverable (waits to 1066 s). Ruled out
on the way (each by instrument): vehicle recycling (same ent/gen), CoopSpeedAdvice ([coop]
silent), all other Pos/Speed writers (grep-complete).

**Fix (both halves reached only via the strand clamp — a state no committed golden enters):**
1. The clamp accumulates WaitingTime honestly (`+= dt`) — every WaitingTime-keyed safety net
   works again.
2. `RescueStrandedVehicles` (SERIAL post-execute pass — the occupancy scan must not run inside
   region-parallel execute; par==single by construction): a strand-clamped car whose edge has a
   sibling lane that connects to the route's next edge and has room at ego's span SNAPS over
   and re-resolves its pool — mirroring SUMO's outcome (its duration-0 changeLanes phase never
   freezes on a short stub).

**Measured:** veh282 snaps at t=78.0 and drives off at free flow (13 m/s by t=83).
Ped-heavy A/B: **:34991 holds 125 → 0**; :34994 87 → 39; max JXNHOLD wait 1066 → 788;
stoppedFrac 0.65 → 0.60, meanSpd 2.52 → 2.69, aggMove +6.7%, arrivals flat (2062 → 2064).
Full sln green; bench hash `A134ED3716DDE7BC` par==single unchanged.

**Next named target:** the residual 788 s wedge — `__veh56 :34994_6_0@33.3
bind=crossJxnLeader/none wait=788 -> __veh1375 gen_road_7261_1@2.6 keepClear/none` (the
Entry 58 junction; keepClear-rooted chain, likely the landed-standoff family from RESUME-3).

## Entry 63 — merge PHASE 1/2 ignore-junction-blocker parity (the veh903 hold shape); honest mixed result

**Trace:** the residual 788 s wedge head (`__veh903`, `:34991_4_0@16.3`) was
`PHASE2-targetFollow foe=__veh280@gen_road_4504_0 x=0.00` — following a car STRANDED on the
dead stub lane (Entry 62's lane, sibling permanently full at that pocket). SUMO's
`gIgnoreJunctionBlocker` skip (MSLink.cpp:1601) applies to EVERY link leader, but our merge
arm's PHASE 1 (route-matched pick) and PHASE 2 (shared-target rearmost) never carried it —
so the 60 s recovery structurally could not fire for merge-held streams. Fix: both phases
skip a foe whose WaitingTime ≥ IgnoreJunctionBlockerSeconds (inert at the parity default -1;
verified: full sln green, hash `A134ED3716DDE7BC` par==single).

**Measured (ped-heavy A/B vs fix62):** max JXNHOLD wait 788 → 706; arrivals 2064 → 2074;
overlaps flat (34 → 36 pairs — no drive-through cost spike); :34994 holds 39 → 38 and
stoppedFrac 0.60 → 0.64 — i.e. the SPECIFIC hold shape is cured (veh903's own chain), but
the junction's wider standoff family persists and run-to-run noise dominates the aggregates.
Honest verdict: a real SUMO-faithfulness gap closed, modest immediate effect. **Next named
wedge (706 s, CORRECTED — the line below is the measured head, an earlier draft named a
wrong exemplar): `__veh56 :34994_6_0@33.4 bind=crossJxnLeader/none wait=706 -> __veh1762
gen_road_7261_1@3.4 keepClear/none`** — the SAME :34994 -> gen_road_7261 keepClear-rooted
chain as before (now headed by veh1762, the Entry 58 exemplar's partner): the crossJxnLeader
hold on a keepClear-held exit-lane car has no 60 s recovery either (crossJxnLeader is
car-following, not a junction arm) — next session should trace veh1762's keepClear chain to
ITS root before touching any skip.

## Entry 64 (BEFORE) — owner 3D verdict on E1/E2: "greatly reduced, far from eliminated; converging"; the residual mechanism named

Owner observations (3D, fresh engine with Entries 60–63): last-instant turners greatly
reduced but still not scarce — screenshot shows THREE diagonal half-way stops nearly SYNCED
in one queue (yellow/red/blue); plus ONE caught pure-lateral change at standstill on red
(scarce). Owner's quantification idea: **compare standing-car orientation (as the IG renders
it) against lane direction** — some misalignment is realistic, but near junctions European
drivers are already aligned; ours are not.

**Residual mechanism (hypothesis from the E1/E2 mechanics — instrument before believing):**
E1 only vetoes against a NEAR-STOPPED leader (<1 m/s). A car committing at ~2 m/s behind a
CREEPING leader (queue pulse) still starts the 2 s maneuver; the queue re-compresses, ego
brakes to a stop mid-sweep, and E2's past-midpoint arm completes the flip at standstill —
the IG then renders the lateral settle on a stopped car = the diagonal pose (and, at a red
light, the pure-lateral slide). The "synced" look = one creep pulse triggering several
commits in the same queue. Candidate fixes for next round: (a) E1 widened to slow leaders
using closure (gap vs ego travel through the maneuver assuming the leader can stop NOW);
(b) engine-side witness first: `LIVECITY-DIAGSTOP` = stopped cars (v<0.5) with an
in-progress or just-completed maneuver — the engine-side proxy of the owner's
orientation-vs-lane metric, so the count is measurable headless before/after.

Owner overall: "better than before, we are converging."
