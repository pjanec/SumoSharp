# synthetic_junction2 — byte-parity teleport witness (jam + yield residual)

Geometry-free synthetic that reproduces the real-box acceptance residual: on the same
net/demand/flags, **vanilla SUMO 1.20.0 teleports 0, SumoSharp teleports 42 (jam=13, yield=29)**.
Same direction and magnitude class as the real urban box (SumoSharp 36 = 18 jam + 18 yield
vs vanilla 2). Deterministic (fixed seed 42), no-cheating audit PASS.

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
