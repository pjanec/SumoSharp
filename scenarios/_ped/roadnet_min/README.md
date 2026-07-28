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

## Offline net preparation (making an arbitrary net pedestrian-capable)

This fixture is built from scratch by `netgenerate` (see `provenance.txt`), but the more common case
for a **real** road-net-import dataset is starting from a bare, vehicle-only `net.xml` that has no
pedestrian infrastructure at all. `scripts/prep-ped-net.sh` (docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md
§8) is a small wrapper around the `netconvert` guessing recipe that adds one:

```
scripts/prep-ped-net.sh <in.net.xml> <out.net.xml>
```

which runs

```
netconvert --sumo-net-file <in.net.xml> \
           --sidewalks.guess --crossings.guess --walkingareas \
           -o <out.net.xml>
```

This is dev-side tooling only (needs `netconvert` on PATH) and is **never** invoked by `dotnet test`
or any other part of the offline test loop; commit the resulting `net.xml` as a new scenario input if
it should persist, exactly as this fixture's own `net.xml` is committed.
