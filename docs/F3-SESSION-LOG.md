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
| `dotnet test tests/Sim.ParityTests -c Release` | **672 passed / 4 skipped / 0 failed** |
| `dotnet run --project src/Sim.Bench -c Release` | hash **`D96213B7BB4021A7`**, `deterministic=True`, par==single |
| `dotnet test tests/Sim.LiveCity.Tests` (**no** `--no-build`; not in `Traffic.sln`) | **48 / 48** |
| `dotnet test tests/Sim.Pedestrians.Tests -c Release` | **272 / 272** |

672 = the historical 661 goldens + 9 `ContTurnInternalLaneOwnershipTests` + 2 diagnostics
(`Scenario44DefectDiagTests`, `ContTurnFlagOnGridlockDiagTests`).
The 4 skips are pre-existing (`LaneChangeOverlapDiagTests`, `RungC4vii…`, `RungP24…`, `RungP2Core…`).

**The 5 gridlock diagnostics are the load-bearing regression net** — they caught a bad change in one run and
quantified it. Never judge a junction change on goldens alone (§7, Lesson 1):
`WillPassSaturationDiagTests`, `DenseFlowDeadLaneDrainTests`, `RungHDp2g2CoordinatedLaneChangeTests`,
`RblLeftTurnsDiagTests`, `LowDensityTeleportTests`.

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

A/B switches: demo → `LIVECITY_CONTTURNFIX=1`, `LIVECITY_F3OCCUPANCY=1`; shim → `SUMOSHARP_CONTTURNFIX=1`
and `--ignore-junction-blocker <TIME>`.

## 6. NEXT ACTION — port `isLeader()` (the faithful fix; owner-chosen)

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
> **Your task: port SUMO's `isLeader()` — §6 of that log has the full spec** (source
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
