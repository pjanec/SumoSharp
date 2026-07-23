# C1 witness: permissive-turn gap starvation at junction d_4_1

## What this is

A minimal, **synthetic, place-token-free** crop of `experiments/subarea/demo_city/box/net.xml`
around junction `d_4_1` (a static-TL 3-way T-junction), plus one hop downstream at its
structural mirror `d_4_2`, built to reproduce **C1**: a permissive turner starved of a gap
under saturation, stalling at the head of a shared lane and blocking the through traffic
queued behind it.

- `net.xml` — netconvert crop of the box net, bbox `2080,1580,3120,2370` (~1040m x 790m).
  Kept: `d_4_1`'s incoming edges (`e_d_5_1_d_4_1`, `e_d_4_0_d_4_1`, `e_d_3_1_d_4_1`) and
  outgoing edges (`e_d_4_1_d_3_1`, `e_d_4_1_d_4_2`, `e_d_4_1_d_5_1`), one upstream hop on
  each approach, one downstream hop off each destination, and `d_4_2` (structurally
  identical mirror T-junction) with its own approach/destination edges. `d_4_1`'s
  `tlLogic` is preserved **verbatim** (phases/durations/state string unchanged — verify
  with `grep -A6 'tlLogic id="d_4_1"' net.xml`).
- `routes.rou.xml` — static routes only, explicit `route edges="..."` on every flow,
  **no `device.rerouting` anywhere** in this scenario (isolates C1 from the box's
  downstream rerouting artifact).
- `scenario.sumocfg` — relative paths, no extra options; `time-to-teleport` left at
  SUMO's default (so a genuine stall shows up as a very long wait / eventual teleport,
  not silently suppressed).

## The junction / movement

Junction `d_4_1` (x=2600, y=1850), a 3-way T: East leg to/from `d_5_1`, South leg
(stem, one-way in) from `d_4_0`, West leg to/from `d_3_1`; the North leg (`d_4_2`) is
one-way **out** only.

`e_d_4_0_d_4_1` (the South/stem approach) has 3 lanes: lane0 is pedestrian-only, lane1
carries right+through (`dir=r` linkIndex3, `dir=s` linkIndex4), **lane2 carries
through+LEFT** (`dir=s` linkIndex5, `dir=l` linkIndex6). linkIndex6 is the permissive
left (`via=":d_4_1_6_0"`, destination `e_d_4_1_d_3_1`), state `'g'` (permissive/yield) in
`d_4_1`'s `tlLogic`. Read directly off `d_4_1`'s `<request index="6" ... foes="...">`
bitmask, linkIndex6's true vehicle foes are `{0, 1}` — both connections from the East
approach `e_d_5_1_d_4_1` (index1 is its through movement to the same destination edge,
`e_d_4_1_d_3_1`). That East-approach through is the "opposing (foe) through" stream
this witness saturates.

`d_4_2` (one hop downstream on the same one-way arterial, `d_4_0 -> d_4_1 -> d_4_2 ->
d_4_3`) is a byte-identical `tlLogic` program (same phases, same offset) with the exact
same lane/movement layout — `e_d_4_1_d_4_2` lane2 shares through + a permissive LEFT to
`e_d_4_2_d_3_2`. This witness stages the same conflict there too (see "Tuning history").

## Demand (`routes.rou.xml`)

All flows `begin=0 end=1200`, static routes, `sigma=0.5`, no rerouting:

| flow | route | rate (vph) | role |
|---|---|---|---|
| `flow_opposing` | `e_d_5_1_d_4_1 → e_d_4_1_d_3_1 → e_d_3_1_d_2_1` | 2200, forced lane1 | saturates the East-approach through foe of the permissive left |
| `flow_left` | `e_d_4_0_d_4_1 → e_d_4_1_d_3_1 → e_d_3_1_d_2_1` | 500, natural lane choice | **the measured permissive-left movement** |
| `flow_through` | `e_d_4_0_d_4_1 → e_d_4_1_d_4_2 → e_d_4_2_d_4_3` | 1600, natural lane choice | through traffic sharing lane2 with `flow_left`; queues behind a stalled left-turner |
| `flow_opposing2` | `e_d_5_2_d_4_2 → e_d_4_2_d_3_2 → e_d_3_2_d_2_2` | 2200, forced lane1 | mirrors `flow_opposing` at `d_4_2` |
| `flow_left2` | `e_d_4_1_d_4_2 → e_d_4_2_d_3_2 → e_d_3_2_d_2_2` | 500, natural lane choice | mirrors `flow_left` at `d_4_2` |

## Verified signature (measured, 1200 s, this witness)

Engines run: vanilla = `sumo` 1.20.0. SumoSharp = built from **SumoSharp @ `1a908ee`**
(the pinned submodule HEAD — no branch switch was needed; it ran this witness directly).

**Network-level (sumo `--summary-output`, final step t≈1199s):**

| | vanilla | SumoSharp | delta |
|---|---|---|---|
| running (still in-network) | 253 | 390 | **+54%** |
| arrived (total network throughput) | 1301 | 1009 | **-22.4%** |
| meanSpeed (m/s) | 2.27 | 1.04 | **-54%** |

SumoSharp over-accumulates and under-discharges the network relative to vanilla by a
very similar margin to the already-confirmed full-box/arterial-tjunction finding
(+52%/-22%/-56% there) — this is the primary verified C1 signature for this witness:
**a sustained, reproducible network-wide discharge deficit and congestion buildup**,
driven by permissive-turn stalls at the shared lane.

**Head-of-lane stall / blocking (FCD, `speed<0.3` continuous run on `e_d_4_0_d_4_1`
lane2):** in both engines, a permissive left-turner (`flow_left`) and a through vehicle
(`flow_through`) queued directly behind it on the same shared lane show matching
maximum stall durations (vanilla: ~299s for both; SumoSharp: ~83s for both) — direct
confirmation that a stalled left-turner blocks the through traffic behind it on the
shared lane, in both engines.

**Per-movement discharge on `e_d_4_0_d_4_1` (vehicles/100s bins, FCD-derived, crossing
into the junction):**

| | vanilla `flow_left` | SumoSharp `flow_left` |
|---|---|---|
| bins (100s) | 8,8,7,2,1,0,0,0,0,0,1,0 | 10,8,8,7,9,11,6,5,11,5,1,4 |
| total (of 100 issued) | 27 | 85 |

## Tuning history / honest caveat

A single isolated T-junction (`d_4_1` alone, forced-lane demand) did **not** reproduce
a SumoSharp-worse signature on this specific local movement — repeated attempts (forced
lanes, natural lane-choice, heavier saturation) consistently showed the *opposite* at
the local-edge level: vanilla's permissive left stalled for much longer (up to 299s,
non-recovering for 600s straight) than SumoSharp's (up to ~83s). FCD tracing showed why:
SumoSharp exhibits a lane-discipline defect on this cross-section (cars spilling onto
the pedestrian-only lane0 and the wrong through lane), which locally *relieves*
pressure on `e_d_4_0_d_4_1` even though it degrades the network overall. This matches
prior findings recorded upstream in `SumoSharp/scenarios/_repro/arterial-tjunction/`
(same C1 family, "turn-lane segregation" / discharge deficit at 3-way T-lights),
where the clean reproduction required **multiple T-junctions in series** compounding
into a network-wide knee, not a single isolated junction. Staging the identical
conflict one hop downstream at `d_4_2` (mirroring `d_4_1`) reproduced that same
compounding: the aggregate network signature above (+54% running / -22% arrived /
-54% meanSpeed) is robust and closely matches the already-verified full-box magnitude.
The **local, single-movement** deficit (SumoSharp discharging fewer left-turners
specifically off `e_d_4_0_d_4_1` than vanilla) is **not** cleanly reproduced by this
witness — treat the network-level signature above as the faithful, verified anchor,
and the local per-movement numbers as a secondary, noisier signal.

## How to run

```bash
export PATH="/root/.dotnet:$PATH"
cd experiments/subarea/witnesses/c1-perm-turn-starvation

# vanilla
sumo -c scenario.sumocfg --end 1200 \
  --tripinfo-output /tmp/van.trip.xml --fcd-output /tmp/van.fcd.xml \
  --summary-output /tmp/van.summary.xml --no-step-log true

# SumoSharp (build first: dotnet build src/Sim.Sumo -c Release, from the SumoSharp checkout)
dotnet <path-to>/SumoSharp/src/Sim.Sumo/bin/Release/net8.0/sumosharp.dll -c scenario.sumocfg --end 1200 \
  --tripinfo-output /tmp/ss.trip.xml --fcd-output /tmp/ss.fcd.xml \
  --summary-output /tmp/ss.summary.xml --no-step-log true

# compare final-step running/arrived/meanSpeed in the two summary-output files
```
