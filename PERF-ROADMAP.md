# PERF-ROADMAP.md — SIMD & parallelization roadmap

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
6. **Super-linear scaling unprofiled at the top end.** `city-15000` (25.7k veh) did not finish in the
   measurement window — there is likely an O(N·junctions)-ish term worth profiling before claiming
   linear parallel scaling holds at 25k+. An algorithmic fix there could dwarf SIMD.

## Suggested execution order (when this lands on a fresh main)

1. Add a `city-3000` throughput floor to the bench harness (guardrail first).
2. Layer 0a — stream emit / kill `TrajectoryPoint`+`SortedDictionary` churn. Re-measure.
3. Layer 0b — best-lanes hand-loop + per-vehicle cache. Re-measure.
4. Layer 1 — size-gated auto-parallel + chunk partitioner; then parallel execute/emit. Re-measure.
5. Profile the city-15000 super-linear term; fix algorithmically if present.
6. (Optional, last) Layer 2 — hot-columns SoA mirror + `Vector<double>` on the arithmetic inner loop,
   FMA off, parity-gated. Only if Layers 0–1 leave a worthwhile arithmetic fraction on the table.
