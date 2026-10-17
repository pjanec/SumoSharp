# HROT-INTEGRATION-TRACKER.md

Design + tasks: [`HROT-INTEGRATION-DESIGN.md`](HROT-INTEGRATION-DESIGN.md). Branch `hrot/slice1`, then `hrot/slice2` (H5–H9).

- [x] **H1** — `<location>` parsed into `NetworkModel.Location`
- [x] **H2** — `Sim.Scene`: SUMO-frame road/marking/crosswalk/junction/ambient-ground meshes with a kind per mesh + signal heads; CityLib wraps it
- [x] **H3** (rail landed in slice 2: `tests/Sim.Replication.Dds.Tests`) — `DdsSubscriber(participant, stampWithSimTime)`
- [x] **H4** — `SpawnVehicle(..., vehicleId, ...)` with a live-duplicate guard
- [x] full `dotnet test -c Release` green, goldens byte-identical (2026-10-17 at H2: ParityTests 790/795 +5 skipped, Sim.Scene 21, LiveCity 92, Pedestrians 324, Host 6, Viewer.Motion 19, DotRecast 2; CityLib.Tests 186 +4 skipped)
- [x] **H5** — `LiveCityConfig.VehicleIdProvider` + `Engine.SpawnVehicle(type, from, to, vehicleId, …)`
- [x] **H6** — `LiveCityConfig.StartTime` (engine `Begin` + `LiveCitySim.Time`)
- [x] **H7** — `LifecycleRecord.ExternalType/Color`, `IReplicationSource.Tags`, `DdsVehicleLifecycle` fields, `ReplicationPublisher.TagOf`
- [x] **H8** — `[DdsKey] Source` on the SUMO topics, `DdsReplicationSink(participant, source)`, `DdsMultiSourceSubscriber` (+ sender tracking)
- [x] **H9** — `LiveCityConfig.VehicleTypes` weighted mix (+ `VTypeParams.Width`)
- [x] full `dotnet test Traffic.sln -c Release` green with H5–H9, goldens byte-identical (2026-10-17: ParityTests 793/798 +5 skipped (+3 rails), LiveCity 100 (+8), Host 10 (+4), Replication.Dds 7 (new project), Pedestrians 324, Viewer.Motion 19, Sim.Scene 21, DotRecast 2; CityLib untouched)
