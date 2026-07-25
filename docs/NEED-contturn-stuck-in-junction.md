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
