# LIVE-CITY-PERF-DESIGN.md — ENGINE performance of the coupled cars+pedestrians live-city path

**Status: measurement-first. No optimization is designed here yet, deliberately.** This document
defines the target, the instrument, the acceptance gates, and the decision procedure. The candidate
levers in §6 are explicitly **unvalidated guesses** and none may be implemented before §4 localizes
the cost. Read `CLAUDE.md` (§Measurement discipline) and `PERF-HANDOVER.md` (§Experiments log — the
already-tried-and-reverted list) first.

## 1. The target (owner-stated, this is the definition of done)

**5 000 vehicles + 20 000 pedestrians must run smoothly in the SumoSharp ENGINE.**

Scope: the engine only. **Rendering is explicitly NOT the goal** — the Godot viewer is out of scope
for this effort (viewer-side findings are recorded in the tracker's appendix so they are not lost, but
no viewer work is part of it). Every measurement here is headless.

"Smoothly" is made falsifiable as: at the configured sim rate (step budget = `Dt` seconds),
**RTF ≥ 1.0 AND p99 step time ≤ the step budget.** The tail clause is not decoration — a config whose
*mean* fits the budget while its p99 is several× over is not smooth, and a mean-only report hides
exactly that. The instrument therefore prints an explicit `REALTIME: yes/no` per config.

## 2. Why this is NEW territory (prior perf docs do not answer it)

All prior perf work — `PERF-ROADMAP.md` (Layers 0–2), `PERF-HANDOVER.md` (on-target, 3.06× → 3.57×
SUMO), `SPATIAL-OPT.md` — measured **`Sim.Core.Engine` with cars only**, on `scenarios/_bench/*` which
contain **no pedestrians**. Pedestrian work (`PEDESTRIAN-P6-*`, `Sim.BenchCrowd`, `Sim.BenchPedLod`)
measured ORCA/LOD **in isolation** — no cars, outside the coupled host.

**Nothing has ever measured `LiveCitySim`, the coupled host, at any scale.** Confirmed by inventory: no
harness existed that ran it headless and printed a timing. So the prior conclusion ("we are at a
memory-bandwidth wall and everything has been tried") **does not transfer** — it was reached on a
different workload. Treat this as an unprofiled system, and treat the excluded-ideas list in §6 as
binding only for the car hot path where it was actually measured.

## 3. The cost dimensions (a ped count alone is an uninterpretable number)

Four axes, all of which must be labelled on every measurement:

1. **Vehicle count** (`CarTargetConcurrent`).
2. **Pedestrian count** (`PedPopulationCap`).
3. **High-res vs low-res ped split — the dominant ped axis.** Peds inside the high-realism ORCA pocket
   run full ORCA (high-power `FreeKinematic`); outside it they are low-power dead-reckoned
   (`ActivityTimeline`/`PathArc`). 20 000 mostly-low-power peds and 20 000 all-high-power peds are two
   completely different workloads. Knobs: `LiveCitySim.SetLcRealismZone(x, y, radius)`
   (`LiveCitySim.cs:740`; promote = radius, demote = 1.3×radius; static default 70 m/100 m at `:326`);
   observable via `PedLodManager.HighPowerCount` (`PedLodManager.cs:438`).
4. **Sim rate** (`Dt` / `LIVECITY_HZ`) — sets the step budget the whole verdict is relative to.

**Achieved, never requested.** `LiveCitySim` is closed-loop: it inserts only while
`live < CarTargetConcurrent` (`CLAUDE.md` rule 4). A config that never filled to its cap would
otherwise be reported as if it had, so every number carries the **achieved** car/ped counts and the
achieved high/low-power split. Also mandatory (rule 10): every `LIVECITY_*`/`SUMOSHARP_*` gate value is
printed per run — they are process-global and an inherited shell value is indistinguishable from a
measured one.

## 4. Localization — the only permitted first step

Optimizing before knowing which component dominates is the exact failure mode `CLAUDE.md`
§Measurement-discipline rule 2 documents (five reasoned interventions, all inert, before one trace
found the cause in minutes).

| # | Candidate home | Isolating measurement | Discriminating observation |
|---|---|---|---|
| A | Car engine step (`Sim.Core.Engine`) | `--peds 0` | scales with cars, flat in peds |
| B | Ped simulation (ORCA / steering / LOD churn) | `--cars 0` | scales with peds; scales hard with hi-res radius |
| C | Publish → reconstruct path (event stream, `HeadlessIg`) | phase split inside `LiveCitySim` | scales with peds but absent from A and B measured alone |
| D | **Coupling / interaction** (ped↔car yield queries, shared structures) | the 2×2 below | superadditivity |

**Coupling must be measured, not assumed additive.** Run `0:0`, `cars:0`, `0:peds`, `cars:peds`. If
`cars:peds` > (`cars:0` + `0:peds` − `0:0`) by a material margin, there is a real interaction cost and
*that* is the target — a fact no isolated bench (and none of the existing car-only or ped-only benches)
could ever have revealed.

**Spikes are a distribution question.** The instrument records **every** step duration and reports
p50/p95/p99/max plus the count of steps over 3×p50, and reads `GC.GetTotalPauseDuration()` (.NET 7+)
for the direct pause measurement rather than inferring pauses from collection counts. Note prior
finding: on the car path `% Time in GC` was ~6.5% and Server GC was **a wash** — so GC is a hypothesis
to test here, not an established cause.

## 5. Instrument-first, and the instrument is committed

`CLAUDE.md` rule 8: a probe run once and reverted makes its own number unfalsifiable and poisons every
later comparison. `src/Sim.BenchLiveCity` and the `LiveCitySim` phase-profiling scaffolding are
therefore **committed, default-off, permanent**, exactly like `Engine.ProfilePhases`.

## 6. Candidate levers — UNVALIDATED, ranked by (expected ROI × safety). NOT a plan.

Nothing here is approved. Each names what must be TRUE for it to be worth doing.

1. **Ped-side parallelization** — the ped population is 4× the car population at the target ratio and
   per-ped work is largely independent. Worth doing iff B dominates and the ped step is currently
   serial. Must preserve per-entity seeded RNG so results stay independent of thread order.
2. **High-power/ORCA cost per ped** — iff the hi-res sweep shows the pocket population dominating.
   ORCA is O(neighbours) per agent; a poor neighbour structure shows up as superlinear in pocket
   density. `Sim.BenchCrowd` already measures ORCA in isolation and can bound what is achievable.
3. **Coupling-query indexing** — iff §4's 2×2 shows superadditivity. Almost certainly an unindexed
   scan, the exact shape of `PERF-ROADMAP.md`'s L2 finding (`FindFoeVehicle`/KeepClear were O(N²)
   scans; indexing gave ~44×). Highest ROI *if* the interaction term is real.
4. **LOD churn** — promote/demote hysteresis thrash at a large pocket boundary would cost per-step
   allocation and rebuild work. Observable as high-power count oscillating rather than tracking.
5. **Publish/reconstruct allocation** — the one path that scales with ped count and is known to
   allocate per-ped-per-step. Output-side, low parity risk.
6. **`SPATIAL-OPT.md` §11** (persistent segmented HotVeh store) — worth doing iff A dominates AND 5 000
   cars hits the same bandwidth wall. Fallback, not a lead: projected ~6% on the car hot path.

**Excluded — already built, measured, reverted on the car hot path** (`PERF-HANDOVER.md` §Experiments
log): per-field SoA for foe reads (#4/#5), parallel `foeIndex` (#7, twice, lock-overhead-bound),
chunked range partitioner, Server GC (a wash), inline `Pos` in neighbour buckets, vType-resolve
memoization, region-parallel emit. **Do not re-attempt these for cars.** They are *not* automatically
excluded for the ped path, which was never measured — but re-proposing one there requires saying why
the ped access pattern differs.

## 7. Acceptance gates — every change, no exceptions

A change ships only if it is **byte-identical** OR **behind an opt-in flag off by default**
(`CLAUDE.md` rule 3).

**All four, after every engine/`Sim.Core`/`Sim.Pedestrians` change:**
1. `dotnet test -c Release` (`Traffic.sln`) — no new failures vs the recorded baseline.
2. `dotnet run -c Release --project src/Sim.Bench` — hash `909605E965BFFE59`, and `hashA == hashPar`.
3. `dotnet test -c Release tests/Sim.LiveCity.Tests` — **not in `Traffic.sln`**, must be built and run
   explicitly (`CLAUDE.md` trap #9) or stale code is measured. Also `tests/Sim.Pedestrians.Tests`.
4. `city-3000` 0 stuck + aggregate PASS — the junction-exercising gate; the `Sim.Bench` hash uses a
   junction-free highway and does **not** cover junction/foe code.

**Perf claims:** interleaved paired A/B of two snapshotted builds, alternating runs, counting paired
wins and comparing medians. Noise on this box is ~8% (thermal); single-config medians taken minutes
apart are confounded by drift and **will lie**. Under ~5% is not a result. Never build while measuring.

**Parallelism claims:** thread count must never leak into results. Verify serial-vs-parallel identity
on a junction-exercising scenario (trip SHA match), not just the highway hash.

## 8. Task list and status

See `LIVE-CITY-PERF-TRACKER.md`.
