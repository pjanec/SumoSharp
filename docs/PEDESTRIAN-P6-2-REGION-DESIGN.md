# PEDESTRIAN-P6-2-REGION-DESIGN.md — spatial region decomposition for `OrcaCrowd.Step`

**Status: design (design-first per `CLAUDE.md`).** HOW + a task breakdown with success conditions. This is
the pedestrian port of the vehicle engine's proven byte-identical `--region` domain decomposition
(`Engine.cs`, `docs/SPATIAL-OPT.md`), triggered and justified by the perf campaign
(`PEDESTRIAN-P6-1-RESULTS.md`, `PEDESTRIAN-COMBINED-LOAD-RESULTS.md`; the P6-2 line in
`PEDESTRIAN-TRACKER.md` — owned by the perf session — records the GO).

## 1. Why (the trigger, in one paragraph)

The combined-load campaign found the single-station deployment is feasible **only if peds get ≥ 6 cores**,
and heavy-churn 100k peds **fail real-time (5.5 st/s, −45%) when starved to 4 cores** — while vehicles clear
by 30–50× in every split. Pedestrian per-core throughput is the sole real-time-marginal quantity, and it is
**core-scaling-limited, not contention-limited** (concurrency tax is only ~5–9% at 6–8 cores). The vehicle
engine already solved this exact memory-bandwidth-bound shape with `--region` (byte-identical; ~3× @4c,
~4.5× @8c vs serial). **Target: ~1.4× ped churn per-core uplift** so a 6-core ped budget clears churn with a
comfortable ~1.5× margin (≥15 st/s vs the 11.1 st/s measured at 6+6). More is better; 1.4× is the bar.

## 2. What already exists (the bit-identical baseline we must not break)

`OrcaCrowd.Step(dt)` is **already parallel and already spatial-hash-accelerated, both bit-identical to
serial** — P6-2 refines *this*, it does not replace it:

- **`UseParallelStep`** — `Parallel.For` over agents, each worker holding its own `ScratchSet`
  (localInit/localFinally). The plan reads only **frozen start-of-step state** and each iteration writes
  **only its own `_newVelocity[i]` slot**, so output is **bit-identical to serial regardless of thread count
  or scheduling** (`OrcaParallelStepTests`).
- **`UseSpatialHash`** — a uniform grid (cell size == `NeighbourDist`, 3×3 covers a neighbourhood), rebuilt
  serially once per `Step` from frozen positions; candidates are **sorted to the identical order** the
  brute-force path produces, so the neighbour set *and order* (hence every LP, hence every trajectory) are
  **bit-identical** — the grid is a pure pre-filter.
- **`MaxParallelism`** / **`ParallelThreshold`** — the same knobs the lane engine exposes.

**The plateau P6-2 attacks** (`PEDESTRIAN-P6-1-RESULTS.md`): agent-parallel + a *global* hash still scatters
neighbour reads across the whole SoA + hash, so it saturates memory bandwidth at ~8–16 threads. The fix is
**locality**, not more threads.

## 3. HOW — region-partitioned, cache-resident agent sweeps (bit-identical)

The core idea, mirroring the vehicle `--region`: **partition the crowd's world into a grid of spatial
regions; group/reorder agents by region so each parallel worker sweeps one region's agents whose
neighbourhood data is cache-resident**, instead of every worker touching the whole array. The ORCA math per
agent is untouched; only *which agents a worker processes together* and *the memory order they sit in* change.

Mechanism per `Step` (all reads of frozen start-of-step state, exactly as today):

1. **Region grid.** Overlay a uniform region grid on the crowd bounding box, region cell size a small
   multiple of `NeighbourDist` (so a region is many hash cells wide — big enough that the halo is a small
   fraction of the working set, small enough to stay cache-resident). Region size is a tuning knob (finer =
   better balance + smaller working set, per the vehicle findings).
2. **Assign + reorder agents by region.** Bucket agents into regions by frozen position and process regions
   as the unit of parallel work (`Parallel.ForEach` over regions, or a work-stealing queue as `--region`
   uses). Optionally **reorder the agent SoA into region-contiguous (grid/Morton) order** so a region's
   agents are a contiguous span — this is what makes the neighbour reads cache-local.
3. **Halo neighbours.** An agent near a region edge still needs neighbours within `NeighbourDist` from the
   adjacent region. Each region's neighbour gather therefore reads its own agents **plus a halo band of
   width `NeighbourDist`** from neighbouring regions — all from the **frozen** snapshot. The **neighbour SET
   for every agent is exactly the same set the global solve produces** (same radius, same agents); the
   region is only a work-partition, not a neighbourhood cutoff.
4. **Per-region parallel solve → own slots.** Each worker computes `_newVelocity[i]` for its region's agents
   from the frozen halo, writing only its own slots (no cross-region writes) — identical discipline to
   `UseParallelStep`.
5. **Integrate.** Unchanged.

**Boundary hand-off is free**, exactly as in `--region`: an agent that moves into another region this step is
simply re-bucketed by its new position next step (regions are re-assigned per `Step` from frozen positions);
there is no explicit state transfer.

## 4. The parity argument (the load-bearing part)

P6-2 must be **bit-identical to serial** (or gated behind an explicit opt-in fast-mode flag — never a silent
trajectory change; `CLAUDE.md` rule 3). Bit-identity holds because:

- **Same neighbour set.** The halo is the full `NeighbourDist` radius, so every agent sees exactly the
  agents it sees in the global solve — no neighbour is dropped or added at a region edge.
- **Same neighbour order.** The candidate list is put through the **identical deterministic sort** the
  existing `UseSpatialHash` path uses (total order + stable tie-break on handle), so the ORCA line order —
  hence every LP, hence every velocity — is unchanged regardless of the order regions/halo cells were
  gathered in.
- **Same per-agent math, own-slot writes.** Each agent's solve is independent given its (unchanged) neighbour
  list, reads frozen state, and writes only `_newVelocity[i]` — so region grouping and SoA reordering are
  pure memory/scheduling changes with no numerical effect.
- **Reordering is transparent.** If the SoA is reordered into region order, results are permuted back to
  stable handle order before any hash/observer sees them, so no downstream consumer can observe the reorder.

**Gate:** extend the POC-7a / `OrcaParallelStepTests` style — assert the region-decomposed `Step` produces a
**bit-identical trajectory hash** to the serial (and to the existing parallel) path over a churny scenario,
across thread counts and region sizes. If any sub-optimization cannot be made exactly bit-identical, it is
gated behind an opt-in `UseRegionDecomposition` fast-mode flag (default off → today's behaviour, untouched),
never silently accepted.

## 5. Reuse / seams touched

- **Reuses:** `UseParallelStep`'s frozen-plan / own-slot discipline; `UseSpatialHash`'s grid + identical
  candidate sort (the region gather is the same grid queried region-locally); the vehicle `--region`
  ownership + work-stealing + free-boundary-handoff pattern as the template.
- **Touched:** `src/Sim.Core/Orca/OrcaCrowd.cs` (the region partition + gather + parallel dispatch), behind a
  new opt-in flag. `PedLodManager` opts its high-power `OrcaCrowd` into it once green. No public API removed.
- **Untouched:** the ORCA LP math, the serial path, the parity core, every committed golden. Parity-inert by
  construction (this is a perf refactor of an already-gated, non-golden-reachable crowd solve).

## 6. Tasks & success conditions

Legend: `[ ]` not started · `[~]` in progress · `[x]` done.

- [x] **P6-2-1 — Region grid + per-`Step` agent→region bucketing** (frozen positions; region size a knob).
  *Success:* buckets partition all agents exactly once; re-bucketing per step handles movement (an agent that
  crosses a region boundary lands in the new region next step) — unit-tested against a brute-force oracle.
  *Done:* `src/Sim.Core/Orca/RegionPartition.cs` (deterministic, ascending-by-index, pooled; same FloorDiv/
  pack-cell math as the agent grid; parity-inert — no solve touched). `RegionPartitionTests` (7 tests) prove
  the partition/oracle/movement/determinism conditions; offline gate green.
- [x] **P6-2-2 / P6-2-3 — Region-*task* dispatch over the shared frozen grid (opt-in `UseRegionDecomposition`).**
  **Design refinement (as built):** a separate per-region *local* grid + explicit `NeighbourDist` halo gather
  proved **unnecessary** — keeping the existing *shared, read-only* frozen grid (`RebuildGrid` /
  `GridCandidates`, already bit-identical) and changing only *which agents a worker processes together*
  (region-task parallelism) is the faithful port of the vehicle `--region` (which also processes region-grouped
  work over shared frozen state, not isolated per-region grids). This makes the halo moot and bit-identity
  automatic (the gather + per-agent `Plan` are untouched). *Done:* `OrcaCrowd.UseRegionDecomposition` +
  `RegionCellSizeMultiplier` + `PlanRegions` (one `Parallel.For` task per region, own `ScratchSet`, unchanged
  `Plan`). *Success met:* `OrcaRegionDecompositionTests` (7) assert the region path is **bit-identical to
  serial** for every agent every step across region sizes {1,2,3,4,8×NeighbourDist}, thread caps {−1,2,4,8},
  spatial-hash on/off, and the MaxNeighbours+removal combination, on 400/600-agent spread + dense-counterflow
  scenarios (all > the 256 parallel threshold). Default-off path untouched; offline gate 649/142/2/1 green.
- [ ] **P6-2-4 — Perf validation (on-target, perf session).** *Success:* measured **≥1.4× ped churn per-core
  uplift** vs the current parallel path on `Sim.BenchCrowd`/`Sim.BenchPedLod` at the 4–8-core caps, and a
  re-run of the 6+6 / 8+4 combined-load splits showing heavy churn clearing real-time with ≥1.5× margin at
  6 ped cores (or the honest measured figure). Documented in a P6-2 results doc / findings update.
- [x] **P6-2-5 — Wire into `PedLodManager`** high-power crowd (opt-in). *Done:* `UseRegionDecompositionHighCrowd`
  + `HighCrowdRegionCellSizeMultiplier` passthroughs (mirror `UseParallelHighCrowd`), default off.
  `PedLodRegionDecompositionTests` promotes 400 peds (≥256 high-power after the dwell) through the FULL manager
  and asserts region-on is bit-identical to default on position AND DR-model every step. Gate green with the
  flag off (all ped tests pass with the passthrough present) and bit-identical with it on. *(Base gate has
  grown to 649/142/2/1 as tests were added; the invariant is "never drops / hash unchanged", satisfied.)*

## 7. Standing invariants (every task)

- [ ] Offline gate green and **unchanged in count/hash** with the flag off; with the flag on, the ped
  determinism/hash gate (P6-2-3) is bit-identical to serial.
- [ ] No `System.Random`; per-entity seeded RNG only; results independent of thread count / region size /
  schedule (determinism is exact in phase 1).
- [ ] Any sub-step that cannot be proven bit-identical is gated behind the opt-in fast-mode flag, never
  silently accepted (`CLAUDE.md` rule 3).

## 8. Risks / open questions

- **SoA reorder cost vs locality win.** Reordering the agent array into region order each step has its own
  bandwidth cost; the net win must exceed it. Mitigation: reorder incrementally (agents move little per
  step) or keep an index permutation rather than moving payload. Measure both.
- **Region size tuning** (balance vs halo overhead vs cache residency) — a knob, swept in P6-2-4.
- **Diminishing return at the bandwidth wall.** Even `--region` on vehicles caps ~3–4.5× (memory-bandwidth-
  bound); P6-2 will *raise* the ped curve and improve low-core locality but will not defeat the wall — the
  1.4× target is deliberately modest and within reach, not an unbounded claim.
- **Dense single-plaza worst case** (one hot region) balances poorly; work-stealing over finer regions is the
  mitigation, as on the vehicle side.
