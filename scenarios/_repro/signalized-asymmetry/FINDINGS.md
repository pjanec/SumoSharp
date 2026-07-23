# signalized-asymmetry — faithful repro of the calibration-knee discharge redistribution + its fix

**Date:** 2026-07-22. **Purpose:** SumoData localized their real-box knee blocker to a **discharge
REDISTRIBUTION at signalized junctions keyed on connectivity asymmetry** — SumoSharp piles 8–10× on the
3-way T-light approaches while keeping the 4-way center at parity (network-wide only ~1.22×, so it hides
in aggregate metrics). The `sustained-box` grid missed it because it attached fringe to every border node,
making all junctions degree-4 (no asymmetry). This benchmark **reproduces the redistribution** and — it
turned out — **root-caused and fixed it**.

## Setup
- `grid5.net.xml`: `netgenerate --grid --grid.number=5 --grid.length=200 --default.lanenumber=3
  --tls.guess` with **NO fringe attach** → the 3×3 interior junctions are **4-way** (deg 4), the border
  edge-mids are **3-way T** (deg 3), corners are 2-way. 21 `traffic_light` junctions, 3-lane approaches —
  the mixed 3-way/4-way TL topology SumoData specified.
- `asym.rou.xml`: 12 bidirectional corridors (WE/EW rows + SN/NS cols) crossing at the interior 4-ways,
  each a **held flow** `vehsPerHour=900`, `sigma=0`. The corridors **end at border 3-way T-junctions**
  (arrival edges), exactly as a bounded/served demand's destinations do.
- Measure **per-edge steady-state (t ≥ 60% horizon) accumulation** vs vanilla, tagged by the degree of
  the junction each edge feeds — NOT just total arrivals.

## Result — the redistribution reproduces, then vanishes with the fix

Per-edge mean accumulation ratio (SumoSharp / vanilla), by the degree of the junction the edge feeds:

| edges feeding | BEFORE fix | AFTER fix | vanilla |
|---|---|---|---|
| **3-way T-lights** (deg 3, arrival edges) | **2.45×** (top hotspots 3.1–3.2×) | **1.01×** | — |
| 4-way through-junctions (deg 4) | 1.03× | 1.03× | — |
| totals: running / arrived / meanSpeed | 385 / 3215 / 5.91 | **274 / 3326 / 8.44** | 279 / 3321 / 8.17 |
| a corridor trip `we1` duration | 135 s | **90 s** | 90 s |

Before the fix this is SumoData's signature exactly: the 3-way T-light approaches back up ~2.5× (locally
3.2×) while the 4-way approaches stay at parity. After the fix, **SumoSharp matches vanilla on every axis**
— accumulation, arrivals, speed, per-edge distribution, and per-vehicle duration.

## Root cause — SumoSharp braked ARRIVING vehicles at red border TLs

The FCD is decisive. On a corridor-end edge `D1E1` (length 172.8, route ends here at border T-light `E1`),
steady state:
- **vanilla**: front vehicles at pos 158.9, **speed 13.9** — flowing at full speed to the exit.
- **SumoSharp (before)**: front vehicles at pos 169.9, **speed 6.4** — braking toward the E1 stop line.

SUMO `MSVehicle::planMoveInternal` (MSVehicle.cpp:2587): a vehicle on its **final route edge**
(`myCurrEdge + view + 1 == myRoute->end()`) approaches `arrivalPos` at `arrivalSpeed` (default laneMaxV —
no deceleration) and **`break`s out of the link walk BEFORE the red/yellow brake is added**. An arriving
vehicle exits at the end of its arrival edge and never crosses that edge's exit TL. SumoSharp's
`RedLightConstraint` keyed off the *current lane's* exit connection with no such exemption, so it braked
arriving vehicles at red border TLs — adding ~45 s per trip (`we1` 90→135 s) and backing up every arrival
approach. Because arrival edges concentrate at low-degree border T-junctions (their routes terminate
there), the backup reads as "3-way T-lights pile up" — the connectivity-asymmetry signature.

## The fix (Engine.RedLightConstraint)
Port SUMO's final-edge break: skip the TL brake when `v.LaneSeqIndex + 1 >= v.LaneSeqLen` (ego is on the
last lane of its route — the arrival edge), the same "no upcoming lane" test `SuccessiveLaneSpeedConstraint`
already uses. **Every committed golden stays byte-identical** (no golden vehicle arrives at a red-held TL
edge); full suite green (657 parity + 227 pedestrian); deterministic (serial == `--max-parallelism 8`).

## Reproduce (~30 s)
```
DLL=src/Sim.Sumo/bin/Release/net8.0/sumosharp.dll
cd scenarios/_repro/signalized-asymmetry
sumo   -c asym.sumocfg --fcd-output /tmp/v.xml --tripinfo-output /tmp/v_ti.xml --no-step-log true
dotnet ../../../$DLL -c asym.sumocfg --fcd-output /tmp/s.xml --tripinfo-output /tmp/s_ti.xml --no-step-log true
# per-edge accumulation by to-junction degree; `f_we1` trip duration (both == vanilla with the fix).
```

## For SumoData
This is the same *mechanism* class as your real-box redistribution (arrival edges at signalized junctions),
and the fix closes it completely on this faithful repro. **Please re-run the real sub-area calibration on
this dev HEAD** — the arrival-at-TL brake affects every vehicle whose randomTrips destination sits on a
TL-controlled edge, which should be a large share of your 3-way-T pile-up. If residual overshoot remains
after this, it is a smaller, separate contributor and we localize the next hotspot the same way.
