# NEED — `checkRewindLinkLanes` is only partially ported; it blocks the cont-turn predicate fix

**Found by:** F3 session, while landing the cont-turn predicate fix
(`docs/NEED-contturn-stuck-in-junction.md`).
**Scope:** `src/Sim.Core/Engine.cs` — `KeepClearConstraint` (~:7345).
**Why it matters now:** it is the **blocker** for enabling `Engine.ContTurnInsideJunctionGate`.

## The dependency (this is the interesting part)

Enabling the cont-turn predicate fix — which is *provably correct*, pinned by
`ContTurnInternalLaneOwnershipTests` — regresses `RungHDp2g2CoordinatedLaneChangeTests` on
`scenarios/_diag/willpass-saturation`: **stuck 1 → 28**, against a ceiling of 5. All 661 goldens stay
byte-identical.

Diagnosis: **the mis-gated cautious-approach brake was accidentally substituting for
`checkRewindLinkLanes`.** Braking a car that was already mid-junction happened to throttle junction entry;
removing that accidental brake, with no real "don't enter a junction you can't clear" gate upstream, lets an
over-saturated grid commit too many cars into junction interiors at once.

So the order of work is forced: **finish this port, then enable the predicate fix.** Two independently
correct changes where one must precede the other.

## What SUMO does (`MSVehicle::checkRewindLinkLanes`, `sumo/src/microsim/MSVehicle.cpp:5025`)

Three passes over the vehicle's own upcoming-link list `myLFLinkLanes`:

1. **Forward** (`:5036-5148`) — accumulate `availableSpace`, seeded at `seenSpace = -lengthsInFront`.
   Subtract `getBruttoVehLenSum()` for a `keepClear` link's internal lane; else add
   `getSpaceTillLastStanding(...)`.
2. **Backward** (`:5151-5194`) — propagate `availableSpace` back through links that "allow continuation",
   where `opened = havePriority() || i==1 || link->opened(...)` and
   `allowsContinuation = (isCont() || opened) && !hadStoppedVehicle`. **A link that is not `opened()` sets
   `foundStopped` for all earlier links.**
3. **removalBegin + revoke** (`:5196-5257`) — first `i` where
   `availableSpace - getLengthWithGap() < 0` **and** `keepClear(link)` → downgrade `myVLinkPass = myVLinkWait`
   and clear `mySetRequest`, unless ego cannot brake in time (`myDistance < brakeGap`) or it is an exit link
   whose entry already committed.

## What we have, and the four gaps

`KeepClearConstraint` ports only the "removal" half, with simplifications its own header documents. The
load-bearing loop:

```csharp
for (var i = egoLinkSeqIndex; i < v.LaneSeqLen && !foundStopped; i++)
{
    var lane = _network.LanesByHandle[_laneSeqPool[v.LaneSeqStart + i]];
    if (lane.Id.StartsWith(':')) seenSpace -= LaneBruttoVehLenSum(lane, v);
    else                          seenSpace += LaneSpaceTillLastStanding(lane, v, dt, out foundStopped);
}

if (!foundStopped || seenSpace - (v.VType.Length + v.VType.MinGap) >= 0.0)
    return double.PositiveInfinity;
```

| # | Gap | SUMO reference | Likely impact |
| --- | --- | --- | --- |
| **G1** | `foundStopped` is set **only** by an already-STOPPED downstream vehicle. SUMO ALSO sets it from `last->myHaveToWaitOnNextLink \|\| last->isStopped()` — a car that merely *cannot proceed* propagates blockage backward. | `MSVehicle.cpp:5126-5129` | **Highest.** In a saturating grid the blockage is a *forming* queue, not an already-halted one, so we admit cars SUMO would hold. Prime suspect for the 28-stuck regression. |
| **G2** | No backward pass: a downstream link that is **not `opened()`** never marks earlier links blocked. | `MSVehicle.cpp:5151-5194` | High — red/blocked links downstream don't propagate. |
| **G3** | `lengthsInFront` hardcoded 0 — ego's own approach-lane queue ignored, so required space is underestimated. | `MSVehicle.cpp:5036` (`seenSpace = -lengthsInFront`) | Medium. |
| **G4** | Blind to `IsParked` vehicles (`LaneNeighborQuery` excludes them), so a parked car never blocks the box. | — | Low in goldens, real in the live-city demo. |

Also: the gate `if (request is null \|\| !request.Foes.Contains('1')) return +∞` is **static** — it asks "does
this link have any crossing foe *in the matrix*", never whether one is actually there. Faithful to
`link->hasFoes() && link->keepClear()`, so not a defect, but it means the arm is entirely
occupancy-driven downstream of that point.

## Suggested order

1. **G1 first** — it is the smallest change with the largest expected effect, and it is testable directly:
   assert that a vehicle whose downstream leader is itself yield-blocked (not yet stopped) is held at the
   junction entry. Needs a "this vehicle cannot proceed" signal; `v.CrossingYieldTaken` and the binder tag
   already exist and may serve as the `myHaveToWaitOnNextLink` equivalent.
2. Re-measure `RungHDp2g2` (`stuck <= 5`) **with `ContTurnInsideJunctionGate` ON**. If G1 alone brings it
   under the ceiling, the predicate fix can be enabled by default and the flag retired.
3. Then G2/G3 as separate, individually-measured steps.

## Success conditions

- `RungHDp2g2CoordinatedLaneChangeTests` passes (`stuck <= 5`) **with `ContTurnInsideJunctionGate = true`**.
- The other four gridlock diagnostics stay green: `WillPassSaturationDiagTests`,
  `DenseFlowDeadLaneDrainTests`, `RblLeftTurnsDiagTests`, `LowDensityTeleportTests`.
- `Sim.ParityTests` goldens byte-identical, or any shift justified by a live-SUMO diff (SUMO **1.20.0** is
  available at `/usr/local/lib/python3.11/dist-packages/sumo/bin/` — put it FIRST on `PATH`; bare `sumo` is
  apt's 1.18.0).
- `Sim.Bench` hash `D96213B7BB4021A7`, par==single.
- A direct behavioural test for G1 (not only the aggregate stuck-count diagnostics).
