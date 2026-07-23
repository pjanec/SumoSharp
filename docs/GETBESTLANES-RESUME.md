# GETBESTLANES-RESUME.md — the calibration-knee fix: turn-lane segregation (getBestLanes lane-desirability)

**Written 2026-07-22 to survive context compaction.** Self-contained: a fresh session resumes from this file
(plus `docs/CALIBRATION-KNEE-INDEX.md` for the full arc). SUMO source: `/sumo` (v1_20_0).

> **UPDATE 2026-07-22 (session 5) — READ FIRST. The "getBestLanes under-values the turn lane" hypothesis
> below is FALSIFIED.** Traced the pool: `ComputeBestLanes` offsets are CORRECT (a turner's pool targets
> `AB_2 → BC_2`). The real free-flow bug is **keep-right pulling the turner back off its turn lane** — SUMO's
> stayOnBest **rule 2** (`MSLCM_LC2013.cpp:1410-1418`, `bestLaneOffset==0 && neighLeftPlace*2 < laDist`,
> position-relative) was never ported (only VARIANT_21's static `neighDist<200`, which can't fire on the
> 272 m edge). **A faithful port of rule 2 is implemented in `ApplyKeepRightDecision` (byte-identical: 657+227
> goldens pass; deterministic; free-flow `BC_2` 87%→95%).** BUT it barely moves the high-density knee
> (running 462→460): under a jam `laDist` shrinks so rule 2 rightly rarely fires. **Conclusion: the knee's
> dominant cause is NOT lane-choice but a separate saturation/DISCHARGE mechanism** (on-junction yield /
> LCA_URGENT blocker-cooperation / insertion). Next localization → discharge, on the free-flow-clean engine.
> Full detail: `scenarios/_repro/arterial-tjunction/FINDINGS.md` (UPDATE 2026-07-22 session 5). The
> lane-desirability investigation below (§2–§8) is kept for history but is NOT the root cause.

---

## RESUME PROMPT (paste to restart)
> Implement the **turn-lane segregation** fix on branch `claude/dense-lane-overlap-fix-5tr4ha` (HEAD around
> `2fc7a12`). Read `docs/GETBESTLANES-RESUME.md` then `scenarios/_repro/arterial-tjunction/FINDINGS.md`. The
> calibration-knee blocker is **CONFIRMED and localized**: SumoSharp does not sort turning vs through traffic
> onto the correct lanes like vanilla. On the arterial repro, at FREE-FLOW (no jam), SumoSharp mis-lanes ~13%
> of left-turners onto a through lane vs vanilla's ~1% (12× worse) — a genuine upstream lane-choice bug (not a
> gridlock symptom, per the low-density disambiguation). Under load it compounds: a stuck permissive
> left-turner sits at the head of a *through* lane, yields forever, and serial-blocks through discharge → the
> 3-lane→~1-lane bottleneck that is the box's 5.5× over-accumulation. The fix is in **`ComputeBestLanes`**
> (SumoSharp's `getBestLanes` port that writes each vehicle's target lane sequence `_laneSeqPool`;
> `TryStrategicLaneChange` faithfully steers toward it). Find why it under-values the correct turn lane and
> port SUMO's `MSVehicle::getBestLanes` lane-desirability faithfully. **Iron law:** every committed golden
> byte-identical (this area is heavily tested: LANE-CHANGE-OVERLAP, keep-right, cooperative-LC, dense-LC
> rungs, all lane-change scenarios), full `dotnet test Traffic.sln` green, deterministic (serial ==
> `--max-parallelism 8`). **Validate at SERVED/SUSTAINED density**, never sparse-probe/one-shot flow (the
> knee-selection artifact: a fix that only frees sparse probes pushes SumoData's selected knee UP and makes
> the overshoot WORSE — that is exactly what the arrival-edge fix `ca8d515` did, 538→556%). **Success:**
> arterial `art.sumocfg` SumoSharp accumulation/discharge → vanilla parity (running 462→~303, arrived
> 858→~1094, meanSpeed 2.69→~6.1); `art_lo.sumocfg` left-turners on the correct lane → ~vanilla's 99% (from
> ~87%); then hand to SumoData to re-run the real box (target: 5.5× / 556% overshoot drops toward parity).

---

## 1. State of play (branch `claude/dense-lane-overlap-fix-5tr4ha`)
Everything below is LANDED, byte-identical goldens, full suite green (657 parity + 227 pedestrian),
deterministic. In commit order:
- **Gap-1 dead-lane, Stage-4 box, parking** — see `CALIBRATION-KNEE-INDEX.md`.
- **Permissive-yield** (`f69a58d`): `lt` 112→7 = vanilla. Realism win, SEPARATE axis (reduces throughput;
  NOT the knee). `docs/DISCHARGE-YIELD-RESUME.md`.
- **Arrival-TL discharge fix** (`ca8d515`): `RedLightConstraint` final-edge exemption (a vehicle whose route
  ENDS at a TL edge is not braked at that TL). Real fix, but SumoData's re-run showed it does NOT help their
  box (their demand arrives off-lane at parkingAreas, not at TL through-edges) — and nudged their overshoot
  538→556% via the knee-selection artifact. `scenarios/_repro/signalized-asymmetry/`.
- **Repro witnesses + docs** (`2c00179`..`2fc7a12`): `sustained-box` (unfaithful, kept as a lesson),
  `signalized-asymmetry`, `parking-maneuver` (parking ruled out), **`arterial-tjunction`** (the faithful knee
  repro + low-density disambiguation). Index: `docs/CALIBRATION-KNEE-INDEX.md`.

## 2. THE ROOT CAUSE (confirmed genuine, not a symptom)
SumoData localized the box 5.5× to a **through-discharge deficit at 3-way T-lights**. With their repro upgrade
(through traffic routing PAST the T-lights, shared through+turn lanes) I built `arterial-tjunction/` (3-lane
arterial through static-TL T-junctions) and reproduced it (running 462 vs vanilla 303, arrived 858 vs 1094,
meanSpeed 2.69 vs 6.14). FCD-traced:
- The stuck head vehicle is a **permissive LEFT-turner on a THROUGH lane** (`BC_0`), yielding forever via
  `AdaptToJunctionLeader` to the oncoming-through stream → blocks all through traffic behind it. The
  "protected-through stuck during green" SumoData saw is the SYMPTOM (a through car stuck behind it).
- Steady-state lane distribution: **vanilla runs left-turners 100% on the dedicated left lane and keeps
  through OFF it; SumoSharp mixes them.**
- **Cause-vs-symptom disambiguation (Geneva's gate) — RUN and PASSED:** at FREE-FLOW (`art_lo.sumocfg`, no
  jam, vehicles CAN reach any lane), SumoSharp still mis-lanes **~13% of left-turners onto a through lane vs
  vanilla's ~1%** (12× worse). So it is a genuine upstream lane-choice bug. (Mild at free-flow — no deficit
  there — but bites under load; 13%→26% jammed.)
- SumoData corroborated on their real box: left-on-wrong-lane 22.9% vs 3.7%, right-on-wrong-lane 25% vs 0%,
  through skewed 67% vs 50% — 5–8× worse in SumoSharp. Their edge has only 2 SHARED lanes (no dedicated turn
  lane), so segregation manifests as through-vs-turn placement across the two shared lanes — same bug class.

## 3. THE FIX TARGET (all `src/Sim.Core/Engine.cs` unless noted)
- **`NetworkModel.ComputeBestLanes`** (called at `Engine.cs:3192` `_network.ComputeBestLanes(route.Edges,
  route.Edges[0], stopOverride)`) — SumoSharp's `getBestLanes` port. It computes, per edge in the route, the
  target lane / `bestLaneOffset` and writes the per-vehicle lane sequence into `_laneSeqPool`. **This is
  where the wrong target lane is chosen.** For a left-turner (route `… BC CSc`, and `BC→CSc` connects only
  from `BC_2`), the ONLY route-continuing lane is `BC_2`, so ComputeBestLanes should assign `BC_2` with the
  minimal future-change cost. It assigns a through lane for ~13% — investigate the backward pass
  (`BackwardPassEdge`, referenced in the `TryStrategicLaneChange` comment ~`Engine.cs:10837`) that propagates
  the required lane back through the route: how it scores lane desirability / breaks ties / handles shared
  lanes (a lane that allows BOTH through and the turn).
- **`TryStrategicLaneChange`** (`Engine.cs:10814`) — steers toward `_laneSeqPool[LaneSeqStart+LaneSeqIndex]`
  (the assigned target). It is faithful to whatever ComputeBestLanes assigned; the bug is upstream in the
  assignment, not here (confirmed at free-flow, where safety checks pass and it still lands wrong).
- **`ResolveBestDepartLane`** (departLane="best" insertion, `Engine.cs:3306`) — where the vehicle is first
  placed; on the arterial the insertion is mostly right (left-turners ~97% on the correct lane on the FIRST
  edge), and the mis-laning appears after crossing the first junction (AB→BC), so the primary suspect is the
  multi-hop backward pass in ComputeBestLanes, not insertion. Verify.
- **`_bestLanesCache`** (`Engine.cs:71`), **`BestLaneLookahead = 3000`** (`Engine.cs:3182`),
  `TryBestLanesForEdge`, `ContinuationLength` — supporting machinery.
- **SUMO reference:** `MSVehicle::getBestLanes` (`/sumo/src/microsim/MSVehicle.cpp`, the `updateBestLanes` /
  the backward loop computing each `LaneQ`'s `length` / `bestLaneOffset` / `allowsContinuation` /
  `bestContinuations`), and `MSLCM_LC2013::wantsChangeStrategic` (the LCA_STRATEGIC desire from
  `bestLaneOffset` + `usableDist`). Port the lane-desirability faithfully.

## 4. HOW TO REPRODUCE / MEASURE (all committed, offline, ~30 s)
```
dotnet build -c Release src/Sim.Sumo/Sim.Sumo.csproj
DLL=src/Sim.Sumo/bin/Release/net8.0/sumosharp.dll
cd scenarios/_repro/arterial-tjunction
# HIGH density (reproduces the knee): compare running/arrived/meanSpeed at t=999
sumo   -c art.sumocfg    --summary-output /tmp/v.xml  --fcd-output /tmp/vf.xml  --no-step-log true
dotnet ../../../$DLL -c art.sumocfg --summary-output /tmp/s.xml --fcd-output /tmp/sf.xml --no-step-log true
# LOW density (the disambiguation): lane-distribution of f_wsc(left) vs f_we(through) on BC (t>=300)
sumo   -c art_lo.sumocfg --fcd-output /tmp/vlo.xml --no-step-log true
dotnet ../../../$DLL -c art_lo.sumocfg --fcd-output /tmp/slo.xml --no-step-log true
```
Numbers to hit (with the fix): `art` SumoSharp running ~303 / arrived ~1094 / meanSpeed ~6.1 (== vanilla);
`art_lo` left-turners on `BC_2` ~99% (from ~87%). Vanilla baselines: `art` 303/1094/6.14; `art_lo` left
100% BC_2.

## 5. PARITY GATES (iron law — this area is golden-DENSE, tread carefully)
1. Full `dotnet test Traffic.sln` green (657 parity + 227 pedestrian); **every committed golden
   byte-identical.** Highest-risk goldens: everything under LANE-CHANGE-OVERLAP, keep-right (rung 8b),
   cooperative-LC / HIGH-DENSITY-P2G2/P2G3, the dense-LC rungs (RungHDp2g2…), and every multi-lane scenario.
   A faithful getBestLanes refinement that stays byte-identical for these is the bar; if it moves any, it is
   NOT byte-identical and must be reworked (or, only with strong SUMO-faithful justification + provenance
   bump + SumoData sign-off, regenerated — but default to byte-identical).
2. Deterministic: two runs identical; serial == `--max-parallelism 8`.
3. Gap-1 synthetic stays 2× 0/290, 1× ≤2 tp; permissive-yield `lt` stays 7; arrival-TL `signalized-asymmetry`
   stays at parity.
4. **Measure at SERVED/SUSTAINED density** (the arterial `art.sumocfg`, or SumoData's pipeline). NEVER judge
   by sparse-probe/one-shot flow — the knee-selection artifact makes a probe-only improvement WORSEN the
   overshoot.

## 6. SUCCESS CRITERIA
- `art_lo.sumocfg`: SumoSharp left-turners on the correct lane ≈ vanilla (~99%, from ~87%).
- `art.sumocfg`: SumoSharp accumulation/discharge/tempo ≈ vanilla (running 462→~303, arrived 858→~1094,
  meanSpeed 2.69→~6.1).
- Parity gates §5 all hold.
- Add a committed anchor test asserting the arterial through-discharge (or the lane distribution) is within
  tolerance of vanilla (offline via SumoShim).
- Hand to SumoData: re-run the real sub-area calibration on the new HEAD; target the 556%/5.5× overshoot
  dropping toward parity (peak_veh_lkm 37→~6). NOTE the honest caveat: the free-flow mis-laning is mild and
  Geneva flagged a possible *additional* discharge component behind the "large stuck bucket" — so this fix is
  warranted and should help, but may not be the COMPLETE knee story. Re-measure to find out; if residual
  remains, localize the next hotspot the same way (per-edge density → FCD-trace → name mechanism).

## 7. DEAD ENDS / do-NOT
- Do NOT chase `ComputeWillPass` / the junction-entry gate as the ROOT — SumoData's original pointer; it is
  the SYMPTOM (through cars stuck behind mis-laned turners), confirmed by the free-flow trace.
- Do NOT chase parking — ruled out by BOTH sessions (SumoSharp under-accumulates at parking;
  `parking-maneuver/FINDINGS.md`). Parking Bug B (FCD parked-emission) was FALSIFIED (SumoSharp emits parked,
  matches golden 70). Parking Bug A (skips full lot) is real but wrong-direction for the knee — a fidelity
  item, not this fix.
- Do NOT tune by sparse-probe/one-shot flow (knee-selection artifact — see §5.4).
- Do NOT relax any lane-change golden to force the number — the fix must be a faithful getBestLanes port that
  stays byte-identical for them.

## 8. Pointers
- Faithful repro + evidence: `scenarios/_repro/arterial-tjunction/FINDINGS.md` (+ committed `net.net.xml`,
  `art.{rou,sumocfg}`, `art_lo.{rou,sumocfg}`).
- Whole-arc map: `docs/CALIBRATION-KNEE-INDEX.md`. SumoData mechanism note (their WillPass framing + the box
  corroboration + the knee-selection-artifact guardrail): the uploaded
  `SUMOSHARP-MECHANISM-3way-tlight-through-discharge.md` (UPDATE 2026-07-22g).
- Lane-change design history: `docs/LANE-CHANGE-OVERLAP-*.md`, `docs/HIGH-DENSITY-P2G2/P2G3-*.md`.
- SUMO: `/sumo/src/microsim/MSVehicle.cpp` (`getBestLanes`/`updateBestLanes`),
  `/sumo/src/microsim/lcmodels/MSLCM_LC2013.cpp` (`wantsChangeStrategic`).
