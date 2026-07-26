# NEED — the live-city demo runs with SUMO's teleport safety net DISABLED

**Found by:** the owner's report that the demo, after ~an hour of simulated time, ends with all cars
queued at junctions blocked by cars stuck in each other — *"blocked forever, no fallback, no teleport or
other unblock"*.
**Severity:** HIGH for the demo's stated goal (**believability**). Not a parity defect — the committed
goldens are unaffected — but it is the reason a *single* residual wedge becomes *city-wide permanent*
gridlock.
**Status:** diagnosed with citations; not yet fixed (it is a config/defaults decision, not a code bug).

## The finding

| | Value | Effect |
| --- | --- | --- |
| SUMO's own default | **300 s** — `MSFrame.cpp:412-413`, *"defaults to 300, non-positive values disable teleporting"* | a stuck vehicle is teleported after 5 minutes |
| Our parser default when `<time-to-teleport>` is absent | **−1.0** — `ScenarioConfigParser.cs:45` | non-positive ⇒ disabled |
| `Engine`'s own fallback config | **−1.0** — `Engine.cs:1506` (`DefaultNetworkConfig`) | disabled |
| `LiveCityConfig.TimeToTeleportSeconds` | **0.0** — `LiveCityConfig.cs:92` | |
| `LiveCitySim` emission | element written **only when `> 0.0`** — `LiveCitySim.cs:327` | at the default the element is **omitted** ⇒ −1.0 ⇒ **disabled** |

Every teleport path in the engine is gated on `TimeToTeleport > 0.0` (e.g. `Engine.cs:2951`, and the
`P1F` comments at `:229`, `:235`, `:712`, `:2810`). So in the demo **no vehicle is ever teleported, ever**.

## Why this is the load-bearing fact for believability

The owner's three reported symptoms turn out to be one root plus two consequences:

1. **Junctions blocked forever** — the root.
2. **A long queue in one lane while the parallel same-direction lane is free** — a *consequence*: cars
   stack behind whichever lane's lead car is wedged, while the parallel lane's lead car got through.
   (Owner's own words: *"the single lane queue is less important as that would never show if junction
   never blocks forever"*.)
3. Cars interpenetrating in a shared exit lane — a separate defect
   (`NEED-colocated-vehicles.md`, same-target merge), not caused by this.

With teleporting disabled there is **no unblock path of any kind**. That makes the failure mode
*absorbing*: the city can only ever accumulate wedges, never clear one. Over an hour that converges to
total gridlock **regardless of how rare the wedge mechanism is**.

Critically, **this remains true after this branch's fixes.** The arm-5 mutual deadlock is now fixed
(`docs/F3-INTERNAL-JUNCTION-DESIGN.md`), but any *other* wedge mechanism — e.g. the documented
`NEED-multilane-junction-passage.md`, where a vehicle on a valid connecting lane is simply never granted
passage — will still freeze the city permanently. **Fixing wedge mechanisms one at a time cannot deliver
believability while the safety net is off.**

## Two levers, both already implemented

Neither needs new code; both are settings.

1. **`TimeToTeleportSeconds = 300`** — matches SUMO's own default, so it is *more* faithful than the
   current value, and gives a last-resort net. Cost: a car visibly jumps, which is jarring — but far less
   jarring than a permanently frozen city, and it is what the parity anchor itself does.
2. **`IgnoreJunctionBlockerSeconds = 5`** (shipped on this branch, CLI `--ignore-junction-blocker`) — a
   **gentler** unblock: the blocked vehicle is simply allowed to proceed past a foe that has been stopped
   ≥ 5 s, with no teleport and no jump. Visually much more believable. It is a SUMO-*optional* deviation
   from SUMO's own `-1` default, but it was measured to resolve the arm-5 deadlock outright (teleports
   5 → 2; vehicles 95 and 102 complete their routes).

**Suggested demo configuration** (the demo is not a parity golden, so it may deviate deliberately):
the three junction gates ON + `IgnoreJunctionBlockerSeconds = 5` as first-line unblock +
`TimeToTeleportSeconds = 300` as last resort. That gives three layers: prevent the wedge, release it
gently, and teleport only if both fail.

## ⚠ What is NOT yet established

That enabling either lever actually prevents the hour-long collapse **in the demo**. The mechanism above
is certain (with teleport disabled there is no unblock path at all), but the outcome is not measured.

Note also that every demo diagnostic in the repo runs **200 steps**
(`F3JunctionOverlapDiagTests.cs:213,478,892`; `DemoCarOverlapInvariantTests`), which is **far too short
to observe an hour-scale collapse**. Any claim about believability based on those 200-step numbers —
including this branch's own "45 → 51 overlap events" comparison — is measuring the wrong horizon.

## Success conditions

- A **long-horizon** demo diagnostic (≥ 1 hour of simulated time) that asserts the city still flows:
  completed trips per 10-minute slice stays above a floor, and **no vehicle is stopped from some step
  through to the end of the run**.
- OFF-vs-ON comparison of that diagnostic across the three junction gates and the two unblock levers,
  reported either way.
- Committed goldens byte-identical (the demo is not a golden; this must not touch parity).
- `Sim.Bench` hash `D96213B7BB4021A7`, par == single.
