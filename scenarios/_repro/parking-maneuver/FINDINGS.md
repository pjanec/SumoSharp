# parking-maneuver — testing SumoData's "parking maneuver lane-blocking" knee hypothesis

**Date:** 2026-07-22. **Context:** after the arrival-TL fix (`ca8d515`) did not move SumoData's real-box
5.5× overshoot, their top hypothesis for the remaining blocker was **parking entry/exit maneuver
lane-blocking** (their `auto_parking` demand arrives off-lane at `parkingArea` sinks). This benchmark tests
that hypothesis on a controllable repro. **Verdict: NOT reproduced — every parking signal points to SumoSharp
*under*-representing parking, the OPPOSITE direction from the 5.5× over-accumulation.**

## Setup
`net.net.xml`: a 2-lane straight corridor `A→B→C` (500 m + 400 m, priority nodes). `extra.add.xml`: a
`parkingArea pa` on `AB_0`. Two demands, sustained held flows (`sigma=0`):
- `park.rou.xml` (maneuver): park flow 450 vph parks 25 s then continues (repeated entry+exit maneuvers) +
  through flow 900 vph.
- `sink.rou.xml` (capacity-fill): park flow 500 vph **park-and-stay** (`duration=99999`) into a cap-12
  parkingArea (fills, then excess can't park) + through flow 1400 vph.

## Results — SumoSharp under-accumulates at parking (three independent signals)

1. **Maneuver load, cap not exceeded (`park.sumocfg`), t=999:** vanilla running 31 / arrived 344 /
   meanSpeed 12.9; SumoSharp running **25** / arrived **351** / meanSpeed **13.6**. SumoSharp is *slightly
   better* — the entry/exit maneuver does NOT over-block the through lane.
2. **Capacity-fill sink (`sink.sumocfg`), t=999:** vanilla running 65 / arrived 364 / **halting 29** /
   meanSpeed 5.9; SumoSharp running **38** / arrived **490** / **halting 0** / meanSpeed **13.0**. When the
   parkingArea fills, **vanilla queues the un-parkable vehicles ON-LANE (halting 29, backs up)**; SumoSharp
   **skips the full parkingArea and drives on** (no queue, more arrivals). SumoSharp under-accumulates.
3. **FCD parked-vehicle emission:** at t=500 (maneuver) / t=900 (sink), **vanilla emits parked vehicles in
   the FCD** (speed 0 on `AB_0` — 40 stopped park-cars at t=900 in the sink case); **SumoSharp emits ZERO
   parked vehicles** in the FCD. SumoSharp excludes parked cars from FCD output.

All three make SumoSharp's on-lane / FCD-visible count at parking **lower** than vanilla, not higher — so
parking does not explain the knee's 5.5× **over**-accumulation.

## Two concrete SumoSharp fidelity bugs found (real, but WRONG DIRECTION for the knee)
- **A — full-parkingArea handling:** SumoSharp does not wait/queue for a full parkingArea like vanilla; it
  skips the stop and continues. (My vanilla had no `parking.rerouting`, so it queued on-lane. If SumoData's
  vanilla uses parking rerouting it also would not queue — so verify against their config before treating
  this as a divergence. Either way it makes SumoSharp *under*-accumulate.)
- **B — FCD excludes parked vehicles: FALSIFIED (2026-07-22).** This was a mis-observation. SumoSharp DOES
  emit parked vehicles in the FCD — verified against committed scenario 70 (`parked-passable`): its `parkStay`
  car appears throughout SumoSharp's FCD at speed 0, matching the vanilla-generated golden. The zero-parked
  readings above were an artifact of Bug A: in those tests SumoSharp *skipped* the full lot (never parked), so
  there were simply no parked cars to emit. There is no FCD parked-emission bug.

## Conclusion / hand-off
The parking-maneuver hypothesis does not reproduce here. Combined with SumoData's own finding (the arrival-TL
fix is real but not their blocker), the knee's **over**-accumulation is still unlocalized on a faithful
offline repro. **Waiting on SumoData's in-flight re-localization** (matched vanilla-knee demand, parking-vs-TL
hotspot breakdown) to name the actual dominant hotspot before building the next repro — guessing another
config risks another unfaithful witness (the `sustained-box` lesson). When their per-edge breakdown lands:
find the biggest SumoSharp-over-vanilla edge → read its FCD → identify the mechanism → build/extend a faithful
repro → port the SUMO exemption → verify goldens byte-identical (the method that landed `ca8d515`).

Bugs A and B are worth fixing on their own merits (FCD parked-emission especially, for viz/serve fidelity),
tracked here pending confirmation they matter to the calibrate path.
