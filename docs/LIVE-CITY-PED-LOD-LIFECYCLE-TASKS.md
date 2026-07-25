# TASKS — live-city ped LOD lifecycle

Work broken into stages. Each task names its **design reference** (a §, not a copy), the **files** it touches,
its **dependencies**, and **mandatory success conditions** (specific assertions / measurable outcomes). Design is
`docs/LIVE-CITY-PED-LOD-LIFECYCLE-DESIGN.md`; the WHAT is the handoff. Tracker:
`docs/LIVE-CITY-PED-LOD-LIFECYCLE-TRACKER.md`.

**Global gates (every stage must keep green):** parity `661/4` byte-identical, bench `D96213B7BB4021A7`
(par==single), `Sim.LiveCity.Tests` `27/27`, `Sim.Pedestrians.Tests` green, no `System.Random`. Confirm the
baseline on the clean tree in **Stage 0** before any change.

---

## Stage 0 — baseline + repro (gates all fixes)

### T0.1 — confirm the iron-law baseline on the clean tree
- **Design ref:** §0, §7.
- **Files:** none (measurement only).
- **Deps:** none.
- **Success:** record actual counts for parity / bench hash / LiveCity / Pedestrians on the untouched branch; they
  match §7 (or the doc is corrected to the measured truth before proceeding).

### T0.2 — `PedLodManager.DiagnosticSnapshot` (additive, read-only)
- **Design ref:** §1.
- **Files:** `src/Sim.Pedestrians/Lod/PedLodManager.cs`.
- **Deps:** none.
- **Success:** new `PedLodDiag` record + `IEnumerable<PedLodDiag> DiagnosticSnapshot(double now)` returns one row
  per ped in ascending id with `Model`, `HighIndexValid`, `StateEnteredAt`, `OutsideSince`, `Pos` matching
  `PositionOf`. Purely additive — `Sim.Pedestrians.Tests` and all gates still green (no behaviour change).

### T0.3 — `--live-city-pedtrace` headless trace
- **Design ref:** §1.
- **Files:** `src/Sim.Viz/Program.cs`.
- **Deps:** T0.2.
- **Success:** `--live-city-pedtrace <out.csv> [steps]` builds the real `LiveCitySim`, honours `LIVECITY_PEDS`,
  and writes per-step/per-ped rows with both **server-truth** (`DiagnosticSnapshot`) and **wire-reconstructed**
  (`PedRemoteReconstructor` off `PedSource`) pose + `visible` + `onGraph`. Runs to completion at
  `LIVECITY_PEDS=1600`; the CSV parses and is non-empty.

### T0.4 — capture the three baselines from the trace
- **Design ref:** §2/§3/§4 root-cause confirmations, §8 open questions.
- **Files:** none (analysis; findings recorded in the tracker).
- **Deps:** T0.3.
- **Success:** trace-measured evidence recorded for: (#3) count of promote frames where the wire pose is
  invisible/origin-snapped; (#4a) whether `OutsideSince` ever holds `dwellSeconds` and whether stuck peds stay
  within demote radius; (#4b) fraction of post-transition routes that are single-segment/off-graph; (#6) idle-spot
  cluster count + a spread metric. These numbers become the pass/fail targets for Stages 2–4.

---

## Stage 1 — nothing to implement until Stage 0 findings are reviewed

The exact fix variant for #3 (consumer seed vs +producer), #4b (how load-bearing the recovery is), and #6
(destination vs pause spot) is **selected from T0.4**. Owner reviews T0.4 findings before Stage 2 begins. (This is
the design-first checkpoint, not a code stage.)

---

## Stage 2 — #3 promote flicker

### T2.1 — seed-on-switch in `HeadlessIg`
- **Design ref:** §2 (primary fix).
- **Files:** `src/Sim.Pedestrians/Lod/HeadlessIg.cs`.
- **Deps:** T0.4.
- **Success:** on `Apply(DrSwitchEvent{To:FreeKinematic})` with no prior FreeKinematic sample, the ped's
  reconstructed pose immediately equals its low-power pose at `s.Time` (unit test: promote, withhold the first
  sample, assert `Reconstruct`/`ReconstructSample` is on-body and `visible`). No change to any low-power or
  post-first-sample reconstruction (existing `PedLodManagerTests` server==IG tests unchanged).

### T2.2 — (conditional) producer first-sample force-publish
- **Design ref:** §2 (fallback).
- **Files:** ped publish scheduler (only if T0.4 shows the seed is insufficient — confirm the file is in
  `src/Sim.Pedestrians/Lod/` before touching; if it is in shared `Sim.Replication`, coordinate/avoid).
- **Deps:** T2.1 measured insufficient.
- **Success:** the first `FreeKinematicSample` after a promote is never deferred; trace shows zero
  invisible/origin frames at switch. Skipped entirely if T2.1 already achieves that.

### T2.3 — #3 whole-run trace invariant
- **Design ref:** §2 success condition.
- **Files:** trace analysis + a test asserting it on a scripted promote.
- **Deps:** T2.1.
- **Success:** over the 1600-ped trace, every promote/demote frame has `visible==true` and wire pose within one
  ped-step of server truth — **zero** gap frames.

---

## Stage 3 — #4 stuck-ORCA / wander

### T3.1 — #4b off-graph route recovery (demote + promote apply)
- **Design ref:** §3.1.
- **Files:** `src/Sim.Pedestrians/Lod/PedLodManager.cs`.
- **Deps:** T0.4.
- **Success:** when `FindPath(pos, Destination)` is null, the applied route is multi-segment and on-graph
  (recovered onto the retained `e.Path` via nearest-vertex + tail splice), starting exactly at `pos`
  (`ReanchorAt` no-pop preserved). New unit test: force a null-FindPath demote from an off-graph pose, assert the
  resulting `PathArc` path has ≥3 vertices and lies on the nav graph (not the 2-point straight line). Existing
  demote test (`Demotion_ReattachesFreshPathArc_...`) still green.

### T3.2 — #4a leaky dwell + stuck-ORCA watchdog
- **Design ref:** §3.2.
- **Files:** `src/Sim.Pedestrians/Lod/PedLodManager.cs`.
- **Deps:** none (independent of T3.1; land after for coherent review).
- **Success:** (a) existing `Demotion_ReattachesFreshPathArc_...` and `Demotion_DoesNotFlap_...` both still green
  (leaky accumulator ≠ regression). (b) New test: a ped drifting net-outward while clipping the demote edge every
  other step **demotes** within a bounded time. (c) New test: a ped held only by the hysteresis band (inside
  demote, outside every promote radius) force-demotes by `MaxHighPowerSeconds`; a `ForcedHighPower` ped does not.
  (d) Trace: zero peds FreeKinematic while > demoteRadius from the zone for longer than `MaxHighPowerSeconds`.

---

## Stage 4 — #6 idle clustering (LOW PRI)

### T4.1 — per-ped seeded destination jitter (or pause-spot spread, per T0.4)
- **Design ref:** §4.
- **Files:** `src/Sim.Pedestrians/Demand/PedDemand.cs` (`DestJitterSalt`, `PedDemandConfig.DestinationJitterRadius`),
  `src/Sim.LiveCity/LiveCitySim.cs` (wire the demo-only knob).
- **Deps:** T0.4 (cluster attribution).
- **Success:** with jitter off (default 0) `PedDemand` is byte-identical to today (determinism test:
  identical spawn/arrival events + trajectories vs a pre-change run). With jitter on, two runs at the same seed
  produce identical layouts (seeded determinism), and the trace's idle-spread metric is materially above the
  T0.4 baseline. No `System.Random`; the new salt never perturbs existing streams (jitter-off draws nothing on it).

### T4.2 — demand spread test
- **Design ref:** §4 success condition.
- **Files:** `tests/Sim.Pedestrians.Tests`.
- **Deps:** T4.1.
- **Success:** a unit test builds a small demand with jitter on and asserts the idle/arrival spread metric exceeds
  the jitter-off baseline by the target margin, deterministically across two runs.

---

## Stage 5 — gate + close

### T5.1 — full gate re-run
- **Design ref:** §7.
- **Files:** none.
- **Deps:** all above.
- **Success:** parity `661/4`, bench `D96213B7BB4021A7`, `Sim.LiveCity.Tests` `27/27`, `Sim.Pedestrians.Tests`
  green (incl. all new tests). Each stage's trace target met. Docs (design/tasks/tracker + COORDINATION +
  TASKS-TODO) updated to reflect what shipped.
