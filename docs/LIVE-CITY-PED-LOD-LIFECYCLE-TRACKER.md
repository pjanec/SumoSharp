# TRACKER — live-city ped LOD lifecycle

At-a-glance status. Task IDs and success conditions live in `docs/LIVE-CITY-PED-LOD-LIFECYCLE-TASKS.md`; the HOW
in `docs/LIVE-CITY-PED-LOD-LIFECYCLE-DESIGN.md`. Tick a box only when its stated success condition is verified
first-hand (re-run the gate / read the trace), never on a report.

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
- [ ] T3.1 — #4b off-graph route recovery — OPTIONAL cheap hardening only (straight-line fallback is 0.2% /
  3 peds; genuine but tiny). Keep or skip per owner.
- [x] ~~T3.2 — #4a leaky dwell + watchdog~~ — DROPPED (evidence: no stuck-ORCA reproduces at 400 or 1600, static
  or moving zone; demotion is correct). Pending owner ratification.

## Stage 4 — #6 idle clustering (LOW PRI)
- [ ] T4.1 — per-ped seeded destination jitter (or pause-spot spread), demo-only, jitter-off byte-identical
- [ ] T4.2 — demand spread test above baseline

## Stage 5 — gate + close
- [ ] T5.1 — full gate re-run + docs synced (design/tasks/tracker, COORDINATION, TASKS-TODO)

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
