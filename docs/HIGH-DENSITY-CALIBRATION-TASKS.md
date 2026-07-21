# HIGH-DENSITY-CALIBRATION-TASKS.md — work breakdown + success conditions

References `HIGH-DENSITY-CALIBRATION-DESIGN.md` (HOW), `DENSE-FLOW-THROUGHPUT-DIAGNOSIS.md` (Gap 1
evidence). Order = increasing risk: Gap 3 → Gap 2 → Gap 1. Each stage: implement, add anchor, keep full
`dotnet test Traffic.sln` green + goldens byte-identical, then proceed. Branch:
`claude/dense-flow-throughput-diag` (restart from `origin/main` if the diag PR merged).

## Stage 0 — DONE (this session)
Reproduced Gap 1 gridlock on `scenarios/_repro/synthetic-junction2` @2× density (vanilla 0 tp / 290 arr /
drains; SumoSharp 10 tp / 275 arr / ~45 stuck). Localized to wrong-lane strand + no reroute fallback
(clamp at `TryReResolveFromActualLane` ~9080). Ruled out the landed overlap fix as cause. Confirmed Gap 2
(`ResolveParkingAreaStops` static monotonic lot index) and Gap 3 (`ParseDepartPos` rejects `"base"`).

## Stage 1 — Gap 3: departPos="base" (design §4)
Files: `Sim.Ingest/DepartValue.cs`, `Sim.Ingest/DemandParser.cs`, `Sim.Core/Engine.cs` (insertion arm).
- Add `DepartPosSpec.Base` + `DepartPosValue.Base`; accept `"base"` in `ParseDepartPos`.
- Resolve at insertion to SUMO `basePos`: `MIN(vType.Length + PositionEps, lane.Length)`, capped to the
  first stop's endPos when the first stop is on the depart edge.
**Success:**
1. A `departPos="base"` vehicle inserts at SUMO's `basePos` (verify vs a tiny SUMO FCD @1e-3), with and
   without a first-edge stop.
2. Full suite green; every golden byte-identical (no golden uses `"base"` → inert).
3. The 30 box `"base"` vehicles no longer throw at load.

## Stage 2 — Gap 2: parkingArea time-based lot reuse (design §3, Option A)
Files: `Sim.Core/Engine.cs` (`ResolveParkingAreaStops` → runtime lot manager; park/depart hooks),
`Sim.Ingest/ParkingArea.cs` (LotPosition unchanged).
- Per-parkingArea free-lot set; claim lowest-free on park, free on depart (MSParkingArea::computeLastFreePos).
**Success:**
1. A `roadsideCapacity=1` area referenced by ≥2 non-overlapping-in-time vehicles LOADS and both park at
   the correct SUMO lot positions (new anchor + SUMO tripinfo/FCD check).
2. Committed parking goldens 48/66/67/68/69/70 byte-identical (≤capacity simultaneous → lowest-free
   reproduces current static indices).
3. With Gap 3, the full `scenarios/_ped/demo_city/box` LOADS on SumoSharp.

## Stage 3 — Gap 1: reroute-on-wrong-lane (design §2). THE density fix. May take multiple passes.
Files: `Sim.Core/Engine.cs` (`TryReResolveFromActualLane` drop-lane branch; `ExecuteMoveVehicle`; a
plan-phase approach trigger; new `TryRerouteFromDeadLane`).
- Replace the drop-lane clamp with a reroute via a connection the current lane HAS (router to dest); clamp
  only on a true dead end. Add an approach-side trigger so a stalled front vehicle recovers within a step.
**Success:**
1. Dense synthetic (2×): teleports ≈ 0 (was 10), halting drains toward 0 (no permanent ~45-stuck),
   arrivals ≈ vanilla (≈290, was 275).
2. Full suite green + goldens byte-identical (drop-lane clamp not reached by any golden → inert); Sim.Bench
   determinism hash unchanged; serial == region-parallel.
3. New committed anchor: a wrong-lane vehicle reroutes instead of stalling.
4. If reroute alone is insufficient, escalate per design §2.3 (cooperative exit-lane change) — separate
   sub-stage, re-measure.

## Stage 4 — end-to-end calibration validation
- With Gaps 1-3 landed, run the full `demo_city/box` (and, if the SumoData crop pipeline is reachable, a
  crop) on SumoSharp vs vanilla; confirm teleports ≈ 0, arrivals/flow ≈ vanilla, knee within tolerance.
- Update `SUMOSHARP-NEED-*` status / write a hand-off note back to the SumoData session.
**Success:** SumoSharp's calibrated max-density knee matches vanilla within the pipeline's sane band —
SumoSharp becomes a trustworthy calibrate engine, not just serve/run.
