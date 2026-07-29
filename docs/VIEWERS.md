# Build & watch the engine run — the 2D and 3D viewers

SumoSharp ships **two desktop viewers** for watching the engine run. Neither is a NuGet package — both
**build from this repository** (see [`docs/SUMOSHARP-PACKAGING-DESIGN.md §V2`](SUMOSHARP-PACKAGING-DESIGN.md)
for why the shipped packages are just the engine + optional DDS transport, and the viewers are apps).

- **2D raylib viewer** — `src/Sim.Viewer`. A fast native window (raylib + Dear ImGui) that renders the
  running engine in 2D, with an in-window scenario picker, live controls, and click-to-drop obstacles.
- **3D Godot city viewer** — `demos/City3D`. A Godot 4 (.NET) 3D city that consumes the `SumoSharp`
  package from a local NuGet feed — a real package-consumer, end to end.

Both need a desktop with a GPU. On a headless box you can still capture a screenshot with software GL
(`xvfb-run …`), but interactive use wants a real display.

---

## 2D raylib viewer (`src/Sim.Viewer`)

`Sim.Viewer` is out of `Traffic.sln` (it pulls the raylib native package), so run the project directly —
`dotnet run` builds it on first use.

### Strong net / scenario CLI

`--mode local` selects **what to simulate** explicitly:

| Flag | What it loads | Traffic |
|---|---|---|
| `--scenario <dir>` | a committed scenario directory (one each of `*.net.xml` + `*.rou.xml` + `*.sumocfg`) | the scenario's **real demand** |
| `--sumocfg <file>` | a self-describing `.sumocfg` (its `<input>` names the net + route-files) | the scenario's **real demand** |
| `--net <net.xml \| dir>` | a bare road network | a random-traffic **sandbox** (`--fleet N` sets the size) |
| `--demo "<name>"` | a curated built-in demo (in-window picker) | the demo's own traffic |
| *(positional `<dir\|net.xml>`)* | back-compatible auto-detect: real demand if a rou+cfg sit beside the net, else sandbox | — |

Precedence when more than one is given: `--sumocfg` > `--scenario` > `--net`.

```bash
# Real demand from a committed scenario directory:
dotnet run -c Release --project src/Sim.Viewer -- --mode local --scenario scenarios/11-priority-junction

# Real demand from any self-describing .sumocfg (net + routes resolved from its <input>):
dotnet run -c Release --project src/Sim.Viewer -- --mode local --sumocfg path/to/your.sumocfg

# A bare network as a sandbox, filled with ambient traffic (10k-scale perf pass):
dotnet run -c Release --project src/Sim.Viewer -- --mode local --net path/to/your.net.xml --fleet 4000

# The in-window curated picker:
dotnet run -c Release --project src/Sim.Viewer -- --mode local --demo "Roundabout"
```

Or use the wrapper:

```bash
scripts/watch-2d.sh                                 # a default committed scenario (real demand)
scripts/watch-2d.sh scenarios/09-traffic-light      # any committed scenario dir (real demand)
scripts/watch-2d.sh --sumocfg path/to/your.sumocfg  # any self-describing .sumocfg
scripts/watch-2d.sh --net path/to/your.net.xml      # a bare network as a sandbox
```

**In-window controls:** drag = pan · wheel = zoom · click a road = drop an obstacle · `d` = diagnostics
(the diagnostics panel shows the sim mode — `SCENARIO` vs sandbox — the live vehicle count, sim time,
and FPS). Other modes (`loopback`, `publish`, `remote`) and the headless `--screenshot <png> --frames N`
capture are documented in the README's "Live & native viewers" section.

---

## 3D Godot city viewer (`demos/City3D`)

The 3D viewer consumes the `SumoSharp` package (and `SumoSharp.Dds` for the remote/DDS path) from a
**local NuGet feed** that its `build.sh` populates by packing the engine — so it doubles as the
end-to-end proof that the packages compose for a real consumer.

### From a fresh checkout — one command to prepare

```bash
demos/City3D/setup.sh          # checks prerequisites, fetches the Godot .NET editor (~100 MB,
                               # ephemeral), packs the local package feed, builds the viewer
demos/City3D/setup.sh --remote # ...and also pack SumoSharp.Dds for the remote/DDS split
```

`setup.sh` verifies the prerequisites up front and prints the exact install line if any are missing:
the **.NET 8 SDK**, `curl` + `unzip` (to fetch/extract the editor), and — only on a headless box —
`xvfb` + a software-GL (mesa) stack. The Godot editor is downloaded outside the repo (never committed);
override with `GODOT_HOME` / `GODOT_VERSION` on restricted networks.

### Watch it run

```bash
demos/City3D/run-local.sh                                   # default scenario, interactive
demos/City3D/run-local.sh --scenario=_bench/city-mixed-1k   # a bigger signalized city (~1k vehicles)
demos/City3D/run-local.sh --sumocfg=/path/to/scenario.sumocfg   # an arbitrary net + demand
demos/City3D/run-remote.sh                                  # remote viewer over DDS (needs --remote)
```

`run-local.sh` is itself self-bootstrapping (it re-packs the feed, builds, and fetches Godot if needed),
so on most machines you can skip straight to it; `setup.sh` is the explicit "prepare everything, with a
friendly prerequisite check" step. Both accept `--scenario=<dir>`, `--sumocfg=<file>`, `--dataset <dir>`,
`--camera=`, and a headless `--shot=<png>`; see [`demos/City3D/README.md`](../demos/City3D/README.md) for
the full flag set and the remote/DDS topology. On a headless box the run-scripts auto-wrap in
`xvfb-run` + software GL so they still render (and can screenshot).

## Solutions & CI

Each viewer builds from its own solution, so a fresh checkout opens exactly what it needs:

| Solution | Builds | CI |
|---|---|---|
| `Traffic.sln` | the engine (packages) + all tests | `ci.yml` — parity + determinism gate |
| `SumoSharp.Viewer.Raylib.sln` | the 2D raylib viewer + tests | `viewer-raylib.yml` — build + test + **uploads the viewer binary artifact** |
| `SumoSharp.Viewer.Godot.sln` | the Godot demo (CityLib + tests + the Godot `Viewer`) | `secondary-solutions.yml` builds/tests the **managed** CityLib half (Godot data path) headlessly; the Godot `Viewer` builds locally |
| `SumoSharp.Viz.sln` | Sim.Viz + Sim.Run + Sim.ExtDemo (headless replay) | `secondary-solutions.yml` (build) |
| `SumoSharp.Experiments.sln` | Sim.Sumo, DensityDiff, EvacProfile, PedDdsLoopback, LiveHost (legacy), IgBridge, benches | `secondary-solutions.yml` (build + IgBridge.Tests) |

---

## Which viewer?

- **Just want to see traffic / debug a scenario quickly** → the 2D viewer (`scripts/watch-2d.sh`).
- **Want the 3D city, or to see the packages consumed as a real integrator would** → `demos/City3D`.
- **Building your own game/engine integration** → you don't need either viewer as a dependency; install
  the `SumoSharp` package and render with your own engine, using its render-side motion reconstruction
  for smooth poses. The viewers here are worked examples you can read and copy.
