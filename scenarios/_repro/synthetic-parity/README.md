# Synthetic SUMO parity scenario (vanilla 1.20.0 vs SumoSharp)

A **geometry-free** repro that reproduces two engine-behaviour divergences between
vanilla SUMO 1.20.0 and the SumoSharp drop-in, on a synthetic `netgenerate` grid —
**no real road-network data**, so it is safe to share with the SumoSharp team as the
golden repro.

## What it is

- **Net:** an 8×8 grid of **unsignalized priority** junctions, **2 lanes** per edge,
  120 m edges, dead-end fringe stubs (`--grid.attach-length 100`) so vehicles are
  born/die only at the fringe. Built deterministically by `build.py`.
- **Demand** (`build.py`, seed 42, 1360 vehicles, interleaved onto a dense schedule):
  - **1200 through** cars: fringe → fringe, long grid-crossing routes (Issue 2 load).
  - **120 park-and-stay** cars: fringe → a multi-occupant `parkingArea` (lane 0) with
    `<stop parkingArea=… duration="100000"/>` — a "park and stay" sink far past sim end.
  - **40 depart-from-parked** cars: born parked (`departPos="stop"`) in an origin lot,
    pull out, drive to a fringe exit.
- vType files are copied verbatim from the real produced fixture (they carry no
  geometry) so the multi-file `<route-files>` + `<vTypeDistribution>` path is exercised.

## Reproduce it

```bash
cd experiments/subarea/synthetic_parity          # this directory (self-contained, relative paths)
export SUMO_HOME=$(dirname $(dirname $(readlink -f $(which sumo))))   # or your SUMO_HOME
python3 build.py                                  # (re)generate net + demand + add + cfg (deterministic)

VAN=sumo
SS=/home/user/SumoData/SumoSharp/artifacts/sumosharp/linux-x64/sumosharp
OUT=../../scratch/synthetic_parity ; mkdir -p $OUT

# identical flags, only the binary differs:
$VAN -c scenario.sumocfg --fcd-output $OUT/van.fcd.xml --summary-output $OUT/van.sum.xml \
     --tripinfo-output $OUT/van.ti.xml --statistic-output $OUT/van.stat.xml --end 1000 --no-step-log true
$SS  -c scenario.sumocfg --fcd-output $OUT/ss.fcd.xml  --summary-output $OUT/ss.sum.xml \
     --tripinfo-output $OUT/ss.ti.xml  --statistic-output $OUT/ss.stat.xml  --end 1000 --no-step-log true
```

Read completed counts from `*.ti.xml` (tripinfo), peak `running` / mean rel speed from
`*.sum.xml` (summary), and jam-teleports from `*.stat.xml` (`<teleports jam=…>`).

## No-cheating audit (must PASS on both engines)

```bash
PYTHONPATH=$SUMO_HOME/tools python3 ../audit_nocheat.py \
    grid.net.xml scenario.rou.xml scenario.add.xml $OUT/ss.ti.xml $OUT/ss.fcd.xml
# -> NO-CHEATING AUDIT: PASS   (0 birth/death/FCD violations)
```

Both the vanilla and SumoSharp runs audit **PASS** — every birth/death is at a fringe
stub or off-road in a parkingArea. The divergences below are legitimate engine
behaviour, not audit cheating.

## Measured divergences (seed 42, `--end 1000`, this repo's binary)

### Issue 1 — park-and-stay residency  → REPRODUCED

| metric | vanilla | sumosharp |
|---|---:|---:|
| completed trips (tripinfo count)   | 1240 | 1334 |
| park-and-stay completed (of 120)   | **0** | **111** |
| peak `running`                     | **960** | 485 |
| end-of-sim `running` / `stopped`   | 120 / 120 | 10 / 4 |

Vanilla changes lane onto the parkingArea's lane (lane 0), parks the park-and-stay cars,
and they stay **resident** for the whole horizon (never "arrive", never in tripinfo,
`running` stays high). SumoSharp does **not** lane-change onto the parking lane: the car
stays on lane 1, never brakes for the lane-0 stop, drives off the **end of its final
(parking) edge**, and is removed as "arrived" → it completes (111 of 120 appear in
tripinfo) and drops out of `running`. Root cause is in `Sim.Core/Engine.cs`:
`StopLineConstraint` only brakes for a stop when `stop.LaneId == v.LaneId`, and there is
no strategic lane-change toward the stop lane; combined with the position-based
last-edge arrival, a car on the wrong lane runs off the route end instead of parking.

### Issue 2 — excess deadlock / jam-teleport  → REPRODUCED

| metric | vanilla | sumosharp |
|---|---:|---:|
| jam-teleports (`teleports jam=`)          | **0** | **21** |
| total teleports                           | 4 | 21 |
| mean relative speed (avg over active steps) | 0.493 | 0.411 |

Vanilla flows through the unsignalized priority junctions with essentially no
jam-teleports; SumoSharp deadlocks at the junctions and jam-teleports 21 times, with a
lower mean relative speed. Same net, same demand, same seed — a genuine
right-of-way / gap-acceptance / junction-blocking divergence, matching the real-net
observation (there: vanilla 1–2 jam-teleports, SumoSharp 33).

## Notes

- Deterministic: fixed `build.py` seed (42) and fixed `netgenerate --seed`; both engines
  run the same `--seed` implicitly (SUMO default 23), `step-length=1.0`.
- SumoSharp's `--statistic-output` currently emits only `<teleports …/>`; that is why
  jam-teleports are read from there and everything else from summary/tripinfo.
- `build.py` is re-runnable and cross-platform (list-form `subprocess`, `shell=False`,
  `sys.executable` is not needed here since it shells out only to `netgenerate`).
  It self-audits the generated demand before writing (all births/deaths at fringe/parking).
