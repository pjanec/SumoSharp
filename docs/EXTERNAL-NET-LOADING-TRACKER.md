# EXTERNAL NET LOADING — tracker

At-a-glance state of `docs/EXTERNAL-NET-LOADING-TASKS.md`. A box is ticked only when its task's
stated success conditions have been verified first-hand (CLAUDE.md: the review is the load-bearing
step — every "done" is unverified until proven).

## Stage F — fixture
- [x] **F1** georeferenced 3-D cut fixture (`scenarios/_ped/georef_min/`, `scripts/gen-georef-fixture.sh`)

## Stage C — engine (shared by BIG + both viewers)
- [ ] **C1** `LiveCityConfig.NetPath`/`RoutePath`/`RoutePaths` + `ForSumocfg`
- [ ] **C2** pedestrian elevation on a 3-D net (`IPedElevationSource`, `NetLaneElevationSource`, `TryGetRenderPose` overload)
- [ ] **C3** live density setters (`PedDemand.SetPopulationCap`/`SetSpawnRatePerSecond`, `LiveCitySim.SetPedDensity`/`SetCarDensity`)

## Stage T — Godot City3D viewer
- [ ] **T1** load an arbitrary dataset / sumocfg (`--dataset=`, `--sumocfg=`)
- [ ] **T2** recenter for float precision (`SumoGodotFrame`)
- [ ] **T3** live car + pedestrian density sliders

## Stage V — validation
- [ ] **V1** full offline `dotnet test` loop green (incl. the two out-of-solution test projects)
- [ ] **V2** headless external-net harness (`--external-net`)

## Definition of done (from the handoff)
- [ ] `LiveCityConfig.NetPath` / `ForSumocfg` land — BIG can load `swiss_roads.net.xml`, a cut box, or a `.sumocfg`
- [ ] `PedRemoteReconstructor.TryGetRenderPose(..., out double z, ...)` overload lands, real elevation on a 3-D net
- [ ] live ped-density setter lands — ped count changes with no sim rebuild
- [ ] all validated headless in isolation
