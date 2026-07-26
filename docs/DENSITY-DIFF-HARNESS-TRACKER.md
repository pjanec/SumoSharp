# TRACKER — the density differential harness

At-a-glance status. Task IDs and success conditions live in `DENSITY-DIFF-HARNESS-TASKS.md`; mechanisms in
`DENSITY-DIFF-HARNESS-DESIGN.md`. **A box is ticked only when the reviewer has verified that task's success
conditions first-hand** — never on an implementor's report (`CLAUDE.md` §orchestration loop).

## Stage A — the SUMO side
- [x] **A1** three-column SUMO runner — `scripts/run-density-diff.sh`. SC1 ✅ (all four outputs, both
      columns, non-empty) · SC2 ✅ (**config diff is exactly 4 elements**, asserted in-script, run fails
      otherwise) · SC4 ✅ (fatal, non-zero exit when `sumo` is absent) ·
      **SC3 ⏸ BLOCKED ON B1, and the block is itself a finding — see below.**
- [ ] **A2** internal-lane + approach-lane detector generation

## Stage B — our side
- [x] **B1** demand recorder → SUMO `.rou.xml`. SC1 ✅ · SC2 ✅ (SUMO loads 5863 vehicles, exit 0, no
      errors) · SC3 ✅ (5863 == 5863, departs positionally identical) · SC4 ✅ *with two honest gaps*:
      reroutes are **NOT MEASURED** (no cumulative counter exists — reported as such, not as 0) and
      "insertions refused" is a labelled **proxy** (2), not an event tally.
      **Inertness proven empirically, not just by the recorder-off suites:** the recorded run's
      `ArrivedTotal` is **5381**, identical to the recorder-off head-probe at the same density.
      Route fidelity is **exact by construction** — `Engine.SpawnVehicle` routes via
      `Router().Route(from,to)` and the recorder calls the *same* two-arg overload on a `NetworkRouter`
      whose fields are all readonly (pure Dijkstra, fixed `EdgeCost`), so same function, same inputs.
- [ ] **B2** our global metrics, same schema as SUMO's
- [ ] **B3** our per-junction discharge / queue / occupancy

## Stage C — the comparison
- [ ] **C1** gap-decomposition report (cheat dividend vs real gap)
- [ ] **C2** density sweep 160 / 320 / 480 / 640
- [ ] **C3** ranked work list (measured entries only; cheats listed as won't-fix)

---

## The question this whole tracker exists to answer

> **Do our throughput curve and *honest* SUMO's bend at the same density?**

- **Same density** ⇒ the net/demand saturates; 480 in this crop is not achievable by anyone, and the target
  is wrong rather than the engine.
- **Ours bends earlier** ⇒ a real mechanism gap, ranked by its onset density.

Everything before C2 is instrument-building. **Nothing should be ported until C3 exists.**

---

## ⚠ A1/SC3: the committed demo route file CANNOT measure density (found by the guard, not by luck)

SC3 requires S-default to teleport **> 0** at high density, on the reasoning that a zero means the density is
too low to be measuring anything. Measured on `scenarios/_ped/demo_city/box/scenario.rou.xml`:

| | s-default | s-honest |
| --- | --- | --- |
| inserted | 861 | 861 |
| **teleports** | **0** | 0 |
| **collisions** | **0** | 0 |
| trips COMPLETED | **96** | 96 |
| still "running" at horizon | **765** | 765 |

**Why:** every route in that file ends `<stop parkingArea="pa_…" duration="100000"/>` — the cars **park
permanently**. It is a *parking* scenario, not a throughput one. 765 of 861 are parked-but-"running", which
is why only 96 trips complete and why nothing ever gets stuck enough to teleport.

**So this demand cannot be used as the shared input**, and A1's smoke run says nothing about SUMO's
high-density behaviour in either direction. **B1 (the demand recorder) is not optional convenience — it is
the only way to get comparable demand**, exactly as design §2 argues. Do not be tempted to reuse this file.

The one thing it does establish: the runner works end to end and the cheat-isolation assertion holds.

---

## ⭐ THE HEADLINE RESULT (480 cars, 7200 steps, identical recorded demand, 5863 vehicles all three columns)

**The premise that motivated this harness — "the engine is far from what we need" — is WRONG.
We are at 96% of honest SUMO's throughput, and SUMO buys its remaining margin with collisions it does
not even look for.**

| Metric | S-default | S-honest | **Ours** | cheat dividend | real gap |
| --- | --- | --- | --- | --- | --- |
| inserted | 5863 | 5863 | 5863 | 0 | 0 |
| **completed trips** | **5604** | **5604** | **5381** | **0** | **223 (4.0%)** |
| teleports | **0** | 0 | **0** | 0 | 0 |
| **junction collisions** | **0** *(not checked!)* | **26** | see B2 | — | — |
| mean duration (s) | 213.61 | 213.61 | see B2 | 0 | — |
| mean timeLoss (s) | 117.82 | 117.82 | see B2 | 0 | — |

### Three findings, in order of importance

1. **SUMO'S CHEAT DIVIDEND ON THROUGHPUT IS EXACTLY ZERO AT THIS DENSITY.** S-default and S-honest agree
   on *every* throughput metric to 2 dp, and **teleports are 0 in BOTH**. SUMO is not teleporting here at
   all, so the throughput target is legitimate and may be chased without ladder concerns. (The design
   anticipated a large dividend; measured, it is nil. Good — that was worth finding out before porting.)

2. **BUT SUMO COMMITS 26 JUNCTION COLLISIONS, AND ITS OWN DEFAULTS HIDE THEM.** S-default reports
   `collisions="0"` *only because* `collision.check-junctions=false`. The collisions were always happening;
   enabling the check reveals 26 — and enabling it changed throughput by **exactly zero**, which means SUMO
   **drives straight through them and keeps going.**

3. **⭐ THOSE COLLISIONS ARE ON THE EXACT LANES WE WEDGE ON.** From `collisions.xml`:

   | lane | SUMO collisions | our status there |
   | --- | --- | --- |
   | `:d_3_3_1_2` | 8 | — |
   | `:d_5_3_10_1` | **4** | **the lane carrying our four queued cars (§9.118)** |
   | `:d_5_4_9_1` | **4** | **holds three of our stopped foes (§9.117 dump)** |
   | `:d_5_4_3_0` | **2** | **one of the original four wedge bays** |
   | `:d_5_3_17_0` | **2** | **one of the residual wedge bays** |

   **SUMO resolves the very junctions we get stuck in by letting cars pass through each other.** Our
   admission arm refuses to, so we queue instead. **The 4% throughput gap and our residual bay stalls are,
   at least in part, the price of not cheating** — and that price is small.

### What this does NOT prove (read before quoting the 96%)

- **Ours is the only column without a comparable collision count.** "We are cleaner" is *not yet
  quantified in SUMO's units* — that is B2. Do not claim it until then.
- **⚠ SUMO GOT NO PEDESTRIANS IN THIS RUN.** `--add` was not passed, so SUMO ran cars-only while our run had
  **160 pedestrians** blocking crossings. That is an uncontrolled variable **favouring SUMO**, so **4.0% is
  an UPPER BOUND on our deficit** — with peds equalised the gap can only narrow, possibly to zero or
  negative. Equalising it is the next task, and until then the honest statement is "≤ 4%".
- Reroutes are uncounted (B1/SC4), so our realised paths may differ from the recorded ones.
- `running` at horizon (SUMO 259 vs our 480) is **not** a jam signal: our demo tops up to a 480 concurrency
  cap by design, so it necessarily ends full.

### Consequence for the workstream

The remaining work is **not** "catch up to SUMO on throughput". It is: close a ≤4% gap whose upper bound is
inflated by an uncontrolled pedestrian variable, while *staying* at zero teleports and beating SUMO's 26
junction collisions. **Reject outright any port whose mechanism is "let the cars interpenetrate"** — that is
what SUMO does here, it is ladder rung 3, and copying it would trade our one clear advantage for 4%.

## Known-answer anchors (an instrument that misses these is wrong, not interesting)

| Anchor | Expected | Source |
| --- | --- | --- |
| S-default teleports at 480 cars | **> 0** | `time-to-teleport` defaults to 300 |
| S-honest teleports | **0** | `--time-to-teleport -1` |
| Our teleports | **0** | ladder rung 4, currently measured 0 |
| Stopped cars on `d_5_3`/`d_5_4` internal lanes at 480, gates ON | **present** | `F3-SESSION-LOG.md` §9.118 |
| Our determinism | two runs identical | already re-verified twice |
