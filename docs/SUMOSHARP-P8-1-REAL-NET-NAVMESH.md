# P8-1 finding: the ped navmesh bake fragments on REAL cropped geometry (crowd can't populate)

**From the SumoData sub-area session.** Surfaced by running the full sub-area pipeline end-to-end on a
**real ~2 km Geneva box** (cars + deduced POIs + the `--ped-subarea-fcd` recorder + shared replay). The
whole chain runs and merges into a clean self-contained replay — but the **pedestrian crowd is
effectively empty (peak 3 live)**, and the cause is a navmesh-bake connectivity limitation on real
geometry, not the SumoData box. This is exactly the case `P8-1` was flagged to verify ("bake against a
real crop — not yet run"); the answer is: **the bake produces a navmesh, but a fragmented, unroutable
one.**

## The measurement

Same recorder, same options (`--dial 0.05 --seconds 300`), two boxes:

| box | walkable polygons | connected components | O/D draws rejected unroutable | peak live peds |
|---|---|---|---|---|
| synthetic grid (`scenarios/_ped/subarea-box`) | 456 | **1** | 0 | **203** |
| real Geneva ~2 km crop | 1291 | **1010** (largest = 9 polys) | **3680 / 3683** | **3** |

On the real crop the walkable surface shatters into ~1000 disconnected islands, so almost every weighted
origin→destination draw is cross-component and gets rejected as unroutable. Peak live is
**routing-limited, not dial-limited** (the density cap was 2773). The car side is fine (peak 206
vehicles), and their box is well-formed (738 fringe edges, 673 walkable; 1070 POIs all validated on
walkable edges).

## Root cause (from tracing `WalkablePolygonBaker` / `PolygonGraph`)

Portal/adjacency detection stitches two walkable polygons only when they **share an edge or corner within
`AdjacencyEpsilon = 1e-3` (1 mm)**, and it **skips corners where 3+ polygons meet**. netconvert emits
sidewalks as **independently-buffered strips**, plus separate `walkingArea` and `crossing` polygons that
meet at junctions — on real geometry these don't line up to 1 mm and frequently meet 3-at-a-corner, so
the portals never form and the surface fragments. The synthetic grid is regular enough that its polygons
do line up, which is why it showed 1 component / peak 203 and hid the issue.

## The ask (ped-side P8-1)

Make the walkable bake connect real netconvert geometry. Options (ped session's call):
- Loosen/relativize `AdjacencyEpsilon` (1 mm is far too tight for buffered real strips) and handle the
  **3+-polygon corner** case instead of skipping it.
- Better: **stitch using the net's own connectivity** rather than pure polygon-corner geometry — the
  `net.xml` already states which sidewalk lane connects to which `walkingArea`/`crossing` (lane
  connections / `<connection>` + `walkingArea` incoming/outgoing). Baking portals from that adjacency
  (which `PedNetworkParser` already reads) would be robust to buffering slop.
- Acceptance: on a real (or real-like irregular) crop, the walkable surface is **1 (or few large)
  connected component(s)**, unroutable O/D draws ≈ 0, and the recorder populates to the dialed density
  (not 3).

## Shareable repro (no real geometry needed)

The real Geneva box can't cross to the SumoSharp repo, but the fragmentation is **geometry-shape**, not
Geneva-specific: an **irregular** synthetic net with sidewalks/crossings (netgenerate `--rand
--sidewalks.guess --crossings.guess`, short/odd-angle edges) should fragment the same way the uniform
grid does not — the same trick that gave the car teleport witness. **SumoData can build that
synthetic-irregular ped-infra box as a shareable P8-1 repro if wanted** (say the word). Until then
the signature above (component count, `AdjacencyEpsilon`, 3+-corner skip, synthetic-vs-real contrast) is
the geometry-free lead.

## Status

- Real-geometry **pipeline** proven end-to-end: crop → calibrated cars → landuse POIs → recorder →
  shared replay, all clean, self-contained. The **car** populated box is real and rich (206 veh).
- The **ped** populated box waits on this navmesh-connectivity fix. It is a ped-engine (SumoSharp) item;
  the SumoData box they hand over is correct and unchanged.

---

## Ped-session response / design pointer

Accepted as **P8-1b** (`PEDESTRIAN-TRACKER.md` Stage P8). Design (the HOW) is being written to
`docs/PEDESTRIAN-P8-1B-NAVMESH-CONNECTIVITY-DESIGN.md`; the working approach is (a) a hermetic
hand-built-`BakedPolygon` repro of the fragmentation (no real geometry needed) + a connected-components
measure, then (b) relativize `AdjacencyEpsilon` and make the 3+-polygon-corner case connect each
non-area polygon *to the junction-area polygon* (preserving the POC-0 no-shortcut invariant), with
net-connectivity (`JunctionId`) stitching as the fallback. Parity bar: the POC-0 fixture and all existing
ped tests stay bit-identical.
