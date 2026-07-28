# livecity-mega — a net that can host 5,000 cars AND 20,000 pedestrians at once

Built for `docs/LIVE-CITY-PERF-SESSION-LOG.md` A12: **no existing scenario could host the
owner's target (5,000 vehicles + 20,000 pedestrians concurrently)** — the default demo net
gridlocks the engine at ~3,084/5,000 cars, and the 11 km central-Geneva road-net cut fills
cars fine but has almost no pedestrian infrastructure (plateaus at ~40 concurrent peds). This
net is purpose-built to clear both bars at once.

## What it is (and is NOT)

- **It is** a single committed `net.net.xml` (`netgenerate` grid, 2 lanes/direction, guessed
  sidewalks + crossings + walkingareas + guessed traffic lights) plus a minimal `config.sumocfg`
  with **no route file** — `Sim.LiveCity` generates its own procedural car and pedestrian demand
  at runtime (`LiveCityConfig.ForSumocfg`), so nothing here needs a `<route-files>` entry.
- **It is NOT a SUMO-parity scenario.** Unlike `city-3000`/`city-15000` there is no
  `golden.*`/`tolerance.json`/`aggregate-tolerance.json` and no SUMO reference trajectory —
  this net exists to be filled by `Sim.LiveCity`'s own engine, not diffed against SUMO.

## Net parameters

```
netgenerate --grid --grid.number=15 --grid.length=500 -L 2 \
            --sidewalks.guess --crossings.guess --walkingareas --tls.guess \
            --no-turnarounds --seed 42 -o net.net.xml
```

15x15 junction grid, 500 m block length, 2 lanes/direction, sidewalks + crossings +
walkingareas guessed on every edge/junction, traffic lights guessed. Result: **8,999 lanes,
extent ~7,017 m x 7,017 m**. See `provenance.txt` for the exact net-sizing exploration (why
this beats the task's literal "40x40 grid @ 200 m" suggestion — junction density, not block
length or lane-km, is what makes RouteGraph pedestrian routing expensive on an arbitrary net).

## Reproduce / verify

```
dotnet run -c Release --project src/Sim.BenchLiveCity -- \
    --sumocfg scenarios/_bench/livecity-mega/config.sumocfg \
    --cars 5000 --peds 20000 --fill-steps 1500 --steps 40 --warmup 5
```

with `LIVECITY_COOP=1 LIVECITY_PEDYIELD=1 LIVECITY_YIELD=1 LIVECITY_WRONGLANE=1
LIVECITY_DRIVETHROUGH=1 LIVECITY_HELDSWERVE=1` all set explicitly (CLAUDE.md rule 10 — these
gates are process-global). **Report the printed ACHIEVED counts, never the requested ones.**

Last measured (see `provenance.txt` for the full block): **4,872/5,000 cars (97.4%, still
rising — not gridlocked) and 20,000/20,000 peds (100%)** at the target combined load.
