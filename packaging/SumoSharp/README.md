# SumoSharp

**SumoSharp** is a C#/.NET reimplementation of [Eclipse SUMO](https://eclipse.dev/sumo/)'s
microscopic traffic-simulation algorithms, built to drop into a **simulation or game engine**. This is
the one portable package — `dotnet add package SumoSharp` gives you the whole engine:

- **Simulation core** — load a SUMO network (`.net.xml`), spawn and route vehicles at runtime, step
  deterministically or run async, and read vehicle state through a data-oriented API.
- **Pedestrian crowd** — an ORCA-backed pedestrian crowd sharing the network with the vehicles, with
  cars yielding to crossing pedestrians (the "live city" coupled host).
- **Replication** — a transport-neutral dead-reckoning wire model (compact records, a packed codec, an
  adaptive publish policy, and a `.simrec` record/playback format) for streaming simulation state to a
  client, game, or viewer. The data model *is* the API; transports are pluggable bindings.
- **Render-side motion reconstruction** — turn sparse authoritative/streamed samples into smooth
  per-frame poses (position + heading) in your own renderer. This is the piece a Unity or Godot game
  consumes to draw the simulation.

Multi-targets **`net8.0`** (the parity/perf target) and **`netstandard2.1`** (Unity Mono/IL2CPP,
Godot). It carries **no native dependency**.

## Optional companion package

- **`SumoSharp.Dds`** — a CycloneDDS binding of the replication transport contract, for streaming
  simulation state between processes over DDS. Native (net8.0). Add it only if you want the DDS wire; a
  game engine that brings its own networking does not need it.

The desktop viewers (a 2D raylib viewer and a 3D Godot viewer) are **not** packages — they build from
the SumoSharp repository. See the repository's documentation for build-and-run instructions.

## License

Dual-licensed **EPL-2.0 OR GPL-2.0-or-later**, matching Eclipse SUMO.

SumoSharp is an **unofficial, independent reimplementation**. It is **not** affiliated with, endorsed
by, or a product of the Eclipse SUMO project or the Eclipse Foundation.
