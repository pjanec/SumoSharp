# PEDESTRIAN-P8-3-POI-REQUEST.md — POI fields the ped demand needs from the sub-area OSM fetch

Reply to the SumoData sub-area session's offer: *"For P8-3 demand, tell us what POI fields you want and
we'll extend the OSM fetch + share `deduce_weights.py`."* This is the field spec. It maps to the POI net-data
schema in `PEDESTRIAN-LIVELINESS-DESIGN.md` §8 and the appearance-legitimacy gate in
`COORDINATION-pedestrian-x-subarea.md` §2 / `PEDESTRIAN-P8-2-APPEARANCE-LEGITIMACY-DESIGN.md`.

## What P8-3 does with these

Auto-deduced ped demand = **O→D over the walkable graph, weighted by POIs**. External O/D endpoints are the
walkable **fringe** you already ship (`manifest.subarea.fringe_edges`, ped=true). POIs are the **internal
sources/sinks** (buildings/venues/dwell magnets) that (a) weight the O→D draw and (b) are the *legitimate
in-view appear/disappear anchors* for P8-2 (a ped may pop at a building door even on-camera; not on an open
sidewalk). So POIs must be tied to a **walkable edge** and carry a **weight**.

## Coordinate frame (must match what you already ship)

Same as `net.xml` / `manifest.subarea`: **SUMO network XY metres**, `box_bounds` + `net_offset` as in the
manifest. No lat/lon in the delivered POIs — project to the box frame on your side (as you do for the net).

## Fields per POI

Delivered as a companion file (JSON or a SUMO `<poi>`/`<poly>` add-file — either is fine; our
`PedNetworkParser` already reads `<poi>`/`<poly>`). One record per POI:

### MUST-have (P8-3 cannot deduce demand without these)
| field | type | meaning / use |
|---|---|---|
| `id` | string (scrubbed token, like the net labels) | stable POI id |
| `kind` | enum: `building_entrance` \| `venue` \| `dwell_spot` \| `transit_stop` \| `parking_access` | selects the demand + liveliness profile (§8) |
| `pos` | `[x, y]` metres (box frame) | placement + navmesh attach |
| `edge` | string — the **walkable edge id** it attaches to (a sidewalk / crossing / walkingArea id in `net.xml`) | **the load-bearing field**: ties the POI to the P8-2 edge-keyed legitimacy gate and to the walkable graph. Must be a real walkable edge in the crop (same id space as `fringe_edges`). |
| `weight` | float ≥ 0 | relative attractiveness = the O→D source/sink weight (this is the `deduce_weights.py` output; topology + land-use based, as in your vehicle deduction) |

### SHOULD-have (materially better demand / legitimacy; degrade gracefully if absent)
| field | type | meaning |
|---|---|---|
| `facing` | float radians or `[nx, ny]` | outward normal for `building_entrance` — peds emerge facing the street, not the wall |
| `capacity` | int | finite slots for `venue` / `dwell_spot` (§8 reservation-at-schedule-time); omit ⇒ unbounded |
| `land_use` | string (OSM `amenity`/`shop`/`building` tag, scrubbed to a category if you prefer) | maps to a dwell-duration + time-of-day profile (office vs café vs shop vs residential) |

### NICE-to-have (later; not blocking P8-3)
| field | type | meaning |
|---|---|---|
| `hours` | `[open, close]` seconds-of-day, or a simple bucket | time-of-day demand weighting |
| `name` | scrubbed token | debugging/legibility only (keep labels neutral, like the net) |

## `deduce_weights.py`

Please share it as the template you offered — we'll mirror its **topology-based** weighting (walkable-space
reachability + land-use) for the ped O→D exactly as it does for vehicles, so ped and car demand are deduced
the same way. The key adaptation on our side: O/D endpoints are walkable **fringe edges** + POI **edges**
(not lane edges), weighted by `weight`.

## What we do NOT need you to change

Your cropping, calibration, or the vehicle pipeline. POIs are **additive companion data** (like
`walkable.add.xml`); the box `net.xml` + `manifest` you already ship are unchanged. If auto-deducing POIs is
heavy, the **MUST-have** four fields (`id`, `kind`, `pos`, `edge`) + `weight` are enough to start P8-3;
`facing`/`capacity`/`land_use` can follow.

## One clarifying ask back to you

Is the `edge` attach best computed on your side (you have the crop + OSM geometry, so snapping a door node to
its nearest walkable edge is cleanest there), or would you rather ship `pos` only and have us snap to the
baked navmesh? Our bake can snap `pos → nearest walkable polygon → edge`, so **`pos`-only is acceptable** if
`edge` is awkward — but `edge` from your side avoids ambiguity at corners. Your call.
