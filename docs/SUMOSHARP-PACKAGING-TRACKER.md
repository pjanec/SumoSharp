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

## Stage P1 — Replication transport contract + neutral sample (D8, D9)  ✅ COMPLETE
- [x] P1.1 — `IReplicationSink`/`IReplicationSource` (4-channel contract) + `LifecycleRecord` in
      `Sim.Replication` (`IReplication.cs`); references only data-model types (`24f8760`).
- [x] P1.2 — `TimestampedSample` + `IVehicleSampleHistory` in `Sim.Replication` (landed via P2-A).
- [x] P1.3 — DDS implements the contract: `DdsSubscriber : IReplicationSource` (surface already
      matched) and `DdsPublisher : IReplicationSink` (encode+write extracted; byte-identical writes).
      (These classes live in `Sim.Viewer.Core`; the contract is defined in `Sim.Replication`.)
- [x] P1.4 — `InMemoryReplicationBus` (a non-DDS binding) + `ReplicationInMemoryTransportTests`
      hermetic round-trip proves a second transport. Verified: parity 457/0/3, hash unchanged.

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
- [x] P3.3 — packaged the **generic** viewer as `SumoSharp.Viewer.Raylib` (native leaf, net8.0;
      Renderer/RoadLayerCache/FrameStats/IRenderOverlay/MarkerOverlay/DdsSubscriber/
      DdsGeometryLaneSource + `RenderHelpers`, → `Viewer.Motion` + `Replication.Dds` only; TTF + README
      packed). `DdsQos` → `Replication.Dds`; `LoopbackSelfTest` → demo exe; the EngineHost/DDS-command
      control panels → demo (`ViewerControlsPanels`); `DrawDynamicWorld` takes obstacle points, not
      `EngineHost`, so the package carries **no** `Sim.Viewer.Core`/evac/demo dependency. `dotnet pack`
      → `SumoSharp.Viewer.Raylib.0.1.0.nupkg` (lib/net8.0 + font + README; deps = Motion/Replication.Dds/
      Raylib-cs/rlImgui-cs). `dotnet test` 465/0, bench hash unchanged; publish.yml packs it; guard fact
      added. **GPU-verified the raylib window renders** (local scenario, loopback DR, evac demo via the
      overlay seam, DemoCatalog picker) — the handoff step the headless session couldn't do. (`b4f69a2`.)
      Follow-up (Option A, own stage): extract the generic render *loop* into a reusable `ViewerHost` API.

## Stage P4 — Dev-time & domain packages  ✅ COMPLETE
- [x] P4.1 — `SumoSharp.Testing` from `Sim.Harness` (packs lib/net8.0 + README).
- [x] P4.2 — `SumoSharp.Evac` from `Sim.Evac` (packs lib/net8.0 + README; parity unchanged).

## Stage P5 — Convenience & CI  ✅ COMPLETE
- [x] P5.1 — `SumoSharp` meta-package: bundles Core + Ingest + Replication (Viewer.Motion left
      opt-in, un-opinionated). Nuspec deps verified, no lib.
- [x] P5.2 — `PackagingLayoutTests.cs`: 7 hermetic guards (targets/packability/PackageIds,
      no-native-leak in Viewer.Motion, contract-in-Replication, no-evac-in-viewer-core, meta bundle).
      Parity 464/0/3.
- [x] P5.3 — publish CI packs the full shipped set on a `v*` tag; pack loop validated locally (all 8
      packages pack). Fixed a latent NU5039 (missing/unpacked README) on Replication + Replication.Dds.

## Already shipped before this session (context)
- [x] `SumoSharp.Core`, `SumoSharp.Ingest` — packable, net8+ns2.1, publish CI, B13 guard.
- [x] `SumoSharp.Replication`, `SumoSharp.Replication.Dds` — packable.
- [x] `SumoSharp.Viewer.Motion` — packable (this session, on main).
