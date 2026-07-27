# NEED — two vehicles occupying the IDENTICAL lane position (engine bug, not junction-related)

**Found by:** F3 junction-overlap session (`docs/F3-JUNCTION-OVERLAP-DESIGN.md` §7, N2).
**Scope:** `src/Sim.Core` longitudinal/insertion logic. **Nothing to do with junctions.**
**Severity:** high — two cars perfectly superposed is a hard correctness violation, and it silently
corrupts every overlap metric that runs over the demo.

## Evidence (live-city demo, `scenarios/_ped/demo_city/box`, 200 steps)

Two distinct pairs sit at **identical** lane position, identical speed, and identical derived world pose:

| pair | lane | steps | pos (both) | speed (both) | persistence |
| --- | --- | --- | --- | --- | --- |
| `__veh56` / `__veh84` | `e_d_3_3_d_3_4_2` | **191–199** | 27.834 → 94.514 | 16.670 | **9 consecutive steps** |
| `__veh83` / `__veh121` | `e_d_4_3_d_5_3_2` | 192 | 52.969 | 16.670 | 1 step (blip) |

For `__veh56`/`__veh84` the two vehicles move in **perfect lockstep** for the whole remaining window — same
`pos`, same speed, same `(X, Y, angle)` every step, `posLat` 0.000 for both. This is not a near-miss or a
car-following overshoot; they are exactly coincident and stay that way.

`16.670 m/s` is the vType max speed, so both are running free at max speed, superposed.

Reproduce with the committed diagnostic (always-passing, prints the co-located table):

```
dotnet test tests/Sim.LiveCity.Tests -c Release \
  --filter "FullyQualifiedName~F3JunctionOverlapDiagTests" --logger "console;verbosity=detailed"
```

## Why it matters

- **It is a real physics violation.** SUMO's core invariant is a 1-D longitudinal gap along the lane
  (`MSLane::detectCollisions`: `gap = victimBack - colliderPos - minGapFactor * minGap`,
  `sumo/src/microsim/MSLane.cpp:1884`). Two vehicles at the same `pos` on the same lane means `gap` is
  strongly negative — SUMO would register a **collision** here and apply `collision.action` (default
  `teleport`). We neither prevent it nor detect it.
- **It corrupts the overlap metrics.** These pairs land in `BOTH-NORMAL-SAME-LANE` and saturate the OBB
  penetration at exactly the vehicle **width** (1.800 m), because a shared forward anchor shift cancels for
  identical poses. They are 14 of the demo's 61 overlap events and are **immune to the anchor correction**
  (identical in both variants) — so they will survive any anchor fix and any junction fix.
- **It blocks F4b.** No overlap invariant can be tightened while two cars can be exactly superposed.

## Suspected mechanisms (unverified — start here)

1. **Insertion/spawn** placing a vehicle on an already-occupied position without a gap check (both are at
   max speed from the outset, which fits a spawn-time defect better than a car-following drift).
2. **The demo's own demand/spawn path** (`Sim.LiveCity`) bypassing the engine's normal insertion gap check.
3. A **lane-change/teleport** landing a vehicle on top of an existing one.
4. Two vehicles sharing a lane-sequence slot / a handle-aliasing bug (would explain the *perfect* lockstep:
   they may be advancing from the same state rather than coincidentally agreeing).

Mechanism 4 is the most economical explanation for 9 steps of bit-identical agreement and should be checked
first — coincidental agreement of two independently-integrated vehicles for 9 steps is implausible.

## Suggested first step

Add a cheap debug assertion over `LaneNeighborQuery`'s per-lane buckets (already sorted by `Pos`): after
each `Refill`, flag any adjacent pair on the same lane with `|posA - posB| < minGap`. That localises whether
the state is created at insertion or drifts into existence, and whether the two entities are genuinely
distinct.

## Out of scope for F3

F3 is the junction admission gate (crossing internal lanes). This pair is on a **normal** lane with no
junction involved, and the F3 fix cannot affect it.
