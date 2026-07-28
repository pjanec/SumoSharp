# HANDOFF - adapt the Godot City3D 3D viewer to load any SumoData net + add density knobs

For a SEPARATE session working in **SumoSharp `demos/City3D`** (Godot/.NET). Written by the BIG/Spectacle
session (2026-07-27). Sibling docs: `SumoSpectacle/HANDOFF-external-net-loading.md` (the SumoSharp engine
changes both viewers share) and `SumoSpectacle/PROGRESS.md`.

## Goal

Make the Godot City3D live-city viewer able to (a) LOAD AN ARBITRARY SumoData cut sub-area (e.g. a
georeferenced Geneva box, up to ~20x20 km) instead of only the hardcoded synthetic demo, and (b) add live
CAR + PEDESTRIAN COUNT (density) sliders to its UI - matching what BIG's Spectacle live-city scene now has.

## Scope limits (owner)

- Target area: a **smaller part of Geneva, up to ~20x20 km**. NOT whole Switzerland (~280 km).
- Because of that: a simple **recenter** (subtract a net-centre origin) keeps all coords within +-10-20 km,
  where Godot's 32-bit floats are fine (~mm at 10 km). **No large-world/double-precision/tiling machinery
  needed.** If someone ever loads the full country, accept the float error - out of scope.

## Repo state

SumoSharp `main` (BIG's submodule is fast-forwarded to `791d3e6`). The parallel SumoSharp engine session is
on branch `claude/document-review-r0uhcw` - coordinate the shared engine changes (below) with it so they
land on `main` once and both viewers + BIG pull them.

## Current state - findings (file:line)

### Load path (hardcoded to the demo)
- `--live-city` is a bare flag with no dataset/crop args (`Viewer/Main.cs:4833 ParseLiveCityArg`).
- `Main.cs:701` -> `ReadyLiveCityLive(repoRoot)` (`Main.cs:932-1013`):
  `var liveCfg = LiveCityConfig.ForRepoRoot(repoRoot); liveCfg.SimHz = _simHz; _liveCitySource = new
  LiveCitySource(liveCfg);` (`Main.cs:942-945`). `LiveCitySource(cfg)` = `new LiveCitySim(cfg)`
  (`CityLib/LiveCitySource.cs:29-33`). Tick is LIVE per-frame (`Main.cs:1625-1724 ProcessLiveCity`:
  accumulator -> `_liveCitySource.Tick()` (== `_sim.Step()`) -> `Reconstructor.Reconstruct(...)` ->
  `UpdateCars/UpdatePeds`).
- `LiveCityConfig.ForRepoRoot` HARDCODES `DatasetDir = <repo>/scenarios/_ped/demo_city/box` + the pinned crop
  `X0=2055,Y0=2055,X1=2895,Y1=2895` (`LiveCityConfig.cs:42-45,221-235`). `Main.cs` never calls the existing
  arbitrary-net entry point `LiveCityConfig.ForDataset(dir)` (`LiveCityConfig.cs:243`, sets RouteGraph +
  RegionPlan; already tested via `tests/Sim.LiveCity.Tests`, NOT wired into the viewer).
- Net filename convention is hardcoded: `LiveCitySim.cs:143` `netPath = Path.Combine(cfg.DatasetDir,
  "net.xml")`. A cut is often `scenario.net.xml` / a `.sumocfg` -> needs the shared engine change (below).

### Coordinates - NO recenter today (the real risk)
- `CityLib/CoordinateTransform.cs:32` `SumoToGodot(x,y,z) => ((float)x, (float)z, (float)-y)` - a bare float
  cast, ZERO offset (verified incl. `CoordinateTransformTests.cs`). Demo coords are ~2000-2900 (safe by
  luck). A real Geneva cut keeps absolute local coords ~ -100000..-140000 (see
  `tests/Sim.LiveCity.Tests/ArbitraryNetStageE4Tests.cs`, "Geneva/CH1903-style frame") -> float ULP ~cm and
  worse once composed with camera/MultiMesh transforms -> jitter, z-fighting, orbit instability.
- Z IS carried through on the vehicle side (`Reconstructor.cs:145` uses `r.Z`; `CarTransform.cs:50` keeps it).
  Ped side is 2D (`PedNetworkParser` drops z) -> peds render at Godot Y=0; on a 3D Geneva net that's
  inconsistent with the Z-varying roads (see shared engine change 2).

### UI - code-built Godot Control nodes
- All UI is hand-built `Control` trees in `CanvasLayer`s. Reference pattern: `BuildRateControlUi()`
  (`Main.cs:2275-2352`) - a `PanelContainer`/`VBoxContainer` with `Label`s and `HSlider`s (render-Hz slider
  `Main.cs:2300`, playout-delay slider `:2318`, `ValueChanged += handler` writes a private field), plus the
  `OptionButton` "LC zone" (`:2340`). Add density sliders as two more rows here, same pattern.

### Density - env + ctor-baked, mixed live-mutability
- Set only via env `LIVECITY_CARS`/`LIVECITY_PEDS` inside `LiveCityConfig.WithEnvOverrides`
  (`LiveCityConfig.cs:256-337`); `Main.cs` exposes nothing. Defaults `CarTargetConcurrent=160`,
  `PedPopulationCap=160`, `PedSpawnRatePerSecond=8.0`.
- CAR count is live-mutable in principle: `LiveCitySim` holds `_cfg` by reference (`:131`) and reads
  `_cfg.CarTargetConcurrent` every `Step()` (`:743`). BUT `LiveCitySource` doesn't expose that cfg -> add a
  passthrough (below).
- PED count is baked once into an `init`-only `PedDemandConfig` at `LiveCitySim` ctor
  (`LiveCitySim.cs:272-278`, `PedDemand.cs:718,721`) -> NOT live without a rebuild or a deeper setter.
- City3D has NO restart/reload of the live sim (built once at `Main.cs:945`; the only "Restart" is the
  replay clock, unrelated).
- Render side is not a limiter: car/ped MultiMesh `InstanceCount` is set from `vehicles.Count`/`peds.Count`
  each frame (`Main.cs:3506,3592,3629`), no fixed cap.

### Realism zone ring / ped regime already work
- Ring: `BuildHighRealismZone()`/`UpdateLcZoneVisual()` (`Main.cs:4144-4186`) - unit `MakeGroundRing`
  (`:4303`) scaled+placed via `Transform3D` from `LcZoneX/Y/Radius`. No scale assumption -> only needs the
  recenter (below) applied upstream.
- Ped regime colouring already reads `Ig.ModelOf(id)==PedDrModel.FreeKinematic`
  (`CityLib/PedReconstructor.cs:84`).

## Tasks

### T1 - load an arbitrary SumoData cut
- Add a CLI arg / simple file-or-dir picker (e.g. `--dataset=<dir>` or `--sumocfg=<path>`) and route it to
  `LiveCityConfig.ForDataset(dir)` (arbitrary-net: RouteGraph + RegionPlan) instead of `ForRepoRoot`.
- **Disable/override the pinned crop** (`X0..Y1`) for arbitrary nets - it's the demo hero-block; a real cut
  should use the whole net (or an explicit crop rect). Confirm `ForDataset` already leaves the crop off /
  set it explicitly.
- Depends on shared engine change **C1** (NetPath/ForSumocfg) to load `scenario.net.xml`/`.sumocfg`; until
  then, only a dir containing `net.xml` works.

### T2 - recenter for float precision (needed new work)
- Compute a single **origin** once at load = centre of the net (or crop) bounding box in SUMO-local metres
  (mirror `LiveCitySim`'s own `ComputeNetAabbCentre`, or read the crop centre). Store it on the viewer.
- Subtract it in the SUMO->Godot mapping BEFORE the float cast. Cleanest: add an overload/wrapper
  `SumoToGodot(x,y,z, originX, originY)` (or a small `CoordinateTransform` instance holding the origin) and
  route EVERY placement through it: cars (`Reconstructor`/`CarTransform`), peds (`PedTransform`), road/
  building meshes, the realism-zone ring, selection ring, and the camera home/target. Consistency is the
  whole point - one origin, applied everywhere.
- Keep it ~20 km-bounded (scope). No tiling.

### T3 - density sliders (match Spectacle)
- Add two `HSlider`+`Label` rows in `BuildRateControlUi()` (same pattern as the render-Hz slider):
  - **Cars (target concurrent)**: live. Add `LiveCitySource.SetCarTarget(int)` (or expose the live
    `LiveCityConfig`) that pokes the SAME cfg object `_sim` holds -> takes effect next `Step()`. Also
    `CarSpawnPerStep` if wanted.
  - **Peds (population cap)** + spawn rate: use shared engine change **C3** (live ped-demand setter, now a
    firm task) so the ped slider is live like cars - call `LiveCitySim.SetPedDensity(cap, ratePerSec)` (or
    the exposed `PedDemand`) from the slider handler. (If you build ahead of C3 landing, a Restart/reload of
    `_liveCitySource` + scene meshes is the interim - but prefer waiting for C3.)

## Shared SumoSharp engine changes (coordinate with the `claude/document-review-r0uhcw` session)

These live in `src/Sim.*` and benefit BIG + both viewers; land on `main`:
- **C1 (needed for T1):** `LiveCityConfig.NetPath` + `ForSumocfg(sumocfgPath)` so a net not named `net.xml` /
  in a subfolder / a `.sumocfg` loads. (Full spec in `HANDOFF-external-net-loading.md` Change 1.)
- **C2 (optional, for ped elevation on 3D nets):** non-breaking `PedRemoteReconstructor.TryGetRenderPose(...,
  out double z, ...)` overload sampling the shared vehicle-lane Z. Without it, City3D peds sit at Y=0 and
  won't follow the 3D road elevation. (Spec in Change 2 of the same doc.)
- **C3 (optional, for a live ped-count slider):** make ped demand density live - either a
  `PedDemand.SetPopulationCap/SetSpawnRate` + a `LiveCitySim`/`LiveCitySource` passthrough, or make
  `PedDemandConfig` cap/rate mutable and read live each step. Enables live ped sliders in BOTH City3D and
  Spectacle (BIG's ped sliders currently apply on Restart for the same reason).

## Validation

- Load a real Geneva cut (produced by SumoData `preprocess.py`, or a `net.xml`-named dir) - cars+peds render
  without jitter/z-fighting, camera orbits cleanly (proves T2 recenter). Compare against the demo (still
  works).
- Car slider changes density within a tick; ped slider changes count (live via C3, or after Restart).
- Realism-zone ring still tracks the camera and sits on the ground at the recentered scale.
