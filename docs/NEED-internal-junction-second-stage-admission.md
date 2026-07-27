# NEED — `MSInternalJunction` is unported: a cont turn's SECOND stage has no admission control

**Source:** `sumo/src/microsim/MSInternalJunction.cpp` (`postloadInit`), consumed via the bay link's foe
lanes.
**Found by:** tracing why the `isLeader` port (T2.4b) did **not** resolve the arm-5 deadlock.
**Status:** **this is the actual root cause of the veh 95 / 102 deadlock.** It supersedes `isLeader` as
*the* answer, though `isLeader` remains necessary (see "Why `isLeader` was necessary but not
sufficient").
**Severity:** HIGH — it is why `ContTurnInsideJunctionGate` cannot go default-ON, and plausibly why the
12 both-moving F3 overlaps persist.

## The measurement that forced this conclusion

With `JunctionIsLeaderGate` **ON**, `ContTurnInsideJunctionGate` **ON**,
`IgnoreJunctionBlockerSeconds = -1`, on `scenarios/_repro/synthetic-junction2` through
`SumoShim.Run` — veh 95 and 102 are still stopped for **120 contiguous steps** (t=322…441), binder
`10 junctionYield` / arm **5** on 120/120 steps for both. Frozen state:

| veh | lane | pos | ET | ETN | CET |
| --- | --- | --- | --- | --- | --- |
| 95 | `:2336_42_0` (cont **stage 2**) | 1.903 | 320 | 320 | 321 |
| 102 | `:2336_3_0` (non-cont) | 15.989 | 319 | 319 | 319 |

Per-direction, over all 121 evaluated steps — **100% each, not a mix**:

| ego | `IsLeader` | `FoeIsInTheWay` | gap | `contLane` forced? |
| --- | --- | --- | --- | --- |
| 95 (foe=102) | **true** 121/121 | false 121/121 | −12.186 | no, 0/121 |
| 102 (foe=95) | **false** 121/121 | **true** 121/121 | −9.486 | no, 0/121 |

**The entry-time ordering works exactly as designed.** `IsLeader(102, 95)` is `false` on every step —
102 *is* released by the ordering, and the branch mix (mutual 74, `!response` 26/20, default 21/27)
matches the design's own §0a table. But the call site is SUMO's own disjunction

```
isLeader(link, leader, gap) || it->inTheWay()          // MSVehicle.cpp:3429
```

and `FoeIsInTheWay(102, 95)` is independently `true` on every step, so the OR stays true and 102 keeps
braking. 95, meanwhile, is told by the ordering to yield to 102. Both stuck.

## Why that geometry should never have existed

`inTheWay` is a *symmetric geometric fact*, independent of entry order — so no amount of correct
ordering can dissolve it. The real question is why veh 95 is sitting on cont **stage 2**
(`:2336_42_0`) while 102 occupies the conflicting `:2336_3_0` at all. In SUMO it could not be.

`MSInternalJunction::postloadInit` (`MSInternalJunction.cpp`):

- *"the first lane in the list of incoming lanes is special. It defines the link that needs to do all
  the checking for this internal junction"* — here `incLanes = ":2336_18_0 -2439_0"`, so the **stage-1
  bay** lane is the checker;
- `ownLinkIndex` = the parent junction's link index of that bay's entry link (**18**);
- `response = parent->getLogic()->getResponseFor(ownLinkIndex)`;
- every lane in the internal junction's `intLanes` whose `foeIndex` satisfies `response.test(foeIndex)`
  is pushed into `myInternalLaneFoes`, which the bay link must then respect before crossing.

For our case: internal junction `:2336_42_0` has `intLanes` containing **`:2336_3_0`**, and
`response[18]` has **bit 3 set** (established at the very start of this workstream). **So veh 95 must
yield *in the bay* to veh 102 before entering stage 2.** It never becomes an obstacle.

## What we model: nothing

- All **251** internal junctions in this net carry **zero `<request>` rows** — the internal junction's
  foe information lives in its `intLanes` attribute plus the parent's response row, exactly as
  `postloadInit` reads it.
- `NetworkParser.ParseJunction` bails on `intLanes.Count == 0 || requestEls.Count == 0`, returning a
  junction with empty `Links`/`Requests`/`Conflicts`. So every internal junction parses as **inert**.
- `grep -rn "MSInternalJunction\|InternalJunction" src/` finds only comments in the new `isLeader`
  code. There is no second-stage admission logic anywhere in the engine.

Consequence: a cont-turn vehicle advances from the bay into stage 2 **without checking any foe**. That
is the wedge.

## Why `isLeader` was necessary but not sufficient

Keep the `isLeader` port. It is faithful, it is measurably safe (no golden moved, all five gridlock
diagnostics green, `Sim.Bench` hash unchanged, LiveCity/Pedestrians green), it improved
`synthetic-junction2` teleports **5 → 4**, and its ordering demonstrably resolves the pair — the trace
proves the release happens. It is simply *downstream* of the defect: it arbitrates who goes first among
vehicles legitimately inside the junction, whereas the missing mechanism controls **who is allowed in**.

This also corrects `NEED-arm5-mutual-junction-deadlock.md`'s conclusion that *"the only reason SUMO does
not hit this deadlock is `isLeader()`"*. SUMO has **two** defences and we had ported neither; `isLeader`
was the visible one. The load-bearing one for this scenario is internal-junction admission.

## Fix sketch

1. Parse internal junctions instead of discarding them: keep `Id`, `IncLanes`, `IntLanes` for
   `type="internal"` even with no `<request>` rows.
2. For each internal junction, resolve the **checker** (first `incLanes` entry = the stage-1 bay lane),
   its parent junction, and `ownLinkIndex` (the bay's entry link index — already available via
   `LinkIndexByInternalLane`, which maps both cont stages to the parent link index).
3. Build `InternalLaneFoes` = every lane in the internal junction's `intLanes` whose link index is set
   in the parent's `Requests[ownLinkIndex].Response`.
4. Gate the bay→stage-2 advance on those foe lanes being clear, mirroring `postloadInit`'s attachment
   of `myInternalLaneFoes` to the bay link.

Port `indirectBicycleTurn` as a **guarded omission** — no committed net has an indirect link (0 of 134).

## Success conditions

- veh 95 is held in the bay `:2336_18_0` while 102 occupies `:2336_3_0`, and **never** reaches
  `:2336_42_0` in that state — asserted directly, not merely via a teleport count.
- With `ContTurnInsideJunctionGate` **and** `JunctionIsLeaderGate` on, `synthetic-junction2` yields
  **≤ 2** teleports and vehicles 95 and 102 **arrive** (SUMO: 433 s / 497 s).
- A direct test that internal junction `:2336_42_0` resolves `InternalLaneFoes` containing
  `:2336_3_0`, derived from `response[18]` bit 3 — non-vacuous, since it must **fail** if the parent
  response row is ignored.
- All 661 goldens byte-identical, or every shift justified by a live-SUMO 1.20.0 diff.
- `Sim.Bench` hash `D96213B7BB4021A7`, par == single; all five gridlock diagnostics green; the F3
  buckets re-measured and reported either way.

## One loose end worth settling

The trace also showed an asymmetry in `IsLeader`'s branch mix between the two directions (`!response`
20 vs 26, default-pair 27 vs 21). `ResponseFor`'s attempt-1 sub-branch legitimately uses the **foe's**
speed/gap/length and **ego's** minGap, so swapping ego/foe is not a pure mirror even though the
top-level `haveRed` test is symmetric. That is expected and matches SUMO, but it has not been verified
line-by-line against a SUMO trace and is worth a look if any future result depends on it.
