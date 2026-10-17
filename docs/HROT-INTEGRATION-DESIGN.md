# HROT-INTEGRATION-DESIGN.md — the host seams HROT needs (design + tasks, folded)

> **Status:** LIVE, 2026-10-17. Branch `hrot/slice1`. Tracker: [`HROT-INTEGRATION-TRACKER.md`](HROT-INTEGRATION-TRACKER.md).
> **Why:** HROT (github.com/pjanec/HROT) embeds SumoSharp as a traffic module in its editor and, later, a dedicated traffic
> node. Every decision below was made and approved on the HROT side — its design of record is HROT's
> `docs/blueprints/Architect_Question_93_Sumo_Traffic_Node.md` (rulings R-4020…R-4052). This file is the SumoSharp half:
> the exact seams, and why each is parity-neutral. HROT vendors these sources read-only (AQ93 §11 V1–V3), so every change
> it needs lands HERE first.

## Parity argument (applies to every task)

Every change is **additive and default-off**: a new optional field, a new overload, a new constructor flag, or a new
library. No existing call site, default value or per-step code path changes ⇒ all goldens byte-identical, the bench hash
unchanged. Each task's success condition includes the full `dotnet test -c Release` (CLAUDE.md measurement rule 9).

## H1 — `<location>` is parsed *(AQ93 E1/E1b)*

`NetworkParser` ignored `<location>`; a host could not place a net in its own world. **Seam:** `NetworkModel.Location`
(`NetLocation? = null`, last record parameter) carrying `NetOffsetX/Y`, `ConvBoundary`, `OrigBoundary` (raw), `ProjParameter`
and `IsGeoreferenced` (`ProjParameter != "!"`). SumoSharp still never re-origins or reprojects
(`EXTERNAL-NET-VIEWER-DESIGN.md`); the host does. Absent element ⇒ null; a malformed number throws.
**Success:** a georeferenced net (`_ped/georef_min`) parses UTM zone 32 with its offset; `_ped/demo_city/box` parses `"!"`
with offset (−150, −150) and `IsGeoreferenced == false`; a hand-built model compiles unchanged; full suite green.

## H2 — `Sim.Scene`: road and signal geometry, out of the Godot demo *(AQ93 V7, R-4049)*

CityLib's builders are already Godot-free except ONE thing: every vertex goes through `CoordinateTransform.SumoToGodot`
(`RibbonMesh.Vertices` is "already in GODOT space"). **Seam:** a new packable library `src/Sim.Scene` (net8.0 +
netstandard2.1, bundled into the `SumoSharp` package like the other eight) that emits geometry in **SUMO's own frame**
(x east, y north, z up, metres, relative to a caller origin) with a **kind per triangle** — asphalt, junction, marking,
crosswalk, ambient ground — plus the signal-head list; and adds the one missing builder, **junction-polygon
triangulation** (`Junction.Shape`). CityLib's builders become thin wrappers that apply the Godot axis swap at the end, so
the City3D viewer and `CityLib.Tests` see identical numbers.
**Success:** `CityLib.Tests` green with no expected value changed (the wrappers are exact: the swap is a reorder and a
negation); new `Sim.Scene` tests for the frame (a known lane point maps to `(x − ox, y − oy, z − oz)`) and for junction
triangles covering the polygon's area; full suite green; the package packs.

## H3 — DDS samples can carry SIM time *(AQ93 E9, R-4027)*

`DdsSubscriber` stamps samples with the DDS SourceTimestamp (wall clock); a host whose clock is the sim clock (pausable,
scalable, seekable) resolves with `DrClock.ResolveAt` at sim time. **Seam:** `DdsSubscriber(participant, stampWithSimTime)`;
the old constructor keeps wall-clock stamps. (`InMemoryReplicationBus` already stamps with the frame's sim time.)
**Success:** a loopback test: with the flag, the history's sample times equal the frames' sim times; without, unchanged.

## H4 — the host may choose a vehicle's id *(AQ93 E11, R-4029/R-4038)*

The id `SpawnVehicle` generates (`__vehN`) is what `VehicleIds` and `LifecycleRecord.Name` carry; HROT keys a car to its
own network id. **Seam:** `SpawnVehicle(type, routeEdges, string? vehicleId, …)`; null = the generated id (the old overload
forwards with null); a duplicate of a live (Pending/Active) vehicle's id throws.
**Success:** a spawned vehicle with id `"4711"` reports `"4711"` in `VehicleIds` and on its lifecycle record; spawning a
second live `"4711"` throws; after it arrives the id may be reused; full suite green.
