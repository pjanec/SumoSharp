# SUMOPED — How to port it: staged development that stays faithful

**Status: PROPOSAL — the method, awaiting sign-off with the rest of the doc set.**

`SUMOPED-TASKS.md` says *what* to build in what order. This says *how to build it so each stage is
provably right before the next one starts*, and what to do when a stage is wrong.

Read `SUMOPED-ALGORITHM.md` first — you cannot port faithfully what you do not understand.

---

## 1. The problem this document exists to solve

**A partially-ported model cannot match a full-SUMO golden.** At the end of Stage 3 we will have
`walk()`, the obstacle fold and lane transitions, but no junctions, no vehicles and no traffic lights.
Run any real scenario and it diverges immediately — not because the port is wrong, but because most of
the model is absent. So the golden suite gives **no signal at all** until very late.

That is the trap. Three bad things follow from it if unaddressed:

1. You write thousands of lines with no feedback, then debug a 30,000-row trajectory mismatch at the end.
2. The first scenario to go green does so through the *combined* behaviour of everything, so a
   compensating pair of errors passes.
3. Unported branches silently fall through to *something plausible*, and a later golden passes for the
   wrong reason.

The answer is not "port faster". It is **four verification ladders running simultaneously**, three of
which give signal long before any golden can, plus a staging discipline that makes unported code fail
loudly instead of guessing.

---

## 2. The four ladders

| ladder | granularity | what it proves | available from |
| --- | --- | --- | --- |
| **L1 — function** | one function, hand-built inputs | the ordered fold is in SUMO's order and each constant is right | day one |
| **L2 — single-step replay** | one step, real golden state | our step function maps SUMO's state at `t` to SUMO's state at `t+1` | as soon as `walk()` exists (§3) |
| **L3 — scenario golden** | a whole run | the *accumulation* is right, and nothing outside the ported set is being silently faked | when a scenario's whole behaviour is inside the ported subset |
| **L4 — branch coverage** | the 148-branch inventory | the suite actually *exercises* what it claims | continuously |

They prove genuinely different things and none substitutes for another. **L2 is the load-bearing new
one** and the reason staged porting is feasible at all.

---

## 3. L2 — single-step replay, the technique that unlocks staging

> Reconstruct the **complete per-lane pedestrian population** at step `t` from a committed golden, run
> **exactly one step** of our ported code, and compare against the golden at `t+1`.

Why this changes everything:

- **It works on a partial port.** A step whose behaviour lies entirely inside the ported subset can be
  replayed and checked even though the surrounding scenario cannot run end-to-end.
- **It localises absolutely.** A trajectory test says "diverged at step 340". A replay test says "given
  *this exact* input state, our `Walk()` returned `xSpeed = 1.21` where SUMO returned `1.39`". No error
  accumulation, no bisection.
- **It is dense.** One 300 s saturated golden yields ~30,000 person-steps — 30,000 independent
  assertions from a single committed file, at no extra golden cost.

### 3.1 The reconstruction is complete — verified field by field

Single-step replay needs the *whole* `PState`, not just the observable part. Every field is
recoverable from the committed goldens:

| `PState` field | recovered from | status |
| --- | --- | --- |
| `myLane` / edge | FCD `edge` (carries internal ids `:c_c1`, `:c_w1`) | direct |
| `myRelX` | FCD `pos` | direct |
| `myRelY` | project `(x, y)` onto the lane centreline | derived; **self-checked** — the derived `pos` must agree with the FCD's own `pos` to 1e-6 (task SP-2.1c) |
| `mySpeed` | FCD `speed` | direct |
| `mySpeedLat` | invert FCD `angle`: `speed · tan(angle − laneBearing)` | derived; **verified** — recovered maxima land exactly on the model's two caps, `0.5556` (`vMax·0.4`) and `0.6401` (`stripeWidth`). `SUMOPED-COVERAGE.md` §2.1 |
| `myDir` | sign of `pos` progression | derived |
| `myWaitingTime` | consecutive steps at `speed < 0.1`, counted from insertion | derived; exact when replaying a ped's whole trajectory (§3.2) |
| `myWaitingToEnter` | true until the ped's first `speed ≥ 0.1` | derived |
| `myAmJammed` | **set**: the stderr line `Person 'X' is jammed on edge 'Y', time=Z`. **ongoing**: the exact `vMax/4` speed signature | derived; **measured** — 1983 samples at exactly `0.3472 = 1.3889/4` in the counterflow-jam golden, the third most common speed value in that run |
| `myNLI`, `myWalkingAreaPath` | recomputed from `(net, route, current lane)` | not observed — a deterministic function of committed inputs |

The `myAmJammed` recovery is worth dwelling on: it is the one field with real behavioural weight that
the FCD does not carry, and it turns out to be readable two independent ways. Cross-check them against
each other; a disagreement means the reconstruction is broken, and it is better to find that in the
harness than to chase it as a port bug.

### 3.2 Replay whole trajectories, not random steps

`myWaitingTime` and `myWaitingToEnter` are *histories*. Reconstructing them at a random step `t`
requires walking that pedestrian's samples from its insertion anyway, so the natural unit is **one
pedestrian's whole trajectory, replayed step by step, each step re-seeded from the golden**. This is
not the same as running the scenario: after each step we *discard* our computed state and re-load
SUMO's, so errors never accumulate and every step is an independent test.

### 3.3 What L2 does not prove

It validates the **step function**, never the **accumulation**. A step function correct to 1e-12 can
still drift over 300 steps if a rounding or ordering detail differs. Only L3 catches that. Neither
ladder is optional; SP-2.x builds both.

---

## 4. Unported code must fail loudly

The single most dangerous failure mode in a staged port is an unported branch that quietly does
something reasonable. A later golden then passes for the wrong reason, and the bug surfaces three
stages later with no obvious cause.

**Rule: every branch not yet ported throws `NotPortedInThisStageException(branchId)`**, naming the
`SUMOPED-BRANCH-INVENTORY.md` ID. Not a `TODO`, not a fallback, not a default value — a throw.

Two things follow, both of them good:

- **"Which scenarios can I run at stage N?" becomes mechanical**, not a judgment call. Run the suite;
  whatever throws is out of scope for this stage, and the exception names exactly which branch pulled
  it out. The set of runnable scenarios is *discovered*, not declared.
- **A scenario that starts passing at stage N+1 does so because branch X landed**, and the coverage
  counter (L4) says so.

The throws are removed branch by branch as the port advances; the last one disappears when the
inventory is fully covered. A `NotPortedInThisStage` still present at SP-7.5 is a release blocker.

---

## 5. The stage gate

A stage is closed when **all four** hold. Verified first-hand by the reviewer, per CLAUDE.md
§Subagents — never on an implementor's report:

1. **L1**: every function the stage introduces has unit tests with hand-built inputs, asserting exact
   post-state, each case citing the `.cpp` line whose behaviour it pins.
2. **L2**: single-step replay is green over **every** golden step that does not throw
   `NotPortedInThisStage`, and the count of replayed steps is **recorded in the tracker** and is
   **strictly greater than the previous stage's**. A stage that adds code without adding replayable
   steps has not been shown to do anything.
3. **L3**: every scenario the stage claims is green, and no previously-green scenario regressed.
4. **L4**: the branch counters the stage claims to have implemented are actually hit; the tracker
   records covered-vs-total out of 148.

Plus the standing rules (`SUMOPED-TASKS.md` §Standing rules): `.cpp:line` comments, no `System.Random`,
`Engine.Persons` null by default, and the **full** `dotnet test -c Release` re-run after any
`Sim.Core`/`Sim.Ingest` change.

### 5.1 The one metric that says whether a stage did anything

**Replayable step count** (gate 2). It rises monotonically as branches land, it cannot be gamed by
adding code that is never reached, and it gives a single number per stage:

```
stage 3 (straight sidewalk)  :  _____ / 30,000 person-steps replayable
stage 4 (junctions)          :  _____
stage 5 (vehicle coupling)   :  _____
stage 6 (traffic lights)     :  _____  -> must reach 100%
```

Filling that column *is* the progress report.

---

## 6. The divergence protocol — the ladder run downwards

When something fails, walk the ladders in the **reverse** direction. Each rung localises further, and
the rule is: **never debug at a higher rung than necessary.**

1. **L3 fails** (a golden diverges). Take the **first divergence step** `t` — never a later one;
   everything after is contaminated.
2. **Drop to L2 at step `t`.** Reconstruct the population at `t−1`, replay one step. If it diverges,
   the bug is in the step function and the replay has already narrowed it to one pedestrian on one lane
   with one obstacle array.
   If it does **not** diverge, the step function is right and the bug is in the *accumulation* —
   ordering, rounding, or a structural mutation applied at the wrong point. That is a completely
   different investigation, and knowing which one you are in is worth more than any amount of staring.
3. **Drop to L1.** Turn that exact input state into a unit test with a hand-built `Obstacle[]`. It is
   now a fast, permanent regression test regardless of the outcome — **commit it even if it passes**,
   because a case that was worth suspecting is worth pinning.
4. **Only then, trace SUMO.** Run the oracle on the same scenario, dump the same step, compare
   intermediate values. CLAUDE.md §Measurement discipline item 2 is emphatic here: *a mechanism
   hypothesis reasoned from the SUMO source has a bad track record in this repo — five reasoned
   interventions were inert before one trace found the cause in minutes.* Trace first, hypothesise
   after.

### 6.1 The rule about "fixing" a divergence

A divergence is **never** fixed by adjusting a constant, adding an epsilon, or widening a tolerance
until it passes. If our value differs from SUMO's, either we ported something wrong or we understood
something wrong — both are found by reading, not tuning.

The only legitimate outcomes are: (a) the port is corrected to match the source; (b) a genuine
structural deviation is identified, **named in the design, gated, and justified in writing** (CLAUDE.md
prime directive 4 — so far there is exactly one, the dawdling RNG stream, `SUMOPED-DESIGN.md` §12).

Tolerances in `tolerance.json` exist for floating-point representation, not for behavioural
disagreement. If a scenario needs a wider tolerance than its siblings, that is a finding to
investigate, not a knob to turn.

---

## 7. Why the stages are in this order

The ordering principle: **each stage introduces exactly one new source of divergence.** That is what
makes the divergence protocol cheap — when a stage's first golden fails, the newly added mechanism is
the prime suspect, and there is only one of it.

| stage | new divergence source | deliberately absent |
| --- | --- | --- |
| **S3** straight sidewalk | the stripe utility fold itself | junction geometry, the router, vehicles, TLs |
| **S4** junctions | `WalkingAreaPath` geometry + the junction-local router | **vehicles** — `walk-junction-turn` has none, so a divergence has exactly one possible cause |
| **S5** vehicle coupling | the phantom-leader injection and the ped↔vehicle obstacle exchange | TL state |
| **S6** traffic lights | link state, `ignoreRed`, `getImpatience` | — |

Two consequences worth stating:

- **S4 before S5, and S4's first scenario has no vehicles.** This is not fastidiousness: walkingarea
  geometry is the hardest single item in the port (`SUMOPED-DESIGN.md` §13 risk 1) and it is hard to
  unit-test in isolation. Landing it against a vehicle-free scenario is the only cheap way to debug it.
- **`Walk()` is unit-proven (L1) before the surrounding machinery exists** (SP-3.3 before SP-3.4). Its
  ordered folds are non-commutative and a subtly wrong order still "looks fine" in aggregate. It gets a
  hand-built-input suite *first*, not a scenario.

---

## 8. Faithfulness discipline

The mechanics that keep "faithful" from degrading into "close enough":

- **Every ported function carries a `/sumo/<path>:<line>` comment** naming its C++ original, so a
  reviewer can diff side by side. This is standing rule S-a and it is what makes review possible at all.
- **One constants table**, `StripingParams`, every entry with its `.cpp:line` and SUMO's exact value.
  `SUMOPED-ALGORITHM.md` §4 is the rationale for auditing it line by line — several of those constants
  move behaviour by >1000%.
- **Preserve calculation order, not just the formula.** The `walk()` folds interact; `moveInDirection`
  runs FORWARD fully then BACKWARD fully; peds sort by `dir·myRelX` with an **ordinal** id tie-break.
  These are behaviour, not implementation detail.
- **Port the branch, even where no golden witnesses it.** `SUMOPED-COVERAGE.md` §8 lists branches we
  cannot witness (`reserve-oncoming` on normal lanes, and others). Implement them faithfully and record
  them as unwitnessed — an unported branch is a latent divergence, and a wrong-but-unwitnessed branch
  will bite when the scenario set grows.
- **Look at it.** `scripts/render-ped-fcd.py` renders any run into the real Sim.Viz player. A parity
  number tells you *that* something diverged; the render tells you *what it looks like*, which is
  usually how you recognise the mechanism. SP-7.3's overlay draws our peds live against the golden's as
  ground-truth rings, in the same frame.

---

## 9. Delegation shape

Per CLAUDE.md §Subagents, and `SUMOPED-TASKS.md`'s B0–B10 batching:

- **Opus** owns decomposition, the accept/reject gate, and anything requiring a judgment call about
  SUMO's intent. Notably **SP-1.3 (the begin-of-timestep ordering trace) stays with Opus** — it is a
  measurement whose interpretation decides an engine-wide invariant.
- **Sonnet** implements batches: routine porting against a named `.cpp` file, running the loop,
  authoring scenarios.
- Every delegation names: the exact `/sumo/` source file, the target C# file, the scenario, the command
  to run, and the numeric done-condition. A subagent starts from near-zero context.
- **Commit before delegating.** Never hand a subagent a file with uncommitted edits.
- **End a delegation at "compiles, verified, committed".** Never delegate *waiting* for a long run —
  that has burned a whole agent budget three times in this repo's history.
- **Review by re-running, not by reading the report.** A task is closed when the reviewer has
  personally re-run its done-condition and read the test to confirm it asserts the real thing rather
  than something vacuous.
