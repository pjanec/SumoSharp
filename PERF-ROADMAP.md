# PERF-ROADMAP.md — SIMD & parallelization roadmap

> **📄 Reference documentation — superseded for the on-target work.** The Layer-0/1 allocation &
> parallelization items in this roadmap have **landed** (byte-identical, parity-gated). The actual
> on-target (16-core/24-thread Windows) measurement, results, and the definitive experiments log —
> including which further ideas were tried and **regressed** (per-field SoA, parallel foeIndex, …) —
> now live in **`PERF-HANDOVER.md`**; read that first for the current perf picture. This file is the
> original plan, kept for context.

**Read `CLAUDE.md`, `DESIGN.md` first.** This is a design + measurement record for the README's
in-scope-not-done "SIMD and parallelization" item. It is a PLAN, not an implementation — no engine
code is changed by this doc. When optimization work starts, it must obey the parity iron law: the
`Sim.Bench` determinism hash and every committed golden stay byte-identical, or the change is gated
behind an explicit opt-in `fast-mode` flag (CLAUDE.md rule 3).

Headline: **the money is in killing per-step allocation and enabling the (already byte-identical)
parallel path at scale — not in vectorized math.** SIMD is last, marginal, and highest-risk.
This matches DESIGN.md's own prediction ("real early wins come from struct-of-arrays cache locality
and multithreading; SIMD … a later, more marginal gain").

## Measured baseline (4 cores, this VM; commit 6fffec4)

`Sim.Bench` (`src/Sim.Bench`) runs `scenarios/_bench/highway-dense`; measurements below also point it
at the larger city benches (temporary edit, reverted — not committed).

| Scenario | peak concurrent | ms/step (serial) | alloc / veh-step | parallel speedup (4c) |
|---|---|---|---|---|
| highway-dense | 378 | 0.84 | 744 B | **0.95×** (net loss) |
| city-3000 | 756 | 401 | 9,301 B | **3.32×** (win) |
| city-15000 (25.7k veh) | — | — | — | did not finish in window |

Determinism hash `909605E965BFFE59` (highway) / `B8357C3898EEFA4C` (city-3000); **`hashPar == hashA`
in every case** — the existing `Parallel.For` plan phase is already provably byte-identical.

### What the numbers say

1. **Parallelization already exists and is correct.** `Engine.UseParallelPlan` wraps `PlanMovements`
   + the willPass pre-pass in `System.Threading.Tasks.Parallel.For`. Its payoff is scale-gated:
   **0.95× at 378 veh** (overhead > benefit; per-vehicle work too light) but **3.32× at 756
   concurrent** (heavy per-vehicle junction/LC work amortizes the overhead). It is OFF by default.
2. **No SIMD** anywhere (`grep System.Numerics.Vector` → nothing), and the layout is **AoS**:
   `VehicleRuntime` is a `sealed class` in `List<VehicleRuntime>` (pointer-chasing) — SIMD-hostile.
3. **The plan phase is already allocation-free** (`MoveIntent` is a struct; `ComputeMoveIntent`
   allocates nothing). All churn is elsewhere:
   - **Emit path** — `TrajectoryPoint` is a heap `record` stored in
     `Dictionary<string, SortedDictionary<double, TrajectoryPoint>>` (`TrajectorySet`): one record +
     one red-black-tree node insert *per vehicle per step*. Serial, and NOT under `UseParallelPlan`,
     so it caps parallel scaling.
   - **`ComputeBestLanes`** (`Sim.Ingest/NetworkModel.cs`) — `OrderBy`/`Where`/`Select`/`Distinct`/
     `ToList` + closures, called per lane-changing vehicle per step (from `TryStrategicLaneChange`/
     `KeepRightStrategicStay`). This is the 744 B → 9.3 KB/veh-step jump at city scale.
4. **The city step is super-linear** (0.84 ms → 401 ms for 2× vehicles) — dominated by per-vehicle
   junction/best-lanes/LC work, not car-following (which is O(N) via the sorted per-lane neighbor
   lists that `LaneNeighborQuery.Refill` builds once/step).

## The plan — three layers, ordered by ROI and inverse risk

### Layer 0 — de-allocate the hot path  (biggest single win, low–medium risk)

- **Emit:** stop materializing `TrajectoryPoint` records into a `SortedDictionary`. Stream through
  the existing zero-alloc `ISimExportObserver` / `VehicleExportSnapshot` (`readonly struct`) seam, or
  write to reusable columnar buffers (SoA arrays reused across steps). The FCD writer and the bench
  only *stream*; they don't need `TrajectorySet`'s query-by-(vehicleId, time). Keep a query adapter
  only where a caller (the comparator) genuinely needs it.
- **Best-lanes:** replace `ComputeBestLanes`' LINQ with hand loops over pooled buffers, and **cache
  the result per vehicle**, invalidating only on edge change (best-lanes is stable within an edge —
  recomputing every step is pure waste). The code already hints at this ("so the allocating
  ComputeBestLanes pass stays off the per-step hot path") but the city LC path still calls it hot.
- **Expected:** alloc/veh-step 9.3 KB → tens of bytes; GC gen0/1/2 → ~0; **+30–80 % single-thread at
  city scale**; and it *unblocks parallel scaling* (removes cross-thread GC contention).
- **Risk:** LOW–MEDIUM. The cached best-lanes result and the streamed emit must be byte-identical;
  the parity hash + goldens are the guard. Cache invalidation must be provably correct on edge change.

### Layer 1 — make parallel the default at scale  (medium win, already proven byte-identical)

- **Auto-enable** `UseParallelPlan` above a concurrency threshold (~1k peak concurrent) so the tiny
  parity scenarios (1–756 veh) stay serial — no regression, no test-loop scheduling flakiness.
- **Chunk the partitioning** (`Partitioner.Create` range partitioner) to cut the per-index delegate/
  task overhead that makes the current `Parallel.For(0, count, i => …)` lose at small scale.
- After Layer 0, **parallelize the embarrassingly-parallel execute/emit work** too — position
  integration and x/y polyline derivation are per-vehicle-independent and currently serial.
- **Expected:** 3.0–3.5× on 4 cores at ≥1k concurrent; combined with Layer 0, **~4–6× at city scale**
  over today. Scales toward (cores − 1) as per-vehicle work grows.
- **Risk:** MEDIUM. Junction determinism (already neutralized via the static `<request>` matrix +
  frozen start-of-step snapshot) must be preserved. Thread count must never leak into results (proven
  today: `hashPar == hashA`). Must stay size-gated so the offline test loop stays serial + fast.

### Layer 2 — SIMD the vectorizable arithmetic  (marginal win, highest risk, gated behind SoA)

- **Blocker: AoS→SoA.** `VehicleRuntime` (a `sealed class`, ~30 fields, threaded by reference through
  plan/execute/command-buffer/neighbor-query) must become columnar arrays (`pos[]`, `speed[]`,
  `accel[]`, vType/lane handles, …) before any vectorization is possible.
- **Vectorizable once SoA** (`Vector<double>`, 4-wide AVX2 / 8-wide AVX-512): the `finalizeSpeed`
  accel/decel clamp, the Krauss / Rail traction + resistance polynomial, and the x/y polyline
  interpolation in emit — all per-vehicle-independent arithmetic.
- **NOT vectorizable:** the leader-gap **gather** (each follower's leader is at a non-contiguous
  index — DESIGN.md's own caveat), the branchy junction/foe logic, the min-over-heterogeneous-
  constraints reduction.
- **Expected:** 1.3–1.8× on the arithmetic fraction → **~1.15–1.35× on the whole plan phase** after
  Amdahl. Marginal.
- **Risk:** HIGH. (1) **Parity vs FP reassociation** — SIMD FMA and reordered horizontal reductions
  change rounding → trajectory drift → parity fail. Must disable FMA, preserve op order, avoid
  reassociating reductions. This is the #1 SIMD blocker. (2) The SoA refactor is engine-wide — huge
  regression surface against the goldens. Recommend an incremental **hot-columns mirror**
  (pos/speed/accel in parallel arrays alongside the class, synced at step boundaries, used only in the
  vectorized inner loop) to bound the blast radius — but even that is a large change for a ~1.2×
  return. (3) The gather cost may eat the arithmetic win.

## Memory strategy — reusable managed buffers, NOT a native pool

"Even small heap allocs are bad" is half-right for .NET: allocating a small gen0 object is cheap
(bump-pointer); what bites is the **rate** (gen0 GC frequency -> pauses) and **promotion** (surviving
to gen1/2). The two allocation sources here are different kinds of bad, and neither is fixed by a
memory pool of the current objects:

- **Emit retains a growing live set.** `TrajectorySet` keeps EVERY `TrajectoryPoint` (a heap
  `record`) for the whole run in `Dictionary<string, SortedDictionary<double, …>>`. That is the
  81.7 MiB "alloc total" promoted to gen1/2 — not transient gen0 garbage. Pooling objects you never
  free during the run buys nothing; the fix is to stop retaining what is only streamed.
- **`ComputeBestLanes` LINQ** is genuinely transient gen0 garbage (dies same-step).

**The fix is "don't allocate," via value types in reusable MANAGED buffers — not a pool, not native
memory:**
- Emit: make `TrajectoryPoint` a `struct`, stream it through the existing zero-alloc
  `VehicleExportSnapshot` / `ISimExportObserver` seam (the FCD writer only streams), or store into a
  columnar growable buffer (`double[] pos`, `double[] speed`, … reused/grown) instead of millions of
  records + red-black-tree nodes. One array of structs is ~zero per-step alloc AND GC-scan-free.
- Best-lanes: hand loops over a preallocated per-vehicle scratch buffer + cache the result per edge;
  `ArrayPool<T>.Shared` for variable-size scratch.

This is zero-alloc on the hot path, fully managed, fully cross-platform (Linux + Windows), no
`unsafe`, and no parity risk beyond the logic change.

### Why a native / unmanaged memory pool does NOT earn its keep here

Key .NET fact: **an array of unmanaged structs (`double[]`, `int[]`, the SoA columns) is already not
GC-scanned in its interior** — the GC only tracks the array header, not its millions of doubles.
Moving that data to `NativeMemory.Alloc` saves the GC almost nothing, while adding manual lifetime
management (leaks, use-after-free, no bounds checks) — a direct threat to the parity iron law.
Unmanaged pools only win with gigabytes of live *reference-bearing* data the GC must scan, or for
native interop / huge-pages / NUMA — none of which apply here.

### Cross-platform memory options (single codebase, no per-OS branches)

- **`ArrayPool<T>`** — pure managed, identical on both platforms; use for variable-size scratch.
- **`GC.AllocateArray<T>(n, pinned: true)`** (.NET 5+) — a MANAGED PINNED array: stable address for
  SIMD/interop, keeps bounds checks + GC ownership, never moved. The safe middle ground if a SoA
  column ever needs a fixed address — prefer it over raw native.
- **`NativeMemory.Alloc/AlignedAlloc/Free`** (.NET 6+) — genuinely cross-platform (wraps
  malloc / _aligned_malloc). Reach for it LAST and gate it hard; only if true off-heap/interop is
  needed.
- **SIMD does not need native memory.** `System.Numerics.Vector<T>` / `System.Runtime.Intrinsics`
  vectorize over MANAGED arrays via `MemoryMarshal.Cast<double, Vector<double>>(span)`; the JIT picks
  AVX2/AVX-512 (x64) or NEON (ARM) per platform. Layer 2 SIMD therefore works on plain `double[]` —
  the only caveat stays FP reassociation vs parity (below), not memory placement.
- Avoid assuming a GC *mode* (Server vs Workstation) or huge-page/NUMA APIs — they differ across OSes;
  the reusable-managed-buffer route never touches them.

| Approach | Fixes the measured problem? | Cross-platform | Parity risk | Verdict |
|---|---|---|---|---|
| Structs + reusable managed buffers / `ArrayPool` | Yes | Trivially | Low | **Do this (== Layer 0)** |
| `GC.AllocateArray(pinned)` for SoA columns | Marginal extra | Yes | Low | Only if profiling shows column GC pauses |
| Native/unmanaged pool (`NativeMemory`) | No real gain for unmanaged-struct data | Yes, but manual mgmt | High (UAF/leaks) | Skip unless native interop later |

## RESULTS — implemented Layers 0–1 (measured, 4 cores, this VM)

Landed on `main` (perf commits), each parity-gated: full suite **227 passed / 1 skipped** and the
`Sim.Bench` determinism hash **`909605E965BFFE59` unchanged** (single AND parallel) after every step.

| Change | What | Highway (378) result |
|---|---|---|
| **L0a** | `TrajectoryPoint` heap record -> `readonly record struct`; `TrajectorySet` = `List<struct>` append + LAZY sorted index (built off the hot path) | single 2048 -> 3072 steps/s (**1.5x**); GC gen1 3 -> 1 |
| **L0b** | memoize `ComputeBestLanes` (pure fn of route+edge) in a `ConcurrentDictionary`, state-passing `GetOrAdd` (alloc-free hits, thread-safe) | alloc/veh-step 774 -> 348 B (**-55%**); parallel flips from 0.80x (loss) to **1.22x** (win) |
| **L1** | size-gated AUTO-parallel: `UseParallelPlan` is an explicit override, else auto when `_vehicles.Count >= 256` (tiny parity scenarios + the test loop stay serial; large city auto-parallelizes) | keeps per-index `Parallel.For` |
| **L0c** | `ResolveSequenceCore` (per-vehicle-at-insertion) called `ComputeBestLanes` per edge AND per hop, each re-running the whole route-end backward pass -> O(N²) `BackwardPassEdge` calls (each ~10 collection allocs). New `ComputeAllBestLanes` captures EVERY edge's LaneQ from ONE backward pass (O(N)); byte-identical (same threading, first-occurrence dict). | city-mixed-1k total engine alloc 3780 -> 1842 MiB (**-51%**) |
| **L0d** | per-step junction-constraint allocs: `CrossJunctionLeaderConstraint` List+closure -> span + by-value struct callback; `LaneSpaceTillLastStanding` List -> `[ThreadStatic]` scratch; `InsertDepartingVehicles` LINQ (`OrderBy`/`.First`/`new List`/`new HashSet`) -> in-place stable `Sort` + manual loop + reused buffers. Parallel byte-identical (forced ser-vs-par on junction-heavy city-300). | city-mixed-1k 1842 -> 1156 MiB (**-37%**; L0c+L0d **-69%** vs 3780) |

City scale (`city-3000`, 756 concurrent): parallel **~2.4x** over single, byte-identical (hashPar ==
hashA). A CHUNKED range partitioner was tried for L1 and REVERTED: it regressed city from ~2.4x to
~1.0x because `_vehicles` is sparsely active (7632 total, ~756 active), so static contiguous chunks
load-imbalance — per-index `Parallel.For`'s work-stealing handles the sparse case, and its overhead is
negligible post-L0. Highway (378, near the parallel break-even) is noisy at ~0.95–1.7x/run; the
auto-gate keeps it and everything below 256 serial anyway. Net: parallel is now DEFAULT at scale and a
clean ~2.4x win at city, with correctness proven byte-identical.

### Corrected finding — the DOMINANT city allocator is INSERTION, not best-lanes

The original roadmap guessed the `744 B -> 9.3 KB/veh-step` city jump was `ComputeBestLanes` LINQ.
**Per-phase allocation probing (city-mixed-1k, single-thread) disproved that:** L0b's best-lanes memo
helped highway a lot but barely moved city, because city vehicles have UNIQUE embedded routes so the
memo never shares. The real per-step allocation split (MiB over 60 steps):

```
insert=73.6  execute=20.6  postlc=8.3  plan=7.6  willpass=4.3  emit=1.2  reroute=0  refill=0
```

`InsertDepartingVehicles` (73.6 MiB, ~47% of all alloc) dominates. It resolves each vehicle's route
to a lane sequence ONCE at depart via `NetworkModel.ResolveSequenceCore`, which is LINQ-dense
(`.First(pred)`/`.Where().Select().ToList()`/`.OrderBy().First()` per route edge) AND calls the
UN-memoized internal `ComputeBestLanes` per edge. On unique routes every call is a cache miss, so
memoization cannot help. **DONE (L0c):** the real fix was ALGORITHMIC, not buffer-pooling — the
dominant cost was `ResolveSequenceCore` calling `ComputeBestLanes` per edge AND per hop, each
re-running the whole route-end backward pass (O(N²) `BackwardPassEdge` calls, each allocating ~10
collections). `ComputeAllBestLanes` captures every edge's LaneQ from ONE backward pass (O(N)),
byte-identical (hash `909605E965BFFE59` unchanged, 227 tests green), cutting city-mixed-1k total
engine allocation −51% (3780 -> 1842 MiB). It is a per-vehicle-ONCE cost (dominates SHORT sims; the
per-step allocators below dominate LONG sims), and it is SERIAL (Input phase), so reducing it also
unblocks parallel scaling.

Per-step allocators — **DONE (L0d)**, each byte-identical (hash `909605E965BFFE59` unchanged, 227
tests green + a forced serial-vs-parallel check on the junction-heavy city-300, since highway-dense
has no junctions to exercise these paths):
- `CrossJunctionLeaderConstraint` — was `new List<int>` + a `h => neighbors.GetRearmost(ego, h)`
  CLOSURE per vehicle per step. Now a `ReadOnlySpan<int>` over the (plan-phase-stable) `_laneSeqPool`
  slice + a by-value struct callback (`where TRearmost : struct, IRearmostSource`, JIT-specialized),
  so zero alloc. The insertion caller's `poolSeq[1..]` array copy became `poolSeq.AsSpan(1)` too.
- `LaneSpaceTillLastStanding` (KeepClear) — was `new List<VehicleRuntime>` per call; now a
  `[ThreadStatic]` scratch reused across calls (thread-local because KeepClear runs in the parallel
  Plan phase; the method neither recurses nor keeps the list past its return).
- `InsertDepartingVehicles` per-step LINQ — `candidates.OrderBy(Depart)` (stable) became an in-place
  `Sort` keyed by `(Depart, EntityIndex)` (byte-identical since `_vehicles` is ascending-EntityIndex
  order), the per-candidate `edge.Lanes.First(l => l.Index == ...)` closure became a manual loop, and
  the per-step `candidates`/`blockedLanes` collections are reused instance buffers.

Combined L0c+L0d win: city-mixed-1k total engine allocation **3780 -> 1156 MiB (-69%)** (L0c 3780
-> 1842, L0d 1842 -> 1156).

### L2 — the ~4k-concurrent super-linear TIME term (profiled + fixed), byte-identical

The alloc work above cut GC pressure; it did NOT touch the per-step COMPUTE, and `city-3000` (~4,300
concurrent) still ran at **~0.7 steps/s** (~28 min for the 1200-step run). Per-constraint timing
(city-mixed-1k, `Stopwatch` probe, reverted) found the cause — two per-vehicle constraints were **~88%
of the whole plan phase**, both doing a flat O(N) scan over EVERY active vehicle, called per vehicle =>
**O(N²)**:

```
JunctionYield  128,296 ns/call   (~55% of plan)   <- FindFoeVehicle: scan all vehicles x whole route
KeepClear      100,933 ns/call   (~43% of plan)   <- LaneBruttoVehLenSum / LaneSpaceTillLastStanding
CrossJunction         275 ns/call (~0%)           <- uses the pos-sorted per-lane index (cheap)
```

Car-following is ~275 ns/call; these were ~400x slower purely from re-scanning the population. Both
fixed with a per-lane / per-internal-lane index and NO behavioral change:
- **`FindFoeVehicle`** — a per-step index (`BuildFoeApproachIndex`, built once right after
  `neighbors.Refill`) maps each internal lane handle to the FIRST TWO distinct vehicles (in `_vehicles`
  order) whose route contains it; the lookup is O(1) and byte-identical (ego can only be the first of
  the two, so "first non-ego match" == `first != ego ? first : second`).
- **`LaneBruttoVehLenSum` / `LaneSpaceTillLastStanding`** — read the pos-sorted per-lane bucket
  `LaneNeighborQuery` already maintains (`OnLane(handle)`) instead of filtering all active vehicles;
  byte-identical (same frozen snapshot; homogeneous sum is order-independent; the space-walk reverses
  the bucket for the same front-first order).

Result (byte-identical: 227 goldens + hash `909605E965BFFE59` unchanged single AND parallel; the
`city-3000` vs-SUMO aggregates are numerically IDENTICAL pre/post, proving only speed changed):

| Scenario | before | after | speedup |
|---|---|---|---|
| city-mixed-1k (steps/s) | 35.3 | **112.4** | **3.2x** |
| city-3000 full 1200-step run | ~0.7 steps/s (~28 min) | **31.1 steps/s (38.6 s)** | **~44x** |

This is the item flagged as "super-linear scaling unprofiled at the top end" below — now profiled and
fixed. The remaining `city-3000` vs-SUMO accuracy gap (~34% fewer arrivals at saturation) is a
SEPARATE behavioral question, untouched by this byte-identical change.

## Cumulative estimate (4 cores, ~1k+ concurrent, vs today's single-thread)

| Layer | Local win | Cumulative | Risk | Effort |
|---|---|---|---|---|
| 0 dealloc + best-lanes cache | 1.3–1.8× | 1.3–1.8× | Low–Med | Med |
| 1 parallel-by-default + parallel execute/emit | 2.5–3.5× | **~4–6×** | Med | Low–Med |
| 2 SIMD (needs SoA) | 1.15–1.35× | ~5–8× | High | High |

Most realizable gain (**~4–6×, Layers 0+1**) is structural/algorithmic and low-to-medium risk. SIMD
is the last ~1.2× at high risk.

## Cross-cutting risks & blockers

1. **The parity iron law is the master constraint.** Every layer keeps the determinism hash + all
   goldens byte-identical, or hides behind an opt-in `fast-mode` flag. This *forbids* FP reassociation
   (SIMD) and requires the best-lanes cache + parallel partitioning to be provably order/thread-
   independent.
2. **AoS→SoA is the SIMD gate and the biggest structural risk.** Defer SIMD until Layers 0–1 are
   exhausted — they don't need it.
3. **Parity scenarios are tiny (1–756 veh) and run in the offline test loop.** Parallel MUST be
   size-gated or it slows the test loop and risks nondeterministic scheduling artifacts (correctness
   is safe — `hashPar == hashA` — but speed/flakiness is not).
4. **No committed throughput gate.** The hash guards correctness, not perf. Add a `city-3000` steps/s
   floor (a perf smoke test) BEFORE optimizing, or regressions land silently.
5. **Junction order-sensitivity** (DESIGN.md) — neutralized today via static matrix + frozen snapshot;
   preserve it under any partition/parallel change.
6. **Super-linear scaling — PROFILED + FIXED at ~4k concurrent (L2 above).** The O(N²) was
   `FindFoeVehicle` + the keepClear space helpers scanning all vehicles per vehicle; indexing them
   took `city-3000` from ~0.7 to 31.1 steps/s (~44x), byte-identical. `city-15000` (25.7k veh) should
   now be re-measured — if a residual super-linear term remains at 25k+ it is a smaller, separate one.

## Suggested execution order (when this lands on a fresh main)

1. Add a `city-3000` throughput floor to the bench harness (guardrail first).
2. Layer 0a — stream emit / kill `TrajectoryPoint`+`SortedDictionary` churn. Re-measure.
3. Layer 0b — best-lanes hand-loop + per-vehicle cache. Re-measure.
4. Layer 1 — size-gated auto-parallel + chunk partitioner; then parallel execute/emit. Re-measure.
5. Profile the city-15000 super-linear term; fix algorithmically if present. **DONE at ~4k concurrent
   (L2): FindFoeVehicle + keepClear scans were O(N²); indexed → city-3000 ~44x. Re-check 25k+.**
6. (Optional, last) Layer 2 SIMD — hot-columns SoA mirror + `Vector<double>` on the arithmetic inner loop,
   FMA off, parity-gated. Only if Layers 0–1 leave a worthwhile arithmetic fraction on the table.
