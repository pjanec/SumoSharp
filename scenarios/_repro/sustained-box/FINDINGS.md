# sustained-box — a sustained-load grid (does NOT faithfully reproduce the real knee)

> **CORRECTION 2026-07-22 (SumoData cross-check):** this benchmark **does NOT reproduce the real
> calibration-knee blocker.** SumoData ran their real sub-area pipeline on the same pre-yield-fix commit
> `3cbc8b9` this grid calls "near-parity" (396 vs 408 accumulation, 0.97×) and got **5.5× over-accumulation**
> (peak 33.24 vs 6.0 veh/lkm, 538.6% overshoot, 382 teleports). Same engine — this grid under-captures the
> phenomenon by ~40×. Why: it is **uniform straight corridors on protected-green TLs with every border node
> attached to fringe → all junctions are degree-4 (no connectivity asymmetry)**. The real blocker is a
> **discharge REDISTRIBUTION keyed on 4-way-vs-3-way TL connectivity asymmetry**: SumoSharp drains the
> high-degree 4-way center faster than vanilla while backing up the surrounding 3-way T-light approaches
> 8–10× locally (network-wide only ~1.22×, so it hides in aggregate metrics — and in this grid). See the
> **faithful** repro in `scenarios/_repro/signalized-asymmetry/` and SumoData's localization note.
>
> What this grid DID correctly show (kept for provenance): the permissive-yield fix is a ~12% junction-tempo
> regression under sustained load — a *separate, much smaller* axis from the 5.5× knee, and a realism win
> worth landing regardless (lt 112→7, goldens byte-identical). It is a red herring for the knee.

**Date:** 2026-07-22. **Purpose (original, now corrected):** attempt an offline reproduction of SumoData's
sustained-insertion knee. It reproduces a sustained-load *tempo* effect but NOT the real 5.5× density
blow-up — see the correction above.

## Setup
- `grid.net.xml`: `netgenerate --grid --grid.number=4 --grid.length=200 --default.lanenumber=2
  --tls.guess --tls.default-type=static` → a 4×4 grid, **16 static-TL junctions**, 2 lanes.
- `box.rou.xml`: 8 corridors (4 west→east rows, 4 south→north columns) that **cross at every junction**,
  each a **held flow** `vehsPerHour=1100 begin=0 end=1000` (`sigma=0`, deterministic). The chained
  fringe entries make the corridors **turn** at their entry junctions, so the demand exercises
  permissive turns as well as straights.
- `box.sumocfg`: `time-to-teleport=-1` (OFF) so we measure **pure accumulation**, not teleport-masked.
- Run 1000 s; compare `running` (accumulation), `arrived` (discharge), `meanSpeed` (tempo).

## Result — the deficit reproduces, and is dominated by the permissive-yield fix

Three engines, same net+demand (steady state t=999):

| engine | arrivals (t=999) | accumulation (running) | meanSpeed |
|---|---|---|---|
| **vanilla** SUMO 1.20.0 | **1896** | **408** | **5.37** |
| **pre-yield-fix** (`3cbc8b9`) | 1835 (97%) | 396 | 5.04 |
| **current** (with yield fix, `f69a58d`) | 1670 (88%) | 552 (+35%) | 3.52 |

- Early (t≤400) all three are identical; the divergence **emerges as the box fills and compounds** —
  the signature of a sustained-insertion discharge deficit.
- **The pre-yield-fix engine was near-parity** (1835/396 ≈ vanilla 1896/408). The permissive/minor
  crossing-yield fix (`FindCrossFoeVehicle` + `BlockedByCrossingFoe` window + impatience) **regresses**
  it to 1670/552.

## Why the yield fix regresses it (and why that is expected)

The yield fix makes SumoSharp yield permissive turns to crossing traffic **like vanilla** (the `lt`
micro-benchmark: 112→7 left-turns = exact parity; every FCD golden byte-identical). But it makes
junction yielding **more conservative**, which *reduces* junction throughput. Vanilla yields the same
turns and still flows well (1896/408); SumoSharp yields them correctly but with **worse per-event tempo**
under load (1670/552) — so the *steady count* matches vanilla while the *dynamic throughput* lags ~12%.

This is the **opposite axis** from the knee: the knee is a discharge *deficit* (needs MORE throughput),
and a correct-but-slower yield gives LESS. So this fix — a realism/correctness win worth keeping for the
serve/run (live-demo) role — is **not** the knee fix and pushes slightly the wrong way on it. Diagnostics:

- The crossing yield fires ~7346× over the run (permissive turns); on-junction `AdaptToJunctionLeader`
  braking fires ~1× — so the effect is the approaching-crossing yield, not on-junction following.
- **Impatience is not the lever**: `--time-to-impatience` 60 s vs 300 s gives *identical* output
  (1670/552/3.52) — the turners' continuous `WaitingTime` resets in stop-and-go, so impatience never
  builds. The residual is in the yield/window decision dynamics vs vanilla's multi-foe gap-acceptance,
  not the impatience blend.

## How to reproduce (~20 s)
```
DLL=src/Sim.Sumo/bin/Release/net8.0/sumosharp.dll
cd scenarios/_repro/sustained-box
sumo   -c box.sumocfg --summary-output /tmp/v.xml --tripinfo-output /tmp/v_ti.xml --no-step-log true
dotnet ../../../$DLL -c box.sumocfg --summary-output /tmp/s.xml --tripinfo-output /tmp/s_ti.xml --no-step-log true
# compare <step ... running= arrived= meanSpeed=> at t=999; count <tripinfo> for total arrivals.
```

## Implication for the calibrate role
Two consistent readings:
1. **Keep the yield fix** (realism win, on the serve/run critical path) and **calibrate the knee with
   vanilla** (SumoData's standing guidance) — SumoSharp's throughput deficit is then irrelevant to
   calibration, which uses vanilla.
2. If SumoSharp must itself be the calibrator, the remaining work is to match **vanilla's dynamic yield
   tempo** (not just the steady count) — a harder fidelity target (vanilla's full multi-foe
   `MSLink::opened` gap-acceptance) than the count parity already achieved.
