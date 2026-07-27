# External georeferenced net loading + pedestrian elevation — DESIGN (HOW)

**Requirements / WHAT:** `docs/EXTERNAL-NET-LOADING-HANDOFF.md` (the request as received from the
BIG/Spectacle session, verbatim). This document does not restate it — it says *how* we satisfy it.

**Tasks & success conditions:** `docs/EXTERNAL-NET-LOADING-TASKS.md`.
**Tracker:** `docs/EXTERNAL-NET-LOADING-TRACKER.md`.

Baseline for this work: `main` @ `791d3e6` (the exact commit the handoff was written against, so its
quoted line numbers are valid).

**Real validation data is available** (received 2026-07-27, `geneve.7z`, 248 MB unpacked, held
**ephemerally** in the session scratchpad — never committed, see §6):
`common/swiss_roads.net.xml` (161 MB, the exact full-Switzerland net named in the handoff's
definition-of-done), `geneve/tools/geneve.net.xml` (44 MB Geneva cut), and four real
`geneve_*.sumocfg` files. Every measurement below marked **[measured]** was taken on these files, not
on a proxy. This dissolves most of what §7 originally listed as unverifiable — and it corrected the
design in one place (§0/C4).

---

## §0 — Scope, and three corrections to the handoff

The handoff asks for two changes. Reading the code establishes that **three** are needed, and that one
of its stated mechanisms is not implementable as written.

| # | Handoff says | Reality | Consequence |
|---|---|---|---|
| C1 | net path is hardcoded; add `NetPath`/`RoutePath` + `ForSumocfg` | **Correct.** `LiveCitySim.cs:143`, `:473`; `LiveCityConfig` has only `DatasetDir` | Implement as asked (§2), plus a dir probe for `scenario.net.xml` so `ForDataset(cutDir)` also works |
| C2 | add a `TryGetRenderPose(..., out double z, ...)` overload that projects onto the nearest ped lane and calls `ElevationAtOffset` | **Not implementable as written.** `PedRemoteReconstructor` is constructed from an `IPedReplicationSource` alone (`PedRemoteReconstructor.cs:104`) — it holds **no `PedNetwork` and no `NetworkModel`**. It has no geometry to project onto. Worse, `Sim.Pedestrians.csproj` states outright that the project **must never reference `Sim.Ingest`**, where `NetworkModel`/`LaneGeometry` live | The overload cannot compute z from geometry it does not hold. §3 resolves this by **retaining** the z that `PedNetworkParser` currently discards, rather than reconstructing it — which also keeps the whole change inside `Sim.Pedestrians` and so *satisfies* Principle 6 instead of tunnelling through it (§3.1) |
| C3 | "keep `Step()` reading these off the by-reference `cfg` each tick (**it does today**)" for `CarTargetConcurrent`, `CarSpawnPerStep`, `PedPopulationCap`, `PedSpawnRatePerSecond` | **Half true.** The *car* knobs are read live off `_cfg` every step (`:734`, `:743`). The *ped* knobs are **copied once into a fresh `PedDemandConfig` in the constructor** (`:277-278`) and never re-read | BIG's pedestrian slider would silently do nothing. A third change is required. §4 |

**C4 — found by the real data, after the first draft of this design.** The draft said `ForSumocfg`
should scrape spawn edges from `RouteFiles[0]`. On the real `geneve_Medium.sumocfg` that is
**`common/vType.config.xml`** — a file with **107 `<vType>` elements and zero routes** [measured]. The
actual route files sit at positions 4 and 5 of a six-entry list (`gen_flow_medium.rou.xml`: 600 routes,
`routes_K1000.rou.xml`: 1000 routes). Taking `[0]` would scrape nothing, silently fall through to
derive-from-net, and lose the real demand's edge set. Corrected in §2.3: scrape the **union of all**
route files. This is exactly the kind of error that reading one real input catches and no amount of
reasoning does (CLAUDE.md measurement discipline #2).

**C5 — the handoff's "no change to `PedNetworkParser`" is wrong, and so was this design's first draft
for accepting it.** Both assumed the ped subsystem should stay 2-D and z should be recovered from the
vehicle-side model. But the ped net is parsed from the *same 3-D `net.xml`*, and `ParseShape`
(`PedNetworkParser.cs:60,77,78,89`) reads the z and throws it away — so recovering it by spatial search
is reconstructing discarded input, approximately and at a per-frame cost. `PedNetworkParser` **does**
change: it retains the third coordinate (§3.2). The blast-radius fear behind the original decision does
not survive checking — ped geometry has **9 usages across 5 files** — and the layering objection points
the other way (§3.1).

Out of scope, explicitly: SumoData's `--percent` / `auto_calibrate.py` density calibration (the handoff
itself puts it off BIG's critical path), and everything on the IG side (UTM→flat, terrain Z, placement).

---

## §1 — Verified ground truth

Each of these was read, not assumed. They are the load-bearing facts for §2–§4.

1. `LiveCitySim` ctor derives **one** `netPath` (`:143`) and feeds it to **three** consumers:
   `NetworkParser.Parse` (`:147`), `PedNetworkParser.Load` (`:161`), and `CrosswalkSignals.FromNet`
   (`:271`). All three must move onto the resolved path together.
2. `LiveCityScene.Load(cfg.DatasetDir)` (`:141`) reads the optional `zones/buildings/pois` JSON
   companions **from the directory**, not from the net file. It stays on `DatasetDir`.
3. The route file is read once, at `:473`, by `ReadDrivableEdges`, whose empty result already falls
   through to `DeriveDrivableEdgesFromNetwork(model)` (`:475-483`). A missing/renamed route file is
   therefore already a soft failure, not a throw.
4. `ScenarioConfigParser` (`src/Sim.Ingest/ScenarioConfigParser.cs`) already parses `<input>` into
   `NetFile` (`string?`) and `RouteFiles` (list), defaulting to null/empty when the section is absent.
   No new parser is needed.
5. `NetworkParser.Parse` iterates `root.Elements("edge")` with **no `function` filter** and populates
   `ShapeZ` for *every* `<lane>` (`NetworkParser.cs:39-67`). Therefore sidewalk, `crossing`,
   `walkingarea` and internal lanes **are** in `NetworkModel.LanesById`, each carrying `ShapeZ` when the
   net is 3-D. This makes the *superseded* nearest-lane mechanism possible, and it is also how the vehicle side gets
   its own z; §3 no longer relies on it for pedestrians.
6. `Lane.ShapeZ` is `IReadOnlyList<double>?`, **null on a 2-D net** (`NetworkModel.cs:43`), and its
   header states it is consumed only by output-side geometry, never by car-following/lane-change/junction
   math. Elevation is provably parity-inert.
7. `LaneGeometry.ElevationAtOffset(shape, shapeZ, offset)` (`LaneGeometry.cs:75`) already interpolates z
   along a polyline using the **same 2-D segment walk and clamp** as `PositionAtOffset`. Reused as-is.
8. `PedNetwork` carries ped geometry as `Vec2` only: `PedLane.Shape`, `PedCrossing.Shape`/`Outline`,
   `PedWalkingArea.Polygon`. No z anywhere. It is a `sealed record` with an `Empty` singleton used by the
   ctor's graceful-degrade path.
9. `LiveCitySim.Sample()` builds every `LiveCityPed` with a literal `0.0` z (`:1076`) from
   `_manager.PositionOf(id, _now)` — it does **not** go through `PedRemoteReconstructor`. So there are
   **two** ped surfaces to fix, not one.
10. `TryGetRenderPose`'s existing 4-out-param signature has **15 call sites** across `Sim.Viewer`,
    `Sim.Viz`, `demos/City3D`, and two test projects. It must not change.
11. `PedDemandConfig` is a `sealed class` and `PedDemand` reads `_config.PopulationCap` /
    `_config.SpawnRatePerSecond` **live** on the spawn path (`PedDemand.cs:165`, `:174`, `:182`). The
    liveness gap in C3 is purely that `LiveCitySim` never keeps or refreshes that object.
12. No committed net in the repo is georeferenced (`grep` for `projParameter="+proj` → zero hits). The
    only 3-D geometry in the tree is `demos/City3D/CityLib.Tests/fixtures/elevated.net.xml`, which has no
    pedestrian infrastructure. A new fixture is required (§6).

---

## §2 — Change 1: net/route path resolution

### §2.1 Config surface (additive)

Two nullable properties on `LiveCityConfig`, defaulting to `null` so every existing caller is unchanged:

```csharp
public string? NetPath { get; set; }     // explicit net file; null => probe DatasetDir (§2.2)

// SUMO's <route-files> is a LIST, and on real configs the first entry is often a vType file with no
// routes at all (§0/C4). So the knob is plural and every entry is scraped.
public IReadOnlyList<string>? RoutePaths { get; set; }

// Single-file shorthand for the common case and for the name the handoff asked for. Setting it is
// equivalent to setting RoutePaths to a one-element list; RoutePaths wins if both are set.
public string? RoutePath { get; set; }
```

### §2.2 Resolution rules

A single private static helper, `ResolveNetPath(LiveCityConfig)`, applied once in the ctor and used by
all three consumers from §1.1. Precedence, in order:

1. `cfg.NetPath`, if non-null/non-empty — used verbatim, no existence check (let the parser's own
   exception name the missing file).
2. `Path.Combine(cfg.DatasetDir, "net.xml")` if it exists.
3. `Path.Combine(cfg.DatasetDir, "scenario.net.xml")` if it exists — the `preprocess.py` cut-sub-area
   name the handoff calls out.
4. Otherwise fall back to `net.xml` anyway, so the thrown error message names the conventional file.

Rule 2 before rule 3 is what keeps the demo byte-identical: `scenarios/_ped/demo_city/box/` contains
`net.xml`, so it never reaches the probe. Route resolution is `RoutePaths ?? [RoutePath] ??
[DatasetDir/scenario.rou.xml]`, with no probe, since §1.3 already makes absence harmless. The ctor
scrapes **each** resolved route file and unions the drivable-edge sets, so a vType-only entry
contributes nothing instead of shadowing the real routes (§0/C4).

Deliberately *not* done: globbing `*.net.xml`. A dir with two nets would resolve unpredictably; two
fixed names are explicit and diagnosable.

### §2.3 `ForSumocfg`

```csharp
public static LiveCityConfig ForSumocfg(string sumocfgPath)
```

- Parse with the existing `ScenarioConfigParser.Parse` (§1.4) — no new XML reader.
- `DatasetDir = Path.GetDirectoryName(Path.GetFullPath(sumocfgPath))`.
- For `NetFile` and **every** entry of `RouteFiles`: `Path.IsPathRooted(p) ? p :
  Path.Combine(sumocfgDir, p)`. This is SUMO's documented rule and covers both emitters the handoff
  names (`preprocess.py` absolute, demo-city relative) with one expression. The real Geneva configs use
  **relative** paths [measured], so both branches are genuinely exercised by available data.
- `NetFile` absent ⇒ leave `NetPath` null ⇒ §2.2 probes the directory. Non-throwing and predictable.
- `RouteFiles` empty ⇒ leave `RoutePaths` null ⇒ §1.3's derive-from-net fallback.
- **All** route files go into `RoutePaths`, in config order (§0/C4). `ScenarioConfigParser.ParseFileList`
  already splits on `,`/space/tab/newline and trims, so the real configs' multi-line, tab-indented
  `<route-files value="…">` block parses correctly with **no parser change** [measured].
- Otherwise identical to `ForDataset`: `WithEnvOverrides`, `NavMode = RouteGraph`, `RegionPlan = true`.
  Implemented by *calling* `ForDataset(sumocfgDir)` and then setting the two paths, so the two factories
  cannot drift.

### §2.4 Non-breaking argument

Every new property defaults to null; every resolution path with a `net.xml` present behaves exactly as
before; no existing signature changes. The demo, all goldens, and `_bench/*` are untouched by
construction.

---

## §3 — Change 2: pedestrian elevation

> **Superseded design.** The first draft of this section resolved ped z by a **nearest-lane spatial
> search** over the vehicle-side `NetworkModel`, keeping the ped subsystem 2-D. That was wrong in
> principle: the source net **is** 3-D, `PedNetworkParser` currently **discards** the z it reads, and the
> search was reconstructing — approximately, and at a per-frame cost — information that ingest had thrown
> away. It also justified itself with a blast-radius claim that does not survive checking (9 geometry
> usages across 5 files) and a Principle-6 claim that in fact argues the *other* way (below). The design
> now **retains** z instead of reconstructing it. The superseded mechanism survives only as an optional
> fallback for one surface (§3.6).

### §3.1 Principle: retain, don't reconstruct

The pedestrian network is parsed from the same `net.xml` as the vehicle network, and on a real Swiss net
**every** ped lane carries a 3-coordinate shape (§3.2's measurement: 100 %, both nets). `ParseShape`
(`PedNetworkParser.cs:60,77,78,89`) reads that shape and keeps only x,y. So the elevation a pedestrian
needs is present in the input, three characters from where it is dropped.

Retaining it is strictly better than reconstructing it on all four axes that matter:

| | reconstruct (superseded) | **retain (this design)** |
|---|---|---|
| accuracy | nearest lane *in plan view* — approximate | the lane the ped is **actually on** — exact |
| ambiguity | 27 stacked-lane spots nationwide can resolve to the wrong surface | none: a ped on a bridge follows the bridge |
| runtime cost | a spatial query per ped per frame (grid, ring expansion) | interpolation at a cursor the ped already holds — **no search** |
| project layering | needs a cross-project seam to reach `Sim.Ingest`'s `NetworkModel` | stays **entirely inside `Sim.Pedestrians`** |

The layering point is worth stating plainly because the first draft got it backwards.
`Sim.Pedestrians.csproj` forbids referencing `Sim.Ingest` (PEDESTRIAN-DESIGN.md §0 Principle 6). The
superseded design had to tunnel *through* that rule with an `IPedElevationSource` seam precisely so it
could read `NetworkModel.Lane.ShapeZ`. Retaining z needs **no new project reference at all** —
`PedNetworkParser` already opens the net file itself. Principle 6 favours this design.

### §3.2 Ingest: keep the third coordinate, output-only

Add a per-vertex elevation channel to the ped geometry records, index-aligned with the existing 2-D
shape, `null` when the net is 2-D:

```csharp
public sealed record PedLane(..., IReadOnlyList<Vec2> Shape, IReadOnlyList<double>? ShapeZ = null);
public sealed record PedCrossing(..., IReadOnlyList<double>? ShapeZ = null);
public sealed record PedWalkingArea(..., IReadOnlyList<double>? PolygonZ = null);
```

This is **deliberately the same pattern the vehicle side already uses**: `Lane.ShapeZ`
(`NetworkModel.cs:43`) is documented as "consumed only by the read surface's PosZ, never by any
car-following / lane-change / junction math". The ped side becomes symmetric with the car side rather
than inventing a second mechanism. Defaulted trailing parameters keep every existing
`new PedLane(...)` / `new PedCrossing(...)` call compiling unchanged.

`ParseShape` gains a sibling `ParseShapeZ` returning `null` unless the shape carries a 3rd component —
mirroring `NetworkParser.ParseShapeZ`, which already does exactly this on the vehicle side.

**[measured] The data supports this everywhere it matters.** 100 % of ped-lane ids in both real nets
carry z, in all three categories:

| net | sidewalks | crossings | walkingareas | with z / without |
|---|---|---|---|---|
| `geneve.net.xml` (44 MB) | 2 201 | 221 | 2 179 | **4 601 / 0** |
| `swiss_roads.net.xml` (161 MB) | 13 811 | 735 | 13 537 | **28 083 / 0** |

### §3.3 The simulation stays 2-D — on purpose

Only the *output* becomes 3-D. ORCA collision avoidance is inherently a 2-D velocity-obstacle algorithm,
strategic routing is a plan-view graph search, and walking distance on a road-legal grade differs from its
horizontal projection by well under a percent. Making the solver 3-D would change trajectories for no
fidelity gain and would put determinism and the ped regression suite at risk.

So `ShapeZ` is **parity-inert by construction**, exactly as `Lane.ShapeZ` is: no steering, no ORCA, no
routing, no `ActivityTimeline` decision reads it. It is carried alongside and consumed only at the render
seam. This is what keeps every committed 2-D scenario bit-identical.

### §3.4 How z reaches a pedestrian: a path-elevation channel

A ped in flight holds a path (`IReadOnlyList<Vec2>` from `IPedNavigation.FindPath`) and a waypoint cursor
(`ILocalSteering.DesiredVelocity`'s `ref int waypointIndex`). That is already enough to locate the ped
along known geometry — no search required.

`IPedNavigation` gains a **default interface method**, following the existing `HalfWidthsAlong(path)`
precedent verbatim (`INavigation.cs:51-58`) — same signature shape, same "providers that have no model
get a safe default so nothing needs changing" rationale:

```csharp
/// Per-vertex elevation (metres) along `path`, index-aligned with it. Default: all zeros, so a provider
/// with no elevation model yields today's flat behaviour and needs no change.
IReadOnlyList<double> ElevationsAlong(IReadOnlyList<Vec2> path)
{
    return new double[path.Count];   // flat
}
```

`SumoNavMesh` and `SumoRouteGraphNav` override it from the `ShapeZ`/`PolygonZ` retained in §3.2 — each
already knows which lane/polygon produced each waypoint, since it built the path. The ped's instantaneous
z is then a linear interpolation between the two elevations bracketing its cursor, using the fraction it
already computes for steering. Cost: one lerp. No grid, no nearest-neighbour, no ring expansion.

Existing providers (DotRecast, the test doubles) inherit the flat default and are untouched — the same
property that made `HalfWidthsAlong` safe to add.

### §3.5 The two surfaces

**(a) `LiveCitySim.Sample()` — the surface BIG actually drives.** The literal `0.0` at `:1076` becomes
the ped's interpolated path elevation, read from the manager alongside `PositionOf`. On a 2-D net every
`ShapeZ` is null, `ElevationsAlong` returns zeros, and `LiveCityPed.Z` stays exactly `0.0` — City3D,
raylib and `VizReplayBuilder` bit-identical.

**(b) `PedRemoteReconstructor` — the overload the handoff asked for.** Unchanged in signature from the
first draft:

```csharp
public bool TryGetRenderPose(int id, out Vec2 pos, out double z, out bool visible, out string animTag);
```

with the existing 4-out-param overload untouched so all 15 call sites (§1.10) compile as-is. But **the
source of z here is a genuine open decision**, because this surface reconstructs from the wire alone and
`PathArcRecord` carries 8 B/point — two `float32`, x and y only (`FrameCodec.cs:37,240`). There is no z on
the wire to interpolate. Options in §3.6.

### §3.6 The one open decision: z on the remote/wire surface

| option | what it means | cost | verdict |
|---|---|---|---|
| **W1 — extend the wire** | `PathArcRecord` 8 → 12 B/point, carrying z per path point; publisher fills it from §3.2 | +50 % on a once-per-ped-lifetime durable record (not the per-step hot path); a wire-format change touching `FrameCodec`, the DDS path and City3D consumers | **Recommended.** Consistent with §3.1 (retain, don't reconstruct), exact, and closes the pre-existing gap already noted in `DEMO-CITY3D-TRACKER.md` T1.2: *"wire `LaneGeo` is 2-D — elevation over the wire needs a future `GeometryCodec` Z-extension"* |
| **W2 — receiver-side lookup** | the remote consumer loads the ped network itself and resolves z from the retained `ShapeZ` by nearest-lane search — i.e. the superseded mechanism, scoped to this one surface | no wire change; reintroduces the per-frame search and the 27-spot ambiguity, on this surface only | Acceptable fallback if a wire change is out of scope this cycle |
| **W3 — defer** | ship (a) only; the reconstructor overload returns 0 and is documented as flat-until-W1 | none | Only if BIG's Spectacle path truly never uses the reconstructor — the handoff says it drives `Step()` + reconstruction, so **probably not** |

**This needs a decision before Stage C is scheduled.** It does not block Stage A/B, and (a) — BIG's actual
consumption path — is unaffected either way.

---

## §4 — Change 3: make the pedestrian density knobs live

Per §0/C3 and §1.11, the fix is to close the gap between `LiveCityConfig` and the `PedDemandConfig` the
ctor built:

- Keep the constructed `PedDemandConfig` in a field (`_pedDemandConfig`).
- At **one fixed point at the top of `Step()`**, before any spawn logic, mirror the two live knobs:
  `_pedDemandConfig.PopulationCap = _cfg.PedPopulationCap;`
  `_pedDemandConfig.SpawnRatePerSecond = _cfg.PedSpawnRatePerSecond;`

Only those two. The rest of `PedDemandConfig` (seeds, O/D sets, crosswalk signals) is structural and
mid-run mutation of it is not a supported operation.

Determinism: a caller that never mutates `cfg` writes back the same values every step, so the spawn
schedule is unchanged — the demo, goldens and benches are unaffected. A caller that *does* mutate gets a
change that is deterministic given the step at which it mutated, which is exactly the free-style-slider
semantics BIG asked for. Mirroring at a fixed point in `Step()` (rather than opportunistically) is what
makes that timing well-defined.

---

## §5 — Coordinate-frame contract (what we must *not* do)

**[measured] The handoff's coordinate claims are confirmed verbatim on the real files.** Both nets carry
byte-identical `<location>` georeferencing — the cut preserves the full net's absolute UTM offset exactly,
with only `convBoundary` shrinking:

```
swiss_roads.net.xml  netOffset="-388091.80,-5257586.90"  convBoundary="-118546.05,-185472.68,161214.07,22405.38"
geneve.net.xml       netOffset="-388091.80,-5257586.90"  convBoundary="-118546.05,-151908.12,36965.80,-39548.18"
both                 projParameter="+proj=utm +zone=32 +ellps=WGS84 +datum=WGS84 +units=m +no_defs"
```

Elevation is real and large: **199.48 – 1633.77 m (span 1434 m)** across Switzerland, **324.39 –
1062.24 m (span 738 m)** in the Geneva cut. This is the quantitative case for Change 2 — placing peds at
`z = 0` would put them **hundreds of metres to 1.6 km** below the terrain, exactly as the handoff warned.

The handoff asks us to keep several things true. They are true today and this design touches none of
them; recorded here so a later change cannot break them silently:

- **No offset re-normalisation, no reprojection.** Nothing in SumoSharp reads or rewrites `<location>`
  `netOffset` / `projParameter`; we consume lane shapes in the net's own SUMO-local frame and hand them
  out unchanged. BIG does SUMO-local → UTM → IG flat itself. This design adds no coordinate transform.
- **Z is passed through in the net's own datum.** `ElevationAtOffset` interpolates the committed
  `shape` z values and applies no datum shift, geoid correction, or ground clamp.
- **Consumption is the live tick.** `Step()` + `Sample()` / reconstruction, per frame. The `sim_viz.py`
  replay HTML is offline QA and is not on this path.

---

## §6 — Validation data: two tiers

The real dataset arrived after the first draft, so validation is now **two-tier**. Both tiers are
needed: the real nets are the ground truth, but they are 205 MB of third-party data and **cannot be
committed** (CLAUDE.md's committed-vs-ephemeral split; a fresh VM must pass `dotnet test` with neither
SUMO nor this dataset present).

### §6.1 Tier 1 — the real nets, opt-in via an environment variable

The dataset lives in the session scratchpad only. Tests that use it are **gated and skipped by default**:

```
SUMOSHARP_GENEVA_DIR=<dir containing common/ and geneve/>
```

Absent ⇒ those tests `Skip` with a message naming the variable. Present ⇒ they run against
`common/swiss_roads.net.xml`, `geneve/tools/geneve.net.xml`, and `geneve_Medium.sumocfg`. This is the
same discipline `dotnet test` already uses for SUMO: never a hard dependency, always available when the
inputs are. **No part of the offline gate may depend on this variable.**

Tier 1 is what proves the handoff's definition-of-done items 1–3 for real. It is also the only surface
that can answer the scale questions (§7).

### §6.2 Tier 2 — a small committed synthetic fixture

Still required, because Tier 1 is unavailable on a fresh clone and cannot guard against regressions in
CI. It follows the **`scenarios/_ped/roadnet_min` precedent exactly**: a committed *input* fixture, not a
parity golden, with its generation recipe recorded in `provenance.txt`. Its job is narrower now — a fast,
always-run regression of the same code paths Tier 1 validates at scale, plus the analytic elevation
checks that a real net cannot give (no known closed-form z).

Its parameters are chosen to **match the measured real-net properties** from §3.2/§5 so both tiers
exercise the same code: UTM32N `projParameter`, non-zero absolute `netOffset`, 3-D shapes on sidewalks
*and* crossings *and* walkingareas (so every retained-`ShapeZ` path is covered), elevations in a
Swiss-like band.

New: `scenarios/_ped/roadnet_geo3d/`

| File | Role |
|---|---|
| `geo3d.nod.xml`, `geo3d.edg.xml` | committed plain-XML inputs — nodes in **lon/lat** with `z` in a Swiss-like 380–450 m band |
| `net.xml` | the netconvert output (committed; what the tests load) |
| `scenario.net.xml` | byte-identical copy under the `preprocess.py` name, to exercise the §2.2 probe |
| `relative.sumocfg` | `<net-file>` / `<route-files>` as **relative** paths (demo-city style) |
| `absolute.sumocfg` | the same as **absolute** paths (`preprocess.py` style) — generated by a test at runtime into a temp dir, since an absolute path cannot be committed |
| `provenance.txt`, `README.md` | recipe, generator version, and an explicit "INPUT ONLY — not a golden" |

Recipe (dev-side, never run by `dotnet test`):

```
netconvert -n geo3d.nod.xml -e geo3d.edg.xml \
  --proj.plain-geo --proj "+proj=utm +zone=32 +ellps=WGS84 +datum=WGS84 +units=m +no_defs" \
  --sidewalks.guess --crossings.guess --walkingareas.all-nonspecific \
  -o net.xml
```

No `--offset.*` flag, so netconvert's default normalisation produces exactly the cut-net shape the
handoff describes: a non-zero absolute `netOffset` with a UTM `projParameter`. The fixture must satisfy,
as asserted properties: a `projParameter` containing `+proj=utm +zone=32`; a non-zero `netOffset`;
3-coordinate lane shapes; ≥1 sidewalk, ≥1 crossing, ≥1 walkingarea.

**Generator version.** Use the **pinned** 1.20.0 build: `pip install eclipse-sumo==1.20.0` puts
`netconvert` 1.20.0 at `/usr/local/bin`, which shadows the apt package (1.18.0) on `PATH`. Verify with
`netconvert --version` before generating, and record the result in `provenance.txt`. (For an input-only
fixture a version skew would not actually affect parity — `roadnet_min`'s `provenance.txt` makes that
argument, since nothing here is compared against a SUMO trajectory — but the pin is available, so there
is no reason to accept the skew.)

---

## §7 — Scale: measured, and a warning for BIG

The real nets parse **today**, with existing APIs and no feature code — but the cost is substantial and
BIG needs to know it before wiring a UI. All figures below are **[measured]** in this session (Release,
this VM):

| pass | Geneva cut (44 MB) | Switzerland (161 MB) |
|---|---|---|
| 1. `NetworkParser.Parse` | 9.2 s | **67.7 s** |
| 2. `PedNetworkParser.Load` | 1.2 s | 6.5 s |
| 3+4. `CrosswalkSignalSchedule.FromNet` | 1.3 s | *(~5 s, scaled)* |
| **total ctor net I/O** | **≈ 11.6 s** | **≈ 80 s** |
| lanes / edges | 53 229 / 41 933 | 175 465 / 141 571 |
| peak working set | 572 MB | **1 652 MB** |

Findings that matter:

- **The `LiveCitySim` ctor makes FOUR full passes over the net file**, not two as the first draft assumed:
  `NetworkParser.Parse`, `PedNetworkParser.Load`, and `CrosswalkSignalSchedule.FromNet` — which is itself
  two passes (`CrossingTlReader.LoadPrograms` + `LoadCrossingLinks`,
  `CrosswalkSignalSchedule.cs:48-49`). All four are **pre-existing** behaviour that this change neither
  introduces nor worsens.
- **Pass 1 dominates at ~85 % of the total.** Collapsing passes 2–4 would buy little; a single-pass
  refactor of `NetworkParser` would be the only meaningful win, and that touches the parity-critical
  ingest path. **Explicitly out of scope here** — it is a separate, gated piece of work, not a rider on
  an additive change.
- **Recommendation for BIG:** load a **cut box**, not full Switzerland. 80 s and 1.65 GB is tolerable as a
  one-off startup cost but not as anything a user waits on repeatedly; the Geneva cut at ~12 s is
  workable, and a smaller `preprocess.py` box will be faster still. If full Switzerland is genuinely
  needed, it wants a load-time progress indicator and a warm cache, both BIG-side concerns.

What still **cannot** be verified here: nothing in the handoff's definition-of-done. Items 1–3 are all
reachable with the Tier-1 data (§6.1). The remaining honest gap is only that a `preprocess.py`-produced
cut and an *absolute*-path `.sumocfg` are not among the received files — the real configs use relative
paths — so the absolute branch stays covered by a synthesised temp-dir config (§2.3, B2·SC2).

---

## §8 — Risks

- **R1 — CLOSED by measurement, and now moot.** "netconvert may emit 2-D crossing/walkingarea shapes"
  does not happen: 100 % of ped lanes in both real nets carry z (§3.2 table). Under the retain design this
  is not a fallback question at all — it is the precondition for §3.2, and it holds.
- **R2 — DISSOLVED by the redesign.** There is no spatial index. `ShapeZ` is a `double[]` per ped lane,
  index-aligned with a shape already in memory: ~8 B per vertex, 103 k vertices in Geneva / 888 k in
  Switzerland ⇒ under 1 MB / ~7 MB, against the 572 MB / 1.65 GB the parsed net already costs (§7).
- **R3 — DISSOLVED by the redesign.** The per-ped, per-frame spatial query is gone. z is one lerp between
  two path elevations at a cursor the ped already maintains for steering (§3.4). The cell-size and
  ring-expansion sub-risks no longer exist, and the "cache z and refresh every N frames" mitigation is
  unnecessary. What remains is ordinary interpolation cost, still worth confirming at a high ped count
  (C4) but no longer a design risk.
- **R8 — DISSOLVED by the redesign.** Vertical ambiguity was an artefact of resolving z from a horizontal
  position. A ped now takes z from the path it is actually walking, so a footbridge ped gets the bridge
  deck by construction. (The measurement stands as a fact about the data — 27 stacked spots nationwide,
  0.004 % — and now serves only to bound the error of option **W2** in §3.6, should that fallback be
  chosen for the wire surface.)
- **R9 — OPEN, and the one real decision left.** z on the remote/wire surface: `PathArcRecord` carries
  x,y only, so §3.6's W1/W2/W3 must be chosen. W1 (extend the wire to 12 B/point) is recommended and also
  closes a gap already logged in `DEMO-CITY3D-TRACKER.md` T1.2, but it is a wire-format change and needs
  explicit sign-off. Blocks Stage C scheduling; blocks neither Stage A/B nor BIG's own `Sample()` path.
- **R6 — OPEN (informational, not ours to fix).** Full-Switzerland load is ~80 s / 1.65 GB across four
  pre-existing net passes (§7). Not caused by this change and explicitly out of scope, but BIG must be
  told before it builds a UI around it.
- **R7.** The Tier-1 (real-data) tests must be **skip-by-default**. If `SUMOSHARP_GENEVA_DIR` ever
  becomes a hard dependency, `dotnet test` breaks on every fresh VM. Assert the skip path explicitly.
- **R4.** `tests/Sim.LiveCity.Tests` is **not in `Traffic.sln`** (CLAUDE.md measurement-discipline #9),
  so `dotnet test Traffic.sln` will not build it. Every task touching it must build that csproj
  explicitly or it measures stale code.
- **R5.** Mid-run mutation of the ped knobs (§4) changes the spawn schedule by design. Any *test* that
  mutates them must not also assert a pinned trajectory.
