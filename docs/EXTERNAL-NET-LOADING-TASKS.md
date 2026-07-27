# EXTERNAL NET LOADING — tasks

Work breakdown for `docs/EXTERNAL-NET-LOADING-DESIGN.md`. Each task names its design reference (a
section, never a copy), the files it touches, its dependencies, and the **success conditions** that
close it. Tick boxes live in `EXTERNAL-NET-LOADING-TRACKER.md`.

Test-project note (CLAUDE.md measurement discipline #9): `dotnet build -c Release` does NOT build
`tests/Sim.LiveCity.Tests` or `demos/City3D/CityLib.Tests` — they are not in `Traffic.sln`. Build and
run those csprojs explicitly or you are measuring stale code.

---

## Stage F — fixture (no dependencies)

### F1 — georeferenced 3-D cut fixture
**Design:** §6. **Files:** `scripts/gen-georef-fixture.sh`, `scenarios/_ped/georef_min/*`.

Generate and commit a synthetic stand-in for a SumoData Geneva cut.

**Success conditions**
1. `scenarios/_ped/georef_min/scenario.net.xml` exists and its `<location>` has
   `projParameter` containing `+proj=utm +zone=32` and a non-zero `netOffset`.
2. `convBoundary` minimum x > 50000 and minimum y > 50000 (i.e. the net is far from the origin — the
   condition that makes it a valid T2 test; a fixture at 0..400 would pass a viewer with no recenter).
3. Every lane `shape` carries a 3rd component; sampled z values lie in [360, 410].
4. ≥ 10 edges with `function="crossing"` and ≥ 10 with `function="walkingarea"`.
5. Companion `scenario.rou.xml` (≥ 5 `<vehicle>` with non-empty `edges`), `scenario.sumocfg` with
   RELATIVE `<net-file>`/`<route-files>`, and `provenance.txt` naming the SUMO version.
6. `scripts/gen-georef-fixture.sh` re-runs clean on a machine with SUMO 1.20.0 and reproduces them.

---

## Stage C — engine (the shared changes both viewers need)

### C1 — `NetPath` / `RoutePath(s)` / `ForSumocfg`
**Design:** §1. **Files:** `src/Sim.LiveCity/LiveCityConfig.cs`, `src/Sim.LiveCity/LiveCitySim.cs`.
**Depends:** F1 (for the tests).

**Success conditions** — new tests in `tests/Sim.LiveCity.Tests/ExternalNetLoadingTests.cs`:
1. `NetPath` pointing at `georef_min/scenario.net.xml` (a dir with NO `net.xml`) constructs a
   `LiveCitySim`, and `Network.EdgesById.Count > 0`.
2. After 200 `Step()` calls, `CurrentCars > 0` **and** `Sample().Cars.Count > 0` — cars actually
   spawned on the arbitrary graph, not merely "did not throw".
3. `PedestriansEnabled == true`, `CrossingsEnabled == true`, and `RouteGraphNavigationActive == true`
   on that fixture; after 200 steps `Sample().Peds.Count > 0`.
4. `ForSumocfg(georef_min/scenario.sumocfg)` yields `NetPath` = the fixture net's full path,
   `RoutePaths` containing the fixture's `scenario.rou.xml`, `DatasetDir` = the fixture dir,
   `NavMode == RouteGraph`, `RegionPlan == true`; and a sim built from it steps as in (2)/(3).
5. A `.sumocfg` written to a **temp dir** whose `<net-file>`/`<route-files>` are **absolute** paths
   into the fixture resolves to those exact absolute paths (the `preprocess.py` form).
6. A `.sumocfg` with no `<input><net-file>` throws `InvalidDataException` naming the cfg path.
7. A `.sumocfg` with `<route-files>` listing several files (vType files + the real routes, the
   `subarea-box` shape) scrapes the union: the resulting `CropEdges` is non-empty and equals the set
   obtained from the routes file alone.
8. **Byte-identical demo:** `ForRepoRoot` leaves `NetPath`/`RoutePath`/`RoutePaths` null, and a
   `ForRepoRoot` sim's `CropEdges` sequence is unchanged (assert against the existing expectation in
   `ArbitraryNetStageATests`/`LiveCitySimTests` — those must still pass untouched).

### C2 — pedestrian elevation on a 3-D net
**Design:** §4. **Files:** `src/Sim.Pedestrians/Lod/IPedElevationSource.cs` (new),
`src/Sim.Pedestrians/Lod/PedRemoteReconstructor.cs`, `src/Sim.LiveCity/NetLaneElevationSource.cs`
(new), `src/Sim.LiveCity/LiveCitySim.cs`, `src/Sim.LiveCity/LiveCitySnapshot.cs` (doc comment only).
**Depends:** F1.

**Success conditions**
1. `Sim.Pedestrians.csproj` gains **no** `ProjectReference` (Principle 6 — grep the diff).
2. Existing 4-out-param `TryGetRenderPose` signature unchanged; all existing
   `tests/Sim.Pedestrians.Tests` pass untouched.
3. With no elevation source, the 5-param overload returns `z == 0.0` and a `pos` bit-identical to
   the 4-param overload's, for the same id and pump sequence.
4. On `georef_min`: for every live ped, `NetLaneElevationSource.ElevationAt(pedPos)` is in
   [360, 410] and **not** 0.
5. **Ped-vs-car agreement:** for at least one live ped, the nearest live car within 30 m has
   `|pedZ - carZ| <= 2.0` m (the handoff's own bar). Assert over the whole population: the *median*
   absolute difference to the nearest car within 30 m is ≤ 2.0 m.
6. `LiveCitySim.Sample()` on the **demo** (`ForRepoRoot`, a 2-D net) still yields `Peds[i].Z == 0.0`
   exactly, for every ped, at several time points.
7. `NetLaneElevationSource` resolves ≥ 90% of the fixture's ped-lane ids against
   `NetworkModel.LanesById` (proves the shared-id-space claim in §4.2 rather than assuming it).

### C3 — live density setters
**Design:** §3. **Files:** `src/Sim.Pedestrians/Demand/PedDemand.cs`,
`src/Sim.LiveCity/LiveCitySim.cs`.

**Success conditions**
1. `PedDemandConfig` remains `init`-only/immutable (no property loses `init`).
2. `PedDemand.SetPopulationCap` / `SetSpawnRatePerSecond` change `PopulationCap` /
   `SpawnRatePerSecond` and take effect on the **next** `Step` with no rebuild.
3. **Raise converges:** demo sim at cap 40, stepped to steady state; `SetPedDensity(120, 12.0)`;
   within 20 simulated seconds `CurrentPeds > 40 * 1.5`, trending to ≥ 100.
4. **Lower is honest:** from a filled population, `SetPedDensity(10, 1.0)`; over the next 20 s the
   live count is **non-increasing** and `PedDemand.SpawnEvents.Count` gains **zero** new entries
   while the count is above the cap.
5. `SetCarDensity(N)` changes `CurrentCars` toward N within a few seconds without a rebuild.
6. **Determinism:** two sims with identical seeds and the identical *sequence* of setter calls
   produce identical `SpawnEvents` (ids + times). A sim that never calls a setter produces
   `SpawnEvents` identical to one built before this change (guarded by the existing ped tests).
7. `SetPedDensity` on a net with `PedestriansEnabled == false` is a silent no-op (no throw).

---

## Stage T — Godot City3D viewer

### T1 — load an arbitrary dataset / sumocfg
**Design:** §1, §5.5. **Files:** `demos/City3D/Viewer/Main.cs`,
`demos/City3D/CityLib/LiveCitySource.cs`. **Depends:** C1.

**Success conditions**
1. `--dataset=<dir>` and `--sumocfg=<path>` parse (both the `=`-joined and two-token forms, matching
   `--replay`'s existing convention), resolved against the launch CWD via the existing
   `ResolveAgainstLaunchCwd`.
2. `--dataset` routes to `LiveCityConfig.ForDataset`, `--sumocfg` to `ForSumocfg`; neither path calls
   `ForRepoRoot`. Bare `--live-city` still calls `ForRepoRoot` — the demo is unchanged.
3. The pinned demo crop is **not** applied to an arbitrary net: `LiveCitySource.Crop` covers the
   whole net AABB (so road meshes and camera framing use the real extent). Unit-tested on the
   fixture: the crop rect contains every lane shape point.
4. Headless smoke: a CityLib-level test constructs `LiveCitySource` from `ForSumocfg(georef_min)`,
   ticks 200×, and gets `Sample().Cars.Count > 0`.

### T2 — recenter for float precision
**Design:** §5. **Files:** `demos/City3D/CityLib/CoordinateTransform.cs` (+`SumoGodotFrame`), every
CityLib builder that calls `SumoToGodot`, `demos/City3D/Viewer/Main.cs`. **Depends:** T1.

**Success conditions**
1. `SumoGodotFrame.Identity.ToGodot(x,y,z)` is **exactly** (bitwise) equal to
   `CoordinateTransform.SumoToGodot(x,y,z)` for a spread of inputs; all existing
   `CoordinateTransformTests` pass untouched.
2. `ToSumo(ToGodot(p))` round-trips within 1e-3 m for coordinates of magnitude 1e5.
3. **Precision:** for a point at SUMO (91850.5, 73960.25, 372.5) with the fixture's origin, the
   recentered Godot coordinate round-trips to within 1e-4 m, whereas the identity frame's does not
   (assert the identity frame's error is > 1e-3 m — i.e. the test proves the problem exists, not just
   that the fix compiles).
4. **No missed call site:** a test enumerates every `CoordinateTransform.SumoToGodot(` occurrence
   under `demos/City3D/` outside `CoordinateTransform.cs` itself and asserts it is zero (all
   placements go through a frame). This is a source-level guard because a missed site type-checks
   perfectly while being 90 km wrong.
5. The inverse mapping in `Main.cs` (camera → `SetLcRealismZone`) uses `ToSumo` with the same
   origin — covered by a test that pushes a known Godot camera position and asserts the SUMO zone
   centre lands within 1 m of the expected net coordinate.
6. Demo unchanged: with `ForRepoRoot`, the origin is the demo crop centre; assert the *rendered*
   geometry is a pure translation of the pre-change output (same shape, offset by the origin).

### T3 — density sliders
**Design:** §3. **Files:** `demos/City3D/Viewer/Main.cs` (`BuildRateControlUi`),
`demos/City3D/CityLib/LiveCitySource.cs`. **Depends:** C3.

**Success conditions**
1. `LiveCitySource.SetCarTarget(int)` / `SetPedDensity(int, double)` passthroughs exist and poke the
   same live objects `LiveCitySim` holds (unit-tested: call, tick, observe the count move).
2. Two `HSlider`+`Label` rows appear in `BuildRateControlUi()` following the existing render-Hz
   slider pattern (`ValueChanged +=` handler, label shows the live value).
3. Moving the car slider changes `CurrentCars` within a tick's spawn budget, with no sim rebuild
   (no new `LiveCitySource` constructed — assert by reference identity in the headless test).
4. Same for the ped slider, live via C3.

---

## Stage V — validation

### V1 — full offline loop
**Success conditions:** `dotnet test` green for `Traffic.sln`, `tests/Sim.LiveCity.Tests`,
`tests/Sim.Pedestrians.Tests`, and `demos/City3D/CityLib.Tests`, on a machine where SUMO is **not**
required. No golden regenerated; `git status` shows no golden touched.

### V2 — headless external-net harness
**Design:** handoff "Validate in isolation". A console harness that loads a net by `NetPath` /
`ForSumocfg`, steps N times, and prints car/ped counts + a ped-vs-car Z comparison, so a human can
point it at the real 168 MB `swiss_roads.net.xml` or a real Geneva cut outside this environment.
**Success conditions:** runs against `georef_min` and prints non-zero cars, non-zero peds, and a
median |pedZ − nearest carZ| ≤ 2 m; documented in the design's §6.
