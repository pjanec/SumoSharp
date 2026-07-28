# TUTORIAL-PEDESTRIANS.md — running a pedestrian crowd from your own code

How to bake a walkable surface from a SUMO network, generate pedestrian demand over it, and run the
two-level level-of-detail system that makes tens of thousands of pedestrians affordable.

**The runnable companion is [`samples/PedestrianCrowd`](../samples/PedestrianCrowd)**, which is in
`Traffic.sln` — so CI compiles it and a snippet that stops compiling breaks the build instead of rotting
here. Run it:

```bash
dotnet run --project samples/PedestrianCrowd
```

Previous: [`TUTORIAL-VEHICLES.md`](TUTORIAL-VEHICLES.md) · Next:
[`TUTORIAL-LIVE-CITY.md`](TUTORIAL-LIVE-CITY.md) · Front door: [`PEDESTRIANS.md`](PEDESTRIANS.md) ·
Normative contract: [`PEDESTRIAN-NAVMESH-CONTRACT.md`](PEDESTRIAN-NAVMESH-CONTRACT.md)

## First, what this is not

**This is not a port of SUMO's person model.** SumoSharp does not reproduce SUMO's `<person>`
trajectories, and there is no golden FCD to diff a pedestrian against. The crowd layer sits on the
**live-reactivity axis, not the parity axis**, and is validated by behavioural and property tests.

That has a practical consequence for you: the vehicle parity guarantees do **not** transfer. What *is*
guaranteed is determinism (same seed, same step sequence, same trajectories) and that the whole subsystem
is **inert when absent** — with no pedestrians attached, the vehicle goldens and the determinism hash are
byte-unchanged. If you are wondering whether adding pedestrians can break parity: not by existing.

## The four objects, and how they stack

```
PedDemand          picks O/D pairs, routes them, spawns and despawns   <- you configure this
  PedLodManager    owns the low-power population + the ORCA crowd      <- you construct this
    SumoNavMesh    A* routing over baked polygons                      <- you bake this
      PedNetwork   sidewalks / crossings / walkingAreas from the net   <- parsed from .net.xml
```

Each layer only knows about the one below it. `PedDemand` populates the scenario; `PedLodManager` decides
*how expensively* each pedestrian is simulated; the navmesh answers "how do I walk from here to there".

## 1. Bake the walkable surface

```csharp
var network  = PedNetworkParser.Load(netPath, walkableAddPath);
var polygons = WalkablePolygonBaker.Bake(network);
var space    = new SumoWalkableSpace(polygons);
var nav      = new SumoNavMesh(polygons, space, network.PedConnections);
```

`walkableAddPath` is optional and adds polygons the net does not declare — a plaza, a parking lot.
Passing `network.PedConnections` lets the graph stitch portals from the net's **declared** pedestrian
connections, which a purely geometric pass misses. It is always safe to pass.

### Check the bake before you trust anything above it

```csharp
var componentCount = nav.ConnectedComponentCount();
```

**This one line is the highest-value diagnostic in the subsystem.** A well-connected network is `1`, or a
small handful of genuinely separate islands. If you get hundreds, the bake fragmented — real cropped
geometry produces sliver overlaps and near-abutting sidewalk ends that look connected to a human and are
not. Pedestrians will then fail to route, `PedDemand` will silently skip O/D pairs as unreachable, and your
crowd will be mysteriously empty. That failure mode is real enough to have its own investigation:
[`SUMOSHARP-P8-1-REAL-NET-NAVMESH.md`](SUMOSHARP-P8-1-REAL-NET-NAVMESH.md), and the three additive
connectivity passes that fixed it are in
[`PEDESTRIAN-P8-1B-NAVMESH-CONNECTIVITY-DESIGN.md`](PEDESTRIAN-P8-1B-NAVMESH-CONNECTIVITY-DESIGN.md) and
[`PEDESTRIAN-P8-1C-NAVMESH-CONTINUATION-DESIGN.md`](PEDESTRIAN-P8-1C-NAVMESH-CONTINUATION-DESIGN.md).

Watch `PedDemand.UnreachableSkipCount` too. A non-zero value that keeps climbing means the same thing.

## 2. The LOD manager

```csharp
var publisher = new PedPublisher();
var lod = new PedLodManager(nav, publisher, arriveRadius: 0.3, dwellSeconds: 1.0);
```

`PedPublisher` is the in-memory wire a DDS or image-generator consumer reads from. You may never inspect
it, but every LOD manager needs one.

`dwellSeconds` is the minimum time in a LOD state before it may flip again. It exists for a reason — see
§4.

## 3. Demand

```csharp
var config = new PedDemandConfig
{
    Origins      = new[] { pointA, pointB },
    Destinations = new[] { pointA, pointB },
    SpawnRatePerSecond = 1.0,
    PopulationCap = 6,
    Seed = 0xC0FFEE_1234UL,
    MaxSpeed = 1.4,          // m/s, typical adult walking speed
    Radius = 0.3,            // m, ORCA agent radius
    ArrivalRadius = 0.5,
};
var demand = new PedDemand(config, nav, lod, startTime: 0.0);
```

Then one call per tick:

```csharp
demand.Step(now, dt, interestField, worldDiscs);
```

`Seed` makes **every** random decision reproducible — when a pedestrian spawns, which O/D pair it draws.
Same seed plus same step sequence gives identical trajectories every time, because every draw comes from a
per-entity seeded stream rather than `System.Random`. Never introduce a `System.Random` here; the
determinism the whole replication story rests on depends on it.

**`PopulationCap` is closed-loop.** New pedestrians are inserted only while the live count is below the cap.
That means the resident count can never run away — and equally, that a run like this **cannot** tell you
anything about capacity or throughput. See the same warning in the live-city tutorial; it has already
produced one retracted measurement.

## 4. The part that matters: two-level LOD

This is the central idea of the whole subsystem, and the reason a city's worth of pedestrians is
affordable.

| Level | What it is | Cost |
| --- | --- | --- |
| **low-power** | `PathArc` — pose is a closed-form function of `(route, seed, width, time)` | **O(1)** per pedestrian, no neighbour query at all |
| **high-power** | a real agent in a persistent `OrcaCrowd`, doing reciprocal collision avoidance | the usual crowd-solver cost |

Pedestrians move between the two through an **interest field**:

```csharp
var field  = new InterestField();
var source = new InterestSource(centre, promoteRadius: 10.0, demoteRadius: 20.0);
field.Register(source);
```

A low-power pedestrian inside `promoteRadius` becomes a full ORCA agent. It demotes once it has been
continuously outside the larger `demoteRadius` for `dwellSeconds`. **The two radii are deliberately
different** — that gap is spatial hysteresis, and without it a pedestrian sitting exactly on one shared
radius flips level every single step. `dwellSeconds` is the temporal half of the same guard.

Register no interest source at all and the entire population stays low-power, whatever it walks past. The
sample makes this concrete by running two phases: with no source, `high-power = 0` throughout; with a
source at the junction every route passes through, the split moves `0 → 2 → 6`, back down as pedestrians
leave, then up again.

**Read the level back with `lod.ModelOf(id)`** — `PedDrModel.FreeKinematic` is high-power, anything else is
low-power.

### The consequence you will hit

**Only promoted pedestrians are visible to cars.** The vehicle-side crowd footprint source contains
high-power pedestrians only, by design. So outside your interest zones, a car sees a pedestrian *only* if
that pedestrian is on a crossing (crossing occupancy is tracked separately and covers everyone). This is a
real, measured limitation, not a bug — it is the open "out-of-zone cars are blind to pedestrians" item in
[`TASKS-TODO.md`](TASKS-TODO.md), and it bounds how far any car-side pedestrian-safety work can go. If you
need cars to react to pedestrians somewhere, put an interest source there.

## 5. Reading poses back, with elevation

```csharp
var pos       = lod.PositionOf(id, now);
var elevation = lod.ElevationOf(id, now);

var route      = nav.FindPath(from, to, out var vertexSurfaces);
var elevations = nav.ElevationsAlong(route, vertexSurfaces);
```

**Elevation is mandatory, and that is deliberate.** There is no 2-D-defaulting `FindPath(from, to)`
overload and no defaulted `ElevationsAlong` — the 2-D siblings were deleted, so an omitted height is a
compile error rather than a silent zero. The blocked-set query is named `FindPathAvoiding` precisely so the
compiler cannot bind a three-argument `FindPath` call to it by accident.

`vertexSurfaces` is per-vertex lane/surface provenance, and it is what stops a footbridge and the path
beneath it collapsing onto one height. Pass it through; do not discard it. `ElevationOf` resolves a live
pedestrian's height **along its own path**, never by a nearest-lane search, for the same reason.

> **If you see `z = 0.00`, check the net before filing a bug.** On a flat 2-D net — including
> `poc0-crossing-plaza`, which the sample uses — zero is the correct answer. There is *also* a genuine open
> bug where low-power pedestrians report `z = 0` on a real 3-D net
> ([`TASKS-TODO.md`](TASKS-TODO.md)). The two are indistinguishable on screen, which is why the sample
> prints which case it is in. For real relief use `scenarios/_ped/georef_min` (27.5 m), asserted by
> `tests/Sim.LiveCity.Tests`' `PedElevation*` tests.

## Beyond the basics

Everything below is built and default-off. [`PEDESTRIAN-TRACKER.md`](PEDESTRIAN-TRACKER.md) is the
authoritative done-and-parked map.

- **Liveliness** — activity timelines (walk / pause / sit / step inside a building), scheduled two-person
  meet-and-talk, templated actors. All still low-power.
  [`PEDESTRIAN-LIVELINESS-DESIGN.md`](PEDESTRIAN-LIVELINESS-DESIGN.md).
- **Deterministic lateral weave** (`PedDemandConfig.EnableWeave`) — offsets low-power pedestrians onto
  their own half of a sidewalk as a pure function of `(route, seed, width, time)`, so opposing flows thread
  a shared pavement at O(1) per pedestrian with no neighbour queries.
  [`PEDESTRIAN-WEAVE-PRODUCTION-DESIGN.md`](PEDESTRIAN-WEAVE-PRODUCTION-DESIGN.md).
  ⚠ Weave changes how crowds *look*; it does **not** make low-power pedestrians avoid each other. They
  still pass through one another — that is the open PED-REALISM-1 item.
- **Density as a dial** — `PedDensityKnob`, pedestrians per walkable km.
  [`PEDESTRIAN-P8-4-DENSITY-DESIGN.md`](PEDESTRIAN-P8-4-DENSITY-DESIGN.md).
- **Legitimate spawn points** — weighted POI and fringe endpoints so pedestrians appear and vanish only
  where a real person would. [`PEDESTRIAN-P8-3-DEMAND-DESIGN.md`](PEDESTRIAN-P8-3-DEMAND-DESIGN.md).
- **An alternate navmesh provider** — `Sim.Pedestrians.Nav.DotRecast`, cross-checked against `SumoNavMesh`.
- **Streaming to a renderer** — because a low-power pose is a pure function of its inputs, the server and
  every remote image generator reconstruct it **bit-for-bit**: a route is broadcast once and ambient
  pedestrians emit zero per-step bytes. Proven over an in-process byte loopback and real CycloneDDS.
  [`PEDESTRIAN-DDS-TRANSPORT-DESIGN.md`](PEDESTRIAN-DDS-TRANSPORT-DESIGN.md).

## Watching it without writing code

```bash
dotnet run -c Release --project src/Sim.Viz -- --ped-lod-promotion ped-lod.html
dotnet run -c Release --project src/Sim.Viz -- --ped-dense-city ped-dense.html
dotnet run -c Release --project src/Sim.Viz -- --ped-remote ped-remote.html   # rendered FROM the wire
```

13 pedestrian scenes in all — see [`TOOLS.md`](TOOLS.md). Tests:
`dotnet test tests/Sim.Pedestrians.Tests -c Release` (324/324).

**Next:** [`TUTORIAL-LIVE-CITY.md`](TUTORIAL-LIVE-CITY.md) couples this crowd to traffic, so cars yield to
the pedestrians you just created.
