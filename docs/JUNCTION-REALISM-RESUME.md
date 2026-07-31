# JUNCTION-REALISM — RESUME (start here)

**Read this first, cold.** It is self-contained: you can pick the work up from this page alone.
Branch: **`claude/sumosharp-traffic-bugs-g1y9hl`**. Gate state at handoff:
**`dotnet test tests/Sim.ParityTests -c Release` = 779 passed / 5 skipped / 0 failed** (the +1 is
`UrgentStrategicFollowBehaviourTests`), all 661 goldens byte-identical — at the NEW shipped default
`UrgentStrategicLeaderFollow = true` (Entry 31). The battery reference is now
`docs/reports/net-regression-urgentfollow-on.txt` (the keepclear-direction baseline carries 4 rows of
pre-insertion-fix rot — Entry 29 attribution). ⚠ Two build traps: `src/Sim.Run` and `src/Sim.Sumo`
are **NOT in `Traffic.sln`** — build those csproj files explicitly or you will measure stale code
(Entry 30 lost a full measurement round to this). ⚠ Determinism is workload-relative: after any
change that shifts saturated-net trajectories, re-run the repeat-hash check (N identical runs,
compare FCD hashes; parallel vs `--max-parallelism 1` too) before trusting its numbers — Entry 30
found a latent parallel-plan race this way.

---

## 1. What this workstream is

The owner reported four realism defects in the live-city demo: **gridlock**; **lateral lane changes
while standing at a red** (sometimes into an occupied lane); **two cars visually merged inside a
junction**; **two cars driving through each other** when one turns left and the other goes straight.

The approach was: build a **small synthetic net with a SUMO oracle**, reproduce each defect there, then
chase them one at a time with instruments rather than reasoning.

**Owner priorities, stated explicitly:** gridlock ≫ everything else — *"permanent gridlocks with no
automatic resolution are a show stopper"*. Normal-traffic junction overlaps and lateral lane changes in
high-realism zones come next. The evac pusher overlap (§5) is explicitly deprioritised. **Gridlock is
global, not zone-scoped** — an earlier suggestion to zone-scope it was wrong and is withdrawn.

## 2. Status of the four defects

| defect | status |
|---|---|
| Cars driving **through** each other at a junction | **FIXED, shipping default-ON.** Root cause: the omitted `myInternalLinkFoes` half of `MSLink::setRequestInformation`. Our trajectory is now byte-identical to SUMO across the traced hold-and-release. Overlap **events** on the repro: 12 751 → 313 (−97.5%) |
| Two cars **overlapping stopped** in a junction | **FIXED** — same mechanism |
| **Gridlock** | **FIXED on L2 and L1.** L2 drains identically to honest SUMO; L1 went **112 → 386 arrived** of 450 with `stuckDwell` **1062 → 0**. `stuckDwell` is now 0 on every net in the 26-net battery except `city-3000` (13, unchanged). Two root causes, both in Entry 17/18 |
| **Lateral lane change while stopped at red** | **Strategic path FIXED, shipping default-ON (Entries 24–31):** the `informLeader`/`informFollower` pair (binders 18/19, `UrgentStrategicLeaderFollow`), scoped to the moving-merge regime — the L2-light left-turner now changes at t=2 / 11.95 m/s, SUMO's move; L2 rate **1.466 → 1.155** per 1000 stopped-vehicle-steps (SUMO 0.410). keepRight & speedGain halves remain (Entries 21–22). The *overlap* half does not reproduce on this net at all — 0 in both engines |

## 3. What shipped (engine changes, all on this branch)

| flag | default | what it does |
|---|---|---|
| `Engine.InternalJunctionApproachArm` | **ON** | The `myInternalLinkFoes` approach arm — holds a cont-bay vehicle against a foe *approaching* the conflict lane, not only one standing on it |
| `Engine.BayExitLaneKeepClear` | **ON** | `checkRewindLinkLanes`: don't release a vehicle from a cont bay when its own **exit lane** cannot accept it (needs `Length + MinGap` of room) |
| `Engine.BayExitLaneKeepClearExtra` | `-1` (= MinGap) | Threshold knob. Swept: the gridlock fix survives to 0.5 m, dies at 0.0. A **smaller** threshold does **not** recover throughput (city-mixed-1k 1001 → 985) |
| `Engine.JunctionPhysicalOccupancyGate` | OFF | **Do not pair it with the above.** Measured 4× counterproductive; the pair takes L2 from 320 arrived down to **61** |

Accepted cost, owner-approved: ~1% arrivals on two organic city nets (`city-mixed-1k` 1014 → 1001,
`city-organic` 509 → 499), in exchange for eliminating a total deadlock.

## 4. What was fixed since (Entries 17–18) — read before touching junction code

Two independent defects, both source-verified against vendored SUMO, both shipped unconditionally.

**(a) `LaneSpaceTillLastStanding` walked the exit-lane queue from the WRONG END.** SUMO iterates
`myVehicles`, which `MSLane.h:1439` documents as **rear-most first**, returning the space up to the
queue's **tail**. We walked the pos-ascending bucket in reverse, measuring to the **head** — 59.22 m of
reported room on a 65.60 m lane whose tail sat at pos 4.21. `KeepClearConstraint` therefore applied,
evaluated, and **never bound**, so vehicles entered junctions they could not clear. One loop direction.

**(b) `SameTargetMergeConstraint` PHASE 0 was missing `!foe.WillPass`.** `MSLink::blockedByFoe` opens with
`if (!avi.willPass) return false` (MSLink.cpp:935); the crossing arm ports it, PHASE 0 did not. A vehicle
sat inside a junction with an **empty downstream** for 120 s yielding to a foe that was **stopped at a red
light** — with both speeds at 0 the arrival-time windows overlap forever.

Also ported: SUMO's guard against aborting a vehicle already committed inside a junction
(`!(removalBegin == 0 && myLane->getEdge().isInternal())`, MSVehicle.cpp:5235). Right by the source,
but **no measurement shows it changed an outcome** — recorded as unmeasured, not as a win.

**Measured together:** L1 112 → 386 · L2 residual gone · `city-mixed-1k` 1001 → 1012 ·
synthetic-junction2 teleports → **0, exactly matching vanilla SUMO** · **all 661 goldens byte-identical**.
Cost: `city-organic` 499 → 491, and 2 extra in-junction wedges on the dense torture scenario.

**(c) `SumoShim` shipped three junction gates OFF that the `Engine` has ON** — the unsafe `== "1"` read,
fixed in Entry 19. Two shim-driven tests had been silently calibrated in that configuration, and one
carried a "hard invariant" floor **unreachable by the shipped engine**. Now guarded twice:
`SumoShimUnsetGateFallbackTests` (behavioural, with a vacuity guard) and
`EnvGateDocumentationTests.GatesWhoseEngineDefaultIsTrue_AreNotReadWithTheTwoStateForm` (verified to
fail on a revert). Shim-driven **tests** should still pin explicitly via
`tests/Sim.ParityTests/JunctionGateEnv.cs` — pinning states intent.
⚠ Any SumoData measurement taken through `SUMO_BINARY` **before** this fix is not comparable with one
taken after.

## 5. Remaining backlog, in owner priority order

1. **In-junction wedges that survive the Entry 17/18 fixes.** Two named, with exact repros, on
   `scenarios/_repro/synthetic-junction2` (dense cfg, gates pinned): `internalJunctionAdmission`
   (binder 14) on `:2810_8_0`, and `crossJxnLeader` on `:2450_0_1`. Separately, vehicles **122 and 256**
   are stranded on the dead lane `30_1` at pos 24.12 / 16.62 — **pre-existing, identical in both arms**,
   and never covered by that test's old 290-arrivals figure despite the test being named for it.
2. **Normal-traffic junction overlaps — pileup mechanism FIXED (T2.6, Entry 29): the cross-junction
   walk now also follows the ACTUAL lane's connection path when ego is off-pool
   (`BuildActualDownstreamSpan`, the planning-time mirror of `TryReResolveFromActualLane`). L2 peak
   overlaps 9 → 1 (OFF) / 21 → 3 (ON), deterministic.** Remaining in this family: (a)
   **back-protrusion invisibility** — a car whose front crossed the boundary vanishes from the lane
   its back still occupies (SUMO: `myPartialVehicles`; we have no partial occupancy) — second-order
   per Entry 28, untested since T2.6; (b) the `city-*` overlaps (`city-mixed-1k` 9 peak pairs,
   `city-3000` 6, `city-organic` 2 in the Entry 31 battery) — plausibly the same cause, now worth
   re-tracing on the fixed engine.
3. **Lateral lane change while stopped — strategic path SHIPPED default-ON (Entry 31; tracker all
   green).** The scoped informLeader/informFollower pair (binders 18/19) is
   `UrgentStrategicLeaderFollow = true`; `UrgentStrategicFollowBehaviourTests` pins the behaviour;
   `SUMOSHARP_URGENTFOLLOW=0` is the A/B/bisect switch. **Remaining halves: keepRight and
   speedGain — now DECOMPOSED on a lockstep oracle net (Entry 32, `scenarios/_diag/
   keepright-standing`): head-car stopped keepRight is correct SUMO behaviour; the artefact is
   followers, which SUMO fires AT SPEED via speedGain-right on the approach while we defer both
   paths to standstill. Entry 32 has the term-checked arithmetic (our keepRight rolling rate is
   63% of SUMO's — `neighDist` missing the best-lanes continuation), why Entries 21/22 rejected
   the right ingredients (coupled pair tried one half at a time, racy-era numbers), the exact fix
   shape (both halves in ONE design-first change), and the acceptance gates. START THERE.**
   Background (Entry 24):
   On `junction-realism-L2-light`, left-turner `f_left_W00.0` must reach lane 1:
   **SUMO changes at t=3 / pos 30.94 / 11.95 m/s** (158 m out, having *decelerated* to fit);
   **we change at t=45 / pos 189.60 / 1.00 m/s** (at the lane end, stopped). The traced veto is
   `unsafeLeadOnly=True, nFollow=none`, identical every step — the target lane's **leader** blocks, and
   both cruise at 13.89 (their shared max) so **the gap can never open by itself**. SUMO brakes to fit
   behind it (`MSLCM_LC2013::informLeader`). **We have that port** — `DeadLaneMergeBrakeConstraint`
   cites it — but scoped to "genuine dead lanes only", so it never engages. Widening it is
   parity-relevant; Entry 24 lists the three questions a design must answer, including a blast-radius
   check that is testable *before* writing the fix.
   Use `SUMOSHARP_TRACEVEH=<id>` for the `[strategic]` / `[strategic-veto]` lines.

   Background (still true, do not re-derive):
   Read journal Entries 20-22 before touching it; four hypotheses are already dead **by measurement**.
   - **The metric is a RATE, not a count**: ours **1.560** vs SUMO's **0.410** per 1000
     stopped-vehicle-steps (3.8×). SUMO stands cars still *more* than we do, so normalising
     strengthens the gap rather than explaining it. `scripts/detect-stopped-lane-change.py` prints it.
   - **All three commit paths over-fire** (keepRight 23×, strategic 16×, speedGain 4.6× by stopped
     count), so no single threshold is the answer. **81%** of ours go toward the lower lane index
     where SUMO's are balanced. Read the split with `SUMOSHARP_LCLOG=1`.
   - **Ruled out BY MEASUREMENT, do not retry**: `LaneChangeMinSpeed` (inert on the parity path);
     `neighDist` → best-lanes continuation (made it worse, Entry 21); `resetState()` zeroing both
     accumulators (a real omission, but −10 arrivals on `city-mixed-1k` for no benefit, Entry 22);
     a blanket ban on stopped changes (SUMO makes 65, not 0).
   - ⚠ **Two instrument errors are recorded in Entry 22 — read them first.** SUMO's TraCI getter for
     `keepRightProbability` **NEGATES**, and the two engines' trajectories diverge immediately, so a
     per-vehicle cross-engine side-by-side is **not** a controlled comparison. Building one (TraCI
     `moveToXY`/`setSpeed`, or a scenario that stays in lockstep) is the real next step.
   ⚠ The junction fixes made the raw COUNT worse (47 → 83 → 113): more held vehicles ⇒ more sideways
     slides. The *rate* is the comparable quantity.
   - **into an occupied lane** (NOT reproduced here): build the minimal repro
     `docs/SUMOSHARP-ISSUE-stopped-lane-change-overlap.md` §5 specifies.
4. **Pedestrian amplifier** (owner's hypothesis: a ped on the exit crossing holds a car inside the
   junction). **Cannot be tested through `Sim.Run` — it has no ped coupling at all**; needs a
   `LiveCitySim` harness plus crossings and ped demand on the repro net.
5. **Deprioritised:** `docs/NEED-evac-pusher-orca-pairwise-overlap.md` — a 0.463 m overlap between two
   evac ORCA pushers. Its test is `[Fact(Skip=…)]` with the threshold **UNCHANGED**. Do not "fix" it by
   relaxing the threshold; it measures a real overlap.
6. **Housekeeping:** `TASKS-TODO.md` states the parity law as 775/4; measured is **776/5**. Don't edit
   the literal — **single-source** it the way `EnvGateDocumentationTests` single-sources the env-gate
   table, so it cannot rot again.

## 6. Instruments (all committed — use these, do not re-derive)

| tool | what it answers |
|---|---|
| `scripts/gen-junction-realism-net.py` | regenerates the repro nets; header explains the demand calibration |
| `scripts/analyze-junction-realism-fcd.py` | onset, dwell, wedge/overlap causal order, OBB overlap pairs. Runs on **either** engine's FCD |
| `scripts/run-net-regression.py` | the 26-net battery. `--compare <baseline>` prints regressions |
| `scripts/detect-stopped-lane-change.py` | stopped sideways lane changes + whether they landed overlapping |
| `SUMOSHARP_TRACEVEH=<vehId>` | per-vehicle constraint trace to stderr: `KeepClearConstraint`'s space walk, `SameTargetMergeConstraint`'s phase + foe. Read by **both** `Sim.Run` and the `sumosharp` drop-in |
| `SUMOSHARP_LCLOG=1` | committed lane changes histogrammed by [path][changer speed] — the only way to see WHICH path swaps a standing car |
| `pip install traci` → `laneChangeModel.keepRightProbability` | SUMO's own accumulator, live. ⚠ the getter **NEGATES** (MSLCM_LC2013.cpp:2120) |
| `SUMOSHARP_BINDERLOG=<path>` | the binder CSV below, from the **drop-in binary** — needed because shim and `Sim.Run` are different engine configurations |
| `Sim.Run --binder-log PATH` | per-vehicle per-step CSV: `t,veh,lane,pos,speed,binder,binderName,jyArm,jyGreen,blocker` — **the single most useful instrument here** |
| `tests/…/EvacPusherOverlapDiagTests.cs` | always-passing report on evac pusher separation |

Baselines: `docs/reports/net-regression-{baseline,approach-arm,bay-exit-keepclear}.txt`.
**Always measure both arms with the same script** — cross-instrument numbers are invalid.

Binder legend: 0 none · 1 leaderFollow · 2 crossJxnLeader · 3 freeFlow · 4 successiveLane ·
5 deadLaneMerge · 6 stopLine · 7 redLight · 8 railSignal · 9 railCrossing · **10 junctionYield** ·
**11 keepClear** · 12 obstacle · 13 crowd · **14 internalJunctionAdmission** · 15 colocationSymmetryBreak ·
16 crowdYield · **17 internalJunctionApproachArm**.
`jyArm` (only meaningful when binder==10): 1 cycleHold · 2 cautiousApproach · **3 sameTargetMerge** ·
4 externalAgent · **5 onJunctionLeader** · 6 approachingCross.

## 7. Traps this session actually hit — read before trusting a number

1. **A diagnostic that never fires is evidence about the GUARD, not the hazard.** `keepClear` = 0 of 226
   was read as "not the mechanism". It was the opposite: the guard is broken. Cost: one wrong conclusion.
2. **A field that is constant across every sample is probably not wired.** `jyArm` read 0 for 100% of
   15 739 rows because a constructor argument was silently dropped. I argued myself out of the right
   instinct with a plausible code-reading.
3. **Scripted text replaces fail SILENTLY.** That dropped argument came from a `python replace` whose
   anchor no longer matched. **Assert every anchor.**
4. **Incidence ≠ duration.** "Overlap events +16%" (pair×step) looked like a regression; distinct
   overlapping pairs were 92 → 93, i.e. flat. And peak-simultaneous *understated* the same fix 20×.
   **Report both, never let one stand for the other.**
5. **A snapshot wait-for graph cannot see a cycle that a periodic constraint masks.** The first cycle
   looked like a tree rooted at a red light; the closing edge was invisible ~54% of samples.
6. **Check whether the guard already exists before building one.** Two of the most valuable moves this
   session were *not* writing code.

7. **A vehicle disappearing is not a vehicle standing still.** A stall-run metric that never closes its
   run when a vehicle leaves the sim scored two teleported vehicles as stalled for 1677 s and 1228 s.
   The wedge was real; the durations were fiction. Check the exit condition of any run-length metric.
8. **A shim-driven number is a different engine configuration.** `SumoShim` forces three junction gates
   off that the engine ships on. Pin them (`JunctionGateEnv`) or the number is not about what we ship.
9. **An assertion can outlive its premise.** "The knob must not make teleports worse" became
   unsatisfiable-except-at-zero once the baseline reached zero. Re-read what an assertion *means* when
   its baseline moves, rather than treating the failure as a regression.

**Scoreboard, kept honestly: eleven wrong hypotheses, six instrument/process defects.** Every real result
came from an instrument; the instruments needed fixing about as often as the engine did. Record
predictions *before* measuring — several would otherwise have been quietly revised afterwards.

## 8. Doc map

- `JUNCTION-REALISM-SESSION-JOURNAL.md` — append-only BEFORE/AFTER entries, 16 of them. **The full trail.**
- `JUNCTION-REALISM-TRACE-FINDINGS.md` — the measured characterisation (§5 lists what is *not* established).
- `JUNCTION-APPROACH-ARM-{DESIGN,TASKS,TRACKER}.md` — the design-first trio for the shipped approach arm.
- `NEED-evac-pusher-orca-pairwise-overlap.md` — the parked evac defect.
- `ENV-GATES.md` — every gate, including the four added here. Completeness is test-enforced.
