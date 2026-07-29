# SUMOSHARP-PACKAGING-TRACKER.md — at-a-glance to-do

Checklist for the packaging rethink. Task IDs → `SUMOSHARP-PACKAGING-TASKS.md`; design →
`SUMOSHARP-PACKAGING-DESIGN.md`. A box is ticked only when the task's success conditions are
verified first-hand (build / `dotnet pack` / `dotnet test`), per the CLAUDE.md accept gate.

## Stage V — collapse to the adoption-first 2-package set (CURRENT; supersedes P0–P5 on count)

Baseline at plan time (post 544-commit main integration, verified first-hand): `dotnet test
tests/Sim.ParityTests` = **777 passed / 0 failed / 4 skipped**; `Sim.Bench` determinism
`single == parallel` (hash `BF3794A4704BCD79`, new-main value — engine changed over main; packaging is
inert to it).

### Batch 1 — the package collapse  ✅ COMPLETE (verified first-hand)
- [x] V1.1 — `SumoSharp` bundle package: one nupkg, `lib/net8.0` + `lib/netstandard2.1` each carry all
      8 engine DLLs; ns2.1-only deps (`System.Memory`, `System.Text.Json`) as package deps; no native
      leak. Contents inspected.
- [x] V1.2 — `Sim.Evac` multi-targets `net8.0;netstandard2.1` (one ns2.1 fix: `Enum.GetValues<T>()` →
      `Enum.GetValues(typeof)`); builds both TFMs.
- [x] V1.3 — `SumoSharp.Replication.Dds` → **`SumoSharp.Dds`**; nuspec depends on `SumoSharp` +
      `CycloneDDS.NET`, packs only its own DLL (no engine duplication). Inspected.
- [x] V1.4 — the 8 engine projects + raylib viewer + harness no longer packable; `SumoSharp.Meta`
      removed, id reused for the bundle.
- [x] V1.5 — `pack-check.yml` / `publish.yml` pack the 2 packages, assert count == 2.
- [x] V1.6 — `PackagingLayoutTests` rewritten (5 hermetic guards): exactly `{SumoSharp, SumoSharp.Dds}`
      packable; bundle portable + native-free + lists the 8 engine projects; DDS native/net8-only +
      depends on the bundle; contract-in-Replication; every engine project multi-targets + native-free.
- [x] V1.7 — `demos/City3D` re-pointed to `SumoSharp` (+ `SumoSharp.Dds` remote); `nuget.config`
      pattern `SumoSharp*`; `build.sh` packs the bundle. **CityLib builds against the bundle** (real
      consumer).
- [x] V1.8 — docs to the one-install story: `PACKAGES.md` (2-node graph + "what's inside" + install),
      `README`, `SUMOSHARP-API.md §1`, `demos/City3D/README.md`. Retired-id grep over the consumer docs
      returns empty (verified).
- [x] Iron law after Batch 1: `dotnet test` **773 passed / 0 failed / 4 skipped** (total 781→777 only
      because the guard refactor replaced 9 test cases with 5); determinism `single == parallel`
      unchanged.

### Batch 2 — viewers as repo apps  ⏳ PENDING (Batch-2 scope Q open with user)
- [ ] V2.1 — "build & watch" doc (2D + 3D) linked from README.
- [ ] V2.2 — 2D viewer strong net/scenario CLI (exact scope: full `--sumocfg` demand vs built-in — TBC).
- [ ] V2.3 — demo run-scripts for each viewer on committed demo scenarios.

---

## (HISTORICAL) à-la-carte 10-package plan — baseline & stages P0–P5

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
- [x] P3.3 follow-up (Option A) — extracted the byte-for-byte-common render *loop* into the package as
      `ViewerHost.Run(ViewerHostConfig)`: window/font bootstrap, the 60 fps loop, the Camera2D
      pan/zoom/click state machine, the 'D' toggle, resize, the fixed BeginDrawing→world→ImGui→EndDrawing
      order, the headless screenshot/frames exit, shutdown (+ moved `ExportScreenshot`). Everything that
      differed between local/loopback/remote is a config callback (`PumpFrame`/`DrawWorld`/`DrawImGui`/
      `OnWorldClick`/`OnFrameStart`/`RefitCameraBounds`/`OnResize`/`OnFrameEnd`/`OnHeadlessExit`); the three
      `Run*` methods shrink to setup + config + `Run`. `RoadLayerCache` is now lazily built on the first
      `DrawWorld` (its `LoadRenderTexture` needs the GL context `ViewerHost.Run` creates). **GPU-verified
      all four paths render through `ViewerHost`** (local scenario, local evac demo via the overlay seam +
      DemoCatalog, loopback `DrawWorldDds`, 2-process remote late-join via `RefitCameraBounds`) + fresh
      regen. `dotnet test` 465/0/3, bench hash unchanged, package packs clean. (`3579d28`.)

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

## Post-merge reconciliation (City3D branch fast-forwarded into main)
- [x] `SumoSharp.Host` (from `src/Sim.Host`, built for the City3D handoff as the portable
      snapshot→wire publisher) promoted to the 10th shipped package: added to the `pack-check.yml` /
      `publish.yml` portable pack loops, the packing-count assertion, and the
      `PackagingLayoutTests.PortablePackage_MultiTargets_AndHasExpectedPackageId` guard theory.
      `docs/PACKAGES.md` and `docs/SUMOSHARP-PACKAGING-DESIGN.md` §2/§6 updated accordingly.
- [x] `demos/City3D` landed as the first real package-consumer proof (Godot 4 viewer, local
      co-hosted + remote/DDS, consuming `SumoSharp.Core`/`Ingest`/`Replication`/`Replication.Dds`/
      `Viewer.Motion`/`Host` via `<PackageReference>` from a local NuGet feed, not `<ProjectReference>`).
      Pointers added: `docs/DEMOS.md`, `README.md`, and the `docs/PACKAGES.md` examples table.
