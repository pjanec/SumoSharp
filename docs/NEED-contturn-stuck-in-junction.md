# NEED — cars freeze for ~100 steps INSIDE a junction on a cont-turn's first-stage internal lane

**Found by:** F3 junction-overlap session. Tracker task **T1.5**.
**Scope:** `src/Sim.Core/Engine.cs` — `JunctionYieldConstraint`'s cautious-approach arm (arm 2).
**Severity:** HIGH. This is the largest single contributor to the demo's car–car overlap, and a car
motionless in the middle of a junction with nothing ahead of it is a hard behavioural defect.

## Symptom (measured, live-city demo, 200 steps)

| vehicle | internal lane | consecutive stopped steps | frozen pos | lane length | `GapAhead` | `NextMouthGap` | binder | arm |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `__veh127` | `:d_3_4_5_0` | **95** (steps 98–192) | 7.17 | **7.27** | `+Inf` | `+Inf` | **10 junctionYield** | **2 cautiousApproach** (95/95) |
| `__veh140` | `:d_5_4_12_0` | **75** (steps 113–187) | 7.17 | — | `+Inf` | `+Inf` | 1 leaderFollow | (arm 2 also recorded, not binding) |

Both stop **0.10 m from the end** of a first-stage internal lane, with **no leader and no blocked exit
mouth** for every step of the run. Both eventually recover on their own (`__veh127` at step 193 via
`:d_3_4_20_0`; `__veh140` at 188 via `:d_5_4_22_0`) and are still moving at step 199 — so it is a long stall,
not a permanent deadlock.

Reproduce: `dotnet test tests/Sim.LiveCity.Tests -c Release --filter "FullyQualifiedName~F3JunctionOverlapDiagTests" --logger "console;verbosity=detailed"`

## CONFIRMED root-cause component (verified against the net)

Junction `d_3_4`'s `intLanes` attribute (`scenarios/_ped/demo_city/box/net.xml`) lists **`:d_3_4_20_0` but
NOT `:d_3_4_5_0`**:

```
:d_3_4_18_0 :d_3_4_1_0 :d_3_4_1_1 :d_3_4_1_2 :d_3_4_19_0 :d_3_4_20_0 :d_3_4_6_0 ... :d_3_4_25_0
```

This is normal SUMO topology: for a **cont turn** the junction's `intLanes` carries the *link-controlling*
lane, and `:d_3_4_5_0` is the **first-stage** lane before the internal junction
(`<connection from=":d_3_4_5" to="e_d_3_4_d_3_5" via=":d_3_4_20_0" dir="r" state="m"/>`).

Consequences, in order:

1. `NetworkModel.LinkByInternalLane` is built from `Junction.IntLanes`, so it **does not contain
   `:d_3_4_5_0`**.
2. `JunctionYieldConstraint`'s forward scan (`Engine.cs:6649-6665`) looks for the first internal lane
   *that is in `LinkByInternalLane`*, so it **skips `:d_3_4_5_0`** and returns
   `egoInternalLaneId = :d_3_4_20_0`.
3. `egoOnInternal = (v.LaneId == egoInternalLaneId)` → `":d_3_4_5_0" == ":d_3_4_20_0"` → **false**, even
   though the vehicle is *physically inside the junction*.
4. The cautious-approach arm's gate (`Engine.cs:6832`) is
   `if (!egoOnInternal && approachLane is not null && request.Response.Contains('1') && !egoHasSignalPriority)`
   — so it **fires while ego is mid-junction**, which directly contradicts its own stated precondition
   (`Engine.cs:6823-6824`: *"Only applies while ego is still on its APPROACH lane (!egoOnInternal) — once it
   has entered its internal lane the link is behind it"*).

**So `egoOnInternal` is not a reliable "am I inside the junction?" test on a cont turn.** That is the
confirmed defect. It is a *class* of bug: any arm gated on `!egoOnInternal` is mis-gated for a vehicle on a
first-stage cont-turn lane.

## LEADING HYPOTHESIS for the 95-step freeze (NOT yet verified — verify before fixing)

The arm only fires when `brakeDist < seen && seen > visibilityDistance` (`4.5`, `Engine.cs:6836,6867`). At
`pos = 7.17` on a `7.27 m` lane the honest remaining distance is `0.10 m`, which is **below** 4.5 — so the arm
should *not* fire. It does, which means `seen` is being **over-computed**.

The cont-turn branch (`Engine.cs:6848-6856`) is:

```csharp
seen = _network.LanesByHandle[v.LaneHandle].Length - v.Kinematics.Pos;      // current lane remainder
for (var i = v.LaneSeqIndex + 1; i < egoLinkSeqIndex; i++)
    seen += _network.LanesByHandle[_laneSeqPool[v.LaneSeqStart + i]].Length; // intervening lanes
```

This is only correct when `v.LaneSeqIndex` indexes the vehicle's **current** lane. If `LaneSeqIndex` lags —
still pointing at the approach lane `e_d_4_4_d_3_4_1` while the vehicle has physically advanced onto
`:d_3_4_5_0` — then `:d_3_4_5_0` sits at `LaneSeqIndex + 1 < egoLinkSeqIndex` and is **counted twice**:

```
seen = (7.27 - 7.17)  +  7.27  =  7.37 m      >  4.5   ->  arm fires, forever
```

`7.37 > 4.5` for **any** position on the lane, so the gate can never release by ego advancing — and ego
cannot advance, because the arm brakes it to a stop line derived from that same inflated `seen`. **Frozen
pos → same inflated `seen` → same brake → frozen pos.** A closed loop, with no leader and no blocked exit,
exactly matching the observed `GapAhead = NextMouthGap = +Inf`.

**This is a hypothesis that fits every measured number; it is NOT confirmed.** Do not fix on it blind.

### The decisive experiment (do this first, it is 4 values)

For `__veh127` across steps 93–195, print: `v.LaneSeqIndex`, the lane id at
`_laneSeqPool[v.LaneSeqStart + v.LaneSeqIndex]`, `egoInternalLaneId`, `egoLinkSeqIndex`, `approachLane.Id`,
and the computed `seen`. That settles in one run whether `LaneSeqIndex` lags and whether `seen ≈ 7.37`.

If confirmed, the fix is to derive the current-lane term from `LaneSeqIndex` consistently (start the sum at
the vehicle's actual lane index, or begin the loop at the lane *after* the current one) so no lane is counted
twice — plus a separate correctness fix for (3): use a genuine "am I on any internal lane of this junction?"
predicate rather than string equality against the link-controlling lane.

## DIFFERENTIAL ANALYSIS vs SUMO — CONFIRMED MIS-PORT, and the fix is cheap

Method: SUMO does not have this bug, so either we ported something wrong or we omit a mechanism. Answer:
**both, and the mis-port is the actionable one.**

### SUMO's predicate is a LANE PROPERTY; ours is a lane-id string match

`sumo/src/microsim/MSEdge.h:264-266`:
```cpp
inline bool isInternal() const {
    return myFunction == SumoXMLEdgeFunc::INTERNAL;
}
```
`sumo/src/microsim/MSLane.cpp:2498-2501`:
```cpp
bool MSLane::isInternal() const { return myEdge->isInternal(); }
```
Set once at load from the edge's `function` attribute. **True for every internal lane of every stage of
every junction**, with no reference to link or stage.

The load-bearing call site, `MSVehicle::isLeader` (`sumo/src/microsim/MSVehicle.cpp:7348`):
```cpp
if (!myLane->isInternal() || myLane->getEdge().getToJunction() != link->getJunction()) {
    // if this vehicle is not yet on the junction, every vehicle is a leader
    return true;
}
```
The second clause compares the internal edge's **junction node**, not a lane id — it exists to exclude an
*adjacent* junction's internal lane, not a different stage of the same one.

**Ours** (`Engine.cs:6711`): `var egoOnInternal = v.LaneId == egoInternalLaneId;` — equality against the one
lane that happens to be link-controlling for this request row. The two predicates **coincide for a
single-stage turn and diverge exactly on a cont turn**, where the vehicle is on the first-stage lane:
`isInternal()` is true, string-match is false.

**Our own code already asserts the false equivalence.** The comment block at `Engine.cs:7035-7061` claims
`egoOnInternal` "is exactly SUMO's `myLane->isInternal()` for this junction". It is not. That comment is the
bug, written down.

### Why netconvert makes this inevitable (traced to the writer)

`sumo/src/netwrite/NWWriter_SUMO.cpp:634-649`:
```cpp
if (!(*k).haveVia) { intLanes.push_back((*k).getInternalLaneID()); }
else               { intLanes.push_back((*k).viaID + "_0"); }
```
`getInternalLaneID()` is the **first**-stage lane; `viaID` is the **second**-stage lane. So for a cont
connection (`haveVia`) `intLanes[i]` is the **second**-stage lane and the first-stage lane is *never* written
into `intLanes`. Confirmed empirically on two independent nets (`d_3_4` and scenario 44's `C`).

One further consequence to know: for a cont turn, `MSInternalJunction::postloadInit`
(`sumo/src/microsim/MSInternalJunction.cpp:53-107`) calls `setRequestInformation` **twice** with the **same**
`ownLinkIndex` — so **one** request row / link index governs **two** physical links and two internal lanes.
Any code assuming "one request row ↔ one internal lane" is wrong on cont turns.

### The missing mechanism (context, not required for the fix)

SUMO has an `MSLink` whose before-lane **is** the first-stage internal lane (`thisLink` in `postloadInit`),
with `getViaLane()` = the second-stage lane and `myAmCont = true`; plus `isInternalJunctionLink()`,
`isExitLinkAfterInternalJunction()`, `getCorrespondingEntryLink()` (`MSLink.cpp:1282-1342`) to walk that
two-link chain. We model only the **lane** chain, never these link objects — which is *why* the only
lane-identity available to our code was the link-controlling lane. **The fix does not require modelling that
extra link.**

### THE FIX (minimal, faithful, cheap)

`NetworkModel` currently has **no `IsInternal` field at all** on `Lane`/`Edge` (`NetworkModel.cs:28-62`); the
only notion of "internal" is the `':'`-prefix convention, duplicated privately in `NetworkRouter.cs:358` and
`RerouteEdgeWeights.cs:102`, and never consulted by `Engine.cs`. `LinkByInternalLane` (`NetworkModel.cs:235`)
is keyed only by link-controlling lanes.

1. Add a lookup covering **every** internal lane, not just `intLanes` members — e.g.
   `IReadOnlyDictionary<string, Junction> JunctionByInternalLane`, or an `IsInternal` + owning-junction field
   on `Lane`. **The data is already gathered**: the via-chain walk at `NetworkModel.cs:487-506` already
   enumerates first-stage lanes; nothing currently records their owning junction.
2. Replace `egoOnInternal = v.LaneId == egoInternalLaneId` with a probe: "is ego's current lane an internal
   lane **of this junction**" — a dictionary lookup, correct for every stage of a cont turn.

### A DIRECT, non-vacuous test for the fix (better than any overlap metric)

Because the defect is a wrong predicate, it can be asserted **directly** — no trajectory statistics needed:

- On a net with a cont turn (scenario 44's `C`, or `_diag/cont-turn-sequence`, both committed and offline):
  assert the model answers **true** for "is `:C_3_0` an internal lane of junction `C`" while `:C_3_0` is
  **absent** from `C`'s `intLanes`. That single assertion fails today and passes only when fixed.
- Then assert `egoOnInternal` is true for a vehicle positioned on `:C_3_0`.

This is fast, deterministic, SUMO-free, and cannot pass vacuously.

### Other sites that inherit the same defect

Every place a port gates on "am I on the junction" must use the lane property, not a lane-id match. SUMO call
sites to check: `checkRewindLinkLanes` (`MSVehicle.cpp:5025`), `isLeader`'s first clause (`:7348`, which our
`Engine.cs:7035-7061` already mis-describes), and the general `myLane->getEdge().isInternal()` guards at
`MSVehicle.cpp:2351, 2428, 4211, 5378, 5782, 6248, 7333, 7971, 7990`.

## Why this matters for F3

Fixing this targets, per the attribution measured in `docs/F3-JUNCTION-OVERLAP-DESIGN.md` §6a:
- the **5 deepest** `BOTH-INTERNAL-DIFFERENT-LANE` events, including that bucket's worst (**1.987 m**,
  `__veh5` at 10.40 m/s vs the frozen `__veh127` at 0.00), and
- **60 of the 62** `ONE-INTERNAL-ONE-NORMAL` events (that bucket is 60/62 `STOPPED-FOE`) — the single largest
  overlap bucket in the demo.

It must be fixed **before** any junction-occupancy gate is re-attempted: both prior attempts failed *by
braking cars*, which grows the stuck-in-junction population this bug creates.

## Related

- `docs/F3-JUNCTION-OVERLAP-DESIGN.md` §6a (attribution + why this is first), §3d (the failed attempts).
- `docs/NEED-colocated-vehicles.md` — `__veh140`'s binder is `leaderFollow` with `GapAhead = +Inf`, i.e. it
  is following a leader the witness cannot see. That smells like the co-located/ghost-vehicle defect rather
  than this one; treat `__veh140` as a *possibly different* bug until instrumented.
- `docs/NEED-multilane-junction-passage.md` — the same cont-turn/over-yield family.
- `RungC4viiMultilaneJunctionParityTests` is **skipped** and its comment already names cont-lane bugs; this
  may be the same defect, and that test may be the cheapest committed reproduction.
