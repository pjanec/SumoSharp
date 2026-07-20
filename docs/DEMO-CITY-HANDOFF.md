# SumoData → SumoSharp handoff: the synthetic demo-city (Prototype E substrate)

A fully **synthetic** ~5×5 km composed city + a 1 km feature kernel, for demonstrating and testing every
vehicle + pedestrian feature and their interactions, in both `sim_viz` 2D and City3D 3D. No real
geometry, no place tokens — safe to keep in the SumoSharp repo.

## Contents
- `box/` — the full 5×5 km city (net + calibrated demand + companion data). **Bakes to `components=1,
  unreachableSkips=0`** (the earlier 6-component fragmentation was a citygen ped-connectivity gap in the
  plaza + ring-NW; fixed — see the requirements doc R1 "RESOLVED").
- `kernel/` — a ~1 km feature slice: one of every junction type (traffic_light, roundabout, priority,
  right_before_left, allway_stop, zipper) + multi-lane + a ped-only park. Bakes to `components=1`. The
  shareable navmesh stress-test.
- `SUMOSHARP-DEMO-CITY-REQUIREMENTS.md` — **read first.** Prioritized asks R1–R8, what SumoData provides
  vs what you build, and the full bake-findings history (incl. the netconvert speed-threshold traps).
- `SUBAREA-DEMO-CITY-DESIGN.md` — district layout + the exact schemas (`pois/v2`, `zones/v1`,
  `buildings/v1`) with the cross-reference graph.

## Companion data (in `box/` and `kernel/`)
- `net.xml`, `scenario.sumocfg`, `scenario.rou.xml`, `scenario.add.xml`, `vType*.xml` — SUMO scenario.
- `pois.json` (`pois/v2`) — POIs incl. the new `parking_lot` / `park` kinds + set-piece cross-refs
  (mall+lot+boardable_car, restaurants+tables+doors, park+meet_areas, hidden garages, quiet-area lots).
- `zones.json` (`zones/v1`) — 6 district polygons. `buildings.json` (`buildings/v1`) — 28 footprints
  with `levels`/`height_m`/`type` (City3D massing). `edge_fields.json` — per-edge ped fields.
- `manifest.json` — the sub-area contract (bbox / fringe / frame) + density block.

## First contact — the one known blocker
Your recorder throws `FormatException: unknown POI kind` on `parking_lot` / `park`. Extend
`PedPoiReader.cs` / `PedNetworkParser` to parse them (R2/R5) before loading `box/pois.json` unfiltered.
Everything else (net, cars, v1 POIs) loads today.

## Quick start
```
# vanilla SUMO (cars): loads clean, drive-away/garage cycles run, no-cheating audit passes
sumo -c box/scenario.sumocfg --end 800 --no-step-log true

# navmesh bake (your recorder): kernel -> components=1; full box -> components=1
dotnet run --project src/Sim.Viz -c Release -- \
  --ped-subarea-fcd ped.fcd.xml --reachable-filter --dial 0.3 --seconds 200 --box <path>/box

# turn the weave on (R7) for the no-pass-through look — this box is the visible proof of W1-W4
```
