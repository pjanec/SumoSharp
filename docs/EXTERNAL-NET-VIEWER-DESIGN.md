# EXTERNAL NET LOADING — design (HOW)

Design of record for the two handoffs received 2026-07-27 from the BIG/Spectacle session:

* `HANDOFF-external-net-loading.md` — the **engine** changes BIG needs (C1/C2/C3).
* `HANDOFF-godot-city3d-arbitrary-net.md` — the **Godot City3D viewer** changes (T1/T2/T3).

The WHAT lives in those two handoff documents, reproduced verbatim under `docs/handoffs/`. This
document is the HOW: mechanisms, data structures, the exact seams touched, and the
determinism/parity argument. Work breakdown is in `EXTERNAL-NET-VIEWER-TASKS.md`; the checkable
to-do list is `EXTERNAL-NET-VIEWER-TRACKER.md`.

---

> ### ⚠ Read this first: two sessions, one feature
>
> A parallel session owns the **engine-side API contract**:
> `docs/EXTERNAL-NET-LOADING-API-CONTRACT.md` (branch `claude/document-review-r0uhcw`), with its own
> `EXTERNAL-NET-LOADING-{DESIGN,TASKS,TRACKER}.md`. **That contract is authoritative for every public
> signature.** This document covers what THIS branch actually implemented and why; where the two
> speak about the same API, the contract wins and this document is the implementation note.
>
> These docs were originally named `EXTERNAL-NET-LOADING-*` and collided with theirs file-for-file.
> Renamed to `EXTERNAL-NET-VIEWER-*` so both sets can land on `main` without a merge conflict.
>
> **Division of labour, as it actually stands:**
>
> | Contract task | What it delivers | State |
> | --- | --- | --- |
> | B1, B2 | `NetPath`/`RoutePaths`/`RoutePath`, `ForSumocfg` | **implemented on this branch** (§1), signatures match the contract exactly |
> | D1 | live pedestrian density knobs | **implemented on this branch** (§3) — API not specified by the contract, see §3.4 |
> | C1–C5 | pedestrian Z (retained `ShapeZ`, `ElevationsAlong`, wire kind 5, the `out z` overload) | **theirs** — deliberately not built here (§4) |
> | T1–T3 | the Godot viewer: arbitrary nets, recenter, density sliders | **implemented on this branch** (§5) |
>
> The contract's own status probes (`grep -n "NetPath" src/Sim.LiveCity/LiveCityConfig.cs` etc.) will
> report B1/B2/D1 as landed once this branch merges, and C1–C5 as absent until theirs does.

---

## §0 Scope, and the one invariant that governs everything

Two consumers — BIG's Spectacle scene and the Godot City3D viewer — want to run **georeferenced
external Swiss nets** (a SumoData `preprocess.py` cut sub-area of Geneva) through `LiveCitySim`,
with free-style live density controls. Today they cannot, for five independent reasons (of which
this work addresses four — see §4 for the one it deliberately leaves alone):

| # | Blocker | Where |
| - | ------- | ----- |
| C1 | net path is hardcoded `<DatasetDir>/net.xml`; a cut is `scenario.net.xml` + a `.sumocfg` | `LiveCitySim.cs:143`, `:473` |
| C2 | pedestrians have no Z; on a 3-D net they would render hundreds of metres below the road | `PedRemoteReconstructor.cs:106`, `LiveCitySim.Sample()` — **not addressed here, see §4** |
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

### 3.4 The D1 API is ours by default — flag for the other session

`EXTERNAL-NET-LOADING-API-CONTRACT.md` §4 names task **D1** ("live ped density knobs") but does not
specify its signatures, so this branch chose them:

```csharp
PedDemand.SetPopulationCap(int) / SetSpawnRatePerSecond(double)   // + PopulationCap/SpawnRatePerSecond getters
LiveCitySim.SetPedDensity(int populationCap, double spawnRatePerSecond)
LiveCitySim.SetCarDensity(int targetConcurrent, int? spawnPerStep = null)
LiveCitySim.PedDemand { get; }                                    // escape hatch
```

If the other session implements D1 independently with a different surface, these collide. This is the
one place where the two workstreams can produce incompatible public API without either being wrong,
so it wants an explicit decision rather than a merge.

One behavioural point worth agreeing on regardless of whose API wins: **lowering a cap drains by
attrition** (§3.2) — it stops new spawns and does not despawn anybody. That matches the existing car
knob and avoids people vanishing mid-stride.

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

## §4 Pedestrian Z — NOT DONE HERE (contract tasks C1–C5, the other session's)

The handoff's Change 2 asks for `PedRemoteReconstructor.TryGetRenderPose(..., out double z, ...)` and
real pedestrian elevation on a 3-D net. **This work does not deliver it** —
`EXTERNAL-NET-LOADING-API-CONTRACT.md` §2 assigns it to tasks C1–C5 in the other session, and its
design is better than what this branch briefly had: z is **retained** from the net through
`PedLane.ShapeZ` and interpolated along the ped's existing waypoint cursor, rather than recovered by
a nearest-lane spatial search at render time. It also handles the 27 measured multi-level locations
nationwide that any nearest-surface heuristic gets wrong (contract §9.6).

An earlier revision of this branch did implement it, as an injected `IPedElevationSource` sampled
from the vehicle-side `Lane.ShapeZ` (the ped subsystem may not reference `Sim.Ingest`, so the
dependency was inverted rather than the ped types made 3-D). That is a fundamentally different shape
from retaining z in the ped stack, so it was **removed** rather than left to conflict. If it is ever
wanted, it is in this branch's history.

**The viewer is ready for theirs.** Per contract §1 the City3D change is two lines, in
`demos/City3D/CityLib/PedReconstructor.cs`; this branch has already moved that call onto the
placement frame and left the note at the call site. The moment C4+C5 tick, it becomes:

```csharp
if (!_reconstructor.TryGetRenderPose(id, out var pos, out var z, out var visible, out _) || !visible) continue;
var (gx, gy, gz) = _frame.ToGodot(pos.X, pos.Y, z);   // was _frame.GroundToGodot(pos.X, pos.Y, 0.0)
```

Note this branch routes placement through `SumoGodotFrame` (§5) rather than the bare
`CoordinateTransform.SumoToGodot` the contract quotes — the axis mapping is identical, the frame just
subtracts the recenter origin first.

**Consequence, stated so it is not discovered later:** until the parallel work lands, pedestrians on
a georeferenced 3-D net render at the viewer's flat ground datum (§5.3) rather than on the local road
surface, and `LiveCityPed.Z` is 0. Every other part of the 3-D story is in place — road meshes, cars,
crosswalk and lane markings all follow the net's real elevation (§5.6).

## §4.1 API change — lane provenance through `IPedNavigation` (added after C1–C5)

**What went wrong.** `ElevationsAlong(path)` receives bare 2-D points, so the provider could only
decide which surface a point belonged to by nearest-in-plan-view. Wherever surfaces STACK — a
footbridge over the path beneath it — both candidates are equidistant and the tie-break decides. A
synthetic stacked-net test (12.5 m clearance) showed both queries collapsing onto the bridge, so a
ped walking underneath would be lifted onto it for the vertices inside the overlap. `FindPath` knew
which node produced each vertex and discarded it before returning.

This mattered at runtime, not just at the query API: the ped's elevation channel is *built* by
calling `ElevationsAlong`, so the wrong height was baked into the channel the ped then carried.

**The change — `IPedNavigation` now has exactly TWO members, both mandatory:**

```csharp
IReadOnlyList<Vec2>? FindPath(Vec2 start, Vec2 goal, out IReadOnlyList<int>? vertexSurfaces);
IReadOnlyList<double> ElevationsAlong(IReadOnlyList<Vec2> path, IReadOnlyList<int>? vertexSurfaces);
```

**This is a BREAKING interface change, deliberately.** It first shipped additively — as default
interface methods alongside the old `FindPath(start, goal)` / `ElevationsAlong(path)` pair, so that
DotRecast, every test double, and all 79 existing call sites compiled untouched. That is exactly the
property that made it wrong, and it is documented here as the reasoning, not just the outcome:

- **A default that returns flat is a silent wrong answer, not a safe fallback.** A provider that
  simply does not override reports "no provenance" and gets zeros — indistinguishable at the call
  site from a genuinely 2-D net. `SumoNavMesh` sat in exactly that state and was described in this
  document as "correct for a 2-D net, which is all it is used for". It is not: `PedSimSource` and
  `SceneGen` route on it, and the City3D viewer renders those peds in 3-D.
- **Z is not an optional aspect of a position.** Every consumer of a ped pose in the 3-D viewer needs
  a height. An API where the 2-D form is still callable means the omission is invisible; an API where
  it is not means the compiler enumerates every site that has to be thought about. That is the whole
  value of making it mandatory — the migration list is generated, not remembered.

So the 2-D forms are **gone**, with no default bodies:

| Removed | Replacement |
| --- | --- |
| `IPedNavigation.FindPath(Vec2, Vec2)` (default body) | the 3-arg form; discard with `out _` |
| `IPedNavigation.ElevationsAlong(IReadOnlyList<Vec2>)` (default body) | the 2-arg form; pass `vertexSurfaces: null` |
| `SumoRouteGraphNav.FindPath(Vec2, Vec2)` | ditto |
| `SumoRouteGraphNav.ElevationsAlong(IReadOnlyList<Vec2>)` | ditto |
| `PedRemoteReconstructor.TryGetRenderPose(id, out pos, out visible, out animTag)` | the 5-out-param form; discard z with `out _` |

`SumoNavMesh.FindPath(start, goal, ISet<int>? blocked)` — POC-5's blocked-set query — was **renamed
to `FindPathAvoiding`**. It is not part of the interface, and leaving it as an overload of `FindPath`
made `FindPath(a, b, out _)` ambiguous at the call site (CS1615/CS1620: the compiler binds the
3-argument call to the blocked-set form and then rejects the `out`). The rename keeps the routing
entry point unambiguous, which is precisely what the mandatory signature depends on.

Every migrated call site now discards **explicitly** (`out _`, `vertexSurfaces: null`) rather than by
omission, so "this caller does not want the height" is a visible decision in the diff.

#### 4.1.1 This CONTRADICTS contract C5·SC1 — flag for the other session

`EXTERNAL-NET-LOADING-API-CONTRACT.md` C5·SC1 states the success condition as: *"the existing
4-out-param overload's body is untouched, and all 15 call sites compile unedited."* That condition is
now **deliberately unmet** — the overload is deleted and all of its call sites were edited (to
`out _`). This is the owner's call, recorded verbatim: *"why Z as additive. that leads to omissions.
no dual interfaces when we need to pass z coord everywhere. no just 2d. compiler will catch."*

Nothing about the *behaviour* C5 specifies changed: the 5-out-param body, its smoothing, its
`ReconstructElevationAt` call, and the `z == 0.0`-on-a-flat-stream semantics are all exactly as C5
describes. Only the additivity requirement is dropped. SC2 ("both overloads agree") is unsatisfiable
by construction and has been **replaced** by a reflection test asserting there is exactly one
overload and that its third parameter is `out double` — a compile-time check cannot assert its own
absence, so the absence is asserted at runtime instead.

The ids are **opaque and provider-local** (node index for `SumoRouteGraphNav`, and nothing at all for
providers that do not override): they mean nothing except to the instance that issued them, carry no
ordering, and must only be handed back to that same instance. `SumoRouteGraphNav.ElevationsAlong`
validates the range and falls back to flat rather than indexing a foreign id into its own graph.

**Where provenance is threaded.** The path is reassigned or re-sliced at several points, and a stale
or misaligned surface list is worse than none — it reads heights off the *previous* route — so every
one of them is handled explicitly:

| Site | Handling |
| --- | --- |
| `PedDemand` spawn | routes with provenance; hands it to `AddPed`, or to the lively timeline sampler |
| lively Walk legs | sub-paths (pause-split, with interpolated split points) map each point back onto the ped's own full route to recover its surface |
| `PedLodManager.PathSurfaces` | cleared by the `Path` setter, so it can never outlive the path it describes |
| promotion | steering route's own provenance attached |
| demotion | `ReanchorSurfaces` re-anchors the list the same way `ReanchorAt` re-anchors the path (prepend + possible leading-vertex drop), returning null if the lengths cannot be reconciled |
| `RecoverRoute` fallbacks | splice/beeline paths are not routed, so provenance is explicitly nulled |
| lively ped's timeline geometry | a different list from `Path`, so ids would not line up — falls back to proximity, by reference check |

**`SumoNavMesh` — now a full provenance provider too.** It was previously left inheriting the flat
default, on the reasoning that it is "the 2-D demo provider". Removing the default made that claim
untenable (see above), so the mesh provider was given real provenance rather than a hand-written
flat implementation:

- `FindPathAvoiding` records, per emitted waypoint, the **`BakedPolygon` index it belongs to**. The
  corridor the funnel walks *is* the provenance — it was already computed and then discarded along
  with the polygon list once the waypoints were pulled out of it. A portal vertex is attributed to
  the polygon being **entered**, so a waypoint on a sidewalk/crossing boundary names the surface the
  ped is heading onto rather than the one it is leaving.
- `ElevationsAlong(path, vertexSurfaces)` reads each height off `_polygons[index]`'s own
  `ElevationReference`/`ElevationZ` pair (sidewalk → `Spine`, walkingarea/crossing → `Vertices`),
  falling back to the plan-view `LocatePolygon` lookup when the provenance is absent or misaligned.

`PedElevationMultiLevelTests` now runs the same stacked-deck fixture (12.5 m clearance) against
**both** providers, and asserts the mesh one separates the decks and names `ground_0` for every
vertex of the under-bridge route. The two providers are held to one standard.

**Still not covered:** `DotRecastNavMesh`, which reports `null` provenance and flat elevations. That
is now an explicit, hand-written implementation with a comment saying so — a *choice* recorded in the
provider, not a default silently inherited from the interface. The same applies to the 13 test
doubles.

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
   with that real value. **The datum was FLAT at this point**, so a ground overlay could sit tens of
   metres off the true surface on hilly terrain. **§7.2 has since closed that**: `GroundToGodot`
   samples a baked `TerrainField`, so every one of these overlays follows the real ground with no
   further edits — putting the ground height behind this one seam is what made the later fix a
   one-line change instead of seven.
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
stepped 400x in 2.65s (151 steps/s)
cars=160 (peak 203, arrived 102)  peds=160 (peak 160)
car elevation range: 371.64 .. 395.64 m
```

The car elevation range is the 3-D check: cars resolve real `Lane.ShapeZ` elevations across the
fixture's 370–400 m band rather than sitting at 0.

---

## §7 Parity and determinism argument (the accept/reject gate)

| Surface | Why it cannot move |
| ------- | ------------------ |
| Goldens / `Sim.ParityTests` | One parity-core file IS modified: `Sim.Ingest/NetworkParser.cs`, for the multi-lane cont-bay bug of §7.1. Full suite re-run after: **775 pass, 0 fail** — no golden moved. Nothing else here is in the parity path, and `PedRemoteReconstructor` is left exactly as found. |
| The demo (`ForRepoRoot`) | Every new config field is null/0/false by default and unset by `ForRepoRoot`. `netPath` resolves to the identical string. `RoutePaths` unset ⇒ the identical single-file scrape. `SumoGodotFrame.Identity` is bitwise the same arithmetic. |
| Ped determinism | No new RNG stream; no existing draw removed or reordered. A run that never calls a density setter is bit-identical. |
| `dotnet test` without SUMO | The fixture is committed XML. `gen-georef-fixture.sh` is never invoked by a test. |

The gate is: `dotnet test` green (including the City3D `CityLib.Tests`, which are NOT in
`Traffic.sln` and must be built explicitly — CLAUDE.md measurement-discipline item 9), plus the new
tests in `EXTERNAL-NET-VIEWER-TASKS.md` each asserting its stated numeric condition.

### §7.1 A parser bug the fixture found (the one parity-core change)

Committing `georef_min` immediately failed a test that nothing here touched:
`JunctionLinkLaneMapTests.EveryCommittedNet_IntLanesAreAllPresentInLinkIndexByInternalLane…` sweeps
**every** committed `*.net.xml`, and reported

```
[scenarios/_ped/georef_min/scenario.net.xml] junction 'n00' link 2 lane ':n00_2_0'
mapped to link index 3, expected 2.
```

That is a real defect in `NetworkParser`, not a bad fixture. Building
`LinkIndexByInternalLane`/`EntryConnectionByLink` involves walking back from a link's final internal
stage through the earlier stages of a continuation ("cont") turn. The walk mapped **every lane of
each internal edge** it passed through to that link, and found the previous hop by matching the
**edge** rather than the lane.

On a single-lane internal bay — which is every cont bay in every net committed before this one —
"the edge" and "the lane" are the same thing, so the bug was invisible. `georef_min`'s junction
`n00` has a two-lane internal bay `:n00_2` where only lane 1 continues through the internal junction
into link 3's second stage. The edge-wide loop therefore also stamped `:n00_2_0` — which is link 2's
own controlling lane — as belonging to link 3, silently overwriting a correct entry.

The fix follows one lane per stage: the hop's own `fromLane` on the internal edge it came from, and a
previous-hop search keyed on that exact lane id. It is the only change to a parity-core file in this
work. **Full parity suite after: 775 pass, 0 fail** — no golden moved, which is what one expects
given no previously-committed net has a multi-lane cont bay to trigger it.

This is the fixture earning its keep on its first day, and an argument for the "add a net shape the
committed corpus lacks" instinct generally.

---

## §7.2 The ground datum stops being flat — a baked terrain field

Owner's directive, verbatim: *"what about the grey grid - cant stay at zero elevation. maybe at avg
elevation? maybe not flat but following the avg height of the closest road net parts?"* and
*"regarding grey grid and zones. needs to be built/baken. on road net load. tinted zones need to
follow the grid."* This closes §8.5 (was: "the viewer's ground datum is flat") and §5.6.

### 7.2.1 Mechanism — one field, one seam

The road network already carries the only elevation truth the viewer has: `Lane.ShapeZ`, a real
height at every lane vertex. A **`TerrainField`** turns that scattered sample set into a function
defined everywhere:

1. **Lattice.** A uniform corner lattice over the net's x/y bbox at `CellSizeMeters` (40 m default).
   The per-axis corner count is capped (`MaxCornersPerAxis = 512`) by growing the cell size, so a
   Switzerland-sized net costs the same lattice as a city block — bounded memory, bounded bake time.
2. **Scatter.** Every lane vertex with a z distributes its height into the four surrounding corners
   by **bilinear weight** (the transpose of the sampling operator, so a corner surrounded by road
   ends up at the road's height rather than at the height of whichever vertex happened to be
   nearest). Each corner accumulates `Σ w·z` and `Σ w`; corners with `Σ w > 0` are **measured**.
3. **Fill.** Corners with no road near them are filled by a deterministic breadth-first flood from
   the measured set: each ring takes the mean of its already-known 4-neighbours. This is the literal
   reading of the directive — *"following the avg height of the closest road net parts"*.
4. **Relax.** Two Jacobi smoothing passes applied **only to filled corners**; measured corners are
   pinned. So the field passes exactly through the road heights and the fill in between is smooth
   rather than terraced.
5. **Sample.** `HeightAt(x, y)` is bilinear over the containing cell, with the query clamped to the
   lattice, so it is defined (and continuous) over the whole plane.

`TerrainField.Flat(z)` is the degenerate field — `HeightAt` returns `z` everywhere. `IsFlat` is true
for it and only for it.

**The field lives on `SumoGodotFrame`.** That is the single seam every ground-anchored overlay
already goes through:

```csharp
frame.GroundToGodot(x, y, h)   ==   frame.ToGodot(x, y, frame.Terrain.HeightAt(x, y) + h)
```

`SumoGodotFrame.ForNetwork` bakes the field from the same one lane scan it already makes to compute
the recenter origin, so the terrain costs one extra pass over vertices it is already visiting. Every
existing `GroundToGodot` caller — zone tint, POI markers, doors, procedural building bases and their
walls, traffic-light poles, the realism ring — becomes terrain-following with **no signature change
and no call-site edit**. That is why the field goes on the frame rather than being threaded as a new
parameter: threading it would have meant touching seven builders and inviting exactly one of them to
be missed.

On a 2-D net `ForNetwork` bakes `Flat(0.0)` and `SumoGodotFrame.Identity` is `Flat(0.0)`, so
`GroundToGodot` is bit-identical to what it was — the 2-D regression is structural, not a tolerance.

### 7.2.2 Why the grid had to be re-baked rather than re-offset

The grid was a **flat line mesh translated under the camera every frame** (snapped to the spacing, so
it read as an infinite floor). A translated flat mesh cannot follow terrain — the whole point of the
recenter was that the mesh never changes.

So the grid is now **baked once, on net load, over the net's own bbox** (+ `GridMarginMeters`), and
the per-frame recenter is **deleted**. Each grid line is a polyline subdivided at the terrain cell
size and sampled through `GroundToGodot`, so it drapes over the field. The line spacing adapts
(`max(25 m, extent / MaxGridLinesPerAxis)`) so the vertex count stays bounded on a large net.

This is a deliberate behaviour change on the 2-D demo too: the grid is finite and shows where the
city actually is, instead of following you to infinity. On a flat field the baked mesh is planar and
looks exactly as before within the net; fly far enough out and it now ends.

### 7.2.3 Zones: sampling at the corners is not enough

A district polygon has a handful of vertices. Sampling the field only at those vertices makes each
fan triangle a **plane through three terrain points** — over a 400 m district on a slope the middle
of the tint can be metres under the road it is supposed to sit beneath.

`ZoneGroundBuilder.Build` therefore keeps its fan topology (so the covered area is exactly what it
was — no clipping, no coverage change) and **recursively subdivides each fan triangle at edge
midpoints** until every edge is ≤ the terrain cell size, capped at `MaxSubdivisionDepth = 5`
(1024 sub-triangles per fan triangle). Every generated vertex — including the interior midpoints —
is placed with `GroundToGodot`, so the interior follows the field, not a chord across it.

On a flat field subdivision changes the vertex *count* but not the surface, so the tint renders
identically; `Area` (the largest-area-first sort key) is computed from the original polygon and is
untouched. `PoiGroundBuilder` markers are single points and need no subdivision — they follow the
field through `GroundToGodot` alone.

### 7.2.4 Determinism

The bake is a fixed sequence of double arithmetic in index order: the scatter walks lanes in
`NetworkModel` order and vertices in shape order, the flood is a FIFO queue seeded in raster order,
the relaxation is a fixed two-pass Jacobi. No `Random`, no parallelism, no dictionary-order
dependence. Baking the same net twice yields a bitwise-identical field, asserted in
`TerrainFieldTests`. None of this is in the engine: it is all viewer-side presentation, so the parity
suite and the determinism hash cannot see it at all.

## §8 Known limitations, stated rather than hidden

1. **Lowering a density cap drains by attrition** (§3.2) — deliberate, matches cars.
2. **Pedestrians have no elevation** (§4) — owned by the parallel ped-engine workstream, not
   delivered here. On a 3-D net they render at the flat ground datum.
3. **The recenter is ≤20 km** (§5.5) — owner scope, not a technical ceiling.
4. **Whole-Switzerland load is untested *here*, but the other session measured it.** The real nets
   are not in this repo, so nothing on this branch has run against them.
   `EXTERNAL-NET-LOADING-API-CONTRACT.md` §8 supplies the numbers: the Geneva cut (44 MB) loads in
   ~11.6 s / 572 MB, full Switzerland (161 MB) in ~80 s / 1.65 GB, and the constructor makes four
   full passes over the net file (pre-existing, not changed by either workstream). Their
   recommendation — load a cut, not the country, and show a progress indicator if you must, since the
   load is synchronous in the constructor — applies directly to the City3D loader built here.
   `Sim.Viz --external-net` (§6.1) is the tool for re-checking on a given machine.
5. ~~**The viewer's ground datum is flat**~~ — **CLOSED by §7.2.** The datum is a baked `TerrainField`
   interpolated from `Lane.ShapeZ`, and every overlay that goes through `GroundToGodot` (zone tint,
   POI markers, doors, procedural building bases and walls, traffic-light poles, the realism ring)
   follows it. The grey grid is baked over the net and draped over the same field. What *remains*
   limited: the field is an interpolation of **road** heights, so terrain far from any road is a
   smooth fill rather than real ground, and its resolution is `CellSize` (40 m, growing on very large
   nets). Measured on `scenarios/_ped/georef_min` — 27.5 m of relief — the field reproduces every
   lane vertex's own height to within **0.326 m**; the flat datum it replaced was out by up to ~14 m.
   Also: the grid is now **finite** (net bbox + 400 m) rather than following the camera to infinity.
6. **Three `CityLib.Tests` were already failing before this work** (`ReconstructorS2Tests`:
   `…DoesNotCreep`, `…CenterIsHalfLengthBehindSnapshotFront`, `…FollowsConnectingLaneArc_Smoothly`).
   Verified by running them on a clean worktree at the pre-change commit: same three fail. They are
   wall-clock/`Thread.Sleep`-paced reconstruction tests and are unrelated to anything here; they are
   left as found rather than silently adjusted.
