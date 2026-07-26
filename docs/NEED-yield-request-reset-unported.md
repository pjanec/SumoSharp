# NEED — SUMO's junction yield-request reset is unported, and the obvious wiring is a trap

**Source:** `sumo/src/microsim/MSVehicle.cpp:3720-3731`, in `processLinkApproaches`' no-request `else`.
**Found by:** the `isLeader` port design (`docs/F3-ISLEADER-PORT-DESIGN.md` §5b), while establishing
where the three junction entry-time fields are assigned.
**Status:** deliberately NOT ported. Not required by any measured defect.
**Severity:** LOW today — but it is a real SUMO mechanism, so it is recorded rather than forgotten.

## What SUMO does

```cpp
// blocked on the junction. yield request so other vehicles may become junction leader
myJunctionEntryTime = SUMOTime_MAX;
myJunctionConflictEntryTime = SUMOTime_MAX;
```

Guarded by: ego is on an internal lane of junction `J`, its next link continues from an internal lane
of the same `J`, and the enclosing branch means `link == nullptr || !dpi.mySetRequest`.

It resets `ET` and `CET` but deliberately **not** `myJunctionEntryTimeNeverYield` — that omission is
the entire reason the third field exists, and the cont turn's *"renew yielded request"* line
(`MSVehicle.cpp:4361`) restores `ET` from it one stage later.

**Effect.** With `ET = CET = MAX`, a foe evaluating this vehicle in `isLeader` gets `foeET = MAX`, so
`egoET > foeET` is false and the foe **stops yielding to it**. It is a deadlock-breaking mechanism in
its own right, independent of the entry-time ordering.

## ⚠ The trap — why the obvious wiring would make things WORSE

The tempting implementation is "reset when ego is on an internal lane and `!v.WillPass`". That is
wrong, and measurably so in principle:

- SUMO's `mySetRequest` is `(v > eps && !abortRequestAfterMinor) || leavingCurrentIntersection`
  (`MSVehicle.cpp:2732`). The **`leavingCurrentIntersection` disjunct means a vehicle already inside a
  junction normally keeps its request even at speed 0.** The reset therefore fires only when
  `checkRewindLinkLanes` has *cancelled* the request because downstream space ran out
  (`MSVehicle.cpp:5221,5249-5253` — spillback).
- Our `VehicleRuntime.WillPass` is `(planned vNext > eps)`, and its own doc comment
  (`Engine.cs:5722-5724`) states that `leavingCurrentIntersection` is **deliberately excluded**
  because the bool exists to gate the *approaching-foe* arm.

So `!WillPass` is true for **every stopped in-junction car**, not just a spillback-blocked one.
Wiring the reset to it would blank `ET`/`CET` for every stopped car inside a junction and destroy the
entry-time ordering the `isLeader` port exists to establish — strictly worse than omitting it.

## What a faithful port needs

A real `mySetRequest`, which needs `checkRewindLinkLanes`' spillback request-abort. That is
explicitly parked (`docs/F3-SESSION-LOG.md` §6, "Parked, with reasons":
`NEED-checkrewindlinklanes-partial-port.md`), and nothing currently depends on it.

## Why deferring is right, not merely convenient

The one confirmed arm-5 deadlock (veh 95 / 102 at junction `2336`) is **not** spillback-blocked: a
2.99 m clear box gap, and both cars stopped *short* of the crossing point (`F3-SESSION-LOG.md` §9.27).
SUMO's reset would not fire for them — the *ordering* is what prevents their state. And bundling an
approximation of `mySetRequest` into the `isLeader` measurement would put a second,
poorly-characterised behavioural change inside the same A/B, which is exactly the error that made the
first `--ignore-junction-blocker` A/B uninterpretable (§9.33).

## Success conditions, if it is ever picked up

- A faithful `SetRequest` including `leavingCurrentIntersection`, with a direct test that a vehicle
  stopped **inside** a junction with clear downstream space still has `SetRequest == true` (this is
  the assertion that distinguishes it from `WillPass`).
- A direct test that the reset clears `ET`/`CET` but leaves `ETN` intact, and that a cont turn's
  stage-2 hop restores `ET` from `ETN` afterwards.
- Goldens byte-identical, or every shift justified by a live-SUMO 1.20.0 diff.
