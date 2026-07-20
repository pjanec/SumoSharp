# pedfrag_witness — shareable ped-navmesh FRAGMENTATION repro (SumoSharp P8-1)

Geometry-free witness for the pedestrian navmesh-bake fragmentation described in
`SUMOSHARP-P8-1-REAL-NET-NAVMESH.md` (SumoData `docs/`). Built entirely from
`netgenerate --rand` — **no real geometry, no place data.** The ped-side analogue of
the synthetic teleport witness for the car engine.

## The signal

The SumoSharp recorder (`--ped-subarea-fcd`) bakes a box's walkable surface with
`WalkablePolygonBaker` / `PolygonGraph`, then drives seeded weighted O/D demand over it.

| box | walkable polygons | connected components | unroutable O/D | peak live peds | pop cap |
|---|---|---|---|---|---|
| uniform grid reference | 456 | **1** (largest 456) | **0** | **203** | 203 |
| synthetic irregular (this) | 492 | **222** (largest 18) | **all** | **0** | 115 |

On the irregular net the walkable surface shatters into 222 disconnected components, so every
origin->destination draw is cross-component and rejected — the crowd cannot populate (peak 0,
routing-limited, NOT dial-limited: cap was 115). The uniform grid bakes into 1 component and
populates to peak 203. This is the same fragmentation direction seen on the real ~2 km crop
(1010 components, 3680/3683 draws unroutable, peak 3 vs cap 2773).

## Root cause (see SUMOSHARP-P8-1-REAL-NET-NAVMESH.md)

`PolygonGraph.AdjacencyEpsilon = 1e-3` (1 mm) is too tight for independently-buffered netconvert
sidewalk strips, and the vertex-proximity pass **skips 3+-polygon corners**. Irregular
sidewalk/walkingArea/crossing geometry rarely lines up to 1 mm and often meets 3-at-a-corner, so
portals never form and the surface fragments. The uniform grid's regularity lines the polygons up
and hides it.

## Fix acceptance

The bake connects real-like netconvert geometry: walkable surface -> **1 (or few large)
connected component(s)**, unroutable O/D draws ~= 0, and the recorder **populates to the dialed
density (not 0)** on this irregular box, while the uniform grid stays at 1 component / peak ~203.

## Contents

- `net.xml` — the irregular synthetic ped-infra net (`netgenerate --rand` + guessed
  sidewalks/crossings/walkingAreas, short/odd-angle edges, seed 42).
- `box/` — the assembled recorder box dir the recorder reads: `net.xml`, `manifest.json`
  (subarea metadata + 26 walkable fringe edges), `pois.json` (187 weighted ped endpoints, all
  validated on walkable edges). The box is well-formed; only the bake fragments.
- `logs/grid_reference.stdout.log` — recorder stdout on the committed uniform grid box
  (`components=1`, `peakLive=203`, `unreachableSkipped=0`).
- `logs/synthetic_fragmented.stdout.log` — recorder stdout on this witness
  (`components=222`, `peakLive=0`, all draws unroutable).

The `PEDFRAG-DIAG:` lines in the logs come from a small temporary connected-component pass added
to `SubareaFcdRecorder.Record` (over `PolygonGraph.Neighbors`) to surface the component count; the
shipped recorder prints only the `wrote ... peakLive=...` line. Add the same pass to reproduce the
component numbers.

## Run

On the SumoSharp recorder branch (`--ped-subarea-fcd` support):

```
dotnet build src/Sim.Viz -c Release
dotnet run --project src/Sim.Viz -c Release -- \
  --ped-subarea-fcd ped.fcd.xml --dial 0.05 --seconds 120 --box box
```

Regenerate the net+box from scratch (needs SUMO 1.20.0) with the committed generator
`experiments/subarea/synthetic_pedfrag/build.py` in SumoData.
