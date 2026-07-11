# NEED — saturated-junction gridlock: the engine lacks SUMO's IMPATIENCE (waiting → force through)

**For the SUMO parity coding session.** Found while profiling/optimizing the scaled-city benchmark.
This is a **behavioral RoW-core gap**, not a perf issue (the perf work is done and byte-identical).
On the committed `city-3000` demand the engine permanently gridlocks **~2231 vehicles** while SUMO
runs the identical net+routes nearly free-flow (**2 waiting** at the end). Parity-track bar (byte-
identical on the committed goldens, anchor + parity-reviewer gate). **High regression risk — it
touches the 9b/C4 junction RoW family.**

## Reproduced (engine vs real SUMO 1.20.0 on the identical committed net+demand)

```
# engine:
dotnet run -c Release --project src/Sim.BenchCity -- scenarios/_bench/city-3000 --fcd-out "" \
  --sumo-summary scenarios/_bench/city-3000/summary.xml \
  --sumo-tripinfo scenarios/_bench/city-3000/tripinfo.xml \
  --aggregate-tolerance scenarios/_bench/city-3000/aggregate-tolerance.json
# SUMO (available at /usr/local/bin/sumo):
cd scenarios/_bench/city-3000 && sumo -c config.sumocfg --no-step-log true --duration-log.statistics true
```
- **Engine:** 2162 arrived, **2231 permanently stuck** (>=120 s @ <0.1 m/s), 5434 running@end. Passes
  the 35 % aggregate band but only just (arrived relError 0.337, meanSpeed 0.190).
- **SUMO:** 3260 arrived, **2 waiting** at end, 0 teleports. Free-to-busy flow, no gridlock.

The two are the SAME committed inputs, so this is an engine-side behavioral gap, not demand.

## Where the gridlock is (measured, not guessed)

Instrumented `Sim.BenchCity`'s stuck detector to split by final lane, and `Engine` to tally which
constraint produced the binding `min` speed for slow (<0.3 m/s) vehicles within 25 m of a lane end:

- **Stuck location:** 2190 of 2231 stuck are on **normal lanes at junction approaches** (stop-line
  standoffs); only **41 are mid-junction** (box-block). → the keepClear box-block port
  (`C4-VII-REMAINING.md #3`) is ~2 % of this, NOT the cause. (A quick reservation-subtraction
  prototype for #3 made it slightly WORSE, 2231→2292, confirming this.)
- **Binding constraint on slow-at-approach veh-steps:** leaderFollow **59 %** (queues behind stopped
  leaders), junctionYield **25 %** (the queue heads yielding), keepClear 9 %, crossJunction 7 %,
  **redLight 0 %** (NOT a traffic-light timing issue). → the roots are `JunctionYieldConstraint`
  over-yields; everything else is cars correctly following the stalled heads.

## Root cause: the engine hardcodes `impatience == 0`; SUMO defaults it ON

`Engine.JunctionYieldConstraint` explicitly omits impatience ("phase 1 has no impatience",
arrival-time RoW arm). But SUMO runs **`--time-to-impatience 180` BY DEFAULT** (`sumo/src/microsim/
MSFrame.cpp:481` registers default `"180"`; non-positive disables). So in the golden run every
vehicle's impatience grows 0→1 over 180 s of accumulated waiting, and `MSLink::blockedByFoe`
(`sumo/src/microsim/MSLink.cpp:949-954`) **discounts the foe's arrival time by that impatience**:
```
if (impatience > 0 && arrivalTime < avi.arrivalTime) {
    foeArrivalTime = (1 - impatience) * avi.arrivalTime + impatience * fatb;   // fatb = foe-brakes estimate
}
```
so a long-waiting minor-road vehicle eventually treats the foe as "will brake for me" and **forces
through** — the real-world "a driver stuck long enough just goes / the priority driver waves the side
road in." Our `impatience==0` model never does, so a saturated minor road yields forever → gridlock,
and the queues behind it (the 59 % leaderFollow) never drain.

This scales exactly as observed: at low density (city-30/300) no vehicle waits long enough for
impatience to matter, so `impatience==0` is byte-identical AND those rungs pass (city-300 = 0 stuck,
matches SUMO within 0.4 %). Only at city-3000's ~4365 concurrent do junction waits exceed the window
and the missing impatience bites.

## What was TRIED and did NOT work (do not repeat as-is)

An opt-in prototype that graded the approaching-foe **reservation distance** down by ego impatience
(`foeReservationDist *= (1 - min(1, WaitingTime/180))`) had **zero effect** (2162/2231 bit-identical).
Instrumentation showed impatience>0.5 fired 66,885 veh-steps but flipped the `foeNotApproaching` gate
only **8 times** — because the foes are genuinely CLOSE (`SeenToInternalLaneEntry` ≪ reservation), so
shrinking the reservation window almost never changes the decision, and when it did the vehicle was
still bound by another term/constraint. **The reservation-distance lever is wrong.** Impatience must
enter the way SUMO does — through the **arrival-time gap comparison** in `blockedByFoe`, not the
approaching/not-approaching distance gate.

## Suggested fix shape (own rung, parity-gated)

Port SUMO's arrival-time `MSLink::blockedByFoe` (`MSLink.cpp:919-1013`) faithfully, INCLUDING the
impatience blend at :949-954, into `JunctionYieldConstraint`'s approaching-foe arm (which today is a
simplified blanket yield with no arrival-time comparison). Ego yields only if the foe's
impatience-discounted arrival/leave window actually overlaps ego's crossing window. `impatience =
min(1, WaitingTime / 180)` is deterministic (function of the already-tracked `WaitingTime`, no RNG),
and **0 for any non-waiting vehicle**, so it should stay byte-identical on the committed goldens
(none of which build up 180 s junction waits) while unwinding the saturation gridlock.

Because ego and foe each read impatience from the frozen start-of-step snapshot, it stays order-
independent / parallel-safe. If a residual gridlock survives (SUMO also uses an RNG-keyed deadlock
abort + teleport that we deliberately don't match), an opt-in aggressive/anti-gridlock flag is the
escape hatch — but try the faithful impatience port first, since SUMO reaches near-free-flow on this
net with impatience alone (0 teleports).

## Gate

Anchor: a small deterministic saturated-junction grid where SUMO flows via impatience and the pre-fix
engine gridlocks (stuck-count / arrival gauge, like `willpass-saturation` — a gridlock is not a
per-step FCD golden). Full committed suite (**227 + 1 skip**) byte-identical; `Sim.Bench` hash
`909605E965BFFE59` unchanged; parity-reviewer ACCEPT. If it moves ANY committed golden, the impatience
window is entering too early — investigate before promoting.

## Note — this is NOT a regression from the perf work

The perf commits (L0a–L0d, L1, L2 super-linear) are byte-identical: `city-300` FCD and the full
`city-3000` result (2162 arrived / 2231 stuck) are IDENTICAL at `bd008e4` (pre-session) and HEAD. The
gridlock is pre-existing. `SCALING.md`'s older "city-3000: 0 stuck / 3458 arrived" was measured on an
earlier, less-saturated demand before the rou was retuned to overshoot ~4365 concurrent; it is stale.
