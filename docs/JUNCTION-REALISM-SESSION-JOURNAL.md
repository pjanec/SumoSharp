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
