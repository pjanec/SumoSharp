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
| `dotnet test tests/Sim.ParityTests -c Release` | **729 passed / 4 skipped / 0 failed** (session 3; was 689) |
| `dotnet run --project src/Sim.Bench -c Release` | hash **`D96213B7BB4021A7`**, `deterministic=True`, par==single |
| `dotnet test tests/Sim.LiveCity.Tests` (**no** `--no-build`; not in `Traffic.sln`) | **48 / 48** |
| `dotnet test tests/Sim.Pedestrians.Tests -c Release` | **272 / 272** |

729 = 689 (the session-2 baseline) + 13 `JunctionLinkLaneMapTests` (T2.1) + 3 `JunctionEntryTimeTests`
(T2.2) + 12 `JunctionIsLeaderTests` (T2.3) + 4 gap/flag (T2.4) + 4 `InternalJunctionFoeTests` (T3.1)
+ 4 `InternalJunctionAdmission*Tests` (T3.2). The 4 skips are pre-existing
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

## 5. What is SHIPPED (gate green; every behavioural change is default-OFF)

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
| **Session 3 — `isLeader` port, Stage 1 + T2.3 (all parity-inert):** | |
| `NetworkModel.LinkIndexByInternalLane` (both cont stages) + `EntryConnectionByLink` (T2.1) | **live, unconditional**, nothing reads them in sim |
| 3 `long` timestamps on `VehicleRuntime` + `AssignJunctionEntryTimestamps` (T2.2) | **live**, written at the lane-advance seam, **read by no sim path** (audited) |
| `Engine.IsLeader` / `IsLeaderByEntryOrder` / `ResponseFor` + gap helper (T2.3) | **live but UNCALLED** — 297 insertions, 0 deletions |
| `tests/Sim.ParityTests/SumoShimEnvCollection.cs` | **live** — serialises the six `SumoShim.Run` classes (§2's ⚠) |
| `Engine.IsLeader`/`ResponseFor`/`GapForIsLeader` + `JunctionIsLeaderGate` (T2.3/T2.4) | **default OFF** — faithful, safe, **but insufficient alone** (§9.48-50) |
| `NetworkModel.InternalJunction`/`InternalJunctionByBayLane`/`InternalLaneFoes` (T3.1) | **live, unconditional**, no reader in sim |
| `Engine.InternalJunctionAdmissionConstraint` (arm 14) + `InternalJunctionAdmissionGate` (T3.2) | **default OFF** — ⭐ **this is what FIXES the deadlock** (§9.53) |
| `LiveCitySim` gates `LIVECITY_ISLEADERFIX` / `LIVECITY_INTERNALJUNCTIONFIX` | live, both default OFF |

**Three gates now form a PACKAGE and must be judged together:** `ContTurnInsideJunctionGate` (lets a car
commit inside a junction — exposes the wedge), `JunctionIsLeaderGate` (orders cars already inside), and
`InternalJunctionAdmissionGate` (controls who gets in — the load-bearing one). Shim env gates:
`SUMOSHARP_CONTTURNFIX` / `SUMOSHARP_ISLEADERFIX` / `SUMOSHARP_INTERNALJUNCTIONFIX`.

A/B switches: demo → `LIVECITY_CONTTURNFIX=1`, `LIVECITY_F3OCCUPANCY=1`; shim → `SUMOSHARP_CONTTURNFIX=1`
and `--ignore-junction-blocker <TIME>`.

## 6. IN PROGRESS — port `isLeader()` (the faithful fix; owner-chosen)

**⚠ SESSION 3: this task is UNDERWAY and now has its own design trio — read those, not just this
section:** `docs/F3-ISLEADER-PORT-DESIGN.md` (HOW, with the proof in §0a and the traps in §3b/§5b),
`…-TASKS.md` (staged tasks + success conditions), `…-TRACKER.md` (what is ticked).

**Progress: the `isLeader` port is COMPLETE (T2.1-T2.5) and it was NOT the fix.** It is faithful, safe and
default-OFF, but on its own it does not resolve the deadlock (§9.48-50). **The deadlock is fixed by
`MSInternalJunction` second-stage admission (T3.1/T3.2, §9.51-53)** — veh 95 and 102 both arrive at SUMO's
own `--ignore-junction-blocker` default. See also §9.38 (attempt 1, not the response matrix, is the
operative arm) and §9.43 (a cross-test race that made two of the five diagnostics unreliable).

The rest of this section is the original spec. It remains accurate except where §9.38 corrects it: the
mutual-conflict branch **is** the one that resolves the deadlock, but it is reached via attempt 1's
both-red arm, **not** via the response matrix.

**Everything else is either done or explicitly parked.** The owner has chosen the faithful fix over the
pragmatic knob.

### Why this is the single highest-value port

It closes **two** open items with one piece of work:
- **T1.11 / the arm-5 mutual deadlock.** Two cars on crossing internal lanes of one junction car-follow
  *each other* via `JunctionYieldConstraint` arm 5 (`AdaptToJunctionLeader`), which by design has no
  right-of-way notion and no escape (`Engine.cs:7252-7256`; `JunctionYieldTimeoutSeconds` only suppresses
  arm 6). Measured: veh 95 / 102 at speed **exactly 0.000 for 121 steps**, freed only by the 120 s teleport.
  **`isLeader()` is the reason SUMO never enters this state** — it is not a timer, SUMO simply orders the pair.
- **T1.6 / the true-F3 residue.** The remaining `BOTH-INTERNAL-DIFFERENT-LANE` overlaps are **12 of 15
  BOTH-MOVING** (corrected math) — genuine simultaneous admission, exactly what entry-time ordering fixes.

It also makes `ContTurnInsideJunctionGate` shippable on its own merits rather than propped up by the knob.

### What to port

**Source: `sumo/src/microsim/MSVehicle.cpp:7343-7483` (`MSVehicle::isLeader`).** Consumed at
`MSVehicle.cpp:3429` as the gate `if (isLeader(link, leader, gap) || it->inTheWay())` — i.e. ego adapts to a
foe when **either** the foe has priority-by-entry-order **or** the foe physically occupies the conflict point.
Our `FoeIsInTheWay` already ports the second disjunct.

Algorithm, in SUMO's order:

1. `if (!myLane->isInternal() || myLane->getEdge().getToJunction() != link->getJunction()) return true;`
   — **a vehicle not yet on the junction always treats every foe as a leader** (it yields, stopping *outside*).
   We already have the right predicate for this: `NetworkModel.IsInternalLaneOfJunction` (do **not** use
   `egoOnInternal`; that was the T1.5a mis-port).
2. If the foe is not on an internal lane of this junction → `return true`.
3. Otherwise compare entry times: `egoET = myJunctionConflictEntryTime`, `foeET = veh->myJunctionEntryTime`,
   with two adjustments:
   - **same source lane** (both entered from the same predecessor): use
     `myJunctionEntryTimeNeverYield` for both.
   - else compute `response` / `response2` (does ego yield to foe / foe to ego) from TL state, priority, or
     the response matrix, then: if `!response` (ego has right of way) use
     `foeET = veh->myJunctionConflictEntryTime, egoET = myJunctionEntryTime`; if `response && response2`
     (mutual conflict) use `myJunctionConflictEntryTime` for both.
4. **Tie-break chain — must be reproduced exactly for determinism:**
   `if (egoET == foeET) { if (speed == foeSpeed) return getID() < veh->getID(); else return getSpeed() < veh->getSpeed(); }`
   `else return egoET > foeET;`
   i.e. **entered later ⇒ you yield**; tie → **slower yields**; tie → **lexicographically smaller ID yields**.
   ⚠ Use the **vehicle id string**, NOT `EntityIndex` — CLAUDE.md requires order-independence, and SUMO's own
   tie-break is the id.

### New state required

Three per-vehicle timestamps SUMO maintains and we do not: `myJunctionEntryTime`,
`myJunctionConflictEntryTime`, `myJunctionEntryTimeNeverYield`. Grep SUMO for where each is assigned
(`MSVehicle.cpp`, around the enter-lane / `enterLaneAtMove` path) and mirror the assignment points, not just
the fields. Add them to `VehicleRuntime` next to `WaitingTime`. They must be set from the frozen start-of-step
state so the plan phase stays order-independent.

### Where it plugs in

`JunctionYieldConstraint`'s foe loop, arm 5. Today arm 5 applies whenever a foe is on the foe internal lane
(plus `FoeIsInTheWay` for the `FoeWith`-only case). It must become
`if (IsLeader(v, foe, ...) || FoeIsInTheWay(...))` — matching `MSVehicle.cpp:3429`.

### Success conditions

1. Arm-5 mutual deadlock gone **without** the knob: with `ContTurnInsideJunctionGate = true` and
   `IgnoreJunctionBlockerSeconds = -1`, `synthetic-junction2` yields **≤ 2** teleports and vehicles **95 and
   102 arrive** (SUMO: 433 s / 497 s; the knob got 647 s / 587 s).
2. A **direct** unit test of the tie-break chain: equal entry times + equal speeds ⇒ the smaller **id**
   yields; equal entry times + different speeds ⇒ the **slower** yields; different entry times ⇒ the **later**
   entrant yields. Must not use entity index.
3. All 661 goldens byte-identical, or every shift justified by a live-SUMO 1.20.0 diff (§1 for the binary).
4. `Sim.Bench` hash `D96213B7BB4021A7`, par==single.
5. All **five** gridlock diagnostics green (§2), and `Sim.LiveCity.Tests` / `Sim.Pedestrians.Tests` green.
6. Re-measure the F3 buckets (§3) — expect the 12 BOTH-MOVING events to drop.
7. Determinism unchanged: no `System.Random`; parallel == serial.

### Then, and only then

- Decide whether `ContTurnInsideJunctionGate` goes default-ON (it is currently only OFF pending this).
- Re-evaluate whether `IgnoreJunctionBlockerSeconds` is still wanted at all, or stays at SUMO's `-1`.

### Parked, with reasons (do not pick these up first)

| Item | Why parked |
| --- | --- |
| ORCA/cooperative rescue tier (`DESIGN-NOTE-tiered-junction-blockage-rescue.md`) | **No trigger measured** — the one confirmed deadlock has a 2.99 m clear gap, so nothing to avoid. Also needs a world→lane re-entry projection that `LaneGeometry.cs:7-9` forbids, and a non-holonomic box agent. |
| `checkRewindLinkLanes` (`NEED-checkrewindlinklanes-partial-port.md`) | The regression that motivated it **dissolved**; four real SUMO gaps remain, but nothing depends on them now. |
| N2 co-located vehicles, N3 net geometry, scenario-44 golden | Independent; N2 is a genuine engine defect (`__veh56`/`__veh84` identical pose for 9 steps). |
| `JunctionPhysicalOccupancyGate` widening | Measured **worse three times**. Needs `isLeader()` first — i.e. this task. |

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

    | Metric | baseline (off) | all gates ON | |
    | --- | --- | --- | --- |
    | `BOTH-INTERNAL-DIFFERENT-LANE` total | 15 | **20** | ⬆ WORSE (count) |
    | — STOPPED-FOE | 3, worst 2.382 m | 3, worst 2.382 m | unchanged |
    | — BOTH-MOVING | 12, worst **1.831 m** | **17**, worst **0.639 m** | count ⬆, depth ⬇ **−65%** |
    | vehicle-steps stopped on a `:` lane | 206 | **94** | ⬇ BETTER, **−54%** |

    **So the SECOND goal of this workstream is NOT met as specified.** The design's criterion was
    *"expect the 12 both-moving events to drop"*; the **count ROSE to 17**. What improved is *severity*
    (deepest both-moving −65%) and stopping-inside-junctions (−54%). Plausible reading — admission control
    keeps traffic **moving** through junctions, so more cars are inside one at once (more shallow events)
    while none wedges deeply — but that is a **reading, not a measured causal claim**. This is exactly the
    trade design §6.3 predicted ("may trade deadlock for junction overlap"), so it is reported, not spun.
    **Unexplained:** a new STOPPED-FOE pair absent from the baseline (`:d_3_4_10_2`/`:d_3_4_13_0`,
    1.959 m). With all gates ON, LiveCity is still **48/48** — `DemoCarOverlapInvariantTests` holds
    because 20 events spread across frames stay inside its ≤ 7 pairs/frame and 3.0 m ceiling.

**State at end of session 3:** gate green (**729/4/0**, `D96213B7BB4021A7` par==single, 48/48, 272/272),
tree clean, all pushed. **The arm-5 mutual deadlock is RESOLVED at SUMO's own defaults** — both vehicles
complete their routes, teleports at the ceiling, nothing regressed on any surface. The `isLeader` port is
**complete, faithful and safe but insufficient alone**; the load-bearing mechanism was **admission
control**. All three gates remain **default OFF**: turning them on changes outward-facing behaviour and is
an **owner decision**, with a genuine trade to weigh (see §9.54).

---

## 10. RESUME PROMPT (paste this into a fresh session)

> Continue the F3 junction workstream in `/home/user/SumoSharp` on branch
> **`claude/f3-junction-overlap-handoff-okf5nu`** (already pushed; 22 commits on top of `9ff0655`).
>
> **Read `docs/F3-SESSION-LOG.md` first, top to bottom** — it is the resumable record: environment
> bootstrap, gate numbers, six DISPROVEN handoff claims, what is shipped, the next task fully specified,
> and the traps that cost time. Do not re-derive anything marked disproven, and do not re-attempt anything
> in §6's "Parked" table.
>
> **⚠ READ THIS FIRST — SESSION 3 OUTCOME. The arm-5 deadlock is FIXED.** veh 95 and 102 now both
> complete their routes (t=427 / t=677; real SUMO 433 / 497) with **2** teleports, at SUMO's own
> `--ignore-junction-blocker -1` default. The fix is **`MSInternalJunction` second-stage admission**
> (`docs/F3-INTERNAL-JUNCTION-DESIGN.md`, T3.1/T3.2, §9.51-53), **not** `isLeader`.
> **All three gates are default OFF** — `ContTurnInsideJunctionGate`, `JunctionIsLeaderGate`,
> `InternalJunctionAdmissionGate` — and they are a **package**, to be judged together. The one open item
> is the **owner's defaults decision**, which has a real trade to weigh: the deadlock resolves and
> stopping-inside-junctions halves (206 → 94 vehicle-steps), but the F3 overlap event **count rises
> 15 → 20** even as the deepest both-moving penetration falls **1.831 m → 0.639 m** (§9.54). Baseline is
> now **729/4/0**. Do NOT re-attempt `isLeader` or the internal-junction port — both are done.
>
> **⚠ SESSION 3 RESULT — the `isLeader` port is COMPLETE, and it is NOT SUFFICIENT.** It is shipped
> behind a default-OFF `JunctionIsLeaderGate` and is measurably safe, but the arm-5 deadlock and the 12
> both-moving F3 overlaps **persist**. Traced (§9.49-50): the ordering works — `IsLeader(102,95)` is
> false 121/121 — but the call site is SUMO's own `isLeader(...) || inTheWay()` disjunction and
> `FoeIsInTheWay` is independently true 121/121, a symmetric geometric fact ordering cannot dissolve.
> **The real defect is that `MSInternalJunction` is unported**, so a cont turn's second stage has no
> admission control and a car enters it checking no foe. **START THERE:**
> `docs/NEED-internal-junction-second-stage-admission.md`. Do NOT re-attempt `isLeader` — it is done.
>
> **⚠ SESSION 3 UPDATE — the `isLeader` port is UNDERWAY, not unstarted.** It has its own design trio:
> `docs/F3-ISLEADER-PORT-{DESIGN,TASKS,TRACKER}.md`. Read the **DESIGN** (especially §0a's proof, §3b's
> two gap traps, §5b's `!WillPass` trap) and the **TRACKER** to see what is ticked. **T2.1/T2.2/T2.3 are
> done and confirmed; T2.4a/T2.4b (wiring behind a default-OFF `JunctionIsLeaderGate`) and T2.5
> (measurement + owner decision) remain.** Baseline is now **717/4/0**, not 689. Two extra
> non-negotiables learned in session 3: **(7) a new test calling `SumoShim.Run` MUST carry
> `[Collection(SumoShimEnvCollection.Name)]`** — a process-global env var made two of the five gridlock
> diagnostics unreliable (§9.43); and **(8) attempt 1 (`haveRed`), not the response matrix, is the arm
> that decides the measured deadlock** (§9.38) — a matrix-only reading of the problem tests the wrong
> branch and passes.
>
> **Original task framing: port SUMO's `isLeader()` — §6 of that log has the full spec** (source
> `sumo/src/microsim/MSVehicle.cpp:7343-7483`, consumed at `:3429` as `isLeader(...) || inTheWay()`; the
> algorithm in SUMO's order, the three new per-vehicle entry-time fields, where it plugs into
> `JunctionYieldConstraint` arm 5, and seven success conditions). The owner chose this **faithful** fix over
> the pragmatic `--ignore-junction-blocker` knob that is already shipped (default `-1`, off).
>
> **Before you start:** bootstrap the environment per §1 (the VM is volatile — .NET 8 and SUMO 1.20.0 must
> be reinstalled; note both traps), then run the §2 gate to confirm a clean baseline:
> `Sim.ParityTests` **689 passed / 4 skipped / 0 failed**, `Sim.Bench` hash **`D96213B7BB4021A7`** par==single,
> `Sim.LiveCity.Tests` **48/48**, `Sim.Pedestrians.Tests` **272/272**.
>
> **Non-negotiables, learned the hard way this session (§7):**
> 1. **"All goldens byte-identical" does NOT mean parity-inert.** The live-city demo and the five gridlock
>    diagnostics are not goldens. Measure both, every time.
> 2. **Verify the instrument before trusting its output.** THREE instrument-level defects were found this
>    session (OBB anchor, OBB forward axis, stale binder diagnostics) and each produced confident wrong
>    numbers before any engine bug was reached.
> 3. **Never mix harnesses.** `engine.Run()` directly and `SumoShim.Run` give different baselines on the same
>    scenario (4 vs 2 teleports); comparing across them produced a false negative once already.
> 4. **Test a hypothesis before implementing it.** Five were refuted by measurement this session; each cost
>    minutes to check and would have cost days to build.
> 5. Behavioural changes go behind a **default-off flag** until measured; goldens must stay byte-identical or
>    every shift must be justified by a live-SUMO 1.20.0 diff.
> 6. Keep appending to §9 of the log and updating §2–§6 in place, so the next compaction is survivable too.

### 10b. One-paragraph state summary (if you read nothing else)

F3 as briefed ("fix the junction occupancy gate, then assert zero overlap") was **four unrelated defects plus
three broken instruments**. Zero-overlap is unachievable *in principle* — SUMO does not guarantee it
(`--collision.check-junctions` defaults off; internal lanes overlap by construction). Two real engine fixes
shipped: the cont-turn `egoOnInternal` **mis-port** (SUMO tests a lane *property*, we tested lane-id equality —
this froze cars for 95 steps mid-junction) and the **stale binder diagnostics** (written only on the real pass,
but `ReuseIntent` skips it). The measuring instruments were also fixed: the OBB helper had a **reflected
forward axis** *and* a front-bumper-as-centre anchor — correcting both moved every F3 number and revealed the
axis error had been *hiding* seven real overlaps. What remains is one faithful port, `isLeader()`, which
resolves both the arm-5 mutual deadlock and the 12 remaining both-moving F3 overlaps.

**Session 3 added, in one line each:** that port is now **COMPLETE but INSUFFICIENT** — behind its own design trio, with
Stage 1 and the `isLeader` helpers **shipped and provably parity-inert** (written-not-read; 297
insertions / 0 deletions) and only the flag-gated wiring and the measurement left; the deadlock is now
backed by a **proof** rather than a plausible mechanism — attempt 1 (`haveRed`), *not* the response
matrix, is the operative arm, and it makes the symmetric state **structurally unreachable**; and a
**fourth instrument defect** turned up, this time in the test harness rather than the engine — a
process-global env var let one test's configuration leak into another, making two of the five gridlock
diagnostics fail about one run in three for reasons unrelated to any change under test. And the port
itself, though faithful and safe, **did not fix the deadlock**: the ordering fires correctly but is
OR-ed with a physical-presence term that is symmetrically true, and the state it arbitrates should never
have formed — a cont turn's **second stage has no admission control** because `MSInternalJunction` is
unported. The lesson generalises past this bug: **a correct port of the wrong mechanism is still a
wrong fix**, and only measuring the end-to-end outcome (not the mechanism's own unit tests) reveals it.
