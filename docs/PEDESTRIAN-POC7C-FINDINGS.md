# PEDESTRIAN-POC7C-FINDINGS.md — integrated LOD CPU-scale acceptance

**Status: measured.** Final POC-7 sub-part. Companion to `PEDESTRIAN-POC7A-FINDINGS.md` (parallel
`OrcaCrowd.Step`) and `PEDESTRIAN-POC7B-FINDINGS.md` (single-stream bandwidth). Measures the *integrated*
LOD system (`PedLodManager` driving ~10 % high-power `OrcaCrowd` agents + ~90 % low-power `PathArc`
path-followers) at the 100k target, via `src/Sim.BenchPedLod`.

**Hardware caveat (as in POC-7a):** this session's VM has **4 logical processors**. The owner target is a
**16+‑core Windows box**. Numbers below are 4-core measurements; treat the *ratios and shapes* as the
finding and extrapolate the absolute rates upward with POC-7a's near-linear-to-core-count scaling.

Benchmark command (Release):
`dotnet run -c Release --project src/Sim.BenchPedLod -- --sizes 20000,50000,100000 --high-fraction 0.1 --steps 30 --warmup 8`

---

## Q1 — does ms/step scale with the HIGH-power set, or with the TOTAL?

| config | actual high-power | serial ms/step | parallel ms/step | speedup |
|---|---:|---:|---:|---:|
| 10 000 high-power only (0 low-power) | 6 714 | 142.0 | 64.2 | 2.21× |
| 100 000 total (~10k high + ~90k low) | 10 477 | 238.1 | 75.2 | 3.17× |

**Verdict: cost scales with the high-power set, not the total.** Adding **90 000 low-power** `PathArc`
path-followers on top of the high-power crowd raised the parallel step time only from ~64 ms to ~75 ms
(≈ **+11 ms for 90k agents** — and the 100k run even had *more* high-power agents, 10 477 vs 6 714, which
accounts for much of the difference). The low-power tier is genuinely O(1)/step (no neighbour queries, no
ORCA), so the 100k world costs essentially what its ~10k high-power subset costs. **This is the core LOD
thesis (design §5/§9), confirmed empirically.**

## Main sweep — Scenario A (stable membership) vs Scenario B (churning membership)

| N total | scenario | actual high | switches/step | serial ms/step | parallel ms/step | speedup |
|---:|---|---:|---:|---:|---:|---:|
| 20 000 | A / stable | 2 098 | 70 | 19.1 | 7.7 | 2.50× |
| 20 000 | B / churn | 5 254 | 175 | 53.3 | 17.6 | 3.02× |
| 50 000 | A / stable | 5 235 | 175 | 83.8 | 26.9 | 3.11× |
| 50 000 | B / churn | 12 160 | 614 | 321.9 | 94.1 | 3.42× |
| 100 000 | A / stable | 10 477 | 349 | 258.3 | 80.1 | 3.23× |
| 100 000 | B / churn | 23 515 | 1 379 | 1 048.4 | 285.5 | 3.67× |

## Q2 — is promotion churn a bottleneck? (YES — and it quantifies the Add/Remove backlog)

At 100k, the churning scenario costs **285 ms/step vs 80 ms/step stable — 3.6× worse.** Two effects
combine, and both matter:

1. **More high-power agents.** The moving interest sources sweep through the crowd, so Scenario B holds far
   more agents high-power at once (23 515 vs 10 477 at 100k). Part of B's cost is simply *more real ORCA
   work* — legitimate, not overhead.
2. **Rebuild-on-membership-change overhead.** Every promotion/demotion triggers `PedLodManager.RebuildHighCrowd`,
   which rebuilds the *entire* high-power `OrcaCrowd` from scratch (the POC-3/POC-6 workaround for
   `OrcaCrowd` having no `Add`/`Remove`). At **~1 379 switches/step** that rebuild runs constantly, and its
   cost is O(current high-power count) *per switch*. This is the dominant avoidable cost in B.

**This is the concrete, quantified motivation for the deferred crowd-API work (design §3d).** A genuine
`Add`/`Remove` on `OrcaCrowd` (free-list + generation, or stable-slot deactivate/reactivate) replaces the
O(high)-per-switch full rebuild with O(1)-per-switch, which would remove most of the ~205 ms gap between B
and A at 100k. It is the single highest-value follow-up for the interest-sources-move-constantly case
(which is the *normal* case: a player avatar or IG camera sweeping the city, §5). Stable and lightly-
churning worlds already run without it.

## Q3 — interactive-rate verdict + combined acceptance picture

- **On this 4-core VM:** stable 100k ≈ **80 ms/step (~12.5 steps/s)** parallel; heavy-churn 100k ≈
  **285 ms/step (~3.5 steps/s)**.
- **Extrapolated to the 16+‑core target:** POC-7a measured near-linear scaling up to the box's core count
  and the lane engine reaches ~3.2–3.6× at 8 threads on a 16c/24t box (`PERF-HANDOVER.md`, memory-bandwidth
  bound). So the stable 100k case should reach comfortably interactive rates on the target hardware; the
  heavy-churn case is where the Add/Remove fix pays off and should be prioritized before churn-heavy
  production loads.
- **Combined with POC-7b (bandwidth):** bandwidth is *not* the constraint — even the worst-case all-100k-
  promoted spike is 182 Mbit/s against the ~500 Mbit/s budget (63 % headroom). **CPU is the acceptance
  constraint, the LOD split makes the stable target tractable, and `OrcaCrowd.Add/Remove` is the priority
  optimization for constant-churn workloads.**

---

## Consolidated POC-7 summary

- **7a (CPU parallel):** `OrcaCrowd.Step` parallelized opt-in, **bit-identical** to serial (2000-agent ×
  300-step exact-equality gate); ~3–4× on a 4-core VM, near-linear to core count. Lane parity untouched.
- **7b (bandwidth):** quantized 18 B `PedFreeKinematicRecord` + `PathArc` record; the single DDS-multicast
  stream measures **36.75 Mbit/s typical / 182 Mbit/s worst-case spike / 294 Mbit/s naive** — all under the
  500 Mbit/s budget. Bandwidth is solved.
- **7c (integrated CPU scale):** the LOD split makes 100k tractable (cost tracks the ~10k high-power set,
  not the 100k total); **membership churn via `RebuildHighCrowd` is the main remaining CPU cost**, which
  concretely justifies building real `Add`/`Remove` on the crowd store as the top follow-up.

**Acceptance:** the design's scale thesis holds — 100k pedestrians + 10k cars is tractable on CPU (LOD) and
comfortable on bandwidth (multicast + quantization). The one identified priority follow-up before heavy-
churn production is efficient crowd `Add`/`Remove` (design §3d), now quantified here.
