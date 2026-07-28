# LIVE-CITY-PERF-TRACKER.md — task list & status

Design of record: `LIVE-CITY-PERF-DESIGN.md`. Target: **5 000 vehicles + 20 000 peds real-time in the
ENGINE** (design §1). Gates: design §7. Method: design §3–§4. Rendering is out of scope.

**Rule for this tracker: a box is ticked only when its success condition was verified FIRST-HAND by the
orchestrator, not when an implementor reported success.**

---

## Stage 0 — instruments (must land before any optimization)

- [ ] **P0 · `src/Sim.BenchLiveCity`** — headless coupled cars+peds bench, in `Traffic.sln`.
      `--cars/--peds/--steps/--warmup/--sweep/--repeats/--csv/--hi-res-radius/--hi-res-centre`;
      `--cars 0` and `--peds 0` both work (the isolation arms).
      Reports per config: **achieved** (not requested) car/ped counts, **achieved high-power/low-power
      ped split** + pocket radius in effect, wall, steps/s, RTF, step budget, **`REALTIME: yes/no`
      (mean ≤ budget AND p99 ≤ budget)**, per-step mean/p50/p95/p99/max + count over 3×p50,
      `GC.GetTotalPauseDuration()` delta, alloc/step, gen0/1/2, peak RSS, and every observed
      `LIVECITY_*`/`SUMOSHARP_*` gate value.
      *Success:* isolation arms run; two identical runs report identical behavioral counters (the
      instrument does not perturb the sim); ParityTests + LiveCity.Tests unchanged.
- [ ] **P1 · `LiveCitySim.ProfilePhases`** — additive, default-off phase timing mirroring
      `Engine.ProfilePhases` exactly, plus the inner `Engine.PhaseTicks` surfaced with a prefix.
      `--profile` prints ms + % per phase descending, **with an explicit "unaccounted" remainder** so
      missing instrumentation is visible instead of hidden.
      *Success:* no allocation and no Stopwatch when off; phases + unaccounted == measured wall.

## Stage 1 — localization (measure; fix nothing)

- [ ] **M0 · the 2×2 + ladder.** `0:0, 1000:0, 5000:0, 0:5000, 0:20000, 1000:5000, 5000:20000`,
      `--repeats 3`, medians. Establishes which of cars/peds dominates and whether the coupled cost is
      **superadditive** (⇒ an unindexed interaction scan, design §4).
- [ ] **M1 · hi-res zone sweep.** The two largest configs at `--hi-res-radius` 0 (static 70 m) vs 300.
      Establishes how hard cost scales with the high-power population — the dominant ped axis.
- [ ] **M2 · spike + GC attribution.** Does p99 ≫ p50, and does `GetTotalPauseDuration()` account for
      it? If pauses do NOT explain the tail, the owner's GC hypothesis is refuted and the tail is
      algorithmic (LOD churn, rebuilds, insertion bursts) — chase that instead.
- [ ] **M3 · the headline number.** At `5000:20000`: ms/step mean + p99, RTF, REALTIME verdict,
      high/low split, dominating phases. This is the number the whole effort is judged against.

## Stage 2 — optimization candidates (all BLOCKED on Stage 1; each needs its precondition true)

Ranked by (expected ROI × safety). Preconditions in design §6. Nothing starts until M0–M3 exist.

- [ ] **E0 · low-power ped tick-rate decoupling** *(structurally different; potentially the largest
      single win)*. Low-power peds are **dead-reckoned** — their pose is a pure function of
      (timeline, t), so a consumer can evaluate them at any time without them having been stepped every
      step. If the vast majority of 20 000 peds are low-power, ticking them on a deterministic 1-in-K
      schedule (K derived from entity id, so it is thread- and order-independent) cuts the dominant
      population's cost by ~K. *Precondition:* B dominates AND low-power peds are the bulk AND their
      per-step work is not already near-free. *Risk:* changes ped event timing ⇒ almost certainly NOT
      byte-identical ⇒ must be opt-in, and needs a behavioral gate (crowd flow/arrival equivalence).
- [ ] **E1 · SoA for the ORCA neighbour read** *(the one place SoA is right)*. `PERF-HANDOVER.md` #4
      rejected per-field SoA for CAR foe reads with a specific reason: the gap math reads ~7 fields of
      **ONE** foe (AoS-shaped), so splitting them cost 7 cache lines. **ORCA is the opposite access
      pattern** — it streams **2 fields (pos, vel) over MANY** neighbours, which is precisely what SoA
      is for. So the prior refutation does not apply here, and this is a legitimate retry on a
      different workload. *Precondition:* high-power/ORCA cost dominates and neighbour iteration is the
      hot loop.
- [ ] **E2 · ped-side parallelization.** Per-ped work is largely independent. *Precondition:* B
      dominates and the ped step is currently serial. Must keep per-entity seeded RNG (no thread-order
      dependence); prove serial==parallel.
- [ ] **E3 · car/ped phase concurrency.** Cars and peds are independent within a step up to their
      coupling points, so the car plan phase and the ped step could overlap (task-parallel, not
      data-parallel). *Precondition:* both A and B are material, so neither alone is the bottleneck.
      *Risk:* the coupling points must be identified exactly; order-dependence would be silent.
- [ ] **E4 · coupling-query indexing.** *Precondition:* M0 shows superadditivity. Expect the
      `PERF-ROADMAP.md` L2 shape (an O(N²) scan; indexing gave ~44×).
- [ ] **E5 · LOD churn / hysteresis thrash.** *Precondition:* high-power count oscillates rather than
      tracking the pocket, or promote/demote shows up as a phase cost.
- [ ] **E6 · ORCA neighbour structure.** *Precondition:* cost is superlinear in pocket *density*
      (not population) — i.e. a grid/hash problem. `Sim.BenchCrowd` bounds what is achievable.
- [ ] **E7 · publish/reconstruct rate limiting.** The publish path scales with ped count and allocates
      per-ped-per-step. *Precondition:* C is material. Note: publish exists to feed a consumer, so
      rate-limiting is a behavioral change for that consumer — opt-in.
- [ ] **E8 · ped event/pose allocation pooling.** *Precondition:* M2 shows GC pauses actually explain
      the tail.
- [ ] **E9 · `SPATIAL-OPT.md` §11** persistent segmented store. *Precondition:* A dominates and 5 000
      cars hits the car bandwidth wall. Fallback only — projected ~6%.

**Excluded (already measured + reverted on the CAR path, `PERF-HANDOVER.md` §Experiments log):**
per-field SoA for car foe reads, parallel `foeIndex`, chunked partitioner, Server GC, inline `Pos`,
vType memoization, region-parallel emit. Do not re-attempt for cars. Not automatically excluded for
peds (never measured there) — but see E1 for the standard of argument required.

---

## Appendix — viewer findings (OUT OF SCOPE for this effort; recorded so they are not lost)

The owner has scoped this effort to the engine; rendering is not the goal. A render-path audit was done
before that scoping and found the render mechanism already sound (one `MultiMesh` each for cars and
peds, no node-per-entity, no per-frame allocation in the transform loops, one directional light,
shadows off, buildings built once). Three real findings, none of them engine issues:

1. **Unbounded catch-up loop.** `while (_liveCityAccumulator >= _liveCityDt) { _liveCitySource.Tick(); }`
   (`demos/City3D/Viewer/Main.cs:1740-1745`, same shape `:1597-1605`) runs the sim synchronously on the
   Godot main thread. Once one tick exceeds the frame budget the loop runs *more* ticks per frame,
   compounding. Note this makes the viewer a **cost amplifier** for any engine slowness — so engine
   wins will show up superlinearly in the viewer, but the clamp is still worth doing later.
2. **Per-ped double geometry scan.** `HeadlessIg.TimelineElevationAt` (`HeadlessIg.cs:168-220`) rewalks
   the whole timeline and each walk-leg polyline to re-derive geometry the pose query already scanned.
   Commit `789a4b8` (the ped-z fix) widened that branch, so *more* peds take it — that fix likely made
   per-frame ped cost worse. Relevant to the engine only if the same path is on the sim-side hot loop.
3. **Unconditional per-frame `GD.Print`** with string interpolation, ungated (`Main.cs:1808-1810`,
   `:2024-2035`, `:1686`, `:1907-1910`, `:1969-1971`).

## Log

- 2026-07-28 — Inventory complete. Confirmed **no coupled cars+peds perf instrument has ever existed**:
  all prior perf work is car-only on ped-free `_bench` scenarios; all ped perf work is car-free and
  outside the coupled host. So `PERF-HANDOVER.md`'s "bandwidth wall, everything tried" conclusion does
  not transfer to this workload. Owner scoped the effort to the engine (not rendering) with the target
  5 000 cars + 20 000 peds real-time, and granted full autonomy over profiling and method.
