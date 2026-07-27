# F3 SESSION LOG — resumable running record

**Purpose:** a self-contained log so this workstream can be resumed from **near-zero context** (e.g. after
an auto-compact or in a fresh session). Read this first, top to bottom. It is kept **append-mostly**: new
findings go in §9 (Chronological log) and the summary tables in §3–§6 are updated in place.

**Branch:** `claude/f3-junction-overlap-handoff-okf5nu`
**Original brief:** `docs/F3-JUNCTION-OVERLAP-HANDOFF.md` (⚠ contains several claims now DISPROVEN — see §4)
**Design trio:** `docs/F3-JUNCTION-OVERLAP-{DESIGN,TASKS,TRACKER}.md`

---

## 1. Environment bootstrap (do this first in a fresh VM)

The VM is volatile. Nothing below is committed; all of it must be redone.

```bash
# .NET 8 SDK -- NOT pre-installed. apt-get update FIRST or the install 404s on stale indexes.
apt-get update && apt-get install -y dotnet-sdk-8.0

# SUMO 1.20.0 -- the PINNED version (SUMO_VERSION). Needed only for golden work / SUMO diffs.
pip install eclipse-sumo==1.20.0
export PATH=/usr/local/lib/python3.11/dist-packages/sumo/bin:$PATH
sumo --version   # MUST print 1.20.0
```

**⚠ TRAP:** `apt-get install sumo` gives **1.18.0**, and bare `sumo` on `PATH` resolves to it. 1.18.0 is
**not a valid parity anchor** (`SUMO_VERSION` pins 1.20.0). Always put the pip `bin/` first on `PATH`.

**⚠ TRAP:** an earlier note in this repo claimed `pip install eclipse-sumo==1.20.0` fails here. **It does
not** — that was a bad verification command (`import sumo` is not the module name), not a pip failure.

## 2. The gate (run these to confirm a clean starting point)

| Command | Expected |
| --- | --- |
| `dotnet test tests/Sim.ParityTests -c Release` | **755 passed / 4 skipped / 0 failed** (752 + 3 from main at merge; was 689) |
| `dotnet run --project src/Sim.Bench -c Release` | hash **`BF3794A4704BCD79`**, `deterministic=True`, par==single (⚠ **re-pinned session 4** — was `D96213B7BB4021A7` with the gates OFF; change verified attributable by stashing the defaults and re-running) |
| `dotnet test tests/Sim.LiveCity.Tests` (**no** `--no-build`; **NOT in `Traffic.sln`**) | **50 / 50** (session 4: +`HeadOfQueueStallProbeTests`) |
| `dotnet test tests/Sim.Pedestrians.Tests -c Release` | **272 / 272** |

752 = 689 (session-2 baseline) + T2.1 13 + T2.2 3 + T2.3 12 + T2.4 4 + T3.1 4 + T3.2 4 + fix-1 6 +
fix-2/3 7. The 4 skips are pre-existing
(`LaneChangeOverlapDiagTests`, `RungC4vii…`, `RungP24…`, `RungP2Core…`).

**The 5 gridlock diagnostics are the load-bearing regression net** — they caught a bad change in one run and
quantified it. Never judge a junction change on goldens alone (§7, Lesson 1):
`WillPassSaturationDiagTests`, `DenseFlowDeadLaneDrainTests`, `RungHDp2g2CoordinatedLaneChangeTests`,
`RblLeftTurnsDiagTests`, `LowDensityTeleportTests`.

**⚠ SESSION 3: two of those five were UNRELIABLE and are now fixed.** `SumoShim` reads the
**process-global** env var `SUMOSHARP_CONTTURNFIX` (`SumoShim.cs:250`), `IgnoreJunctionBlockerTests`
*sets* it, and xUnit runs separate collections in **parallel** — so a concurrent shim test could
silently simulate with the cont-turn gate ON. `LowDensityTeleportTests` failed **1 in 3** full-suite
runs with exactly **5** teleports (the flag-ON count) while passing standalone. Fixed by serialising all
six `SumoShim.Run` classes into `SumoShimEnvCollection`. **Contract: a new test calling `SumoShim.Run`
MUST carry `[Collection(SumoShimEnvCollection.Name)]`.** See §9.43 and
`NEED-sumoshim-process-global-contturn-env.md`.

## 3. What the task actually is (vs how it was briefed)

Brief: "two vehicles occupy the same space at a junction, worst 3.035 m; fix the missing occupancy gate;
then flip the overlap invariant to assert ZERO."

Reality, measured: the 61 overlap events in the live-city demo are **four unrelated causes plus a broken
measuring instrument**, and F3 proper is **8 of 61**.

| Bucket | Events | Worst | Cause | Doc |
| --- | --- | --- | --- | --- |
| `BOTH-INTERNAL-DIFFERENT-LANE` | **8** | 3.035 m | **true F3** — crossing internal lanes | design §2 |
| `ONE-INTERNAL-ONE-NORMAL` | 31 | 1.800 m | 60/62 involve a car stopped at/inside a junction | N-contturn / N-stale |
| `BOTH-NORMAL-SAME-LANE` | 14 | 1.800 m | not junction; incl. *exactly co-located* cars | `NEED-colocated-vehicles.md` |
| `BOTH-NORMAL-DIFFERENT-LANE` | 8 | 1.800 m | two **normal** lanes overlapping in world space | `NEED-democity-overlapping-lane-geometry.md` |

**⚠ The table above is the BROKEN-MATH baseline.** CORRECTED (axis + anchor fixed, `VehicleObb`):
**total 45** events, worst **2.382 m** (`__veh109/__veh163`, step 185), max 4 pairs/frame; buckets
0 / **15** / **8** / 14 / 8. The two `BOTH-NORMAL` buckets are unchanged (the bug is provably inert there),
the F3 bucket went **8 → 15** with worst **down** 0.653 m, and `ONE-INTERNAL-ONE-NORMAL` collapsed
**31 → 8**. `__veh134/__veh38` now peaks at **1.022 m**, not 3.035 m. F3-bucket split is now
**3 stopped-foe / 12 both-moving** (80% both-moving).

## 4. Handoff claims DISPROVEN (do not re-derive these)

1. **`--live-city-drcheck` / `--live-city-cartrace` do not exist** despite `[verified]` marks. Deleted;
   survive only in commit `d9b209b`. Use `DemoCarOverlapInvariantTests` / `F3JunctionOverlapDiagTests`.
2. **The headline 3.035 m is mostly an instrument artefact.** Sampled `(X,Y)` is the **front bumper**
   (`Engine.cs:2278` → `LaneGeometry.PositionAtOffset`, `Kinematics.Pos`), but `ObbOverlap` treats it as the
   box **centre** → every box drawn `Length/2` (2.5 m) too far forward. Anchored correctly the famous
   `veh134/veh38` pair is **0.497 m**, not 3.035 m. → `NEED-obb-anchor-halflength.md`.
   (Correcting it *raises* the count 61→97 — both cars shift, so it changes *which* pairs overlap.)
3. **Handoff "Pattern B" is misdiagnosed.** For steps 51–57 `veh80` is on `e_d_6_5_d_5_5_2`, a **normal**
   lane, overlapping the garage stub — no internal lane involved. Not an admission-gate bug.
4. **"Assert ZERO overlap" (F4b) is stronger than SUMO parity and must not be done as specified.**
   `--collision.check-junctions` **defaults to `false`** (`MSFrame.cpp:391`); SUMO's default safety model is
   1-D longitudinal (`MSLane.cpp:1884`); width only matters under the sublane model (also off by default);
   internal lanes **overlap by construction** (`MSLink.cpp:334-366`, `DIVERGENCE_MIN_WIDTH 2.5`). SUMO docs
   say junction collisions "are only registered when setting `--collision.check-junctions`". The
   SUMO-faithful invariant is 1-D `gap >= 0`; keep the 2-D check only as a calibrated tripwire. → design §6b.
5. **`1.800 m` is the vehicle WIDTH** (L=5.0, W=1.8) — the min-penetration axis saturating, not a depth.
5b. **⚠ THE OBB FORWARD AXIS IS ALSO WRONG — a REFLECTION, not a sign flip.** `ObbOverlap` uses
   `forward = (-sin θ, cos θ)`, but `PositionAtOffset` returns **naviDegree**
   (`naviDeg = 90 - atan2(dy,dx)·180/π`, `LaneGeometry.cs:59-60`), so the true tangent is
   **`(+sin θ, cos θ)`**. Verified numerically: identical axis at 0°/90° (`|dot|=1.000`) but
   **PERPENDICULAR at 45°** (`|dot|=0.000`). Junction internal lanes are curved, i.e. mostly diagonal —
   exactly where it fails. It produced a **false positive 0.328 m overlap** in the veh95/veh102 check.
   The handoff's own §6 heading-convention lesson "validated on veh80 (`angle=90`)" — the one degenerate
   case where both conventions agree. **So every overlap figure in §3 and in this session's A/B tables is
   unreliable for non-axis-aligned headings, in BOTH directions (false positives and false negatives).**
   → `NEED-obb-anchor-halflength.md`. Fix WITH the anchor bug in one commit; they interact.
6. **Scenario 44's skip banner is STALE.** Its documented bugs A and B do **not** reproduce at HEAD (both
   cont chains traverse correctly, 4/4 arrive). It is **not** a repro for the cont-turn defect.
   Separately its **golden is invalid** — generated with **ballistic** integration because
   `config.sumocfg` omits `<step-method.ballistic value="false"/>`. Verified by running SUMO 1.20.0: as
   committed it reproduces the golden byte-for-byte with `pos=1.300` at t=1; with the flag, `2.600` =
   the engine's Euler. → `NEED-scenario44-golden-ballistic-mismatch.md`.

## 5. What is SHIPPED (gate green; the seven junction/overlap gates now default **ON** — §9.119)

| Item | State |
| --- | --- |
| `src/Sim.Ingest/VehicleObb.cs` — one correct OBB helper (axis **and** anchor) | **live, unconditional** |
| `VehicleObbConventionTests` (15) — derives the tangent from `LaneGeometry` over 2153 internal lanes | **live**, non-vacuous by construction |
| `NetworkModel.JunctionByInternalLane` + `IsInternalLaneOfJunction(laneId, junction)` | **live, unconditional, parity-safe** |
| `ContTurnInternalLaneOwnershipTests` (9) | **live** — direct, offline, non-vacuous |
| Binder diagnostics written on BOTH passes (T1.8) | **live, unconditional, parity-safe** |
| `Engine.IgnoreJunctionBlockerSeconds` + `--ignore-junction-blocker` (CLI) + `<processing>` cfg element | **live, default `-1` = SUMO's own default = byte-identical** |
| `IgnoreJunctionBlockerTests` (2) | **live** — A/B on the SumoShim harness |
| `Engine.ContTurnInsideJunctionGate` | **default OFF** — fixes the 95-step freeze; **no longer blocked**, see §6 |
| `Engine.JunctionPhysicalOccupancyGate` | **default OFF** — measured counterproductive three times; do NOT retry alone |
| `F3JunctionOverlapDiagTests`, `Scenario44DefectDiagTests`, `ContTurnFlagOnGridlockDiagTests` | live, always-passing instruments |
| 9 NEED/design docs + design/tasks/tracker + this log | live |
| **Session 3 — SIX GATES, ALL DEFAULT OFF. They are a PACKAGE; partial enablement is measurably worse.** | |
| `NetworkModel.LinkIndexByInternalLane` / `EntryConnectionByLink` (T2.1) | live, unconditional, no sim reader |
| 3 `long` junction timestamps on `VehicleRuntime` (T2.2) | live, written at the lane-advance seam, read only by `isLeader` |
| `Engine.IsLeader`/`ResponseFor`/`IsLeaderByEntryOrder`/`GapForIsLeader` (T2.3/T2.4a) | live |
| `NetworkModel.InternalJunction` / `InternalJunctionByBayLane` / `InternalLaneFoes` (T3.1) | live, unconditional, no sim reader |
| **1** `ContTurnInsideJunctionGate` | **OFF** — faithful bug fix (SUMO tests a lane *property*) ✅ safe to default ON |
| **2** `JunctionIsLeaderGate` (arm 5 disjunction) | **OFF** — faithful `MSVehicle::isLeader` ✅ safe to default ON |
| **3** `InternalJunctionAdmissionGate` (arm 14) | **OFF** — faithful `MSInternalJunction`. **⚠ ONLY safe together with gate 7 below**; alone it creates a 4-way circular wait at 3x (§9.103-109) |
| **7** `InternalJunctionAdmissionEntryOrder` (sub-gate of 3) | **OFF** — restores `isLeader`'s entry-time ordering for a bay-vs-bay foe. **Breaks the circular wait**: longest wedge 4890 → **637** steps, trips **+57%** at 3x from this one variable (§9.114). Residual 9 stalls remain (§9.115) |
| **4** `InsertionFollowerGapCheck` | **OFF** — faithful `isInsertionSuccess` follower pass (SUMO default) ✅ safe to default ON |
| **5** `ColocationSymmetryBreak` (arm 15) | **OFF** — the **one deliberate deviation**; recovers a state SUMO cannot reach |
| **6** `LaneChangeArrivalArbitration` | **OFF** — beneficial only *with* the others; **harmful alone** (3046-step episodes) |
| `Engine.IgnoreJunctionBlockerSeconds` | **−1** = SUMO's own default. **Retracted as a demo tool** (timer-triggered ⇒ rung-5 concealer) |
| `LongHorizonGridlockDiagTests` (hour-long, OFF-vs-ON, in-zone split, episode stats) | **live** — the only diagnostic that sees hour-scale failure |
| `SumoShimEnvCollection` + `AllLiveCityGateVars` | live — the two process-global-env leak fixes |
| ~14 NEED/design/constraint docs + this log | live |

**Demo env gates:** `LIVECITY_{CONTTURNFIX,ISLEADERFIX,INTERNALJUNCTIONFIX,INSERTIONFOLLOWERGAP,COLOCATIONSYMMETRYBREAK,LANECHANGEARBITRATION}=1`
· shim: `SUMOSHARP_{CONTTURNFIX,ISLEADERFIX,INTERNALJUNCTIONFIX}` + `--ignore-junction-blocker`.

### Demo result with all six ON (full hour, deterministic)

| Metric | OFF | ALL ON |
| --- | --- | --- |
| completed trips | 1295 | **2684** (+107%) |
| stopped runs > 300 steps | 161 | **0** |
| total overlap events | 148877 | **1408** (−99.1%) |
| fully co-located, **in-zone** | 17015 | **13** (−99.9%) |
| same-lane overlaps, **in-zone** | 28 | **12** (−57%) |
| teleports | 0 | **0** |

**At 3x density this table used to NOT hold** — 539 stalls > 300 steps remained, 94% of whose heads were
the arm-14 wedge. **Session 4 fixed that** with gate 7: at 3x, trips **1583 → 5381**, peak concurrent deep
stalls **469 → 17**, stall heads **57 → 7**, and the permanent 4890-step bay lock becomes a bounded 637-step
delay (§9.114). A residual **9** bay stalls remain and are §6's open item.

## 6. NEXT ACTION — fix LANE SELECTION (why 22% of our cars need a reroute rescue)

### TRACE-1 is DONE, and it moved the target off junctions entirely

Traced at 1.4 veh/s where both engines are steady (`DENSITY-DIFF-HARNESS-TRACKER.md`, "TRACE-1 RESULT").

| | n | mean delta vs SUMO | share of ALL excess time |
| --- | --- | --- | --- |
| **same route as SUMO** | **77.8%** | **+2.7 s** on 173.5 s = **+1.6%** (median **+0.0 s**) | **5.8%** |
| **rerouted** | 22.2% | **+156.7 s** | **94.2%** |

**When our cars drive SUMO's route, we are at parity.** Our junction and car-following core is not the
problem. The deficit is that **22% of our cars end up on a detour** our engine chose and SUMO never takes.

**And the rescue is load-bearing — it cannot just be removed.** Both mechanisms are individually mandatory
at 1.4 veh/s: trips **4448 → 2119** with `WrongLaneRerouteAtApproach` off, **→ 2126** with
`DeadLaneDriveThrough` off, RUNAWAY either way.

### The root defect, and it is the task

**Our cars routinely fail to reach the lane their turn requires** — badly enough that two separate rescues
are both mandatory, while SUMO needs neither on identical demand and identical routes. That is a
**lane-selection / lane-change** defect, which is exactly why both `jyArm 2` hypotheses failed.

1. Instrument **why** the rescue fires: per event, the vehicle, the junction, the lane it is on vs the lane
   its next connection requires, and how long it had to change. Related: `NEED-multilane-junction-passage.md`.
2. Compare against SUMO on the same approach — SUMO's `getBestLanes` / strategic lane-change urgency is the
   reference. **Trace before porting** (score on reasoned hypotheses is 0 for 2 here, 7 refuted overall).
3. Only then change behaviour, and clear **both** surfaces (all 661 goldens **and** the open-loop discharge
   test).

**Rung-5 note:** the rescue conceals the defect that causes it, so per the ladder the cure is the cause. But
it may not be deleted first — today it is the only thing keeping the demo out of gridlock.

### ⚠️ RETRACTED: §9.127's "our cars roll ~27% slower"

Mean-driven and wrong as a population claim. **Median excess is +2.0 s and 43% of our cars are FASTER than
SUMO's**; the worst 10% carry 73.6% of the excess. The halting-fraction equality (33.3% vs 33.7%) stands.

### Do NOT re-attempt (each disproven, with its measurement)

- `addBlockedLink` — dead code in 1.20.0 · entry-time ordering for **non-bay** foes — provably inert
- `InternalJunctionAdmissionGate` without its entry-order sub-gate — the 4890-step wedge
- Any **capacity** claim from **closed-loop** demand
- **G1 `KeepClearHeldPropagation`** as a discharge fix — measured worse
- **`MinorApproachArrivalSpeed`** — +67% and 14 broken goldens
- **Removing either reroute rescue** — measured: instant gridlock
- **Any junction-yield mechanism hunt** — the same-route population is already at +1.6%

### Hard constraint

SUMO's drain is partly wider because it **lets cars overlap inside junctions** — 26 junction collisions its
own defaults do not check for. Ladder rung 3. **Target SUMO's flow, never SUMO's method.**

## 7. LESSONS / TRAPS (these cost real time — read before investigating)

1. **"All goldens byte-identical" ≠ parity-inert.** The live-city demo and the gridlock diagnostics are
   **not** goldens. A change passed all 661 goldens while moving the demo 61 → 94 overlap events. Always
   measure both.
2. **Verify the instrument before trusting its output.** *Two* instrument-level defects distorted this
   investigation before any engine bug was reached: the OBB half-length anchor (N1) and the stale binder
   diagnostics (T1.8). Both produced confident, wrong attributions.
3. **A confirmed mechanism is not a confirmed cause.** The cont-turn mis-port is real *and* provably
   unrelated to the freeze (trajectory bit-for-bit identical with the fix on). Two premises being true does
   not make the link between them true.
4. **Test hypotheses before coding them.** H3 ("stopped-in-junction dominates") was refuted by measurement in
   minutes; had it been implemented first it would have been days wasted.
5. **`egoOnInternal` has two meanings in the code.** "On the link-controlling lane" (needed for lane-relative
   arithmetic, e.g. `AdaptToJunctionLeader`'s `seen`) vs "inside the junction" (the gating predicate). Do
   not conflate them — flipping the flag naively mixes positions measured on different lanes.
6. `Console.Error` from a test host is swallowed by VSTest — temporary instrumentation must write to a **file**.
7. Backticks in a `git commit -m` string get shell-expanded. Use `git commit -F <file>`.
8. **The measuring apparatus includes the TEST HARNESS, not just the engine.** Lesson 2 said "verify the
   instrument"; three instrument defects were engine/analysis code. The fourth was **process-global
   state in the tests**: `SumoShim` reads env vars to set engine flags, one test sets them, and xUnit
   runs classes in **parallel** — so a test can silently simulate a configuration it did not ask for
   (§9.43). Symptom: a diagnostic failing ~1 run in 3 while passing standalone. Two consequences worth
   internalising: **(a)** "deterministic engine" does **not** imply "deterministic test suite" —
   `Sim.Bench`'s `par == single` stayed green throughout; **(b)** when a test is intermittent, reproduce
   the mechanism **deterministically** (here: set the env var by hand) instead of re-running and hoping
   — the reproduction is what identifies the cause, and re-running only ever gives you a probability.
9. **An intermittent guard is worse than no guard.** A false RED costs a session chasing a regression
   that does not exist, and — worse — it trains the reader to discount the guard exactly when a real
   failure needs to be believed. Fix flakiness in the regression net *before* measuring a behavioural
   change against it, not after.
10. **Before porting a SUMO mechanism, prove it has a LIVE CONSUMER.** A whole session's "primary
    hypothesis" was to port `addBlockedLink`. It is **dead code**: `myBlockedFoeLinks` has exactly one
    reader, and that reader is commented out at **both** of its call sites. Cost of finding out: **one
    grep**. Cost of building it first: a day, plus a null result needing explanation. The mechanical
    check is: grep the writer, grep the reader, grep the **reader's** callers — vendored C++ contains
    plenty of vestigial machinery, and reading `postloadInit` alone makes it look load-bearing.
11. **A symmetric predicate cannot arbitrate a cycle — check for a tie-break, and copy SUMO's.** Bare
    occupancy ("is any foe on a foe lane?") is symmetric, so N mutually-conflicting streams all yield
    forever. Every SUMO junction predicate that could face a cycle carries an explicit ordering, and its
    tie-break chain is total (entry time → speed → **id**) precisely so no pair can mutually yield. SUMO
    even says so in a comment at the branch in question: *"in a mutual conflict scenario, use entry time
    to avoid deadlock"*. **When porting an admission rule, ask what breaks the tie before asking what
    the foe set is** — the four-way wedge was a missing tie-break, not a missing foe set.
12. **A subagent that starts a background job will end its turn and lose the result — collect it
    yourself.** This has now happened **three times** in this workstream: the agent launches a long
    run, says something like *"I'll wait for the notification"*, and terminates — the notification fires
    into a context that no longer exists. **Delegate the BUILDING of an instrument, never the WAITING for
    it.** A delegation that ends in "run it and report the numbers" is a delegation that will come back
    empty after burning its whole budget; one that ends in "build it, verify it compiles, commit it" comes
    back useful. The orchestrator runs the measurement. (Corollary, learned the hard way in the same
    exchange: do **not** hand a subagent a file the orchestrator has uncommitted edits in — the agent left
    a non-compiling call site behind and the tree could not be committed until it was fixed by hand.
    Commit first, *then* delegate.)
13. **Commit the instrument, not just the conclusion.** The head-of-queue probe was a scratch edit, run
    once and reverted, so its numbers had to be taken on trust for a whole session and could not be
    compared against anything afterwards. Re-created as a committed test, it immediately paid twice: it
    **reproduced** the historical wedge (same four bays, same pos, 48.7% vs the reported 48.1%,
    validating the old number) and it revealed that the historical "857 steps" was a mid-run snapshot of
    a **4890-step** lock. A conclusion whose instrument is deleted is unfalsifiable — and cross-instrument
    number comparisons are invalid, so a deleted instrument also silently poisons every later comparison.
14. **Never hand-decode a `response`/`foes` bit mask — the RIGHTMOST character is index 0.**
    `NetworkModel.Bit` is `mask[mask.Length - 1 - link]`. A confident left-to-right hand-decode produced a
    documented conclusion that was the exact **opposite** of the truth (§9.116), and it is the second
    backwards-bit-mask error in this workstream. Call `RespondsTo`, or reverse the string first. The
    aggravating factor: the error flattered the fix being defended, so nothing about the result looked
    wrong.
15. **An occupancy metric is not a causation metric.** The wedge probe recorded *every* occupied foe lane,
    but arm 14 stops at the **first** blocking foe — so "had a bay foe present" read **5 of 9** where the
    causal answer was **0 of 9**. It would have sent the next session to re-open a completed fix. When a
    constraint short-circuits, any "what was present" tally is an **upper bound**; label it as one, and put
    the decisive figure on the last line, not the first. (Same error class as the `downstreamFree` mislabel
    in §9.100 — measuring a different population than the one you name.)
16. **⭐ LABEL EVERY MEASUREMENT WITH ITS DEMAND MODEL — a capacity claim from CLOSED-LOOP demand is
    invalid.** `LiveCitySim` inserts only while `live < CarTargetConcurrent`, so **inflow is throttled by our
    own drain**. Under that model resident count cannot run away, so a discharge deficit *cannot manifest*,
    so the comparison reports "close to SUMO" no matter how narrow the drain is. It produced a confident
    "96% of SUMO" against a parallel open-loop experiment showing us climbing 258 → 2623 and never reaching
    steady state. **Ask what the demand model can physically express before believing what it reports.** The
    generalisation: a closed-loop control system hides the very deficit it compensates for.

## 8. Key code locations

| What | Where |
| --- | --- |
| `JunctionYieldConstraint` | `src/Sim.Core/Engine.cs` ~:6642; foe loop ~:6890; cautious-approach arm gate ~:6832 |
| `egoOnInternal` / `egoInsideJunction` | `Engine.cs` ~:6711 |
| `AdaptToJunctionLeader` | `Engine.cs` ~:7934 (+ `FoeIsInTheWay` just above) |
| `KeepClearConstraint` | `Engine.cs` ~:7345 |
| Binder diagnostic write | `Engine.cs:5183` (`if (!prePass)`) |
| `ReuseIntent` skip | `Engine.cs:4955`, `:4972` |
| `JunctionByInternalLane` build | `src/Sim.Ingest/NetworkParser.cs`, after the junction loop |
| `IsInternalLaneOfJunction` | `src/Sim.Ingest/NetworkModel.cs` (on the record) |
| SUMO refs | `sumo/src/microsim/{MSLink,MSVehicle,MSLane,MSEdge.h,MSInternalJunction}.cpp`, `sumo/src/netwrite/NWWriter_SUMO.cpp:634-649` |

## 9. Chronological log

### Session 1 (2026-07-26)

1. **Repro confirmed exactly** — worst 3.035 m, `__veh134/__veh38`, step 197. Baselines: parity 661/4/0,
   bench `D96213B7BB4021A7`. Found the drcheck tools missing (§4.1).
2. **Root cause of true F3 found** (design §2): SUMO keeps two foe sets — `myFoeLinks`←`response`
   (arbitration) and `myFoeLanes`←`foes` (physical), `MSRightOfWayJunction.cpp:92-146`; `checkLinkLeader`
   gates on `isLeader(...) || inTheWay()` (`MSVehicle.cpp:3429`). We collapsed both into `RespondsTo`
   (`Engine.cs:6892`), making the occupancy arm unreachable for a non-yielded foe.
3. **Lane-classified the 61 overlaps** → F3 is 8 of 61 (§3). Found N1/N2/N3.
4. **Attempt 1** (`FoeWith` + `inTheWay`): F3 bucket 8 → 33, 5 gridlock tests red. **Worse.**
5. **Attempt 2** (+ `isLeader` first clause): 8 → 27. **Still worse.** → flag OFF.
6. **Discovered** goldens-only evidence is insufficient (§7.1): the skip guards moved the demo 61 → 94 while
   all 661 goldens stayed byte-identical.
7. **F4b premise disproven** (§4.4) — zero OBB overlap is stronger than SUMO parity.
8. **H3 refuted before coding**: F3 bucket is 62% both-moving; stopping-in-junction is 2.2% of all stopping.
   That measurement surfaced the 95-step freeze (veh127) as the biggest single lead.
9. **User suggested differential-vs-SUMO analysis** — highest-yield move of the session. Yielded:
   (a) the cont-turn `egoOnInternal` mis-port, traced to `NWWriter_SUMO.cpp:634-649`;
   (b) scenario 44's ballistic golden bug, found by running the real 1.20.0 binary.
10. **Implemented the cont-turn fix** — `JunctionByInternalLane` + `IsInternalLaneOfJunction` + 9 direct
    tests + `ContTurnInsideJunctionGate` (OFF: enabling regresses `RungHDp2g2` 1 → 28).
11. **Sharpened experiment on the freeze** → **both H-A and H-B refuted**; `junction.Id == d_3_4`,
    `seen = 0.1010` (not 7.37), `LaneSeqIndex` no lag, **arm never fired (95/95)**, trajectory identical with
    the fix on. Found the **stale-diagnostics bug** (T1.8) that had produced the original attribution.
12. **Corrected the docs** to mark the refuted attribution and removed contradictory tracker entries.

**State at end of session 1:** gate green (671/4/0, `D96213B7BB4021A7`, 48/48, 272/272). One genuine engine
correctness fix shipped (flag-gated) + 9 tests. F3 itself **not fixed**. Freeze **unexplained**. Next: T1.8.

### Session 2 (2026-07-26, continued) — T1.8 + T1.9: the freeze is FIXED

13. **T1.8 done — stale binder diagnostics fixed.** Removed the `!prePass` guard from all four diagnostic
    writes (`v.BindingConstraint`, `v.JunctionYieldArm`, and both `v.JunctionYieldFoeSpeed` sites). The
    pre-pass runs first and the real pass overwrites it, so a normal vehicle is unchanged, while a
    `ReuseIntent` vehicle (whose pre-pass Intent IS its final Intent) now reports its live binder. The
    genuinely *behavioural* `!prePass` guards (`LastActionTime`, `CoopSpeedAdvice` reset, IDMM state,
    `LatOffset`, the crossing-yield relax) were left untouched. Parity 671/4/0 and hash unchanged, exactly
    as predicted — the fields are never read by the sim.
14. **T1.9 SOLVED, immediately, because of T1.8.** With live diagnostics `__veh127`'s arm is
    **3 (sameTargetMerge)**, not 2 (cautiousApproach) — 95/95 steps. The real cause is
    `SameTargetMergeConstraint`'s **PHASE 0** stop-line arrival-time yield, gated on `!egoOnInternal` with
    the same false precondition ("once ego is on its internal lane it is committed and no longer gated").
    PHASE 0 is a *stop-line* yield, so on a cont turn it brakes an ego already committed inside the junction
    toward an entry that is **behind** it. **The cont-turn mis-port WAS the cause after all** — via the merge
    arm, which the first fix never reached because `egoOnInternal` was passed into it.
15. **Fixed** by threading a separate `egoInsideJunction` parameter into `SameTargetMergeConstraint`, used
    **only** for the PHASE 0 gate (`distToMerge` keeps `egoOnInternal`, being lane-relative). Measured with
    the flag ON: `__veh127`'s 95-step and `__veh140`'s 75-step stalls both **GONE**; total vehicle-steps
    stopped on an internal lane **206 → 39 (−81%)**.
16. **The T1.7 blocker dissolved.** `willpass-saturation` with the flag ON: stuck **28 → 0**, arrivals
    411 → 411. The earlier regression was an artefact of gating only one of the two arms, not a missing
    `checkRewindLinkLanes`.
17. **Probed default-on:** all 661 goldens byte-identical, but `LowDensityTeleportTests` fires 5
    yield-teleports vs a ceiling of 2 → reverted to default OFF and filed as **T1.10**, the last blocker.

**State at end of session 2:** gate green (**672/4/0**, `D96213B7BB4021A7`, 48/48, 272/272). Two genuine
engine fixes shipped (one unconditional and parity-safe, one flag-gated). **The 95-step mid-junction freeze
is FIXED** and the biggest overlap contributor is neutralised. F3 proper (the 8 both-moving events) still
open. One narrow blocker (T1.10) between the cont-turn fix and default-on.

### Session 2 (continued) — T1.10 diagnosed

18. **T1.10: the blocker is not the flag.** Differential run against real SUMO 1.20.0 (with
    `--step-method.ballistic false` added, since this scenario's `sumocfg` omits it — the scenario-44 trap
    again): SUMO fires **0** teleports and **all five** of our ON-teleporting vehicles complete their routes.
    Vehicle 101 teleports identically OFF and ON, so it is an unaffected control.
19. **Root causes found, all pre-existing:** (D1) `Engine.cs:10169` hard-returns from the stuck-reroute when
    the vehicle is on an internal lane, so an in-junction wedge can never be rescued — 2 of 5 vehicles;
    (D2) the rescue is one-shot and re-plans only the future route without unsticking the vehicle — 2 more;
    (D3) the open and probably decisive question — our yield wait exceeds 120 s where SUMO's equivalent
    resolves in ~10 s, which looks like a THIRD mis-gated `!egoOnInternal` release path.
20. **Decision: flag stays default-OFF and the `<= 2` ceiling is NOT raised.** Raising a shared guard to
    accommodate an opt-in path would blind the default path. Filed as
    `docs/NEED-stuck-reroute-blind-inside-junctions.md` with the fix order D3 → D1 → D2, on the principle
    that fixing the cause beats widening the mitigation.

**State:** gate green (**672/4/0**, `D96213B7BB4021A7`, 48/48, 272/272). The cont-turn fix is complete and
correct; what blocks default-on is a separate, now precisely-characterised rescue gap.

### Session 2 (continued) — D3 refuted

21. **D3 refuted.** Found and corrected four more `!egoOnInternal` commitment gates — cycle-hold,
    the approaching-foe `takesCrossingYield`, the external-agent arm, and the `foeWillNotPass` probe. The
    `takesCrossingYield` one looked compelling: it brakes to `approachLane.Length - Pos`, and on a cont turn
    `approachLane` IS the lane ego stands on, so at its far end that is a ~0.1 m stop target re-applied every
    step — and the teleports are classified `Yield`. **Teleports still 5.** Goldens byte-identical, T1.9
    freeze fix unaffected (206 → 39 holds), so the four gates were kept as a consistency fix, explicitly not
    as T1.10 progress. The teleports are therefore genuinely D1/D2 (rescue coverage): our rescue cannot reach
    a vehicle wedged on an internal lane (`Engine.cs:10169` hard `return`), and where it does fire it never
    unsticks the vehicle.

**State:** gate green (**672/4/0**, `D96213B7BB4021A7`). Three hypotheses refuted by measurement this
session (H3, H-A/H-B, D3) and two real fixes shipped. Next: **D1** — let the stuck rescue act on an
internal-lane wedge.

### Session 2 (continued) — T1.10 root cause NAMED

22. **The `Yield` teleport label is not a cause.** `ClassifyTeleportKind` (`Engine.cs:~12359`) returns
    `Yield` iff the next junction link's TL state char is lowercase (minor), `Jam` if uppercase. It never
    inspects why the vehicle waited — faithful to SUMO, but it means "yield=5" says only "these 5 vehicles'
    next link is minor". D3's framing ("why is a *yield* wait > 120 s") rested on a premise the counter never
    attested, which retro-explains why fixing four yield-path gates could not move it.
23. **Instrumented the real binder** (trustworthy only because of T1.8) and the cause split cleanly in two:
    - **95 + 102: mutual arm-5 deadlock** at junction 2336 — binder 10 / arm 5, 121/121 steps, speed exactly
      0.000, each verified as the other's foe. → `NEED-arm5-mutual-junction-deadlock.md`.
    - **14 + 317: `redLight` 77.5%** — and both **wasted a real green window** (9–11 steps at `tl=G`) held at
      0 speed by `deadLaneMerge`/`crossJxnLeader` before the next red timed them out. Separate defect.
24. **D3's open question closed:** SUMO breaks this deadlock in **5 s** via `JUNCTION_BLOCKAGE_TIME`
    (`MSVehicle.cpp:119`/`:3487`) and can skip a long-waiting foe outright (`MSLink.cpp:1601`). We have
    neither, so we wait 120 s for the teleport. That is the whole of the ">120 s vs ~10 s" gap.
25. **Convergence worth noting:** `isLeader()` is now the single highest-value outstanding port — it is what
    T1.6 (true-F3 residue) needs AND what prevents this deadlock forming. One piece of work, two blockers.

**State:** gate green (**672/4/0**, `D96213B7BB4021A7`, 48/48, 272/272), tree clean. Four hypotheses refuted
by measurement this session (H3, H-A/H-B, D3, and the D1/D2-primary framing), two fixes shipped, one 95-step
freeze eliminated, and T1.10's cause now identified with citations rather than inferred.

### Session 2 (continued) — owner's tiered-rescue idea, and a THIRD instrument bug

26. **Owner proposed a tiered blockage rescue** (low realism: SUMO's 5 s let-go; high realism: brief
    ORCA mode + cooperation, "one waits while the other passes"; **trigger only when cars physically
    overlap**). Captured and assessed in `docs/DESIGN-NOTE-tiered-junction-blockage-rescue.md`.
    Assessment: the overlap trigger is the strongest part — it is what stops a rescue becoming a bug
    concealer (`__veh127` was stuck with NOTHING overlapping it; an ORCA rescue there would have masked the
    mis-gate). `OrcaCrowd` is already deterministic/order-independent and the cross-regime bridge works both
    ways, so the obstacles are (a) re-entry inverts the lane-authoritative data flow that
    `LaneGeometry.cs:7-9` forbids, and (b) ORCA is holonomic/disc while a car is a non-holonomic box.
    "Both turn to unblock" likely needs REVERSE (we have none); "one waits, the other passes" is pure
    sequencing and is the cheap workable half.
27. **Measured the trigger on our one confirmed deadlock: veh95/veh102 do NOT overlap.** Penetration
    **0.0000 m**, box gap **2.9866 m**, and both cars stopped *short* of the crossing point (1.387 m and
    2.733 m). The lanes do cross (`JunctionConflict EgoLink=3/FoeLink=18`) but the cars never reach it.
    **So the ORCA tier is not triggered by any case measured so far** — `isLeader()` + the 5 s timeout is the
    correct fix, and the ORCA tier is speculative machinery awaiting a demonstrated trigger.
28. **THIRD instrument bug found (see §4.5b): the OBB forward axis is a reflection.** Correct at 0°/90°,
    perpendicular at 45°. Curved junction lanes are exactly the failing case. It gave a false-positive
    0.328 m overlap in this measurement. **This is now the highest-priority fix**, because every overlap
    number this session rests on it.

**State:** gate green (**672/4/0**, `D96213B7BB4021A7`), tree clean. **Three** instrument-level defects found
this session (OBB anchor, stale binder diagnostics, OBB forward axis) — each produced confident wrong numbers
before any engine bug was reached. Next: fix the OBB helper (axis + anchor together, re-baselining thresholds),
then the 5 s `JUNCTION_BLOCKAGE_TIME` escape, then `isLeader()`.

### Session 2 (continued) — the 5 s escape WORKS; T1.10 blocker resolved

29. **Fixed the OBB helper properly**: `src/Sim.Ingest/VehicleObb.cs` (one implementation, beside
    `LaneGeometry` which owns the convention) + `VehicleObbConventionTests` (15 tests) which derives the
    tangent from `LaneGeometry` by finite difference across **2153 real internal lanes** rather than
    restating the formula, plus a test asserting the *reflected* basis is perpendicular at 45° so the guard
    cannot go vacuous. Both consumers migrated; the obsolete front-anchor/centre-corrected A/B deleted (it
    varied the anchor while leaving the axis reflected, so neither variant was ever right).
30. **Corrected F3 numbers** — see §3. Total 61→45; F3 bucket **8→15** (the reflected axis had been HIDING
    seven real overlaps while manufacturing one deep false positive); `ONE-INTERNAL-ONE-NORMAL` 31→8; both
    `BOTH-NORMAL` buckets unchanged (the consistency check — provably inert there).
    `__veh134/__veh38` peaks at **1.022 m**, not 3.035 m.
31. **Corrected my own claim about SUMO**: `--ignore-junction-blocker` defaults to **−1 = never ignore**
    (`MSFrame.cpp:370-371`, −1 → `SUMOTime::max()` at `:1043`), and `JUNCTION_BLOCKAGE_TIME` prevents
    *entering* behind a long-waiting leader rather than freeing a car already inside. So SUMO does NOT break
    this deadlock by default; it avoids it via `isLeader()`. Enabling the knob is a SUMO-optional deviation.
32. **Ported the option** (`Engine.IgnoreJunctionBlockerSeconds`, CLI `--ignore-junction-blocker`, cfg
    `<processing>` element) **including its −1 default, so the default path is byte-identical.**
33. **A first A/B was invalid** (direct `engine.Run()` vs the shim's config path — different baselines,
    4 vs 2). Redone on the shim path: **teleports 5 → 2**, and vehicles **95 and 102 now arrive** (647 s,
    587 s) where they previously never did. `WaitingTime` verified to accumulate inside junctions.
34. **T1.10 RESOLVED.** With both `IgnoreJunctionBlockerSeconds=5` and `ContTurnInsideJunctionGate=true`:
    661 goldens byte-identical, all 5 gridlock diagnostics green, hash unchanged, LiveCity 48/48. Left at
    shipped defaults pending an owner decision, since flipping changes outward-facing defaults.

**State:** gate green (**689/4/0**, `D96213B7BB4021A7`, 48/48, 272/272). **The cont-turn fix is now
unblocked** — the last obstacle to enabling it is a defaults decision, not a defect. Remaining faithful work:
**`isLeader()`** (unblocks T1.6 too), plus N2 (co-located vehicles), N3 (net geometry), scenario-44 golden.

### Session 3 (2026-07-26, continued) — the `isLeader` port: design, Stage 1, T2.3

Working docs: **`docs/F3-ISLEADER-PORT-{DESIGN,TASKS,TRACKER}.md`** (the design trio, written before any
source edit per CLAUDE.md). This log stays the running record; the trio holds the detail.

35. **Baseline re-confirmed first-hand** before starting: 689/4/0, `D96213B7BB4021A7` par==single,
    48/48, 272/272. Environment was already provisioned (dotnet 8.0.129, SUMO 1.20.0 on `PATH`).
36. **The deadlock pair is a TWO-STAGE CONT turn**, established from the real net rather than assumed:
    link 18 is `2417 → :2336_18_0 → [internal junction :2336_42_0] → :2336_42_0 → -2337`, and
    **`intLanes[18]` is the STAGE-2 lane** while stage-1 `:2336_18_0` is absent from `intLanes`.
    Consequently `JunctionLink.Connection` (resolved by `Via == intLanes[i]`) is the **second hop**,
    carrying **no `tl`/`linkIndex`** (static `'m'`), while the entry hop carries `tl=2336, linkIndex=18,
    state='o'`. This is exactly why SUMO has `getCorrespondingEntryLink()`.
37. **CORRECTED an assumption:** `isLeader`'s To/From junction asymmetry is **stylistic, not
    functional** — `NLHandler.cpp:431-445` sets *both* endpoints of an internal edge to the same
    junction, for both cont stages. So **one** predicate (`IsInternalLaneOfJunction`) is right for ego
    and foe, and no cont special case is needed.
38. **PROOF that this port resolves the measured deadlock — and a correction of my own framing.** I first
    justified the port via the mutual-response *matrix* bits. Measuring which arm actually runs showed
    junction `2336` **never** shows links 3 and 18 non-red simultaneously (0 of 12 phases), so
    **attempt 1 (`haveRed`) fires in 121 of 121** deadlock steps and the matrix is **never reached**.
    The matrix bits are real but inoperative here. Working attempt 1's stuck-foe arm across the cycle:
    both-red 75 steps (→ mutual branch, both use `CET`), link-3-red-only 26, link-18-red-only 20,
    neither-red **0**. In every class the pair compares **the same two numbers in opposite senses**, so
    the result is **antisymmetric — exactly one yields, never both**; on a tie it falls to the id, and
    `CompareOrdinal("102","95") < 0`. **The symmetric state is structurally unreachable.**
    ⇒ **Attempt 1 is MANDATORY, not stageable.** T2.3's success condition had to be rewritten: as first
    drafted it asserted the matrix bits and **would have passed while testing the wrong branch.**
39. **The yield-request reset (`MSVehicle.cpp:3720-3731`) is deliberately NOT ported, with the trap
    recorded.** `mySetRequest` includes `leavingCurrentIntersection`, so an in-junction car keeps its
    request even at speed 0 and the reset fires only on *spillback*. Our `WillPass` explicitly
    **excludes** that disjunct, so wiring the reset to `!WillPass` would blank `ET`/`CET` for **every**
    stopped in-junction car and **destroy the ordering the port establishes**. It is also not what the
    measured deadlock needs (2.99 m clear gap; both cars stopped short of the crossing).
    → `NEED-yield-request-reset-unported.md`.
40. **Blast radius quantified over all 134 committed nets** instead of guessed: mutual-response pairs
    **2599 / 93961 (2.8%)**, confined to **12** nets; **26** nets contain cont links (8623 cont request
    rows, so the two-stage logic is broadly load-bearing); **0** nets contain an indirect link
    (validating that omission). And since clause 1 makes a not-yet-on-junction vehicle always yield —
    today's arm-5 behaviour — **the behavioural delta is confined to ego already INSIDE the junction.**
41. **T2.1 + T2.2 shipped and confirmed** (parity-inert). T2.2's inertness was confirmed
    **structurally**, not just numerically: a field-read audit showed every read of the three
    timestamps is either in a test-only accessor (sole caller: the test file) or a self-contained
    read-then-write. Measured cont trace matches the design exactly — `CET` holds `MAX` for 119 steps in
    the stage-1 bay, then stage 2 stamps `CET=439` while `ET` renews from `ETN=320`.
    Review caught a weakness worth noting: the all-nets sweep wrapped each parse in a `catch` and
    asserted only `checkedLinks > 0`, so a parser regression would have **skipped every in-loop
    assertion while still passing**. Floors re-derived from the corpus (134 nets, 2927 RoW junctions,
    37426 `intLanes` entries).
42. **T2.3 shipped** — `IsLeader` / `IsLeaderByEntryOrder` / `ResponseFor`, **297 insertions, 0
    deletions**, so arm 5 is byte-for-byte unchanged and nothing calls it. Predicates verified against
    source rather than names: `haveRed = 'r'|'u'`, `haveYellow = 'y'|'Y'`, `havePriority = 'A'..'Z'`
    (`MSLink.h:437-454`, `SUMOXMLDefinitions.h:1695-1701`); `HaltingSpeed 0.1` = `SUMO_const_haltingSpeed`.
    Note `'Y'` ∈ `'A'..'Z'`, so a `Y`-vs-`y` pair resolves at attempt 2 before attempt 3 — SUMO's own
    ordering, so faithfulness falls out. `VehicleRuntime` is `internal`, so a public
    `JunctionLeaderCandidate` projection exists to make the helpers testable; `IsLeader` takes
    `NetworkModel` **explicitly** (not `_network`) for `ReferenceEquals` consistency — **T2.4b must pass
    `this._network`.**
43. **⚠ A REAL CROSS-TEST RACE, found and fixed — the most consequential finding of this session.**
    `LowDensityTeleportTests` failed **1 of 3** full-suite runs ("fired 5 teleports … should hold it at
    <= 2") while passing every standalone run. **Not** engine non-determinism — `Sim.Bench`'s
    `par == single` was green throughout. Cause: `SumoShim.cs:250` reads the **process-global** env var
    `SUMOSHARP_CONTTURNFIX`; `IgnoreJunctionBlockerTests` **sets** it; xUnit runs separate collections in
    **parallel** and a class with no `[Collection]` forms its own — so five reader classes could run
    concurrently with the one writer. Pinned **deterministically**, not by re-rolling:
    `SUMOSHARP_CONTTURNFIX=1 … --filter LowDensityTeleportTests` **fails with that identical message**;
    unset, it passes. **5 is exactly the cont-turn-gate-ON count** for that scenario, which identified
    the cause rather than merely correlating with it.
    **Why it mattered:** that test and `DenseFlowDeadLaneDrainTests` are two of the five gridlock
    diagnostics — this repo's regression net for junction changes — and T2.4b, the first behavioural
    change of this port, is judged against them. A false RED costs a session chasing a phantom
    regression and teaches the reader to discount the diagnostic exactly when a real failure needs
    believing. It is also the test blocking `ContTurnInsideJunctionGate` from default-ON.
    **The race was introduced earlier in this same session by my own `IgnoreJunctionBlockerTests`.**
    Fixed by serialising all six `SumoShim.Run` classes into `SumoShimEnvCollection`. The
    process-global read itself remains a latent hazard →
    `NEED-sumoshim-process-global-contturn-env.md` (which also says the `[Collection]` serialisation
    should be **removed** when the seam is fixed properly, so the coupling is proven gone, not contained).
44. **Two more traps found in the `gap` derivation** (`MSLink.cpp:1376-1647`), both recorded in design
    §3b before delegating: (a) `getLeaderInfo`'s `sameSource` uses **`getLogicalPredecessorLane()`**
    (one hop) while `isLeader`'s same-source test uses **`getNormalPredecessorLane()`** (recursing past
    internal lanes) — same-sounding names, different predicates, must not share a helper; (b) the
    `contLane ⇒ gap = -DBL_MAX` rule is **not cosmetic and is live for veh 95** (it sits on a cont
    continuation lane), and omitting it flips attempt 1's answer.
45. **Two net-shape findings from the implementation:** `:2336_42_0`'s downstream edge `-2337` is
    **0.20 m**, so a car crosses two junction boundaries in one step and stamps both entry times — SUMO
    does the same (`enterLaneAtMove` twice, one `getCurrentTimeStep()`); and an **internal junction can
    carry a vestigial `intLanes`** naming a lane owned by a *different* real junction (`:J_2_0`,
    scenario 41), so corpus sweeps must scope to `junction.Links`, not raw `IntLanes`.
46. **F3 overlap baseline re-measured** (not recalled) for T2.5's A/B: `BOTH-INTERNAL-DIFFERENT-LANE`
    **15** (3 stopped-foe worst 2.382 m / **12 both-moving** worst 1.831 m), `ONE-INTERNAL-ONE-NORMAL`
    8, **206** stopped vehicle-steps on internal lanes. Several both-moving pairs have **identical
    speeds** (2.600/2.600, 3.900/3.900), so the tie-break's speed-equal rung — and the ordinal-id
    compare beneath it — is reachable in practice.

47. **T2.4a/T2.4b shipped** (`12e441d`): the gap helper, plus `JunctionIsLeaderGate` (default OFF) wiring
    `isLeader` into arm 5. Flag-OFF preserved **by construction** — the sole deleted line is
    `if (!respondsTo` → `else if (!respondsTo`, condition and body byte-identical — and measured
    byte-identical on all four surfaces (721/4/0, hash unchanged, 48/48, 272/272).
48. **⚠ T2.5: THE PORT DOES NOT ACHIEVE ITS GOAL. Measured, reported, not dressed up.** Flag ON is
    *safe* everywhere — no golden moved, all five gridlock diagnostics green, hash unchanged,
    LiveCity/Pedestrians green — but it delivers **neither** outcome it was chosen for:

    | Metric | Flag OFF | Flag ON |
    | --- | --- | --- |
    | `synthetic-junction2` teleports (cont-turn ON, ignore-blocker −1) | 5 | **4** |
    | veh 95 / 102 arrive? | no | **still no** |
    | F3 `BOTH-INTERNAL-DIFFERENT-LANE` | 15 (12 both-moving) | **identical** |
    | internal-lane stopped vehicle-steps | 206 | 204 |

49. **ROOT CAUSE TRACED — and it is NOT `isLeader`.** Instrumented the arm-5 gate and `IsLeader`'s
    return points, flag ON, over the 120-step window (t=322…441; binder `10`/arm **5** on 120/120 for
    both vehicles). Per direction, **100% of 121 steps each, not a mix**:

    | ego | `IsLeader` | `FoeIsInTheWay` | gap | `contLane` forced? |
    | --- | --- | --- | --- | --- |
    | 95 (foe=102) | **true** 121/121 | false 121/121 | −12.186 | no, 0/121 |
    | 102 (foe=95) | **false** 121/121 | **true** 121/121 | −9.486 | no, 0/121 |

    **The ordering works exactly as §0a proved** — `IsLeader(102,95)` is false every step, so 102 *is*
    released by entry order, and the branch mix (mutual 74, `!response` 26/20, default 21/27) matches
    the design's own table. But the call site is SUMO's own disjunction
    `isLeader(...) || inTheWay()` (`MSVehicle.cpp:3429`), and `FoeIsInTheWay(102,95)` is independently
    true every step — a **symmetric geometric fact that no ordering can dissolve.** So the OR stays true
    both ways and both cars keep braking. **A correct mechanism, applied where it cannot help.**
50. **The real defect: `MSInternalJunction` is unported — a cont turn's SECOND STAGE has no admission
    control.** The question the trace forces is why veh 95 is on stage 2 (`:2336_42_0`) at all while 102
    occupies the conflicting `:2336_3_0`. In SUMO it cannot be:
    `MSInternalJunction::postloadInit` makes the **first** `incLanes` entry — the stage-1 **bay**
    `:2336_18_0` — *"the link that needs to do all the checking"*, takes the **parent** junction's
    `getResponseFor(ownLinkIndex=18)`, and every internal lane of the internal junction that
    `response[18]` responds to becomes a foe the bay link must respect. `:2336_42_0`'s `intLanes`
    contains **`:2336_3_0`**, and `response[18]` has **bit 3 set** (established at the start of this
    workstream). **So SUMO holds 95 in the bay; it never becomes an obstacle.**
    We model none of it: all **251** internal junctions in this net carry **zero `<request>` rows**, and
    `NetworkParser.ParseJunction` bails on `requestEls.Count == 0`, so every internal junction parses
    **inert**; `grep MSInternalJunction src/` finds only comments. A cont vehicle advances bay→stage-2
    **checking no foe at all.** → `NEED-internal-junction-second-stage-admission.md`.
    **This also corrects `NEED-arm5-mutual-junction-deadlock.md`**, which concluded *"the only reason
    SUMO does not hit this deadlock is `isLeader()`"*. SUMO has **two** defences and we had ported
    neither; `isLeader` was merely the visible one. **Keep `isLeader` — it is necessary, faithful, safe,
    and its release demonstrably fires — then port the admission control that is actually load-bearing.**

### Session 3 (continued) — `MSInternalJunction`: **THE DEADLOCK IS FIXED**

Design: `docs/F3-INTERNAL-JUNCTION-DESIGN.md`.

51. **Designed the port and CORRECTED MY OWN NEED DOC.** The NEED doc (written an hour earlier) said the
    foe set is *"every `intLanes` lane whose link index is set in the parent's response row"*. **Wrong,
    and wrong in the direction that matters** — it makes the load-bearing case depend on a bit never
    consulted for it. `MSInternalJunction.cpp:78-95` is **two-branch**:
    - a **plain** internal lane (does not lead to another internal lane) ⇒ **ALWAYS a foe**;
    - a cont **stage-1 bay** lane ⇒ foe **only if** `response[ownLinkIndex]` responds to it (SUMO's
      comment: *"only respect vehicles **before** internal junctions if they have priority"*);
    - a cont candidate's **stage-2** lane ⇒ **ALWAYS a foe** regardless.

    Verified on `:2336_42_0`: **13 unconditional** + `:2336_44_0` (stage 2 of `:2336_25_0`, whose bay is
    *excluded* since `response[18][25]` is false) = **14 lanes**. **`:2336_3_0` — veh 102's lane — is
    UNCONDITIONAL**, so the deadlock is prevented without consulting the response matrix at all.
    T3.1's success condition 2 was written specifically so a test **cannot pass under the wrong rule**
    (it must pin `:2336_25_0` ABSENT while `:2336_44_0` is PRESENT); checking only the 13 plain lanes
    would have passed either way.
52. **T3.1 shipped and confirmed** (`b8e0f19`) — internal junctions parsed at last (they were discarded
    entirely: `ParseJunction` bails on `requestEls.Count == 0` and **all 251** internal junctions here
    have zero `<request>` rows). Parity-inert: reader audit found **no consumer outside the ingest
    layer**, and the only two deletions in the diff are parameter-list terminators being extended. The
    implementor read the C++ directly and **independently confirmed the two-branch correction**.
    725/4/0, hash unchanged, 48/48.
53. **⭐ T3.2 shipped — THE DEADLOCK IS FIXED** (`6e4e299`). New arm 14: a car on a cont **stage-1 bay**
    lane is held there while any of its internal junction's `InternalLaneFoes` is physically occupied.
    Flag OFF is byte-identical **by construction** (first statement returns `+∞`, so `Math.Min` is
    untouched). Verified **first-hand**, `synthetic-junction2` 2000 s via `SumoShim.Run`, all three
    gates ON, `IgnoreJunctionBlockerSeconds = -1` (**SUMO's own default — no knob**):

    | | Result |
    | --- | --- |
    | veh 95 held in bay while 102 occupies `:2336_3_0` | **YES**, and **0** violation steps |
    | teleports | **2** (jam=0, yield=2) — the ≤ 2 ceiling |
    | veh 95 | **arrives t=427** (real SUMO 433) |
    | veh 102 | **arrives t=677** (real SUMO 497) |
    | five gridlock diagnostics | all green |
    | goldens | byte-identical |

    The load-bearing test asserts the **trajectory directly** and **guards its own non-vacuity**: it
    requires veh 95 to have been *observed* on the bay lane while 102 occupied the foe lane (proving the
    gate engaged) **before** asserting zero steps of the bad state. Without that first assertion it would
    pass trivially if 95 never approached — the shape of mistake that let `isLeader` look correct while
    fixing nothing.
54. **Wired the new gates into `LiveCitySim` and measured the F3 buckets PROPERLY** (`7887786`). T3.2 had
    reported the buckets *"identical to baseline"* — but that was an **UNMEASURED condition masquerading
    as a neutral result**: `InternalJunctionAdmissionGate` was never wired into `LiveCitySim`, so the demo
    could not exercise it whatever was enabled. With all three gates ON:

    | Demo metric (200 steps) | gates OFF | all gates ON | |
    | --- | --- | --- | --- |
    | vehicle-steps stopped on a `:` lane (A2) | 206 | **94** | ⬇ **−54%** BETTER |
    | max overlapping pairs / frame | 4 | **3** | ⬇ BETTER |
    | deepest BOTH-MOVING penetration | 1.831 m | **0.639 m** | ⬇ **−65%** BETTER |
    | total overlapping-pair events | 45 | **51** | ⬆ **+13%** WORSE |
    | `BOTH-INTERNAL-DIFFERENT-LANE` total | 15 | **20** | ⬆ WORSE |
    | — BOTH-MOVING count | 12 | **17** | ⬆ WORSE |
    | — STOPPED-FOE | 3, worst 2.382 m | 3, worst 2.382 m | unchanged |
    | **worst penetration overall** | **2.382 m** | **2.382 m** | **unchanged** — same pair, same step |
    | distinct (veh, `:`lane) stopped pairs (A1) | 14 | **22** | ⬆ but see below |
    | distinct `:` lanes hosting a stop (A4) | 13 | **18** | ⬆ but see below |

    **A1/A4 up while A2 halves is the mechanism confirming itself:** stops become MORE NUMEROUS but MUCH
    SHORTER — which is precisely what an admission gate does (it adds brief holds in bays and removes long
    wedges). 22 distinct stopped pairs across only 94 vehicle-steps averages ~4 steps each, against 14
    pairs across 206 steps (~15 steps each).

    **So the SECOND goal of this workstream is NOT met as specified.** The design's criterion was
    *"expect the 12 both-moving events to drop"*; the **count ROSE to 17**. What improved is *severity*
    (deepest both-moving −65%) and stopping-inside-junctions (−54%). Plausible reading — admission control
    keeps traffic **moving** through junctions, so more cars are inside one at once (more shallow events)
    while none wedges deeply — but that is a **reading, not a measured causal claim**. This is exactly the
    trade design §6.3 predicted ("may trade deadlock for junction overlap"), so it is reported, not spun.
    **Unexplained:** a new STOPPED-FOE pair absent from the baseline (`:d_3_4_10_2`/`:d_3_4_13_0`,
    1.959 m). With all gates ON, LiveCity is still **48/48** — `DemoCarOverlapInvariantTests` holds
    because 20 events spread across frames stay inside its ≤ 7 pairs/frame and 3.0 m ceiling.

### Session 3 (continued) — ⭐ THE LONG-HORIZON MEASUREMENT: the demo's real failure, and the real result

55. **The owner supplied the actual acceptance criterion — believability — and three observed symptoms:**
    (a) after ~an hour of sim time, all cars queued at junctions blocked by cars stuck in each other,
    **blocked forever, no fallback/teleport/unblock**; (b) cars driving **through** each other, e.g. from
    two directions into the **same exit lane**, including in the high-realism zone; (c) a long queue in
    **one lane** while the parallel same-direction lane is free. The owner then clarified that **(c) is a
    CONSEQUENCE of (a)** — it would never show if junctions did not block forever.
56. **⚠ EVERY demo diagnostic in this repo runs 200 STEPS** (`F3JunctionOverlapDiagTests.cs:213,478,892`,
    `DemoCarOverlapInvariantTests`). At dt=0.5 s that is **100 seconds** — against an hour-scale failure.
    **So this branch's own "45 → 51 overlap events" comparison was measuring the wrong horizon**, and any
    believability claim resting on it was unsupported. This is the session's biggest methodological miss.
57. **Teleport is DISABLED in the demo** — SUMO's default is 300 s and "non-positive disables"
    (`MSFrame.cpp:412`); our parser defaults to **−1.0** when the element is absent
    (`ScenarioConfigParser.cs:45`), `LiveCityConfig.TimeToTeleportSeconds` is **0.0**, and `LiveCitySim`
    emits the element only when `> 0`. Every teleport path is gated on `TimeToTeleport > 0.0`. So the
    failure mode is **absorbing**: the city can only accumulate wedges, never clear one.
    → `NEED-livecity-teleport-safety-net-disabled.md`.
58. **⭐ MEASURED OVER A FULL HOUR (7200 steps @ dt=0.5), gates OFF vs ALL THREE ON.** Teleports **0 in
    both**, so every improvement below comes from the gates, not from a safety net. Reproduced identically
    on a second run (deterministic):

    | Metric | gates OFF | all gates ON | |
    | --- | --- | --- | --- |
    | horizon reached | 7200 | 7200 | both a full hour |
    | **completed trips** | 1295 | **2709** | ⬆ **+109%, more than DOUBLE** |
    | **stopped runs > 300 consecutive steps** | **161** | **0** | ⬇ **ELIMINATED** |
    | stopped from some step through to horizon | 156 | **59** | ⬇ −62% (see below) |
    | **SAME-TARGET-MERGE overlaps** (two directions → one exit lane) | **4374** | **0** | ⬇ **ELIMINATED** |
    | **fully co-located events** (pen ≥ 1.79 m ≈ vehicle width) | **83015** | **868** | ⬇ **−99%** |
    | **total overlap events (all pairs)** | **148877** | **1823** | ⬇ **−98.8%** |
    | SAME-NORMAL-LANE overlaps (same lane id) | 492 | **696** | ⬆ WORSE, worst 1.800 m both |
    | teleports fired | 0 | 0 | disabled in both |

    **The 59 remaining "stopped to horizon" cases with the gates ON are NOT wedges.** All began in the
    final few hundred steps (7066–7169 of 7200; runs of 31–134 steps) — ordinary queueing at the cut-off,
    which is why "runs > 300 steps" is simultaneously **0**. Sampling confirms the city is still flowing at
    the end of the hour (e.g. step 7050: **146 moving / 14 stopped** of 160). With the gates OFF the same
    counters read 161 long runs and 156 blocked-to-horizon — **genuine permanent gridlock**.

59. **This OVERTURNS the 200-step conclusion (§9.54).** At 200 steps the gates looked like a wash-to-worse
    (overlap events 45 → 51). Over a realistic horizon they **halve nothing and double everything that
    matters**: throughput +109%, total overlaps −98.8%, long stalls eliminated, and the owner's
    symptom (b) — same-exit-lane interpenetration — **eliminated outright**. The 200-step window was
    simply too short for gridlock to have formed yet, so it compared two healthy cities.
60. **Promoted the probe to a committed guard, `LongHorizonGridlockDiagTests`** — but only *after* it was
    shown to discriminate (161 vs 0; 4374 vs 0). Its four assertions are anchored to that measured
    separation and bear only on the **gates-ON** configuration, so it cannot fail on an unrelated change to
    the default path. A probe that cannot tell the two configurations apart would not have been worth
    committing.
61. **What is still NOT fixed:** `SAME-NORMAL-LANE` interpenetration — two vehicles on the **same** lane
    overlapping, count **worse** (492 → 696), worst **1.800 m** = exactly the vehicle width = fully inside
    each other, unchanged. That is owner symptom (b)'s same-lane half and is the documented, still-open
    `NEED-colocated-vehicles.md`. **This is now the top remaining believability defect.**

### Session 3 (continued) — the owner's BELIEVABILITY REQUIREMENTS, and the violations quantified

62. **⭐ REQUIREMENTS (owner-stated, binding) — the high-realism artefact ladder.** Recorded in full at
    `docs/CONSTRAINT-high-realism-artefact-ladder.md`. This is a **requirement**, not an inference, and it
    overrides any convenience argument elsewhere in these docs.

    | # | Behaviour | High-realism verdict |
    | --- | --- | --- |
    | 1 | **Prevent the blockage** | ✅ the only acceptable *general* solution |
    | 2 | Cars **pass through each other** | ⚠️ ONLY when **already crashed into each other AND blocking the junction** — a recovery from an already-broken state. Otherwise disallowed. |
    | 3 | Cars **overlap during normal, non-unblocking manoeuvres** | ❌ NOT allowed |
    | 4 | **Teleport** | ❌ **NEVER**, no exception — *"the most unrealistic and most visible artefact"* |
    | 5 | A car **blocked with no obvious reason** (not overlapped) | ❌ must NOT be "solved" by teleport or overlap; requires fixing the **real cause** |

    Consequences that cost me two retractions of my own advice:
    - **`TimeToTeleportSeconds = 300` withdrawn** — rung 4.
    - **`IgnoreJunctionBlockerSeconds = 5` withdrawn as a general tool** — its trigger is elapsed
      **waiting time**, not overlap (`foe.WaitingTime >= IgnoreJunctionBlockerSeconds`), so it fires on
      merely-stuck cars, i.e. rung 5, where a rescue is *actively harmful* because it conceals the defect.
      Admissible only if re-gated on **measured overlap**.
    - **Rung 5 vindicates the earlier "trigger only on physical overlap" principle** (§9.26): `__veh127`
      froze for 95 steps with **nothing overlapping it**, and an ORCA-style rescue there would have masked
      the mis-gate that caused it.
    - Tiers 2 and 3 are the **same geometry**, separated only by *cause* — so an overlap count alone can
      never establish compliance.

63. **VIOLATIONS QUANTIFIED**, full hour, split by the high-realism pocket
    (measured centre **(2351.1, 2363.2)**, promote radius **70 m**, demote 100 m), via
    `LongHorizonGridlockDiagTests`:

    | Rung | Violation class | OFF total | OFF in-zone | ON total | ON in-zone |
    | --- | --- | --- | --- | --- | --- |
    | 3 | same-lane overlap (normal driving) | 492 | **28** (5.7%) | **696** | **115** (16.5%) |
    | 3 | same-target merge (2 dirs → 1 exit lane) | 4374 | **0** (0.0%) | **0** | **0** |
    | 2/3 | fully co-located (pen ≥ vehicle width) | 83015 | **17015** (20.5%) | **868** | **131** (15.1%) |
    | 4 | teleports | 0 | 0 | 0 | 0 |
    | 5 | stopped runs > 300 steps | 161 | — | **0** | — |
    | 5 | stopped through to horizon | 156 | — | **59** | — |

    worst in-zone penetration (fully-co-located class): **2.685 m → 2.426 m**; worst anywhere
    **3.137 m → 3.240 m** (slightly **worse**).

    **Reading:**
    - **In-zone fully-co-located violations collapse 17015 → 131 (−99.2%).** That is the headline: the most
      egregious visible artefact inside the high-realism pocket is essentially gone.
    - **Same-lane overlap is 4× WORSE in-zone (28 → 115)** and its in-zone *share* rises 5.7% → 16.5%. So
      the residual violation is not merely unfixed, it has become **more concentrated where it is least
      acceptable**. This is the top remaining defect (`NEED-colocated-vehicles.md`).
    - **Same-target merge was never an in-zone problem** — 0 in-zone in *both* configurations, despite the
      owner reporting having seen it "also in the high realism area". See the caveat below; do not treat
      this as contradicting the report.
    - Every overlap counted is **rung 3 by construction**, because no unblock-by-overlap mechanism is
      enabled (`IgnoreJunctionBlockerSeconds = -1`). None of them is an excusable rung-2 recovery.

    **⚠ CAVEAT that limits every in-zone percentage above.** The pocket is **camera-driven**
    (`LiveCitySim.SetLcRealismZone`); in this headless diagnostic it sits at the net's geometric centre with
    a 70 m radius. A viewer moves the camera, so **any** part of the city can become the high-realism area.
    Therefore the in-zone columns describe *one particular pocket placement*, not a bound — and
    **out-of-zone violations still matter**, because the camera can go there. That is also the most likely
    explanation for the merge discrepancy: 0 in-zone here, yet observed in-zone by the owner at a different
    camera position. Do not use these percentages to deprioritise an out-of-zone defect.

### Session 3 (continued) — WHY the cars overlap: root cause found, and my hypothesis falsified

64. **Analysed the 696 same-lane overlaps by cause** (`NEED-same-step-double-placement-colocation.md`).
    Re-ran the hour with all gates ON and reproduced **696 events / 146 episodes exactly** ⇒ deterministic.
65. **⚠ MY LEADING HYPOTHESIS WAS FALSIFIED, and it was a documented one.**
    `LANE-CHANGE-OVERLAP-DESIGN.md` §3 Stage 3 proposed *"the second emerging vehicle overshoots its
    cross-junction leader"*, fix = *"clamp behind the target lane's rearmost occupant"*. I adopted it and
    asked for it to be attacked. Measured: of 103 emergence samples with a real prior occupant,
    **0 (0%) negative gaps**, min **+4.05 m**, median +113 m. An emerging vehicle **never** overshoots.
    **The Stage-3 clamp would fix nothing** — do not implement it on that document's strength.
66. **ACTUAL ROOT CAUSE — same-step double placement, then perfect symmetry.** Three entry mechanisms
    (junction emergence, insertion, lane change) each compute a placement from the **same frozen
    start-of-step snapshot** and **none cross-checks another placement made in the same step**. Two
    vehicles therefore land at the **same slot in the same step**, each correctly seeing an empty slot in
    the pre-step world. Then the second half: **once byte-identical, Krauss/IDM applies identical forces to
    both forever** — perfect symmetry, nothing can separate them. A one-step collision becomes a ~100-step
    artefact. Concrete instances: insertion at a fixed depart offset (~5.65/6.95/8.90 m) **on top of a car
    already queued at the lane start** (insertion never checks for a backed-up queue); two cars
    lane-changing from *adjacent* source lanes into the *same* target lane+pos in one step
    (`…4_1→…4_2` and `…4_3→…4_2`, both at pos 27.83, spd 16.67).
67. **PERSISTENCE, not onset frequency, drives the visible volume.**

    | Episode length | Episodes | Events | Share |
    | --- | --- | --- | --- |
    | 1 step | 53 | 53 | 7.6% |
    | 2 steps | 47 | 94 | 13.5% |
    | 3–5 | 28 | 95 | 13.6% |
    | 6–10 | 5 | 32 | 4.6% |
    | **> 10 steps** | **13** | **422** | **60.6%** |

    **13 episodes = 60.6% of all events.** One incident (`__veh56`/`__veh84`, onset an H-LC double
    lane-change at step 191) holds a byte-identical pose 28 steps, persists across **three lanes/edges for
    ~100 steps**, and alone contributes **91 events (13.1%)**. ⇒ Making episodes *self-resolve* removes
    ~60% of events even if **no** onset is prevented.
68. **Attribution** (categories overlap; per-episode onset is the causal basis — per-event is inflated by a
    3-step lookback, 239 of 386 "unexplained" being later events in an episode whose onset *was*
    explained): H-E **61.6%**, H-LC **52.7%**, H-INS **56.8%**, H-CF **0.7%**, unexplained-at-onset
    **4.1%**. **147 events (21%) sit in episodes unexplained from onset** — recorded as genuinely open, not
    force-fitted. **H-CF is effectively ruled out (1 event)**, so the *"ECS frozen-snapshot car-following
    reaction"* that `LaneChangeOverlapDiagTests`' skip banner blames is real but **negligible** here.
    Clustering: 63 lanes, top-10 ≈ 54%, top-2 ≈ 23%.
69. **Fix options — all rung 1 (prevention), so all admissible in high realism.** (1) **insertion
    occupancy check** — cheapest, SUMO-native (`MSLane::isInsertionSuccess` refuses an insertion that does
    not fit; we never check), removes one onset mechanism outright. (3) **symmetry break** so co-location
    self-resolves — highest leverage per unit work (bounds the 60.6%), deterministic tie-break on the
    **ordinal vehicle id** as `IsLeaderByEntryOrder` already does, never `EntityIndex`; does **not** fix
    onset so must not ship *instead of* (1)/(2). (2) **same-step arrival arbitration** — the correct but
    largest piece: SUMO gets it free by being sequential (`MSLaneChanger` in order), our frozen-snapshot
    parallel plan does not; needs a claim/reservation in the command buffer, i.e. exactly the *"timing of
    structural mutations"* deviation CLAUDE.md permits — **and it is where the owner's "check for imminent
    overlap and pause one of the cars" belongs**: deferring one of two simultaneous arrivals *is* the
    arbitration. **Recommended order (1) → (3) → (2).**

### Session 3 (continued) — FIX 1 SHIPPED: the insertion follower-gap check

70. **Root cause pinned to a single line of logic.** `Engine.TryInsertOnLane`'s occupancy scan selects a
    leader with `other.Kinematics.Pos >= insertPos` — so a vehicle sitting **just BEHIND** the depart
    position was **never examined at all**. Inserting in front of it buries the new vehicle's **rear**
    inside the existing body: depart at 5.65 m with a car queued at 5.00 m ⇒ **4.35 m of body overlap**.
71. **SUMO refuses these BY DEFAULT — so this was a porting omission, not a deviation.**
    `MSLane::isInsertionSuccess` runs a **FOLLOWER** pass after the leader pass
    (`getFollowersOnConsecutive(aVehicle, getBackPositionOnLane(), false)`) and bails when
    `followers[i].second < 0` under `InsertionCheck::COLLISION`; **`insertionChecks` defaults to
    `InsertionCheck::ALL`** (`SUMOVehicleParameter.cpp:60`). Ported as `Engine.InsertionFollowerGapCheck`
    (+ `LIVECITY_INSERTIONFOLLOWERGAP`). The gap is **body-to-body with NO minGap term** — SUMO keeps minGap
    in `backGapNeeded`, the separate `FOLLOWER_GAP` arm, **deliberately not ported** (it refuses merely
    *uncomfortable* rear gaps and would change throughput far beyond the measured defect).
72. **⭐ ALL 661 GOLDENS BYTE-IDENTICAL WITH THE CHECK ON** — and the bench hash too
    (`D96213B7BB4021A7`, par == single). Verified by temporarily forcing the default to `true` and running
    the full suite: the **only** failure was my own `DefaultIsOff` guard, which *must* fail then (the same
    shape as `IgnoreJunctionBlockerTests.DefaultIsMinusOne`). The reason it is inert is structural: goldens
    were generated **by** SUMO, which already refuses these insertions, so no golden can contain one.
73. **Demo effect, isolating the insertion check (junction gates ON in both):**

    | Metric | 3 gates | + insertion check | |
    | --- | --- | --- | --- |
    | same-lane overlap events | 696 | **569** | ⬇ −18% |
    | **same-lane IN-ZONE** | 115 | **35** | ⬇ **−70%** |
    | fully co-located (≥ 1.79 m) | 868 | **548** | ⬇ −37% |
    | **fully co-located IN-ZONE** | 131 | **38** | ⬇ **−71%** |
    | total overlap events | 1823 | **1566** | ⬇ −14% |
    | stopped to horizon | 59 | **49** | ⬇ better |
    | completed trips | 2709 | **2675** | ⬆ −1.3% (cost) |

    **The in-zone reduction is the believability-critical result: −70% / −71%.** The 1.3% throughput cost is
    the expected price of refusing an unsafe departure. Against the original gates-OFF baseline, in-zone
    same-lane is now **28 → 35** (was 28 → 115): near parity on the violation while throughput is **+107%**
    and long stalls are eliminated.
74. **⚠ MY OWN MEASUREMENT WAS CONTAMINATED FIRST — by the exact bug class I had documented hours earlier.**
    Running the A/B with `LIVECITY_INSERTIONFOLLOWERGAP=1` exported in the shell produced an "OFF" column of
    **392 arrivals / 6567 same-lane overlaps** against the true OFF baseline of **1295 / 492** — because
    `RunConfig` cleared only the *three* junction gates and **inherited the fourth** from the ambient
    environment. Same process-global-env failure as `SumoShimEnvCollection` (§9.43), this time in my own
    harness. **Fixed structurally:** `AllLiveCityGateVars` now sets **every** gate explicitly to `"1"`/`"0"`
    for **both** configurations, with a stated contract that a new `LIVECITY_*` gate must be added to that
    list. Re-run reproduces the OFF baseline exactly, confirming the diagnosis.
    **Standing lesson reinforced:** an env-var-configured A/B must set *every* variable it depends on, not
    just the ones it is varying — inheriting one is indistinguishable from measuring it.

### Session 3 (continued) — FIX 2: co-location symmetry break (and a golden-caught 1-D/2-D bug)

75. **Measured first, then built** — the discipline that keeps paying. After fix 1, episode stats (added to
    the diagnostic, since **episodes = onsets** and **events = persistence**, and only the former is a
    prevention metric): onsets 88 → 100, **longest episode 79 steps**, 14 episodes > 10 steps. Onsets per
    unit of traffic actually *improved* 46% (0.068 → 0.037 per arrival), but a 79-step episode is ~40 s of
    two cars sitting inside each other. Perfect symmetry means it never resolves ⇒ fix 2 confirmed needed.
76. **Fix 2: `Engine.ColocationSymmetryBreak` (arm 15, default OFF).** When two same-lane bodies already
    overlap, the designated yielder holds for the step so the other pulls clear. Yield rule is deterministic
    and antisymmetric: **the vehicle behind yields; on an exact positional tie the lexicographically GREATER
    id yields** (`string.CompareOrdinal`, never `EntityIndex`).
    **This is the ONE deliberate deviation from SUMO on this branch** — SUMO has no such mechanism because
    it cannot reach the state (it places vehicles sequentially). It recovers from a state SUMO never
    produces rather than altering behaviour SUMO defines. **Ladder-compliant:** triggered by *measured
    overlap*, never a timer, so it cannot fire on a rung-5 stuck car and conceal its defect; and it neither
    teleports (rung 4) nor passes cars through each other (rung 2) — it **separates** them.
77. **⚠ MY "PARITY-SAFE BY CONSTRUCTION" CLAIM WAS WRONG, AND FIVE GOLDENS CAUGHT IT.** I tested body
    overlap with **longitudinal intervals only**. Under the sublane model two vehicles legitimately share a
    lane **side by side** — longitudinally overlapping, laterally clear, never touching. So the arm braked
    legitimate overtakes and broke `RungP22SublaneSideBySide`, `RungD3CooperativeOvertake`,
    `RungD2ReturnGap`, `RungOV3OvertakeExecution`, `RungRvoMultiNeighbor` — **every one a lateral-passing
    scenario**. **Same error class as this branch's OBB axis bug (§4.5b): a 1-D test of a 2-D condition.**
    Fixed by adding the lateral term (`|ΔLatOffset| < (widthA+widthB)/2`, `Kinematics.LatOffset` +
    `VType.Width`). With that, **all 661 goldens byte-identical** and bench `D96213B7BB4021A7` par == single.
    Note the lateral term changed **nothing in the demo** (all cars sit at `LatOffset ≈ 0` there, sublane
    off) — so the demo measurement was accidentally unaffected while the **goldens** were what caught the
    bug. Precisely why goldens-plus-diagnostics is the required net, not either alone.
78. **Fix 2 measured** (all five gates ON vs none):

    | Metric | OFF | 4 gates | **+ fix 2** | |
    | --- | --- | --- | --- | --- |
    | same-lane events | 492 | 569 | **365** | ⬇ −36% vs fix 1 |
    | episodes (onsets) | 88 | 100 | **80** | ⬇ −20% |
    | episodes > 10 steps | 9 | 14 | **7** | ⬇ **−50%** |
    | episodes > 3 steps | 17 | 20 | **12** | ⬇ −40% |
    | longest episode | 98 | 79 | **75** | ⬇ barely moved |
    | fully co-located (≥1.79 m) | 83015 | 548 | **429** | ⬇ |
    | total overlap events | 148877 | 1566 | **1530** | ⬇ |
    | completed trips | 1295 | 2675 | **2668** | ≈ unchanged |

    Onsets fell too (100 → 80) even though the mechanism only shortens episodes — separating a pair early
    prevents downstream cascades that would have started new ones.
79. **⚠ RESIDUAL, and its cause is understood: the longest episode is still 75 steps.** The symmetry break
    cannot help when **both** cars are stopped — the "winner" has no room to pull clear either, so holding
    the loser changes nothing. Separating them would need reverse (we have none). **Only onset prevention
    fixes this**, i.e. fix 3. The measured trigger of the long episodes is **H-LC: two vehicles
    lane-changing into the SAME slot in the same step**, so fix 3 should target lane-change arrival
    arbitration specifically rather than the whole plan phase.

### Session 3 (continued) — FIX 3: built, golden-safe, and MEASURED INERT (so not recommended)

80. **Fix 3: `Engine.LaneChangeArrivalArbitration` (default OFF).** `IsTargetLaneOverlapped` already blocks a
    change into a slot an **existing** target-lane occupant holds; it cannot see a vehicle on a **different
    source lane** entering the same slot in the same step, because neither is a target-lane occupant in the
    frozen snapshot. Ported as a snapshot-only arbitration (no intent pre-pass, so no new phase ordering):
    for ego on lane *i* → target *t*, the only other-lane claimant is one on the lane **beyond** *t*;
    tie-break by **smaller ordinal id wins the slot**, so exactly one defers. SUMO needs none of this —
    `MSLaneChanger` is sequential.
81. **⚠ IT IS MEASURED INERT ON THE DEMO — and I am not shipping it as an improvement.** Instrumented over
    2000 demo steps it fires plenty: **10226 evaluations, 1156 contested slots, 785 deferred lane changes**.
    Yet every same-lane overlap statistic is **BIT-FOR-BIT IDENTICAL** with it on: 365 events / 80 episodes /
    longest 75 / 7 over-10. All 661 goldens also unaffected.
    **This REFUTES my own inference (§9.79) that the residual onsets were the H-LC adjacent-lane same-slot
    case.** That inference came from the attribution measured **before** fix 1; fix 1 removed the insertion
    onsets, and the surviving 80 episodes are evidently a **different** mechanism — H-E emergence, or the
    **21% that were unexplained at onset**. I built the fix on a stale attribution instead of re-attributing
    after fix 1 changed the population. That is the session's recurring failure mode in miniature: *a
    correct port of the wrong mechanism*.
82. **Decision: kept, default OFF, explicitly labelled measured-inert.** It is a correct, golden-safe port of
    an arbitration SUMO gets free by being sequential, and it may matter on a different lane topology — but
    it is **not** part of the recommended configuration and must not be enabled without a scenario where it
    demonstrably helps. Same standard as `JunctionPhysicalOccupancyGate` and the parked ORCA tier.
83. **What the residual actually needs: RE-ATTRIBUTION AFTER FIX 1**, not another mechanism. The onset
    population has changed and the pre-fix-1 attribution no longer describes it. Next step is to re-run the
    cause-attribution instrument (H-E / H-LC / H-CF / H-INS + unexplained) against the **current** 80
    episodes and target whatever now dominates. Building before re-measuring is what produced §9.81.

### Session 3 (continued) — ⚠ CORRECTION: fix 3 is NOT inert, and the FINAL measured state

84. **⚠ §9.81's "measured inert" verdict on fix 3 was WRONG — it ran against a STALE BUILD.**
    `dotnet build -c Release` builds `Traffic.sln`, which **does not contain `Sim.LiveCity.Tests`**, so the
    demo measurement ran with a `Sim.Core` that had no fix 3 compiled in and reproduced the fix-2-only
    numbers. Caught because two consecutive runs of the "same" configuration gave 365 then 327 while the OFF
    column reproduced exactly — a same-config discrepancy is a **build/harness** smell, not a result.
    With a correct build (`dotnet build tests/Sim.LiveCity.Tests -c Release`), arbitration OFF → ON:

    | Metric | arb OFF | arb ON |
    | --- | --- | --- |
    | same-lane events | 386 | **327** (−15%) |
    | episodes (onsets) | 84 | **72** (−14%) |
    | longest episode | 75 | **64** steps |
    | total overlap events | 1546 | **1408** |
    | completed trips | 2672 | **2684** |

    **Third time this session a harness/build issue produced a confident wrong conclusion** (after the
    `SumoShim` env race §9.43 and my own gate-leak §9.74). Standing rule now: **`dotnet build -c Release`
    does NOT rebuild `Sim.LiveCity.Tests` — build that project explicitly before any demo measurement.**
85. **⚠ ARBITRATION MUST NOT BE ENABLED ALONE.** With the junction gates OFF and only arbitration on, the
    demo is far **worse** than baseline: **13308** same-lane events, longest episode **3046 steps**, and only
    **402** completed trips (vs 1295). Deferring lane changes in a city that is already gridlocking starves
    it further. The gates are a **package**; this one is beneficial only in combination.
86. **⭐ FINAL MEASURED STATE — all gates ON vs shipped default OFF, full hour, deterministic (two
    consecutive runs identical):**

    | Metric | OFF | ALL ON | |
    | --- | --- | --- | --- |
    | completed trips | 1295 | **2684** | ⬆ **+107%** |
    | stopped runs > 300 steps | 161 | **0** | ⬇ eliminated |
    | stopped to horizon | 156 | **52** | ⬇ −67% |
    | same-target-merge overlaps | 4374 | **0** | ⬇ eliminated |
    | total overlap events | 148877 | **1408** | ⬇ **−99.1%** |
    | fully co-located (≥1.79 m) | 83015 | **330** | ⬇ **−99.6%** |
    | — of those, IN-ZONE | 17015 | **13** | ⬇ **−99.9%** |
    | same-lane overlap events | 492 | **327** | ⬇ −34% |
    | — IN-ZONE | 28 | **12** | ⬇ **−57%** |
    | same-lane episodes (onsets) | 88 | **72** | ⬇ −18% |
    | longest episode | 98 | **64** steps | ⬇ −35% |
    | worst penetration overall | 3.137 m | **2.951 m** | ⬇ better |
    | worst penetration in-zone | 2.685 m | **2.123 m** | ⬇ better |
    | teleports | 0 | **0** | never needed |

    **NOTHING IN THE MEASURED SET IS WORSE.** Earlier "worse" readings (same-lane 492 → 696, in-zone
    28 → 115) were **intermediate states with only the three junction gates on**; fixes 1–3 reversed them.
    In-zone same-lane is now **better than the shipped baseline**, not merely recovered.
87. **Remaining honest gaps:** (a) 327 same-lane events / 72 onsets / longest 64 steps still violate ladder
    rung 3 — **cause unknown**, needs re-attribution after fix 1 changed the onset population (§9.83);
    (b) 52 cars stopped at the horizon, look benign but not cleared; (c) the one-lane-queue symptom is
    **inferred** gone (it was a consequence of permanent blockage) but **never directly measured**;
    (d) in-zone figures are for **one** camera position; (e) the new arms are O(N) per vehicle per step,
    i.e. O(N²) — fine at the demo's ~160 vehicles, **unmeasured at scale**; (f) all gates remain
    **default OFF** pending an owner decision.

### Session 3 (continued) — the DEFAULTS question, answered gate by gate

88. **Perf fix to the symmetry-break arm.** It scanned `ActiveVehicles()` for every vehicle every step —
    **O(N²)** on the hot plan path. Replaced with ego's own pos-sorted lane bucket
    (`neighbors.OnLane`), which is not merely faster but the **exact** candidate set (only same-lane
    vehicles can body-overlap). Demo results **bit-identical** afterwards (327 / 72 / 2684 / 1408),
    confirming it is a pure speed-up.
    **Perf impact is UNMEASURABLE on this machine** — bench throughput OFF 2806 / 1599 steps/s vs ON
    2440 / 2744, i.e. the ON range sits *inside* the OFF range. An earlier single-pair reading of "−20%"
    was noise and is retracted. The remaining analytic concern is shape, not a measurement: the arm is
    O(bucket) per vehicle per step, and on a single-lane highway the bucket is N. Reducible to O(1) by
    exploiting the bucket's pos-sorted order (only pos-adjacent vehicles can overlap) — not done.
89. **DEFAULTS RECOMMENDATION — the six gates split cleanly in two.**

    **Four are strictly-more-faithful SUMO ports; I see no reason to keep them OFF:**
    `ContTurnInsideJunctionGate` (SUMO tests a lane *property*; we tested lane-id equality),
    `JunctionIsLeaderGate` (`MSVehicle::isLeader`), `InternalJunctionAdmissionGate`
    (`MSInternalJunction`), `InsertionFollowerGapCheck` (`isInsertionSuccess`'s follower pass, which
    SUMO runs **by default** — `insertionChecks = ALL`). Each fixes a porting **omission or mis-port**;
    all 661 goldens are byte-identical and the bench hash is unchanged. **Defaulting these ON *increases*
    parity fidelity**, which is the repo's stated bar — so the burden of proof is on keeping them off.
    The original blocker (T1.10's 5 teleports) is resolved.

    **Two are deviations from SUMO and warrant a deliberate decision:**
    `ColocationSymmetryBreak` — the **only** intentional deviation on this branch; SUMO has no such
    mechanism because it cannot reach the state. Engine-default-ON means the library does something SUMO
    does not. `LaneChangeArrivalArbitration` — **more conservative than SUMO**: it defers to a far-lane
    vehicle that merely *could* claim the slot, without knowing intent.

90. **Three reasons that apply regardless of which gates are chosen:**
    (a) **Partial enablement is a FOOTGUN, and this is measured.** Three-junction-gates-only is *worse*
    than baseline (same-lane 492 → 696); arbitration alone is *catastrophic* (13308 events, longest
    episode **3046** steps, trips 1295 → 402). They must flip **as a package** or not at all.
    (b) **Unmeasured surfaces:** `Sim.BenchCity` and the other `scenarios/_bench` city nets have **not**
    been run with the gates on. Coverage so far is 661 goldens + 5 gridlock diagnostics + LiveCity +
    Pedestrians + the demo hour + `synthetic-junction2`.
    (c) The **one remaining rung-3 violation is unexplained**, so "done" is not the right word for the
    same-lane defect even though every metric improved.

    **Recommended:** flip the **four faithful** gates ON in the engine; enable **all six** in the demo
    config; hold the two deviations at engine-default OFF until the `_bench` city scenarios are measured
    and the O(bucket) shape is addressed.

### Session 3 (continued) — ⚠ 3x DENSITY: the fixes hold on collisions, NOT on gridlock

91. **Stress-tested at 3x vehicles** (`LIVECITY_CARS=480`, **449 concurrent achieved**) — note the config's
    own comment says the downtown crop holds *"~157 concurrent cars … cleanly"*, so this is **~3x its
    documented capacity**, i.e. deliberately oversaturated. Full hour, OFF vs ALL GATES ON:

    | Metric | OFF | ALL ON | |
    | --- | --- | --- | --- |
    | completed trips | 1583 | **3426** | ⬆ +116% ✅ |
    | total overlap events | 340287 | **26011** | ⬇ −92% ✅ |
    | fully co-located (≥1.79 m) | 208688 | **11511** | ⬇ −94% ✅ |
    | same-lane **IN-ZONE** | 1540 | **36** | ⬇ **−98%** ✅ |
    | same-target merge | 16915 | **1561** | ⬇ −91% ✅ |
    | — merge **IN-ZONE** | 11056 | **0** | ⬇ eliminated ✅ |
    | **stopped runs > 300 steps** | 622 | **539** | ⬇ only −13% ❌ |
    | **stopped to horizon** | 470 | **394** | ⬇ only −16% ❌ |
    | **longest overlap episode** | 4301 | **2849** steps | ❌ ~24 min |
    | same-lane events (total) | 7774 | **11076** | ⬆ WORSE ❌ |
    | episodes > 10 steps | 27 | **43** | ⬆ WORSE ❌ |

92. **The headline must be qualified: gridlock elimination is a DESIGN-DENSITY result, not a general one.**
    At 1x the gates take stalls > 300 steps from **161 → 0**. At 3x they take **622 → 539** — the city
    still gridlocks. So the earlier "the city no longer gridlocks" claim holds **at ~160 concurrent cars
    and does NOT hold at 449**.
93. **What DOES scale is the collision/visual quality.** Every overlap metric improves by ~−92% or better at
    3x, and **in-zone** same-lane overlaps fall **1540 → 36 (−98%)** with in-zone merge overlaps
    **eliminated**. So inside the high-realism pocket the picture is dramatically better even when the city
    as a whole is jammed — the visual artefacts are fixed, the *throughput collapse* is not.
94. **Two readings, and I cannot yet separate them:** (a) the fixes do not scale to oversaturation; or
    (b) 3x is simply beyond the crop's physical capacity and *some* jamming is legitimate — a real city
    oversaturated 3x does queue. **But 539 stalls > 300 steps and 394 cars stopped to the horizon are NOT
    legitimate queueing** — that is terminal gridlock, so at minimum the *rung-1 violation returns at 3x*.
    Distinguishing (a) from (b) needs a density sweep (e.g. 160 / 240 / 320 / 400) to find where the gates
    stop holding — not done.
95. **Consequence for the demo:** **3x is not a believable configuration even with every gate on.** If more
    traffic is wanted, the honest options are to raise capacity rather than demand (more lanes / a bigger
    crop), or to find the density ceiling first via the sweep in §9.94.

### Session 3 (continued) — WHY it gridlocks at 3x: classified, with one bucket MISLABELLED

96. **Classified the 539 long stalls at 3x** into the owner's four hypotheses (A crashed / B red light /
    C no-free-exit-spillback / D stopped-on-green-no-reason). Concentration is sharp: the top ~10 lanes hold
    most stalls, feeding junctions **`d_5_4`, `d_3_4`, `d_5_3`** — e.g. `e_d_5_3_d_5_4_2` alone has **79**.
    So this is a few bottleneck junctions, not a city-wide failure.
97. **A — CRASHED IN THE JUNCTION: essentially absent.** Only isolated instances appear in the per-lane
    breakdown (1 on `e_d_4_4_d_3_4_2`, 1 on `e_d_4_4_d_5_4_2`). **Not the cause of the gridlock.**
98. **B — RED LIGHT: a large, legitimate share** (e.g. 29 of 38 on `e_d_5_2_d_5_3_2`, 16 of 16 on
    `e_d_4_4_d_5_4_1`). Ordinary signal waiting.
99. **⚠ D — "STOPPED ON GREEN, NO VISIBLE REASON" = 110, but the bucket is MISLABELLED — my classifier
    measured the wrong gap.** Every bucket-D example carries **`binder = 1`**, which is
    `LeaderFollowSpeedConstraint` (`Engine.cs:5171`) — **ordinary car-following**. So these cars are stopped
    because *their leader is stopped*: they are **queue members**, not independently stuck.
    The tell is that `downstreamFree` is a near-constant **23.80 m / 30.20 m** across unrelated junctions —
    because it measured free space on the **next (internal) lane**, not the gap to the car immediately
    ahead on the **current** lane. Positions confirm queues, e.g. `e_d_3_5_d_3_4_3` at pos **209.44,
    201.94, 186.93, 149.43** — four cars nose-to-tail.
    **So "green light + room downstream + still stopped" is not evidence of a broken predicate here**; the
    room was on a lane the car cannot reach because the car in front of it has not moved.
100. **Answering the owner's question directly:** the 3x gridlock is **not** cars crashed in junction
     centres (A ≈ 0), and **not** cars frozen on green for no reason (D is a measurement artefact). It is
     **queueing — B (red) plus C (spillback), i.e. genuine oversaturation** of a few junctions. That
     supports reading (b) of §9.94: 3x is beyond the crop's capacity and the jamming is largely physical.
101. **⚠ What is still NOT established, and needs one more targeted run:** what holds the **HEAD** of each
     queue. Every measurement so far samples vehicles independently, so a queue of 40 cars reports 40
     car-following stalls and hides the one vehicle at the front whose binder is the real cause. The correct
     next instrument classifies **only head-of-queue vehicles** (no stopped leader on their own lane) and
     reports *their* binder. Until that exists, "predominantly legitimate saturation" is the best-supported
     reading but **not proven** — a single mis-gated head-of-queue vehicle can stall 40 followers and would
     look exactly like this.
102. **Arm 14 cleared of suspicion.** Two UNEXPLAINED entries showed `binder=14` (my internal-junction
     admission arm) on internal lanes with "could not resolve a controlling connection". Checked: **every**
     failure path in `InternalJunctionAdmissionConstraint` returns `double.PositiveInfinity`, so the arm
     never holds a car on an unresolvable lookup — that message came from the *classifier's* own downstream
     resolution. Where arm 14 does bind, it is correctly holding a cont-bay car whose foe lane is occupied.

### Session 3 (continued) — ⚠⚠ HEAD-OF-QUEUE: MY OWN FIX CREATES A 4-WAY DEADLOCK AT 3x

103. **The owner was right that only the head matters.** Probed heads vs followers directly (a stalled
     vehicle is a FOLLOWER iff another deeply-stalled vehicle sits ≤ 15 m ahead on the same lane).
     Default density: **HEADS = 0, FOLLOWERS = 0** — no deep stalls at all, so 1x is genuinely clean.
     3x: **HEADS = 618, FOLLOWERS = 2327 (79% followers)**, which confirms §9.99's mislabel: followers are
     97.2% `leaderFollow`, i.e. pure queue noise.
104. **⚠⚠ WHAT HOLDS THE HEADS — 48.1% is MY OWN ARM 14.**

     | Binder | Share of heads |
     | --- | --- |
     | **14 `internalJxnAdmission`** (T3.2, mine) | **48.1%** (297) |
     | 2 `crossJxnLeader` | 46.0% (284) |
     | 10 `junctionYield` arm 5 | 3.1% (19) |
     | 1 `leaderFollow` | 1.6% |
     | 11 `keepClear` | 1.0% |

     Followers, for contrast: **97.2% `leaderFollow`** — exactly as predicted, so the head/follower split is
     doing its job.
105. **THE MECHANISM: a FOUR-WAY CIRCULAR WAIT inside junction `d_5_4`, created by my admission gate.**
     Four vehicles sit on four cont **bays** of the same junction — `:d_5_4_3_0`, `:d_5_4_7_0`,
     `:d_5_4_11_0`, `:d_5_4_15_0` — **each at pos 4.91**, each held by **binder 14**, with run lengths
     climbing 457 → 657 → **857** steps: they *never* move. Simultaneously the four approach lanes
     (`e_d_5_5_d_5_4_2`, `e_d_5_3_d_5_4_2`, `e_d_6_4_d_5_4_2`, `e_d_4_4_d_5_4_2`) each report
     `nextMouthGap = 4.91` — blocked by precisely those four cars, and held by `crossJxnLeader` (the 46%).
     **So the two dominant head binders are the two halves of ONE defect**: my gate wedges four cars in the
     bays, and `crossJxnLeader` then correctly refuses to admit anyone else. Together **94.1% of heads.**
106. **This is the SAME FAILURE SHAPE I set out to fix, now caused by my own fix.** The original arm-5
     deadlock was two cars each yielding to the other with no escape. Arm 14 reproduces it with **four**
     cars, and `isLeader` cannot help because they are held by **arm 14, not arm 5** — the ordering never
     runs. Each bay car yields to a foe lane occupied by another bay car ⇒ circular wait, and arm 14 has
     **no escape hatch and no ordering**.
107. **The likely missing piece is exactly what I DELIBERATELY OMITTED** (design §5): SUMO's
     `myInternalLinkFoes` + **`addBlockedLink` mutual registration** between the bay link and each foe link.
     I omitted it to keep the measurement interpretable — a defensible call at the time — but it is
     plausibly the very thing that prevents four bays from mutually blocking. **Unverified.** Also
     unverified: whether the stage-2 lane being added to the foe set **unconditionally** (correct per
     `postloadInit`) is what closes the cycle.
108. **CONSEQUENCE FOR THE DEFAULTS RECOMMENDATION (§9.89) — it must be amended.**
     `InternalJunctionAdmissionGate` was listed among the four "strictly-more-faithful, no reason to keep
     OFF" gates. That is no longer supportable as stated: it is faithful and it fixes the 2-car deadlock at
     design density, but **it introduces a 4-way deadlock under load**. It is invisible at 1x (0 deep
     stalls) and dominant at 3x. **Do not default it ON until the circular wait is resolved.** The other
     three faithful gates are untouched by this finding.
109. **Also revised: §9.100's verdict that the 3x gridlock is "predominantly legitimate saturation" is
     WRONG.** Only ~3% of heads are ordinary queueing; **94% are the arm-14/crossJxnLeader wedge.** The
     gridlock at 3x is a **defect**, not oversaturation. My earlier reading was an artefact of sampling
     followers alongside heads — precisely the error the owner pointed at.

### Session 4 (2026-07-26) — the §6 hypothesis is FALSIFIED; the real defect is arm 14's *predicate*

110. **PRIMARY HYPOTHESIS FALSIFIED IN ONE GREP — `addBlockedLink` IS DEAD CODE IN SUMO 1.20.0.**
     `myBlockedFoeLinks` (`MSLink.h:730`) has exactly one reader, `MSLink::willHaveBlockedFoe()`
     (`MSLink.cpp:696`). That function is called from **two** places and **both are commented out**:
     ```
     MSVehicle.cpp:5221:  if (leftSpace < 0/* && item.myLink->willHaveBlockedFoe()*/) {
     MSVehicle.cpp:7255:  if (link->hasFoes() && link->keepClear() /* && item.myLink->willHaveBlockedFoe()*/) {
     ```
     So the `addBlockedLink` mutual registration at the end of `MSInternalJunction::postloadInit`
     (`MSInternalJunction.cpp:125-126`) writes a set **nothing ever reads**. Porting it would have been
     provably inert. §6's "primary hypothesis" was wrong, and §9.107's "likely missing piece" with it.
     **Cost of finding out: one grep.** Cost had I built it first: a day, and a null measurement I would
     have had to explain. This is the "measure/verify before building" rule paying for itself.

111. **WHAT SUMO ACTUALLY DOES — and the exact line where my port diverges.**
     `myInternalLaneFoes` is passed as `setRequestInformation`'s `foeLanes` argument and lands in
     `MSLink::myFoeLanes` (`MSLink.cpp:213`). I traced **every** consumer of `myFoeLanes`:

     | Consumer | Predicate | On the driving path? |
     | --- | --- | --- |
     | `MSLink::getLeaderInfo` (`:1373`) | collects candidate leaders, then a long ignore-cascade | **yes** |
     | `MSLink::hasApproachingFoe` (`:1070`) | **bare occupancy** — `lane->getVehicleNumberWithPartials() > 0` | **NO** |
     | `setRequestInformation` bookkeeping (`:253/419/437/528/1255`) | conflict-geometry setup | n/a |

     `hasApproachingFoe` — the *only* bare-occupancy test — has exactly three callers:
     `MSLane.cpp:1077` (**insertion**), `MSVehicle.cpp:6901` (`unsafeLinkAhead`, **lane-change abort**, and it
     explicitly skips internal edges), and libsumo/TraCI. **SUMO never uses bare foe-lane occupancy as a
     driving-path admission rule.** My arm 14 does exactly that (`Engine.cs:7757-7776`: scan vehicles, set
     `occupied = true` on any foe-lane match, brake to the end of the bay). **That single line is the defect.**

112. **BARE OCCUPANCY IS SYMMETRIC — WHICH IS *WHY* IT DEADLOCKS, AND SUMO SAYS SO IN A COMMENT.**
     Both real mechanisms are **asymmetric by construction**:
     - `opened()` → `blockedAtTime` (`:880`) → `blockedByFoe` — compares **arrival/leave time intervals**
       and requires `avi.willPass`.
     - `getLeaderInfo` → `checkLinkLeader` → the consumption site `MSVehicle.cpp:3429`
       `isLeader(link, leader, gap) || it->inTheWay()`.

     And `isLeader`'s mutual branch carries SUMO's own comment naming this failure mode verbatim
     (`MSVehicle.cpp:7437`):
     ```cpp
     } else if (response && response2) {
         // in a mutual conflict scenario, use entry time to avoid deadlock
     ```
     A symmetric predicate over a 4-cycle of mutually-responding streams has no fixed point other than
     "everyone waits forever". That is precisely the observed wedge. **The cycle is not a missing foe set —
     it is a missing tie-break.**

113. **STRUCTURAL RESULT THAT MAKES THE FIX EXACT: for a bay-vs-bay foe, `inTheWay` CANNOT fire.**
     `inTheWay` (`MSLink.cpp:1437-1441`) requires
     `(!foeExitLink->isInternalJunctionLink() || foeIsBicycleTurn)`. When the foe is itself standing on a
     cont **bay**, its exit link *is* the internal-junction link, and it is not a bicycle turn — so the
     whole conjunct is **false**. Therefore in the exact configuration that wedges (four cars, four bays,
     one junction) the disjunction at `:3429` reduces to **`isLeader` alone**: admission is decided purely
     by entry-time ordering with a total tie-break (ET → speed → id). Non-bay foes — cars physically
     standing on a plain stage-2/internal lane — keep the unconditional block, because for them `inTheWay`
     *can* fire and that is the genuinely-occupied case.

     **So the fix is not "add a mechanism", it is "restore the predicate SUMO uses":** gate arm 14's
     occupancy on `IsLeaderByEntryOrder` when the occupying foe is on a bay lane. Everything needed is
     already ported — the three timestamps (`VehicleRuntime.cs:295+`), `IsLeaderByEntryOrder`
     (`Engine.cs:8641`), and the bay predicate (`InternalJunctionByBayLane`, which is *by definition* the
     set of internal-junction checker lanes).

114. **THE FIX, AND THE ONE-VARIABLE A/B THAT ATTRIBUTES IT.** New sub-gate
     `Engine.InternalJunctionAdmissionEntryOrder` (default OFF, env `LIVECITY_INTERNALJUNCTIONENTRYORDER`),
     `EgoYieldsToBayFoe` in `Engine.cs`. When the occupying foe stands on a cont **bay**, admission is
     decided by `IsLeaderByEntryOrder` instead of by bare occupancy; a foe on a plain internal lane keeps
     the unconditional block. Timestamp pairing copies `isLeader` exactly: mutual response ⇒ both sides use
     `ConflictEntryTime`, otherwise the `:7357` defaults (ego `Conflict`, foe `Entry`).

     Measured with the **committed** probe `HeadOfQueueStallProbeTests` (3 columns, 7200 steps @ 480 cars,
     all other gates ON in both ON columns, so ON-vs-noOrd differs in **exactly one variable**):

     | 3x, 480 cars | gates OFF (shipped) | ON, entry-order OFF | **ON, entry-order ON** |
     | --- | --- | --- | --- |
     | completed trips | 1583 | 3426 | **5381** |
     | deep stalls (>300 steps) | 625 | 651 | **103** |
     | peak concurrent deep stalls | 469 | 220 | **17** |
     | stall HEADS | 57 | 39 | **7** |
     | of which binder 14 | 0 (arm off) | 19 (48.7%) | **4** |
     | arm-14 bay-wedge stalls | 0 (arm off) | 24 | **9** |
     | **longest wedge run** | — | **4890 steps** | **637 steps** |

     **+57% trips and −92% peak deep stalls from the single variable**; **+240% trips** against the shipped
     default. The `noOrd` column **independently reproduces §9.105's wedge**: the same four bays
     `:d_5_4_{3,7,11,15}_0` at the same pos **4.91**, and 48.7% of heads on binder 14 against the
     historically-reported 48.1%. Its true run length is **4890 steps** — the historical "857" was a
     mid-run snapshot, so those cars genuinely never moved for the whole hour. **With the ordering on the
     longest is 637 steps: a permanent lock becomes a bounded delay.** Determinism confirmed: the ON column
     came out identical (5381 / 103 / 17 / 7 / 9) on two independent runs.

115. **⚠ THE RESIDUAL IS REAL AND IS *NOT* THE SAME DEFECT — 9 stalls, longest 637 steps.** Read the net
     rather than guessing (`scenarios/_ped/demo_city/box/net.xml`). All seven residual lanes are genuine
     cont bays. Taking `d_5_4` bay idx **7**, whose response row is `01100110111000001110` ⇒ it responds to
     {1,2,5,6,8,9,10,16,17,18}, and only **8** of those is itself `cont=1`. So its foe set is dominated by
     **plain internal lanes** — the through movements `:d_5_4_1_*`, `:d_5_4_5_*`, `:d_5_4_9_*` and the
     pedestrian crossings `c1..c3` — and for those the new code deliberately keeps the **unconditional**
     block.

     Is keeping it right? Working `isLeader` through for bay-vs-plain-internal: ego (cont entry) has
     `Conflict = MAX`; the foe entered on a NON-cont link so **both** its timestamps are finite. Mutual ⇒
     `MAX > finite` ⇒ ego yields. `response && !response2` ⇒ defaults ⇒ ego yields. Only `!response` lets
     ego go, and then it is not a foe worth blocking on anyway. **So the ordering would change nothing for
     this shape** — option "extend the ordering to all foes" is provably inert here, and must not be
     attempted (it is on the do-not-re-attempt list below).

     What SUMO has that we do not is **`inTheWay`'s geometry**: a foe that has already passed ego's
     conflict point is ignored (`MSLink.cpp:1437`, via `myConflicts[i].getLengthBehindCrossing`). We block
     while the foe is anywhere on the lane. **The residual is therefore a CONSERVATISM from unported
     internal-junction conflict geometry, not a circular wait** — which is consistent with its bounded run
     lengths (637 vs 4890). Confirming which foe actually holds each of the 9 is the next measurement; it
     has NOT been done, so this is a structural argument, not a measurement.

116. **⚠ MY §9.115 ARGUMENT WAS BUILT ON A BIT-ORDER ERROR. I read every `response` mask BACKWARDS.**
     `NetworkModel.Bit` is `mask[mask.Length - 1 - link]` (`NetworkModel.cs:144`) — **the RIGHTMOST
     character is index 0**, as SUMO writes it. §9.115 hand-decoded `d_5_4` bay idx 7's row left-to-right
     and concluded "only 8 of its foes is `cont`". Decoded correctly, idx 7 responds to
     {1,2,3,9,10,11,13,14,17,18} and idx 3 responds to {7,9,10,15,16,17} — so **bays 3 and 7 respond to each
     other**, i.e. that pair IS a mutual conflict, the exact opposite of what I wrote.

     **The CODE was never wrong** — it calls `RespondsTo`, which applies the reversal internally. Only my
     prose reasoning was invalid. But it was invalid in the direction that flattered my own fix, and it is
     the second time in this workstream that a confident hand-decode of a bit mask has been backwards. **Do
     not hand-decode a `response`/`foes` mask; call `RespondsTo` or reverse the string first.**

117. **THE MEASUREMENT (§6's task), AND WHY ITS HEADLINE METRIC MISLEADS.** 9 of 9 residual wedges resolved
     a foe set. The instrument's first-cut headline said **5 of 9 held by ≥1 BAY foe**, which would mean the
     ordering is still incomplete. **That reading is wrong, and the metric is the reason:** the snapshot
     records **every occupied foe lane**, while arm 14 stops at the **first** blocking foe — so
     "had a bay foe present" is an **upper bound on occupancy, not a statement of causation**.

     Per-row inspection settles it: **all 9 wedges had at least one occupied PLAIN foe lane**, and the plain
     arm is **unconditional**, so that alone suffices to explain every one of them. **Bay-only wedges — the
     one shape that would prove the ordering incomplete — number 0.** The clearest illustration is the
     apparent 2-cycle `__veh2936` (`:d_5_4_3_0`) ↔ `__veh2629` (`:d_5_4_7_0`), both at pos 4.91, both
     binder 14, each listing the other. It looks exactly like the old wedge. It is not: `2936` also has
     `:d_5_4_23_0` (**plain**, occupied by a stopped car) in its foe set, and the ordering correctly gives
     `2936` the right of way over `2629` anyway — `CompareOrdinal("__veh2936","__veh2629") > 0` ⇒ `2936`
     does not yield. **The ordering works; the plain arm holds them.** The probe's reporting has been
     rewritten to lead with the decisive figure and to label the occupancy figure as an upper bound.

     **So §9.115's CONCLUSION survives — a conservatism, not a circular wait — while its supporting
     argument (§9.116) did not.** Being right for a wrong reason is not being right; the measurement is
     what carries this, not the reasoning that preceded it.

118. **THE ACTUAL UPSTREAM CAUSE, and it is a NEW rung-5 finding: cars are QUEUEING INSIDE the
     intersection.** Two distinct sub-shapes in the foe detail:
     - **Moving foes still block.** `__veh4008`'s plain foes on `:d_5_3_10_1` are at **speed 0.89 / 1.07 /
       1.20 m/s** and `__veh1475`'s at **1.42 / 3.05**. They are *crossing*, not stuck — yet they hold the
       bay for as long as they are anywhere on that lane. This is precisely the unported `inTheWay`
       geometry (`MSLink.cpp:1437`): SUMO ignores a foe that has **passed ego's conflict point**.
     - **Standing queues on internal lanes.** `:d_5_3_10_1` carries **four** stopped cars at pos
       **4.67 / 12.17 / 19.67 / 27.17**, binders `leaderFollow` ×3 and `junctionYield` ×1. Others are held
       by `crossJxnLeader`. **These are cars stopped inside the junction because their exit is congested**
       — which `keepClear`/`checkRewindLinkLanes` exists to prevent, and which is a believability defect in
       its own right regardless of arm 14.

     **The bay stalls are therefore a SYMPTOM, and the cause is one level upstream.** Per the ladder's
     rung 5 this must be fixed at the cause. Relaxing arm 14 to let a bay car proceed into an occupied
     conflict area would be the wrong fix twice over: it would conceal this, and it would create exactly
     the overlaps arm 14 was added to prevent.

119. **ALL SEVEN GATES TURNED ON BY DEFAULT — and the flip is itself the strongest parity evidence on this
     branch.** Owner decision: the shipped demo should be the fixed demo. Result of flipping:
     **all 661 goldens BYTE-IDENTICAL.** The only failures were the **six `DefaultIsOff` assertions** —
     tests asserting the defaults themselves, not trajectories. Nothing observable moved on any scenario that
     has a SUMO reference, so keeping the gates off was costing believability for zero parity gain.
     Those six now guard the new default and each carries the evidence; the flag-OFF behavioural test
     disables the gate *explicitly* so the default stays a default rather than a one-way door.

     **Bench hash RE-PINNED `D96213B7BB4021A7` → `BF3794A4704BCD79`.** Verified attributable by stashing only
     the Engine defaults and re-running, which reproduced the old hash exactly. Determinism unaffected
     (par == single). ⚠ Note what this does and does not mean: `Sim.Bench` runs `_bench/highway-dense`, which
     has **no SUMO reference**, so the new hash is a **re-pinned tripwire, not a verified-correct value**.
     The goldens are the parity statement and they did not move.

     **A wiring bug caught before it could bite:** every `LiveCitySim` gate line was
     `GetEnvironmentVariable(name) == "1"` — a two-state override that silently forces **OFF** when the var is
     unset. Harmless while defaults were false; a live bug the instant they became true (the demo would have
     run with all seven gates disabled while everything else had them on, and "the demo still gridlocks"
     would have read as a failed fix rather than a wiring mistake). Replaced with a tri-state `EnvGate`.

120. **BUILT THE DENSITY DIFFERENTIAL HARNESS (design trio + A1 + B1).** Premise, stated as measured fact:
     §9.119 proves the goldens cannot see density work at all. The load-bearing design decision is that
     **SUMO's shipped defaults ARE the cheating** — read from `sumo --save-template` on the pinned 1.20.0:

     | Option | SUMO default | Ladder |
     | --- | --- | --- |
     | `time-to-teleport` | **300** | rung 4 |
     | `collision.action` | **teleport** | rung 4 |
     | `collision.check-junctions` | **false** | **rung 3 made invisible** |

     Hence three columns: **S-default** (upper bound, not a target), **S-honest** (cheats off — the target),
     **Ours**. `scripts/run-density-diff.sh` **asserts its own validity**: the two generated configs must
     differ in exactly the four cheat elements or the run aborts, because the dividend subtraction is only
     sound if the margin is attributable to the cheats alone.

     **A1/SC3 fired correctly and saved a wrong reading:** on the committed demo route file S-default
     teleports **0** — because every route ends `<stop parkingArea=… duration="100000"/>`, i.e. the cars
     **park permanently**. 861 inserted, only 96 trips completed, 765 parked-but-"running". A *parking*
     scenario, not a throughput one. Without the guard I would have reported "SUMO copes fine at this
     density". **B1's recorder was therefore mandatory, not convenient.**

     B1 review notes (reviewed, not trusted): route fidelity is **exact by construction** —
     `Engine.SpawnVehicle` routes via `Router().Route(from,to)` and the recorder calls the *same* overload on
     a `NetworkRouter` whose fields are all readonly (pure Dijkstra, fixed `EdgeCost`). Inertness confirmed
     **empirically**: the recorded run's `ArrivedTotal` is **5381**, identical to the recorder-off probe.
     SC3's "independent log" was overstated in the agent's report (both logs are written at the same call
     site) but is non-vacuous, since the recorder carries an extra route-resolved guard.

121. **⚠⚠ THE 96%-OF-SUMO CLAIM IS RETRACTED. CLOSED-LOOP DEMAND CANNOT MEASURE DISCHARGE.**
     The parallel high-density calibration workstream reported a **discharge deficit**: at fixed inflow
     vanilla SUMO plateaus at ~430 resident cars while SumoSharp climbs **258 → 2623** over an hour and never
     reaches steady state. That looked like a contradiction of this branch's 96%. **It is not a
     contradiction — their measurement is right and mine answered a different question.**

     `LiveCitySim`'s spawn loop is `for (s = 0; s < CarSpawnPerStep && live < CarTargetConcurrent; s++)` —
     it inserts **only while occupancy is below the cap**. **Inflow is throttled by our own drain.** A slow
     junction simply causes fewer insertions and resident count *cannot* run away. A discharge deficit
     manifests as **unbounded queue growth at FIXED inflow**; a closed-loop model cannot produce unbounded
     growth, therefore cannot show the symptom, therefore reports "close to SUMO" **however narrow the drain
     is**. Worse: I recorded *our* demand and fed it to SUMO, so SUMO received a departure profile our own
     engine had already pre-limited to what it could handle.

122. **MY OWN DATA CORROBORATES THEM, once read correctly — and the tell was one I explicitly dismissed.**

     | | SUMO (s-honest) | Ours |
     | --- | --- | --- |
     | in flight at horizon | **259** | **480** ← our cap; we ended FULL |
     | mean trip duration | **213.6 s** | **~321 s** (Little: 480 / 1.4947 s⁻¹) |
     | mean occupancy | ~333 (1.5567 × 213.6) | ~480 |

     **We hold ~45% more cars resident to deliver 4% FEWER trips, each trip taking ~50% longer.** That is a
     narrower drain in my own measurement. And SUMO ending with only 259 in flight proves the offered inflow
     (**1.63 veh/s — chosen by our drain**) never came close to saturating SUMO, so the test had **no power**
     to expose a capacity gap in either direction. I wrote in §9.117's own caveat list that "SUMO 259 vs our
     480 is not a jam signal" and moved on. **It was the whole story.** Third instance on this branch of the
     same error class: *measuring a different quantity than the one named* (cf. §9.100's `downstreamFree`
     mislabel, §9.117's occupancy-vs-causation).

     **What survives untouched:** the cheat findings, because they concern SUMO's behaviour on a *fixed
     input*, not capacity — zero cheat dividend on throughput at that inflow, **26 junction collisions SUMO's
     own defaults do not detect**, and their clustering on the exact lanes where our bays wedge
     (`:d_5_3_10_1` ×4, `:d_5_4_9_1` ×4, `:d_5_4_3_0` ×2, `:d_5_3_17_0` ×2). That last point now reads more
     usefully: **SUMO keeps those junctions draining by letting cars overlap inside them** — one concrete way
     its drain is wider than ours, and one we may not copy.

123. **TWO ALREADY-MEASURED FINDINGS PROMOTED FROM "CONSERVATISM" TO "DISCHARGE MECHANISM".** I had been
     calling both bounded conservatisms. Under a discharge framing they are drain restrictions:
     1. **Cars queueing inside junctions** (§9.118: four stopped on `:d_5_3_10_1`). A car standing in the
        intersection blocks the conflict area for *everyone* crossing it — a drain restriction by definition,
        and precisely what `keepClear` / `checkRewindLinkLanes` exists to prevent.
     2. **Arm 14 holds a bay closed while a foe is anywhere on a conflicting lane — including one still
        moving at 0.89–3.05 m/s that has ALREADY PASSED the conflict point** (§9.118). Every step a bay is
        held shut while it could be discharging is **lost saturation flow**. `inTheWay`'s conflict-point
        geometry (`MSLink.cpp:1437`) is the missing piece.

124. **NEW BLOCKING TASK A3 — OPEN-LOOP DEMAND MODE.** Until the harness can offer a fixed inflow independent
     of our own drain it cannot measure discharge, so **A3 blocks B2/B3/C**. Its non-vacuity condition is the
     important one: **our column must REPRODUCE the calibration workstream's runaway.** If it does not, the
     two instruments disagree and neither's numbers may be trusted until that is resolved.
     Design gains §1b, whose rule is now standing: **every metric must be labelled with the demand model that
     produced it, and a capacity claim from closed-loop demand is invalid** however carefully the rest was
     measured.

### Session 5 (2026-07-27, overnight) — the deficit is ROLLING SPEED; 0 for 2 on fixes

125. **A3 SHIPPED — open-loop demand, and it settles the argument.** `LiveCityConfig.CarInflowVehPerSec`
     (null ⇒ unchanged closed-loop), a fractional-credit accumulator so any real rate is expressible,
     `--inflow`/`--series` on the driver, a two-window steady-state test (last quarter vs the quarter
     before, 5% tolerance — a "final value near the max" test cannot see a level that climbs steadily to the
     horizon), and `scripts/sweep-inflow.sh`. **SC2 met:** at 1.7 veh/s on identical demand SUMO is STEADY
     (311→306) while we are RUNAWAY (420→464). **The two workstreams' instruments AGREE**, which was the
     precondition for trusting either.

126. **⭐ THE CAPACITY ANSWER.** Sweep, 7200 steps, identical demand per row
     (`docs/reports/density-inflow-sweep.txt`):

     | inflow | OURS | SUMO s-honest |
     | --- | --- | --- |
     | 0.8 | STEADY @162 (arr 2573) | STEADY @130 |
     | 1.0 | STEADY @201 (arr 3198) | STEADY @165 |
     | 1.2 | STEADY @254 (arr 3817) | STEADY @203 |
     | **1.4** | **STEADY @306 (arr 4448) ← OUR CEILING** | STEADY @240 |
     | **1.6** | **RUNAWAY → 2242, arr 2938** | **STEADY @280** |
     | 2.0 | RUNAWAY → 3528, arr 1681 | RUNAWAY @940 |

     **Max sustainable inflow: ours ≈ 1.4 veh/s, SUMO's between 1.6 and 2.0** (≥14% deficit, likely ~30%).
     Two things matter more than the ceiling: at **every** sustainable inflow we hold **~25% more resident
     cars for the same flow**, and we do not degrade gracefully — crossing the ceiling takes trips
     **4448 → 2938 → 1681**. SUMO's own runaway at 2.0 is far gentler. Our failure mode is self-amplifying.

127. **⭐⭐ THE DEFICIT IS ROLLING SPEED, NOT QUEUEING.** Measured at 1.4, the one inflow where **both** are
     steady, so nothing is confounded by our collapse. Identical routes (1320.8 m both sides):

     | | SUMO s-honest | Ours |
     | --- | --- | --- |
     | mean trip duration | **180.6 s** | **247.7 s** |
     | **halting fraction** | **33.7%** | **33.3%** ← *the same* |
     | ⇒ mean speed while MOVING | **~11.0 m/s** | **~8.0 m/s** |

     Both figures computed the same way on both sides (SUMO's own `halting`/`running`, the same threshold on
     ours), so **the conclusion is arithmetic, not inference: the lost time is spent rolling slowly, not
     standing still.** "Discharge deficit" implied junctions blocking; the measurement says our cars simply
     progress more slowly *between* stops, which inflates residency at every inflow and is what eventually
     tips us into collapse.

     What holds a MOVING car below **its own lane's** limit: `leaderFollow` **36.1%**, `junctionYield`
     **30.4%**, `freeFlow` 21.1%, `redLight` 5.9%. ⚠ The first version of this histogram used a flat
     13.89 m/s and was an **artefact** — this net's car lanes run 8.33/11.11/13.89/16.67, so cars correctly
     driving a 30 km/h limit counted as slow and `freeFlow` came out top at 34.8%. **Third mislabel of that
     class on this branch.**

128. **❌ G1 (`keepClear` held-propagation) REFUTED.** `Engine.KeepClearHeldPropagation` ports
     `last->myHaveToWaitOnNextLink || last->isStopped()` (`MSVehicle.cpp:5126`), of which we had only the
     second disjunct — the gap `NEED-checkrewindlinklanes-partial-port.md` itself ranks "highest impact".
     A/B at 1.6: trips **2938 → 2762**, resident **2242 → 2498**. **Worse on both**, and coherent in
     hindsight — G1 makes admission *more* conservative, the opposite of widening a drain. Faithful, kept,
     **default OFF**, not to be retried for discharge. Ported with one documented deviation:
     `HeldAtLinkLastStep` is written in the **commit** phase (following the `v.Acceleration` precedent) so
     the parallel plan phase reads a stable previous-step value instead of an order-dependent one.

     Also settled in passing: that NEED's ordering constraint ("finish this port, *then* enable the cont-turn
     fix") is **obsolete** — `RungHDp2g2` passes today with the cont-turn gate ON, because the other gates
     now throttle junction entry properly instead of accidentally.

129. **❌❌ MINOR-APPROACH ARRIVAL SPEED — +67% CAPACITY, AND FLATLY UNFAITHFUL. The night's most
     instructive failure.** `Engine.MinorApproachArrivalSpeed` replaces the minor-link stop-at-the-line plan
     with SUMO's nonzero **arrival-speed** target (`MSVehicle.cpp:2806-2810`, comment: *"decelerates just
     enough to be able to stop if necessary and then accelerates"*). Ours decays as `sqrt(2·decel·seen)`
     toward **zero** at the line; that formula is a **constant 7.99 m/s**:

     | distance to junction | 20 m | 15 m | 10 m | 7 m | 5 m | 4.6 m |
     | --- | --- | --- | --- | --- | --- | --- |
     | ours | 13.42 | 11.62 | 9.49 | 7.94 | 6.71 | 6.43 |
     | SUMO formula | 7.99 | 7.99 | 7.99 | 7.99 | 7.99 | 7.99 |

     **Capacity effect, at 1.6 where we collapse and SUMO does not: trips 2938 → 4919 (+67%),
     RUNAWAY → STEADY @503, halting 79.9% → 29.7%.** The collapse simply stopped happening.

     **And it is wrong: 14+ goldens fail** — `RungC5WillPass`, `RungC4i/iii/iv/v/vi`, `Rung9b`,
     `RungC3OnRampMerge`, `RungER2`×2, `ContTurnSequence`, `DenseFlowDeadLaneDrain`,
     `RungC4iiiSuccessiveLaneSpeed`, `RungHDgap3ParkedPassable`. The goldens **are** SUMO's output, so they
     settle it: SUMO's realised minor approach matches our stop-at-the-line form; `arrivalSpeed` in that
     branch is metadata for the DriveProcessItem's arrival **time** and junction arbitration, not the step
     speed. I had flagged that exact uncertainty while reading and chose to let measurement decide.

     **Kept, default OFF, labelled REFUTED — deliberately not deleted.** The +67% is the largest capacity
     signal on this branch and it localises where the capacity hides: **inside `jyArm 2` under load, in
     conditions no golden covers.**

130. **THE METHOD LESSON, and it is the one worth carrying forward.** §7 lesson 1 says *goldens byte-identical
     ≠ parity-inert* — the demo can move while goldens do not. **§9.129 is the converse and it is sharper: a
     change can transform the demo and be flatly wrong.** A +67% throughput win that eliminates gridlock is
     precisely the result one wants to believe. **Neither surface alone can accept a change; both must.**

     Scoreboard: **0 for 2** on mechanism hypotheses reasoned from source (G1 died to the open-loop A/B, the
     arrival speed to the goldens). With `NEED-junctionyield-impatience-saturation.md`'s five, that is **seven**
     reasoned interventions refuted against **one** SUMO-oracle trace that found a real cause in minutes.
     **The next attempt starts from a per-vehicle trace inside `jyArm 2`, not another reading of the source.**

**State at end of session 3:** gate green (**752/4/0**, LiveCity **49/49**, `D96213B7BB4021A7` par==single, 48/48, 272/272),
tree clean, all pushed. **The arm-5 mutual deadlock is RESOLVED at SUMO's own defaults** — both vehicles
complete their routes, teleports at the ceiling, nothing regressed on any surface. The `isLeader` port is
**complete, faithful and safe but insufficient alone**; the load-bearing mechanism was **admission
control**. All three gates remain **default OFF**: turning them on changes outward-facing behaviour and is
an **owner decision**, with a genuine trade to weigh (see §9.54).

---

## 10. RESUME PROMPT (paste this into a fresh session)

> Continue the F3 / live-city believability workstream in `/home/user/SumoSharp`, branch
> **`claude/f3-junction-overlap-handoff-okf5nu`** (pushed).
>
> **Read `docs/F3-SESSION-LOG.md` §2 (gate), §5 (what is shipped), §6 (your task), §7 (traps) first.**
> Then `docs/CONSTRAINT-high-realism-artefact-ladder.md` — the owner's binding believability requirement.
>
> ### Where things stand
> The demo used to gridlock terminally within an hour. **Seven gates now default ON** and it runs a full hour
> at design density with **0 long stalls, +107% trips, −99% overlaps, 0 teleports**; at **3x** density peak
> concurrent deep stalls are **469 → 17** and the arm-14 four-way circular wait is **fixed**.
> `Sim.ParityTests` **755/4/0**, all 661 goldens byte-identical, `Sim.LiveCity.Tests` **50/50**,
> `Sim.Pedestrians.Tests` **272/272**, `Sim.Bench` **`BF3794A4704BCD79`** par == single (⚠ re-pinned — was
> `D96213B7BB4021A7` before the defaults flip; attribution verified).
>
> ### YOUR TASK — §6: fix LANE SELECTION, not junctions
> **TRACE-1 is done and it moved the target.** At 1.4 veh/s, the 77.8% of our cars that drive SUMO's route
> are at **parity (+1.6%, median +0.0 s)**. The whole deficit is the **22.2% our engine REROUTES**, at
> +156.7 s each = **94.2% of all excess time**. Our junction/car-following core is fine.
>
> **The rescue is load-bearing** — disabling either `WrongLaneRerouteAtApproach` or `DeadLaneDriveThrough`
> takes trips 4448 → ~2120 and goes RUNAWAY. So the root defect is that **our cars fail to reach the lane
> their turn requires**, badly enough that two rescues are both mandatory while SUMO needs neither on
> identical demand. That is a **lane-selection** problem. Instrument why the rescue fires, compare against
> SUMO's `getBestLanes` / strategic urgency, and **trace before porting** — 7 reasoned-from-source
> hypotheses have been refuted here against 2 traces that worked.
>
> **Any change must clear BOTH surfaces** — all 661 goldens AND the open-loop discharge test.
>
> ### NON-NEGOTIABLES — every one of these cost real time here
> 1. **Measure before building.** Five hypotheses were refuted by measurement this session, and **two
>    "fixes" were built on stale attributions** (`isLeader`, then the lane-change arbitration).
>    **A correct port of the wrong mechanism is still a wrong fix.**
> 2. **`dotnet build -c Release` does NOT rebuild `Sim.LiveCity.Tests`** (not in `Traffic.sln`). Build that
>    project explicitly before any demo measurement — a stale build already produced one wrong verdict.
> 3. **Set EVERY env gate explicitly in both A/B arms** (`AllLiveCityGateVars`). Inheriting one from the
>    shell is indistinguishable from measuring it, and has produced two wrong results (`SumoShimEnvCollection`,
>    then my own gate leak).
> 4. **Judge stalls on HEADS, not the population.** Followers are 79% of stalled samples and 97%
>    `leaderFollow`; population metrics hide the one car whose binder is the cause.
> 5. **Judge overlaps on EPISODES (onsets), not events.** 13 episodes once produced 60.6% of all events.
> 6. **200-step diagnostics cannot see hour-scale failure.** Use `LongHorizonGridlockDiagTests` (7200 steps).
> 7. **Goldens alone are insufficient; the demo alone is insufficient.** A 1-D/2-D overlap bug was invisible
>    in the demo and caught only by five sublane goldens. Run both nets.
> 8. **Ladder compliance:** never teleport; a rescue must trigger on **measured overlap**, never a timer, or
>    it fires on rung-5 cars and conceals their defect.
> 9. Behavioural changes ship **default OFF** until measured. Use `git commit -F <file>` (backticks and
>    quotes in `-m` get shell-mangled — hit twice).
>
> ### Do NOT re-attempt
> **any CAPACITY conclusion from closed-loop demand** (§9.121 — the retracted 96%) ·
> `addBlockedLink` / `myBlockedFoeLinks` (**dead code in SUMO 1.20.0** — its only reader
> `willHaveBlockedFoe` is commented out at both call sites, §9.110) · extending the entry-time ordering to
> **non-bay** foes (**provably inert**: a cont entry leaves `ConflictEntryTime` at `MAX`, so every `isLeader`
> branch already yields, §9.115) · `isLeader` (done, insufficient alone) · zero-overlap as an
> invariant (impossible in principle, §4.4) · `TimeToTeleportSeconds=300` or `IgnoreJunctionBlockerSeconds=5`
> as demo tools (both retracted, ladder rungs 4 and 5) · `LANE-CHANGE-OVERLAP-DESIGN.md` §3 Stage 3's clamp
> (falsified: 0/103 negative gaps) · defaulting `InternalJunctionAdmissionGate` ON **without its
> entry-order sub-gate** (that pairing is the 4890-step wedge).

### 10b. One-paragraph state summary (if you read nothing else)

The live-city demo's acceptance bar is **believability**, and it used to fail hard: after ~an hour every car
queued behind junctions blocked forever, with no teleport or unblock path. Root causes were **not** the
briefed "junction occupancy gate" — they were a cont-turn mis-port, an unported `MSInternalJunction`, an
unported insertion follower-gap check, and perfectly-symmetric co-located vehicles that Krauss can never
separate. **Seven** default-OFF gates now take the demo to **0 long stalls, +107% trips, −99% overlaps, 0
teleports** over a full hour at design density, and at **3x** density to **+240% trips** with peak concurrent
deep stalls **469 → 17** — all with **all 661 goldens byte-identical**. Four instrument/harness defects were
found and fixed along the way (OBB axis, OBB anchor, stale binder diagnostics, two process-global env races),
each of which had produced confident wrong numbers. The last defect was **mine**: the internal-junction
admission gate blocked on **bare foe-lane occupancy**, which is symmetric, so a cycle of cont bays wedged
four cars motionless for **4890 steps**. SUMO never uses bare occupancy on the driving path — it filters
foe-lane candidates through `isLeader(...) || inTheWay()`, whose tie-break chain is total precisely to avoid
this. Restoring that ordering took the longest lock to **637 steps**. The branch's own carried hypothesis
(`addBlockedLink`) was **falsified by one grep**: it is dead code in 1.20.0. **What is open is LANE SELECTION.** TRACE-1 settled it: the 77.8% of our cars that drive SUMO's route are at
**parity** (+1.6%, median +0.0 s); the entire deficit is the **22.2% our engine reroutes**, costing +156.7 s
each and carrying **94.2%** of all excess time. Rerouting is a **load-bearing rescue** — removing either
mechanism gridlocks the demo instantly — so the root defect is that our cars fail to reach the lane their turn
requires, while SUMO needs no such rescue on identical demand. **The junction core is not the problem**, which
is why both `jyArm` hypotheses failed.
