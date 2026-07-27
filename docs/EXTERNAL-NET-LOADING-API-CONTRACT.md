# API contract — pedestrian Z + arbitrary road-net loading

**Audience:** the parallel session making the **Godot City3D viewer** support arbitrary road nets and
Z-enabled rendering (and any other consumer session: BIG/Spectacle, raylib viewer).

**Design of record:** `docs/EXTERNAL-NET-LOADING-DESIGN.md`. Tasks/success conditions:
`-TASKS.md`. Live status: `-TRACKER.md`.

---

## ⚠ STATUS: DESIGNED AND FROZEN — **NOT YET IMPLEMENTED**

Every signature below is agreed and stable enough to code against, but **none of it exists on `main` yet**
(baseline `791d3e6`). This document is the contract so both sessions can move in parallel; it is not a
description of shipped code.

Each item names the task that lands it. Check `-TRACKER.md` for ticked boxes, or probe directly:

```bash
# has the ped Z overload landed?
grep -n "out double z" src/Sim.Pedestrians/Lod/PedRemoteReconstructor.cs
# has the wire kind landed?
grep -n "KindPathArcZ" src/Sim.Replication/FrameCodec.cs
# has net-path config landed?
grep -n "NetPath" src/Sim.LiveCity/LiveCityConfig.cs
```

**If a signature here turns out to need changing during implementation, this file is updated and the
change is called out in the tracker — it will not drift silently.**

---

## §1 — TL;DR for the Godot session

Your ped render path is **the wire path**, and the change on your side is ~2 lines.

`demos/City3D/CityLib/PedReconstructor.cs:76,83` today:

```csharp
if (!_reconstructor.TryGetRenderPose(id, out var pos, out var visible, out _) || !visible) continue;
// The ped net is flat (z = 0); ...
var (gx, gy, gz) = CoordinateTransform.SumoToGodot(pos.X, pos.Y, 0.0);
```

becomes:

```csharp
if (!_reconstructor.TryGetRenderPose(id, out var pos, out var z, out var visible, out _) || !visible) continue;
var (gx, gy, gz) = CoordinateTransform.SumoToGodot(pos.X, pos.Y, z);
```

Nothing else changes on the render side:
- `ReconstructedPed` **already has a `Z` field** — it is currently fed a literal `0.0`. Only its doc
  comment ("The ped net is flat, so Z … is always 0") needs correcting.
- `CoordinateTransform.SumoToGodot(x, y, z) => (x, z, -y)` **already maps SUMO z to Godot up (+Y)**. No
  transform change.
- Cars already render at real elevation today (§3). No change.

**Gated by:** C4 (wire kind) + C5 (the overload). Both are in Stage C.

---

## §2 — Pedestrian Z

There are **two** ped surfaces. They use the same underlying mechanism and agree within 0.05 m, but they
are gated by different tasks. **Pick the one you actually consume.**

### §2.1 Surface A — remote/wire: `PedRemoteReconstructor` ← **this is City3D's path**

City3D consumes `LiveCitySource.PedSource` (an `IPedReplicationSource`) and reconstructs from it. It does
**not** read `Sample()` for peds — `LiveCitySource.cs:81` records that the renderer used to read
`Sample()`'s ground-truth positions directly and was deliberately moved onto the wire for the
DR/playout-delay ("no promotion pop") behaviour.

```csharp
// NEW — additive sibling overload. Gated by: C5.
public bool TryGetRenderPose(
    int id, out Vec2 pos, out double z, out bool visible, out string animTag);

// UNCHANGED — the existing 4-out-param overload stays, byte-for-byte.
public bool TryGetRenderPose(
    int id, out Vec2 pos, out bool visible, out string animTag);
```

- `z` is **metres in the net's own vertical datum** (raw SUMO elevation; no geoid correction, no ground
  clamp, no offset).
- `z` is sampled at the **smoothed** render position, consistent with the `pos` returned in the same call.
- `z == 0.0` when the stream carries no elevation (2-D net, or a kind-4 publisher — §6). **This is not an
  error and not distinguishable from "genuinely at 0 m elevation"** — see §9.
- Both overloads coexist; the 15 existing call sites of the 4-param form are untouched, so nothing you
  don't edit changes behaviour.

### §2.2 Surface B — in-process: `LiveCitySim.Sample()`

For a consumer that ticks the sim in-process and reads snapshots (BIG/Spectacle's path; also
`LiveCitySource.Peds`):

```csharp
public readonly struct LiveCityPed   // UNCHANGED shape — Z already exists
{
    public LiveCityPed(int id, double x, double y, double z, PedRegime regime, string animTag);
    // ... Z is currently always 0.0; C3 makes it real.
}
```

**No signature change at all** — `LiveCityPed.Z` exists today and is fed a literal `0.0`
(`LiveCitySim.cs:1076`). C3 makes it carry real elevation. Consumers recompile against nothing new.

**Gated by:** C3.

### §2.3 Where z comes from (so you can reason about it)

z is **retained** from the net, not reconstructed by a spatial search:

1. `PedNetworkParser` keeps the 3rd coordinate → `PedLane.ShapeZ`, `PedCrossing.ShapeZ`,
   `PedWalkingArea.PolygonZ` (`IReadOnlyList<double>?`, **null on a 2-D net**). — C1
2. `IPedNavigation.ElevationsAlong(path)` returns per-vertex elevation along a ped's path, index-aligned
   with it. A **default interface method returning all zeros**, so any nav provider without an elevation
   model stays flat and needs no edit. — C2
3. A ped's instantaneous z is one lerp between the two path elevations bracketing the waypoint cursor it
   already maintains for steering. **No nearest-lane search anywhere.** — C3
4. On the wire, z travels as a third quantized component and `HeadlessIg` interpolates it with the *same*
   arc fraction it already uses for position — so wire z and in-process z cannot structurally
   disagree. — C4/C5

---

## §3 — Vehicle Z: already works, nothing to do

Cars have had real elevation since the geometry-3D work. No API change, no task, no action:

- `LiveCityCar.Z` is fed from `SimulationSnapshot.PosZ` (`LiveCitySim.cs:1058`).
- `KinematicReconResult.Z` resolves lane-surface Z via `NetworkLaneSource.LaneShapeZ` →
  `LaneGeometry.ElevationAtOffset`.
- `Lane.ShapeZ` is `null` on a 2-D net, so `Z` reads 0 there — same convention as peds.

If cars already render at correct elevation in your viewer on a 3-D net, that path is fine and peds were
the only gap.

> Caveat that may bite you: `DEMO-CITY3D-TRACKER.md` T1.2 notes the **wire `LaneGeo` is 2-D**, so
> road/lane *geometry* delivered over the wire is flat even though vehicle *poses* carry Z. The C4 work
> extends the **ped path** record only — it does **not** fix `LaneGeo`. If your road meshes are flat on a
> 3-D net in remote mode, that is this separate, still-open gap, not a bug in this work. Use the
> Z-aware local `NetworkLaneSource` for road geometry, as T1.2 already does.

---

## §4 — Arbitrary road-net loading

```csharp
public sealed class LiveCityConfig
{
    public string? NetPath { get; set; }                     // NEW — explicit net file
    public IReadOnlyList<string>? RoutePaths { get; set; }   // NEW — route files (a LIST, see below)
    public string? RoutePath { get; set; }                   // NEW — single-file shorthand
    public string DatasetDir { get; set; }                   // unchanged
    // ...
}

public static LiveCityConfig ForSumocfg(string sumocfgPath);  // NEW
public static LiveCityConfig ForDataset(string datasetDir);   // existing
public static LiveCityConfig ForRepoRoot(string repoRoot);    // existing (the pinned demo)
```

**Gated by:** B1 (`NetPath`/`RoutePaths` + resolution), B2 (`ForSumocfg`).

### Net path resolution order

1. `NetPath`, if set — used verbatim.
2. `DatasetDir/net.xml` if it exists.
3. `DatasetDir/scenario.net.xml` if it exists — the name `preprocess.py` cut sub-areas use.
4. Else falls back to `net.xml` so the error names the conventional file.

So **`ForDataset(cutDir)` now works on a `preprocess.py` cut directory** without you probing filenames.

### `ForSumocfg`

Parses `<input><net-file>` / `<route-files>` and resolves each **relative to the sumocfg's own
directory** (absolute paths used as-is). Applies the same defaults as `ForDataset`
(`NavMode = RouteGraph`, `RegionPlan = true`, all `LIVECITY_*` env overrides).

**`RoutePaths` is a list, and order matters — do not assume entry 0 is a route file.** Measured on the
real `geneve_Medium.sumocfg`: entry 0 is `common/vType.config.xml` (107 `<vType>`, **zero routes**); the
actual routes are entries 4–5. All entries are scraped and unioned. If you construct configs yourself,
pass every route file rather than picking one.

### Live density knobs (for a slider UI)

`CarTargetConcurrent` and `CarSpawnPerStep` are read off the by-reference `cfg` every `Step()` **today** —
mutate them live and it works. `PedPopulationCap` and `PedSpawnRatePerSecond` **currently do not work
live** (they are copied into a `PedDemandConfig` in the constructor). **D1** fixes that. Until D1 lands, a
pedestrian-density slider is silently inert.

---

## §5 — Coordinate frame and terrain placement

SumoSharp does **not** read, rewrite, or normalise `<location>`. Lane shapes come out in the net's own
SUMO-local frame, unchanged. Georeferencing is the consumer's job.

Read `<location>` from the net file yourself and branch on `projParameter`:

| `projParameter` | frame | placement |
|---|---|---|
| `+proj=utm +zone=32 ...` | **georeferenced** | `UTM = sumoLocal − netOffset`, then your own projection → engine world |
| `!` | unprojected/local | use the SUMO-local frame directly (synthetic demo-city) |

**Measured on the real data** (both files carry byte-identical georeferencing; the cut preserves the full
net's absolute UTM offset exactly, only `convBoundary` shrinks):

```
netOffset     = "-388091.80,-5257586.90"      (identical in swiss_roads.net.xml and the Geneva cut)
projParameter = "+proj=utm +zone=32 +ellps=WGS84 +datum=WGS84 +units=m +no_defs"
```

Elevation actually present — the reason ped Z matters:

| net | elevation range | span |
|---|---|---|
| `swiss_roads.net.xml` (161 MB, all of Switzerland) | 199.48 – 1633.77 m | 1434 m |
| `geneve.net.xml` (44 MB, Geneva cut) | 324.39 – 1062.24 m | 738 m |

At `z = 0` a pedestrian sits up to **1.6 km** below the terrain. Do **not** ground-clamp as a
substitute — clamping hides the multi-level cases (bridges/underpasses) that real z gets right.

Godot mapping is already correct and needs no change: `SumoToGodot(x, y, z) => (x, z, -y)`, i.e. SUMO
elevation → Godot **+Y** (up).

---

## §6 — Wire format change (remote/DDS mode)

**Gated by:** C4. Relevant if you decode ped frames yourself; **not** relevant if you go through
`FrameCodec` / `PedReplicationReceiver` (which handle both kinds for you).

```
KindPathArc  = 4   (unchanged)   14 B +  8 B/point : handle(u32) speed(f32) startTime(f32) n(u16)
                                                     then n × ( x_cm i32, y_cm i32 )
KindPathArcZ = 5   (NEW)         14 B + 12 B/point : ...identical header...
                                                     then n × ( x_cm i32, y_cm i32, z_cm i32 )
```

- z uses the **same int32-centimetre quantization** as x/y — ~1 cm precision, ±21 474 km range.
- `PathArcRecord` gains `PathZ` (`IReadOnlyList<double>?`) via an **additive** constructor overload; the
  existing 4-arg constructor stays.
- **Kind 4 is untouched, and the publisher still emits kind 4 whenever there is no z.** On a 2-D net the
  wire bytes are **byte-for-byte identical to today**. Only 3-D nets pay +4 B/point, on a record sent once
  per ped path lifetime on the durable topic — never on the per-step hot path.
- A new frame **kind** rather than a `FrameCodec.Version` bump, deliberately: `ReadHeader` reads the
  version byte but **never validates it**, so re-striding kind 4 would silently misparse old payloads;
  and `Version` is global across all four frame kinds.

**If you maintain a hand-rolled PathArc decoder, add the kind-5 case** or you will not see ped elevation
in remote mode (and must not misread a kind-5 frame with an 8 B stride).

---

## §7 — What stays 2-D, on purpose

Only the **output** is 3-D. Do not expect otherwise:

- ORCA collision avoidance is a 2-D velocity-obstacle algorithm; strategic routing is a plan-view graph
  search. Neither becomes 3-D.
- `PedLane.Shape` etc. stay `Vec2`; elevation rides alongside as a parallel array.
- z is **parity-inert by construction** — no steering, ORCA, routing or `ActivityTimeline` decision reads
  it, exactly as `Lane.ShapeZ` is on the vehicle side. A ped's 2-D trajectory is asserted **bitwise
  identical** with z populated vs. null (C3·SC4).

Consequence for you: peds walk the same 2-D paths they do today. Only their rendered height changes.

---

## §8 — Measured real-net facts (for sizing your UX)

All measured in this session on the real dataset, Release build:

| | Geneva cut (44 MB) | Switzerland (161 MB) |
|---|---|---|
| total net load (ctor) | **≈ 11.6 s** | **≈ 80 s** |
| peak working set | 572 MB | **1 652 MB** |
| lanes / edges | 53 229 / 41 933 | 175 465 / 141 571 |
| sidewalks / crossings / walkingareas | 2 201 / 221 / 2 179 | 13 811 / 735 / 13 537 |
| ped lanes carrying z | **4 601 / 4 601 (100 %)** | **28 083 / 28 083 (100 %)** |

- The `LiveCitySim` constructor makes **four** full passes over the net file
  (`NetworkParser.Parse`, `PedNetworkParser.Load`, and `CrosswalkSignalSchedule.FromNet` which is two).
  Pass 1 is ~85 % of the cost. This is **pre-existing** and not changed by this work.
- **Recommendation: load a cut box, not full Switzerland.** 80 s / 1.65 GB is a tolerable one-off but not
  something to put a user in front of repeatedly. If you must, add a progress indicator — the load is
  synchronous in the constructor.

---

## §9 — Gotchas / things that are not bugs

1. **`z == 0.0` is ambiguous.** It means "2-D net, or no elevation on this stream, or genuinely at 0 m".
   There is no separate "unknown" signal. If you need to distinguish, check the net's `<location>` /
   `Lane.ShapeZ` yourself.
2. **A 2-D net gives every ped `z = 0`** — that is correct, and it keeps the existing demo and all goldens
   bit-identical. Don't treat flat peds on `demo_city/box` as a regression.
3. **`ElevationsAlong`'s default is flat.** A custom `IPedNavigation` (DotRecast, a test double) that does
   not override it yields z = 0 forever, silently. Override it if you supply your own nav.
4. **`ShapeZ` is `null`, not zeros,** on a 2-D net — check for null, don't index blindly.
5. **Road/lane geometry over the wire is still 2-D** (§3 caveat). Peds and vehicle poses get z; `LaneGeo`
   does not.
6. **Multi-level places are real.** Measured: 27 locations nationwide where ped lanes stack vertically
   within 2 m horizontally (worst 12.6 m apart). Retained z handles these correctly; any ground-clamp or
   nearest-surface heuristic you add on top will get them wrong.

---

## §10 — Packaging

City3D consumes `SumoSharp.*` **0.1.0 from the local feed** (`demos/City3D/nuget.config` pins them to
`./local-nuget` via `packageSourceMapping`). After these changes land you must **repack the local feed**
(`demos/City3D/build.sh --pack-only`) or you will silently build against the old packages.

Packages actually modified by this work: **`SumoSharp.Pedestrians`** (C1–C3, C5),
**`SumoSharp.Replication`** (C4), **`SumoSharp.LiveCity`** (B1, B2, C3, D1). `SumoSharp.Ingest` and
`SumoSharp.Core` are **not** touched — `ScenarioConfigParser` and `LaneGeometry` are reused as-is.

Also note `dotnet build -c Release` does **not** build `tests/Sim.LiveCity.Tests` (not in `Traffic.sln`).

---

## §11 — Landing order, and what you can start now

| Task | Delivers | Blocks the Godot session? |
|---|---|---|
| B1, B2 | `NetPath` / `RoutePaths` / `ForSumocfg` | **Yes** — arbitrary-net loading |
| C1, C2 | retained `ShapeZ`; `ElevationsAlong` | no (internal) |
| C3 | `LiveCityPed.Z` real (in-process) | only if you read `Sample()` |
| **C4, C5** | wire kind 5; `TryGetRenderPose(out z)` | **Yes** — this is City3D's ped path |
| D1 | live ped density knobs | only for a ped-density slider |

**Safe to do now, before anything lands:** everything that is not the 2-line diff in §1 — the arbitrary-net
scene/camera/scale work, the `<location>` parsing and georeferenced-vs-local branch (§5), road meshes from
the Z-aware local lane source, and your UI. The §1 diff is a one-line-each change you can apply the moment
C4+C5 tick in `-TRACKER.md`.
