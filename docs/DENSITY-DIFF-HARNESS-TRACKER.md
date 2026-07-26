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
- [x] **A3** ✅ **DONE — open-loop mode, and it CONFIRMS the deficit.** `LiveCityConfig.CarInflowVehPerSec`
      (null = unchanged), `--inflow`/`--series`, two-window steady-state test, `scripts/sweep-inflow.sh`.
      **SC2 met decisively:** at 1.7 veh/s on identical demand SUMO is STEADY (311→306) and we are RUNAWAY
      (420→464). The two workstreams' instruments AGREE. Full sweep in
      `docs/reports/density-inflow-sweep.txt`.
- [ ] ~~**A3**~~ ⚠ **(superseded by the line above) OPEN-LOOP demand mode — BLOCKS ALL CAPACITY WORK.** The demo's demand is occupancy-capped,
      so inflow self-throttles and a discharge deficit is structurally invisible (design §1b). Do B2/B3/C
      only after this, or they measure the wrong quantity.

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

## ⚠️ RETRACTED AS A CAPACITY CLAIM — the 96% below measures the WRONG QUANTITY (read this first)

**RETRACTION.** This section originally concluded *"the premise that the engine is far from what we need is
WRONG — we are at 96% of honest SUMO's throughput"*. **That conclusion does not follow from this experiment,
and the parallel calibration workstream's contradicting result is the correct one.** The 96% is real but it
is not a capacity measurement, for a reason that is structural:

> **`LiveCitySim`'s demand is CLOSED-LOOP.** The spawn loop is
> `for (s = 0; s < CarSpawnPerStep && live < CarTargetConcurrent; s++)` — it inserts **only while occupancy
> is below the cap**. So **inflow is throttled by our own drain.** If our junctions discharge slowly we
> simply insert fewer cars. Occupancy *cannot* run away; it is clamped at the cap by construction.

Measuring discharge capacity requires **OPEN-LOOP** demand: a fixed inflow, independent of what is already
on the network, so that a too-narrow drain shows up as unbounded queue growth. That is exactly what the
calibration workstream did, and it found vanilla SUMO plateauing at ~430 cars while SumoSharp climbed
258 → 2623 without ever levelling off.

**So this experiment cannot detect a discharge deficit, and its 96% must never be quoted as one.** What it
does legitimately establish is narrower: *given a demand profile our own engine shaped, we complete 96% of
what SUMO completes on that same profile.* The demand was pre-limited to what we could already handle.

### ⭐ AND MY OWN DATA AGREES WITH THEM, once read correctly

Two numbers in the table below were reported at the time as an artefact to be ignored. They are the finding:

| | SUMO (s-honest) | Ours |
| --- | --- | --- |
| still in flight at horizon | **259** | **480** ← *our cap; we ended FULL* |
| mean trip duration | **213.6 s** | **~321 s** (Little's Law: 480 / 1.4947 s⁻¹) |
| mean occupancy | ~333 cars (1.5567 × 213.6) | ~480 |

**We hold ~45% more cars in the network to deliver 4% FEWER trips, and each trip takes ~50% longer.** That
is a narrower drain, stated in my own measurement. And SUMO finishing with only 259 in flight proves the
offered inflow (1.63 veh/s — *chosen by our drain*) never came close to saturating SUMO, so this test had no
power to expose a capacity gap in either direction.

**Method error to learn from:** I used a self-throttling demand model to answer a capacity question. Same
error class as §9.117's occupancy-vs-causation and §9.100's mislabel — *measuring a different quantity than
the one named*. The guard that should have caught it existed and I wrote it off: I noted "SUMO 259 vs our 480
is not a jam signal" and moved on, when it was the whole story.

### What survives unchanged (and is still valuable)

The **cheat findings are unaffected** — they are about SUMO's own behaviour on a fixed input, not about
capacity:

| Metric | S-default | S-honest | **Ours** | cheat dividend | real gap |
| --- | --- | --- | --- | --- | --- |
| inserted | 5863 | 5863 | 5863 | 0 | 0 |
| **completed trips** | **5604** | **5604** | **5381** | **0** | **223 (4.0%)** |
| teleports | **0** | 0 | **0** | 0 | 0 |
| **junction collisions** | **0** *(not checked!)* | **26** | see B2 | — | — |
| mean duration (s) | 213.61 | 213.61 | see B2 | 0 | — |
| mean timeLoss (s) | 117.82 | 117.82 | see B2 | 0 | — |

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

### Consequence for the workstream — REVISED

**A new blocking task, A3, comes before everything else: an OPEN-LOOP demand mode.** Until the harness can
offer a fixed inflow independent of our own drain, it cannot measure discharge, and every column it produces
answers a question nobody asked. The closed-loop comparison stays as a *secondary* check ("on identical
demand, who completes more"), clearly labelled as such.

Then the real target is the one the calibration workstream named: **make the drain wider**, measured as
saturation-flow (vehicles discharged per green second per lane) against SUMO on the same junction — not as
completed trips under a demand we throttled ourselves.

**Still binding:** reject any port whose mechanism is "let the cars interpenetrate". SUMO's 26 junction
collisions are ladder rung 3, and copying them would trade our one clear advantage for throughput.

**Two already-measured candidates now look like DISCHARGE mechanisms, which raises their priority:**
1. **Cars queueing inside junctions** (§9.118: four stopped on `:d_5_3_10_1`). A car standing in the
   intersection blocks the conflict area for everyone crossing it — that is a drain restriction by definition.
2. **Arm 14 holds a bay while a foe is anywhere on a conflicting lane, INCLUDING one still moving at
   0.89–3.05 m/s that has already passed the conflict point** (§9.118). Every step a bay is held closed while
   it could be discharging is lost saturation flow. `inTheWay`'s conflict-point geometry is the missing piece,
   and it is no longer a "bounded conservatism" — it is plausibly a direct discharge cost.

---

## ⭐⭐ THE CAPACITY ANSWER (A3 open-loop sweep, 7200 steps, identical demand per row)

| open-loop inflow | **OURS** | SUMO s-honest | SUMO s-default |
| --- | --- | --- | --- |
| 0.8 veh/s | STEADY @162 (arr 2573) | STEADY @130 | STEADY @130 |
| 1.0 | STEADY @201 (arr 3198) | STEADY @165 | STEADY @165 |
| 1.2 | STEADY @254 (arr 3817) | STEADY @203 | STEADY @203 |
| **1.4** | **STEADY @306 (arr 4448) ← OUR CEILING** | STEADY @240 | STEADY @240 |
| **1.6** | **RUNAWAY → 2242 resident, arr 2938** | **STEADY @280** | STEADY @280 |
| 2.0 | RUNAWAY → 3528, arr 1681 | RUNAWAY @940 (+61.6%) | RUNAWAY @988 |

**Max sustainable inflow: ours ≈ 1.4 veh/s, SUMO's between 1.6 and 2.0.** A deficit of **at least 14%**,
probably nearer 30%.

Two things matter more than the ceiling itself:

1. **At EVERY sustainable inflow we hold ~25% more resident cars for the same flow** (162/130, 201/165,
   254/203, 306/240). Our vehicles spend consistently longer in the network even when perfectly stable.
   That is not junction *blocking* — blocking would show as collapse — it is **uniformly slower progress**.
2. **We do not degrade gracefully, we COLLAPSE.** Crossing the ceiling takes completed trips
   **4448 → 2938 → 1681** while resident climbs 306 → 2242 → 3528. SUMO's own runaway at 2.0 is far gentler
   (940 resident). Whatever our failure mode is, it is self-amplifying.

## ❌ DRAIN-1 / G1 REFUTED AS A DISCHARGE FIX (measured, not argued)

`Engine.KeepClearHeldPropagation` ports G1 of `NEED-checkrewindlinklanes-partial-port.md` — propagate
blockage backward from a car that merely *cannot proceed*, SUMO's
`last->myHaveToWaitOnNextLink || last->isStopped()`, of which we had only the second disjunct. The NEED doc
ranked it "highest impact" of its four gaps. A/B at inflow 1.6:

| | arrived | resident at horizon | last-two-quarter growth |
| --- | --- | --- | --- |
| G1 OFF | **2938** | 2242 | +57.8% |
| G1 ON | **2762** | 2498 | +58.6% |

**Slightly WORSE on both.** Which is coherent in hindsight: G1 makes admission *more* conservative, so it
holds cars back at junction entries — the opposite of widening a drain. It is a **faithfulness** improvement
(it is what SUMO does) but **not** the capacity fix. Kept, default **OFF**, and not to be retried for
discharge.

**Note the shape of this failure:** it was a mechanism hypothesis reasoned from source, and it took one
measurement to refute. `NEED-junctionyield-impatience-saturation.md` records the same pattern ending the same
way — **five** reasoned-from-the-code interventions were inert, and the real cause (a cont-turn U-turn
distance bug) was found by **a single SUMO-oracle FCD trace of one gridlocked vehicle**. Its closing line is
the instruction for what comes next here: *"a single SUMO-oracle trace found in minutes what five
reasoned-from-the-code interventions could not."*

### ⇒ NEXT: trace, do not hypothesise

At **1.4 veh/s** (both engines steady, so the comparison is apples-to-apples and not confounded by collapse),
diff one vehicle's trajectory between ours and SUMO on identical demand and find **where the extra ~25% of
time in system is spent** — which edge, which junction, approach vs interior. Only then pick a mechanism.

## Known-answer anchors (an instrument that misses these is wrong, not interesting)

| Anchor | Expected | Source |
| --- | --- | --- |
| S-default teleports at 480 cars | **> 0** | `time-to-teleport` defaults to 300 |
| S-honest teleports | **0** | `--time-to-teleport -1` |
| Our teleports | **0** | ladder rung 4, currently measured 0 |
| Stopped cars on `d_5_3`/`d_5_4` internal lanes at 480, gates ON | **present** | `F3-SESSION-LOG.md` §9.118 |
| Our determinism | two runs identical | already re-verified twice |
