# LIVE-CITY-PERF-SESSION-LOG.md — append-only log of every optimization attempt

> **THIS FILE IS THE RECOVERY ANCHOR.** If context was auto-compacted, read this file top to bottom
> before doing anything else, then continue from the last entry. Append-only: never rewrite or delete a
> past entry, including failed ones — a reverted attempt is the most valuable kind of record, because
> it is the only thing that stops the next session repeating it.

## RECOVERY HEADER (read first after a compaction)

- **Goal (owner-stated):** 5 000 vehicles + 20 000 pedestrians running **smoothly in the SumoSharp
  ENGINE**. Rendering/Godot is explicitly **out of scope**. "Smoothly" = RTF ≥ 1.0 **and** p99 step
  time ≤ step budget (`Dt`).
- **Mode:** overnight, autonomous. Owner grants full autonomy over profiling, choice of optimization
  method, and hunting alternative approaches. Sonnet subagents execute; the orchestrator (Opus) decides
  and verifies. Do **not** stop to ask for approval.
- **Branch:** `claude/handoff-docs-implementation-pmdu9z`.
- **Docs of record:** `LIVE-CITY-PERF-DESIGN.md` (target, dimensions, gates, candidate levers) and
  `LIVE-CITY-PERF-TRACKER.md` (task list + status). Read both. Also `CLAUDE.md`
  §Measurement-discipline and `PERF-HANDOVER.md` §Experiments-log (the do-not-repeat list).
- **The instrument:** `src/Sim.BenchLiveCity` (new, in `Traffic.sln`) + default-off
  `LiveCitySim.ProfilePhases`. Both are committed and permanent (`CLAUDE.md` rule 8 — never revert an
  instrument, or its numbers become unfalsifiable and poison later comparisons).
- **Gates for every engine change (all four):** `dotnet test -c Release` (Traffic.sln) · `Sim.Bench`
  hash `909605E965BFFE59` with `hashA == hashPar` · `dotnet test -c Release
  tests/Sim.LiveCity.Tests` **and** `tests/Sim.Pedestrians.Tests` (NOT in `Traffic.sln` — build
  explicitly or you measure stale code, `CLAUDE.md` trap #9) · `city-3000` 0 stuck + aggregate PASS.
- **Measurement rules that have already bitten this project:** ~8% thermal noise ⇒ interleaved paired
  A/B of two snapshotted builds, alternating runs, medians + paired win counts; **never build while
  measuring**; anything under ~5% is not a result; label every number with its demand model and its
  achieved (not requested) counts; print every `LIVECITY_*`/`SUMOSHARP_*` gate value (process-global).
- **Verdict vocabulary:** **WIN** = shipped/committed · **NULL** = correct but no measurable gain,
  reverted · **REGRESS** = slower, reverted. Every attempt gets one.

---

## GOALS (what "done" means — read before choosing any work)

**Primary goal.** `LiveCitySim` — the coupled cars+pedestrians host — must sustain **5 000 vehicles +
20 000 pedestrians in real time**, in the engine, headless.

**"Smoothly" is defined falsifiably** as, at the configured sim rate (step budget = `Dt` seconds):

    RTF >= 1.0   AND   p99 step time <= step budget

The p99 clause is load-bearing, not decoration. A config whose *mean* fits the budget while its p99 runs
several× over is not smooth — it stutters — and a mean-only report hides precisely that. This is why the
instrument records **every** step's duration rather than a running average.

**Secondary goals**, in priority order: (1) never regress behaviour — a change is byte-identical or it
is opt-in and off by default (`CLAUDE.md` rule 3); (2) leave the instruments committed and permanent so
tomorrow's numbers are comparable to tonight's (rule 8); (3) leave every attempt recorded here,
including the failures, so no future session re-runs a dead end.

**Explicit NON-goals.** Rendering / Godot / frame rate — the owner scoped this to the engine
(viewer-side findings are parked in `LIVE-CITY-PERF-TRACKER.md`'s appendix and must not be worked).
SUMO parity of the *pedestrian* model is not at stake (peds are not a SUMO port); vehicle parity very
much is. Do not chase the car hot path — it is already at ~3.57× SUMO with a documented list of
exhausted levers.

## THE HARNESS — what to run, the exact command, and WHY

Everything below is run from the repo root (`git rev-parse --show-toplevel`). Windows box; both
PowerShell and Git Bash work. **Always `-c Release`** — Debug numbers are meaningless and Debug/Release
mixups have already cost this project a wrong conclusion.

### The instrument for THIS effort (primary)

    dotnet run -c Release --project src/Sim.BenchLiveCity -- --cars 5000 --peds 20000 \
        --steps 400 --warmup 40 --hi-res-radius 0 --repeats 3 --profile

**Why:** it is the only thing that measures the coupled host. It reports achieved (never merely
requested) car/ped counts, the achieved high-power/low-power ped split, RTF, the step budget, an
explicit `REALTIME: yes/no`, the per-step distribution (mean/p50/p95/p99/max + count over 3×p50),
`GC.GetTotalPauseDuration()`, alloc/step, gen0/1/2, peak RSS, and every observed env gate.
Sweep form: `--sweep "0:0,1000:0,5000:0,0:5000,0:20000,1000:5000,5000:20000" --csv <path>`.
⚠ **Flag names are as-specified to the implementor and must be confirmed against
`src/Sim.BenchLiveCity/Program.cs` — run `--help` or read the arg parser before trusting this line.**

**Why the sweep shape matters:** `0:0`, `cars:0`, `0:peds`, `cars:peds` is a 2×2 that tests whether the
coupled cost is *superadditive*. If `cars:peds` exceeds (`cars:0` + `0:peds` − `0:0`) materially, there
is a real car↔ped interaction cost and that becomes the target. No isolated bench in this repo could
ever reveal that, which is why the existing car-only and ped-only benches did not.

**Why `--hi-res-radius` is not optional:** ped cost is driven by how many peds are inside the
high-realism ORCA pocket, not by the population. 20 000 mostly-dead-reckoned peds and 20 000 all-ORCA
peds differ by orders of magnitude. A ped count reported without its high/low split is uninterpretable.

### The four gates — run ALL of them after every engine change

    dotnet test -c Release                                     # (1) Traffic.sln suites
    dotnet run  -c Release --project src/Sim.Bench             # (2) determinism hash
    dotnet test -c Release tests/Sim.LiveCity.Tests            # (3a) NOT in Traffic.sln
    dotnet test -c Release tests/Sim.Pedestrians.Tests         # (3b)
    dotnet run -c Release --project src/Sim.BenchCity -- scenarios/_bench/city-3000 --no-fcd \
        --sumo-summary  scenarios/_bench/city-3000/summary.xml \
        --sumo-tripinfo scenarios/_bench/city-3000/tripinfo.xml \
        --aggregate-tolerance scenarios/_bench/city-3000/aggregate-tolerance.json   # (4)

- **(1) Why:** the committed goldens are the parity contract. Compare against the *recorded baseline*
  count, not against "looks green" — note there are known pre-existing failures elsewhere (2
  `ReconstructorS2` in CityLib), so "some red" is not automatically your fault, and equally not
  automatically fine.
- **(2) Why:** expect hash **`909605E965BFFE59`** and **`hashA == hashPar`**. This is the cheap proof
  that a change is byte-identical *and* that thread count did not leak into results. If the hash moves
  and you meant the change to be byte-identical: revert, do not rationalise.
- **(3) Why:** `src/Sim.LiveCity` and `tests/Sim.LiveCity.Tests` are **outside `Traffic.sln`**
  (`CLAUDE.md` trap #9), so `dotnet build`/`dotnet test` on the solution silently skips them — this has
  produced contradictory numbers for the same configuration more than once. Run them explicitly or you
  are measuring stale code. Expect ~80 passing in LiveCity, 317 in Pedestrians.
- **(4) Why:** the `Sim.Bench` hash uses a **junction-free highway**, so it does **not** exercise
  junction/foe/keep-clear code at all. city-3000 is the junction gate: 0 stuck + aggregate PASS. For a
  ped-only change this is regression insurance; for anything touching shared structures it is essential.
- **For a parallelism change specifically:** byte-identity must be shown on a junction scenario via a
  serial-vs-parallel **tripinfo SHA match** (`--serial` vs default), not just the highway hash. For ORCA
  parallelism, `OrcaParallelStepTests` / `OrcaRegionDecompositionTests` already assert bit-identity —
  run them and name them in the log entry.

### Supporting instruments (diagnosis, not gates)

    dotnet run -c Release --project src/Sim.BenchCity -- scenarios/_bench/city-3000 --no-fcd --profile
    pwsh scripts/bench-scaling.ps1 -Threads 1,2,4,8,16 -Repeats 5
    dotnet run -c Release --project src/Sim.BenchCrowd  -- --sizes 1000,10000 --steps 20 --warmup 3
    dotnet run -c Release --project src/Sim.BenchPedLod -- --high-fraction 0.1 --steps 30 --warmup 8
    dotnet run --project src/Sim.DensityDiff -- --cars 480 --steps 200 --out <scratch>/demand.rou.xml

**Why each:** `BenchCity --profile` gives the car-side per-phase split (car-only, no peds).
`bench-scaling.ps1` sweeps thread counts (note: 8 threads is the measured sweet spot; 16/24 are *slower*
from HT oversubscription). `BenchCrowd` isolates raw `OrcaCrowd.Step` — use it to bound what ORCA can
ever achieve before optimizing it inside the host. `BenchPedLod` isolates `PedLodManager` (LOD + ORCA)
and is the source of the 20 000-ped 27.9→8.25 ms/step parallel figures — but it uses trivial
straight-line paths with weave OFF, so it *understates* production per-ped cost. `Sim.DensityDiff` is
**behavioural only** (discharge/gridlock/capacity) and prints no timings — use it to check a change did
not alter traffic behaviour, never to measure speed.

**Relevant env gates** (process-global — see protocol below): `LIVECITY_CARS`, `LIVECITY_PEDS`,
`LIVECITY_HZ` (sets `Dt`, hence the step budget), plus the ~30 behavioural `LIVECITY_*` gates.

### MEASUREMENT PROTOCOL — violating any of these invalidates the number

1. **Interleaved paired A/B of two snapshotted builds.** Build both variants, copy each
   `bin/Release/net8.0` aside, then **alternate** runs (old, new, old, new…), count paired wins and
   compare medians. Run-to-run noise here is ~8% from thermal drift; single-config medians taken minutes
   apart are confounded and **will lie**. Under ~5% is not a result.
2. **Never build while measuring**, and run nothing else on the box. A background compile silently
   invalidates the reading.
3. **Set every `LIVECITY_*` / `SUMOSHARP_*` gate explicitly in BOTH arms.** They are process-global; an
   inherited shell value is indistinguishable from a measured one. The instrument prints them for this
   reason — if that line is missing from a result, the result is uninterpretable.
4. **Label every number with its demand model and its ACHIEVED counts.** `LiveCitySim` is closed-loop
   (it inserts only while `live < CarTargetConcurrent`), so a config that never filled its cap would
   otherwise be reported as if it had. Any *capacity* claim from closed-loop demand is invalid outright.
5. **Both surfaces must accept a change.** Goldens are tiny (2–5 vehicles) and cannot contain a
   saturated junction; the benches saturate but have no SUMO reference. "Goldens green" is not
   sufficient and "bench faster" is not sufficient — run both, always.
6. **Warmup is excluded** from every statistic (JIT + first-touch). The first run of anything is a
   warm-up run, full stop.
7. **Commit the instrument, not just the conclusion.** A probe run once and reverted makes its own
   number unfalsifiable and poisons every later comparison.

### ORDER OF OPERATIONS for one attempt (the loop)

1. Append an entry here with the hypothesis, its precondition, and the **BEFORE** number (measured, not
   recalled). No before-number ⇒ no attempt.
2. Implement (delegate volume to a Sonnet subagent; keep the accept/reject decision).
3. Run the four gates. If byte-identity was expected and the hash moved — revert, and log it as a
   finding rather than arguing with it.
4. Measure AFTER, interleaved paired, same config, same gates set.
5. Append the AFTER number and a verdict of **WIN** / **NULL** / **REGRESS**. Revert NULL and REGRESS
   immediately — but never delete their log entry; that entry is the whole value.
6. Commit WINs small and individually, with the numbers in the commit message.

## Entry format (use this for every attempt)

```
### A<n> · <short title> — <WIN|NULL|REGRESS|IN PROGRESS>
- When:
- Hypothesis / why now:
- Precondition (from tracker) and whether it was actually confirmed:
- BEFORE (measured, with config + achieved counts):
- Change (files, approach):
- AFTER (measured, same config, interleaved paired):
- Gates:
- Verdict + commit SHA (or reason reverted):
- Lesson:
```

---

## A0 · Scoping, inventory, and docs — DONE (no perf change)

- **When:** 2026-07-28 00:30–01:15 CEDT.
- **What:** established the target, inventoried existing instruments, audited the ped/render paths,
  wrote `LIVE-CITY-PERF-DESIGN.md` + `LIVE-CITY-PERF-TRACKER.md`.
- **Findings that shape everything after:**
  1. **No coupled cars+peds perf instrument has ever existed.** All prior perf work
     (`PERF-ROADMAP.md`, `PERF-HANDOVER.md`, `SPATIAL-OPT.md`) is **car-only on ped-free**
     `scenarios/_bench/*`; all ped perf work (`Sim.BenchCrowd`, `Sim.BenchPedLod`,
     `PEDESTRIAN-P6-*`) is **car-free and outside the coupled host**. `Sim.DensityDiff` drives
     `LiveCitySim` headless but prints **zero timings**.
  2. **Therefore `PERF-HANDOVER.md`'s "memory-bandwidth wall, everything already tried" conclusion
     does NOT transfer to this workload.** It was measured on a different system. Treat the coupled
     host as unprofiled. The do-not-repeat list binds for **cars**, not automatically for **peds**.
  3. **A ped count alone is uninterpretable.** Cost is driven by the high-power (ORCA, inside the
     high-realism pocket) vs low-power (dead-reckoned) split, not by population. Knobs:
     `LiveCitySim.SetLcRealismZone(x,y,r)` (`LiveCitySim.cs:740`; promote=r, demote=1.3r; static
     default 70/100 m at `:326`); observable via `PedLodManager.HighPowerCount`
     (`PedLodManager.cs:438`).
  4. `src/Sim.LiveCity` and `tests/Sim.LiveCity.Tests` are **outside `Traffic.sln`** (trap #9).
- **Verdict:** N/A (no perf change). Docs written, not yet committed at time of writing.

## A1 · Build the instrument (`Sim.BenchLiveCity` + `LiveCitySim.ProfilePhases`) — IN PROGRESS

- **When:** started 2026-07-28 00:50 CEDT.
- **Why:** cannot optimize what is not measured, and `CLAUDE.md` rule 2 records a 5-for-5 failure rate
  for interventions reasoned from source without a trace first. Rule 8 requires the instrument be
  committed, not a throwaway probe.
- **Scope:** headless bench with `--cars/--peds/--steps/--warmup/--sweep/--repeats/--csv/
  --hi-res-radius/--hi-res-centre`; reports achieved car/ped counts, achieved high/low-power split,
  RTF, step budget, explicit `REALTIME: yes/no` (mean **and** p99 ≤ budget), per-step
  mean/p50/p95/p99/max + count over 3×p50, `GC.GetTotalPauseDuration()`, alloc/step, gen0/1/2, peak
  RSS, and every observed env gate. Plus default-off `LiveCitySim.ProfilePhases` mirroring
  `Engine.ProfilePhases`, with an explicit "unaccounted" remainder line so missing instrumentation is
  visible rather than hidden.
- **BEFORE:** no number exists. That is the point.
- **Status:** delegated; awaiting the first reading (M0–M3 in the tracker).
- **Next after it lands:** the 2×2 (`0:0`, `cars:0`, `0:peds`, `cars:peds`) to test whether the coupled
  cost is **superadditive** — if it is, an unindexed interaction scan is the target (the
  `PERF-ROADMAP.md` L2 shape, where indexing two O(N²) scans gave ~44×).

## A2 · Ped hot-path structural map — IN PROGRESS (read-only, no builds)

- **When:** started 2026-07-28 01:05 CEDT.
- **Why:** prepares the optimization decision so it can be made the instant profiling data arrives,
  without burning measurement wall-clock on reading. Deliberately read-only: a concurrent build would
  corrupt A1's timings.
- **Questions:** serial vs parallel per ped phase; which phases are O(all peds) vs O(high-power);
  what low-power peds actually cost per step (they are dead-reckoned, so possibly little); ORCA's
  neighbour structure and **exactly which neighbour fields the inner loop reads** (decides SoA); per-
  ped-per-step allocation incl. the publish/event path; LOD churn; determinism constraints on
  parallelizing; and any **already-measured** ORCA/ped-LOD numbers from the existing ped benches.

### A2 RESULTS (2026-07-28 01:20) — the ped path is mapped, and it is FULL of unclaimed wins

All read from source; `file:line` given so each is independently checkable. Ranked by
(confidence × ROI × safety). **These reorder the whole plan — the top item is a config change, not a
refactor.**

1. **`LiveCitySim` NEVER ENABLES THE ORCA PARALLEL STEP.** `OrcaCrowd` already has a *tested,
   bit-identical* parallel plan (`UseParallelStep` / `UseParallelHighCrowd` /
   `UseRegionDecomposition`), and a grep shows they are set **only** by `src/Sim.BenchPedLod`
   (`Program.cs:197,200`) — **never** by `LiveCitySim` or any other production caller. Production
   therefore always takes the serial branch (`OrcaCrowd.cs:514-525,550-560`) while
   `PedLodManager.cs:209` sets only `UseSpatialHash = true`.
   Bit-identity is by construction and already covered by tests: `OrcaCrowd.cs:8-11` — *"every agent's
   new velocity is computed from the FROZEN start-of-step state of all agents, then positions/velocities
   are all committed together. That makes a step order-independent and trivially parallelisable"* —
   proven by `OrcaParallelStepTests` / `OrcaRegionDecompositionTests`.
   **Already-measured payoff on THIS box** (`docs/PEDESTRIAN-P6-1-RESULTS.md:75-82`, 24-core, 20 000
   peds): stable **27.9 → 8.25 ms/step (3.4×)**; churn **55.1 → 13.9 ms/step (4.0×)**.
   ⇒ **New A3. Highest-confidence, lowest-risk lever in the whole effort.**
2. **`PedPublisher._events` IS NEVER CLEARED — unbounded, permanently-rooted growth.**
   `PedPublisher.cs:50` `private readonly List<PedEvent> _events = new();`; every `Publish*` appends
   (`:75,81,88,94,108`); **no `Clear`/truncate exists anywhere in `src/`**. `LiveCitySim.cs:952,1065`
   only reads a *cursor* (`beforeCount`) into it. So one heap record per sample/switch/heartbeat, for
   every ped that ever lived, is retained **for the whole process lifetime** — plus a per-step
   `new List<PedEvent>` at `:1065`. `FreeKinematicSample` is a `record` (reference type) allocated per
   high-power ped per step (`PedPublisher.cs:92-96`).
   This is gen2 growth, i.e. **exactly the mechanism behind the owner's "GC spikes" report**, and it is
   invisible to every existing bench (`BenchPedLod` times only 30 steps). ⇒ **New A6.**
3. **Low-power pose is recomputed 3–4× per ped per step, each time doing up to THREE linear polyline
   walks.** `PoseAt`/`PositionOf` is called at `PedLodManager.cs:645` (frozenPos),
   `PedDemand.cs:827` (despawn), `LiveCitySim.cs:968` (gather), and `LiveCitySim.cs:1246` (`Sample`).
   Each call, under weave (`PedEnableWeave` defaults **true**, `LiveCityConfig.cs:263`), runs
   `SampleAt` + `PathLength` + `WeaveAxisAt` = 3 O(leg-vertices) scans (`ActivityTimeline.cs:301,306,327`).
   **`PathLength(w.Path)` at `:306` recomputes a total length the `WalkSegment` constructor already
   computed** (`ActivityTimeline.cs:73-74`) — pure waste on every single call.
   `PathArcMotion.Walk/SampleAt` also linear-scan from vertex 0 every call (`PathArcMotion.cs:292-330`),
   so the class-header claim of *"O(1)"* (`PedLodManager.cs:10-11`) is **false in implementation**.
   `PoseAt` is a pure function of `(timeline, now)`, so memoizing it per step changes nothing
   observable ⇒ **byte-identical**. ⇒ **New A4.**
4. **Five to seven separate O(all-20 000-peds) passes per step, plus O(N) allocation per step.**
   `PedLodManager.Step` allocates `new List<int>(_peds.Keys)` + `ids.Sort()` +
   `new Dictionary<int,Vec2>(N)` **every step** (`:639-649`), then makes five `foreach (var id in ids)`
   passes (`:642-810`). Only ORCA is bounded by high-power count; everything else — the promote/demote
   decision loop (`:650-687`), both publish scans (`:794-810`), `PedDemand.DespawnArrivals`
   (`:817-848`), `LiveCitySim`'s gather (`:963-970`) — visits all N. Reusable buffers are
   byte-identical. ⇒ **New A5.**
5. **`MaxNeighbours` is uncapped in production** (`OrcaCrowd.cs:189-197` default 0 = unlimited;
   `PedLodManager` never sets it), so every agent within `NeighbourDist` = 15 m constrains the solve.
   Cost is O(local density) per agent, and `GridCandidates` additionally **`Array.Sort`s** each
   candidate list (`:1013`) purely to match brute-force order. A cap changes behaviour ⇒ opt-in only.
6. **ORCA's hot neighbour read is exactly 3 fields** — `_position[j]`, `_velocity[j]`, `_radius[j]`
   (`OrcaCrowd.cs:878,897`; `OrcaSolver.cs:92-96`); a neighbour's `_goal`/`_maxSpeed` are never read.
   They live in three separately-allocated arrays. So the right layout change is **packing those three
   hot fields together** (one cache line per neighbour), NOT per-field SoA — and note this *inverts* my
   earlier E1 framing: the data is already SoA-per-field; the win is interleaving the hot triple.
7. **Region decomposition already FAILED and must not be retried** (`PEDESTRIAN-P6-2-RESULTS.md:3-7`):
   best uplift 1.08× vs a ≥1.4× target, root-caused as *"a region's agents still live at scattered SoA
   indices, so their neighbour position/velocity reads still scatter across the whole array."*
8. Prior combined car+ped work (`PEDESTRIAN-COMBINED-LOAD-RESULTS.md`): peds need **≥6, ideally 8**
   cores or heavy-churn ped throughput falls under the real-time bar; shared-bus contention tax ~5–9%
   when neither engine is starved, ~20% for an engine squeezed to 4 cores.
9. Determinism constraints for parallelizing: per-ped RNG is **already** per-entity salted SplitMix64
   (`PedDemand.cs:20-25`), and `OrcaCrowd.SymmetryBreak` is a deterministic hash of (index, step), not
   RNG (`OrcaCrowd.cs:686-695`). The promote/demote **decision** phase is pure over frozen state
   (`PedLodManager.cs:633-636`) and parallel-safe; the **apply** phase mutates `_highCrowd`/
   `_highController` and must stay serial; the two publish passes must stay ordered and contiguous or
   the crowd-frame batching contract breaks (`PedLodManager.cs:784-793`).

**Caveat on the borrowed numbers:** the 27.9/8.25 ms figures come from `BenchPedLod --high-fraction 0.1`
(2 098 high of 20 000) with trivial 2–5-vertex straight paths and weave OFF. Production peds are
`ActivityTimeline` + weave with real routes, so per-ped cost is *higher* than that bench and the
high-power count depends on the pocket radius. These are order-of-magnitude guides, **not** predictions;
A1's own numbers supersede them.

### A1 RESULTS (2026-07-28 01:26) — instrument LANDED (3 commits); first ladder is INVALID for the target, and that is the finding

Commits: `e36d0da` (P0 `Sim.BenchLiveCity`), `814cdf7` (P1 `LiveCitySim.ProfilePhases` + `--profile`),
`b2542fc` (hi/lo-power split + REALTIME verdict). Bench is in `Traffic.sln`. **P0/P1 = DONE.**

Raw data: `<scratchpad>/ladder_r0.csv` (+ `.log`), 300 steps, 60 warmup, `dt=0.5`, 3 repeats.

**THE HEADLINE IS THAT THERE IS NO HEADLINE YET: the ladder never reached the target counts.**

| requested | cars achieved | peds achieved | hi-power | mean ms/step | p99 ms | alloc/step | GC pause %wall |
|---|---|---|---|---|---|---|---|
| 0:0 | 0 | 0 | 0 | 0.20 | 0.36 | 34 KB | 0% |
| 1000:0 | 1001 | 0 | 0 | 5.25–7.04 | 8.5–19.1 | **17.0 MB** | **12–21%** |
| 5000:0 | **1628** | 0 | 0 | 6.08–6.61 | 12.1–12.4 | **20.4 MB** | **15–17%** |
| 0:5000 | 0 | **1391** | 72 | 2.20–3.45 | 5.1–8.3 | 184 KB | 2.5–4.8% |
| 0:20000 | 0 | **1391** | 72 | 2.20–2.43 | 5.7–6.1 | 184 KB | 2.2–4.5% |
| 1000:5000 | 1008 | **1391** | 72 | 8.25–8.95 | 12.6–18.1 | 17.3 MB | 7.6–8.0% |
| 5000:20000 | **1601** | **1391** | 72 | 9.84 | 16.9 | **20.5 MB** | 8.2% |

**Findings, in order of importance:**

1. **THE TARGET WORKLOAD WAS NEVER RUN. Every "REALTIME: yes" above is worthless.** Peds pin at
   **exactly 1391** whether 5 000 or 20 000 are requested; cars reach only **1628 of 5 000**. This is
   why the design mandates achieved-not-requested reporting — without it this ladder would have been
   filed as "5 000 cars + 20 000 peds runs real-time with 50× headroom", which is **false**.
2. **Root cause is FILL RATE, not capacity — and probably also net size.** 300 steps at `dt=0.5` is only
   150 simulated seconds; at the default `PedSpawnRatePerSecond` ≈ 8/s that is ~1 200 peds, matching the
   observed 1391. So the run ends long before the population reaches its cap. Separately, the default
   `LiveCityConfig` crop is an **840 m × 840 m** box (2055..2895) — 20 000 peds there is ~28 peds per m²
   and 5 000 cars is instant gridlock, so **the demo scenario physically cannot host the target**
   regardless of fill time. ⇒ needs (a) a prefill/much longer warmup **and** (b) a big net. The
   **11 km central-Geneva cut already on disk** (`<scratchpad>/geneva_city.net.xml`) is the candidate.
3. **Real-time is NOT currently the binding constraint — headroom is ~50×.** At `dt=0.5` the step budget
   is **500 ms** and the worst observed mean is 9.8 ms (RTF ≈ 51×). So the interesting question is not
   "does it fit 500 ms" but **"what counts can it reach before it stops fitting"** — the ladder must be
   pushed until REALTIME flips to `no`, and/or run at a realistic `dt`. A 2 Hz sim is a very soft target;
   the owner's felt problem is likely at a higher rate and/or far higher counts.
4. **CARS, not peds, dominate at these counts — and the car side allocates catastrophically.**
   1 628 cars = 6.4 ms/step and **20.4 MB per step** (~12.5 KB per car per step), with **15–17% of wall
   in GC pause**. Peds (1 391, only 72 high-power) cost 2.2 ms/step and **184 KB per step** — two
   ORDERS OF MAGNITUDE less allocation. **This is the GC-spike mechanism the owner reported, and it is
   on the CAR path in the live-city host**, not the ped path. Note `PERF-ROADMAP.md` L0c/L0d cut
   allocation hard on the `Sim.BenchCity` path; those wins evidently do **not** cover whatever
   `LiveCitySim` does per step. **This is now the top suspect.**
5. **Only 72 of 1 391 peds are high-power** at the static 70 m pocket — 5%. So ORCA is a small slice
   *here*, and A3 (enable parallel ORCA) will show little at this scale. A3's value depends entirely on
   the high-power population, which depends on the pocket radius — the `--hi-res-radius 300` arm was
   never run. **A3 is therefore NOT yet justified; do not ship it on the borrowed BenchPedLod number.**
6. Mild superadditivity: coupled 9.84 vs additive 6.4 + 2.2 − 0.2 = 8.4 (≈ +17%) — inside the noise band
   given the differing achieved counts, so **not** yet evidence of a real interaction term.
7. gen2 collections are non-zero and climbing with run length (0 → 1 → 2 → 3 → 4 across configs),
   consistent with A2's finding #2 (the never-cleared `PedPublisher._events`) plus the car-side churn.

**Process lesson (cost: one delegated agent's whole budget):** the implementor launched the ladder as a
background run and ended its turn, losing the report — exactly `CLAUDE.md`'s "delegate BUILDING an
instrument, never delegate WAITING for one". The CSV survived on disk and was recovered. **Future
delegations end at "compiles, verified, committed"; the orchestrator runs every measurement.**

### A9a RESULTS (2026-07-28 01:55) — car-side allocation attribution by SOURCE READING: ~350 KB of 20.4 MB explained. Residual is the story.

Read-only trace of `LiveCitySim.Step()` → `Engine.Step()` → `Advance`/`AdvanceOneStep` and all ~16
constraints, plus the publish path and the bench driver.

**Refuted (good — these are ruled out, don't revisit):**
- **Trajectory/FCD export is NOT active.** `LiveCitySim.cs:1019` calls parameterless `_engine.Step()` →
  `Advance(null, steps)` (`Engine.cs:2122-2128`), so `if (trajectory is not null) EmitTrajectory(...)`
  (`:3085-3090`) never runs. No `TrajectorySet` is built by this host.
- **`Sample()`/`SampleCars()` are never called by the measured loop** (`Sim.BenchLiveCity/Program.cs:362-381`
  calls only `Step()` + cheap property reads). So the documented "dominant GC pressure" ped-list
  allocation is NOT in play here.
- **Diagnostics are off by default** (`LiveCitySim.cs:460-461`).
- The per-car plan/execute path is genuinely already clean: `MoveIntent`/`StopTransition`/
  `JunctionLeaderCandidate` are `readonly record struct`, buckets reused, `stackalloc` spans, LINQ and
  closures explicitly removed by the L0 work.

**Accounted for (READ, ~300–350 KB/step ≈ 1.5–1.7% of the measured 20.4 MB):**
| site | scope | est/step @1628 cars |
|---|---|---|
| `SimulationSnapshot.Capture(_engine)` (`SimulationSnapshot.cs:56-93`, called `LiveCitySim.cs:1042`) | per step, ~24 fresh arrays sized to car count | ~200–210 KB |
| `InMemoryReplicationBus…PublishFrame` → `movers.ToArray()` (`InMemoryReplication.cs:111-112`) | per step, fresh `VehicleRecord[]` | ~100–130 KB |
| `Engine.SpawnVehicle` `new Route` + `List<string>` + `VehicleDef` + 2 interpolated strings (`Engine.cs:2694-2716`) | per spawn (5/step) | ~1–2 KB |
| `ResolveRightBeforeLeftCycles` nested dictionaries + per-link `Stack`/`HashSet` (`Engine.cs:6051-6231`) | per step, scales with busy priority junctions | tens of KB |

**STRUCTURAL BUG FOUND — `Engine._bestLanesCache` is silently defeated by this host.**
The cache (`Engine.cs:65-72`) is a `ConcurrentDictionary<(RouteId, EdgeId), …>` built by PERF-ROADMAP
L0b on the stated assumption that each `(routeId, edgeId)` is computed **once and shared for the whole
run** — true when many `<vehicle>`s reference one `<route id="r0">`. But `Engine.SpawnVehicle` — the API
`LiveCitySim.Step()` uses for every car (`LiveCitySim.cs:916`) — mints a **unique RouteId per spawned
vehicle** (`Engine.cs:2694`: `var routeId = $"__route{_runtimeRouteCounter++}"`). So the memo can never
be shared across vehicles in the live-city host; only the same vehicle re-querying the same edge hits.
Its two consumers are per-car-per-step and ON by default here (`CooperativeLaneChange` default true,
`LiveCityConfig.cs:199`): `TryBestLanesForEdge` (`Engine.cs:12193, 12635-12650`) and
`KeepRightStrategicStay`/`BestLanesCached` (`:12421-12428, 12706-12739`). A miss runs
`NetworkModel.ComputeBestLanes` (`NetworkModel.cs:715-748`) — a full backward pass whose
`BuildTerminalLaneQ` (`:768-793`) does `OrderBy(...).ToList()`/`.Select(...).ToList()` plus one
heap `LaneContinuation` **record** per lane per edge. `Engine.cs:65-67` states the un-cached cost
outright: *"Without this it re-allocated a `List<LaneContinuation>` (records) per lane-considering
vehicle per step."* The host is effectively back in that state. The dictionary is also **never pruned on
despawn** (only `Clear()`d at `LoadScenario`, `:1644`) ⇒ unbounded growth for the run.
Fixing the **cache key** (key on the edge-sequence content / `(fromEdge,toEdge)` rather than the
synthetic per-vehicle RouteId) should be **byte-identical by the cache's own stated invariant** —
`ComputeBestLanes` is a pure function of its edges input, so sharing the memo across vehicles traversing
the same edges cannot change any result, only whether it is recomputed. ⇒ **New A10.**

**THE RESIDUAL IS THE MAIN FINDING: ~95%+ of the 20.4 MB/step is UNEXPLAINED by source reading.**
The agent explicitly refused to stretch the identified sites to fit — correct behaviour, and per
`CLAUDE.md` rule 2 (a mechanism reasoned from source has a bad track record here; trace instead).
**Source reading has now hit its limit. The next step is a real allocation measurement, not more reading.**

**Measurement plan (orchestrator runs it — cheap, zero code change, uses existing gates):**
1. `--profile` on a car-only config for the per-phase time split (points at the region, even though the
   flag reports time not bytes).
2. **`LIVECITY_COOP=0` vs default A/B**, comparing `alloc_bytes_per_step`. `LIVECITY_COOP` gates
   `CooperativeLaneChange` → `CoordinatedLaneChange` + `CooperativeInformFollower`, which is exactly
   what enables BOTH `_bestLanesCache` consumers. A large alloc drop implicates the best-lanes path and
   sizes A10 before a line is written; a small drop kills that hypothesis and sends me elsewhere.
   Both arms must set every `LIVECITY_*` gate explicitly (rule 10).
3. If both are inconclusive: `dotnet-trace`/PerfView allocation profile, or bisect by phase with
   `GC.GetAllocatedBytesForCurrentThread` deltas.

### A10 · `_bestLanesCache` re-key — VERIFIED FIRST-HAND, designed, NOT yet sized (do not implement before A11)

Orchestrator re-checked all four load-bearing claims directly (not taking the implementor's word):
1. `Engine.cs:2694` — `var routeId = $"__route{_runtimeRouteCounter++}";` ⇒ **unique per spawn**. ✓
2. `Engine.cs:72` — key is `(string RouteId, string EdgeId)`, and the cache's own comment claims each key
   is *"computed ONCE and shared for the whole run"* ⇒ **the stated invariant is violated here**. ✓
3. `LiveCityConfig.cs:199` `CooperativeLaneChange = true` by default, wired at `LiveCitySim.cs:458-459`
   to `CoordinatedLaneChange` + `CooperativeInformFollower` ⇒ **both consumers are LIVE in this host.**
   (This is `CLAUDE.md` rule 3 — prove a live consumer before acting. Satisfied.) ✓
4. `NetworkModel.cs:715` — the signature is
   `ComputeBestLanes(IReadOnlyList<string> routeEdges, string currentEdgeId, (string,double)? stopOverride)`.
   It takes the **edge list**, never a Route object, and never reads any route id; the body only scans
   `routeEdges` and walks `BuildTerminalLaneQ`/`BackwardPassEdge`. ⇒ **the result is a pure function of
   (edge-sequence CONTENT, currentEdgeId, stopOverride)**, so re-keying the memo on that content is
   semantically identical ⇒ **byte-identical is achievable, by the function's own signature.** ✓

**Design (for the implementor):** intern each distinct edge sequence to a dense `int` shapeId **at spawn**
(`SpawnVehicle`, ~5/step — off the hot path), store it alongside the route, and key the memo
`(int shapeId, int edgeHandle)`. Three wins in one: (a) the memo is shared across all vehicles with the
same route shape, as originally intended; (b) the hot path stops hashing **two strings per lookup on
every hit** — cf. `PERF-HANDOVER.md`, where replacing one per-vehicle-per-frame `LanesById[v.LaneId]`
string hash with a dense handle array cut serial emit 28%; (c) the dictionary becomes **bounded** by
(distinct shapes × edges) instead of growing per-vehicle forever, which removes the unbounded-growth leak.

⚠ **Open correctness question for the implementor to check first:** the current key ignores
`stopOverride`, yet `ComputeBestLanes` takes it and it changes the result. If any live caller passes a
non-null `stopOverride` through the cached path, the existing cache is **returning wrong values** — a
correctness bug, not a perf one. Establish this before touching anything; if real, it is more important
than the perf work and must be reported separately.

**NOT approved for implementation yet.** Reason: A9a accounts for only ~350 KB of 20.4 MB/step, and this
fix targets part of the *unexplained* residual on a mechanism argument. `CLAUDE.md` rule 2 is explicit
that mechanism arguments reasoned from source have a bad track record in this repo (5-for-5 inert). **Size
it with A11 first.** If A11 shows the best-lanes path is a small slice, A10 is still worth doing for the
leak alone — but as a correctness/memory fix, not billed as a speedup.

### A11 (new, NEXT) · per-phase ALLOCATION accounting — the decisive instrument for the residual

`ProfilePhases` currently records **time** per phase. The open question is **bytes**. Extend the existing
default-off profiler to also accumulate allocated bytes per phase (process-wide
`GC.GetTotalAllocatedBytes` deltas around each phase — note `GetAllocatedBytesForCurrentThread` would
**undercount** any phase that runs `Parallel.For`, so it must not be used), and have `--profile` print
bytes and % alongside ms, with the same explicit "unaccounted" remainder line.

Why this and not more reading: it turns a 95%-unexplained residual into a ranked list in **one run**,
costs nothing when off, and is a permanent committed instrument (`CLAUDE.md` rule 8) rather than a
throwaway probe. It also sizes A10 as a side effect. **This is the gate on all remaining optimization work.**

### A8 RESULTS + A12 · FIRST VALID MEASUREMENT AT 20 000 PEDS (2026-07-28 02:20) — commit `002398b`

`002398b` added `--sumocfg`/`--dataset`, a real `--fill-steps` prefill with auto-scaled spawn rates, the
`FILL-FAILED` gate (`REALTIME` forced to `n/a`, `fill_ok=0`), and a dt-honest budget. Gates all green
(LiveCity 80/80, Pedestrians 317/317, ParityTests 775 + 4 pre-existing skips, 0 failed).

**TWO OF MY OWN INFERENCES WERE WRONG — corrected by measurement:**
- I reasoned the 840 m demo box "physically cannot host 20 000 peds (~28/m²)". **False.** With the fill
  fix the default net reaches **19 996 / 20 000 peds**. The 1391 ceiling was *purely* a fill-rate
  artifact. (Also: the net extent is **4758 m**, not 840 m — I had misread the crop as the net.)
- Geneva was my candidate big net. **It is unusable for peds:** cars fill 4999/5000, but concurrent peds
  plateau at **~40** regardless of fill time or spawn rate — that cut has essentially no pedestrian
  infrastructure. Cars saturate at **3084/5000** on the default net (genuine gridlock, not fill rate).
  ⇒ **A12 (new): no committed scenario can host 5 000 cars AND 20 000 peds simultaneously.** Needs a
  purpose-built net (netgenerate grid with `--sidewalks.guess --crossings.guess`, committed under
  `scenarios/_bench/` like `city-3000`), or a Geneva re-cut with sidewalks. Until then the target
  workload is measurable only in its two halves.

**MEASURED — ped-only, 19 996 peds (1 189 high-power end / 1 832 max, 18 807 low-power), static 70 m
pocket, dt=0.5, 150 steps, 30 warmup, prefill 39/500 steps:**

    mean 110.50 ms   p50 91.69   p95 185.18   p99 201.37   max 206.48   steps>3xp50: 0/150
    RTF 4.52x   REALTIME yes (budget 500 ms)   alloc 7.94 MB/step (1135 MiB total)
    GC gen0=57 gen1=20 gen2=2   GC PAUSE = 91.8 ms = 0.554% of wall   peak WS 274 MiB

    phase breakdown:
      pedDemandStep        10097.7 ms   60.9%
      carYieldMetric        3655.5 ms   22.1%   <-- WITH ZERO CARS
      pedLowPowerGather     2454.6 ms   14.8%
      crossingOccupancy       197.8 ms    1.2%
      publishPeds             150.0 ms    0.9%
      engineStep               15.6 ms    0.1%
      unaccounted               2.3 ms    0.0%

**Findings:**
1. **`carYieldMetric` burns 22.1% of wall with ZERO cars — pure waste, and it is a DIAGNOSTIC counter.**
   `CountYieldObservationsThisStep` (`LiveCitySim.cs:1094`) early-outs only on crossing occupancy, then
   `foreach (var p in _movingLowPowerPositions)` calls `_crossingOccupancy.QueryNear(p.X, p.Y, 0.01, …)`
   **per ped per step** (~18 000 spatial queries) *before* it ever consults the car array. With
   `carN == 0` the result is necessarily 0 (the counter only increments when a near car is found), so
   **`if (carN == 0) return 0;` is provably byte-identical** and reclaims the whole 22% in ped-heavy runs.
   Worse for the target: the structure is **O(on-crossing peds × cars)**, so at 5 000 cars it gets far
   more expensive, not less. Consumers are diagnostic prints only (`Sim.Host.App`, `Sim.Viewer`,
   `Sim.Viz`) but **two tests assert `CarYieldObservations > 0`** (`LiveCitySimTests.cs:75,380`), so the
   VALUE must be preserved exactly — cannot simply be deleted or gated off by default. ⇒ **B1.**
2. **`pedLowPowerGather` = 14.8%** — this is A4's target (redundant `PoseAt`/`PositionOf` re-evaluation),
   now confirmed by measurement rather than inferred from source.
3. **`pedDemandStep` = 60.9%** is the elephant but is a single opaque phase. It needs sub-phase
   decomposition (ids+sort, frozenPos, promote/demote decide, promote/demote apply, ORCA step, the two
   publish passes) before aiming at it. ⇒ **B2.**
4. **A3 (parallel ORCA) is now JUSTIFIED where it was not before:** 1 189–1 832 peds are high-power at
   the *static* 70 m pocket, not the 72 seen in the invalid ladder. ORCA is a real slice of the 61%.
   Still measure the split first (B2) before enabling it.
5. **GC is NOT the ped-side problem: pause is 0.554% of wall.** The owner's GC-spike hypothesis is
   **refuted for peds** and remains open only for the **car** path (15–17% pause there). Note ped alloc
   is still 7.9 MB/step — high, but the collector is absorbing it without pausing. This is exactly why
   the design mandated measuring `GetTotalPauseDuration()` directly instead of inferring pauses from
   allocation volume or collection counts.
6. **`engineStep` = 0.1% with 0 cars**, and its sub-phases are all ~0 — the profiler's decomposition is
   behaving sanely, and `unaccounted` is 0.0%, so the phase instrumentation is complete (no hidden work).
7. **The real-time verdict is soft and must not be quoted without its budget.** `yes` here is against a
   **500 ms** budget (dt=0.5, 2 Hz). At 10 Hz the budget is 100 ms and mean 110 ms would **FAIL**. So at
   20 000 peds we are already at roughly the 10 Hz boundary — the honest statement is "real-time at 2 Hz
   with 4.5× headroom; marginal at 10 Hz; and cars are not even in this measurement yet."

### A13 · `CompositeFootprintSource` scratch threshold — **WIN, SHIPPED `b1c9d7f`** (17.4× less allocation)

**⚠ CORRECTION TO THIS FILE'S HARNESS SECTION: the expected `Sim.Bench` hash is `BF3794A4704BCD79`, NOT
`909605E965BFFE59`.** The latter is inherited from `PERF-HANDOVER.md` and is STALE (the bench scenario
changed since). `BF3794A4704BCD79` with `hashA == hashPar` is the current baseline — measured twice this
session.

- **Hypothesis / why now:** B3's new per-phase ALLOCATION accounting showed `engine.plan` = 471.9 MiB and
  `engine.willPass` = 94.1 MiB at 500 cars — >96% of allocation — contradicting `PERF-ROADMAP.md`'s claim
  that the plan phase is allocation-free. So something **LiveCity-specific** allocated inside plan.
- **How it was localized — bisection, not reasoning.** Ran the bench across existing `LIVECITY_*` gates
  (all set explicitly in every arm, rule 10), comparing `alloc_bytes_per_step` at a fixed 507 cars:

      baseline(all on)  10,167,913     COOP=0  10,230,087     WRONGLANE=0     10,063,257
      YIELD=0              585,699     PEDYIELD=0 9,745,665    DRIVETHROUGH=0 10,168,136

  `LIVECITY_YIELD=0` alone cut allocation **17.4×**. A `--profile` diff of the two arms then pinned it to
  `engine.plan` 471.9 → 12.2 MiB and `engine.willPass` 94.1 → 5.2 MiB.
  **My source-reasoning was wrong twice on the way** (I chased `CompositeFootprintSource.QueryNear` and
  then `CrosswalkSignals`, both dead ends — the latter isn't even referenced in `Engine.cs`). The gate
  bisection found it in two commands. `CLAUDE.md` rule 2 holds yet again.
- **ROOT CAUSE (a stale comment, not a design flaw).** `CompositeFootprintSource.QueryNear`
  (`src/Sim.Core/Bridge/CompositeFootprintSource.cs`) allocated its merge scratch on the heap whenever the
  caller's span exceeded 64: `into.Length <= 64 ? stackalloc WorldDisc[64] : new WorldDisc[into.Length]`.
  Its header asserted *"Zero-alloc for spans up to 64 (every current consumer passes 16)"* — but
  **`Engine.MaxCrowdDiscs` was later raised 16 → 256 (commit `f9c837c`)** and this threshold/comment was
  never updated. So every live-city crowd query heap-allocated a **`WorldDisc[256]` ≈ 10 KB, per query,
  per vehicle, per step**, across 4 engine call sites (`Engine.cs:9908, 10026, 10325, 10694`).
  10 KB × 507 cars × ~2 calls ≈ 10 MB/step — matches the measured 9.15 MB/step. Only fires with ≥2
  children, which is exactly what `YieldEnabled` wires (the composite of ORCA footprints + crossing
  occupancy), which is why the YIELD gate isolated it.
- **BEFORE / AFTER (paired, same build settings, `git stash` A/B):**

      500 cars / 0 peds, 60 steps, 507 cars both arms:
        alloc/step   10,167,913 -> 585,744 B   (-94.2%, 17.4x)   [== the YIELD=0 floor: yield path now alloc-free]
        GC pause     12-21% of wall -> 0.43-0.91%
        mean/step    8.694 -> 7.15-7.95 ms     (~12-15%)
      200 cars / 400 peds, 100 steps, stash A/B:
        alloc/step   3,891,017 -> 306,235 B    (-92.1%, 12.7x);  gen0 20 -> 2
        BEHAVIOUR IDENTICAL: cars=201 peds=400 ped_hi_end=11 ped_hi_max=26 ped_lo_end=389 arrived=7

- **Change:** named constant `ScratchDiscs = 256` (must stay ≥ `Engine.MaxCrowdDiscs`), threshold raised to
  it, and the 16→256 drift documented in the header so it cannot silently recur.
- **Gates (all four, run first-hand by the orchestrator):** `dotnet test -c Release` → ParityTests 775
  passed / 4 pre-existing skips, Host 6/6, DotRecast 2/2, Pedestrians 317/317, IgBridge 11/11, **0 failed
  anywhere**; `Sim.Bench` **`BF3794A4704BCD79`, hashA == hashPar**; LiveCity.Tests **80/80**;
  Pedestrians.Tests **317/317**; city-3000 **0 stuck (ever and at end) + AGGREGATE PASS** (arrived
  relError 0.057, meanDuration 0.011, meanSpeed 0.030, KS 0.023, all tol 0.35).
- **Verdict: WIN — shipped `b1c9d7f`.** Byte-identical on the parity path (no golden sets `CrowdSource`)
  and behaviour-identical on the coupled path (paired counters above).
- **Lesson:** a *comment* asserting a performance property is not a test. This one was true when written
  and was falsified by a change 2 files away, silently, for however long. Any "zero-alloc for spans up to
  N" invariant needs the N tied to its consumer by a named constant (done) or asserted by a test.
  Also: the win came from a 2-command gate bisection after ~2 hours of source reading had produced only a
  ~350 KB partial explanation of a 20 MB problem.

### B1/B2/B3 — instruments + yield early-out, SHIPPED

- **B1 `a09fb29`** — `if (carN == 0) return 0;` in `CountYieldObservationsThisStep`. Verified
  bit-identical by stash A/B: `CarYieldObservations = 1789` before and after. Reclaims the measured 22.1%
  of wall that ped-heavy, car-free runs spent on ~18 000 discarded spatial queries per step. Gates 80/80,
  317/317.
- **B2 `7cfe273`** — 9 `ped.*` sub-phases in `PedLodManager.Step` + 2 in `PedDemand.Step`, default-off,
  reconciling to within 0.0–0.1% of `pedDemandStep`. **New finding it exposed:** `ped.despawnArrivals`
  ≈ 20% and `ped.frozenPos` ≈ 19% — both O(live-peds) `PositionOf` scans, i.e. **direct confirmation of
  A4** (redundant pose re-evaluation), previously invisible inside the opaque bucket.
- **B3 `fc5c2e8`** — per-phase allocation bytes via process-wide `GC.GetTotalAllocatedBytes(precise:false)`
  (never per-thread: several phases run `Parallel.For`). This instrument is what found A13.

### A3 · parallel-plan the high-power ORCA crowd — **WIN, SHIPPED `1c51c25`** (−30.4% wall at 20k peds)

- **Why now (and why NOT earlier):** at the invalid first ladder only **72** peds were high-power, so this
  would have been worthless — I explicitly refused to ship it on `BenchPedLod`'s borrowed 3.4× figure. The
  valid 20k measurement then showed `ped.orcaStep` = **37.4% of wall and 79.4% of all allocation** at
  1 189–1 832 high-power agents, which is what justified it.
- **The find:** `OrcaCrowd.UseParallelStep` is a long-shipped, documented, **tested** bit-identical parallel
  plan (`tests/Sim.ParityTests/OrcaParallelStepTests.cs`) — and a grep shows it was set **only** by
  `src/Sim.BenchPedLod`, never by `LiveCitySim`. Production ped ORCA had always run single-threaded on a
  24-core box; `PedLodManager.cs:209` set only `UseSpatialHash`.
- **Change:** new `LiveCityConfig.PedParallelOrca` (default **true**, because byte-identical — this is not a
  fast-mode knob) wired at `LiveCitySim.cs` where `_manager` is built, plus `LIVECITY_PEDPARALLELORCA` for
  paired A/B. **Self-gating:** `OrcaCrowd` engages the parallel plan only at
  `_count >= ParallelStepThreshold` (**256**, `OrcaCrowd.cs:277`), so every small scenario and the whole
  test suite keep the untouched serial path — which is why 80/80 + 317/317 + 775 stayed green unchanged.
- **BEFORE / AFTER — interleaved paired A/B, 20k peds (19 998 achieved, `ped_hi_max=1832` in BOTH arms),
  120 steps, 2 rounds:**

      mean/step   87.7 / 87.0  ->  60.9 / 60.8 ms   (-30.4%)
      p99        132.8 / 119.7 ->  72.3 / 83.2 ms   (-38%)
      RTF          5.70 / 5.75 ->   8.21 / 8.22

- **Gates:** ParityTests 775 / 4 pre-existing skips (includes `OrcaParallelStepTests`); LiveCity 80/80;
  Pedestrians 317/317; `Sim.Bench` `BF3794A4704BCD79`, `hashA == hashPar`. city-3000 deliberately not
  re-run: this touches only `LiveCityConfig` + `LiveCitySim` wiring, and city-3000 drives `Engine` through
  `Sim.BenchCity` with no ped manager and no `CrowdSource` — stated so a reviewer can check the reasoning
  rather than wonder whether a gate was skipped from laziness.
- **⚠ KNOWN REGRESSION (follow-up A14, not a blocker):** alloc/step **8.19 → 10.85 MB (+32%)**, and it
  becomes run-to-run VARIABLE (the serial arm was bit-stable at exactly 8,189,582 B/step, the parallel arm
  gave 10,845,678 then 10,943,820). The parallel path allocates per-task scratch. GC pause stays < 1% of
  wall so it is not currently costing time, but it should be pooled — and note the *variability* also means
  allocation totals can no longer be used as a determinism proxy in the parallel arm.
- **Verdict: WIN.** Cumulative with A13 + B1, the 20k-ped headline is **110.5 → 60.8 ms/step
  (1.82×)**, p99 **201 → ~78 ms (2.6×)**.

### Cumulative scoreboard (20 000 peds, dt=0.5, static 70 m pocket, 19 996–19 998 achieved)

| stage | mean ms/step | p99 ms | RTF | alloc/step |
|---|---|---|---|---|
| baseline (first valid measurement) | 110.5 | 201.4 | 4.52× | 7.94 MB |
| + B1 yield early-out (`a09fb29`) | 87.1 | 129.2 | 5.74× | 7.94 MB |
| + A3 parallel ORCA (`1c51c25`) | **60.8** | **~78** | **8.22×** | 10.85 MB ⚠ |

Car side, separately (500 cars, 0 peds): **A13 `b1c9d7f`** cut alloc/step **10,167,913 → 585,744 B
(17.4×)** and GC pause **12–21% → <1%** of wall.

### A4 · redundant pose recomputation — IN PROGRESS (delegated)

Now the dominant cost: `pedLowPowerGather` 19.0% + `ped.frozenPos` 16.9% + `ped.despawnArrivals` 15.3%
were >50% of wall before A3 and are ~73% of what remains. All three are O(all-peds) `PositionOf`/`PoseAt`
sweeps, and each `PoseAt` does up to **three** O(leg-vertices) polyline walks under weave — one of which
(`PathLength`, `ActivityTimeline.cs:306`) recomputes a total the `WalkSegment` ctor already computed.
- Task 1: kill the redundant `PathLength` walk — must be **bit**-identical, so the cached value has to come
  from the same function over the same sequence (a hand-rolled second summation could differ in the last
  bits and drift trajectories).
- Task 2: a per-step pose memo, **only if** two sweeps provably query the same `now` with no pose-affecting
  mutation between them. `frozenPos` is start-of-step; the gather runs after `_demand.Step` and after ORCA
  has moved agents — so a naive "compute once, reuse everywhere" memo would hand some consumer a
  one-step-stale pose. The implementor is instructed to prove the same-`now` property or decline.

### A14 (new) · pool the parallel-ORCA per-task scratch — see the A3 regression above.

### A4 · redundant pose recomputation — **WIN, SHIPPED `b6cfcb1` + `c2ff38e`** (−22.4% wall at 20k peds)

- **`b6cfcb1`** — `WalkSegment.RouteLength` cached at construction, replacing a `PathArcMotion.PathLength`
  re-walk on **every** `Evaluate` call. Implementor correctly found the ctor's existing value was a
  *different quantity* (Duration = length/speed, not length), so rather than hand-roll a second summation
  (which could differ in the last bits and drift trajectories) it caches a construction-time call to the
  **identical pure function** — bit-for-bit what the hot path used to compute.
- **`c2ff38e`** — a same-instant pose memo. The implementor did the important thing and **declined the
  unsafe part**: `PedLodManager.Step`'s `frozenPos` queries **start-of-step** (`PedLodManager.cs:699-705`)
  and was therefore EXCLUDED, while `PedDemand.DespawnArrivals` (`PedDemand.cs:265,268`) and
  `LiveCitySim`'s low-power gather (`LiveCitySim.cs:1069,1076-1082`) both query **end-of-step** `now+dt`
  with no pose-affecting mutation between them (despawn only removes arrived ids, which the gather then
  never visits) — a provably safe overlap. Guarded by a `now` equality check with a byte-identical
  direct-recompute fallback on any miss. Bonus find from the same reading: `AnimTagOf` and `PositionOf`
  each invoked `PoseAt` separately for the same `(id, now)`; fused into one `PoseInfoOf` call.
- **BEFORE / AFTER — orchestrator's OWN paired A/B at 20k peds** (`git checkout 428aac9 -- src/`, rebuild,
  measure, restore, rebuild, measure), 120 steps, 2 rounds each:

      mean/step  60.827 / 61.367  ->  47.532 / 47.493 ms   (-22.4%, 4/4 paired wins, no overlap)
      ALL counters identical: peds=19998 ped_hi_end=1296 ped_hi_max=1832 ped_lo_end=18702
                             ped_lo_max=18702 arrived=0

  I verified this myself rather than trusting the implementor's 400-ped check, because a pose memo is the
  one change tonight with real behaviour-change risk and 400 peds barely exercises it.
- **Gates:** ParityTests 775/4 skips, LiveCity 80/80, Pedestrians 317/317, `Sim.Bench BF3794A4704BCD79`
  `hashA == hashPar`. **Verdict: WIN.**

### A14 · pool the ORCA worker `ScratchSet` — **NULL, REVERTED** (hypothesis falsified)

- **Hypothesis:** `Parallel.For`'s `localInit: () => new ScratchSet()` (`OrcaCrowd.cs:578`, `:623`) runs per
  TASK; with per-index partitioning that could be ~one fresh scratch set per agent per step, each re-growing
  every buffer from capacity 1 → the 222 MB/step.
- **Change:** a `ConcurrentBag<ScratchSet>` pool rented in `localInit` / returned in `localFinally`.
- **AFTER:** alloc/step **255,517,684 → 253,523,736 (−0.8%)** — noise. Counters identical (so it was safe,
  just pointless). **Verdict: NULL, reverted.**
- **Lesson:** the falsifying observation was available and I should have taken it FIRST — the **serial**
  path uses a single `_scratch` instance for the whole run and allocated the same ~243 MB/step. That alone
  ruled out scratch construction before I wrote a line. Cheap disproof beats plausible mechanism.

### A15 · `HalfPlaneLp.LinearProgram3` projected-lines buffer — IN PROGRESS (delegated)

**Localized by splitting the phase, after THREE successive source-reasoned guesses were all wrong**
(I chased `CompositeFootprintSource.QueryNear`, then `CrosswalkSignals`, then `ScratchSet` pooling).
Splitting `ped.orcaStep` into `orcaDiscs` / `orcaRouteGoals` / `orcaCrowdStep` settled it in one run:

    ped.orcaCrowdStep   6866 ms  50.1% wall   13801.63 MiB  92.2% alloc
    ped.orcaRouteGoals    54 ms   0.4% wall       0.00 MiB   0.0% alloc
    ped.orcaDiscs          0 ms   0.0% wall       0.00 MiB   0.0% alloc

**Root cause — the SAME BUG SHAPE AS A13, in a second place.** `HalfPlaneLp.cs:141-145`:
`projLines = lines.Length <= 64 ? stackalloc OrcaLine[lines.Length] : new OrcaLine[lines.Length]`.
`MaxNeighbours` is **uncapped** in production (`OrcaCrowd.cs:189-197`; `PedLodManager` never sets it), so at
this pocket density an agent has ~283 agent-lines inside the 15 m `NeighbourDist` plus obstacle lines —
far over 64. `OrcaLine` is 32 B, and LP3 runs whenever `LinearProgram2` reports infeasible, which is
constant in a dense jam ⇒ tens of KB heap per call, ≈38 KB per agent per step.
Fix (delegated): thread a caller-owned `ProjLineScratch` from `OrcaCrowd.ScratchSet` (per-worker on the
parallel path, single instance on serial) through `OrcaSolver` into LP3, keeping today's stackalloc/heap
path as the fallback so `ShapedVoSolver` is unaffected. Byte-identity rests entirely on "LP3 never reads a
`projLines` index it did not write this call" — the implementor is required to verify that, because a
reused buffer is not zeroed whereas both current paths are.

### Coupled measurement — 3 009 cars + 19 993 peds (the closest available proxy to the target)

    mean 226.0 ms/step  p50 216.7  p99 358.6  RTF 2.21x  REALTIME yes (vs 500 ms budget @ dt=0.5)
    alloc 255 MB/step (24.4 GiB total)  GC pause 8.9% of wall  gen0 1261  peak WS 910 MiB
    ped_hi_end 6388 high-power / 13605 low-power

- **`ped_hi_end` is 6 388 here vs 1 296 in the ped-only run at the SAME static 70 m pocket** — cars cause
  peds to queue at crossings and accumulate inside the pocket, so **adding cars multiplies the ORCA
  workload ~5×**. This is a genuine coupling effect and it is exactly why the design doc forbade assuming
  the coupled cost is additive.
- **Parallel ORCA is worth 5× HERE, not 30%:** serial 1154.5 ms vs parallel 232.0 ms/step at 6 134
  high-power (`LIVECITY_PEDPARALLELORCA` A/B). The ped-only run understated A3's value by 4×.
- `engine.insert` = 15.8% of wall and 1 546 MiB (6.3% alloc) — **new, unexplained, worth a look** (A16).
- `carYieldMetric` = 4.7% of wall now that cars exist (the O(on-crossing peds × cars) structure I
  deliberately did not restructure in B1) ⇒ A17.

### A15 · LP3 projected-lines scratch — **WIN, SHIPPED `48e5a0f`** (5.5× less allocation, GC pause 9% → 2.5%)

- **Change:** `HalfPlaneLp.LinearProgram3` takes a trailing `Span<OrcaLine> projScratch = default`, using the
  caller's buffer when large enough and otherwise falling back to *exactly* the prior stackalloc/heap path
  (so `ShapedVoSolver`'s call site is untouched). `OrcaSolver.ComputeNewVelocity` forwards it;
  `OrcaCrowd.ScratchSet` gained `ProjLineScratch`, grown identically to `LineScratch`. Implementation note
  worth keeping: the local needed `scoped Span<OrcaLine> projLines;` or the ref-safety checker rejects it
  (CS8353) across the stackalloc-vs-parameter-slice branches.
- **Byte-identity argument (verified by reading, not assumed):** `projLines[0..count)` is always written
  before any read reaches it and only `projLines[..count]` is passed onward, so a reused, **non-zeroed**
  buffer's stale tail is never observed. That mattered because both prior paths (`stackalloc` span and
  `new[]`) were zero-initialized and a pooled buffer is not.
- **BEFORE / AFTER — orchestrator's paired A/B at production density** (3 000 cars + 20 000 peds; 2 984 /
  19 965 achieved, 6 134 high-power), 60 steps, 2 rounds each:

      alloc/step   264.2 / 262.9 MB  ->  48.8 / 47.1 MB   (-82%, 5.5x)
      GC pause     9.05% / 8.81%     ->  2.90% / 2.46%    (-70%)
      mean/step    230.0 / 230.4 ms  ->  216.4 / 214.4 ms (-6.4%, 4/4 paired wins, no overlap)
      ALL counters identical: cars=2984 peds=19965 ped_hi_end=6134 ped_lo_end=13831 arrived=53

- **Note on the implementor's own gate:** its paired check ran at 200 cars / 400 peds where the pocket holds
  only **26** high-power agents — under the 64-line threshold, so LP3's heap path barely engages and it
  measured +0.5% (noise). It said so plainly rather than dressing that up as the result. Correct call: a
  fixed small gate scenario cannot measure a density-dependent win, which is exactly why the orchestrator
  runs the real measurement.
- **Gates:** ParityTests 775/4 skips (incl. `OrcaParallelStepTests`/`OrcaRegionDecompositionTests`),
  LiveCity 80/80, Pedestrians 317/317, `Sim.Bench BF3794A4704BCD79` `hashA == hashPar`. **Verdict: WIN.**
- **Lesson (2nd instance tonight):** a `stackalloc`-vs-heap threshold is a **latent scaling bug** whenever the
  span it guards is sized by runtime data. A13 was `<= 64` vs a consumer raised to 256; this was `<= 64` vs
  a line count that grows with crowd density. **Grep the codebase for every remaining
  `stackalloc … : new …[]` threshold** — that is now a known recurring defect class here, not a one-off.

### Cumulative scoreboard — COUPLED (3 000 cars + 20 000 peds requested; ~2 984 / 19 965 achieved)

| stage | mean ms/step | alloc/step | GC pause |
|---|---|---|---|
| before A15 | 230.0 | 264 MB | 9.0% |
| + A15 (`48e5a0f`) | **215.4** | **47.9 MB** | **2.5%** |

Ped-only 20 000: **110.5 → 47.5 ms/step (2.33×)**. Car-only 500: alloc **10.17 MB → 586 KB/step (17.4×)**.

### ⭐ A12 · TARGET SCENARIO BUILT (`4759b03`) AND THE TARGET IS MET — 5 000 cars + 20 000 peds

`scenarios/_bench/livecity-mega/` — committed scenario input (net 3.9 MB), **8 999 lanes, 7 016.8 m ×
7 016.8 m**, 15×15 grid @ 500 m, `-L 2`, `--sidewalks.guess --crossings.guess --walkingareas --tls.guess
--no-turnarounds --seed 42`, netgenerate 1.20.0. Provenance committed.

**MEASURED AT THE FULL TARGET (achieved, not requested): `cars=5000 peds=20000 fill_ok=1`**

    mean 124.755 ms/step   p50 87.939   p95 277.513   p99 283.806   max 283.806
    RTF 4.01x   REALTIME: yes (mean AND p99 <= the 500 ms budget @ dt=0.5)
    alloc 8.83 MB/step (505 MiB total)   GC pause 0.915% of wall   gen2 = 0   peak WS 968 MiB
    ped LOD: 114 high-power / 19 886 low-power (static 70 m pocket, sparse on a 7 km net)
    prefill: 1071/1800 steps to reach 95% cars, 100% peds

**Caveats stated honestly:**
- `REALTIME: yes` is against a **500 ms** budget (dt=0.5 → 2 Hz). At **10 Hz** the budget is 100 ms and
  mean 124.8 ms would **FAIL**. So: comfortably real-time at 2 Hz with 4× headroom; **not yet** at 10 Hz.
- **There is a TAIL: 7/60 steps exceeded 3× p50**, and p95/p50 = 3.2×. GC does not explain it (pause 0.92%,
  gen2 = 0), so it is algorithmic — most likely the periodic O(N) sweeps. Worth chasing for smoothness even
  though the mean is fine.
- Only **114** peds are high-power here (the 70 m pocket is sparse on a 7 km net), versus **6 134** on the
  small demo net. So this run barely exercises ORCA — **the two scenarios probe genuinely different
  regimes** and both matter: the demo net is the ORCA-heavy case, this is the population-heavy case. Neither
  alone is "the" answer.

**Phase split AT THE TARGET (the ranking is different from every earlier run — read it before optimizing):**

    carYieldMetric      1918.6 ms  25.6% wall    <-- #1, and it is a DIAGNOSTIC counter
    pedDemandStep       2390.9 ms  31.9%  (of which ped.frozenPos 1106.7 ms = 14.8%)
    engineStep          1628.1 ms  21.8%  (engine.plan 9.3%, willPass 5.0%, insert only 0.8%)
    crossingOccupancy    821.3 ms  11.0%    <-- new, not previously visible
    publishPeds          316.0 ms   4.2%
    pedLowPowerGather    196.2 ms   2.6%

- **`engine.insert` is 0.8% here vs 15.8% on the demo net** ⇒ A16 is a **saturation artifact**, not a
  general cost: this net is not gridlocked so there is no pending-insertion backlog. A16 still worth fixing
  (it bites whenever demand exceeds capacity, which is exactly what a user cranking a slider does) but it is
  **not** on the target's critical path. Good example of why one scenario cannot rank the work.

**Bonus finding from the scenario build (a real scaling defect, worth its own task):** ped spawning under
`LiveCityConfig.ForSumocfg`'s RouteGraph nav (`SumoRouteGraphNav`) is **O(ped-graph size) per spawn**, and
the graph scales with **JUNCTION COUNT** (each junction adds crossings/walkingareas/connections). A 40×40
grid (1 600 junctions, 67 999 lanes) cost ~**390 ms/step for only ~12 spawns/step**, making 20 000 peds
unreachable; 15×15 @ 500 m (225 junctions) fixed it with **no loss of car capacity** (which tracks lane-km,
not junction count). ⇒ **A21: make ped spawn not O(graph).** This is why the committed net is 15×15.
Also: `PERF-HANDOVER.md` §5's `-L2` `ResolveSequenceCore` connection defect did **not** fire on this net at
up to ~4 920 concurrent cars over 1 000+ steps, so `-L1` was not needed.

### Remaining known targets (measured, ranked)

- **A16 · `engine.insert` = 15.8% of wall + 1 546 MiB (6.3% alloc)** — **DIAGNOSED (orchestrator, by
  reading; NOT yet measured or fixed).** It is an **O(pending × active) per-step scan** — the same shape as
  `PERF-ROADMAP.md`'s L2 finding (`FindFoeVehicle`/KeepClear, where indexing gave ~44×):
  - `InsertDepartingVehicles` (`Engine.cs`) rebuilds `candidates` by scanning **all** `_vehicles` each step,
    and a vehicle that fails insertion stays `!Inserted`, so it is **retried every step**. On a saturated
    net the pending backlog is large (≈1 500 here: ~4 500 spawn attempts over the fill against a 3 000 cap).
  - For **every** pending candidate, **before** any cheap blocked-lane check, the loop resolves
    `route`/`edge` (two string-keyed dict lookups) and then `ResolveBestDepartLane`, which contains
    `foreach (var other in ActiveVehicles())` — an **O(active)** occupancy scan.
  - It is live in this host: `LiveCitySim.cs:1021` spawns with **`departBestLane: true`** ⇒ SUMO's
    `departLane="best"` ⇒ the `Best` branch, not the cheap `Given` literal. (`CLAUDE.md` rule 3 satisfied:
    writer, reader, and the reader's caller all checked.)
  - ⇒ ~1 500 pending × ~3 000 active ≈ 4.5 M ops/step, matching the observed ~60 ms/step.
  - **Fix design — memoize `ResolveBestDepartLane` per edge WITHIN the insertion loop, and invalidate the
    whole memo on every SUCCESSFUL insertion.** A naive within-step memo would NOT be byte-identical,
    because each successful insertion mutates occupancy that `ResolveBestDepartLane` reads — but at
    saturation the overwhelming majority of attempts FAIL, so an invalidate-on-success memo keeps a high hit
    rate while remaining provably exact. Candidates share a small set of spawn edges here, so hits should
    dominate. **Measure before shipping** (my mechanism guesses went 0-for-3 earlier tonight).
- **A12 · no scenario can host the TARGET** (5 000 cars + 20 000 peds together). Demo net gridlocks at
  ~3 084 cars; Geneva has no ped infrastructure (~40 peds). Needs a purpose-built committed bench net.
  **Without this the target is unmeasurable and every coupled number is a proxy.**
- **A17 · `carYieldMetric` = 4.7% of wall** with cars present (the O(on-crossing peds × cars) structure
  left deliberately unrestructured in B1). Fix by indexing slow cars spatially once per step.
- **A18 · residual 47.9 MB/step** — where is it now? Re-attribute with `--profile` before guessing.
- **A19 · `MaxNeighbours` is UNCAPPED — potentially the single largest remaining lever, but BEHAVIOURAL.**
  `OrcaCrowd.MaxNeighbours` defaults to **0 = unlimited** (`OrcaCrowd.cs:189-197`) and `PedLodManager` never
  sets it, so **every** agent within the 15 m `NeighbourDist` contributes an ORCA half-plane. At the measured
  pocket density that is ~283 neighbours per agent, whereas **RVO2 itself ships a default of 10**. Since
  `ped.orcaCrowdStep` is still ~50% of wall *after* A15 fixed its allocation, capping neighbours is plausibly
  a multiple-× win on the dominant phase — ORCA's own literature treats nearest-k as standard, not a
  degradation.
  **BUT it changes ped trajectories, so it is NOT byte-identical** ⇒ `CLAUDE.md` rule 3 requires it ship as
  an **opt-in flag, OFF by default**, with a behavioural argument (crowd flow / no interpenetration /
  arrival equivalence), not just a speed number. Note `GatherAgentNeighbours` already implements the exact
  RVO2 `insertAgentNeighbor` nearest-k path when `maxN > 0`, so the code exists and is untested only in the
  sense that production never sets it. Prototype, measure, and report to the owner as an available trade —
  do not silently turn it on.
- **A20 · grep every remaining `stackalloc … : new …[]` threshold in the codebase.** Two of tonight's biggest
  wins (A13, A15) were this exact defect class: a threshold that silently stopped covering its caller's
  runtime-sized span. This should be a one-off sweep, not discovered a third time by accident.

---

## REVISED PLAN (post-A1) — the ladder must be made VALID before any optimization

**A8 (new, now FIRST) · make the bench reach the target counts.** Without this, no optimization can be
evaluated, because the target workload has never executed. Needs: a net big enough to hold 5 000 cars +
20 000 peds (the Geneva 11 km cut, or a generated grid city), a prefill/fill-to-cap phase that runs
*before* the measured window with spawn rates raised so the population actually reaches the cap, a hard
**refusal to report a config whose achieved counts miss the request** (loud FILL-FAILED, not a quiet
number), and a `dt` sweep so the real-time verdict is against a realistic budget rather than a soft 2 Hz.

**A9 (new) · chase the car-side 20 MB/step allocation + 15% GC pause.** Biggest measured effect so far,
matches the owner's reported symptom, and is on a path prior perf work never covered. Profile with
`--profile` per phase, then attribute the allocation.

Then, only once A8 makes the numbers real: A4 (pose memoization), A5 (reusable buffers), A6 (bound the
event list), and A3/A7 **only if** a large `--hi-res-radius` shows ORCA actually dominating.

---

## SUPERSEDED PLAN (post-A2, kept for the record) — ordered before A1's numbers arrived

- **A3 · enable ORCA parallel step in `LiveCitySim`.** One config change, existing bit-identity tests,
  measured 3.4–4× on the dominant ped phase. Byte-identical expected. **Do first.**
- **A4 · memoize low-power pose per step** (+ use `WalkSegment`'s precomputed length instead of
  recomputing `PathLength`). Byte-identical; kills 2–3 of 4 redundant evaluations × 3 scans each.
- **A5 · reusable buffers + fused passes in `PedLodManager.Step`.** Byte-identical; kills per-step
  O(N) `List`/`Dictionary` allocation and collapses redundant O(N) sweeps.
- **A6 · bound `PedPublisher._events`.** Fixes unbounded gen2 retention — the likely true cause of the
  reported GC spikes. Needs a drain/cursor design; consumers must be updated together. Highest care.
- **A7 · pack ORCA's hot triple (pos, vel, radius).** Only if profiling still shows ORCA neighbour
  reads dominating after A3.

Deliberately NOT doing: region decomposition (failed, #7), per-field SoA (wrong direction, #6),
`MaxNeighbours` cap (behavioural), anything on the car path from the excluded list.

---

## Standing hypotheses (to be confirmed or killed by A1's numbers — NOT yet approved)

Ranked. Full preconditions in `LIVE-CITY-PERF-TRACKER.md` Stage 2.

1. **E0 · low-power ped tick-rate decoupling — the only candidate with a MULTIPLIER, not a percentage.**
   Low-power peds are dead-reckoned: pose is a pure function of (timeline, t), so a consumer can
   evaluate them at any instant without their having been stepped. Ticking them on a deterministic
   1-in-K schedule keyed off entity id (thread/order-independent) divides the dominant population's
   cost by ~K. Will NOT be byte-identical ⇒ opt-in + behavioral gate.
2. **E1 · SoA for the ORCA neighbour read — a legitimate retry, not a repeat.** `PERF-HANDOVER.md` #4
   rejected per-field SoA for **car foe** reads for a specific reason: the gap math reads ~7 fields of
   **ONE** foe (AoS-shaped) so splitting them cost 7 cache lines instead of 1. ORCA is the mirror
   image — it streams **2 fields (pos, vel) over MANY** neighbours, which is exactly what SoA is for.
   The refutation does not transfer.
3. **E2 · ped-side parallelization** (iff ped step is serial; must keep per-entity seeded RNG).
4. **E3 · car/ped phase concurrency** (task-parallel, iff both A and B are material).
5. **E4 · coupling-query indexing** (iff the 2×2 shows superadditivity).
6. **E5–E9:** LOD churn, ORCA neighbour structure, publish rate-limiting, event pooling,
   `SPATIAL-OPT.md` §11 (fallback only, ~6%).

**Do NOT re-attempt on the car path** (built, measured, reverted — `PERF-HANDOVER.md`): per-field SoA
for foe reads, parallel `foeIndex` (twice, lock-overhead-bound), chunked range partitioner, Server GC
(a wash), inline `Pos` in neighbour buckets, vType-resolve memoization, region-parallel emit.
