# EXTERNAL NET LOADING (viewer + loader) — tracker

**Engine API is governed by `docs/EXTERNAL-NET-LOADING-API-CONTRACT.md`** (parallel session, branch
`claude/document-review-r0uhcw`). Mapping of that contract's task IDs onto this branch:

| Contract | This branch | State |
| --- | --- | --- |
| B1, B2 | C1 | done here — signatures match the contract exactly, incl. its 4-step net-path resolution order |
| D1 | C3 | done here — API chosen by this branch, see design §3.4 (needs an explicit decision if they also build it) |
| C1–C5 (ped Z) | — | **theirs**, not built here |
| — | T1, T2, T3 | the Godot viewer |

At-a-glance state of `docs/EXTERNAL-NET-VIEWER-TASKS.md`. A box is ticked only when its task's
stated success conditions have been verified first-hand (CLAUDE.md: the review is the load-bearing
step — every "done" is unverified until proven).

## Stage F — fixture
- [x] **F1** georeferenced 3-D cut fixture (`scenarios/_ped/georef_min/`, `scripts/gen-georef-fixture.sh`)
      — UTM32N zone 32, `convBoundary` at ~(91850, 73960), z 370–400 m, 20 crossings / 24 walking
      areas, 5 routed vehicles. Asserted by `ExternalNetLoadingTests.Fixture_IsGeoreferenced3DAndFarFromOrigin`.

## Stage C — engine (shared by BIG + both viewers)
- [x] **C1** (= contract **B1 + B2**) `LiveCityConfig.NetPath`/`RoutePath`/`RoutePaths` +
      `ForSumocfg` — 13 tests in `tests/Sim.LiveCity.Tests/ExternalNetLoadingTests.cs` (relative +
      absolute `<input>` paths, multi-file `<route-files>` union, missing net-file throws, missing
      route-files falls back, demo overrides stay null). Reconciled against the contract §4: the
      4-step resolution order now includes the `scenario.net.xml` probe, so `ForDataset(cutDir)`
      loads a `preprocess.py` cut with no explicit `NetPath`.
- [ ] **C2** pedestrian elevation on a 3-D net — **NOT IN THIS WORK, by decision.** Owned by a
      parallel workstream adding z to the pedestrian engine itself. This branch briefly carried an
      incompatible implementation (render-time surface sampling through an injected
      `IPedElevationSource`); it was removed rather than left to collide on the same overload. See
      design §4 for the consequence: peds render at the viewer's flat ground datum until that work
      lands.
- [x] **C3** (= contract **D1**) live density setters (`PedDemand.SetPopulationCap`/`SetSpawnRatePerSecond`,
      `LiveCitySim.SetPedDensity`/`SetCarDensity`) — raise converges (40 → ≥100 within the run),
      lower is non-increasing with zero new spawn events, rate-0 is reversible, setters are
      deterministic for a fixed call sequence, no-op on a ped-less net.

## Stage T — Godot City3D viewer
- [x] **T1** load an arbitrary dataset / sumocfg (`--dataset=`, `--sumocfg=`) — arbitrary nets get
      the net AABB as their crop (every lane point inside it); the demo keeps the pinned rect.
- [x] **T2** recenter for float precision (`SumoGodotFrame`) — Identity is *bitwise* identical to
      the legacy transform; the recentered frame resolves a 0.25 mm offset the identity frame loses
      entirely at 9e4; road meshes are a pure translation of the raw build; a source-level test
      proves no placement bypasses a frame; the camera→zone→ring round trip closes to <1 m.
- [x] **T3** live car + pedestrian density sliders — both move the live count with the same
      `LiveCitySource` instance (asserted by reference identity: no rebuild).

## Stage V — validation
- [x] **V1** full offline `dotnet test` loop green (incl. the two out-of-solution test projects) —
      see "Test state" below.
- [x] **V2** headless external-net harness (`Sim.Viz --external-net`) — output quoted in the
      design's §6.1.
- [x] **P1** (unplanned) `NetworkParser` multi-lane cont-bay fix — the fixture broke
      `JunctionLinkLaneMapTests`' every-committed-net sweep on its first day; a real parser defect,
      not a bad fixture. Design §7.1. Full parity suite after the fix: **775 pass, 0 fail**.

## Test state (measured, not assumed)

| Suite | Result |
| ----- | ------ |
| `tests/Sim.LiveCity.Tests` | 90/90 pass |
| `tests/Sim.Pedestrians.Tests` | 324/324 pass |
| `demos/City3D/CityLib.Tests` | **190/190** (the three `ReconstructorS2Tests` failures were test bugs — fixed, see below) |
| `Traffic.sln` (parity) | 775 pass, 0 fail, 4 skip |
| `Sim.Bench` determinism hash | `BF3794A4704BCD79`, par == single |

### The three `ReconstructorS2Tests` failures — FIXED, and they were TEST bugs

They were long-standing (confirmed failing on clean worktrees at `7985647` and `4bf36e5`), masked by the
stale-NuGet-cache trap below, and initially left as found rather than retuned — correctly, because
retuning a threshold to make a red test green is how a real defect gets buried. Traced instead, and all
three turned out to be assertions that did not measure what their own comments claimed:

1. **Wrong pacing** (root cause of two of the three). The wall-clock frame loop slept a hardcoded 15 ms,
   but every scenario it drives has `step-length = 1` — one `Tick()` is a whole second, so the loop ran
   the sim at **~22× real time**. `DrClock` advances the render clock at *wall* rate × a fitted wall↔sim
   rate, capping catch-up at `frameDt · simRate · 3` (`DrClock.cs:255`, a deliberate anti-jump guard)
   with `frameDt` clamped to 0.1 s; at 15 ms/frame that is 0.045 sim-s/frame against a 0.333 feed, so the
   clock fell ~1 s behind and never recovered within a test's span. Sleeping `dt / FramesPerTick` moved
   the stopped-pivot median from **6.57 m to 2.49 m** (L/2 = 2.50) and the junction stray from **1.01 m to
   0.13 m** (bound 0.6). **The reconstructor was correct throughout** — this was measurement error.
2. **Stoppedness filtered on the wrong side.** Both stopped-vehicle tests filtered on the *reconstructed*
   speed (a pose from `Delay` seconds ago) while comparing against the *live* snapshot, so the frames just
   after the light turned green — car already pulling away in the snapshot, still at rest in the
   reconstruction — counted as "stopped". Worth 4.83 m of the pivot max and 0.61 m/frame of the creep max.
3. **A frame-rate-dependent threshold written as an absolute.** Creep was bounded as `max per-frame metres
   < 0.12`, reasoned from a 60 Hz loop; at the correct 333 ms pacing the same physical hold covers 20× the
   distance. It is a **speed** now, divided by the frame's own measured Stopwatch duration (not the nominal
   sleep, which overshoots under load and would inflate the number), with max < 1.5 m/s catching the gross
   bug the test was written for (a driving car is ~13 m/s) and median < 0.2 m/s separately pinning the
   steady state — one settle frame as the 0.6 s smoothing constant converges is not creep, and only the
   median distinguishes them. Measured: max 0.53, median 0.035.

**Cost, since it is not free:** the wall-clock loops are real-time now, so this suite went from **28 s to
2 m 18 s**. Tick counts were trimmed to the minimum each assertion needs (48→31, 44→36, 60→40).

> **Repacking caveat.** `demos/City3D/build.sh --pack-only` writes `SumoSharp.*.0.1.0.nupkg` at a
> version that never changes, so NuGet's global cache will happily serve a **stale** package and the
> City3D projects will silently build against an old engine. After any engine change, clear
> `~/.nuget/packages/sumosharp.*` before repacking, or the demo suites measure code you are not
> looking at (CLAUDE.md measurement-discipline #9, same failure mode, different mechanism).

## Definition of done (from the handoff)
- [x] `LiveCityConfig.NetPath` / `ForSumocfg` land — BIG can load `swiss_roads.net.xml`, a cut box, or a `.sumocfg`
- [x] `PedRemoteReconstructor.TryGetRenderPose(..., out double z, ...)` lands, real elevation on a
      3-D net — ownership came back to this session; it is now the **only** overload (design §4.1)
- [x] live ped-density setter lands — ped count changes with no sim rebuild
- [x] all validated headless in isolation

## Follow-ups delivered after the original handoff
- [x] **Lane provenance through `IPedNavigation`** (design §4.1) — stacked pedestrian surfaces (a
      footbridge over the path below it) no longer collapse onto one height.
- [x] **Elevation made MANDATORY in the nav and render APIs** (design §4.1, §4.1.1) — the 2-D
      `FindPath` / `ElevationsAlong` / `TryGetRenderPose` siblings are removed, so an omitted height
      is a compile error rather than a silent zero. Contradicts contract C5·SC1 by owner decision.
- [x] **`SumoNavMesh` given real provenance** — it was the "2-D demo provider" that City3D actually
      routes 3-D peds on. Held to the same stacked-deck fixture as `SumoRouteGraphNav`.
- [x] **The ground datum is no longer flat** (design §7.2) — a `TerrainField` baked from `Lane.ShapeZ`
      on net load; the grey grid is baked over the net and draped over it; the zone tint subdivides so
      its interior follows it too; POI markers, doors, building bases, TL poles and the realism ring
      follow it for free through `GroundToGodot`. Measured on `georef_min` (27.5 m of relief): the
      field reproduces every lane vertex's own height to **0.326 m**, versus up to ~14 m of error from
      the flat datum it replaced. Grid drape and two-bake determinism are both exact.

## Not done, and why
- The real **168 MB `swiss_roads.net.xml`** and a real **Geneva cut** are not in this repo (they live
  in BIG's dist repo / SumoData), so load time and memory on them are **unmeasured**. The loader has
  no size ceiling and `Sim.Viz --external-net <path>` is the one-command probe for confirming it
  there.
- The ground datum was flat; **§7.2 closed that**. What remains: the `TerrainField` interpolates
  **road** heights, so ground far from any road is a smooth fill rather than surveyed terrain, and its
  resolution is the 40 m cell (which grows on very large nets to keep the lattice bounded). The grey
  grid is now **finite** — net bbox + 400 m — instead of following the camera to infinity; that is the
  deliberate cost of baking it onto the terrain.
