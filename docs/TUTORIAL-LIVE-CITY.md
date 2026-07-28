# TUTORIAL-LIVE-CITY.md — coupling cars and pedestrians into one simulation

How to run traffic and a pedestrian crowd on the same network, coupled so that cars actually yield to
pedestrians on a crosswalk — and how to measure that coupling without fooling yourself.

**The runnable companion is [`samples/LiveCity`](../samples/LiveCity)**, in `Traffic.sln` so CI compiles it:

```bash
dotnet run --project samples/LiveCity
```

Previous: [`TUTORIAL-VEHICLES.md`](TUTORIAL-VEHICLES.md) ·
[`TUTORIAL-PEDESTRIANS.md`](TUTORIAL-PEDESTRIANS.md) — read both first; this tutorial assumes them.
Status: [`LIVE-CITY-STATUS.md`](LIVE-CITY-STATUS.md) · Harness:
[`LIVE-CITY-HARNESS-GUIDE.md`](LIVE-CITY-HARNESS-GUIDE.md)

## The whole thing is three calls

```csharp
var config = LiveCityConfig.ForDataset(datasetDir);
config.CarTargetConcurrent = 20;
config.PedPopulationCap    = 60;
config.Dt                  = 0.5;          // 2 Hz

using var sim = new LiveCitySim(config);

for (var step = 1; step <= 200; step++)
{
    sim.Step();
    var snap = sim.Sample();               // cars, peds, crossing occupancy
}
```

`LiveCitySim` is the shared host: it parses the net, bakes the pedestrian navmesh, wires the `Engine`,
`PedLodManager`, `PedDemand` and crossing-signal machinery together, and reproduces the reference recipe in
`src/Sim.Viz/SceneGen.cs` as a real-time, steppable object. `Step()` drives everything — car insertion,
Krauss following, lane changing, pedestrian spawn and despawn, LOD promotion, crossing signals, and the
coupling. Nothing else is required of you.

### Getting a network in

| You have | Call |
| --- | --- |
| a directory with `net.xml`, or a cut-style `scenario.net.xml` | `LiveCityConfig.ForDataset(dir)` |
| a `.sumocfg` | `LiveCityConfig.ForSumocfg(path)` — resolves `<net-file>`/`<route-files>` as `sumo -c` would |
| the repo's own demo dataset | `LiveCityConfig.ForRepoRoot(repoRoot)` |

`ForDataset` resolves the net by convention, so directory naming matters. `scenarios/_ped/georef_min` is
named the way a real preprocessed cut is and works with no path configuration;
`scenarios/_ped/poc0-crossing-plaza` does **not**, because its net is `net.net.xml` and it has no
`scenario.rou.xml`. If `ForDataset` cannot find your net, that is why.

A route file, if present, is scraped **for spawn edges only**. `LiveCitySim` generates its own procedural
car and pedestrian demand — it does not replay your `.rou.xml`. If you want SUMO's declared demand replayed
faithfully, you want `Engine.LoadScenario`, not this host.

## The coupling: one seam

Cars see pedestrians through **`Engine.CrowdSource`**, set inside the `LiveCitySim` constructor to a
composite of the promoted-pedestrian footprint source and the crossing-occupancy source. That single seam is
what makes a car brake for a pedestrian — using the same Krauss following model it would use for a car
ahead, with a pedestrian disc standing in as the leader.

This is worth being precise about, because it decides what your simulation can and cannot do:

- **The car-following model is unchanged.** It is still the SUMO-ported Krauss model the parity bar applies
  to. What is new is *what* it reacts to, not *how*.
- **Only promoted (high-power) pedestrians are in the footprint source.** Low-power pedestrians are not, by
  design. Crossing occupancy is tracked separately and covers everyone, promoted or not — which is why a
  car will stop for a pedestrian on a crosswalk even out in the cheap-LOD hinterland, but will *not* see a
  low-power pedestrian standing in the road elsewhere. Put an interest source where you need real reactions.
- **This is a live-reactivity concern, not a parity one.** The whole path is gated on `CrowdSource != null`,
  which no golden attaches, so all 661 parity goldens stay byte-identical.

## Measuring the coupling — the part most people get wrong

The naive check is "count cars below some speed". **Don't.** A car can be slow for a dozen reasons — a red
light, a car ahead, a junction yield — and a speed threshold cannot tell you which. Counting slow cars and
calling it yielding is an occupancy metric masquerading as a causal one, and this repo has a scar from
exactly that mistake: a tally once read 5-of-9 where the causal answer was **0-of-9**.

Ask the engine which constraint actually bound the car:

```csharp
var held = 0;
foreach (var w in sim.WitnessAuthoritative())
{
    if (w.Binder == 13)     // CrowdLongitudinalConstraint -- Engine.cs:5393
    {
        held++;
    }
}
```

Every car's `Binder` byte names **which** speed constraint won the fixed-order `Math.Min` fold over all
candidates that step. `13` is `CrowdLongitudinalConstraint` specifically: *this* car, *this* step, is
slowing for a pedestrian through the `CrowdSource` seam — not for a light, not for another car. That is an
exact count, not a proxy.

Sample it **every** step, not just on your reporting interval: a yield event can be brief, because a car
clears its own gap quickly. The sample's run over 200 steps at 20 cars / 60 pedestrians reports 27 steps
with at least one car held, peak 5 simultaneously.

Two other binders are worth knowing, both verified in `Engine.cs`: `3` is
`FreeFlowDesiredSpeedConstraint` (nothing is holding the car back — it is at its own desired speed), and
`16` is `CrowdYieldConstraint`, the *anticipatory* yield against a pedestrian's predicted corridor rather
than its current overlap. A car weaving past a pedestrian shows `3`; a car holding for one shows `13` or
`16`. [`LIVE-CITY-CAR-YIELDS-PED-DESIGN.md`](LIVE-CITY-CAR-YIELDS-PED-DESIGN.md) has the full story.

## ⚠ Two things that will invalidate your numbers

### 1. The caps are closed-loop

`CarTargetConcurrent` and `PedPopulationCap` insert a new agent **only while the live count is below the
cap**. Inflow is therefore throttled by your own drain, and the resident count can never run away however
congested the network gets.

**So a run like the sample's cannot support any claim about capacity, throughput or discharge rate.** It can
only show whether the cap was reached and held. This is not a theoretical caveat: a closed-loop measurement
once reported "96% of SUMO" while an open-loop run on the same engine climbed 258 → 2623 vehicles and never
reached steady state. For a real capacity measurement use open-loop inflow:

```bash
dotnet run -c Release --project src/Sim.BenchLiveCity -- --inflow 1.4 --steps 2000
```

Always label a measurement with the demand model that produced it.

### 2. The environment gates are process-global

**Read [`ENV-GATES.md`](ENV-GATES.md) before you benchmark or A/B anything.** `LiveCityConfig` applies **14**
`LIVECITY_*` overrides at construction and `LiveCitySim` applies more on top, several of which are
**behavioural** — they change trajectories, not just speed. `LIVECITY_CARS`, `LIVECITY_PEDS` and `LIVECITY_HZ` override exactly the three knobs the sample
sets in code, so `LIVECITY_CARS=40 dotnet run --project samples/LiveCity` silently changes the run.

An inherited shell value is indistinguishable from a deliberately-set one. **Set every gate you care about
explicitly, in both arms of any comparison.** One gate (`LIVECITY_MINORARRIVALSPEED`) enables a refuted
change that makes 14 parity goldens fail; if it is exported in your shell you will get 14 failures with no
visible cause.

That doc's "three-state trap" section is also worth your time: it documents a still-open bug where the
`sumosharp` drop-in binary runs three junction gates OFF that every other host runs ON.

## Running it on your own city

The host loads arbitrary networks, including georeferenced ones far from the origin — the world is recentred
so `float` rendering keeps sub-millimetre precision at UTM coordinates where an identity transform loses the
detail entirely. Design: [`LIVE-CITY-ARBITRARY-NET-DESIGN.md`](LIVE-CITY-ARBITRARY-NET-DESIGN.md) and
[`EXTERNAL-NET-VIEWER-DESIGN.md`](EXTERNAL-NET-VIEWER-DESIGN.md).

Live density changes with no rebuild — `LiveCitySim.SetCarDensity(targetConcurrent, spawnPerStep)` and
`SetPedDensity(populationCap, spawnRatePerSecond)`, which is what the viewer's sliders drive.

If you are embedding this in a renderer, do **not** call `Step()` from your frame loop — a single step at
scale is far longer than a frame, and the frame blocks for all of it. `LiveCitySim` itself has no threading;
the producer-thread wrapper lives one layer up, in the City3D viewer's
`demos/City3D/CityLib/LiveCitySource.cs` (`StartThreadedTick()`, then read `Published` /
`CopyCrossingSignals` instead of `Tick`/`Sample`, which throw once threaded). Copy that pattern rather than
rolling your own: [`LIVE-CITY-THREADED-TICK-DESIGN.md`](LIVE-CITY-THREADED-TICK-DESIGN.md) — read **§8**,
which records what actually landed and where the original lock-free design in §5 was wrong, including a
stale-snapshot bug a test caught.

## Watching it

```bash
dotnet run -c Release --project src/Sim.Viz -- --live-city live-city.html   # HTML replay, no GPU
```

For 3-D on a real network, the Godot viewer in `demos/City3D/` (`--dataset=`, `--sumocfg=`); press `H` to
cycle the camera realism zone, which promotes pedestrians to full ORCA in the area you are looking at while
distant traffic keeps the cheap LOD. See [`TOOLS.md`](TOOLS.md).

## Where the bodies are buried

This host is the most actively-developed part of the repo and it has known open issues. Before concluding
you have found a new bug, check [`TASKS-TODO.md`](TASKS-TODO.md) — in particular a pre-existing ~3 m car–car
overlap on internal junction lanes, junction discharge running slower than SUMO's at the same halting
fraction, and out-of-zone cars being blind to non-crossing pedestrians (§4 above).

Tests: `dotnet test tests/Sim.LiveCity.Tests -c Release` (90/90) — **not in `Traffic.sln`**, so
`dotnet build -c Release` does not build it and neither does `dotnet test Traffic.sln`. `src/Sim.LiveCity`
itself is outside the solution too, which means the main gate will not tell you when you break
`LiveCitySim`. Build that csproj explicitly.
