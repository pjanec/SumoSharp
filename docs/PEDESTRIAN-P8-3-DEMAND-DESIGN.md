# PEDESTRIAN-P8-3-DEMAND-DESIGN.md — auto-deduced sub-area ped demand (HOW)

**Status: design → implementing.** The demand-generation half of Stage P8-3. Consumes the POI bundle
(`PedPoiReader`, `docs/PEDESTRIAN-P8-3-POI-REQUEST.md`) and the walkable fringe (`SubareaManifest`) to
produce weighted O→D pedestrian demand over the crop. WHAT/contract: `COORDINATION-pedestrian-x-subarea.md`
§3; appearance-legitimacy: `PEDESTRIAN-P8-2-APPEARANCE-LEGITIMACY-DESIGN.md`.

## 1. Model

Auto-deduced demand = **weighted origin→destination over a set of legitimate walkable endpoints**:

- **Endpoints** are the union of:
  - **fringe** edges (`manifest.subarea.fringe_edges`, ped=true) — external entry/exit at the crop boundary,
    each at a point on its sidewalk lane. Uniform base weight `FringeWeight` (people entering/leaving the box).
  - **POI** edges (`pois.json`) — internal sources/sinks (building doors, venues, transit, parking, dwell),
    each at `PedPoi.Pos` with the deduced `PedPoi.Weight`.
- **Per ped:** draw an **origin** endpoint and a **destination** endpoint, each **weighted** by endpoint
  weight, with `origin ≠ destination`; route `origin.Pos → destination.Pos` over the navmesh (the existing
  `PedDemand` path logic and liveliness spline are unchanged — only *where* O/D come from changes).

## 2. Appearance legitimacy falls out for free (the key property)

Every endpoint is a **fringe or POI edge** — exactly the legitimate appear/disappear anchors of the P8-2
gate. So a ped **spawns** at a fringe stub or a building door and **despawns** (arrives) at another — both
ends are legitimate *by construction*, on-camera or off. This is the intended P8-3×P8-2 synergy
(`COORDINATION` §2, liveliness §6): fringe/door O→D demand *is* the spawn/despawn legitimacy; no per-spawn
gate call is needed on this path. `PedSpawnPolicy` remains the enforcement layer for *other* appearances
(non-sub-area demand, forced/jam despawn), and for a future non-endpoint despawn (route-to-sink/hold).

## 3. Determinism

Same discipline as today's `PedDemand`: one per-ped `VehicleRng` stream seeded `(config.Seed, id, salt)`;
the weighted draw is a cumulative-weight lookup on a fixed endpoint array (built once, deterministic order),
so which O/D a ped draws depends only on its id — never on spawn order or thread scheduling. A dedicated new
salt keeps the weighted draw independent of the existing timing/O-D/liveliness streams.

## 4. Wiring (inert-default, bit-identical)

- New `SubareaDemand` value: the weighted endpoint set + `DrawWeighted(ref VehicleRng)` (cumulative-weight
  binary search) + `FringeEndpointsFromNetwork(net, fringeEdges)` (edge → sidewalk-shape midpoint).
- `PedDemandConfig` gains an **optional** `SubareaDemand? WeightedEndpoints` (default null). When null,
  `TrySpawnOne` takes the EXACT current uniform `Origins`/`Destinations` path — no new RNG draw, byte-identical
  to today (the ITERON rule, like `Liveliness=null`). When set, it draws O/D endpoints from it instead.
- `Origins`/`Destinations` stay `required` but may be empty when `WeightedEndpoints` is set (the builder
  supplies O/D); a small guard picks the active source.

## 5. Tasks & success conditions

- [ ] **P8-3a — `SubareaDemand`** (weighted endpoints + deterministic weighted draw + fringe-from-network).
  *Success:* draw distribution matches the endpoint weights (a large deterministic sample is within tolerance
  of the weight proportions); every drawn endpoint is a fringe or POI edge; identical draws for identical seed.
- [ ] **P8-3b — wire into `PedDemand`** behind the optional `WeightedEndpoints`. *Success:* null → existing
  ped-demand tests byte-identical (bit-identical off); set → spawns/dests are all on fringe/POI edges
  (legitimacy by construction), population respects the cap, deterministic; a test drives the box's POIs+fringe.
- [ ] **P8-3c (later)** — density knob (P8-4 anchor on `manifest.density.knee`) scaling `SpawnRatePerSecond`;
  crossing-throughput guard. *(Follows once the weighted demand lands.)*

## 6. Invariants

- Inert-default: `WeightedEndpoints=null` leaves every committed ped golden byte-identical.
- No `System.Random`; per-ped seeded `VehicleRng`, dedicated salt.
- Endpoints legitimate by construction → no on-camera spawn pop on this path (P8-2 synergy).
