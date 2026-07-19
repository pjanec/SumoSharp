# PEDESTRIAN-COMBINED-LOAD-RESULTS.md — vehicles + pedestrians concurrent, single-station envelope

**Status: INTERIM (campaign still running).** This doc is pushed as a partial hand-back on request; the
8+4 split and final medians are still being measured and will be filled in. Numbers marked *(prelim)*
are from fewer than 3 reps. Setup, methodology, isolated baselines, and the 6+6 split are final.

## The question

Single-station deployment: one box runs the sim (vehicles **and** pedestrians) plus the image
generators (IG) + OS. The sim may use at most ~50% of the CPU — on this **24-physical-core** box that's
~12 cores, the rest reserved for IG/OS. Target load: **~10k concurrent vehicles** (the `--region` path)
**+ 100k pedestrians** (LOD split, ~10% high-power), running **at the same time**. Both engines are
memory-bandwidth-bound (random neighbour access), so run concurrently they contend for the same DRAM
bus and will not add linearly. **Do vehicles and peds each still clear real-time simultaneously under
the ~50% cap?** Real-time: peds = **10 steps/s** (dt=0.1s); vehicles = **1 step/s** (this scenario's
step-length is 1.0s — see below).

## Box / method

| | |
|---|---|
| CPU | Intel Core Ultra 9 275HX, `ProcessorCount=24` (8 P-cores + 16 E-cores, no SMT) |
| P-core logical CPUs | **{0,1,10,11,12,13,22,23}** (detected via `GetSystemCpuSetInformation`; interleaved, not contiguous) |
| Power plan | High performance |
| Build | `-c Release`, net8.0 |
| Vehicle harness | `Sim.BenchCity <scn> --region --no-fcd --max-parallelism N` → whole-run-avg steps/s, peak concurrent |
| Pedestrian harness | `Sim.BenchPedLod --sizes 100000 --high-fraction 0.1 --steps 30 --warmup 8 --no-high-only` (both A/stable & B/churn) |
| Vehicle scenario | `scenarios/_bench/city-10000` — generated `scripts/gen-benchmark.sh 10000 24 500 1500`; engine-measured **peak 8,354 concurrent**, step-length 1.0s, 0 stuck (SUMO measured ~11k peak / ~8.8k steady) |
| Core capping | **process CPU affinity** (`ProcessorAffinity` mask) + **`DOTNET_PROCESSOR_COUNT=N`** — the latter caps *both* engines' TPL worker counts (neither pins `ProcessorCount`; confirmed each bench prints `logical processors : N`). `BenchPedLod --max-parallelism` is a no-op (P6-1), so affinity+DPC is the only ped lever. |
| Reps | median of 3 (see per-row sample counts) |

**P/E-aware core assignment (the key methodology point).** The P-cores are interleaved, so naive
contiguous blocks would hand the two engines unequal silicon. Using **sim = cores 0–11 (4P+8E), IG/OS
reserve = cores 12–23 (4P+8E)**, and contiguous sub-blocks within, each engine keeps **exactly 2
P-cores in every split** (VEH always holds P{0,1}, PED always P{10,11}); only E-cores shift with the
split. Masks:

| split | VEH cores (P/E) | mask | PED cores (P/E) | mask |
|---|---|---|---|---|
| 6+6 | 0–5 (2P+4E) | 63 | 6–11 (2P+4E) | 4032 |
| 4+8 | 0–3 (2P+2E) | 15 | 4–11 (2P+6E) | 4080 |
| 8+4 | 0–7 (2P+6E) | 255 | 8–11 (2P+2E) | 3840 |

**Contention tax** = concurrent steps/s ÷ **isolated steps/s on the *identical* core mask**. Using the
same physical cores for both cancels P/E-composition differences, so the ratio isolates pure shared-bus
contention. The concurrent protocol keeps the ped config identical to isolated (`--steps 30`) and
**loops the ped bench** while one vehicle scenario run acts as the clock; only ped iterations that ran
entirely within the vehicle run's window (fully mutually loaded) are counted.

## 1. Isolated-at-cap baselines (pinned, final)

Vehicles (`--region`, whole-run avg, peak 8,354, step-len 1.0s, 0 stuck):

| cap | cores | steps/s | × real-time (RT=1/s) |
|----:|------|--------:|---------------------:|
| 4 | 0–3 | 34.7 | 34.7× |
| 6 | 0–5 | 52.6 | 52.6× |
| 8 | 0–7 | 59.8 | 59.8× |

Pedestrians (100k, parallel ms/step → steps/s):

| cap | cores | stable ms | stable steps/s | churn ms | churn steps/s |
|----:|------|----------:|---------------:|---------:|--------------:|
| 4 | 8–11 | 76.3 | 13.1 | 142.2 | **7.0** |
| 6 | 6–11 | 49.7 | 20.1 | 85.3 | 11.7 |
| 8 | 4–11 | 40.5 | 24.7 | 65.1 | 15.3 |

**Even isolated, ped churn on only 4 cores (7.0 st/s) is already below the 10 st/s real-time line.**
Stable clears RT at every cap; churn clears at ≥6 cores. Ped throughput scales with cores (churn
4→6→8c: 7.0→11.7→15.3 st/s), i.e. below the P6-1 16–24-thread plateau this range is still core-limited,
not yet bus-saturated.

## 2. Concurrent split runs + contention tax

| split | engine | isolated st/s | concurrent st/s | tax (loss) | clears RT? |
|---|---|---:|---:|---:|:--:|
| **6+6** | vehicles (6c) | 52.6 | **47.6** | 0.91 (−9%) | ✅ (48×) |
| **6+6** | peds stable (6c) | 20.1 | **18.7** | 0.93 (−7%) | ✅ |
| **6+6** | peds churn (6c) | 11.7 | **11.1** | 0.95 (−5%) | ✅ (just above 10) |
| 4+8 *(prelim)* | vehicles (4c) | 34.7 | ~29 | ~0.84 | ✅ (29×) |
| 4+8 *(prelim)* | peds stable (8c) | 24.7 | ~23 | ~0.93 | ✅ |
| 4+8 *(prelim)* | peds churn (8c) | 15.3 | ~14 | ~0.93 | ✅ |
| 8+4 | — | — | *pending* | — | — |

**Early headline: the shared-bus contention tax is mild — ~5–16%, not the linear-collision blow-up a
worst-case bandwidth model would predict.** Two disjoint-core engines cost each other <~1/6th of
throughput. So concurrency itself is *not* the thing that breaks real-time; **absolute ped throughput at
low core counts is.**

## 3. Verdict (interim)

**(a) Do both clear real-time concurrently under the ~50% cap?** Preliminarily **YES for the balanced
and ped-favoured splits**: at 6+6 and 4+8, vehicles clear by ~30–50× and peds clear both stable
(≥18 st/s) and churn (≥11 st/s) with only a 5–16% concurrency tax. The **risk case is heavy churn when
peds are starved to 4 cores** (the 8+4 split, still measuring) — ped churn is ~7 st/s *isolated* on 4
cores, so concurrently it will land **below the 10 st/s real-time line**. That split under-provisions
the binding engine; the deployment should not give peds fewer than ~6 cores.

**(b) Is ped low-core scaling the limiting factor (peds have no region decomposition, unlike the
`--region` vehicle path)? — YES, directly.** Vehicles clear real-time with enormous margin (30–50×) and
pay a small tax; the entire real-time question rides on **ped churn throughput**, which is core-limited
in the 4–8 range (scales 7.0→11.7→15.3 st/s) and dips under real-time at 4 cores. The contention tax is
small, so the fix is **not** about reducing cross-engine interference — it is about raising **ped
per-core throughput** so churn clears real-time with margin even on a low ped core budget. That is
exactly what **P6-2 (port the vehicle engine's proven byte-identical `--region` domain decomposition to
`OrcaCrowd.Step`)** would deliver. **This is a GO signal for P6-2**, on the strength of: (i) ped churn is
the sole real-time-marginal quantity, and (ii) it is core-scaling-limited, not contention-limited.

*(Final 8+4 numbers, exact 4+8 medians, and the completed tax table to follow once the campaign finishes.)*
