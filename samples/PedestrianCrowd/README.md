# PedestrianCrowd

The smallest possible consumer of **SumoSharp.Pedestrians** — a tutorial-style walkthrough of the
pedestrian subsystem end to end, headless: bake a navmesh from a real SUMO net, generate reproducible
O/D demand, and watch the two-level sim-LOD (low-power `PathArc` followers vs high-power full-ORCA
agents) promote and demote as pedestrians near an "interest source".

> Unofficial, independent C# reimplementation of Eclipse SUMO's microscopic simulation core. Not
> affiliated with or endorsed by the Eclipse SUMO project.

## What this shows

- Loading a committed pedestrian network and **baking a navmesh** —
  `WalkablePolygonBaker.Bake(PedNetwork)` → `new SumoNavMesh(polygons, space, pedConnections)` — and
  reading back `SumoNavMesh.ConnectedComponentCount()` as a direct diagnostic that the bake produced one
  walkable surface, not a shattered mess.
- Creating the LOD manager (`PedLodManager`) and origin/destination demand (`PedDemand`) that populates
  the scenario itself: pick an O/D pair, route it once, spawn low-power, despawn on arrival. Every random
  decision is seeded (`PedDemandConfig.Seed`) — the engine never uses `System.Random`.
- **The central idea of the LOD subsystem**: registering an `InterestSource` at the network's junction
  and watching the live low-power/high-power split change as pedestrians walk into and back out of its
  promote/demote radii.
- Reading a pose back with its elevation via the mandatory-z API (`IPedNavigation.ElevationsAlong`,
  `PedLodManager.ElevationOf`) — 0.0 on this fixture's flat 2-D net, the same API a 3-D net would return
  real heights from.

Every call in `Program.cs` is commented inline, in order, as a tutorial.

## Run it

```bash
dotnet run --project samples/PedestrianCrowd
```

Loads the committed `scenarios/_ped/poc0-crossing-plaza` fixture (a 4-arm signalized junction with
sidewalks, TLS-controlled crossings, walkingAreas, and a plaza/parking-lot walkable polygon), steps
100 times, and prints the live low-power/high-power split at each report interval, then a pose+elevation
read-back for every ped still live at the end.
