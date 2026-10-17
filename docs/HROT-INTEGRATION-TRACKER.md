# HROT-INTEGRATION-TRACKER.md

Design + tasks: [`HROT-INTEGRATION-DESIGN.md`](HROT-INTEGRATION-DESIGN.md). Branch `hrot/slice1`.

- [x] **H1** — `<location>` parsed into `NetworkModel.Location`
- [x] **H2** — `Sim.Scene`: SUMO-frame road/marking/crosswalk/junction/ambient-ground meshes with a kind per mesh + signal heads; CityLib wraps it
- [x] **H3** (rail pending: DDS loopback) — `DdsSubscriber(participant, stampWithSimTime)`
- [x] **H4** — `SpawnVehicle(..., vehicleId, ...)` with a live-duplicate guard
- [x] full `dotnet test -c Release` green, goldens byte-identical (2026-10-17 at H2: ParityTests 790/795 +5 skipped, Sim.Scene 21, LiveCity 92, Pedestrians 324, Host 6, Viewer.Motion 19, DotRecast 2; CityLib.Tests 186 +4 skipped)
