# TOOLS.md — which tool do I run?

There are 17 runnable entry points under `src/`, plus the Godot viewer under `demos/City3D/` and 16
scripts. This page routes you to the right one for a task and tells you the **caveat that makes the
difference between a measurement and a retraction**.

**It does not list flags.** Every entry point has a working `--help`, and that output is the reference —
a flag list copied into a doc rots, and this repo has already paid for that once. Run `--help`, come here
for *which tool* and *what to watch out for*.

If you want to drive the engine from your own code rather than a CLI, the tutorials are a ladder and are
meant to be read in order: [`TUTORIAL-VEHICLES.md`](TUTORIAL-VEHICLES.md) →
[`TUTORIAL-PEDESTRIANS.md`](TUTORIAL-PEDESTRIANS.md) → [`TUTORIAL-LIVE-CITY.md`](TUTORIAL-LIVE-CITY.md).
Each is backed by a runnable sample that `Traffic.sln` compiles.

## ⚠ Read before you measure anything

These four have each invalidated a real result in this repo.

1. **Env gates are process-global.** 34 `LIVECITY_*` / `SUMOSHARP_*` / `CITY3D_*` variables, several
   behavioural. A value inherited from your shell is indistinguishable from one you set. **Set every gate
   you care about explicitly, in both arms.** [`ENV-GATES.md`](ENV-GATES.md). One of them
   (`LIVECITY_MINORARRIVALSPEED`) makes 14 goldens fail.
2. **`dotnet build -c Release` does not build every test project.** `tests/Sim.LiveCity.Tests` and
   `demos/City3D/CityLib.Tests` are **not in `Traffic.sln`**. Build those csproj files explicitly or you
   are testing stale code.
3. **Repacking City3D serves a stale engine.** `demos/City3D/build.sh --pack-only` always writes version
   `0.1.0`, so NuGet's global cache wins. `rm -rf ~/.nuget/packages/sumosharp.*` before repacking.
4. **The car/ped caps are closed-loop.** `LIVECITY_CARS` / `--cars` insert only while `live < cap`, so
   inflow is throttled by our own drain and the resident count cannot run away. **A capacity or discharge
   claim measured that way is invalid** — it once reported "96% of SUMO" while an open-loop run climbed
   258 → 2623 and never reached steady state. Use `--inflow` (open-loop) for anything about capacity.

## By what you are trying to do

### Watch a committed scenario, no GPU, no SUMO

`Sim.Viz` writes a single self-contained `replay.html` (Canvas 2D). This is the everything-committed path
and the one to reach for first.

```bash
dotnet run -c Release --project src/Sim.Viz -- scenarios/11-priority-junction
# then open scenarios/11-priority-junction/replay.html
```

`Sim.Viz` also renders **18 named scenes** directly, with no scenario directory needed: **13 pedestrian**
(`--ped-dense-city`, `--ped-weave-city`, `--ped-remote`, `--ped-lod-promotion`, `--ped-crossing-gate`,
`--ped-od-routing`, `--ped-dodge`, `--ped-reroute`, `--ped-parking`, `--ped-liveliness`, `--ped-social`,
`--ped-waiter`, `--ped-lively-crowd`), **3 evacuation** (`--evac-organic`, `--evac-city`,
`--evac-district`), and **2 coupled** (`--live-city`, `--live-city-demo`). Its remaining flags are
CSV/diagnostic dumps, not scenes — `--help` is authoritative. Whole gallery at once:
`scripts/gen-demos.sh`, then open `site/index.html`.

### Check something visually in 3-D, on a real net

The Godot viewer, `demos/City3D/`. Loads any net via `--dataset=` or `--sumocfg=`, drapes the ground over
a terrain field baked from the net's own elevations, and runs the engine tick on its own thread. `H`
cycles the camera realism zone (Central / Follow / Locked). Needs a GPU.

It has a **headless** mode (`--headless`, `--shot=`, `--frames=`, `--quit-after`) for screenshots and for
CI-ish checks without a display, and `--frame-log=` writes the per-frame CSV (p50/p95/p99 and the count of
frames over 3× p50) that any smoothness claim should be based on rather than on how it felt.

### Watch a live engine with zero install

`Sim.LiveHost` — streams a running engine to a browser over WebSocket with client-side dead reckoning.
The shareable demo.

```bash
dotnet run -c Release --project src/Sim.LiveHost -- scenarios/_bench/city-organic-L2
```

### Watch 10 k vehicles on a desktop, or test the DDS transport

`Sim.Viewer` (raylib + Dear ImGui), four modes: `local` (owns the engine, renders the authoritative
snapshot — no transport, no jitter), `loopback` (publish → DDS → subscribe → dead-reckon → render in one
process), `publish` (headless DDS writer), `remote` (view-only subscriber; a late joiner gets the road
network over durable QoS). `local --demo "<name>"` is also the scenario-picker demo tool.

Use `local` to ask "is the engine right?" and `loopback`/`remote` to ask "is the transport and
reconstruction right?" — conflating those two is how a render artifact gets reported as a sim bug.

### Replace the `sumo` binary

`Sim.Sumo` builds `sumosharp.dll`, a `sumo`-compatible CLI (`--config-file`, `--begin`/`--end`,
`--fcd-output`, `--summary-output`, `--tripinfo-output`, `--statistic-output`, `--max-parallelism`).
Point `SUMO_BINARY` at it.

> ⚠ **Known bug:** three junction gates default OFF through this path when their env vars are unset, while
> the engine, the goldens and every other host have them ON. See [`ENV-GATES.md`](ENV-GATES.md) §"The
> three-state trap" and the entry at the top of [`TASKS-TODO.md`](TASKS-TODO.md). Any measurement taken
> through `SUMO_BINARY` ran with three gates off.

### Measure coupled cars + pedestrians performance

`Sim.BenchLiveCity` — the instrument every coupled perf number in the README was measured with. Reports
achieved (not requested) counts, the high/low-power ped split, per-step mean/p50/p95/p99/max, the count
over 3× p50, GC pause, alloc/step, peak RSS, and **the observed value of every env gate** so the log
proves what it measured.

```bash
dotnet run -c Release --project src/Sim.BenchLiveCity -- --cars 5000 --peds 20000 --steps 200 --warmup 20
```

`--cars 0` / `--peds 0` are the isolation arms — run them before attributing a cost to either side.
For capacity, use open-loop inflow, not the caps (caveat 4 above): `scripts/sweep-inflow.sh`.

### Measure car-only throughput or scaling

`Sim.Bench` (determinism hash + micro-benchmarks — this is the hash the gate pins) and `Sim.BenchCity`
(RTF, peak RSS, stuck detector, and engine-vs-SUMO aggregate parity via
`--sumo-summary`/`--sumo-tripinfo`/`--aggregate-tolerance`). `scripts/run-benchmarks.sh` drives the ladder;
`scripts/bench-scaling.ps1` produces the core-scaling curve.

Always report the **demand model** alongside any number from these (`CLAUDE.md`
measurement-discipline #4).

### Compare against SUMO honestly

`scripts/run-density-diff.sh` (built on `Sim.DensityDiff`) runs three columns: SUMO with its shipped
defaults, SUMO **honest**, and us. The distinction is the point — SUMO's defaults include
`time-to-teleport=300`, `collision.action=teleport`, and `collision.check-junctions=false`, meaning
junction interpenetration is not even *detected*. Comparing against shipped defaults compares against the
cheating. The driver sets every gate explicitly for exactly the reason in caveat 1.

[`CONSTRAINT-high-realism-artefact-ladder.md`](CONSTRAINT-high-realism-artefact-ladder.md) is **binding**
on what may be copied from SUMO: target its flow, never its method.

### Run the tests

```bash
dotnet test Traffic.sln -c Release            # 777 pass / 0 fail / 4 skip -- the gate
dotnet test tests/Sim.LiveCity.Tests -c Release       # 90/90   -- NOT in Traffic.sln
dotnet test tests/Sim.Pedestrians.Tests -c Release    # 324/324
cd demos/City3D && dotnet test CityLib.Tests          # 186 pass / 4 skip
CITY3D_REALTIME_TESTS=1 dotnet test CityLib.Tests     # 190/190, ~2 m 20 s
```

The four City3D skips are real-time render-loop tests — about a second of wall clock per simulated second,
because the render clock tracks wall time and those scenarios use `step-length = 1`. Run them after
touching `Sim.Viewer.Motion`, `CityLib.Reconstructor`, or the render-clock plumbing; nothing else covers
those end to end. A `--filter` alone cannot enable them (xunit decides `Skip` at discovery), which is why
the env var exists.

**The offline loop never needs SUMO.** If `dotnet test` seems to want it, that is a harness bug, not a
missing dependency.

### Regenerate goldens

`scripts/regen-goldens.sh` — needs SUMO, ends in a commit. Only when scenario inputs change or the pinned
SUMO version moves. Goldens are **committed, never computed at test time**; `provenance.txt` records which
SUMO version produced each one, so staleness is detectable.

### Feed an external image generator

`Sim.IgBridge` / `Sim.IgBridge.Host` — for an IG that consumes plain `position / orientation / timestamp`
and does no prediction of its own. It bakes the smoothing in before the wire.
[`IGBRIDGE-INTEGRATION-GUIDE.md`](IGBRIDGE-INTEGRATION-GUIDE.md).

### Everything else

| Tool | For |
| --- | --- |
| `Sim.Run` | engine → SUMO-schema FCD dump (feeds `Sim.Viz`) |
| `Sim.ExtDemo` | external non-SUMO agents from a JSON script, combined FCD out |
| `Sim.Host` / `Sim.Host.App` | the netstandard2.1-clean embedding surface a Unity/Godot host uses |
| `Sim.EvacProfile` | evacuation per-phase cost profile + crowd-solver micro-bench |
| `Sim.PedDdsLoopback` | live CycloneDDS proof that server == IG for pedestrians |
| `Sim.BenchCrowd` / `Sim.BenchPedLod` / `Sim.BenchPedNet` | pedestrian micro-benchmarks |
| `scripts/gen-georef-fixture.sh` | regenerate the committed georeferenced 3-D test fixture |
| `scripts/prep-ped-net.sh` | prepare a net for the pedestrian layer |
| `scripts/publish-sumosharp.sh` | pack the NuGet set ([`PACKAGES.md`](PACKAGES.md)) |

## Which projects are outside `Traffic.sln`

Deliberately, so the hermetic parity gate never touches native, GPU or demo-host dependencies. Verified
against `Traffic.sln`, not assumed:

- **`src/`** — `Sim.Viewer`, `Sim.Viewer.Core`, `Sim.Viewer.Raylib`, `Sim.Replication.Dds`,
  `Sim.PedDdsLoopback`, `Sim.LiveCity`, `Sim.Host.App`, `Shared`
- **`tests/`** — `Sim.LiveCity.Tests`, `Sim.Viewer.Tests`, `Sim.Viewer.Motion.Tests`
- **all of** `demos/City3D/`

Run those directly with `dotnet run --project <csproj>` / `dotnet test <csproj>`, and remember caveat 2:
`dotnet build Traffic.sln` does not build them, so a stale `bin/` will happily run old code. Note
`Sim.LiveCity` being out of the solution is the reason the live-city test suite has its own build step —
if you change `LiveCitySim`, `dotnet test Traffic.sln` will not tell you that you broke it.
