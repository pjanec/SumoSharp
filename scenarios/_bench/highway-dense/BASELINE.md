# highway-dense — D1 benchmark baseline

The many-vehicle workload the **Group-D (FastDataPlane ECS readiness)** refactor is measured
against. NO SUMO golden — this is a *benchmark*, not a parity scenario (it measures the engine's
cost and proves determinism; it does not assert a trajectory). `ParameterCrossCheckTests` skips it
(non-recursive scenario discovery + no `golden.vtype.json`).

## Workload
- 3-lane straight edge `e0`, 5000 m, speed 13.89.
- 420 vehicles, staggered departs (one per lane every 3 s), ~20 % capped at `maxSpeed=8` so the
  lane-change / neighbor-query / reducer hot paths all fire (following, keep-right, speed-gain
  overtakes). `lanechange.duration=0`, `sigma=0`, Euler, `collision.action=none`, seed 42.

## Reproduce
```
dotnet run -c Release --project src/Sim.Bench 500
```
(`Sim.Bench` is a console harness, NOT part of `dotnet test`. `RungD1BenchmarkDeterminismTests`
runs a short slice of this scenario in the offline loop to guard determinism.)

## Baseline — current engine (AoS `VehicleRuntime` class, LINQ + per-step allocations)
Captured on the reference VM (.NET 8, Release, workstation GC), 500 steps:

| metric | value |
|---|---|
| peak concurrent vehicles | 378 |
| veh-steps emitted | 115,141 |
| wall time | 0.389 s |
| throughput | **1284 steps/s** (0.779 ms/step) |
| alloc total | **80.9 MiB** |
| alloc / step | 165.6 KiB |
| alloc / veh-step | **736.5 B** |
| GC gen0/1/2 | 5 / 3 / 0 |
| deterministic (2 runs identical) | **True** |

Absolute numbers are machine-dependent; what matters is the **delta each Group-D rung produces**
against this baseline (re-run the harness and update this table, keeping the old row for history).

## D2 (int-handle lane identity)
Captured on the same reference VM, same command, 500 steps, immediately after the D2 refactor
(dense int `Lane.Handle`/`VehicleRuntime.LaneHandle`/`LaneSequenceHandles`; `LaneNeighborQuery`'s
per-lane buckets keyed by handle instead of `LaneId` string):

| metric | value |
|---|---|
| peak concurrent vehicles | 378 |
| veh-steps emitted | 115,141 |
| wall time | 0.393 s |
| throughput | **1272 steps/s** (0.786 ms/step) |
| alloc total | **80.8 MiB** |
| alloc / step | 165.5 KiB |
| alloc / veh-step | **735.8 B** |
| GC gen0/1/2 | 5 / 3 / 0 |
| deterministic (2 runs identical) | **True** (hash unchanged: `909605E965BFFE59` before and after) |

D2 mainly enables D3/D4 (a dense int handle is a prerequisite for unmanaged FDP components and
for D4's handle-indexed reusable buckets) — the alloc drop here is modest (~0.7 B/veh-step) as
expected, because `LaneNeighborQuery` still allocates a fresh `Dictionary`-shaped array +
per-lane `List<VehicleRuntime>` every Build call (D4's job is making that reusable); what D2
removes is the *string hashing/interning* cost of every per-vehicle, per-step `LanesById[laneId]`
and neighbor-bucket lookup, replacing it with a direct array index. Throughput is within
run-to-run noise of the baseline on this VM.

## D4 (zero-alloc hot path)
Captured on the same reference VM, same command, 500 steps, immediately after the D4 refactor:
the reducer's `new List<double>{...}.Min()` became a running `Math.Min` over the same six
constraint calls in the same order (`Engine.ComputeMoveIntent`); `LaneNeighborQuery` became a
REUSABLE instance (`Engine._neighborQuery`, built once in `LoadScenario`, sized off
`network.LanesByHandle.Count`) with pre-allocated per-lane `List<VehicleRuntime>` buckets that
`Refill` (`List.Clear()` + re-add + re-sort, no new lists/arrays) replaces the old per-step
`Build` factory for BOTH the pre-move snapshot (`Run()`) and the post-move snapshot
(`DecideSpeedGainChanges()`); the junction-yield reducer's `junction.Requests.FirstOrDefault(...)`
/ `junction.Conflicts.FirstOrDefault(...)` (each closing over loop locals, i.e. allocating a
closure every call) became plain `foreach` scans; and the keep-right/speed-gain left/right
neighbor-lane lookups (`edge.Lanes.FirstOrDefault(l => l.Index == lane.Index ± 1)`) became O(1)
reads of a new precomputed `Lane.LeftNeighbor`/`Lane.RightNeighbor` handle, filled once at ingest
in `NetworkParser`:

| metric | value |
|---|---|
| peak concurrent vehicles | 378 |
| veh-steps emitted | 115,141 |
| wall time | 0.230 s |
| throughput | **2175 steps/s** (0.460 ms/step) (run-to-run range 1111–2175 steps/s on this shared VM; alloc/veh-step is the reliable, non-noisy signal below) |
| alloc total | **22.7 MiB** |
| alloc / step | 46.6 KiB |
| alloc / veh-step | **207.1 B** (down from 735.8 B at D2 — a 71.9% reduction, 528.7 B/veh-step removed) |
| GC gen0/1/2 | 2 / 2 / 1 |
| deterministic (2 runs identical) | **True** (hash unchanged: `909605E965BFFE59`, same as D1/D2) |

**What's left (the remaining allocator, out of D4's scope per the briefing):** `EmitTrajectory`'s
`TrajectorySet.Add` — the `TrajectoryPoint` record + its `SortedDictionary<double,
TrajectoryPoint>`-per-vehicle storage — is untouched; this is the FCD/output-contract boundary,
a separate concern from the `OnUpdate` hot path D4 targets (per the briefing: "leave the FCD
emit / `TrajectorySet` alone"). The remaining ~207 B/veh-step is consistent with one
`TrajectoryPoint` allocation (a `sealed record`, heap-allocated) plus its dictionary-entry
bookkeeping per vehicle per step; a future rung could move FCD emission to a reusable buffer/
struct-of-arrays if the export path itself needs to be zero-alloc.

## What the numbers say (targets for D2–D8)
- **~736 B allocated per vehicle-step** is the headline: this is the AoS `class` entities +
  `LaneNeighborQuery`'s per-step `Dictionary`/`List` (built twice/step) + the reducer's
  `new List<double>` per vehicle + LINQ iterators + the `TrajectorySet` emit (SortedDictionary +
  a `TrajectoryPoint` per veh-step). FDP's rule is **zero** heap alloc in the `OnUpdate` hot path —
  D4 targets the step-loop allocations, and the emit can move to a reusable buffer.
- **deterministic = True** at 378 concurrent vehicles is the load-bearing invariant: it is what lets
  D8 parallelize the Simulation phase and still get byte-identical output. The offline determinism
  test locks it in so no later rung can silently break it.
