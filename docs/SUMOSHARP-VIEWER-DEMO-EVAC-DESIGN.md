# Native viewer → interactive demo tool (pure-SUMO scenarios + live panic-evacuation) — design

**HOW it works.** The WHAT: turn the native viewer's `local` mode into a **fast, self-contained demo
tool** with an in-window (Dear ImGui) **scenario picker** that switches between curated demos live —
covering **both** the pure-SUMO behaviours (junctions, traffic lights, lane-changing, rail, city-scale,
sandbox nets) **and**, for the first time *live*, the **panic-evacuation** feature (incident → panic →
jam → abandon car → foot exodus). Today evac is visible only as an offline `Sim.Viz` HTML replay; the
live viewers (`Sim.Viewer`, `Sim.LiveHost`) don't reference `Sim.Evac` at all.

Requirements/spec live in the reader's head + the two feature designs this composes:
`docs/SUMOSHARP-NATIVE-VIEWER.md` (the viewer) and `docs/PANIC-EVAC-OVERVIEW.md` (the evac layer). This
doc is the mechanism; the task breakdown + success conditions are in
`docs/SUMOSHARP-VIEWER-DEMO-EVAC-TASKS.md`, tracked by `…-TRACKER.md`.

## Design tenets

1. **Pure-SUMO demos are first-class; evac is one category among many.** The picker's default content is
   the committed parity scenarios and benches. Evac is an additional category, not the point.
2. **`local` mode only.** Authoritative `Snapshot` every frame — no DR, no jitter, no transport. (Remote
   evac would need a new DDS crowd/incident topic; explicitly out of scope here, noted in *Future*.)
3. **Parity gate is untouchable.** Everything lives in the two out-of-`Traffic.sln` viewer projects and
   `Sim.Evac` (also out of the solution), *except* one small additive seam on `SimulationRunner` (§2).
   That seam is nullable and inert-when-unset: the determinism hash `909605E965BFFE59` and the 446-test
   suite MUST be unchanged. This is the load-bearing invariant every task re-verifies.
4. **Reuse, don't fork.** The live renderer draws the *exact* director read-surface the offline
   `Sim.Viz.SceneGen.BuildEvacGrid/Organic` already consumes (peds, abandoned cars, pushers, fear,
   incident, boundary) — just via raylib each frame instead of baked into HTML. No new evac logic.

## Component map

```
Sim.Core (in Traffic.sln)         SimulationRunner.OnAfterStep  ← the ONE additive seam (§2)
Sim.Evac (out of solution)        EvacDirector + Evac*Scenario.Build  ← reused verbatim
Sim.Viewer.Core (out of solution) EngineHost (evac path)
Sim.Viewer (out of solution)      Program (picker plumbing), Renderer (evac draw pass + Demos panel),
                                   EvacRenderSnapshot, DemoCatalog
```

`EvacRenderSnapshot` and `DemoCatalog` are shown here in `Sim.Viewer`, not `Sim.Viewer.Core` as
originally proposed below (§1, §3): `docs/SUMOSHARP-PACKAGING-DESIGN.md` §3 (D5) later relocated both
into the demo layer (confirmed: `src/Sim.Viewer/DemoCatalog.cs`, `src/Sim.Viewer/EvacRenderSnapshot.cs`)
so the packaged, reusable viewer (`Sim.Viewer.Core`) stays generic and does not carry the demo-only
`DemoCatalog`/evac wiring. `SimulationRunner.OnAfterStep` is unaffected by that move and is still
correct as described below.

## §1 — Demo catalog (`Sim.Viewer.Core/DemoCatalog.cs`, new)

A static, data-driven list of `DemoEntry` — the single place to add/curate demos:

```csharp
public enum DemoKind { Scenario, Sandbox, Evac }
public enum DemoCategory { Junctions, TrafficLights, LaneChange, Rail, CityScale, Sandbox, Evacuation }

public sealed record DemoEntry(
    string Name,             // "Right-before-left (4 left turns)"
    string Blurb,            // one line shown under the picker
    DemoCategory Category,
    DemoKind Kind,
    string PathRelToRepo,    // scenario dir, or a .net.xml for Sandbox; "" for Evac (built by EvacKind)
    string? EvacKind = null, // "grid-tls" | "organic" | "city"  (only when Kind == Evac)
    int SandboxFleet = 0);   // only when Kind == Sandbox (>0 raises the spawn cap)
```

Curated starter set (Sonnet fills the exact list; every path verified to exist at build of the catalog):
- **Junctions:** `11-priority-junction`, `26-right-before-left`, `_diag/rbl-left-turns` (shows the
  deadlock fix), `27-allway-stop`, `32-roundabout`, `44-multilane-junction-turn`.
- **Traffic lights:** `09-traffic-light`, `35-actuated-tls`.
- **Lane change / overtaking:** `12-overtake`, `43-continuous-lanechange`, `57-overtake-opposite`.
- **Emergency / give-way:** `16-emergency-red`, `55-giveway-drift`.
- **Rail:** `47-rail-free-flow`, `49-rail-bidi-meet`, `51-rail-crossing`.
- **City scale:** `_bench/city-organic-L2`, `_bench/city-mixed-1k`; `_bench/city-15000` as a **Sandbox**
  entry (fleet ~4000) for the big-net demo.
- **Sandbox:** `samples/junctions/cross`, `…/bend`, `…/acute`.
- **Evacuation:** `grid-tls` (fast, legible), `organic` (realistic town), `city` (10k-class, local
  exodus). These build via `EvacTlsScenario`/`EvacOrganicScenario`/`EvacCityScenario`.

Repo root is resolved the way the parity tests do (walk up to `Traffic.sln`); an entry whose path is
missing is skipped with a logged warning, so a trimmed checkout still launches.

## §2 — The one in-solution seam: `SimulationRunner.OnAfterStep`

`EvacDirector.Tick()` must run on the engine thread, once per engine step, *between* `Engine.Step()` and
snapshot publish (it reads the just-advanced state and drives panic via public engine seams for the next
step). `SimulationRunner.Tick()` is `DrainCommands(); _engine.Step(); publish;`. Add:

```csharp
// Optional post-step hook, invoked on the engine thread immediately after Engine.Step() and BEFORE the
// snapshot is captured. Null by default and never set on any parity/bench path, so it is inert there
// (determinism hash 909605E965BFFE59 unchanged). Used only by the native viewer's evac demo mode to
// tick the external Sim.Evac layer in lockstep with the core.
public Action<Engine>? OnAfterStep { get; set; }
```

Invoked as `_engine.Step(); OnAfterStep?.Invoke(_engine);` in `Tick()`. **This is the only edit inside
`Traffic.sln`.** Its inertness is a task success condition (hash + full suite re-run).

Rejected alternative: a private per-step loop inside `EngineHost` that bypasses `SimulationRunner` — it
would have to re-implement the snapshot pool, `SnapshotPair`, pause, speed and interpolation the renderer
depends on. The nullable hook is far smaller and is exactly the "drive the core through a public seam"
posture the evac layer already lives by.

## §3 — `EvacRenderSnapshot` (`Sim.Viewer.Core/EvacRenderSnapshot.cs`, new)

The renderer runs on the UI thread; the director is mutated on the engine thread. Rather than lock around
per-frame director reads, **capture an immutable render snapshot on the engine thread** (inside
`OnAfterStep`, where the director is quiescent) and publish it atomically — the same discipline
`SimulationSnapshot` already uses.

```csharp
public sealed class EvacRenderSnapshot            // immutable; one per tick
{
    public double Time;
    public (double X, double Y, bool Escaped)[] Peds;         // director.PedestrianPosition/Escaped
    public (double X, double Y, double R)[] AbandonedCars;    // director.AbandonedCar
    public (double X, double Y, double HeadingRad)[] Pushers; // director.ActivePushers
    public Dictionary<uint, double> FearByVehicle;            // director.Fear(handle) keyed by handle.Index
    public (double X, double Y, double Radius, double StartTime, double SafeRadius) Incident;
    public (double MinX, double MinY, double MaxX, double MaxY) Boundary; // director.NavMesh
    public int Panicked, Converted, Escaped, Abandoned;       // HUD counters
}
```

`FearByVehicle` is keyed by vehicle-handle index so the renderer can look up fear for the vehicle it is
already drawing from the `SimulationSnapshot` (both snapshots are captured in the same `Tick`, so they are
consistent). Capture cost is a few thousand element copies per tick — negligible vs the step itself.

## §4 — `EngineHost` evac path (extend `Sim.Viewer.Core/EngineHost.cs`)

`EngineHost` currently builds its own `Engine` (scenario or sandbox) in `BuildSim()`. Add an **optional
evac provider** so the same host also drives an evac scenario, keeping all its runner/pool/pause/speed
plumbing:

- New ctor arg / factory: `EngineHost.CreateEvac(string evacKind, string repoRoot, Incident? incident)`.
  It stores a delegate `Func<Incident?, (Engine, EvacDirector)>` bound to the right `Evac*Scenario.Build`.
- In `BuildSim()`, when the evac provider is set:
  1. `(engine, director) = _evacProvider(_incident);` instead of `LoadScenario`/`LoadNetwork`.
  2. `_randomTraffic = false` (evac scenarios carry their own demand; no sandbox spawner).
  3. After `runner.Start(...)`, set `runner.OnAfterStep = e => { director.Tick(); _evacSnap = CaptureEvac(director); };`
  4. `Network` still comes from parsing the evac net (needed for road geometry); expose bounds from it.
- New read: `public EvacRenderSnapshot? Evac => _evacSnap;` (volatile field; null for non-evac demos).
- **Interactive incident:** `public void SetIncidentAtWorld(double wx, double wy)` sets
  `_incident = new Incident(wx, wy, StartTime: currentSimTime, Radius: kindDefaultRadius)` and calls
  `BuildSim()` (a restart re-seeded with the clicked incident). For evac demos the left-click handler is
  repurposed from "drop obstacle" to "place incident" (obstacle-drop stays for non-evac demos).

`Restart()`, `SetSpeed`, `SetPaused`, `SnapshotPair`, `Generation` are unchanged and work for both paths.

## §5 — Live demo switching (`Sim.Viewer/Program.cs` + a small session holder)

The viewer currently constructs one `EngineHost` at startup and holds it for the process. Introduce a
tiny **session holder** the render loop reads through a reference that can be swapped:

- On picker selection, build the new `EngineHost` for the `DemoEntry` (scenario/sandbox/evac path),
  then dispose the old one. Swap under a lock the render loop also takes when reading `host`.
- Camera re-fits to the new net bounds on switch (reuse the existing fit-to-bounds used at startup).
- `--demo "<name>"` CLI selects the initial demo by name (default: the first Junctions entry). `--mode
  local <path>` keeps working exactly as today (ad-hoc path, no catalog entry) — the picker then shows
  "(custom)" as the active item.

Switching disposes the old host (stops its runner/timer) before creating the new one, so only one sim
runs at a time.

## §6 — Renderer: evac draw pass + Demos panel (`Sim.Viewer/Renderer.cs`)

**World draw (only when `host.Evac` is non-null), layered under/over the existing vehicle draw:**
1. **Known-world boundary** — dashed rectangle from `Evac.Boundary` (the hard edge).
2. **Incident zone** — filled translucent amber disc at `Incident.(X,Y,Radius)` once `Time >=
   StartTime`; a thin ring at `SafeRadius`. Before `StartTime`, a faint outline (so the user sees where
   it will fire).
3. **Vehicles** — the existing oriented boxes, but **tinted by fear**: blend the normal speed colour
   toward red by `fear = Evac.FearByVehicle[handle]` (0 → unchanged, 1 → full alarm red). Non-evac demos
   keep pure speed colouring.
4. **Abandoned cars** — dark-red discs (`Evac.AbandonedCars`).
5. **Pedestrians** — small discs, cyan when fleeing → green when `Escaped` (`Evac.Peds`).
6. **Pushers** — orange oriented rectangles (`Evac.Pushers`, heading in radians).

Colours/kinds mirror `SceneGen` (`KindFleeing`/`KindEscaped`/`KindAbandoned`/`KindPushingCar`) so the live
view and the HTML replay read identically.

**ImGui "Demos" panel** (always present):
- Collapsing headers per `DemoCategory`; each entry is a selectable row (`Name` + `Blurb` tooltip).
  Clicking switches the active demo (§5). The current demo is highlighted.
- A short **legend** shown only for evac demos (amber = incident, cyan = fleeing, green = escaped,
  dark red = abandoned, orange = shoulder-push) + live counters from `Evac` (panicked / converted /
  escaped / abandoned) and, for evac demos, a hint: "click a road to (re)place the incident".
- The existing controls panel (restart / clear-obstacles / speed / pause / DR-delay-not-applicable-in-
  local / diagnostics) stays; obstacle-drop + random-traffic toggle are hidden for evac demos (they carry
  their own demand), incident-place replaces obstacle-drop there.

## Threading, determinism, parity (the invariants)

- `OnAfterStep` and `CaptureEvac` run on the engine thread; the renderer reads `host.Evac` / `host.Snapshot`
  as atomically-published immutable objects — no director access off-thread, no locks in the draw loop.
- Evac uses `Sim.Evac`'s own deterministic seeding (unchanged); a given demo + clicked incident replays
  identically. No `System.Random` added to the core.
- **Parity:** the sole `Traffic.sln` change is the inert `OnAfterStep` hook. `dotnet test Traffic.sln` →
  446 passed / 3 skipped / 0 failed and `Sim.Bench` hash `909605E965BFFE59` (single + parallel) MUST hold
  after it lands — a task gate, not an afterthought.

## Headless verification (this Linux VM, software GL under Xvfb)

Every phase is checkable here without a GPU or a human, exactly as the viewer was built:
```
LIBGL_ALWAYS_SOFTWARE=1 GALLIUM_DRIVER=llvmpipe xvfb-run -a -s "-screen 0 1280x800x24" \
  dotnet run -c Release --project src/Sim.Viewer -- --mode local --demo "<name>" \
    --screenshot out.png --frames <N>
```
A `--demo` screenshot of a pure-SUMO scenario proves the picker path; an evac `--demo "Evacuation …"`
screenshot at a frame past the incident start proves the evac draw pass (amber zone + peds + abandoned
cars visible). The Dr/DDS paths are untouched.

## Future (explicitly out of scope here)

- **Remote/loopback evac** — needs a new low-rate DDS crowd + incident topic (mirrors the TL topic); the
  `EvacRenderSnapshot` is already the natural payload. Noted for a later track.
- **Scripted demo tour** — auto-advance through the catalog on a timer for an unattended kiosk loop.
