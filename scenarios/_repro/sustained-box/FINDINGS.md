# sustained-box — an offline reproduction of the calibration-knee discharge deficit

**Date:** 2026-07-22. **Purpose:** SumoData's calibration knee is measured under *sustained insertion
held at the calibrated density*, where a small per-junction discharge deficit compounds into unbounded
accumulation (their box piles to 33 veh/lkm where vanilla holds 6). The one-shot synthetic drains
(finite demand) and no longer isolates it. This benchmark reproduces the **sustained-load regime
offline**, no SumoData pipeline required.

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
