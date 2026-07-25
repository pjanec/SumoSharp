# `roadnet_min` — road-net-import regression fixture

A tiny **synthetic, ped-equipped** SUMO network (a 3×3 grid, ~240 m × 240 m) used to test the
**arbitrary road-net import** path (`LiveCityConfig.ForDataset` → `SumoRouteGraphNav`) in the offline
test loop. See `docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md` §9 and `-TASKS.md` E2/E3.

## What it is (and is NOT)

- **It is** a single committed `net.xml` — an *input* to `LiveCitySim` in `RouteGraph` mode: sidewalks +
  crossings + walkingareas + full pedestrian `<connection>` stitching, plus one-lane vehicle edges.
- **It is NOT a parity golden.** There is no `golden.*`/`tolerance.json`; nothing here is compared to a
  SUMO-produced trajectory. Tests assert SumoSharp's own behaviour (peds route on the ped graph and cross at
  crossings, with **no** walkable-polygon bake). So — unlike `demo_city/box` — the SUMO version used to
  generate it is not a parity concern. Full detail in `provenance.txt`.

## Consumed by (offline, no SUMO at test time)

- `tests/Sim.LiveCity.Tests` — the road-net smoke/regression test (`ForDataset` → N steps → peds route +
  cross; nav is a `SumoRouteGraphNav`; bake-free).

## Regenerating

See the exact `netgenerate` command in `provenance.txt`. It needs SUMO installed (dev-side only); the
offline `dotnet test` loop never invokes SUMO — it only reads this committed `net.xml`.
