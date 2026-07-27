# TRACKER — live-city ped LOD lifecycle

At-a-glance status. Task IDs and success conditions live in `docs/LIVE-CITY-PED-LOD-LIFECYCLE-TASKS.md`; the HOW
in `docs/LIVE-CITY-PED-LOD-LIFECYCLE-DESIGN.md`. Tick a box only when its stated success condition is verified
first-hand (re-run the gate / read the trace), never on a report.

## Post-merge with main (PR#13, junction-gridlock + gates-default-ON)
Merged `origin/main` in. New baselines (all re-verified on the merged tree): parity **755/4-skip (759)** with all
661 goldens byte-identical, bench **`BF3794A4704BCD79`** (par==single; hash moved with PR#13's gates-ON, not us),
`Sim.LiveCity.Tests` **50/50**, `Sim.Pedestrians.Tests` **277/277**. Conflicts were only in docs (resolved).
Crowd realism follow-ups shipped after the core fixes: crosswalk diagonal **weave** (opposing streams keep their
side), a **2-D waiting blob**, and per-ped **speed variation** (`SpeedVariationFrac`, demo 0.15) so moving groups
spread. **Test-isolation fix:** disabled xunit parallelization for `Sim.LiveCity.Tests`
(`TestParallelization.cs`) — main's PR#13 probes set process-global `LIVECITY_*` env vars without reset, which
raced our throughput test's config (same config gave 431/707/718); the demo itself is healthy (707 arrivals at
speed=0.15, ≫450 threshold), the flake was purely the env race. Green 3/3 after the fix.

## Stage 0 — baseline + repro (gates everything)
- [x] T0.1 — iron-law baseline confirmed on the clean tree: parity 661/4-skip (657 pass), bench D96213B7BB4021A7 (par==single), LiveCity **43/43**, Pedestrians 272/272 (handoff/COORDINATION "27"/"25" are stale)
- [x] T0.2 — `PedLodManager.DiagnosticSnapshot` (additive, read-only) — done; clean passthrough `LiveCitySim.PedLodDiagnostics` added (no reflection)
- [x] T0.3 — `--live-city-pedtrace` headless trace (server-truth + wire-reconstructed pose) — done; Pedestrians 272/272 + LiveCity 43/43 still green
- [x] T0.4 — baselines captured (LIVECITY_PEDS=400, 400 steps, Dt=0.5); findings below → Stage 2–4 targets set

## Stage 1 — design-first checkpoint
- [ ] Owner reviews T0.4 findings; fix variant selected for #3 / #4b / #6 before Stage 2 begins

## Stage 2 — #3 promote flicker
- [x] T2.1 — seed-on-switch in `HeadlessIg` — origin-snap eliminated (near-origin 2326 → 0).
- [x] T2.2 — **producer crowd-frame de-fragmentation** (`PedLodManager.Step`): the real root was NOT the bandwidth governor (it never truncates — verified `governed=False`/`deferred=0`) nor a stale (0,0) baseline. It was that the final publish loop **interleaved** FreeKinematicSamples with low-power Heartbeats, so `PedReplicationPublisher` fragmented one step's crowd into several frames and the receiver (latest-frame-only) froze every ped but the last fragment. Fix: emit all samples contiguously, then all heartbeats — restores the publisher's documented "consecutive samples ⇒ one crowd frame/step" contract. New test `Step_EmitsFreeKinematicSamplesContiguously_...`.
- [x] T2.3 — whole-run trace invariant met: **freeKinematicWireMismatches 3627 → 0**; every FreeKinematic row tracks within **max 0.28 m** (p50/p90 = 0.20 m, just the 0.15 s playout lag). Pedestrians 274/274, LiveCity 43/43.

## Stage 3 — #4 stuck-ORCA / wander  — REDIAGNOSED (mostly subsumed by the #3 fix)
- [x] Moving-zone repro added to `--live-city-pedtrace` (`moving` arg sweeps `SetLcRealismZone` in a circle
  — camera-Follow). Even so, at **both 400 and 1600 peds** the max consecutive run of a ped FreeKinematic
  while beyond the demote radius is **3 steps** (~dwellSeconds) — demotion works. Demoted peds rejoin proper
  multi-segment on-graph routes (routeVtx 6–13). After the #3 fix, low-power wire fidelity is **0.16 m mean /
  0.48 m max** at 1600 peds — the "wandering ORCA that won't switch back" was the #3 wire-fragmentation bug
  showing moving/demoted peds as frozen-then-extrapolating. **#4a leaky-dwell/watchdog is NOT needed** (would
  be machinery for a non-existent server bug). ← owner decision requested before dropping formally.
- [x] T3.1 — #4b off-graph route recovery — DONE (owner: "do it"). `PedLodManager.RecoverRoute`: when
  `FindPath` returns null, recover onto the ped's retained on-graph route (nearest vertex + splice) instead of
  the straight-line beeline; used at both promote and demote. Behaviour changes only on the null path (rare),
  so the common path is byte-identical. Test `Demote_WhenFindPathReturnsNull_RecoversOntoRetainedRoute_NotStraightLine`.
  Pedestrians 277/277, LiveCity 43/43.
- [x] ~~T3.2 — #4a leaky dwell + watchdog~~ — DROPPED (owner ratified). Evidence: no stuck-ORCA reproduces at
  400 or 1600 peds, static or moving zone, up to 250 s; demotion is correct; the visible wander was #3.

## Stage 4 — #6 idle clustering  — DONE (crosswalk-wait spread, not dest jitter)
- [x] T4.1 — per-ped seeded **crosswalk-wait BLOB + diagonal cross** (`PedDemand.SplitWalkAtCrossings`, owner-chosen
  "Option B"): a waiting ped steps into a seeded 2-D spot in a dense blob at the kerb (lateral along the kerb
  ±`CrosswalkWaitSpreadRadius` AND back into the sidewalk 0..R — "a circle at the end of the rod"), waits, then
  crosses DIAGONALLY straight from its spot to the crossing exit (no step-back; the diagonal consumes the crossing
  sub-segment so `segStart`→i+1). Opt-in (`PedDemandConfig.CrosswalkWaitSpreadRadius`, 0=off=byte-identical;
  `LiveCityConfig.ForRepoRoot`=2.0, demo only). New `WaitJitterSalt` (no `System.Random`). Result: the original 23
  peds stacked on ONE exact point → a dense blob; **busiest 0.5 m cell 2.5%**, worst hotspot = 17 peds over 18
  distinct spots. #3 still clean (0 wire-mismatches). Pedestrians 277/277, LiveCity 43/43.
- [x] T4.2 — tests: determinism (byte-identical at radius=2.0) + OFF=plain-pause / ON=enter-blob + diagonal-cross
  geometry (`CrosswalkSignalComplianceTests`). Isolated the throwaway junction-turn probe (`PedBackstepProbeTests`)
  from the spread (spread=0 there — the junction-turn window is backstep-free).

## Stage 5 — gate + close
- [x] T5.1 — full gate re-run GREEN: parity **661/4-skip (657 pass)**, bench **D96213B7BB4021A7** (par==single),
  LiveCity **43/43**, Pedestrians **276/276**. Design/tasks/tracker synced. (COORDINATION/TASKS-TODO sync +
  owner ratification of the open items below still pending.)

## Open items awaiting owner
- ~~Drop #4a~~ — RATIFIED (dropped).
- ~~#4b~~ — RATIFIED (done).
- ~~#6 crossing style~~ — RESOLVED: owner chose **B (blob + diagonal)**, implemented. Optional future tweak:
  spread the far-side LANDING too (currently peds converge diagonally toward the exit vertex and disperse via
  onward routes — they cross while moving, so no stationary far-side cluster forms).

---

## Findings log (filled in as Stage 0 runs)

- **Baseline (T0.1):** parity 661 total / 4 skipped / 657 pass; bench `D96213B7BB4021A7` (par==single); LiveCity 43/43; Pedestrians 272/272. (Handoff said 27/27, COORDINATION 25/25 — both stale.)
- **#3 (T0.4) — CONFIRMED, the dominant bug.** Of 67 promotions, **44 promoted peds** render at the **world
  origin (0,0)** on the wire (culled from the crop → "vanish"), persisting **avg 82 / max 149 steps** each — 3627
  of 7203 FreeKinematic rows (50%) are >10 m off body, 64% ~exactly at origin. Root: the `DrSwitchEvent` IS
  delivered (IG flips to FreeKinematic) but the first `FreeKinematicSample` is not on the wire, so IG `LastPos`
  stays `default(Vec2)=(0,0)`. `not-on-wire=0`, `invisible=0` (FreeKinematic is always "visible" so the demo
  crop-culls the origin pose instead of hiding it). Fix: consumer seed-on-switch (in-surface); the persistence
  says the producer also under-publishes the promoted ped's samples — `PedReplicationPublisher`/
  `PedPublishScheduler` are in `src/Sim.Pedestrians/Lod/` (my surface), so a producer force-first-sample is
  available too.
- **#4a (T0.4) — UNDER-REPRODUCED with the static zone.** With the static central pocket, peds demote fine: only
  27 peds ever exceed the 100 m demote radius and the max FK-time beyond it is **3 steps** (they demote promptly).
  So #4a "stuck ORCA" is a **moving-zone (camera Follow) phenomenon** — `OutsideSince` resets as the zone sweeps
  — which the static-zone trace does not exercise. Needs a moving-zone repro (drive `SetLcRealismZone`) before
  the leaky-dwell/watchdog fix is validated.
- **#4b (T0.4) — RARE.** Straight-line fallback (`routeVertexCount==2`) is only **3% of FK rows / 3 peds** —
  `SumoNavMesh.FindPath` evidently projects off-mesh poses, so null is uncommon (open-Q2 answered). Off-graph
  recovery is a low-frequency safety net, not the main wander source. Lower priority than first framed.
- **#6 (T0.4) — CONFIRMED.** 224 paused peds, but the **busiest single 5 m cell holds 22 distinct peds and 19%
  (2312/12199) of all idle rows**; the top two adjacent cells ≈ 27% — a clear junction funnel ("merge to one
  point and idle"). Baseline spread: ~66–70 occupied 5 m cells, busiest-cell share 19%. Fix target: cut
  busiest-cell share, raise occupied-cell count, via per-ped seeded destination jitter.
