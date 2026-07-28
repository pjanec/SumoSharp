# TUTORIAL-VEHICLES.md — driving the engine from your own code

How to embed SumoSharp's vehicle simulation in your own program: load a network, create traffic at
runtime, step it, and read the results back.

**Every snippet here is backed by a sample that CI compiles.** The runnable companion is
[`samples/HelloTraffic/Program.cs`](../samples/HelloTraffic/Program.cs) — it is in `Traffic.sln`, so a
snippet that stops compiling breaks the build rather than quietly rotting in this file. Run it:

```bash
dotnet run --project samples/HelloTraffic
dotnet run --project samples/HelloTraffic -- path/to/your/net.net.xml
```

Next: [`TOOLS.md`](TOOLS.md) if you want a CLI instead of code · [`SUMOSHARP-API.md`](SUMOSHARP-API.md)
for the API of record. The pedestrian and live-city tutorials land next, each with its own runnable sample.

## Two ways in, and they are not interchangeable

| You want | Call | You get |
| --- | --- | --- |
| A committed SUMO scenario, run as SUMO would | `LoadScenario(sumocfgPath)` | net + demand + config from the files. Vehicles arrive from `<flow>`/`<vehicle>` as declared. This is the parity path. |
| An empty road network you populate yourself | `LoadNetwork(netXmlPath)` | just the network. No demand. **This is what a game or digital twin wants.** |

`LoadScenario` also has a three-argument form (`netXml`, `rouXml`, `sumocfg`) when the files are not
arranged the way a `.sumocfg` expects, and `LoadNetwork` takes an optional `ScenarioConfig` if you need to
set the step length or begin time without a config file.

## The five calls that are the whole API

```csharp
var engine = new Engine();
engine.LoadNetwork(netPath);                                     // 1. network only, no demand

var car = engine.DefineVType(new VTypeParams { Sigma = 0.0, MaxSpeed = 13.89 }, id: "car");  // 2.

var v1 = engine.SpawnVehicle(car, fromEdge: "SA", toEdge: "DE"); // 3. routed by the built-in router
var v2 = engine.SpawnVehicle(engine.DefaultVType, "AB", "DE");   //    or use the default passenger vType

engine.Step();                                                   // 4. advance one step

foreach (var h in engine.VehicleHandles)                         // 5. read back
{
    if (engine.TryGetVehicle(h, out var s))
    {
        Console.WriteLine($"{s.VehicleId} lane={s.LaneId} x={s.X:F2} speed={s.Speed:F2}");
    }
}
```

### `Sigma = 0.0` is not decoration

`Sigma` is Krauss driver imperfection — the random dawdle. **Set it to 0 unless you specifically want
stochastic behaviour.** With `Sigma > 0` your runs stop being reproducible, and the whole phase-1
determinism guarantee (and every parity golden) depends on it being 0. If you do want randomness, note the
engine never uses `System.Random`: it uses per-entity seeded RNG (SplitMix64, hashed from the entity id),
so results are independent of thread order. Never introduce a `System.Random` here.

### `SpawnVehicle` returns immediately; the vehicle does not appear immediately

The handle comes back in the **Pending** state. SUMO-parity queued insertion places the vehicle on the road
at the next `Step()` where a safe gap exists — so a car you spawned may not show up in `VehicleHandles` for
several steps on a busy lane, and on a saturated one it may never be inserted. That is correct behaviour,
not a bug. If you are counting vehicles, count what the engine reports, never what you asked for.

There are four overloads. The `string fromEdge, string toEdge` one runs the built-in Dijkstra router over
the connection-turn graph. If you already know the route, pass the edge list — and if you are spawning in
bulk, the `ReadOnlySpan<int>` / `int` handle-based overloads skip the string lookups entirely.

### Reading state back

`TryGetVehicle` projects one vehicle into a `VehicleState` record — position, speed, lane, angle. It
returns `false` for a handle that is no longer live (arrived or removed), so a stale handle is an inert
`false` rather than an exception.

For bulk reads at scale, do not loop `TryGetVehicle` over thousands of handles — the engine exposes a
columnar, zero-allocation read surface (`VehicleReadBuffer`, the SoA spans) designed for exactly that.
See [`SUMOSHARP-API.md`](SUMOSHARP-API.md) §4.

## Injecting things SUMO does not control

This is the capability SUMO has no concept of: **external agents that cars react to.** Pedestrians, crowd
agents, live detections — anything you can express as a footprint on a lane.

```csharp
int laneHandle = engine.GetLane("AB_0");            // resolve the string id ONCE, at setup
ObstacleHandle h = engine.AddObstacle(laneHandle, frontPos, length, /* ... */);
engine.UpdateObstacle(h, frontPos, speed);          // per step, from your own source of truth
engine.RemoveObstacle(h);                           // agent left the roadway -> cars resume
```

The API is **handle-based, not string-keyed**. `AddObstacle` / `AddMovingObstacle` always create a new slot
and return a fresh `ObstacleHandle` you must retain; handles are generation-validated, so calling
`UpdateObstacle` or `RemoveObstacle` on a stale one is an **inert no-op**, not a crash and not an
accidental write to a recycled slot. Resolve lane ids to handles at setup, not per step.

Cars respond by braking to a Krauss gap, treating a moving agent as a dynamic leader or a lane-change
blocker, or — when braking alone cannot stop them — swerving within the lane, then spilling into a gap-safe
adjacent lane. Obstacles are frozen once per step, so the outcome is independent of the order you add them.

Full guide, including the reaction taxonomy: [`EXTERNAL-AGENTS-VIZ.md`](EXTERNAL-AGENTS-VIZ.md).

## Starting from populated traffic instead of an empty map

An empty network takes a long time to reach a realistic state. Two options:

```csharp
engine.WarmUp(300);                  // deterministically pre-populate, then carry on
engine.SaveSnapshot(path);           // persist every vehicle (cars and trains) + the engine state machines
engine.LoadSnapshot(path);           // resume from live traffic
```

`WarmUp` is deterministic — the same call gives the same starting state every time — and it works on a
network loaded either way. The snapshot round-trip is cross-checked against SUMO's own `--save-state`
(`golden.state.mid.xml`), so it is not merely self-consistent.

**The snapshot API has real preconditions, and it throws loudly rather than mis-restoring.** Read these
before designing around it (`src/Sim.Core/EngineSnapshot.cs`):

- **It requires `LoadScenario`, not `LoadNetwork`.** It needs a demand model, so
  `InvalidOperationException` if you took the network-only path above. That is the one that will catch you,
  because network-only is otherwise the recommended route for a game or digital twin.
- `NotSupportedException` if the network has **actuated traffic lights** — their phase and per-detector
  occupancy are stateful and not clock-derivable, and `LoadScenario` would rebuild them at their Begin
  state while the clock jumped.
- `NotSupportedException` for a vehicle that **departed via `departLane="best"`**, has **scheduled stops**,
  or has **rerouted** — that progress is not captured yet.
- Saving before any step writes an empty `t = Begin` snapshot rather than failing.

Every one of those is a deliberate guard: the alternative is a snapshot that restores *almost* correctly,
which is far worse to debug than an exception at save time.

## Per-vehicle overrides at runtime

`SetVehicleParams(handle, new VehicleParamOverride { ... })` changes one vehicle's driving parameters
mid-run — the seam the panic-evacuation layer uses to switch a driver to an aggressive flee preset without
touching the engine's own logic. It is a public seam precisely so a layer like that can live *outside*
`Sim.Core` and stay parity-exempt.

## What will bite you

**Parity-safe versus behavioural.** Anything that changes trajectories is subject to `CLAUDE.md` prime
directive 3: it may not push any scenario outside its `tolerance.json`. `Sigma`, integration method,
`actionStepLength` and the junction gates are all behavioural. If you flip one and a golden moves, the
change is wrong — not the golden.

**Environment gates are process-global.** Before you benchmark or A/B anything, read
[`ENV-GATES.md`](ENV-GATES.md). 34 variables, several behavioural, and a value inherited from your shell is
indistinguishable from one you set deliberately. One of them makes 14 goldens fail.

**Parallelism must never change results.** The plan, export and post-move phases auto-parallelize above
256 concurrent vehicles. That is safe by construction — they read only frozen start-of-step state and write
only their own vehicle's intent, with structural changes deferred to a command buffer — and it is *gated*
by `Sim.Bench` asserting the single-threaded and parallel determinism hashes are equal. If you add a phase,
that invariant is yours to preserve. Note the measured sweep on a 24-thread box: **8 threads beat 24**, and
the efficiency knee is around 4, so more parallelism is not automatically better.

**Don't trust a per-tick number without its demand model.** A throughput figure from closed-loop demand
(insert only while `live < cap`) cannot express a capacity deficit — it self-throttles. `CLAUDE.md`
measurement-discipline #4, and it has already produced one retracted claim.

## Where to go next

- **Add pedestrians:** [`PEDESTRIANS.md`](PEDESTRIANS.md) is the front door today; a tutorial with a
  runnable sample lands next.
- **Couple both into one scene:** [`LIVE-CITY-HARNESS-GUIDE.md`](LIVE-CITY-HARNESS-GUIDE.md) and
  `src/Sim.LiveCity/README.md`; a tutorial with a runnable sample lands next.
- **Stream it to a renderer:** [`samples/StreamingLoopback`](../samples/StreamingLoopback) then
  [`samples/MotionReconstruction`](../samples/MotionReconstruction) — the transport contract, then turning
  a sparse low-rate stream into smooth per-frame poses.
- **Embed in Unity or Godot:** [`samples/SumoSharp.GameHostSample`](../samples/SumoSharp.GameHostSample)
  drives `GameHost`, the netstandard2.1-clean integration class.
- **Which package do I install:** [`PACKAGES.md`](PACKAGES.md)
- **Architecture, and why the data structures differ from SUMO's:** [`DESIGN.md`](DESIGN.md)
