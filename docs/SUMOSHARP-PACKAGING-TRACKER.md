# SUMOSHARP-PACKAGING-TRACKER.md — at-a-glance to-do

Checklist for the packaging rethink. Task IDs → `SUMOSHARP-PACKAGING-TASKS.md`; design →
`SUMOSHARP-PACKAGING-DESIGN.md`. A box is ticked only when the task's success conditions are
verified first-hand (build / `dotnet pack` / `dotnet test`), per the CLAUDE.md accept gate.

## Baseline (integrated this session)
- [x] Fast-forwarded the Windows-GPU viewer branch, then rebased onto updated `main` repeatedly as it
      advanced (DR-error publishing, lane-change smoothing as-built, the viewer demo tool, and the
      P2/P3 viewer work below all landed on main).
- [x] DR/smoothing reimplementation guide present: `SUMOSHARP-VIEWER-DR-SMOOTHING.md` (+ §10 as-built,
      lane-change design/tasks, DR-motion-jitter investigation).
- [x] Offline parity gate green, verified first-hand after the latest rebase:
      **451 passed, 0 failed, 3 skipped**; `Sim.Bench` determinism hash `909605E965BFFE59`
      (single + parallel) unchanged.

## Stage P0 — Reconcile docs with reality
- [x] P0.1 — Packaging design/tasks/tracker docs landed.
- [x] P0.2 — `SUMOSHARP-API.md §1` points here; two-package reality + retired `Runtime` recorded.

## Stage P1 — Replication transport contract + neutral sample (D8, D9)
- [ ] P1.1 — `IReplicationSink`/`IReplicationSource` (4-channel contract) added in `Sim.Replication`.
      **(OUTSTANDING — packaging branch owns this.)**
- [x] P1.2 — `TimestampedSample` + `IVehicleSampleHistory` in `Sim.Replication` (+ history test).
      **Landed on main via P2-A (`0d85486`)**, shape matching `PublishScheduler`'s ref field set.
- [ ] P1.3 — `Sim.Replication.Dds` refactored to *implement* the contract. **(OUTSTANDING.)**
- [ ] P1.4 — in-memory-transport round-trip test proves a second binding. **(OUTSTANDING.)**

## Stage P2 — `SumoSharp.Viewer.Motion`  ✅ COMPLETE (on main)
- [x] P2.1 — `DrClock` decoupled from `DdsSubscriber` onto the neutral sample/history (`5a32a3e`);
      straight + junction-straddle + lateral-straddle regression tests.
- [x] P2.2 — `SumoSharp.Viewer.Motion` created (`9f05688`): net8+ns2.1, `IsPackable`, refs only
      Core/Ingest/Replication, `DrPoseSmoother` extracted verbatim; packs `lib/net8.0` +
      `lib/netstandard2.1` (verified first-hand).
- [x] P2.3 — DR/smoothing guide shipped as the package README + license/disclaimer (`22668ce`).

## Stage P3 — Generic viewer + demo-tool separation (D5, D10)
- [x] P3.1 — render-overlay seam `IRenderOverlay` (+ marker test) on the generic viewer (`cc12e87`).
- [x] P3.2 — demo/evac relocated out of `Sim.Viewer.Core`; `→ Sim.Evac` edge moved to the demo layer;
      evac drawn via the seam (`187f57d`). `Sim.Viewer.Core` is generic again (no `Sim.Evac` ref,
      verified).
- [ ] P3.3 — package the **generic** viewer as `SumoSharp.Viewer.Raylib` (native leaf; NO evac/demo);
      demo tool stays a sample. **(OUTSTANDING — depends on P2 ✔; ready to do.)**

## Stage P4 — Dev-time & domain packages
- [ ] P4.1 — `SumoSharp.Testing` from `Sim.Harness`.
- [ ] P4.2 — `SumoSharp.Evac` from `Sim.Evac`.

## Stage P5 — Convenience & CI
- [ ] P5.1 — `SumoSharp` meta-package (Core + Ingest + Replication + Viewer.Motion).
- [ ] P5.2 — packaging guard test extended (targets/packability, no-native-leak,
      contract-in-Replication, no-evac-in-viewer-package).
- [ ] P5.3 — publish CI packs the full shipped set on a `v*` tag.

## Already shipped before this session (context)
- [x] `SumoSharp.Core`, `SumoSharp.Ingest` — packable, net8+ns2.1, publish CI, B13 guard.
- [x] `SumoSharp.Replication`, `SumoSharp.Replication.Dds` — packable.
- [x] `SumoSharp.Viewer.Motion` — packable (this session, on main).
