# synthetic_junction2 — TL-approach gridlock witness (RESOLVED by P2-G Bug-2)

Geometry-free synthetic that reproduces the real-box acceptance residual: on the same
net/demand/flags, the **baseline** SumoSharp (before P2-G Bug-2) progressively gridlocked the
traffic-light approaches — vanilla teleports 0 / SumoSharp 42 (jam=13, yield=29), and, more
tellingly, SumoSharp cleared only 277/290 trips by t=1000 while its on-net halting climbed and
stuck. Same direction as the real urban box.

## Resolution — P2-G Bug-2 (exclude traffic_light junctions from the RBL cycle resolver)

`ResolveRightBeforeLeftCycles` is a deterministic stand-in for SUMO's RNG deadlock-abort, which
fires ONLY for uncontrolled LINKSTATE_EQUAL right-before-left links. Its cycle detector, however,
read the static `<request>` foe matrix (TL-state-blind); on a dense TL junction it found the
geometric 4-way cycle and, via the greedy ascending-index select, held a *green* link a full
signal cycle (`JunctionCycleHold`) while a *red* link "won" the tie-break. That was the rolling
yield-release slowdown that gridlocked the short TL approaches. Excluding `traffic_light`
junctions from the resolver (exactly as `allway_stop` is excluded) lets the TL program own its
links. All 619 committed goldens stay byte-identical (RBL was already inert at the sparse TL
goldens), and here the network flows instead of freezing:

| metric | vanilla | SumoSharp baseline | SumoSharp + Bug-2 |
|---|---|---|---|
| trips cleared @ t=1000 | 290 | 277 | **290** |
| teleports total | 0 | 42 | **17** |
| jam / yield | 0 / 0 | 13 / 29 | 3 / 14 |

**Arrival curve** (the un-confounded believability signal — raw `halting` over-counts SumoSharp's
park-and-stay sinks, which SUMO excludes when parked, so use trips cleared):

| t | vanilla | baseline | + Bug-2 |
|---|---|---|---|
| 499 | 177 | 134 | 146 |
| 599 | 241 | 170 | 219 |
| 699 | 280 | 203 | 254 |
| 999 | 290 | 277 | **290** |

The progressive gridlock is gone: the network clears all 290 trips like vanilla instead of leaving
13 permanently stuck. A modest mid-run lag (~20–26 trips behind vanilla near t=600–700) remains —
SumoSharp is still slightly slower to clear the TL approaches — but it catches up fully and does
not freeze. The committed `ss.*` outputs are the **+ Bug-2** run; the baseline column above is the
pre-fix state this witness was built to catch (a regression back to it would resurface here).

## Baseline headline (pre-Bug-2, for the record)

| metric | vanilla SUMO 1.20.0 | SumoSharp baseline @ ee44ff7 |
|---|---|---|
| teleports total | **0** | **42** |
| jam | 0 | 13 |
| yield | 0 | 29 |
| wrongLane | 0 | 0 |
| no-cheating audit | — | PASS |

## Headline

| metric | vanilla SUMO 1.20.0 | SumoSharp @ ee44ff7 |
|---|---|---|
| teleports total | **0** | **42** |
| jam | 0 | 13 |
| yield | 0 | 29 |
| wrongLane | 0 | 0 |
| no-cheating audit | — | PASS |

Run (both engines, only the binary differs):
```
<bin> -c scenario.sumocfg --end 1000 --no-step-log true \
      --statistic-output {van|ss}.stat.xml --tripinfo-output {van|ss}.ti.xml \
      --summary-output {van|ss}.sum.xml
```

## Why this net reproduces (and the old synthetic_junction no longer does)

After the routeLength / departPos=stop fixes, `synthetic_junction` inverted to vanilla=3,
SumoSharp=0 — SumoSharp now tolerates plain unsignalized congestion *better* than vanilla, so a
pure-priority grid no longer shows the residual. The real-box residual survives because of one
dominant lever this net now embeds:

- **A handful of traffic-light junctions** (here 10 of 140 nodes, `--tls.guess`) carrying heavy
  demand. SumoSharp holds vehicles at TL/minor links a few seconds longer than vanilla; on short
  approaches with no creep-room the wait timer runs to `time-to-teleport=120` and fires a
  teleport, where vanilla releases the same vehicle just under threshold. This is the real box's
  dominant "Y1" yield pattern (10 of its 18 yields sat at one TL node).

Turning TLs off (`--no-tls-guess`) inverts the net back to vanilla>SumoSharp — confirming the TL
handling is the residual's origin, amplified by short edges + `device.rerouting`.

## Knobs (all in build.py defaults, = winning "v6" config)

- `--tls-guess` (default ON), `--tls-thresh 110` → ~10 TL junctions.
- `--mind 22 --maxd 90` → short approach/connector edges (184 of 400 edges < 30 m) so queues
  fill an edge and vehicles cannot creep to reset the wait timer.
- `--default.priority 4 --random-priority` → major/minor asymmetry (yield movements).
- `--random-lanenumber` → 1↔2-lane drops (merge bottlenecks).
- `--through 280 --period 1.7` → moderate load: enough to stress the TLs, not enough to
  gridlock vanilla.
- No-cheating sink: `parkingArea` + park-and-stay `duration=100000` + `departPos="stop"` origins.

## Reproduce

```
cd experiments/subarea/synthetic_junction2
python3 build.py            # regenerates grid.net.xml + scenario.* deterministically
sumo      -c scenario.sumocfg --end 1000 --no-step-log true --statistic-output van.stat.xml
<sumosharp> -c scenario.sumocfg --end 1000 --no-step-log true --statistic-output ss.stat.xml \
            --tripinfo-output ss.ti.xml
python3 audit_nocheat.py grid.net.xml scenario.rou.xml scenario.add.xml ss.ti.xml   # PASS
```
