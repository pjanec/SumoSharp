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

1. **`TimeToTeleportSeconds = 300`** — matches SUMO's own default and would give a last-resort net.
   **⚠ NOT permitted in the high-realism area** (see the retraction below): a teleport is the single most
   visible and least realistic artefact there. Listed only for completeness / low-realism areas.
2. **`IgnoreJunctionBlockerSeconds = 5`** (shipped on this branch, CLI `--ignore-junction-blocker`) — a
   **gentler** unblock: the blocked vehicle is simply allowed to proceed past a foe that has been stopped
   ≥ 5 s, with no teleport and no jump. Visually much more believable. It is a SUMO-*optional* deviation
   from SUMO's own `-1` default, but it was measured to resolve the arm-5 deadlock outright (teleports
   5 → 2; vehicles 95 and 102 complete their routes).

### ⚠ RETRACTED — do NOT enable teleport in the high-realism area

An earlier version of this document recommended *"the three junction gates ON + `IgnoreJunctionBlocker
Seconds = 5` + `TimeToTeleportSeconds = 300` as last resort"*. **The teleport half of that is wrong** and
is withdrawn. The owner's constraint for the high-realism area, in explicit priority order:

| Artefact | Allowed in high realism? |
| --- | --- |
| **Teleport** | **NO** — the most unrealistic and most visible artefact of all |
| Cars passing through each other **to unblock a blocked junction** | tolerated — "a bit better", last resort only |
| Cars overlapping as part of **normal (non-unblocking) manoeuvres** | **NO** |

So the acceptability ladder is: **prevent the block** (best) → **overlap only as a deliberate unblock, last
resort** → *never* overlap during normal driving → *never* teleport.

**The good news is that the measurement makes the safety net unnecessary.** Over a full hour with the three
junction gates ON, **teleports fired 0** and there were **0 stopped runs longer than 300 steps**, while
completed trips more than doubled (1295 → 2709) — see `F3-SESSION-LOG.md` §9.58. Prevention alone
delivered a flowing city, so no teleport and no through-each-other unblock was needed at all. This
document's finding therefore stands as a **diagnosis of why the OFF configuration collapses**
(no unblock path ⇒ absorbing failure), not as a recommendation to switch teleporting on.

`IgnoreJunctionBlockerSeconds = 5` remains available as the **tier-2** last resort if a residual wedge is
ever observed with the gates on, since it unblocks *without* a teleport. It was not needed in the measured
hour.

## RESOLVED by measurement — and neither lever was needed

An earlier version of this section said the outcome was unmeasured. **It has since been measured**
(`F3-SESSION-LOG.md` §9.58, guard `LongHorizonGridlockDiagTests`): a full hour, 7200 steps, gates OFF vs
ALL THREE ON, with **teleporting disabled in BOTH** runs.

| | gates OFF | all gates ON |
| --- | --- | --- |
| stopped runs > 300 consecutive steps | **161** | **0** |
| completed trips | 1295 | **2709** |
| teleports fired | 0 | 0 |

**Prevention alone was sufficient**: with the three junction gates on, the city still flows after an hour
with **zero teleports and zero unblock events**, so the high-realism constraint is satisfiable without
either lever. The diagnosis in this document explains the **OFF** collapse (no unblock path ⇒ absorbing
failure); it is no longer a call to action.

Still worth keeping in mind: every *other* demo diagnostic runs **200 steps**
(`F3JunctionOverlapDiagTests.cs:213,478,892`; `DemoCarOverlapInvariantTests`) — 100 s at dt=0.5 — which is
far too short to observe an hour-scale collapse, and is why this failure mode was invisible to all 48
demo tests. That is what `LongHorizonGridlockDiagTests` now covers.

## Success conditions

- A **long-horizon** demo diagnostic (≥ 1 hour of simulated time) that asserts the city still flows:
  completed trips per 10-minute slice stays above a floor, and **no vehicle is stopped from some step
  through to the end of the run**.
- OFF-vs-ON comparison of that diagnostic across the three junction gates and the two unblock levers,
  reported either way.
- Committed goldens byte-identical (the demo is not a golden; this must not touch parity).
- `Sim.Bench` hash `D96213B7BB4021A7`, par == single.
