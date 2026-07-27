# F3 — design: porting `MSInternalJunction` (cont-turn SECOND-STAGE admission)

**Status:** design. Tasks in §7; tracker rows appended to `docs/F3-ISLEADER-PORT-TRACKER.md`.
**WHY this exists:** `docs/NEED-internal-junction-second-stage-admission.md` — the measured root cause of
the veh 95 / 102 deadlock, found after the `isLeader` port failed to resolve it.
**Source of record:** `sumo/src/microsim/MSInternalJunction.cpp` (`postloadInit`, `indirectBicycleTurn`).

This is HOW. The WHAT and the evidence are in the NEED doc; not restated here.

---

## 0. The one-sentence problem

A cont (two-stage) turn's vehicle advances from its stage-1 **bay** into stage 2 **checking no foe at
all**, because all internal junctions parse inert in our engine — so it wedges itself into a conflict
area SUMO would never have admitted it to.

## 1. ⚠ Correction to the NEED doc's fix sketch

The NEED doc says *"every lane in the internal junction's `intLanes` whose link index is set in the
parent's `Requests[ownLinkIndex].Response`"* becomes a foe. **That is wrong, and wrong in the direction
that matters** — it would have made the load-bearing case conditional on a bit that is not consulted.

Reading `postloadInit` (`MSInternalJunction.cpp:78-95`) the rule is **two-branch**, on whether the
candidate `intLanes` lane itself leads to another internal lane:

```cpp
for (MSLane* const lane : myInternalLanes) {            // the internal junction's intLanes
    for (MSLink* const link : lane->getLinkCont()) {
        if (link->getViaLane() != nullptr) {            // candidate is a cont STAGE-1 bay
            const int foeIndex = lane->getIncomingLanes()[0].viaLink->getIndex();
            if (response.test(foeIndex) || indirectBicycleTurn(...)) {
                myInternalLaneFoes.push_back(lane);     // conditional: only if WE yield to it
            }
            addIfAbsent(myInternalLaneFoes, link->getViaLane());   // its STAGE-2 lane: ALWAYS
        } else {                                        // candidate is a PLAIN internal lane
            addIfAbsent(myInternalLaneFoes, lane);      // ALWAYS
        }
    }
}
```

SUMO's own comment explains the asymmetry: *"only respect vehicles **before** internal junctions if they
have priority"* — a car merely **waiting in another bay** is a foe only when it outranks us, but a car
**actually crossing** (on a plain internal lane, or on someone's stage 2) is **always** a foe.

**Verified on `:2336_42_0`** (`scenarios/_repro/synthetic-junction2`): 14 `intLanes` entries →
**13 unconditional** foes (`:2336_2_0, :2336_3_0, :2336_10_0, :2336_11_0, :2336_21_0, :2336_22_0,
:2336_23_0, :2336_24_0, :2336_26_0, :2336_27_0, :2336_33_0, :2336_34_0, :2336_34_1`) plus **one
cont** candidate `:2336_25_0`, whose `response[18][25]` is **false** so the bay lane itself is *not* a
foe while its stage-2 lane `:2336_44_0` **is**. Final foe set: **14 lanes**.

**`:2336_3_0` — veh 102's lane — is an UNCONDITIONAL foe.** So the deadlock is prevented without
consulting the response matrix at all. Simpler and more robust than the NEED doc's sketch.

## 2. What the foe set is used for

```cpp
thisLink->setRequestInformation(ownLinkIndex, /*hasFoes=*/true, /*isCont=*/false,
                               myInternalLinkFoes, myInternalLaneFoes,
                               thisLink->getViaLane()->getLogicalPredecessorLane());
```

`thisLink` is the **bay→stage-2** link. It receives the internal junction's own two foe sets — exactly
the `myFoeLinks` / `myFoeLanes` split the parent junction uses (`F3-JUNCTION-OVERLAP-DESIGN.md` §2).
So an internal-junction link behaves like an ordinary junction link with its own foes, and
`MSLink::opened()` / `getLeaderInfo()` apply unchanged.

Two further effects, both **omitted with reasons** in §5:
- the **exit** link (stage-2 → normal) also receives `myInternalLaneFoes` (`hasFoes=false`);
- `addBlockedLink` mutual registration between `thisLink` and each `myInternalLinkFoes`.

## 3. Where it goes in our engine

The decision must be made in the **plan** phase, not at the move-phase lane advance: a vehicle that may
not enter stage 2 has to **brake to a stop at the end of its bay lane**, which is a speed constraint.

New arm, `InternalJunctionAdmissionConstraint`:

> **If** ego is on a cont **stage-1 bay** lane of internal junction `IJ`, **and** any lane in
> `IJ.InternalLaneFoes` is occupied by a vehicle, **then** constrain ego with
> `StopSpeedFor(..., bayLane.Length - ego.Pos - PositionEps, ...)`.

That is the same `StopSpeedFor`-at-lane-end shape the existing approaching-foe arms use, so it composes
with `Math.Min` over the other constraints and needs no new machinery.

**"Occupied" means physically on the lane** — the `myFoeLanes` half of the split, i.e. our
`FindCrossFoeVehicle`-style presence test, **not** an approach/`WillPass` test. The `myInternalLinkFoes`
half (approaching foes) is §5's omission.

### 3a. Identifying the bay

Ego is on a bay lane iff its current lane is an internal lane of a junction **and** is *not* in that
junction's `intLanes` — precisely the shape T2.1 established (`LinkIndexByInternalLane` maps both cont
stages; `IntLanes` holds only stage 2). Equivalently: `LinkIndexByInternalLane` resolves it, its link
index `i` has `Requests[i].Cont`, and `IntLanes[i] != ego.LaneId`.

## 4. New network data

`Junction` currently discards internal junctions: `ParseJunction` bails on
`intLanes.Count == 0 || requestEls.Count == 0`, and **all 251** internal junctions in this net have zero
`<request>` rows. Needed:

- **`InternalJunction`** records — `Id`, `IncLanes`, `IntLanes` — parsed for `type="internal"`.
- **`InternalJunctionByBayLane : string → InternalJunction`** — the checker mapping, keyed on the
  **first** `incLanes` entry (SUMO: *"the first lane in the list of incoming lanes is special"*).
- **`InternalLaneFoes : InternalJunction → IReadOnlyList<int>`** (lane handles), built per §1.

All parse-time, read by exactly one new arm ⇒ **inert until the arm is enabled**.

## 5. Deliberate omissions, each with a guard

| Omitted | Why | Guard |
| --- | --- | --- |
| `myInternalLinkFoes` (approaching foes) + `addBlockedLink` | The measured defect is **physical occupancy** of a foe lane; approach-gating is a second, independent behaviour whose blast radius is much larger. Adding both in one change would make the measurement uninterpretable — the mistake that ruined the first `--ignore-junction-blocker` A/B. | Filed as a follow-up in the tracker; the arm's own comment states it |
| `indirectBicycleTurn` | **0 of 134** committed nets contain an indirect link (already measured) | the existing `IndirectLinkGuard` test |
| exit-link foe lanes (`hasFoes=false`) | Constrains leaving stage 2, not entering it. Not the measured defect. | stated in the arm's comment |
| walking-area foe exits | no walking areas in the affected nets | — |

## 6. Parity argument

1. **Default OFF.** New flag `InternalJunctionAdmissionGate = false`; the arm returns
   `double.PositiveInfinity` when off, so `Math.Min` is unaffected and goldens are byte-identical **by
   construction**.
2. **Reachability is narrow even when on:** only a vehicle on a cont stage-1 bay lane is ever
   constrained. **26 of 134** nets contain cont links at all.
3. **Measure all four surfaces**, per the standing lesson that goldens alone are insufficient: 661
   goldens, the **five gridlock diagnostics**, the live-city F3 buckets, and `Sim.Bench` hash +
   `par == single`.
4. **The intended direction is a car waiting LONGER** (held in the bay). So expect *possibly more*
   stopped-in-bay steps and *fewer* wedges/teleports. Report both — if teleports rise, the port is
   wrong, because the whole point is to prevent the wedge that causes them.

## 7. Tasks

### T3.1 — parse internal junctions + build the foe sets (parity-inert: no reader)

**Files:** `src/Sim.Ingest/NetworkModel.cs`, `NetworkParser.cs`,
`tests/Sim.ParityTests/InternalJunctionFoeTests.cs` (new).

**Success conditions:**
1. `:2336_42_0` resolves **exactly** the 14-lane foe set enumerated in §1 — asserted as a set, not a
   count. Must include `:2336_3_0`.
2. The **two-branch rule** is pinned non-vacuously: `:2336_25_0` (cont stage-1, `response[18][25]`
   false) is **absent** while its stage-2 `:2336_44_0` is **present**. A test that only checks the 13
   unconditional lanes would pass under the NEED doc's wrong single-branch rule and does **not**
   satisfy this.
3. `InternalJunctionByBayLane[":2336_18_0"]` resolves to `:2336_42_0`, and the map is keyed on the
   **first** `incLanes` entry only (assert a non-first entry, e.g. `-2439_0`, does **not** key it).
4. Sweep all 134 committed nets: every `type="internal"` junction parses, every foe lane resolves to a
   real lane handle, and the sweep asserts corpus floors (≥ 120 nets, and the 251 internal junctions of
   `synthetic-junction2` all present) so a parser regression cannot silently skip the loop — the
   weakness caught in T2.1's review.
5. `Sim.ParityTests` **721/4/0** + new tests; `Sim.Bench` `D96213B7BB4021A7` par == single. Nothing
   reads the data yet, so any movement is a bug.

### T3.2 — the admission arm behind `InternalJunctionAdmissionGate` (default OFF)

**Files:** `src/Sim.Core/Engine.cs`, `src/Sim.Sumo/SumoShim.cs` (env gate — and honour
`SumoShimEnvCollection`'s contract), tests.

**Success conditions:**
1. Default is `false`, asserted.
2. Flag **OFF**: all four surfaces byte-identical.
3. Flag **ON**, the load-bearing assertion: **veh 95 is held on `:2336_18_0` while veh 102 occupies
   `:2336_3_0`, and never reaches `:2336_42_0` in that state.** Assert directly on the trace, not via a
   teleport count.
4. Flag **ON**, end-to-end: `synthetic-junction2` via `SumoShim.Run`, 2000 s,
   `IgnoreJunctionBlockerSeconds = -1`, `ContTurnInsideJunctionGate` + `JunctionIsLeaderGate` **ON** ⇒
   **≤ 2** teleports and vehicles **95 and 102 arrive** (SUMO: 433 s / 497 s).
5. Flag **ON**: five gridlock diagnostics green; F3 buckets re-measured and **reported either way**;
   goldens byte-identical or every shift justified by a live-SUMO 1.20.0 diff.

### T3.3 — measure, then the deferred defaults decision

Only after T3.2: put to the owner whether `InternalJunctionAdmissionGate`,
`JunctionIsLeaderGate` and `ContTurnInsideJunctionGate` go default-ON together, since the cont-turn fix
is what exposes the wedge and these two are what make it safe.
