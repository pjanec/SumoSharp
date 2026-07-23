# #15 cure — lane-change maneuver must not straddle a junction (DESIGN)

**Status: design for prong 2 (the actual #15 cure). Parity-safe by construction (inert when
`LaneChangeDuration == 0`, i.e. on every committed golden).** Root cause proven per-car in
`docs/LIVE-CITY-15-ATTEMPT-LOG.md` ("PRONG 1 COMPLETE"). Repro/gates: that log's "HOW TO REPRODUCE".

## WHAT (the defect, one paragraph)
With a continuous lane change (`lanechange.duration > 0`; the live-city demo sets `2.0` → 4 steps), the
engine flips a vehicle's physical lane (`LaneHandle`) to the maneuver **target** at the maneuver midpoint
(`Engine.AdvanceLaneChanges`, `2*LcStepsElapsed > LcStepsTotal`) but does **not** clear the maneuver
bookkeeping (`LcTargetHandle/Id`, `LcStepsElapsed/Total`) until the maneuver fully completes
(`LcStepsElapsed >= LcStepsTotal`). If the vehicle **crosses a junction boundary during that window**,
the boundary-cross code (`ExecuteMoveVehicle`) advances `LaneSeqIndex` and sets `LaneHandle` to the next
lane, but leaves `LcTargetHandle` pointing at a lane on the **edge just left**. The next step,
`AdvanceLaneChanges` completes the maneuver — `LaneHandle = LcTargetHandle` — snapping the vehicle
**backward onto the departed edge** while `LaneSeqIndex` already points one slot ahead. That is the
`pool[LaneSeqIndex].edge != LaneHandle.edge` desync; at the next lane end `TryReResolveFromActualLane`
bails (wrong EDGE, not wrong lane) and the vehicle is clamped `Speed=0` forever — the live-city gridlock
seed. A lane change is a *lateral* move within one edge; it must never span a junction.

## HOW (the fix — two guards, both inert when duration 0)
SUMO finalizes/aborts a lateral change at the lane end; it never carries a discrete lane target across an
internal junction lane. Mirror that:

1. **Primary — finalize at the boundary cross.** In `ExecuteMoveVehicle`, at the point the vehicle
   crosses to the next pool slot (right after `LaneSeqIndex++` and `LaneHandle = _laneSeqArrival[...]`),
   if a maneuver is in progress (`LcTargetHandle >= 0`) **clear it**
   (`LcTargetHandle=-1; LcTargetId=""; LcStepsElapsed=0; LcStepsTotal=0`). The convergence guard already
   proved the vehicle crosses on its intended exit lane, so the pending lateral maneuver has served its
   purpose (or is moot) — discard it so it cannot re-apply after the crossing. This is the root fix.

2. **Defense-in-depth — reject a stale cross-edge target in `AdvanceLaneChanges`.** Before the midpoint
   flip, if `LcTargetHandle`'s edge != the vehicle's CURRENT physical edge (`LaneHandle`'s edge), the
   target is stale (the vehicle has since changed edge). **Clear the maneuver and skip** rather than
   snapping `LaneHandle` back to the foreign edge. Catches any path that could leave a stale target
   (e.g. a re-resolve/reroute splice mid-maneuver), not only the boundary cross.

Both guards are no-ops whenever `LcTargetHandle < 0`, which is ALWAYS true when `LaneChangeDuration == 0`
(`CommitLaneChange` takes the instant `ChangeLane` path, never `StartLaneChangeManeuver`) — so every
parity/bench golden is byte-identical. No new knob: the fix is unconditional and correct for all
durations; it only ever fires when a continuous maneuver would otherwise straddle a junction.

## Determinism / parity argument
- Reads/writes only the vehicle's OWN maneuver + lane fields (same discipline as `AdvanceLaneChanges` /
  the boundary cross already use); no cross-vehicle state, order-independent, region-parallel-safe.
- Inert on every golden (duration 0 ⇒ `LcTargetHandle == -1` always ⇒ both guards skip). Success gate:
  `Sim.ParityTests` **657/4** byte-identical; `Sim.Bench` hash **`D96213B7BB4021A7`**, parallel==single.

## RESULT (measured, all first-hand)
Implemented both guards + `ClearLaneChangeManeuver` helper. Verified:
- **Parity `657/4` byte-identical; bench hash `D96213B7BB4021A7`, parallel==single** — unchanged.
- **Tracer clean:** `LIVECITY_SEQDESYNC=1 … --frames 800` prints **0** `SEQDESYNC-CREATED` lines (baseline
  ≥25). The desync can no longer be created.
- **Gridlock lifted:** `LIVECITY_TELEPORT=0 LIVECITY_CARS=160 --frames 2000`: arrivals **258 → 553** by
  t=940 (baseline flatlined at 258), and **713** by t=1500, still climbing; `stoppedFrac` 0.99→~0.4-0.65
  mid-run, oscillating (jam-and-recover), never the terminal pin to 1.0; `meanSpd` holds 3-7 m/s vs the
  baseline collapse to 0.01. Traffic sustainably drains.
- Residual: at high t (>1300s) `stoppedFrac` creeps to ~0.8-0.94 (still draining, arrivals climbing) —
  a smaller, separate peak-load effect, NOT the terminal desync lock. Track separately if it matters for
  the demo; the #15 root cause (the desync freeze-cascade) is fixed.

## Success conditions (verify first-hand, do not trust)
1. Parity `657/4` byte-identical; bench hash unchanged, parallel==single. (Guards inert on goldens.)
2. Tracer clean: `LIVECITY_SEQDESYNC=1 … --frames 800` prints **zero** `SEQDESYNC-CREATED` lines
   (baseline printed ≥25). i.e. the desync can no longer be created.
3. Gridlock lifted on the repro (`LIVECITY_TELEPORT=0 LIVECITY_CARS=160 … --frames 2000`): late
   `stoppedFrac` well below the ~0.99 terminal baseline, `arrivals` climb past the ~258 flatline,
   `strandedDeadEnd`/`poolEdgeMismatch` stay near zero. This is a real fix, so it must MOVE these — if it
   doesn't, the numbers say so and the mechanism is re-examined, not shipped on faith.
4. `WrongLaneRerouteAtApproach` (task #21) stays default-OFF — it was a symptom treatment; this is the
   cause.
