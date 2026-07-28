# LiveCity

The smallest possible consumer of **SumoSharp.LiveCity** — a tutorial-style walkthrough of the coupled
cars+pedestrians simulation: cars (`Engine` + Krauss following) and a pedestrian crowd
(`SumoSharp.Pedestrians`) sharing one net, with cars yielding to crossing pedestrians via a composite
crowd-footprint source.

> Unofficial, independent C# reimplementation of Eclipse SUMO's microscopic simulation core. Not
> affiliated with or endorsed by the Eclipse SUMO project.

## What this shows

- Building a config with `LiveCityConfig.ForDataset(dir)` over the committed
  `scenarios/_ped/georef_min` fixture, and the knobs that matter (`CarTargetConcurrent`,
  `PedPopulationCap`, `Dt`) — the same fields the `LIVECITY_CARS` / `LIVECITY_PEDS` / `LIVECITY_HZ`
  environment gates override.
- **The coupling**: cars see pedestrians through `Engine.CrowdSource`, so a car brakes for a pedestrian
  on or crossing a crosswalk. The sample counts this exactly, per step, via
  `LiveCitySim.WitnessAuthoritative()`'s per-car `Binder` diagnostic (`Binder == 13` is
  `CrowdLongitudinalConstraint`, "brake for a crowd agent ego is still laterally overlapping") rather
  than a speed-threshold guess.
- Stepping the coupled sim and sampling it back (`Sample()`), printing live car/ped counts, the number
  of cars currently held for a pedestrian, and occupied-crossing counts.
- Why `CarTargetConcurrent`/`PedPopulationCap` are **closed-loop** caps, and why that makes a run like
  this one invalid evidence about capacity or throughput (`Sim.BenchLiveCity --inflow` is the open-loop
  tool for that).

Every call in `Program.cs` is commented inline, in order, as a tutorial. See also `docs/ENV-GATES.md`
(mandatory reading before any A/B measurement with this host), `docs/LIVE-CITY-HARNESS-GUIDE.md`, and
`docs/LIVE-CITY-STATUS.md`.

## Run it

```bash
dotnet run --project samples/LiveCity
```

Steps the coupled sim 200 times and prints a report line every 10 steps, ending with a summary of how
many steps had at least one car held for a pedestrian.
