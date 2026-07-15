# SumoSharp.Replication

Transport-agnostic **dead-reckoning replication** for the **SumoSharp** traffic engine — the wire
data-model and the contract every transport carries. **The data model is the API; a transport
(DDS, WebSocket, pipe, …) is just a binding.**

- **Data model** — compact handle-based records: `VehicleRecord` (lane-arc mover prediction),
  `CrowdRecord`, `LaneGeo`, `TlEntry`, `LifecycleRecord`, plus `TimestampedSample` +
  `IVehicleSampleHistory` (the render-side sample the viewer predicts from).
- **Codec** — a canonical packed-blob codec (`FrameCodec` / `GeometryCodec` / `TlCodec` /
  `FrameChunker`) so the *same* bytes ride DDS as an opaque payload or go over TCP/UDP.
- **Publish policy** — a pluggable adaptive scheduler (`IPublishPolicy`, `DefaultPublishPolicy`,
  `DrErrorPublishPolicy`) that decides *when* to send, plus the shared `DrExtrapolation` DR curve
  used by both publisher and viewer.
- **Transport contract** — `IReplicationSink` / `IReplicationSource` over four logical channels
  (durable geometry-once, durable dims/lifecycle-once, per-frame movers, low-rate traffic lights).
  DDS is one binding (`SumoSharp.Replication.Dds`); an in-process `InMemoryReplicationBus` is
  another. A consumer coded against these interfaces never needs to know DDS exists.

Depends only on `SumoSharp.Core`. No transport dependency — concrete DDS types live in the separate
`SumoSharp.Replication.Dds` package.

## License

Dual-licensed **`EPL-2.0 OR GPL-2.0-or-later`** (SumoSharp is a derivative work of Eclipse SUMO and
cannot be relicensed). EPL-2.0 is weak, file-level copyleft: a proprietary app may link SumoSharp and
keep its own source closed, but must keep the SUMO-derived files under EPL and publish modifications
to *those* files. This is not legal advice — get counsel for commercial use.

## Disclaimer

Unofficial, independent C# reimplementation of Eclipse SUMO's microscopic simulation core. Not
affiliated with or endorsed by the Eclipse SUMO project. "SUMO" is an Eclipse trademark.
