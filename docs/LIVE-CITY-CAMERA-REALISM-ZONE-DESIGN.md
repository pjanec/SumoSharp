# Camera-driven high-realism LC zone (City3D viewer) — design + tasks

> **Feature (owner-requested, GPU testing session).** The per-area lane-change realism gate
> (`docs/LIVE-CITY-15-PER-AREA-LOD-DESIGN.md`) uses a **static 70 m pocket** at the crop centre. With a
> close/roaming camera you can see cars OUTSIDE that pocket, which fall back to the cheap pure-lateral
> "float" — visible and unrealistic (observed: veh96 floating on red outside the pocket). Fix: let the
> high-realism zone **track / lock to the camera**, and highlight it. Circle only; demo-only; parity-safe.

## Modes (cycle with **`H`**; mirrored by an OptionButton in the rate panel; default = **Central**)
1. **Central** — today's static crop-centre pocket (unchanged). Baseline / default on launch.
2. **Follow** — zone centre = the camera's look-at ground point, radius auto-sized from camera distance;
   updated every step, so the highlight ring **moves with the camera** and sits centred in the view.
3. **Locked** — on entering Locked, capture the current camera-derived zone and **freeze** it (centre +
   radius); the camera then roams freely while the zone (and its ring) stay put.

## Zone → engine seam (parity-safe)
`LiveCitySim` today hard-codes the LC-realism pocket to the ped-ORCA pocket (`HighRealismPocket{X,Y}` +
`HighRealismPromoteRadius`, used by `IsLowRealismLaneChangePos` in `Step`). Split the LC-realism zone out
as a **settable** zone the host pushes each step:

- `LiveCitySim.SetLcRealismZone(double centreX, double centreY, double radius)` (SUMO coords). Fields
  `_lcZone{X,Y,R}` initialise to the static pocket + 70 m ⇒ **Central mode == current behaviour**.
- `Step`'s per-area classification reads `_lcZone{X,Y,R}` (was `HighRealismPocket*`). Everything else
  about `LIVE-CITY-15-PER-AREA-LOD` is unchanged (previous-snapshot positions, `SetLowRealismLaneChange`).
- Getters `LcZone{X,Y,Radius}` for the viewer to render the ring at the live zone.
- **Determinism / parity:** the setter is called by the viewer BEFORE `Engine.Step`; the zone is a single
  per-step value applied to all cars ⇒ order-independent, serial==parallel. Only active when
  `CooperativeLaneChange` (demo) and `radius > 0`; every parity/bench golden leaves it untouched ⇒
  **657/4 byte-identical, bench hash unchanged**. The ped-ORCA pocket (`_field`) is **unchanged** in v1
  (moving the expensive ORCA promotion with the camera is a separate, heavier change — see Deferred).

`LiveCitySource` passes through `SetLcRealismZone(...)` and `LcZone{X,Y,Radius}` (mirrors its existing
`HighRealismPocket*` passthrough).

## Viewer (`Main.cs`)
- `enum LcZoneMode { Central, Follow, Locked }`, default `Central`. `H` cycles; OptionButton selects.
- Per live-city frame, before `Tick()`:
  - **Central:** zone = static pocket (`HighRealismPocketX/Y`, `HighRealismPromoteRadius`).
  - **Follow:** centre = **camera→ground raycast** through the screen centre (`ProjectRayOrigin/Normal`
    ∩ `y=0`), so it tracks pitch AND yaw — not just the orbit pivot. Godot ground point → SUMO is the
    inverse of `CoordinateTransform.SumoToGodot`'s `(x, z, -y)`: `SumoX = hit.X`, `SumoY = -hit.Z`.
    `radius = clamp(slant · tan(Fov/2) · fill, min, max)` (slant = camera→look-point distance; `Fov` =
    vertical FOV) — a **wider FOV / nearer look-point ⇒ larger circle** that fits the ground frustum
    trapezoid. Horizon fallback (no ground hit): orbit `Focus` + `Distance·K`.
  - **Locked:** on the transition into Locked, snapshot the current zone; then leave it fixed.
  - push via `_liveCitySource.SetLcRealismZone(...)`.
- **Ring render:** reuse `BuildHighRealismZone`'s ring, but make it a node **repositioned + scaled each
  frame** to `LcZone{X,Y,Radius}` (build the ring geometry once at unit radius; set translation+scale) so
  Follow moves cheaply without rebuilding meshes. Distinct tint per mode (e.g. Locked warmer) so the mode
  reads at a glance. A small HUD label shows the active mode.

## Tasks & success conditions
- **T1 — `LiveCitySim` settable zone.** Add `_lcZone{X,Y,R}` (init = static pocket), `SetLcRealismZone`,
  `LcZone{X,Y,Radius}`; classification reads the zone. **Success:** parity 657/4 byte-identical, bench
  hash `D96213B7BB4021A7` unchanged; existing `Sim.LiveCity.Tests` (22) still green; a new test asserts
  Central-mode init == old pocket classification, and a moved zone reclassifies (inside→high, outside→low).
- **T2 — `LiveCitySource` passthrough.** Delegate setter + getters. **Success:** builds; local viewer packs.
- **T3 — Viewer mode SM + per-step push + `H` key + OptionButton + HUD.** **Success:** `H` cycles
  Central→Follow→Locked; in Follow the ring tracks the camera and cars under it stop floating while
  distant cars still float; Locked freezes the ring; Central == pre-feature behaviour.
- **T4 — Ring re-drive.** Ring repositions/scales to the live zone each frame (no per-frame mesh rebuild).
  **Success:** smooth ring motion in Follow at 240 Hz; no per-frame GC spike.

## Deferred (owner-agreed)
- **Camera-distance culling within the frustum:** distant cars that are in-frustum but far from the camera
  need not be high-realism. This is a per-car camera-distance test (engine gate change) — deferred; v1 is
  the ground circle only.
- **Unifying ped-ORCA promotion into the same moving zone** (so peds promote where the camera looks too):
  heavier (touches `InterestField` source + ORCA churn) — deferred; v1 moves only the LC-realism zone.
