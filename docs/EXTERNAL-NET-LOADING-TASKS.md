# External georeferenced net loading + pedestrian elevation — TASKS & SUCCESS CONDITIONS

Design (the HOW, referenced by § — never copied): `docs/EXTERNAL-NET-LOADING-DESIGN.md`.
Requirements (the WHAT): `docs/EXTERNAL-NET-LOADING-HANDOFF.md`.
Tracker: `docs/EXTERNAL-NET-LOADING-TRACKER.md`. Read `CLAUDE.md` first.

Each task is a **self-contained briefing**: design reference, files touched, dependencies, and
**numeric/observable success conditions**. A task is closed only when Opus has confirmed its success
conditions first-hand (diff read + gate re-run), per CLAUDE.md's orchestration loop.

---

## Standing gate — measured on this branch at `791d3e6`, before any change

| Gate | Command | Baseline (must be unchanged after every `src/` task) |
|---|---|---|
| Parity suite | `dotnet test Traffic.sln -c Release` | **775 passed / 0 failed / 4 skipped** |
| Determinism hash | `dotnet run --project src/Sim.Bench -c Release` | **`hashA = hashPar = BF3794A4704BCD79`**, `deterministic=True`, par == single |
| LiveCity tests | `dotnet test tests/Sim.LiveCity.Tests -c Release` | capture on T1; **not in `Traffic.sln`** — must be built explicitly (design §8/R4, CLAUDE.md #9) |

Toolchain on this VM (re-provision on a fresh session — both are ephemeral):
`apt-get install -y dotnet-sdk-8.0` → SDK **8.0.129**; `pip install eclipse-sumo==1.20.0` → `sumo` /
`netconvert` **1.20.0** at `/usr/local/bin` (matches `SUMO_VERSION`; shadows apt's 1.18.0).

**Every task below is additive.** No existing signature changes, no existing default changes. If any task
cannot meet that bar, STOP and report rather than widening it.

---

## Stage A — Validation data (must land first; everything else is validated against it)

### A1 — Synthetic georeferenced 3-D pedestrian net fixture
**Design:** §6. **Depends on:** nothing. **Requires:** `netconvert` 1.20.0 on `PATH` (dev-side only —
never invoked by `dotnet test`).
**Touches (all new):** `scenarios/_ped/roadnet_geo3d/{geo3d.nod.xml, geo3d.edg.xml, net.xml,
scenario.net.xml, relative.sumocfg, provenance.txt, README.md}`.

**Do:** author the plain-XML inputs (nodes in **lon/lat**, `z` in a Swiss-like 380–450 m band, ≥1
four-way junction so crossings/walkingareas are generated, ≥1 edge with a real grade), run the §6
`netconvert` recipe, commit the output. `scenario.net.xml` is a byte-identical copy of `net.xml` under the
`preprocess.py` name. `relative.sumocfg` names the net/route with **relative** paths.
`provenance.txt` records the exact command and `netconvert --version` output, and states **"INPUT ONLY —
not a parity golden"** (mirror `scenarios/_ped/roadnet_min/provenance.txt`'s wording and reasoning).

**Success conditions** — a new test asserting each **on the committed file** (no SUMO at test time):
1. `<location>` `projParameter` contains `+proj=utm +zone=32`; `netOffset` is non-zero in both components.
2. ≥1 lane shape has **3 coordinates per vertex**; the parsed `Lane.ShapeZ` is non-null for ≥1 lane and
   spans a range of **≥ 3 m** across the net (i.e. there is real relief, not a constant plane).
3. `PedNetworkParser.Load` yields **≥1 sidewalk, ≥1 crossing, ≥1 walkingarea**.
4. **Measured and recorded in `provenance.txt` and the tracker (design §8/R1 — measure, do not assume):**
   how many of the ped-lane ids (sidewalk/crossing/walkingarea) resolve in `NetworkModel.LanesById` with
   `ShapeZ != null`. This determines which §3.2 branch real 3-D nets take. Report the counts per category.
5. Total committed size of the directory **< 1 MB** (it is a fixture, not a benchmark net).
6. Standing gate unchanged (no `src/` change in this task).

### A2 — Fixture is reachable from the test projects
**Design:** §6. **Depends on:** A1. **Touches:** whatever repo-root resolution the ped/live-city test
projects already use (read it; do not invent a new mechanism).
**Success conditions:** a test loads `scenarios/_ped/roadnet_geo3d/net.xml` through the existing
repo-root helper and `NetworkParser.Parse` succeeds; no absolute path appears anywhere (CLAUDE.md prime
directive 1).

---

## Stage B — Change 1: net/route path resolution

### B1 — `LiveCityConfig.NetPath` / `RoutePath` + ctor resolution
**Design:** §2.1, §2.2. **Depends on:** A1.
**Touches:** `src/Sim.LiveCity/LiveCityConfig.cs` (two new properties),
`src/Sim.LiveCity/LiveCitySim.cs` (`:143` resolution; the `:161` `PedNetworkParser.Load` and `:271`
`CrosswalkSignals.FromNet` call sites must use the **same resolved** path — design §1.1; `:473` route
path).

**Do:** add the two nullable properties and a private static `ResolveNetPath` implementing §2.2's
4-step precedence. `LiveCityScene.Load` stays on `DatasetDir` (design §1.2) — do not change it.

**Success conditions:**
1. `cfg.NetPath` set to the fixture's `net.xml` while `DatasetDir` points at an **unrelated** directory
   ⇒ `new LiveCitySim(cfg)` loads the fixture: `sim.Network.LanesById.Count` equals the count from a
   direct `NetworkParser.Parse` of that file, and `sim.PedestriansEnabled == true`.
2. `DatasetDir` = the fixture dir with **`net.xml` renamed away** (test copies only `scenario.net.xml`
   into a temp dir) and `NetPath` null ⇒ loads via the §2.2 step-3 probe; `PedestriansEnabled == true`.
3. All three net consumers agree: with `NetPath` pointing at the fixture, `CrossingsEnabled == true`
   (proves `PedNetworkParser`/`CrosswalkSignals` followed the override, not `DatasetDir/net.xml`).
   *This is the non-vacuous part of the task — a version that only rewired `NetworkParser.Parse` would
   pass (1) and (2) and still be broken.*
4. `RoutePath` pointing at a route file in another directory ⇒ the scraped spawn-edge set is non-empty
   and **differs** from the derive-from-net fallback set (proves the scrape ran).
5. **Demo regression:** `LiveCityConfig.ForRepoRoot(repoRoot)` + `new LiveCitySim(cfg)`, 200 steps ⇒
   `PeakCars` / `PeakPeds` / `ArrivedTotal` **identical** to the pre-change values (capture them first).
6. Standing gate unchanged (775/0/4, hash `BF3794A4704BCD79`) **and** `tests/Sim.LiveCity.Tests` built
   explicitly and green.

### B2 — `LiveCityConfig.ForSumocfg`
**Design:** §2.3. **Depends on:** B1.
**Touches:** `src/Sim.LiveCity/LiveCityConfig.cs` only. Reuses `Sim.Ingest.ScenarioConfigParser` — **do
not write a new XML parser** (design §1.4).

**Success conditions:**
1. `ForSumocfg(fixture/relative.sumocfg)` ⇒ `NetPath` and `RoutePath` are absolute, exist on disk, and
   resolve to the fixture's files; `DatasetDir` == the sumocfg's own directory.
2. **Absolute-path case:** the test writes a `.sumocfg` into a temp dir whose `<net-file>` is an
   **absolute** path to the fixture net ⇒ resolves to that path unchanged (not double-combined with the
   temp dir). Both emitters from the handoff are then covered.
3. A `.sumocfg` with **no `<input>` section** ⇒ `NetPath` stays null and construction still succeeds via
   the `DatasetDir` probe (§2.3's non-throwing rule), rather than throwing.
4. Field-for-field, `ForSumocfg` matches `ForDataset` on every knob except `NetPath`/`RoutePath`
   (assert by reflection over the public properties, so a later-added knob cannot silently drift).
5. `new LiveCitySim(ForSumocfg(...))` steps 100 times and `Sample()` returns **> 0 cars**.
6. Standing gate unchanged.

---

## Stage C — Change 2: pedestrian elevation

### C1 — `IPedElevationSource` seam
**Design:** §3.1. **Depends on:** nothing.
**Touches (new):** `src/Sim.Pedestrians/Lod/IPedElevationSource.cs`.
**Success conditions:**
1. Builds for **both** TFMs (`net8.0;netstandard2.1`).
2. `src/Sim.Pedestrians/Sim.Pedestrians.csproj` gains **no new `ProjectReference`** — verify by diff.
   The csproj's "must never reference Sim.Ingest" rule is a hard constraint, not a preference.
3. Standing gate unchanged.

### C2 — `NetPedElevationSource`
**Design:** §3.2, §3.4. **Depends on:** A1, C1.
**Touches (new):** `src/Sim.LiveCity/NetPedElevationSource.cs`.

**Do:** implement the once-at-construction lane-set selection (§3.2 branches 1/2/3), the 25 m uniform
grid, ring expansion with **one extra ring after the first hit**, the 64-ring cap, and
`(distSq, lane id ordinal, segment index)` tie-breaking. Elevation itself comes from the existing
`LaneGeometry.ElevationAtOffset` — do not reimplement interpolation.

**Success conditions:**
1. **Correctness against a known analytic case:** on `demos/City3D/CityLib.Tests/fixtures/elevated.net.xml`
   (E0_0 climbs 0→8 m over 100 m), a query at the lane's midpoint returns **4.0 ± 0.05 m**, at 25 m
   returns **2.0 ± 0.05 m**, and on the flat control lane E1_0 returns **0.0**.
2. **Off-lane query:** a point 5 m to the side of E0_0's midpoint still returns ≈4.0 m (nearest-segment
   projection, not nearest-vertex).
3. **2-D net ⇒ no index:** constructed against `scenarios/_ped/demo_city/box/net.xml`, the factory
   returns **null** (or the construction path reports "no Z"), and **no** grid is allocated.
4. **Determinism:** two independently constructed sources give **bit-identical** doubles for 1000
   pseudo-random query points (fixed seed) on the A1 fixture. Equidistant-tie case exercised explicitly.
5. **Ring-cap behaviour:** a query 5 km from any indexed lane returns exactly `0.0` and does not scan the
   whole net (assert a bounded segment-visit count via an internal counter, or assert the call completes
   in < 1 ms).
6. Which §3.2 branch the A1 fixture selects is **recorded in the tracker** (ties back to A1's SC4/R1).
7. Standing gate unchanged.

### C3 — `PedRemoteReconstructor` 5-out-param overload
**Design:** §3.5(a). **Depends on:** C1.
**Touches:** `src/Sim.Pedestrians/Lod/PedRemoteReconstructor.cs` — **add** a third defaulted ctor param
and a sibling overload. The existing 4-out-param overload body must be **untouched**.

**Success conditions:**
1. All **15** existing `TryGetRenderPose` call sites (design §1.10) compile **unchanged** — verify by diff
   that no call site was edited.
2. With `elevation: null`, the new overload returns the **same** `pos`/`visible`/`animTag` as the old one
   for the same id and render time, and `z == 0.0`.
3. With a stub `IPedElevationSource` returning `pos.X * 0.1`, the overload returns exactly that value —
   proving z is sampled at the **smoothed** render position, not the raw wire sample.
4. `tests/Sim.Pedestrians.Tests` green; both TFMs build.
5. Standing gate unchanged.

### C4 — `LiveCitySim.PedElevation` + real Z in `Sample()`
**Design:** §3.5(b). **Depends on:** C2, C3.
**Touches:** `src/Sim.LiveCity/LiveCitySim.cs` (build the source in the ctor; expose it; replace the
literal `0.0` at `:1076`).

**Success conditions:**
1. **3-D net:** on the A1 fixture, after stepping until ≥5 peds are live, **every** sampled
   `LiveCityPed.Z` is non-zero and within the fixture's actual elevation range.
2. **Cross-check against cars (the handoff's own bar):** for the ped nearest to a live car, `|pedZ −
   carZ| ≤ 2.0 m`. Report the **max** over all ped/car pairs within 10 m, not just one sample.
3. **2-D regression:** on `demo_city/box`, `PedElevation == null` and **every** `LiveCityPed.Z` is
   exactly `0.0` (bitwise), across 200 steps. This is what keeps City3D / raylib / `VizReplayBuilder`
   bit-identical.
4. **Frame cost (design §8/R3):** with ≥300 live peds on the A1 fixture, the added `Sample()` cost is
   **< 5%** of `Sample()`'s total time (measure with the elevation source forced null vs. real, same
   seed, ≥5 repeats, report both numbers — a single-run delta is not a measurement).
5. Standing gate unchanged **and** `tests/Sim.LiveCity.Tests` explicitly built and green.

---

## Stage D — Change 3: live pedestrian density knobs

### D1 — Mirror the live ped knobs into `PedDemandConfig` each `Step()`
**Design:** §4. **Depends on:** nothing (independent of A–C).
**Touches:** `src/Sim.LiveCity/LiveCitySim.cs` (keep the `PedDemandConfig` in a field; mirror the two
knobs at one fixed point at the top of `Step()`).

**Success conditions:**
1. **The bug is demonstrated first.** A test that mutates `cfg.PedPopulationCap` mid-run and asserts the
   live ped count follows must **fail before** this change and pass after. Record both outcomes — an
   after-only pass does not prove the fix did anything.
2. Raise `cfg.PedPopulationCap` from 20 → 120 at step 100 ⇒ live ped count exceeds 20 within 200 further
   steps and trends toward the new cap.
3. Lower it 120 → 20 mid-run ⇒ no new spawns while over cap; count decays monotonically (no despawn
   storm — existing peds finish their trips).
4. `cfg.PedSpawnRatePerSecond` doubled mid-run ⇒ spawns per step in the following 100 steps increase
   (report both rates).
5. **Car knobs unchanged:** the same test pattern on `cfg.CarTargetConcurrent` passes **before and after**
   (proves design §0/C3's claim that the car half already worked, and that this task did not disturb it).
6. **No unmutated behaviour change:** demo 200-step `PeakPeds`/`ArrivedTotal` identical to pre-change.
7. Standing gate unchanged.

---

## Stage E — Scale proxy & close-out

### E1 — Large-net load proxy (honest, non-committed)
**Design:** §7. **Depends on:** B1.
**Touches:** a dev-side script under `scripts/` only. The generated net is **ephemeral and must not be
committed** (CLAUDE.md committed-vs-ephemeral split).
**Success conditions:**
1. Script generates a large `netgenerate` grid (target ≥ 50 MB), loads it via `NetPath`, steps 100 times,
   and prints: net file size, `NetworkParser` parse time, `PedNetworkParser` parse time, total ctor time,
   peak working set, and peak cars/peds.
2. Output is recorded in the tracker with the explicit caveat that this is a **proxy**, not
   `swiss_roads.net.xml` (168 MB), which is not present in this repo.
3. `.gitignore` (or the script's temp dir) keeps the generated net out of the index — verify with
   `git status --short` after a run.
4. Standing gate unchanged (no `src/` change).

### E2 — Close-out
**Depends on:** all of the above.
**Success conditions:**
1. Final gate re-run and quoted: parity **775/0/4**, hash **`BF3794A4704BCD79`** (single == parallel),
   `tests/Sim.LiveCity.Tests` + `tests/Sim.Pedestrians.Tests` green.
2. A short **BIG-side handoff-back** section in the tracker: the exact new API surface, the fixture path,
   the one-line harness for BIG to run against the real `swiss_roads.net.xml`, and a plain statement of
   what was **not** verified here (design §7).
3. Every unchecked box in the tracker either ticked with first-hand evidence or explicitly listed as
   deferred with a reason. No box ticked on an implementor's report alone (CLAUDE.md orchestration loop).
