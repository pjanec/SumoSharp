# DESIGN — the approach arm of internal-junction admission (`myInternalLinkFoes`)

**Status:** DESIGN, awaiting owner sign-off. No `Engine.cs` edit until this is agreed (CLAUDE.md
design-first).
**Companion docs:** `JUNCTION-REALISM-TRACE-FINDINGS.md` (the WHAT and the evidence — this doc does not
restate it), `JUNCTION-APPROACH-ARM-TASKS.md` (the work), `JUNCTION-APPROACH-ARM-TRACKER.md` (the list).
**Owner decision on record (this session):** the approach arm is to be **global and faithful**, not
zone-scoped — *"we can be faithful to SUMO only where it does not harm the realism we need"*, which makes
`CONSTRAINT-high-realism-artefact-ladder.md` the veto on this port, not parity.

---

## 1. What is missing, in one sentence

`InternalJunctionAdmissionConstraint` (`Engine.cs:7878`) decides whether a cont-turn vehicle may leave
its stage-1 bay by asking **who is standing on the foe lanes**. SUMO asks that *and* **who is
approaching the foe links and will be there**, and it is the second question that holds the left-turner
in `JUNCTION-REALISM-TRACE-FINDINGS.md` §4.

## 2. The SUMO mechanism, and proof it has a live consumer

CLAUDE.md lesson 10 is mandatory here: a previous session spent a day porting `addBlockedLink` before
discovering it was dead. So, writer → reader → reader's callers, checked by grep:

`MSInternalJunction::postloadInit` (`MSInternalJunction.cpp:55-127`) builds **two** foe sets and hands
both to one call:

```cpp
thisLink->setRequestInformation(ownLinkIndex, true, false, myInternalLinkFoes, myInternalLaneFoes, ...);
//                                                          ^ foeLinks           ^ foeLanes
```

* `myInternalLaneFoes` — from `myInternalLanes` (the internal junction's own `intLanes`), filtered by
  `response.test(foeIndex)`, plus unconditional `addIfAbsent` of each via-lane. **We have this.**
* `myInternalLinkFoes` — from `myIncomingLanes` **starting at index 1**, i.e. every incoming lane
  *except* the checker/bay lane, taking each of its links whose `getCorrespondingEntryLink()->getIndex()`
  is set in `response[ownLinkIndex]`, plus the follow-on internal-junction link when the via lane itself
  has a via lane (`MSInternalJunction.cpp:100-110`). **We do not have this.**

Consumer chain, verified:

| step | where | note |
|---|---|---|
| `myFoeLinks = foeLinks` | `MSLink.cpp:210` | the writer |
| `opened()` loops `foeLinks` | `MSLink.cpp:845, 856` | **live** — the admission function |
| → `link->blockedAtTime(...)` | `MSLink.cpp:869` | |
| → iterates `link->myApproachingVehicles` | `MSLink.cpp:~880` | the approach registry |
| → `blockedByFoe(...)` | `MSLink.cpp:~917` | the arrival-window predicate |

⚠ `postloadInit` **also** ends with `thisLink->addBlockedLink(link)` for every link foe. **Do not port
that** — `myBlockedFoeLinks`' only reader is `MSLink.cpp:697`, whose call sites are commented out in
1.20.0. It is the dead path the previous session already paid for; it is listed here only so the next
reader does not rediscover it in `postloadInit` and assume it is load-bearing.

**Why `opened()` runs at all on this link, which is the non-obvious part.** `opened()` short-circuits
`if (isCont() && gUsingInternalLanes) return true;` — so a *cont* link never checks. But
`postloadInit` passes `isCont=**false**` for `thisLink` (the link at the END of the bay lane). The bay
entry link is cont; the bay *exit* link is not. That asymmetry is the whole reason SUMO has an
admission decision here at all, and it is what our `Cont` + `IntLanes[i] != ownLane` test at
`Engine.cs:7910-7922` is already (correctly) reproducing.

## 3. The data mapping — what we already have

| SUMO | ours | state |
|---|---|---|
| internal junction, `incLanes`, `intLanes` | `NetworkModel.InternalJunction` | **exists** (T3.1) |
| checker/bay lane → its internal junction | `InternalJunctionByBayLane` | **exists** |
| `myInternalLaneFoes` | `InternalLaneFoes[IJ.Id]` | **exists**, verified correct on the repro |
| `ownLinkIndex`, `response` | `LinkIndexByInternalLane` + `Junction.Requests[i].Response` | **exists** |
| `myInternalLinkFoes` | — | **TO BUILD (T1)** |
| `myApproachingVehicles` | `_foeCrossFirst/_foeCrossSecond[laneHandle]` | **exists**, see §4 |
| `blockedByFoe` arrival window | partly, in `JunctionYieldConstraint` | **to reuse/extract (T3)** |

## 4. `_foeCrossFirst/Second` IS our approach registry — with two differences that matter

`BuildFoeApproachIndex` (`Engine.cs:9340`) records, per internal lane handle, the first two active
vehicles whose *remaining* lane sequence contains it (`i >= v.LaneSeqIndex`), excluding parked ones.
On the repro at t=49 the foe `f_cyc_cw2.1` is on `in_W01_0` with `:J01_10_0` still ahead, so it is in
`_foeCrossFirst[:J01_10_0]` — the vehicle our occupancy scan cannot see. This is the right registry.

Two deviations from SUMO to state up front rather than discover later:

1. **Keyed by internal LANE, not by LINK.** SUMO registers approaches on the link; we index by the
   lane the link leads onto. For the link foes we need, the via lane is exactly that lane, so the
   mapping is total — but it means a link foe with **no** via lane (`getViaLane() == nullptr`) has no
   key. Those exist only where internal lanes are disabled, which no committed scenario does; T1 must
   **assert** it rather than assume it (a silent empty set would make this whole arm inert and look
   like a null result).
2. **First-two, not all.** The index holds two vehicles per lane, not the full approach set. SUMO
   iterates all of `myApproachingVehicles`. For a FIFO approach the first is the nearest, so testing
   the nearest two is a *conservative superset check* for the blocking case — but it is a deviation
   and must be recorded as one. If a measurement ever hinges on a third approaching vehicle, this is
   the first thing to widen.

## 5. The algorithm

Inside `InternalJunctionAdmissionConstraint`, after the existing lane-foe loop (which stays exactly as
it is), and reached only when that loop did **not** already block:

```
for each foeLaneHandle in InternalLinkFoes[IJ.Id]:
    for each foe in { _foeCrossFirst[h], _foeCrossSecond[h] }, skipping ego and parked:
        if !foe.WillPass:                      continue     // MSLink.cpp:918, avi.willPass
        if foe is already ON an internal lane:  continue     // the lane-foe loop owns that case
        if ArrivalWindowBlocks(ego, foe):       blocked = true
```

`ArrivalWindowBlocks` is `blockedByFoe`'s shape, restricted to the branches this configuration can
reach. **Deliberate omissions, each with its reason** (the ladder constraint says target SUMO's flow,
not transplant its every branch):

* `impatience` foe-arrival-time blending — impatience is 0 in phase 1 (`sigma=0`, no `jm*` params in
  any committed vType). Guarded omission: assert impatience==0 at the call site.
* `LINKSTATE_ALLWAY_STOP` waiting-time arbitration — a bay exit link is never an all-way stop.
* the `jmIgnoreFoe*` probabilistic branches — they require an RNG draw, and CLAUDE.md forbids
  introducing one. No committed vType sets them.
* `mySublaneFoeLinks` — `lateral-resolution` is `-1` in every committed scenario and in the repro.

**What is NOT omitted, because it is the deadlock-avoidance:** `blockedByFoe`'s `avi.leavingTime <
arrivalTime` branch ("ego wants to be follower") and the symmetric ordering. CLAUDE.md lesson 11 —
*a symmetric predicate cannot arbitrate a cycle* — is the single most likely way this port creates a
new deadlock: "is anyone approaching my foe link?" is symmetric across a 4-way, so two opposing bays
would each wait for the other forever. **The arrival-time comparison is the tie-break, and it must be
total.** T3's success condition asserts precisely this on a symmetric fixture.

## 6. Determinism and parallel safety

* Reads only `_foeCrossFirst/Second` (built single-threaded in `BuildFoeApproachIndex` before the plan
  phase), `WillPass` (written by the `ComputeWillPass` pre-pass, also before), and immutable network
  data. Writes nothing. So it is safe in the parallel plan phase by the same argument
  `JunctionYieldConstraint` already relies on.
* No `System.Random`, no iteration-order dependence: the foe set is a parse-time-ordered list and the
  arrival comparison's tie-break terminates in `string.CompareOrdinal` on vehicle id, never
  `EntityIndex` or an array position.
* Returns a speed through the same `StopSpeedFor`-at-bay-end shape the existing arm uses, folded into
  the same `Math.Min` stack. No new binder tag — this is arm 14 gaining a second reason to bind, and
  the diagnostic must distinguish them (T5) or the next investigator cannot tell which half fired.

## 7. Parity — this is NOT inert, and that is expected

`InternalJunctionAdmissionGate` is default **ON**, so unlike a zone-scoped change this **can move
goldens**. The discipline, per CLAUDE.md and the F3 precedent:

1. Run the full gate. If all 661 stay byte-identical: the arm is real but no fixture exercises a
   cont-bay-vs-approaching-foe conflict, which is plausible (the goldens are 2–5 vehicle scenarios) —
   and then the repro is the only evidence, so it must carry the proof.
2. **If a golden shifts, it is not accepted on argument.** Run live SUMO 1.20.0 on that exact
   net+demand and diff; the shift is accepted only if it moves us TOWARD SUMO. Otherwise the port is
   wrong and gets reworked. This decision is owner-visible, never silent.
3. `Sim.Bench` hash `BF3794A4704BCD79`, par == single.

## 8. The realism veto (owner's constraint, and where it bites)

Faithfulness is the default, but `CONSTRAINT-high-realism-artefact-ladder.md` overrides it. One place
this could bite: SUMO's admission is ultimately a **1-D longitudinal** model, and it tolerates internal
lanes that overlap by construction (`MSLink.cpp:334-366`) with junction collision checking **off by
default**. So a faithful port gets us SUMO's *flow* but does not by itself guarantee zero
interpenetration — SUMO merely happens to have none on this net. **Do not promote "0 overlaps on the
repro" into "overlaps are structurally impossible".** If the owner needs the stronger geometric
guarantee inside high-realism zones, that is a *separate*, zone-scoped gate stacked on top of this
one — and it is the right place for `JunctionPhysicalOccupancyGate`, which is default-OFF today
precisely because it is too blunt to be global.

## 9. Success conditions

1. **The repro's seed case is fixed, asserted directly, not via an aggregate:** on
   `junction-realism-L1`, `f_cyc_ccw2.0` does **not** occupy `:J01_13_0` while `f_cyc_cw2.1` is on
   `:J01_10_0`; it is held on the bay `:J01_5_0`. This must FAIL with the arm off (non-vacuous).
2. **Junction-interior OBB overlaps → 0** on both `junction-realism-L1` and `-L2`, measured with
   `scripts/analyze-junction-realism-fcd.py`.
3. **The gridlock question is answered either way, and reported honestly.** Re-run
   `JUNCTION-REALISM-TRACE-FINDINGS.md` §2/§3. If `arrived` goes to 450/450 and the network drains, the
   overlap *was* the gridlock. **If overlaps go to 0 and it still gridlocks, that is a real and
   publishable result** — the two are independent defects and the gridlock gets its own trace. §5's
   hypothesis is not permitted to survive a null here.
4. **No new deadlock:** `NEED-multilane-junction-passage.md` and
   `NEED-priorityjunction-farrouted-foe-falsepositive.md` do not regress; the symmetric-cycle assertion
   of §5 passes; `DenseFlow…NoGridlock` and the LiveCity suite stay green.
5. Full gate: `Sim.ParityTests` **775/4** with 661 goldens byte-identical (or every shift SUMO-diffed
   per §7), `Sim.Bench` `BF3794A4704BCD79` par==single, `Sim.LiveCity.Tests` **90/90**,
   `Sim.Pedestrians.Tests` **324/324**.

## 10. Risks, ranked

1. **A new symmetric deadlock** (§5). Highest likelihood; it is exactly how gate 3 behaved without its
   entry-order sub-gate (a 4890-step wedge). Mitigated by the total tie-break + a dedicated fixture.
2. **The arm is inert** because `InternalLinkFoes` comes out empty (§4.1's null via-lane, or a
   response-bit indexing error). ⚠ `NetworkModel.Bit` is **rightmost-bit-is-index-0**; a hand-decode
   got this backwards twice in this workstream, and both times the error flattered the fix. T1's test
   must assert a *specific known* foe set, not merely a non-empty one.
3. **Golden movement** (§7) — expected, planned for, not a surprise.
4. **Over-yield at density**: holding bays longer can cut throughput. This is why §9.3 re-runs the
   discharge numbers rather than only the overlap count.
