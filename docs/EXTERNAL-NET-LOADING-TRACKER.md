# EXTERNAL NET LOADING — tracker

At-a-glance state of `docs/EXTERNAL-NET-LOADING-TASKS.md`. A box is ticked only when its task's
stated success conditions have been verified first-hand (CLAUDE.md: the review is the load-bearing
step — every "done" is unverified until proven).

## Stage F — fixture
- [x] **F1** georeferenced 3-D cut fixture (`scenarios/_ped/georef_min/`, `scripts/gen-georef-fixture.sh`)
      — UTM32N zone 32, `convBoundary` at ~(91850, 73960), z 370–400 m, 20 crossings / 24 walking
      areas, 5 routed vehicles. Asserted by `ExternalNetLoadingTests.Fixture_IsGeoreferenced3DAndFarFromOrigin`.

## Stage C — engine (shared by BIG + both viewers)
- [x] **C1** `LiveCityConfig.NetPath`/`RoutePath`/`RoutePaths` + `ForSumocfg` — 11 tests in
      `tests/Sim.LiveCity.Tests/ExternalNetLoadingTests.cs` (relative + absolute `<input>` paths,
      multi-file `<route-files>` union, missing net-file throws, missing route-files falls back,
      demo overrides stay null).
- [x] **C2** pedestrian elevation on a 3-D net (`IPedElevationSource`, `NetLaneElevationSource`,
      the `TryGetRenderPose` overload) — **68/68** fixture ped lanes resolve to 3-D vehicle lanes;
      median |pedZ − nearest carZ| = **0.097 m** (max 1.52 m) against the handoff's 2 m bar; the 2-D
      demo still yields exactly `Z == 0.0`. `Sim.Pedestrians.csproj` gained no project reference.
- [x] **C3** live density setters (`PedDemand.SetPopulationCap`/`SetSpawnRatePerSecond`,
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

## Test state (measured, not assumed)

| Suite | Result |
| ----- | ------ |
| `tests/Sim.LiveCity.Tests` | 76/76 pass (22 of them new) |
| `tests/Sim.Pedestrians.Tests` | 277/277 pass |
| `demos/City3D/CityLib.Tests` | 148/151 pass — the 3 failures are **pre-existing** |
| `Traffic.sln` (parity) | pass |

The three `CityLib.Tests` failures (`ReconstructorS2Tests.Reconstructor_StoppedVehicle_DoesNotCreep`,
`…_CenterIsHalfLengthBehindSnapshotFront`, `…_JunctionTurn_FollowsConnectingLaneArc_Smoothly`) were
confirmed failing on a clean worktree at the pre-change commit `7985647`. They are wall-clock /
`Thread.Sleep`-paced reconstruction tests, unrelated to this work, and are left as found rather than
silently retuned.

## Definition of done (from the handoff)
- [x] `LiveCityConfig.NetPath` / `ForSumocfg` land — BIG can load `swiss_roads.net.xml`, a cut box, or a `.sumocfg`
- [x] `PedRemoteReconstructor.TryGetRenderPose(..., out double z, ...)` overload lands, real elevation on a 3-D net
- [x] live ped-density setter lands — ped count changes with no sim rebuild
- [x] all validated headless in isolation

## Not done, and why
- The real **168 MB `swiss_roads.net.xml`** and a real **Geneva cut** are not in this repo (they live
  in BIG's dist repo / SumoData), so load time and memory on them are **unmeasured**. The loader has
  no size ceiling and `Sim.Viz --external-net <path>` is the one-command probe for confirming it
  there.
- The viewer's ground datum is **flat** (design §5.6/§8.5): overlays with no elevation data of their
  own sit at the net's mid-elevation and can be tens of metres off on hilly terrain. Sampling the
  surface per overlay point is the natural follow-up.
