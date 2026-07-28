# EXTERNAL NET LOADING — tasks

Work breakdown for `docs/EXTERNAL-NET-VIEWER-DESIGN.md`. Each task names its design reference (a
section, never a copy), the files it touches, its dependencies, and the **success conditions** that
close it. Tick boxes live in `EXTERNAL-NET-VIEWER-TRACKER.md`.

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

### C2 — pedestrian elevation on a 3-D net — **NOT IN THIS WORK**
**Design:** §4.

Owned by a parallel workstream that is adding z to the pedestrian engine itself. Deliberately not
implemented here; an earlier revision of this branch did, in an incompatible shape (render-time
surface sampling via an injected source), and it was removed rather than left to collide. See §4 for
the consequence.

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

---

## Stage E — elevation follow-ups (added after the original handoff)

Design references are sections of `EXTERNAL-NET-VIEWER-DESIGN.md`, not copies of them.

### E1 — lane provenance through `IPedNavigation`
**Design:** §4.1. **Files:** `src/Sim.Pedestrians/Navigation/INavigation.cs`,
`.../RouteGraph/SumoRouteGraphNav.cs`, `.../Bake/{SumoNavMesh,BakedPolygon,WalkablePolygonBaker}.cs`,
`.../PolylineElevation.cs`, `src/Sim.Pedestrians/Lod/PedLodManager.cs`,
`src/Sim.Pedestrians/Demand/PedDemand.cs`.
**Success conditions:**
1. `FindPath` reports, per returned vertex, the provider-local surface id that produced it;
   `surfaces.Count == path.Count` on every non-null result.
2. On a synthetic stacked-deck net (a footbridge 12.5 m over the path beneath it), a route ALONG the
   bridge reads 412.5 m at the shared plan-view crossing point and a route along the ground reads
   400.0 m — both within 0.05 m. Without provenance both collapse onto one height.
3. Every point at which a ped's path is reassigned or re-sliced either carries the provenance across
   or nulls it explicitly (spawn, lively Walk legs, promotion, demotion/`ReanchorSurfaces`,
   `RecoverRoute` fallbacks, the lively timeline's own geometry).
4. Parity 775/0/4 and hash `BF3794A4704BCD79` unchanged.

### E2 — elevation made MANDATORY (the 2-D siblings deleted)
**Design:** §4.1, §4.1.1. **Files:** as E1, plus
`src/Sim.Pedestrians/Lod/PedRemoteReconstructor.cs`, `src/Sim.Pedestrians.Nav.DotRecast/`,
`src/Sim.Viz/`, `src/Sim.Viewer/`, `demos/City3D/CityLib/PedSimSource.cs`, and 13 test doubles.
**Success conditions:**
1. `IPedNavigation` has exactly two members, neither with a default body: the 3-arg `FindPath` and
   the 2-arg `ElevationsAlong`. `SumoRouteGraphNav` exposes no 2-arg `FindPath` and no 1-arg
   `ElevationsAlong`.
2. `PedRemoteReconstructor` exposes exactly ONE `TryGetRenderPose`, whose third parameter is
   `out double`. Asserted by reflection — a compile-time check cannot assert its own absence.
3. `SumoNavMesh`'s blocked-set query is named `FindPathAvoiding`, so `FindPath(a, b, out _)` is
   unambiguous.
4. Every provider that answers flat does so with a hand-written body carrying a comment saying it is
   a choice; none inherits flatness from an interface default.
5. All of: `Traffic.sln`, `src/Sim.Viewer`, `src/Sim.PedDdsLoopback`, `src/Sim.Host.App`,
   `tests/Sim.LiveCity.Tests`, `tests/Sim.Viewer{,.Motion}.Tests`, `demos/City3D/{CityLib,
   CityLib.Tests,Viewer}` build clean in Release.
6. Parity 775/0/4 and hash `BF3794A4704BCD79` unchanged.

### E3 — `SumoNavMesh` given real provenance
**Design:** §4.1 ("now a full provenance provider too"). **Files:**
`src/Sim.Pedestrians/Navigation/Bake/{SumoNavMesh,BakedPolygon,WalkablePolygonBaker}.cs`.
**Success conditions:**
1. `SumoNavMesh.FindPath` attributes each waypoint to a `BakedPolygon` index; a portal vertex is
   attributed to the polygon being ENTERED.
2. The stacked-deck fixture from E1·SC2 passes against `SumoNavMesh` as well as
   `SumoRouteGraphNav`, and every vertex of the under-bridge route names `ground_0`.
3. A 2-D net still reads 0.0 at every vertex, provenance or not.

### E4 — baked terrain field; grid and zones follow it
**Design:** §7.2 (§7.2.1 mechanism, §7.2.2 grid, §7.2.3 zones, §7.2.4 determinism). **Files:**
`demos/City3D/CityLib/{TerrainField,GroundGridBuilder,SumoGodotFrame,ZoneGroundBuilder}.cs`,
`demos/City3D/Viewer/Main.cs`.
**Success conditions:**
1. A lane set with no `ShapeZ`, an empty lane set, and a lane set at one constant height all bake to
   `TerrainField.Flat`, and `SumoGodotFrame.Identity` / `default` carry no field — so
   `GroundToGodot` on them is bitwise the pre-terrain arithmetic.
2. On `scenarios/_ped/georef_min`, `HeightAt(laneVertex)` reproduces that vertex's own `ShapeZ` to
   **well under half the net's own relief**, over every lane vertex in the net. *(Measured: 0.326 m
   worst over 693 vertices against 27.5 m of relief.)*
3. Baking the same geometry twice is **bitwise** identical over a dense sample grid.
4. A 300 km extent grows the cell rather than the lattice: `CountX`/`CountY ≤ MaxCornersPerAxis`.
5. Every grid vertex sits exactly on `HeightAt(x, y) − OriginZ + GroundOffsetSumoZ` (< 1e-3 m); on a
   ramp with 100 m of relief the grid's Y spread is > 80 m; on a flat frame every grid vertex shares
   one Y and each line is a single segment.
6. A 100 km extent caps the line count by growing the spacing; line positions are snapped to the
   spacing so two overlapping bakes share their lines.
7. On a terrain frame the zone tint subdivides (> 100 vertices for a 1000×400 m district), every
   vertex — interior midpoints included — sits on the field (< 1e-3 m), the subdivision is bounded by
   `MaxSubdivisionDepth`, sibling triangles share split-edge vertices, and `Area` is unchanged.
8. On a flat frame the zone tint is the original 4-vertex / 2-triangle fan.
9. Parity 775/0/4 and hash `BF3794A4704BCD79` unchanged — no engine file is touched.

### E5 — visual sign-off on a GPU (NOT doable in this environment)
**Design:** §7.2 as a whole. **Where:** a Windows desktop session with a GPU and the Geneva data.
**Success conditions:** the checklist in `docs/handoffs/WIN-GPU-VISUAL-TEST-terrain-and-ped-z.md`,
signed off item by item with screenshots. Everything above is headless and asserts numbers; only a
human on a GPU can confirm the scene *looks* right.
