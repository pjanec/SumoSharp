# SumoSharp.Host

The **transport-neutral engine-to-wire publisher** for a SumoSharp-driven simulation. Targets
`net8.0` and `netstandard2.1` — no transport dependency, no native binding, so it can drive an
in-process consumer (e.g. `SumoSharp.Replication`'s `InMemoryReplicationBus`) or any transport
binding (DDS, TCP, UDP) that implements `IReplicationSink`.

**What it solves.** Publishing a running engine over the replication contract
(`SumoSharp.Replication.IReplicationSink`) means translating `SimulationSnapshot` columns and a
`NetworkModel` into wire records (`VehicleRecord`, `LifecycleRecord`, `GeometryCodec.LaneGeo`,
`TlCodec.TlEntry`) — lifecycle bookkeeping, adaptive publish-rate gating, and the lane-window ->
`UpcomingLanes` translation. `ReplicationPublisher` does this once so every host (in-process demo,
a future headless DDS host, your own transport) shares the same logic instead of hand-rolling it.

## What's in the box

`ReplicationPublisher` has two entry points:

- **`PublishGeometryOnce(NetworkModel network, IReplicationSink sink)`** — builds one
  `GeometryCodec.LaneGeo` per lane (id/one-way/width/length/polyline) and calls
  `sink.PublishGeometry(...)`. Call once, before the step loop starts, after readers have had time
  to discover the sink's transport (if any).
- **`PublishStep(SimulationSnapshot snap, IReplicationSink sink)`** — call once per sim step:
  publishes lifecycle (spawn/despawn + dims) for vehicles the publisher hasn't announced yet,
  builds `VehicleRecord`s from the snapshot's columns (gated by
  `SumoSharp.Replication.PublishScheduler`'s dead-reckoning-error policy, so a predictable mover is
  sent less often), calls `sink.PublishFrame(...)`, and publishes `TlCodec.TlEntry[]` traffic-light
  state (at a low rate) via `sink.PublishTrafficLights(...)`.

Call **`Reset()`** whenever the driving engine restarts at t=0 (a fresh timeline's `snap.Time` is
smaller than the old bookkeeping's, which would otherwise suppress every vehicle until sim time
climbs back past the stale values).

`ReplicationPublisher` depends only on `SumoSharp.Core` (+ `SumoSharp.Ingest`, transitively) and
`SumoSharp.Replication` — no DDS, no `Sim.Viewer.Core`. It is the reusable half of
`Sim.Viewer.Core.DdsPublisher`, extracted so the same translation logic backs both the native
DDS-based viewer and any other host.

## License & disclaimer

Dual-licensed **EPL-2.0 OR GPL-2.0-or-later** (SumoSharp is a derivative of Eclipse SUMO and cannot
be relicensed).

Unofficial, independent C# reimplementation of Eclipse SUMO's microscopic simulation core. Not
affiliated with or endorsed by the Eclipse SUMO project. "SUMO" is an Eclipse trademark.
