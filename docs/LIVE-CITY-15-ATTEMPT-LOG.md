# Issue #15 residual — ATTEMPT LOG (running journal)

Purpose: a dated, honest journal of what was tried for the live-city junction-jam residual, what the
evidence said, what worked, and **what failed and why** — so a future session does not repeat a dead
end. Append newest entries at the bottom. This is the scratch/decisions trail; the polished diagnosis
lives in `LIVE-CITY-15-RESIDUAL-FINDINGS.md`, the repro in `LIVE-CITY-15-RESIDUAL-REPRO.md`.

Branch: `claude/livecity-15-turnlane-segregation` (off the merged live-city tip incl. `184fb31`).
Iron rule throughout: `Sim.ParityTests` stays **657/4** byte-identical (or a change is gated behind an
explicit fast-mode flag); bench determinism (serial == parallel) holds.

---

## 2026-07-23 — Repro + engine merge (context, already landed on the live-city line)
- Built the headless `LIVECITY-GRIDLOCK` probe (`--mode live-city --smoke`); reproduced the pre-merge
  **terminal** gridlock (stoppedFrac → 0.94, arrivals 38/200 s).
- Merged `claude/dense-lane-overlap-fix-5tr4ha` wholesale → terminal-lock became **jam-and-recover**
  (arrivals 81, stoppedFrac oscillates 0.38–0.73). Parity 654→657/4, bench hash unchanged. **Helped,
  not cured** (GPU verdict: jams still look unrealistic).

## 2026-07-23 — Witness: ground-truth on the residual stalls
- Added `LiveCitySim.WitnessAuthoritative()` + `LIVECITY_WITNESS=1` dump (engine-authoritative per-car
  lane/pos/posLat/speed/TL/gap). Read-only, host-side.
- **Findings that reframed the problem:**
  - `posLat = 0` for every stuck car → the GPU "lateral float" is a **render/DR artifact**, not engine
    motion. Do NOT chase a lateral-jockey bug in the engine.
  - Most stopped cars are at **red** or **behind a stopped leader** (consequence of a jam). The anomaly
    is `stuckOnGreenClear` (7–16 cars): speed≈0, TL green (incl. protected `G`), no leader within 15 m,
    at pos≈226–233 (the stop line). These block the queues behind them.
- Code + `/sumo/` analysis (subagent): getBestLanes offsets are CORRECT and keep-right rule 2 is landed
  → **"wrong turn-lane selection" hypothesis FALSIFIED**. Root localized to **missing LCA_URGENT
  strategic-change cooperation**: `TryStrategicLaneChange` (`Engine.cs:11048-11053`) does a bare
  `return false` when the turn lane is blocked; no ego brake-to-wait, no follower gap-opening. Turner
  never merges → strands at stop line → `Speed=0` clamp (`Engine.cs:9587-9611`).

## OPEN QUESTION raised by owner (2026-07-23) — MUST verify before designing
**"How does telling the follower to slow solve a car already stopped on green at a clear junction?"**
Valid skepticism. The link is INDIRECT (see the explanation section below): cooperation is **upstream
prevention**, not stop-line release. It stops turners from *arriving* stranded in the wrong lane; it does
NOT release a car already stranded.
- **Therefore, before committing to option (1) as the fix, verify the causal chain per stuck car:** does
  each `stuckOnGreenClear` car sit in a lane whose connections do NOT include its next route edge
  (= wrong-lane strand, which cooperation prevents), or does its lane DO connect but it is blocked for
  another reason (junction exit occupied / keep-clear / RoW)? Plan: extend the witness to print, per
  stuck car, whether `currentLane` has a connection to the car's next route edge, and the occupancy of
  the intended next (across-junction) lane. If most are NOT wrong-lane strands, option (1) is the wrong
  lever and we re-target. **Do not design the cooperation port until this split is measured.**

## Attempts that FAILED / were abandoned (avoid repeating)
- **Standalone `tools/livecity-repro/` console harness** — abandoned: the built-in `--mode live-city
  --smoke` path already runs the identical LiveCitySim loop; a separate project just duplicated it.
  Use `--smoke` (+ `LIVECITY_WITNESS=1`), not a new csproj.
- **Component-2 (LaneQ `occupation`/`maxJam`) on the dense-flow branch** — designed (`ad8d738`),
  implemented, measured **low-ceiling (~15% recovery) and entangled with the merge-in lever, then
  REVERTED** (`965fc45`). Do NOT re-attempt occupation-based urgency before the cooperation/merge-in
  lever; it was already shown ineffective in isolation.
- **"Turn-lane mis-segregation via getBestLanes"** as the root — FALSIFIED (offsets correct, rule 2
  landed). Don't re-open the getBestLanes offset port.

## 2026-07-23 — MEASUREMENT RESULT: option (1) REFUTED as the primary lever
Added a decisive, parity-neutral diagnostic counter `Engine.StrandedOffRouteThisStep` (Interlocked,
reset per step) at the EXACT wrong-lane dead-end clamp site (`Engine.cs` ExecuteMoveVehicle, the
`Pos=laneLength; Speed=0` branch reached only when the car is off its pool lane AND its lane has no
onward connection to its next route edge). Surfaced via `LiveCitySim.StrandedOffRouteLastStep` +
`LIVECITY-WITNESS`. **Parity verified byte-identical: `Sim.ParityTests` 657/4, bench hash
D96213B7BB4021A7 unchanged (a counter never read by sim logic cannot move a trajectory).**

Result (cap 160):
```
t     stuck  stuckOnGreenClear  stuckRed  stuckBehindLeader  strandedDeadEnd
 80    122         12             92          18                0
120     80         12             62           6                3
180     83         16             24          43                3
200     47          7             36           4                3
```
- **`strandedDeadEnd` = 0–3 at any instant** — the wrong-lane dead-end strand that URGENT cooperation
  would PREVENT is barely occurring. It cannot explain a 47–122-car jam.
- Corroborating: the `stuckOnGreenClear` example lanes/cars **turn over** between checkpoints (not the
  same cars stuck forever), and `arrivals` keep climbing — so those stalls are **temporary junction
  holds**, not permanent dead-end strands.
- **CONCLUSION: option (1) (URGENT LC cooperation for turn-lane merge) is NOT the right lever for the
  residual.** It would fix ≤3 cars. The owner's skepticism ("I don't see the link") was correct.
- **DO NOT build the URGENT-cooperation port for #15.** (The mechanism-gathering pass and the
  determinism-safe shape it found — the retired `CoopSpeedAdvice`/`afec614` machinery — are recorded
  below for the record, but are shelved unless a future finding actually needs them.)

## Re-diagnosis: what the residual actually is (hypothesis, to verify)
Dominant stuck categories are **red-light queuing** (`stuckRed`) + **queue propagation**
(`stuckBehindLeader`) + a small set of **holds on green** (`stuckOnGreenClear`, NOT dead-end) —
consistent with junction **keep-clear / right-of-way** conservatism and normal signalized-grid
congestion. Open question the witness cannot answer alone: **is this a REAL throughput deficit vs
vanilla SUMO, or just a dense signalized grid where 40–70% stopped-at-instant is normal?**
- **NEXT STEP (authoritative): SUMO cross-check.** SUMO 1.20.0 is available. Run vanilla SUMO on the
  same `net.xml` + comparable demand; diff tripinfo/summary (arrivals, mean speed, timeLoss) against our
  engine on the identical scene. If SUMO clears far more, the deficit is real and localizable (which
  junctions / movements); if SUMO is similar, the "jam" is largely normal dense-grid behaviour and the
  fix is demo-tuning (lower density / signal timing), not an engine bug. This is investigation only —
  the offline `dotnet test` loop never invokes SUMO.

## Retired-machinery note (shelved, for the record — NOT to build now)
Mechanism-gathering found the follower-cooperation channel was already built and RETIRED in `afec614`
("Retire the cooperative informFollower"): `VehicleRuntime.CoopSpeedAdvice` (+∞ default) +
`CommandBuffer.SpeedAdvice(follower, speed)` applied as a commutative MIN in Flush, consumed one step
later in `ComputeMoveIntent` — a determinism-safe shape (recoverable via `git show afec614`). Ego
brake-to-wait template = the `WaitingTime` self-write/next-step-read pattern (`VehicleRuntime.cs`).
Its retired failure mode: organic-net follower over-braking for OPTIONAL overtakes. Shelved.
