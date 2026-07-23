# arterial-tjunction — the calibration-knee blocker: turn-lane segregation (getBestLanes), FCD-traced

**Date:** 2026-07-22. **Context:** SumoData localized the sub-area knee 5.5× over-accumulation to a
**through-discharge deficit at 3-way T-lights**, and gave the repro upgrade: through traffic routing PAST
the T-lights (shared right+through / through+left lanes), not arriving at them. Their pointer was
`ComputeWillPass` (once-per-cycle junction entry). **This repro reproduces the deficit and FCD-traces it to
a deeper root: SumoSharp does not segregate turning vs through traffic onto the correct lanes like vanilla —
the `WillPass`/stopped-during-green symptom is downstream of mis-laned turners blocking through lanes.**

## Setup
`net.net.xml` (hand-built via netconvert): a 3-lane arterial `A—B—C—D—E` with **static-TL 3-way T-junctions**
at B, C, D (a side road T's in from the north at each). Main-through movement (`BC→CD`) is protected `O`;
the left turn onto the side road (`BC→CSc`) is permissive `g` **sharing lane 2** with through. `art.rou.xml`:
sustained held flows (`sigma=0`) — main through W↔E (1600 vph each way) + side-road turning traffic (500 vph)
+ a main→side left-turn flow (400 vph). `time-to-teleport=-1`.

## Result — reproduced the knee signature (t=999)
| | running (accum) | arrived | meanSpeed |
|---|---|---|---|
| vanilla | 303 | 1094 | 6.14 |
| SumoSharp | **462 (+52%)** | **858 (−22%)** | **2.69 (−56%)** |

SumoSharp over-accumulates and under-discharges at the T-lights — the same direction as the real box's 5.5×.

## FCD trace — the mechanism
- A through vehicle (`f_we.200`) sits glued at `BC_0` pos 265.3 for **89 s straight** (t=901→990), crossing a
  full green phase — the once-per-cycle stall SumoData saw.
- The **head** of its lane (`veh1354` @ `BC_0`:272.8, the stop line) is a **permissive LEFT-turner**
  (`egoLink=11` = `BC→CSc`, `sigPri=False`) stuck on a **through lane**. Its binding constraint is
  `JunctionYieldConstraint = 0` via `AdaptToJunctionLeader`, yielding to the continuous opposing-through
  stream (`:C_5_*`). It never gets a gap → blocks every through car behind it on `BC_0`.
- So the "protected-through stuck during green" is a **symptom**: a through car stuck behind a mis-laned
  turner.

## Root cause — turn-lane segregation (getBestLanes / departLane / strategic LC)
Steady-state lane distribution (t∈[600,1000], SumoSharp `sigma=0`):

| | vanilla | SumoSharp |
|---|---|---|
| LEFT-turners on `BC` | **100% lane 2** | lane 2 62%, **lane 1 14%, lane 0 7%** |
| THROUGH on `BC` | only lanes 0/1 | mixed across **all 3 incl. lane 2** |
| THROUGH on `AB` (first edge) on lane 2 | **86 (≈0%)** | **3112** |

**Vanilla keeps through traffic OFF the left-turn lane and all left-turners ON it** → parallel 3-lane
discharge. **SumoSharp mixes them**: through vehicles occupy lane 2, congesting it so left-turners can't reach
it and spill onto through lanes 0/1, where a stuck (yielding) left-turner serial-blocks the through movement.
A 3-lane, ~47%-green approach collapses to a serial ~1-lane bottleneck — SumoData's exact description, and the
8–10× queue / ~40% discharge deficit.

This is a **strategic lane-assignment fidelity gap** (SUMO `MSVehicle::getBestLanes` + `LCA_STRATEGIC`:
vehicles are strongly incentivized onto a lane whose downstream continuation matches their route with the
fewest future changes, which segregates turners from through well upstream of the junction). SumoData's own
evidence corroborates — they saw a lane-changer stuck mid-change (`4595` lane1→lane2).

## Scope note (why this needs alignment before a fix)
Unlike the arrival-edge fix (a small `RedLightConstraint` exemption), this is in the **lane-change / lane-choice
model** — heavily covered by committed goldens (LANE-CHANGE-OVERLAP, keep-right, cooperative LC, the dense-LC
rungs). A change here must stay byte-identical for all of them. The fix likely lives in `getBestLanes`-equivalent
lane-desirability / `ResolveBestDepartLane` / the strategic lane-change desire, ported faithfully from
`MSVehicle::getBestLanes` / `MSLCM_LC2013` strategic arm. **Validate at served/sustained density** (per-edge
discharge on this arterial, target ≈ vanilla's throughput), never sparse-probe flow (knee-selection artifact).

## Reproduce (~30 s)
```
DLL=src/Sim.Sumo/bin/Release/net8.0/sumosharp.dll
cd scenarios/_repro/arterial-tjunction
sumo   -c art.sumocfg --fcd-output /tmp/v.xml --summary-output /tmp/vs.xml --no-step-log true
dotnet ../../../$DLL -c art.sumocfg --fcd-output /tmp/s.xml --summary-output /tmp/ss.xml --no-step-log true
# compare running/arrived/meanSpeed at t=999; lane-distribution of f_wsc (left) vs f_we (through) on BC.
```

---

## UPDATE 2026-07-22 (session 5) — root cause CORRECTED + faithful rule-2 fix (byte-identical), knee residual is DISCHARGE not lane-choice

**The earlier "getBestLanes under-values the turn lane" framing was wrong.** Traced the pool directly:
`ComputeBestLanes` assigns the CORRECT offsets — a left-turner's pool targets `AB_2 → BC_2`; the desirability
is right. The real free-flow bug is that **keep-right pulls the turner back OFF its turn lane**.

**FCD trace (free-flow `art_lo`, `f_wsc.1`):** lands `BC_1`, changes to `BC_2` (pos 82), **keep-rights back to
`BC_1`** (pos 90), oscillates. Vanilla lands `BC_1`, moves to `BC_2`, stays. Mechanism: SUMO has THREE
strategic stay-on-best rules (`MSLCM_LC2013.cpp:1398-1440`); SumoSharp ported only VARIANT_21
(`neighDist < TURN_LANE_DIST=200`), which cannot fire on the 272.8 m `BC` edge. The missing **rule 2**
(`bestLaneOffset==0 && neighLeftPlace*2 < laDist`, POSITION-relative) is what keeps vanilla's turner on the
turn lane as it nears the junction. Confirmed by experiment (force-suppress keep-right when eligible →
oscillation gone, `BC_2` 87%→97%).

**Fix implemented (faithful port of rule 2 into `ApplyKeepRightDecision`):**
- Byte-identical: full suite **657 parity + 227 pedestrian, 0 failed**. Deterministic (serial == `-p 8`).
- Free-flow left-turners on `BC_2`: **87% → 95%** (vanilla 99%); through correctly off `BC_2` (4% = vanilla).

**Knee residual is NOT lane-choice.** High density barely moves (running 462→460, meanSpeed 2.69→2.84):
under a jam speed→low→`laDist`→small, so rule 2 correctly/faithfully rarely fires. With free-flow lane
discipline now ≈ vanilla yet the deficit persisting, **the knee's dominant cause is a separate
saturation/discharge mechanism** (on-junction yield / LCA_URGENT blocker-cooperation / insertion), matching
SumoData's original "through-discharge deficit" and Geneva's "additional discharge component" caveat. The
high-density `BC_2`=26% through-occupancy is gridlock-confounded (Geneva's warning), NOT a clean lane-choice
signal. Next localization should target discharge under saturation, on the now-free-flow-clean engine.
