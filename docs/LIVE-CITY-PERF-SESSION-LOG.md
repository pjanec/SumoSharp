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

---

## REVISED PLAN (post-A2) — ordered for tonight

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
