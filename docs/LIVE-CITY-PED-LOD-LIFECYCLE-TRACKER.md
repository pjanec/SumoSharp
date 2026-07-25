# TRACKER — live-city ped LOD lifecycle

At-a-glance status. Task IDs and success conditions live in `docs/LIVE-CITY-PED-LOD-LIFECYCLE-TASKS.md`; the HOW
in `docs/LIVE-CITY-PED-LOD-LIFECYCLE-DESIGN.md`. Tick a box only when its stated success condition is verified
first-hand (re-run the gate / read the trace), never on a report.

## Stage 0 — baseline + repro (gates everything)
- [ ] T0.1 — confirm iron-law baseline on the clean tree (parity 661/4, bench D96213B7BB4021A7, LiveCity 27/27, Pedestrians green)
- [ ] T0.2 — `PedLodManager.DiagnosticSnapshot` (additive, read-only)
- [ ] T0.3 — `--live-city-pedtrace` headless trace (server-truth + wire-reconstructed pose)
- [ ] T0.4 — capture #3 / #4a / #4b / #6 baselines from the trace → set Stage 2–4 targets

## Stage 1 — design-first checkpoint
- [ ] Owner reviews T0.4 findings; fix variant selected for #3 / #4b / #6 before Stage 2 begins

## Stage 2 — #3 promote flicker
- [ ] T2.1 — seed-on-switch in `HeadlessIg`
- [ ] T2.2 — (conditional) producer first-sample force-publish (only if T2.1 insufficient)
- [ ] T2.3 — whole-run trace invariant: zero invisible/origin frames at any transition

## Stage 3 — #4 stuck-ORCA / wander
- [ ] T3.1 — #4b off-graph route recovery (multi-segment on-graph, no straight-line fallback)
- [ ] T3.2 — #4a leaky dwell + stuck-ORCA watchdog (bounded-time demote guaranteed)

## Stage 4 — #6 idle clustering (LOW PRI)
- [ ] T4.1 — per-ped seeded destination jitter (or pause-spot spread), demo-only, jitter-off byte-identical
- [ ] T4.2 — demand spread test above baseline

## Stage 5 — gate + close
- [ ] T5.1 — full gate re-run + docs synced (design/tasks/tracker, COORDINATION, TASKS-TODO)

---

## Findings log (filled in as Stage 0 runs)

- **Baseline (T0.1):** _pending_
- **#3 (T0.4):** _pending_
- **#4a (T0.4):** _pending_
- **#4b (T0.4):** _pending_
- **#6 (T0.4):** _pending_
