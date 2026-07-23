# COMPONENT2-INQUEUE-LANE-DRIFT-DESIGN.md — faithful port of SUMO's LaneQ `occupation` to stop in-queue drift into dead-end lanes

**Status:** DESIGN (design-first, per CLAUDE.md). Awaiting user nod before implementation.
**Branch:** `claude/dense-lane-overlap-fix-5tr4ha`. **SUMO ref:** `/sumo` (v1_20_0).
**Context docs:** `docs/CALIBRATION-KNEE-INDEX.md`, `scenarios/_repro/arterial-tjunction/FINDINGS.md`,
`docs/GETBESTLANES-RESUME.md` (rule-2, the free-flow sibling of this fix). SumoData relay:
`SUMOSHARP-RELAY-knee-is-multi-component-discharge.md` (component 2).

---

## 1. WHAT this fixes (the WHAT; the WHY-it-matters is in the relay/FINDINGS)

**Component 2 of the calibration-knee discharge gap:** under saturation, a turning vehicle that has correctly
reached its dedicated turn lane **lane-changes OFF it while stopped in the queue**, into a sibling lane that
has **no connection for its movement** (a dead-end lane for its route), and then **freezes there permanently**
(until teleport), becoming a hard blocker. SumoData measured final-lane illegal-occupancy for left-turners at
**32.3% vs vanilla 6.3%**, stranding ~30% of turners.

**Traced live on `scenarios/_repro/arterial-tjunction` (`art.sumocfg`, rule-2 engine `9a77d3b`):**

| veh | drift | outcome |
|---|---|---|
| `f_wsc.14` | `BC_2 → BC_1` at t=297 **at speed 0** | frozen on `BC_1` (no `→CSc` connection) to the stop line ~60 s |
| `f_wsc.15` | `BC_2 → BC_1 → BC_0` (t=493/509, **stopped**) | drifted across TWO lanes; frozen at stop line ~30 s |
| `f_wsc.13` | `BC_2 → BC_1 → BC_2` | oscillated, ~90 s wasted |

The signature: **a discretionary keep-right change fires on a vehicle at speed 0 deep in the turn queue.**

## 2. ROOT CAUSE (mechanism, verified)

SumoSharp's keep-right stay-suppressors (`ApplyKeepRightDecision` → rule 3 VARIANT_21 and the new rule 2)
both compute the position-relative remaining room on the route-leaving right lane as
`neighLeftPlace = MAX2(0, neighDist − posOnLane − maxJam)` and stay-on-best when `neighLeftPlace` is small.
**SumoSharp hardcodes `maxJam = 0`** ("empty-road occupation scope" — the same simplification carried through
the strategic/keep-right code, e.g. `usableDist`'s `best.occupation` term). SUMO's `maxJam`
(`MSLCM_LC2013.cpp:1290`) is:

```
maxJam = MAX2(preb[currIdx + prebOffset].occupation, preb[currIdx].occupation)
```

i.e. the **occupation (jam length) of ego's current lane and the right-neighbour lane**. Under saturation a
turner sits in a long queue on its turn lane → its **current-lane occupation is large** → `neighLeftPlace`
collapses toward 0 → the stay rule fires → SUMO **suppresses the discretionary change and the turner stays on
its lane.** With `maxJam = 0` SumoSharp never suppresses mid-queue, so keep-right drifts the stopped turner
off its lane into the dead-end sibling. **The fix is the missing `occupation` term — not a new mechanism.**

## 3. SUMO's `occupation`, precisely (so the port is faithful, not invented)

`MSVehicle.cpp:6427` `adaptBestLanesOccupation`: `preb[i].occupation = density + preb[i].nextOccupation`, where

- **`density`** = `myChanger[i].dens` (`MSLaneChanger.cpp:96,397`): as the lane changer walks vehicles on edge
  `e` front-to-back, `dens` for lane `i` accumulates `getVehicleType().getLengthWithGap()` of each vehicle
  already processed on lane `i`. At the moment ego is processed, `myChanger[i].dens` = **Σ lengthWithGap of the
  vehicles ahead of ego's position on lane `i`.** (Per-lane, ego-relative, this edge only.)
- **`nextOccupation`** (`MSVehicle.cpp:6213-6222` `updateOccupancyAndCurrentBestLane`): **Σ
  `getBruttoVehLenSum()` over the downstream best-continuation lanes** of lane `i` (its `bestContinuations`,
  skipping the current lane). `getBruttoVehLenSum()` = total brutto vehicle length currently on that lane.

So `occupation(i)` = (vehicles ahead of ego on lane `i` this edge) + (vehicles on lane `i`'s downstream route
continuation). It is a **live, per-step, ego-relative traffic quantity** — which is exactly why it cannot live
in the topology-only, route-keyed, cached `ComputeBestLanes` (that cache is a pure function of net+route and is
shared across vehicles/steps). It must be computed at the decision site from the frozen post-move lane state.

## 4. THE TWO CONSUMERS in SUMO (and where they already exist, gated, in SumoSharp)

1. **`maxJam` → `neighLeftPlace`** (`MSLCM_LC2013.cpp:1290,1297`) — feeds rule 2 (and, via `neighLeftPlace`,
   the "can I get back in time" family). In SumoSharp: `ApplyKeepRightDecision`, my rule-2 block, currently
   `neighLeftPlace = MAX2(0, KeepRightStayRightContLength − pos)` with `maxJam` implicitly 0. **Gate:**
   only reached when `KeepRightStayRule2Eligible` (ego on its best lane, right neighbour leaves the route) —
   already proven golden-inert (the rule-2 commit `9a77d3b` was byte-identical).
2. **`best.occupation` → `usableDist`** (`MSLCM_LC2013.cpp:1288`):
   `usableDist = MAX2(currentDist − posOnLane − best.occupation*JAM_FACTOR, driveToNextStop)` (`JAM_FACTOR=1`).
   In SumoSharp: `TryStrategicLaneChange`, `usableDist = curr.Length − pos` (occupation term = 0). **Gate:**
   `TryStrategicLaneChange` only does work when ego's actual lane ≠ its route-pool target on this edge (a drift
   lane) — already golden-inert for every committed scenario EXCEPT it IS exercised by the Gap-1 synthetic
   (dead-lane drainage). ⇒ **must re-verify Gap-1 stays 2×0/290, 1×≤2 tp.**

Both consumers sit behind gates that are (1) or (mostly) golden-inert, so adding the real `occupation` term
stays byte-identical for the committed suite. Because `occupation` makes rule 2 fire *more* (smaller
`neighLeftPlace`) and makes the strategic change *more* urgent (smaller `usableDist`), the byte-identical
claim is not automatic — it rests on the gates never being active-with-a-changed-outcome in a golden, which
**the full suite + Gap-1 anchors must confirm empirically** (§8).

## 5. DESIGN — the port

### 5.1 New helper: `LaneOccupation(laneHandle, fromPos, downstreamContinuation)`
A method on `Engine` (not `NetworkModel` — it reads live vehicle state), computed from the **same frozen
post-move `neighbors` snapshot** the keep-right/strategic phases already read (determinism: never a live read
of another vehicle's mutating state):

```
occupation(lane, fromPos, contLanes) =
      Σ lengthWithGap(w) for each vehicle w on `lane` with w.Pos > fromPos          // SUMO density (ahead of ego)
    + Σ bruttoVehLenSum(c) for each lane c in `contLanes`                            // SUMO nextOccupation
```

- **`density` term:** iterate the vehicles on `lane` ahead of `fromPos` (the neighbor snapshot already indexes
  per-lane occupants; this is the same traversal `GetNeighborLeader` uses). `lengthWithGap = VType.Length +
  VType.MinGap`.
- **`nextOccupation` term:** `contLanes` = the lane's downstream best-continuation lanes. For the CURRENT lane
  ego is on, this is the remaining route-pool slice (`_laneSeqPool[LaneSeqIndex+1 ..]`, normal edges only). For
  the RIGHT-neighbour lane (which by definition leaves the route — rule-2 eligibility), its continuation is
  short/absent; a faithful first cut uses that lane's own onward best-continuation, but see §5.4.
  `bruttoVehLenSum(c)` = Σ `VType.Length` of vehicles currently on lane `c` (brutto = no gap; SUMO
  `getBruttoVehLenSum`).

Determinism: reads only the frozen snapshot + immutable net + ego's own pool. Serial == region-parallel holds
(same discipline as every other term in this phase). Per-vehicle, per-step; the density traversal is the same
cost class as the leader lookups already done here.

### 5.2 Wire into `maxJam` (consumer 1 — the component-2 fix proper)
In `ApplyKeepRightDecision`'s rule-2 block:
```
var occCurr  = LaneOccupation(lane.Handle,       v.Kinematics.Pos, currentDownstreamContinuation);
var occRight = LaneOccupation(rightLane.Handle,   v.Kinematics.Pos, rightDownstreamContinuation);
var maxJam   = Math.Max(occCurr, occRight);
var neighLeftPlace = Math.Max(0.0, v.KeepRightStayRightContLength - v.Kinematics.Pos - maxJam);
```
(Everything else in the rule-2 block unchanged.) This makes rule 2 fire for a turner queued on its turn lane
→ keep-right suppressed → no drift.

### 5.3 Wire into `usableDist` (consumer 2 — faithful completeness)
In `TryStrategicLaneChange`, replace `usableDist = curr.Length − pos` with
`usableDist = MAX2(curr.Length − pos − occCurr*JAM_FACTOR, driveToNextStop)` (JAM_FACTOR=1). This is the same
`occCurr`. It makes the strategic change-BACK to the turn lane more urgent when the approach is jammed — the
correct companion to §5.2 (a turner that did drift now fights back to its lane sooner). **This is the term the
Gap-1 synthetic can feel**, so it is a SEPARATE task (Stage 2) gated on Gap-1 re-verification; if it perturbs
Gap-1, it is reverted/reworked independently of Stage 1.

### 5.4 Scope decisions (documented, faithful-or-justified)
- **`nextOccupation` for the RIGHT lane:** the right lane leaves ego's route, so its "downstream continuation
  toward ego's route" is empty; SUMO uses that lane's OWN `bestContinuations`. First cut: use the right lane's
  own onward-continuation occupancy (or 0 if it splits immediately). The **current-lane** density term
  dominates the turn-queue case (§2), so this simplification does not change the fix's effect; documented and
  revisited only if a repro needs it.
- **`density` ordering:** SUMO's `dens` is "vehicles the changer already processed ahead of ego." We compute it
  directly as "vehicles ahead of ego's pos on the lane," which is the same set for the semi-implicit Euler,
  single-action-step, `sigma=0` regime (no mid-edge processing-order dependence that changes the ahead-set).
  Documented simplification; exact for phase-1 determinism.

## 6. WHAT THIS DELIBERATELY DOES NOT DO
- Not component 1 (permissive-turn gap-acceptance under saturation) — separate.
- Not component 3 (protected-green through under-discharge) — separate; SumoData is disambiguating whether it
  is a real through-release bug or downstream of components 1+2 (their leader-presence check).
- Not a general `occupation` rollout to every SUMO site that has it — only the two consumers behind the two
  already-gated LC decisions above. A broad rollout would touch un-gated hot paths and is out of scope.

## 7. TASKS (success conditions are mandatory and measurable)

**Stage 1 — `maxJam` / component-2 fix (the load-bearing one):**
- **T1.1** Add `LaneOccupation(...)` (§5.1) + resolve the current/right downstream continuation lists. Files:
  `src/Sim.Core/Engine.cs`.
- **T1.2** Wire `maxJam` into rule 2's `neighLeftPlace` (§5.2). Files: `src/Sim.Core/Engine.cs`.
- **Success T1:** (a) full suite **byte-identical** 657 parity + 227 pedestrian, 0 failed; (b) deterministic
  serial == `--max-parallelism 8` on `art.sumocfg`; (c) on `art.sumocfg` the drift-and-freeze cases are gone —
  **left-turner illegal-final-lane occupancy (on `BC_0`/`BC_1` at the stop line) drops toward vanilla**, and
  no `f_wsc` vehicle changes OFF `BC_2` into `BC_1`/`BC_0` while `speed < 0.5` (assert via FCD trace, the
  `/tmp/drift.py` check → 0 cases); (d) served-density discharge on the arterial improves toward vanilla
  (running 460→lower, arrived 867→higher) — reported, not gated to an exact number (the knee has other
  components).

**Stage 2 — `usableDist` occupation term (faithful completeness, independent):**
- **T2.1** Wire `occCurr*JAM_FACTOR` into `TryStrategicLaneChange`'s `usableDist` (§5.3).
- **Success T2:** (a) full suite byte-identical; (b) **Gap-1 synthetic stays 2×0/290, 1×≤2 tp** (the anchor
  `DenseFlowDeadLaneDrainTests`); (c) permissive-yield `lt` stays 7; signalized-asymmetry stays at parity;
  (d) determinism holds. If (a) or (b) fails, Stage 2 is reverted and re-examined WITHOUT blocking Stage 1.

**Stage 3 — anchor + docs:**
- **T3.1** Add a committed offline anchor (SumoShim, no SUMO at test time) asserting no in-queue drift into a
  non-continuing lane on a saturated multi-lane turn approach (a small committed repro or an assertion on the
  arterial FCD via the shim).
- **T3.2** Update `FINDINGS.md`, `CALIBRATION-KNEE-INDEX.md` (add component-2 row), this doc's tracker.
- **Success T3:** anchor is red on the pre-fix engine, green on the fixed engine; docs updated.

## 8. PARITY GATES (iron law)
1. Full `dotnet test Traffic.sln` byte-identical: 657 parity + 227 pedestrian. Highest risk: multi-lane +
   keep-right/strategic goldens (LANE-CHANGE-OVERLAP, keep-right rung 8b, cooperative-LC, dense-LC rungs).
2. Gap-1 `DenseFlowDeadLaneDrainTests` unchanged (arrivals ≥ 290, tp ≤ 2) — Stage 2's specific risk.
3. Deterministic: serial == `--max-parallelism 8`.
4. Validate the FIX at **served/sustained density** (`art.sumocfg`), never sparse-probe/one-shot flow
   (knee-selection artifact).
5. This closes component 2 only; the knee has components 1 + 3. Re-measure and hand to SumoData; do NOT expect
   full knee closure from this alone.

## 9. TRACKER
- [ ] T1.1 LaneOccupation helper + continuation lists
- [ ] T1.2 maxJam → neighLeftPlace
- [ ] Success T1 (byte-identical + determinism + drift gone + discharge readout)
- [ ] T2.1 occupation → usableDist
- [ ] Success T2 (byte-identical + Gap-1 anchor + yield/asymmetry anchors + determinism)
- [ ] T3.1 committed offline anchor
- [ ] T3.2 docs updated
- [ ] Hand-off to SumoData (re-run the box; report residual = components 1 + 3)

---

## OUTCOME 2026-07-22 (session 5) — implemented, measured, REVERTED; component 2 is entangled + low-ceiling

Implemented Stage 1 (LaneOccupation density + nextOccupation → maxJam → rule 2) and measured on
`art.sumocfg`. **The faithful `maxJam` fix does NOT eliminate the drift** — empirically:

| variant | drift-and-freeze (BC_2→BC_1/0, stuck≥30) | near-stopline illegal % | running/arrived/meanSpeed |
|---|---|---|---|
| rule-2 only (baseline) | ~19 | ~44 | 460/867/2.84 |
| + density maxJam | 19 | 43.9 | 434/857/3.09 |
| + density + nextOccupation | 20 | 44.3 | 393/855/2.44 |
| **full keep-right suppression** (upper bound) | **0** | **27.8** | 383/921/2.69 |
| vanilla | 0 | 0.0 | 303/1094/6.14 |

**Why maxJam fails:** the drift fires EARLY (turner at pos ~150, partial queue ahead), before the local
occupation is large enough to make `neighLeftPlace*2 < laDist`. Determinism held (serial == `-p 8`).

**Why vanilla never drifts (`--lanechange-output`):** vanilla makes exactly ONE `f_wsc` change on BC in the
whole run — `BC_1→BC_2` (strategic|urgent). It NEVER right-changes off `BC_2`. The mechanism: vanilla
segregates correctly (through on BC_0/1, turners on BC_2), so whenever a turner is on BC_2 the right lane BC_1
has a through LEADER → SUMO's keep-right accumulator `fullSpeedGap` collapses (neighLead term) → keep-right
never builds. **SumoSharp's drift is a downstream symptom of the initial mixing (merge-in failure): mixed
lanes → BC_1 sometimes clear ahead of a turner → keep-right fires → more mixing. A vicious cycle seeded by
component 1.**

**Two conclusions:**
1. **Component 2 is not cleanly separable from component 1** and its independent ceiling is modest: even
   perfect drift elimination (full-suppress) only moves illegal 44→28%. The dominant ~28% residual is turners
   that **never reach BC_2** (LCA_URGENT merge-in / gap-acceptance under saturation — component 1 family).
2. **Full keep-right suppression matches vanilla here (0 changes)** but is NOT byte-identical-safe in general
   (SUMO DOES keep-right onto a route-leaving lane when `neighDist ≥ 200`, room to return) — so it is not a
   faithful drop-in.

**Recommendation:** pause component 2; pivot to the **merge-in / gap-acceptance under saturation** lever
(component 1 family), which dominates the turn-lane failure AND, per SumoData, is a top knee component. Revisit
component 2 (keep-right drift) only after segregation-under-load is fixed — much of it should evaporate. WIP
reverted; tree clean at rule-2 (`9a77d3b`) + docs.
