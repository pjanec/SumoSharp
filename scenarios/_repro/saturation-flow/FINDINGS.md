# Saturation-flow micro-benchmarks — localizing the sustained-insertion discharge deficit

**Date:** 2026-07-22 · **Context:** SumoData's hand-off (`SUMOSHARP-HANDOFF-tl-junction-discharge-is-the-knee-blocker`)
reframed the calibration-knee blocker as a **junction/TL discharge deficit under sustained load** (~27% fewer
through-trips; box piles to 33 veh/lkm where vanilla holds 6). These are clean, single-junction
micro-benchmarks (netgenerate 3×3 grid, junction **B1** forced to a static TL: 42 s green / 90 s cycle) that
isolate *which movement class* diverges — the box/synthetic are too noisy, and one-shot cases drain.

Run: `sumo -c <cfg>` (vanilla) vs `dotnet src/Sim.Sumo/bin/Release/net8.0/sumosharp.dll -c <cfg>`; count
`<tripinfo>` arrivals over 600 s.

## Results

| test | cfg | vanilla | SumoSharp | SS / van |
|---|---|---|---|---|
| **1-lane straight-through, saturated** | `sat.sumocfg` | 150 (~1929 veh/hr/ln) | 147 (~1890) | **98%** ✅ |
| **2-lane straight-through, saturated** | `sat2.sumocfg` | 297 | 297 | **100%** ✅ |
| **permissive LEFT across oncoming** (1200+1200 vph) | `lt.sumocfg` | **7** left / 143 onc | **112** left / 142 onc | **1600%** ⚠️ |

## Conclusion — the deficit is NOT base saturation flow
- **Straight-through discharge is at parity** (1-lane 98%, 2-lane 100%). SumoSharp's fundamental
  car-following + TL green-phase discharge headway + start-up behaviour match vanilla. So the box's ~27%
  deficit is **movement-specific**, not a base saturation-flow gap. This *refines* SumoData's "TL discharge
  headway / saturation flow" hypothesis: base discharge is fine.
- **Permissive/minor-link YIELDING is badly off.** On a permissive left turn (`A1B1→B1B2`, linkIndex 14,
  state `g`) across a dense 1200 vph oncoming stream, vanilla lets **7** left-turners through in 600 s (they
  wait for gaps that rarely open); SumoSharp lets **112** through (**16×**). The oncoming through movement is
  unaffected (142 vs 143), so this is purely the left-turners' **gap-acceptance / yield** behaviour:
  **SumoSharp under-yields at permissive/minor links** — it accepts crossing gaps vanilla rejects.

## Suspected mechanism (matches `docs/FOLLOWUP-TL-throughput-flowrate.md` candidate 1)
`Engine.cs` cautious-approach / junction-yield arm keyed on the **static netconvert `<request>` response
matrix** ("is-minor") instead of SUMO's **live `MSLink::havePriority()`**. At a permissive-green (`g`) left,
SUMO's `havePriority()` is false → the vehicle must yield to oncoming with proper gap acceptance; SumoSharp's
static test evidently lets it proceed far too readily. FOLLOWUP doc already flagged this exact arm
(`couldBrakeForMinor`, `Engine.cs` ~6196) and measured "SumoSharp brakes a touch *less* on a `g` permissive
left" — consistent with 16× over-crossing here.

## Why this plausibly *reduces* box throughput (deficit), despite *more* crossings here in isolation
In this isolated geometry over-permissiveness just moves more left-turners with no downstream cost. In the
dense box it is different: a vehicle that crosses a conflicting stream it should have yielded to occupies the
junction interior when the exit isn't clear → **blocks the box / spillback** → conflicting movements stall →
net throughput drops and, under sustained insertion, cascades to gridlock + teleports. i.e. the *same* yield
bug that reads as "16× too many" in isolation reads as "27% too few through-trips" once junctions interact
under load. **To confirm:** a saturated CONFLICTING-movements micro-benchmark (two crossing saturated flows
with keep-clear), and/or SumoData's box-crop pipeline after the yield fix.

## Next step
Port `MSLink::havePriority()` (live signal/RoW state) into the cautious-approach / gap-acceptance arm so a
permissive-`g` movement yields like vanilla. Parity gate: base straight-through stays 98–100%; every committed
golden byte-identical (the priority/onramp goldens 11/19 exercise this arm — do not regress). Then re-measure
`lt.sumocfg` (target: left-turns ≈ vanilla's 7, not 112) and hand back to SumoData's pipeline.
