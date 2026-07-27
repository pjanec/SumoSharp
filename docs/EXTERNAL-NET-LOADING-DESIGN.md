# EXTERNAL NET LOADING — design (HOW)

Design of record for the two handoffs received 2026-07-27 from the BIG/Spectacle session:

* `HANDOFF-external-net-loading.md` — the **engine** changes BIG needs (C1/C2/C3).
* `HANDOFF-godot-city3d-arbitrary-net.md` — the **Godot City3D viewer** changes (T1/T2/T3).

The WHAT lives in those two handoff documents, reproduced verbatim under `docs/handoffs/`. This
document is the HOW: mechanisms, data structures, the exact seams touched, and the
determinism/parity argument. Work breakdown is in `EXTERNAL-NET-LOADING-TASKS.md`; the checkable
to-do list is `EXTERNAL-NET-LOADING-TRACKER.md`.

---

## §0 Scope, and the one invariant that governs everything

Two consumers — BIG's Spectacle scene and the Godot City3D viewer — want to run **georeferenced
external Swiss nets** (a SumoData `preprocess.py` cut sub-area of Geneva) through `LiveCitySim`,
with free-style live density controls. Today they cannot, for five independent reasons:

| # | Blocker | Where |
| - | ------- | ----- |
| C1 | net path is hardcoded `<DatasetDir>/net.xml`; a cut is `scenario.net.xml` + a `.sumocfg` | `LiveCitySim.cs:143`, `:473` |
| C2 | pedestrians have no Z; on a 3-D net they would render hundreds of metres below the road | `PedRemoteReconstructor.cs:106`, `LiveCitySim.Sample()` |
| C3 | ped density is baked into an `init`-only `PedDemandConfig` at ctor time — a slider cannot move it | `LiveCitySim.cs:273-298`, `PedDemand.cs:718,721` |
| T1 | City3D hardcodes `LiveCityConfig.ForRepoRoot` + the pinned demo crop | `Main.cs:942`, `LiveCityConfig.cs:42-45` |
| T2 | `SumoToGodot` is a bare `(float)` cast with zero offset — a ~1e5 coordinate jitters | `CoordinateTransform.cs:32` |

**The invariant: every change here is ADDITIVE and the demo path stays byte-identical.** No parity
scenario, no golden, and no `ForRepoRoot`-built sim may observe any behavioural difference. Every
new knob defaults to the value that reproduces today's code exactly, and the two factory methods
that build the demo (`ForRepoRoot`) and the arbitrary-net path (`ForDataset`) keep their present
field-for-field output. This is the same "ITERON RULE" the ped stack already states throughout.
Concretely it means: no new field is read on a path where it is unset, no RNG stream is drawn that
was not drawn before, and no existing method signature changes.

---

## §1 C1 — loading a net by explicit path or from a `.sumocfg`

### 1.1 Mechanism

`LiveCityConfig` gains three additive, nullable properties:

```csharp
public string? NetPath   { get; set; }              // overrides <DatasetDir>/net.xml
public string? RoutePath { get; set; }              // overrides <DatasetDir>/scenario.rou.xml
public IReadOnlyList<string>? RoutePaths { get; set; }  // the .sumocfg <route-files> list
```

`LiveCitySim`'s ctor resolves them the obvious way:

```csharp
var netPath = cfg.NetPath ?? Path.Combine(cfg.DatasetDir, "net.xml");
```

and, for the drivable-edge scrape, over a **list**:

```
RoutePaths, if set          (a .sumocfg's <route-files> is a comma-separated LIST)
else [RoutePath], if set
else [<DatasetDir>/scenario.rou.xml]
```

**Why a list and not just `RoutePath`.** A real cut's `<route-files>` is
`vType.config.xml,vType_pedestrians.xml,vTypeDist.config.xml,scenario.rou.xml`
(`scenarios/_ped/subarea-box/scenario.sumocfg` is exactly this). Scraping only the first entry
finds zero edges and silently falls through to the net-derived fallback — a wrong-but-plausible
result. The scrape unions over every listed file, skipping ones that do not exist or contain no
route edges. `RoutePath` (singular) is kept because it is what the handoff specifies as BIG's knob;
it is simply the one-element case.

### 1.2 `ForSumocfg`

```csharp
public static LiveCityConfig ForSumocfg(string sumocfgPath)
```

Parses the cfg with the **existing** `Sim.Ingest.ScenarioConfigParser` (the same parser
`Engine.LoadScenario(sumocfgPath)` uses — the `NetFile`/`RouteFiles` fields already exist on
`ScenarioConfig`, so no parser change at all), then:

* resolves each `<input>` path **relative to the sumocfg's own directory**, SUMO's documented rule
  and exactly what `Engine.LoadScenario`'s own `Resolve` local function does (`Engine.cs:1233`);
* **an already-absolute path is taken as-is.** This is the gotcha called out in SumoData's
  `SUBAREA-METHOD.md` §8: `preprocess.py` emits ABSOLUTE net/route paths, demo-city emits RELATIVE.
  `Path.Combine(dir, absolute)` already returns `absolute` on both Windows and Unix, so this is
  free — but it is asserted by a test rather than left to a framework detail;
* sets `DatasetDir = <sumocfg dir>` (so the `LiveCityScene.Load` companion-JSON probe and any other
  dataset-relative lookup still resolves), `NetPath`, `RoutePaths`;
* applies **the same** `RouteGraph`/`RegionPlan`/`LIVECITY_*` defaults as `ForDataset` — a `.sumocfg`
  names an arbitrary net, so it is the arbitrary-net path by construction.

A cfg with no `<net-file>` throws `InvalidDataException` naming the file, mirroring
`Engine.LoadScenario`'s own message. A cfg with no `<route-files>` is **not** an error here (unlike
in `Engine`): `LiveCitySim` generates its own procedural demand and only ever scrapes the route file
for a spawn-edge set, for which the net-derived fallback (`DeriveDrivableEdgesFromNetwork`) is a
complete answer.

### 1.3 What does NOT change

`LiveCityScene.Load(cfg.DatasetDir)` keeps taking the dataset dir. `PedNetworkParser.Load(netPath)`
and `CrosswalkSignals.FromNet(netPath, ...)` simply receive the resolved path. No signature changes.

---

## §2 The coordinate contract (what we must NOT do)

The handoff asks us to keep something true rather than to build it, and it is load-bearing enough
to state as a design constraint:

> Cut sub-areas **preserve the original UTM32N georeference**. The crop is
> `netconvert -s FULL --keep-edges.in-boundary <bbox>` with no `--offset.*` and no reprojection, so
> `<location netOffset=... projParameter=...>` passes through untouched.

BIG converts SUMO-local → UTM by `utm = sumo - netOffset` and then to its IG frame with its own
projection convertor. **Nothing in SumoSharp may re-normalize, re-origin, or reproject a loaded
net.** In particular the viewer recenter of §5 is a *render-side* transform only: it never touches
`NetworkModel`, never round-trips into the sim, and no sim-facing API returns recentered numbers.
Synthetic demo-city nets stay unprojected (`projParameter="!"`, small offset, z=0) and consumers
branch on `projParameter` — that branch is BIG's, not ours.

---

## §3 C3 — live pedestrian density

### 3.1 Mechanism

`PedDemandConfig` stays `init`-only and immutable (it is the *seed* configuration, and several
tests depend on its immutability). Instead `PedDemand` promotes the two density values it reads
**every step** into private mutable fields, initialized from the config:

```csharp
private int _populationCap;          // was _config.PopulationCap   (SpawnDue, line 165)
private double _spawnRatePerSecond;  // was _config.SpawnRatePerSecond (DrawInterArrivalInterval, 174/182)

public int PopulationCap => _populationCap;
public double SpawnRatePerSecond => _spawnRatePerSecond;
public void SetPopulationCap(int cap);
public void SetSpawnRatePerSecond(double ratePerSecond);
```

`LiveCitySim` gets the passthrough the handoff names, plus the car twin for symmetry:

```csharp
public void SetPedDensity(int populationCap, double spawnRatePerSecond);
public void SetCarDensity(int targetConcurrent, int? spawnPerStep = null);
public PedDemand? PedDemand { get; }   // the escape hatch, for a caller wanting finer control
```

`SetCarDensity` writes `_cfg.CarTargetConcurrent` / `CarSpawnPerStep` — which `Step()` already reads
off the by-reference `_cfg` every tick — so cars needed no engine change at all; the method exists
so a viewer has ONE obvious API instead of having to know that trick. `SetPedDensity` is a no-op
(not a throw) when pedestrians are disabled for this net, so a slider handler needs no guard.

### 3.2 Semantics of a *lowered* cap — deliberately by attrition

Raising the cap converges upward within a few seconds (the spawn loop fills to the new cap at the
configured rate). **Lowering it stops new spawns; it does not despawn anybody.** Live peds drain as
they reach their destinations.

This is a decision, not an omission: it is exactly how the car knob already behaves
(`CarTargetConcurrent` gates insertion only; live cars leave by arriving), and deleting pedestrians
mid-stride to satisfy a slider would render as people vanishing. Both viewers therefore get the same
"density follows the dial, with a drain lag downward" behaviour for cars and peds. The tests assert
this honestly: *converges up* on a raise, *non-increasing and no new spawns* on a lower.

### 3.3 Determinism

Determinism is preserved **for a fixed knob trajectory**: same seed + same `(now, dt)` sequence +
same sequence of setter calls ⇒ identical spawn/arrival events. The setters do not touch the RNG
streams; `SetSpawnRatePerSecond` only changes the divisor of the next inverse-CDF draw, and
`SetPopulationCap` only changes the loop guard. A run that never calls a setter draws exactly the
stream it drew before this change — the ITERON RULE holds, so every existing ped test and the demo
are byte-identical.

`SetSpawnRatePerSecond(<= 0)` means "never spawn again" (already `PedDemand`'s documented meaning),
and is reversible: raising the rate later resumes from `_nextSpawnAt`, which `SpawnDue` clamps to
`now`, so there is no burst of catch-up spawns for the quiet interval. That clamp already exists and
is what makes the knob safe to drive from a UI.

---

## §4 C2 — per-pedestrian elevation on a 3-D net

### 4.1 The constraint that shapes the design

`src/Sim.Pedestrians/Sim.Pedestrians.csproj` states, and `docs/PEDESTRIAN-DESIGN.md` §0 Principle 6
requires: **the pedestrian subsystem must never reference `Sim.Ingest` or any parity source.** But
the elevation data lives exactly there — `Lane.ShapeZ` on `Sim.Ingest.NetworkModel`, sampled by
`Sim.Ingest.LaneGeometry.ElevationAtOffset`. The handoff's sketch ("call the existing
`LaneGeometry.ElevationAtOffset`" from inside `PedRemoteReconstructor`) would violate that boundary.

So the design **inverts the dependency**:

```
Sim.Pedestrians          interface IPedElevationSource { double ElevationAt(Vec2 pos); }
                         PedRemoteReconstructor takes an optional one (ctor param, default null)

Sim.LiveCity             sealed class NetLaneElevationSource : IPedElevationSource
   (references both)     built from (NetworkModel, PedNetwork) — the only place both types are legal
```

`Sim.Pedestrians` gains one small interface and no new project reference. `Sim.LiveCity` already
references both projects, so the concrete sampler lands there with no build-graph change at all.

### 4.2 The sampler

`NetLaneElevationSource` is built once, in `LiveCitySim`'s ctor, from the already-parsed
`NetworkModel` and `PedNetwork`:

1. **Which lanes.** Collect the lane ids of every `PedNetwork.Sidewalks` / `Crossings` /
   `WalkingAreas` entry. These live in the SAME id space as `NetworkModel.LanesById`, because
   `NetworkParser.Parse` parses *every* `<edge>`, including `function="crossing"` and
   `function="walkingarea"` ones. Verified on the committed fixture: all 20 crossings and 24
   walking areas resolve.
2. **Geometry.** For each such lane take the **vehicle-side** `Lane.Shape` (2-D points) and
   `Lane.ShapeZ`. A lane whose `ShapeZ` is null (a 2-D net) is skipped entirely — if nothing is
   indexed, `ElevationAt` returns 0 and behaviour is exactly today's.
3. **Index.** A uniform grid over the lane segments' bounding boxes, cell size ≈ 25 m (a few
   segments per cell for urban geometry). Each cell holds the indices of segments whose bbox
   overlaps it. Query = probe the point's cell, widening ring by ring until a hit is found and the
   ring's inner radius exceeds the best distance found so far (so the answer is the true nearest
   segment, not merely one in the same cell). Falls back to a linear scan only if the grid is empty.
4. **Sample.** Project the query point onto the nearest segment, accumulate the arc length of the
   preceding segments plus the projection's own `t`, and call
   `LaneGeometry.ElevationAtOffset(shape, shapeZ, arc)` — the SAME function the vehicle side uses,
   so a ped and a car at the same place get the same surface, by construction rather than by
   coincidence.

Cost: O(total ped-lane segments) memory, built once. For the ~10k-segment nets in scope this is
negligible; the grid keeps queries O(1)-ish so the per-frame cost over a few hundred peds is noise.

### 4.3 The API

Non-breaking sibling overload, exactly as specified:

```csharp
public bool TryGetRenderPose(int id, out Vec2 pos, out bool visible, out string animTag)          // untouched
public bool TryGetRenderPose(int id, out Vec2 pos, out double z, out bool visible, out string animTag)  // new
```

The 4-out-param overload keeps its exact behaviour (it now delegates to the 5-param one and drops
`z`), so the raylib viewer and every existing test are unaffected. With no elevation source
injected, `z` is 0 — the documented 2-D fallback.

`LiveCitySim.Sample()` stops writing a literal `0.0` into `LiveCityPed.Z` and writes
`_pedElevation?.ElevationAt(p) ?? 0.0`. On a 2-D net (the demo) the source is null-or-empty and the
value is still exactly `0.0`, so the demo snapshot is byte-identical.

`LiveCitySim` exposes `PedElevation` so a viewer reconstructing peds off the wire (City3D's
`PedReconstructor`, BIG) can pass it to its own `PedRemoteReconstructor`.

### 4.4 Why not just carry z through `PedNetworkParser`

Because the handoff explicitly rules it out, and it is the right call: `Vec2` is the ped stack's
geometry type throughout (nav mesh, ORCA, timelines, the wire format). Making it 3-D is a rewrite of
a parity-sensitive subsystem to serve a render-side concern. Sampling the surface at render time
gets the same pixels for none of that risk.

---

## §5 T2 — the Godot recenter (float precision)

### 5.1 The problem, quantified

`CoordinateTransform.SumoToGodot` is `((float)x, (float)z, (float)-y)`. Float has 24 bits of
mantissa: at |x| ≈ 9.2e4 (the committed georef fixture) the ULP is ~8 mm; at the ~1.4e5 of a real
Geneva cut it is ~16 mm; composed with camera and MultiMesh transforms the visible result is jitter,
z-fighting between coplanar road/marking quads, and an orbit camera that wobbles. The demo's
2000–2900 coordinates are safe by luck (ULP ~0.2 mm).

### 5.2 Mechanism: a frame value, threaded everywhere

```csharp
public readonly struct SumoGodotFrame
{
    public static readonly SumoGodotFrame Identity;                 // (0,0,0) == today's behaviour
    public SumoGodotFrame(double originX, double originY, double originZ);
    public (float X, float Y, float Z) ToGodot(double x, double y, double z);   // subtract, THEN cast
    public (double X, double Y) ToSumo(float godotX, float godotZ);             // the inverse
}
```

`CoordinateTransform`'s static methods stay exactly as they are and are defined as the `Identity`
frame's behaviour, so every existing test and every unconverted call site keeps compiling and
producing identical numbers. Heading is **origin-invariant** (a translation does not rotate
anything), so `NaviDegToGodotYawRad` / `DirectionToGodotYawRad` are untouched.

The origin is computed **once at load** as the centre of the net's (or crop's) AABB, mirroring
`LiveCitySim.ComputeNetAabbCentre`, and stored on the viewer. Every placement then routes through
the one frame: cars, peds, road meshes, lane markings, crosswalks, buildings, zones, POIs, doors,
traffic-light poles, the realism-zone ring, the selection ring, and the camera home/target. **The
consistency is the whole point** — one origin, applied everywhere; a single missed call site puts
its geometry 90 km away, which is at least loud rather than subtle.

### 5.3 Why the origin has a Z too

A Geneva cut's roads sit at z ≈ 370–400 m. With `originZ = 0` the road mesh renders at Godot
Y ≈ 400 while everything that hardcodes ground level (the realism ring, zone tint, POI ground marks
— all of which pass `sumoZ = 0`) renders at Y = 0, i.e. 400 m underground. Subtracting the net's
mean elevation puts the whole scene in a ±50 m band around Y = 0 and keeps ground-referenced
overlays where they belong. `originZ` is 0 for the demo ⇒ byte-identical.

### 5.4 The inverse direction

`Main.cs` also maps *back*: the camera-driven LC-realism zone reads a Godot camera position and
pushes SUMO coordinates into `SetLcRealismZone`. That is `ToSumo`, and it must use the same origin
or the zone lands somewhere else entirely. This is the call site most likely to be missed, because
it type-checks perfectly while being wrong.

### 5.5 Scope limit (owner's, recorded here)

Target ≤ ~20×20 km, so a single recenter keeps everything within ±10–20 km where float is ~mm.
**No tiling, no double-precision render path, no large-world machinery.** Loading all of
Switzerland (~280 km) is out of scope and will show float error; that is accepted.

### 5.6 Two elevation problems the recenter exposed (found while implementing, not in the handoff)

Recentering a 3-D net surfaced two latent flat-net assumptions in the viewer. Both are invisible on
the demo (a 2-D net, where "z = 0" and "on the ground" are the same statement) and both would have
rendered a Geneva cut visibly broken, so they are fixed here rather than deferred.

1. **Overlays with no elevation of their own.** The zone tint, POI ground markers, building-entrance
   doors, procedural building bases, the realism-zone ring, and the wire-fed signal heads all pass
   `sumoZ = 0` meaning "on the ground". Mapped as an absolute elevation against a datum of ~385 m,
   they would render 385 m underground. They now go through `SumoGodotFrame.GroundToGodot`, which
   anchors `heightAboveGround` to the datum instead. Anything that *has* real elevation — road
   meshes (`Lane.ShapeZ`), cars (`KinematicReconResult.Z`), peds (`IPedElevationSource`), the
   NetworkModel-fed signal heads (which already sample the lane's end z) — keeps using `ToGodot`
   with that real value. The datum is FLAT, so a ground overlay can sit tens of metres off the true
   surface on hilly terrain; that is a stated limitation (§8), not an oversight.
2. **Crosswalk zebra and lane dashes were emitted at absolute z ≈ 0.02.** Not merely un-recentered:
   they never followed the road at all, they just happened to be right on a flat net. Both builders
   now take the lane's own `ShapeZ` and interpolate the surface elevation at each stripe/dash's arc
   position (`CrosswalkBuilder.ZAtArc`), so the paint rides 2 cm above the actual road, uphill and
   down. A 2-D lane passes `null` and gets exactly the old flat behaviour.

---

## §6 The test fixture

No committed fixture is simultaneously georeferenced, 3-D, and named like a cut, so this work adds
one: `scenarios/_ped/georef_min/`, generated by `scripts/gen-georef-fixture.sh` (dev-side tooling,
never invoked by `dotnet test`; its OUTPUT is committed, per CLAUDE.md's committed-vs-ephemeral
split). It is a synthetic stand-in for a SumoData Geneva box:

* 3×3 grid, ~190 m spacing, 2 lanes/direction, near (6.140 E, 46.200 N);
* `--proj.utm --proj.plain-geo` ⇒ `projParameter="+proj=utm +zone=32 ..."`;
* per-node elevations across 370–400 m ⇒ every lane shape carries a real z;
* `--sidewalks.guess --crossings.guess --walkingareas` ⇒ 20 crossings, 24 walking areas;
* produced as a **real cut** (`netconvert -s FULL --keep-edges.in-boundary`, no `--offset.*`) of a
  larger net anchored ~90 km to the SW, so — exactly like a real Geneva box — it inherits the full
  net's `netOffset` and its own local coordinates are ≈ (91850, 73960). Without that, the fixture
  would sit at 0..400 and would silently pass a viewer with no recenter at all;
* named `scenario.net.xml` + `scenario.sumocfg` + `scenario.rou.xml` — a cut's naming, not `net.xml`.

It is **not** a parity scenario: no golden, no `tolerance.json`. It is a loader/viewer fixture.

The absolute-path `.sumocfg` variant (what `preprocess.py` emits) cannot be committed portably, so a
test writes one to a temp dir at run time, pointing at the committed fixture.

### 6.1 The headless probe (V2), and what it measures on the fixture

`Sim.Viz --external-net <dir|net.xml|scenario.sumocfg> [steps]` loads a net by any of the three
accepted forms, steps it, and prints load time, capability flags, populations, and the ped-vs-car
elevation agreement. It exists because the real targets — `swiss_roads.net.xml` (168 MB) and a real
Geneva cut — live in another repo and no test here can reach them; this is what a human points at
one of them, outside this environment, to find out whether it loads and behaves.

Measured on `scenarios/_ped/georef_min` (400 steps):

```
loaded in 0.35s: 127 edges, 195 lanes, 17 spawn edges
pedestrians=True crossings=True routeGraphNav=True
pedElevation: 68/68 ped lanes carry 3-D geometry (hasElevation=True)
stepped 400x in 2.65s (151 steps/s)
cars=160 (peak 203, arrived 102)  peds=160 (peak 160)
pedZ vs nearest carZ (<=30 m, 107 pairs): median 0.097 m, max 1.524 m
```

**100%** of ped lane ids resolved against `NetworkModel.LanesById` (the shared-id-space claim of
§4.2, measured rather than assumed), and the ped-vs-car elevation agreement is ~10 cm at the median
against the handoff's "within a metre or two" bar. The 1.52 m maximum is a ped on a walking area at
a junction where the two joining lanes differ in height — the nearest-lane-polyline attribution of
§8.2, behaving as documented.

---

## §7 Parity and determinism argument (the accept/reject gate)

| Surface | Why it cannot move |
| ------- | ------------------ |
| Goldens / `Sim.ParityTests` | Nothing here is in the parity path. `Sim.Ingest`/`Sim.Core` are read-only to this work — no file in either is modified. The only shared type touched is `PedRemoteReconstructor`, whose existing overload is delegated-to, not changed. |
| The demo (`ForRepoRoot`) | Every new config field is null/0/false by default and unset by `ForRepoRoot`. `netPath` resolves to the identical string. `RoutePaths` unset ⇒ the identical single-file scrape. `_pedElevation` is empty on a 2-D net ⇒ `Sample()` still writes literal 0.0. `SumoGodotFrame.Identity` is the same arithmetic. |
| Ped determinism | No new RNG stream; no existing draw removed or reordered. A run that never calls a density setter is bit-identical. |
| `dotnet test` without SUMO | The fixture is committed XML. `gen-georef-fixture.sh` is never invoked by a test. |

The gate is: `dotnet test` green (including the City3D `CityLib.Tests`, which are NOT in
`Traffic.sln` and must be built explicitly — CLAUDE.md measurement-discipline item 9), plus the new
tests in `EXTERNAL-NET-LOADING-TASKS.md` each asserting its stated numeric condition.

---

## §8 Known limitations, stated rather than hidden

1. **Lowering a density cap drains by attrition** (§3.2) — deliberate, matches cars.
2. **`ElevationAt` is nearest-ped-lane, not point-in-polygon.** A ped standing in the middle of a
   large walking area is attributed the elevation of the nearest *lane polyline* point, which for a
   walkingarea is its outline. On real terrain that is a sub-metre error, well inside the "matches
   the nearby car Z within a metre or two" bar the handoff sets.
3. **The recenter is ≤20 km** (§5.5) — owner scope, not a technical ceiling.
4. **Whole-Switzerland load (168 MB net) is untested here.** The loader has no size ceiling and the
   fixture proves the *shape* of the problem, but the 168 MB net lives in BIG's dist repo and is not
   available in this environment; parse time and memory on it are unmeasured, and this design makes
   no claim about them. `Sim.Viz --external-net` (§6.1) is the tool for finding out.
5. **The viewer's ground datum is flat** (§5.6). Overlays with no elevation data of their own — zone
   tint, POI markers, doors, procedural building bases, the realism ring, wire-fed signal heads —
   sit at the net's mid-elevation, so on hilly terrain they can be tens of metres off the local
   surface. Fixing it properly means sampling the surface per overlay point, which is
   `IPedElevationSource`'s job and a reasonable follow-up; it was not needed to make a cut render.
6. **Three `CityLib.Tests` were already failing before this work** (`ReconstructorS2Tests`:
   `…DoesNotCreep`, `…CenterIsHalfLengthBehindSnapshotFront`, `…FollowsConnectingLaneArc_Smoothly`).
   Verified by running them on a clean worktree at the pre-change commit: same three fail. They are
   wall-clock/`Thread.Sleep`-paced reconstruction tests and are unrelated to anything here; they are
   left as found rather than silently adjusted.
