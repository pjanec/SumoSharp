# DESIGN — the density differential harness (engine vs SUMO vs *honest* SUMO)

**Status:** design of record for the density/discharge workstream. HOW it works; the WHAT is in
`docs/CONSTRAINT-high-realism-artefact-ladder.md` (believability) and the owner's stated long-term goal:
**high believable density and stable discharge of junctions.**
**Companion docs:** `DENSITY-DIFF-HARNESS-TASKS.md` (the work), `DENSITY-DIFF-HARNESS-TRACKER.md` (status).

---

## 0. Why this exists — the validation gap, stated as a measured fact

Turning all seven junction/overlap gates ON left **all 661 goldens byte-identical**
(`F3-SESSION-LOG.md` §9.119). Seven behavioural changes, two of which take the live-city demo from
permanent gridlock to free-flowing, and the entire parity suite could not tell the difference. The only
tests that noticed were the six asserting the *default values*.

That is not the gates being trivial. **The goldens are 2–5 vehicles for ~40 steps.** They cannot contain a
saturated junction, a queue standing on an internal lane, or a discharge cycle, because those states never
arise at that scale. Meanwhile the only nets that *do* saturate — the demo and `_bench/*` — have **no SUMO
reference at all**, so every judgement there has been "does it look better".

**Consequence:** every density fix so far has been reasoned about rather than measured against the
reference. That is why five hypotheses were refuted late, two fixes were built on stale attributions, and
one whole carried hypothesis (`addBlockedLink`) turned out to be dead code. The single highest-yield move
of the entire workstream was the one differential-vs-SUMO analysis anyone ran (§9.9). **This harness makes
that move repeatable.**

---

## 1. The load-bearing design decision: SUMO's defaults ARE the cheating

Read from `sumo --save-template` on the pinned 1.20.0:

| Option | SUMO default | Ladder verdict |
| --- | --- | --- |
| `time-to-teleport` | **300** | **rung 4** — teleports any car stuck 5 min |
| `collision.action` | **teleport** | **rung 4** — resolves a collision *by teleporting* |
| `collision.check-junctions` | **false** | **rung 3 made invisible** — junction interpenetration is not even detected |
| `ignore-junction-blocker` | `-1` | fine (never ignores) |
| `time-to-teleport.highways` | `0` | disabled |

**So "match SUMO" is the WRONG target.** Vanilla SUMO can post excellent high-density discharge while
teleporting stuck cars, teleporting collided cars, and never noticing cars overlapping inside junctions.
Copying that would import exactly the artefacts the ladder forbids — and rank-4 (teleport) is the one the
owner has ruled out unconditionally in high-realism areas.

Equally: **outside the cheats SUMO is the reference and we are behind it.** The design must separate those
two things instead of collapsing them into one "diff from SUMO" number.

### The three columns

| Column | Configuration | Role |
| --- | --- | --- |
| **S-default** | SUMO as shipped | Upper bound. **NOT a target.** Its margin over S-honest *is* the cheat dividend. |
| **S-honest** | SUMO with cheats off: `--time-to-teleport -1`, `--time-to-teleport.highways -1`, `--collision.action warn`, `--collision.check-junctions true` | **THE TARGET.** SUMO playing by our high-realism rules. |
| **Ours** | Our engine, gates at their new defaults | Us. |

`--collision.action warn` rather than `none`: `warn` still logs to `--collision-output` without teleporting,
so we *count* SUMO's collisions instead of letting it hide them. `none` would suppress the record.

### The gap decomposition — the whole point of the harness

```
  S-default  −  S-honest   =  SUMO'S CHEAT DIVIDEND   -> we must NOT chase this. Ever.
  S-honest   −  Ours       =  THE REAL WORK LIST      -> ranked, this is what to port.
  Ours       −  S-honest   =  where we are AHEAD      -> worth knowing; do not regress it.
```

A metric where our deficit vanishes once SUMO's cheats are disabled is **not a defect** and must be closed
out, not worked on. This is the trap the harness exists to prevent: chasing a number that only exists
because SUMO teleported.

**Corollary that must not be missed:** if S-honest's junction collision count is high, then even *honest*
SUMO buys its discharge partly through interpenetration, and the ladder means our target is **S-honest's
throughput at S-honest's collision count of zero** — a bar SUMO itself may not clear. That is an acceptable
and expected outcome, and it must be reported rather than treated as our failure.

---

## 1b. ⚠ OPEN-LOOP vs CLOSED-LOOP DEMAND — the distinction the first version of this design missed

**This section was added after the first measurement produced a confident, wrong conclusion.**

`LiveCitySim`'s demand is **CLOSED-LOOP**: `for (s = 0; s < CarSpawnPerStep && live < CarTargetConcurrent; s++)`
inserts **only while occupancy is below the cap**. Inflow is therefore *throttled by our own drain* — a slow
junction simply causes fewer insertions, and occupancy can never exceed the cap.

**Consequences, and they are severe for anything capacity-related:**

| Question | Closed-loop can answer it? |
| --- | --- |
| "On identical demand, who completes more trips?" | ✅ yes |
| "Do we interpenetrate / teleport more than SUMO?" | ✅ yes |
| **"What inflow can the network sustain?"** | ❌ **NO — the cap hides it** |
| **"Is our junction discharge narrower than SUMO's?"** | ❌ **NO — inflow adapts to the deficit** |

A discharge deficit manifests as **unbounded queue growth at fixed inflow**. A closed-loop model cannot
produce unbounded growth, so it cannot show the symptom, so a comparison built on it will report "we are
close to SUMO" *no matter how narrow our drain is*. That is precisely what happened: the closed-loop run
reported 96% of SUMO's throughput while a parallel open-loop experiment showed SumoSharp climbing
258 → 2623 cars over an hour and never reaching steady state, against vanilla SUMO plateauing at ~430.

**So capacity work REQUIRES an open-loop mode** (task A3): a fixed insertion rate, independent of occupancy,
run to a horizon, with occupancy-over-time plotted. Steady state (level holds) vs runaway (level climbs) is
the measurement; the highest inflow that still reaches steady state is "max density".

**The tell that was present and dismissed:** on the closed-loop run SUMO ended with **259** vehicles in
flight while we ended with **480** — our cap, i.e. full. Same demand, same horizon. SUMO was nowhere near
saturated by an inflow *our own drain had chosen*, and we were pinned at the ceiling. By Little's Law that is
~333 cars at 213.6 s mean duration for SUMO against ~480 at ~321 s for us: **~45% more cars resident to
deliver 4% fewer trips.** The deficit was in the data; the framing hid it.

**Rule going forward:** every metric this harness reports must be labelled with the demand model that produced
it. A capacity claim from closed-loop demand is invalid regardless of how carefully the rest was measured.

---

## 2. Identical demand: record-at-spawn

Both engines must see the same cars. The demo generates demand procedurally from a seeded RNG inside
`LiveCitySim`, so there is no `.rou.xml` to share. **We export ours and feed it to SUMO**, rather than
generating a route file and teaching the demo to consume one — the latter would change the demo, and the
demo's demand *is* the thing under test.

Mechanism: an optional recorder on `LiveCitySim` that, at each insertion, appends
`<vehicle id depart departLane departPos departSpeed type><route edges=.../></vehicle>` in **depart order**
(SUMO requires sorted departs, or `--route-files` must be given `--sorted` off). Written once, then replayed
into SUMO.

Three fidelity caveats, each recorded rather than papered over:

1. **Mid-trip rerouting.** We reroute in two places (`GAP-1` dead-lane reroute, `WrongLaneRerouteAtApproach`).
   A recorded route is the route *at spawn*, so a rerouted vehicle's realised path diverges from what SUMO
   is given. **Mitigation:** count reroutes in our column and report them; if nonzero, the comparison is
   qualified. Do NOT silently accept it.
2. **Insertion refusal.** A car we refuse to insert (`InsertionFollowerGapCheck`) still appears in the
   recorded file, and SUMO may insert it. That is a *legitimate* difference and belongs in the report as
   "insertions refused", not hidden.
3. **Pedestrians.** The demo has ~160 pedestrians affecting vehicle behaviour via crossings. SUMO gets the
   committed ped demand where one exists; where it does not, **the ped column is a declared uncontrolled
   variable**, not an assumed zero.

---

## 3. Metrics — chosen to answer "stable discharge of junctions", not "how close are we"

Global throughput alone cannot distinguish "the net is saturated" from "one junction is broken", which is
exactly the mistake §9.100 made. So the harness reports at three scales.

### 3a. Global (from SUMO's own outputs, mirrored on our side)

| Metric | SUMO source | Ours |
| --- | --- | --- |
| completed trips | `statistic-output` / `tripinfo` count | `ArrivedTotal` |
| mean duration / waitingTime / timeLoss | `tripinfo` | per-vehicle accumulators |
| running / halting per step | `summary-output` | witness scan |
| **teleports** | `statistic-output` | engine counter (**must be 0**) |
| **collisions** | `collision-output` | OBB overlap episodes |

### 3b. Per junction — the discharge curve (the metric that matters)

For each junction, per 60 s bucket: **vehicles that completed a crossing** (entered an internal lane and
left it), plus **max approach-queue length** and **max internal-lane occupancy**. A junction whose discharge
rate collapses while its approach queue grows is broken; one whose discharge holds while the queue grows is
merely saturated. **That distinction is the deliverable** — it is what separates a defect from oversaturation,
and getting it wrong once already cost this project a wrong verdict.

Ours comes from the lane-advance seam; SUMO's from `--netstate-dump` or lane-area detectors generated for
every internal lane. Prefer generated detectors (`e2` on each approach lane + internal lane) over
`netstate-dump`: bounded output size at 7200 steps, whereas a full dump at 480 vehicles is gigabytes.

### 3c. Per vehicle — first divergence

For the worst-deficit junction only, the first step at which a vehicle's position differs by more than
tolerance between Ours and S-honest, to localise a mechanism. Reuses the existing FCD-diff machinery.

---

## 4. The density sweep

160 (design) / 320 / 480 (today's stress point) / 640, 7200 steps, three columns each = 12 SUMO runs +
4 of ours. Purpose: **find where each curve breaks, not just the gap at one point.**

The two informative shapes:

- **Both curves bend at the same density** ⇒ the net/demand saturates. The target is wrong, not the engine,
  and the honest answer is that 480 in this crop is not achievable by anyone.
- **Ours bends earlier** ⇒ mechanism gap, and its *onset density* ranks it: a mechanism that only matters at
  640 is worth less than one that costs throughput at 320.

**This must be run before any porting decision.** It is the ranking function for all subsequent work.

---

## 5. Determinism and the offline-loop invariant

- Phase-1 determinism per `CLAUDE.md`: `sigma=0`, fixed depart, `actionStepLength=1`, Euler.
- **Our side must be byte-reproducible** across runs — already true and re-verified (two independent probe
  runs gave identical figures).
- **⚠ THE HARNESS MUST NEVER BE A `dotnet test` DEPENDENCY.** It invokes SUMO. Per `CLAUDE.md` the offline
  loop must pass on a fresh VM with no SUMO installed. So: the harness is a **separate console project**
  (`src/Sim.DensityDiff`), not a test; any committed test that consumes it reads a **committed report file**,
  never a live SUMO run. Same discipline as golden regeneration.
- Reports are committed (they are evidence, and evidence whose instrument is deleted is unfalsifiable —
  `F3-SESSION-LOG.md` §7 lesson 13).

---

## 6. What this harness is NOT

- **Not a parity gate.** It cannot fail the build. `S-honest − Ours` is a work list, not a tolerance.
- **Not a licence to copy SUMO's cheats.** Any finding that resolves to "SUMO teleports here" is closed as
  won't-fix under the ladder, and recorded as such so it is not rediscovered.
- **Not a replacement for the goldens.** The goldens remain the exactness statement on small scenarios; this
  is the *behavioural* statement at density. §0 is the argument for why both are needed.
