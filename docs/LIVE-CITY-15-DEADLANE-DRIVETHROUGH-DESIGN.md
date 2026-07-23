# #15 — dead-lane drive-through (never-freeze) DESIGN

**Status: IMPLEMENTED, parity-safe, but MEASURED NOT to cure the terminal gridlock** (kept as a sound,
default-off knob). See `docs/LIVE-CITY-15-ATTEMPT-LOG.md` for the full measurement trail.

## Intent
A car that reaches a lane-end whose lane has no connection reaching its destination is clamped to
`Speed=0` forever (`Engine.cs` `TryRerouteFromDeadLane` → `bestTail is null` → the caller's clamp). The
hypothesis was that these permanent strands accumulate and seed the live-city terminal gridlock, and
that letting the car drive through (SUMO `ignore-route-errors`) would prevent it.

## Mechanism (`Engine.DeadLaneDriveThrough`, default false = byte-identical)
When the congestion-weighted reroute finds no path (`bestTail is null`) AND the knob is on:
1. Retry the candidate search with **free-flow** edge weights — a topological path almost always exists;
   congestion just priced it out.
2. If even that fails, splice a **1-hop route onto any forward connection** (`firstForward`) the lane
   has, and re-resolve toward the destination from there next step.
So the car always MOVES; it never permanently walls its lane.

## Parity
The whole dead-lane path is already inert on every committed golden (they never strand), so the change
is byte-identical with the knob off, and off is the default in the Engine/scenario/bench path. Verified
`Sim.ParityTests` 657/4, bench hash unchanged.

## Result (why it is off by default)
Measured on the ~1000 s terminal-gridlock repro (cap 160, teleport off): arrivals **257 at t=940,
identical to baseline** — the fallback barely fires, i.e. the strands are NOT routing-failures
(`bestTail` is rarely null). So this does not cure the gridlock. It is retained as a correct,
parity-safe "never freeze" behaviour (useful for other high-density nets), but it is not the #15 cure.

## What the measurement says the real problem is
The terminal gridlock is density-independent (locks at cap 80/120/160), junctions stay empty
(`stuckInternal=0`), and the stuck mass is red + queue that never clears — a deep junction-discharge
deficit (the dense-flow branch's own unsolved core). Only teleport (relocating a car) cures it. A real
cure is a major engine-research effort, not a scoped knob.
