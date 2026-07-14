# PANIC-EVAC-PHASE5-TIER2-TASKS.md — stages, tasks, success conditions

Task breakdown for Phase-5 Tier 2 (`PANIC-EVAC-PHASE5-TIER2-DESIGN.md`). Loop as usual: Sonnet implements
a batch, Opus reviews hard (reads the diff, confirms the tests assert the real thing, re-runs the gate)
before ticking. Every task names its design section, the files it touches, and **measurable success
conditions**. Standing gate for every batch: `dotnet test` green, `Sim.Bench` hash `909605E965BFFE59`
unmoved.

---

## STAGE T2-S1 — spatial-hash the two crowd solvers (the measured hotspots)

### T2.1 — `MixedTrafficCrowd` uniform-grid neighbour query (opt-in, bit-identical)
- **Design:** §2a. **Files:** `src/Sim.Core/Mixed/MixedTrafficCrowd.cs`.
- Add `public bool UseSpatialHash { get; set; } = false;` and a uniform grid mirroring
  `OrcaCrowd.RebuildGrid`/`GridCandidates`/`PackCell`: cell edge = `NeighbourDist`; rebuild once per
  `Step()` from frozen positions; `GridCandidates(i)` gathers the 3×3 block and **`Array.Sort`s ascending**
  before feeding the existing `Insert(...)` nearest-`k` routine. Brute path unchanged. Walls loop unchanged.
- **Success conditions:**
  1. **New test file `tests/Sim.ParityTests/MixedTrafficSpatialHashTests.cs`** mirroring
     `OrcaSpatialHashTests`: build two `MixedTrafficCrowd`s, set `UseSpatialHash=true` on one, step ≥ 60
     times, assert **exact equality of `Position(i)` AND `Heading(i)`** for every mover every step. At
     least three scenarios: (a) dense many-cell crowd; (b) `MaxNeighbours` cap actually binding (more
     in-range neighbours than the cap); (c) the non-holonomic steering path exercised (movers turning).
  2. A run-to-run reproducibility test (grid==grid twice) as in `OrcaSpatialHashTests`.
  3. Default (`UseSpatialHash=false`) leaves every existing test byte-identical; `dotnet test` green;
     hash `909605E965BFFE59` unmoved.

### T2.2 — enable `OrcaCrowd.UseSpatialHash` for the evac pedestrian crowd (opt-in)
- **Design:** §2b. **Files:** `src/Sim.Evac/EvacConfig.cs`, `src/Sim.Evac/EvacDirector.cs`.
- Add `EvacConfig.UsePedestrianSpatialHash` (default false). When set, `EvacDirector` sets
  `_peds.UseSpatialHash = true` (and, once T2.1 lands, `_mover`'s hash for pushers via a matching flag).
- **Success conditions:**
  1. A test builds the organic demo twice — flag off vs on — runs 300 ticks, and asserts the
     `ContainmentAndDeterminism`-style signature (panicked/converted/pusher/ped counts + every pedestrian
     final position, `"R"` round-trip) is **identical** (grid == brute at the demo level).
  2. Default off → all existing evac tests byte-identical; suite green; hash unmoved.

### T2.3 — heavy-load micro-benchmark for the two hashes
- **Design:** §2a/§2b, §5(1). **Files:** `src/Sim.EvacProfile/` (extend) or a small runner.
- A synthetic load: N ∈ {≈250, ≈1000, ≈2000} agents in a bounded region for each crowd, timed brute vs
  grid.
- **Success conditions:** a printed table (N, brute ms, grid ms, speedup) showing the grid is
  **materially faster at N ≥ ~1000** for both `MixedTrafficCrowd` and `OrcaCrowd` (expect the ~6× at
  n ≥ 400 that `OrcaCrowd`'s Q3 already documents; `MixedTrafficCrowd` similar order). Report actual
  numbers. (This measures the *reason* for the work; it is not a determinism gate.)

---

## STAGE T2-S2 — the 10k-vehicle evac demo + payload/scan handling

### T2.4 — 10k host scenario (spike-gated choice)
- **Design:** §3a, §5(2). **Files:** `scripts/` and/or `scenarios/_bench/…` (+ `provenance.txt`).
- **Timeboxed spike first:** attempt a 2-lane `--rand` net at ~10k concurrent; if it generates and runs
  under `Sim.BenchCity` without the strategic-lane-change/insertion failure, adopt it (option B);
  otherwise fall back to the committed `city-15000` (option A). **Log the decision + why.**
- **Success conditions:** a committed (or already-committed, if A) 10k-class scenario that loads offline
  and reaches ≥ ~8k peak concurrent under `Sim.BenchCity`, with `provenance.txt` recording the exact
  generation commands. No SUMO at test time.

### T2.5 — `EvacCityScenario` (new demo builder)
- **Design:** §3b. **Files:** `src/Sim.Evac/EvacCityScenario.cs` (new).
- Auto-track director on the T2.4 net; a large central incident sized (against a measured tracked-count)
  to trap a **low-thousands** working-region population; boundary `ExitEdges`; the two spatial-hash flags
  ON. Deterministic.
- **Success conditions:** a test confirms it loads, ticks deterministically (two runs bit-identical), the
  cascade emerges (panicked/pushers/pedestrians all > 0), and **the tracked working-region population is
  in the hundreds→low-thousands** (prove the crowd solvers are actually stressed), while a far vehicle is
  still never tracked (locality holds at 10k).

### T2.6 — viz payload management
- **Design:** §3c. **Files:** `src/Sim.Viz/SceneGen.cs` (+ `Program.cs` mode).
- A `BuildEvacCity` scene with a bounded payload: region-crop and/or frame-decimation and/or per-frame
  vehicle cap. **Every drop is logged** (a console line: N vehicles cropped / frames decimated by k / M
  per-frame omissions).
- **Success conditions:** the standalone HTML for the 10k demo is emitted with a payload under an explicit
  stated budget (e.g. ≤ ~15 MB), the console prints exactly what was dropped, and Opus renders it to
  confirm the congestion + local exodus still read on the 10k mesh. No silent truncation.

### T2.7 — before/after 10k cost profile + auto-track measurement (closing deliverable)
- **Design:** §3d, §5(3). **Files:** `src/Sim.EvacProfile/` (city mode).
- Profile `EvacCityScenario` with the crowd hashes **off then on**, and separately report the auto-track
  scan cost at 10k.
- **Success conditions:** a reported before/after showing the pusher + pedestrian phases materially
  reduced with the hashes on (the two ¾-of-cost hotspots), total generation time stated, and an explicit
  verdict on whether the auto-track scan needs its own optimization (with the number that justifies the
  verdict). If it does, that becomes T2.8; if not, say so and stop — no speculative optimization.

---

## Out of scope / deferred (measurement-gated, per design §2 note & §5)
- Spatial `OrcaCrowd.QueryNear` / `VehicleMover.QueryNear` / spatial `FeedVehicleDiscsToPeds` — only if
  T2.7 shows disc feeds material at 10k (Tier-1 measured < 0.5 %).
- FearField uniform grid — only if fear update becomes material (Tier-1 measured ~1 %).
- Any parity-core change — non-goal (design §7 / Phase-5 §7).

---

### Proposed batches
- **Tier2-B1:** T2-S1 (T2.1 + T2.2 + T2.3) — the core algorithmic win, self-contained, no scenario work.
- **Tier2-B2:** T2-S2 (T2.4 spike+scenario, T2.5 demo, T2.6 viz, T2.7 measurement) — the 10k demo and its
  closing before/after profile.
