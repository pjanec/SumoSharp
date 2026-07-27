<!--
  RECEIVED REQUEST — verbatim, as handed over by the BIG/Spectacle session (2026-07-27).
  This is the requirements/WHAT document of record for docs/EXTERNAL-NET-LOADING-DESIGN.md.
  Do NOT edit the body below; corrections and verification findings live in the design doc §0.
-->

# HANDOFF - SumoSharp changes for loading georeferenced external nets (Geneva/Switzerland) + free-style density

For the paused **SumoData / SumoSharp** session. Written by the BIG/Spectacle session (2026-07-27).
Companion context: `SumoSpectacle/PROGRESS.md` (BIG side). This doc = the SumoSharp-side changes BIG needs +
the shared data/coordinate/density contract so both sides align.

## Goal (BIG side)

The BIG **Spectacle** tool has a live-city scene (`Projects/BIG/Tools/Spectacle/Scenes/LiveCityScene.cs`)
that ticks `LiveCitySim` live and renders cars+peds on the Bagira IG. We now want it to run **georeferenced
external Swiss nets** - a full-Switzerland net or a **cut sub-area** produced by the SumoData pipeline
(`preprocess.py`) - placed correctly on the IG's UTM32N Switzerland terrain, with a **free-style UI** to set
vehicle/pedestrian counts (not just pre-baked density).

Everything the IG placement needs (UTM->flat conversion, netOffset, Z) is handled on the BIG side. What we
need from SumoSharp are two additive, non-breaking changes below.

## Which SumoSharp to change

Make these on **SumoSharp `main`** (github `pjanec/SumoSharp`). BIG's submodule tracks `main`
(currently fast-forwarded to `791d3e6` on branch `igbridge-spectacle`); it will fast-forward again to pick
these up. NOTE: the SumoData repo's own SumoSharp submodule is pinned to an **older** commit (`c46f278`,
predates the live-city work) - do not be confused if `LiveCityConfig`/`ForDataset`/`BuildLiveCity` look
absent there; they exist on `main`. Verify against `main` before editing.

---

## Change 1 - load a net by explicit path / from a `.sumocfg` (net-path)

**Problem.** `LiveCitySim` ctor hardcodes the net path: `var netPath = Path.Combine(cfg.DatasetDir, "net.xml")`
(`src/Sim.LiveCity/LiveCitySim.cs:143`), and route edges from `Path.Combine(cfg.DatasetDir,
"scenario.rou.xml")` (`:473`). `LiveCityConfig` exposes only `DatasetDir` (`LiveCityConfig.cs:28`). A real cut
sub-area's net is often named **`scenario.net.xml`** (preprocess.py output) - not `net.xml` - and a user may
want to point at a `.sumocfg`. Neither is loadable today.

**Change (additive).**
1. Add `public string? NetPath { get; set; }` (and optional `RoutePath`) to `LiveCityConfig`.
2. In `LiveCitySim` ctor: `var netPath = cfg.NetPath ?? Path.Combine(cfg.DatasetDir, "net.xml");`
   (route path likewise `cfg.RoutePath ?? Path.Combine(cfg.DatasetDir, "scenario.rou.xml")`).
3. Add a factory `public static LiveCityConfig ForSumocfg(string sumocfgPath)` that parses the `.sumocfg`
   `<input><net-file>/<route-files>` (reuse the EXISTING `ScenarioConfigParser` in `Sim.Ingest`, already used
   by `Engine.LoadScenario(sumocfgPath)`), resolves the net/route paths **relative to the sumocfg's own dir**
   (SUMO's documented rule), sets `NetPath`/`RoutePath` + `DatasetDir = sumocfgDir`, and applies the same
   RouteGraph/RegionPlan defaults as `ForDataset`.
   - Gotcha (from SumoData `SUBAREA-METHOD.md` §8): preprocess.py emits ABSOLUTE net/route paths; demo-city
     emits RELATIVE. Handle both (absolute-as-is, else combine with sumocfg dir).

**Validate in isolation (headless, no IG):** load the real Switzerland net
`Dist/BIG_DistRepo/Data/Terrains/Switzerland/BTraffic/Switzerland/common/swiss_roads.net.xml` (168 MB) or a
cut box via `NetPath`/`ForSumocfg`, `new LiveCitySim(cfg)`, Step N times, assert cars spawn on the graph and
`Sample()` yields nonzero counts. Confirm `RouteGraph` self-generation works on the arbitrary net (it did for
prior arbitrary nets). A small console harness (like the earlier SmokeLoad spike) is enough.

## Change 2 - expose per-pedestrian Z (non-breaking overload)

**Problem.** Cars already get real elevation: `KinematicReconResult.Z` (`Sim.Viewer.Motion`) resolves
lane-surface Z from `LocalLanes.LaneShapeZ` -> `LaneGeometry.ElevationAtOffset` for a 3D net (the Swiss nets
ARE 3D: lane `shape="x,y,z"`, z ~= real Swiss elevation). Peds are 2D throughout:
`PedRemoteReconstructor.TryGetRenderPose(int id, out Vec2 pos, out bool visible, out string animTag)`
(`src/Sim.Pedestrians/Lod/PedRemoteReconstructor.cs:106`) has no Z, `LiveCitySim.Sample()` literal-zeros
`LiveCityPed.Z`, and `PedNetworkParser.ParseShape` drops the 3rd coord. BIG must NOT place peds at z=0
(they'd be hundreds of m off on a UTM terrain) and must NOT rely on IG ground-clamping (owner decision).

**Change (additive, non-breaking - keep the existing 4-out-param overload untouched so raylib/City3D viewers
are unaffected).** Add a sibling overload:
```csharp
public bool TryGetRenderPose(int id, out Vec2 pos, out double z, out bool visible, out string animTag)
```
Compute `z` by sampling the **shared vehicle-side** net elevation: ped-lane ids (`:J_c0_0` crossings,
`:J_w..` walking areas, sidewalks) live in the SAME id space as `NetworkModel.LanesById` (because
`NetworkParser.Parse` parses every `<edge>` incl. crossing/walkingarea/internal), and those lanes carry
`ShapeZ`. So: project `pos` onto the nearest ped-lane polyline segment (over `PedNetwork.Sidewalks/Crossings/
WalkingAreas` ids), get the arc offset, call the existing `LaneGeometry.ElevationAtOffset(shape, shapeZ,
arc)`. No change to `PedNetworkParser`, `PedNetwork`, `ActivityTimeline`, or the existing signature.
- Fallback: if the nearest lane has no `ShapeZ` (2D net), return z=0 (as today).

**Validate:** on a 3D net, assert the overload returns z ~= the local lane elevation (not 0) for a promoted
ped, and matches the nearby car Z within a metre or two.

---

## Shared contract / facts BIG relies on (please keep these true)

- **Coordinate frame: cut sub-areas PRESERVE the original UTM32N georeference.** Verified: cut Geneva boxes
  keep `netOffset="-388091.80,-5257586.90"`, `projParameter="+proj=utm +zone=32 +ellps=WGS84 ..."`, and 3D z
  in lane shapes (crop is `netconvert -s FULL --keep-edges.in-boundary <bbox>`, no `--offset.*`/reprojection,
  so `<location>` is untouched). BIG converts SUMO-local -> UTM (`sumo - netOffset`) -> IG flat via its own
  `Ctx.ProjectionConvertor`. **Please do NOT add offset re-normalization / reprojection to the cut** - keep
  the absolute UTM offset. Synthetic demo-city stays unprojected (`projParameter="!"`, small offset, z=0);
  BIG branches on `projParameter` (real utm -> georef; "!" -> local frame).
- **Consumption is LIVE tick, not the replay-HTML contract.** BIG drives `LiveCitySim.Step()` +
  reconstruction each frame (like `SceneGen.BuildLiveCity` drives the engine to pre-bake frames, but streamed
  live). The static `sim_viz.py` replay HTML is offline QA only - not BIG's path.
- **Density knobs BIG will drive live (free-style UI):** `LiveCityConfig.CarTargetConcurrent`,
  `CarSpawnPerStep`, `PedPopulationCap`, `PedSpawnRatePerSecond` (all present on `main`). BIG will mutate
  these at runtime; **please keep `Step()` reading these off the (by-reference) `cfg` each tick** (it does
  today) so a slider takes effect without a sim rebuild. The `PedDensityKnob.ForNetwork(net, dial, ...)`
  helper (peds/walkable-km) is a nice optional mapping BIG may use for a single "ped density" dial.
- **Dataset dir layout BIG's loader will probe:** a cut dir contains the net as `net.xml` OR
  `scenario.net.xml`, plus `scenario.rou.xml`, `manifest.json` (+ demo-city companions pois/zones/buildings).
  BIG will point at the DIR (or a `.sumocfg`) and resolve the net filename. `manifest.json.subarea.
  {box_bounds, coordinate_frame.net_offset, fringe_edges}` is available if useful.

## Calibration / tuning (the interactive part - your session)

Separate from the two code changes: the SumoData session's `preprocess.py --percent N|max` +
`auto_calibrate.py` find the max vehicle density (knee) for a box and bake `scenario.rou.xml`. Caveats from
the SumoData docs to remember: vehicle `--percent` accuracy is +-25-40% (`manifest.verify.
achieved_vs_target_pct`); SumoSharp's teleport/calibration diverges from vanilla SUMO (calibrate with vanilla
SUMO, serve/run with SumoSharp); ped low live-count is usually navmesh-fragmentation routing-loss, not the
density knob. For BIG's live free-style mode we bypass baked `.rou.xml` density and self-generate via
`CarTargetConcurrent`/`PedPopulationCap`, so exact `--percent` calibration is not on the critical path for
BIG - it matters for realistic pre-baked scenarios.

## Definition of done (for BIG to proceed)

1. `LiveCityConfig.NetPath`/`ForSumocfg` land on `main`; BIG ffs its submodule and can load
   `swiss_roads.net.xml` / a cut box / a `.sumocfg`.
2. `PedRemoteReconstructor.TryGetRenderPose(..., out double z, ...)` overload lands on `main`; returns real
   elevation on a 3D net.
3. Both validated headless in isolation (harness described above). BIG then wires the Spectacle loader + UI.
