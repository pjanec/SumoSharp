# Unified HTML-replay generator — design + full bootstrap (for a dedicated session)

**This document is a self-contained work order.** A fresh session should be able to execute it end-to-end
with no other context. It designs, tasks, and tracks the unification of SumoSharp's several HTML-replay
producers into **one reusable generator** that (a) renders **real deterministic engine output** (NOT
committed golden FCD), (b) supports **vehicles AND pedestrians** as first-class, (c) applies the
**dead-reckoning (DR) motion smoothing** automatically, so future DR/reconstruction improvements
**project to the whole GitHub-Pages demo gallery for free**.

Owner decisions already made (do not re-litigate): **render real engine output, not goldens** (a
deterministic re-run, not the committed `golden.fcd.xml`); **one design, executed in full** by this session;
peds are as important as vehicles (SumoSharp adds a large ped/live-city layer SUMO lacks — the generator
must serve both).

---

## 0. Bootstrap — what you need to know first

- **Repo:** SumoSharp — SUMO's microscopic traffic sim reimplemented in C#/.NET 8, plus a from-scratch
  pedestrian/live-city layer. Read `CLAUDE.md` (operating rules) and `docs/DESIGN.md` first.
- **The iron law (parity):** `dotnet test tests/Sim.ParityTests -c Release` must stay **657 passed / 4
  skipped**, byte-identical; `dotnet run --project src/Sim.Bench -c Release` must stay
  `deterministic=True`, `parallel==single`, hash **`D96213B7BB4021A7`**. **This work is a VIEWER/TOOL change
  — never edit `Sim.Core`; never touch goldens.** It is inherently parity-safe if you stay out of the engine.
- **Determinism:** no `System.Random`; a deterministic engine re-run must be byte-identical run-to-run
  (that is the whole reason "real engine output" is safe to render — same input ⇒ same trajectory).
- **The reconstruction recipe is already written down:** `docs/IGBRIDGE-HTML-REPLAY-GUIDE.md` — READ IT.
  It is the exact, gotcha-annotated recipe for turning engine poses into smooth HTML (vehicles §1–§5, peds
  §5b). This design just makes that recipe the *single* path every demo flows through.
- **A working reference implementation already exists** on branch **`claude/livecity-realism-fixes`**:
  `src/Sim.Viz/SceneGen.cs → BuildLiveCityDemo` + `Program.RunLiveCityDemo` (mode `--live-city-demo`). It
  drives the real `LiveCitySim`, reconstructs cars (`DrClock.ResolveAt` + `KinematicReconstructor`) and peds
  (`PedRemoteReconstructor`), and emits the `useDataHeading` 5-tuple scene. **Start by studying (or
  cherry-picking) it** — it is exactly the loop this design generalizes. (That branch also adds
  `ScenePayload.UseDataHeading`; if you start clean from `main`, re-add that one field — see §4.)

---

## 1. The problem — today's fragmented producers

All producers share **one player** (`src/Sim.Viz/template.{html,js}`, injected via a marker-replace) — that
part is already unified and must stay one file. What is NOT unified is **frame production**: several
independent producers, and only two apply DR smoothing.

| Producer | Feeds it | Source | DR smoothing? |
|---|---|---|---|
| `Sim.Viz RunSingle`/`BuildFcdScene` (**the GitHub-Pages gallery vehicle demos**) | `scripts/gen-demos.sh` | committed `golden.fcd.xml` / `engine.fcd.xml` | ❌ raw 3-tuple (steppy) |
| `Sim.Viz` SceneGen `--ped-*` / `--evac-*` / `--live-city` builders | gen-demos | programmatic scenes | ❌ mostly raw |
| `Sim.Viz BuildLiveCityDemo` (`--live-city-demo`, the reference impl) | manual | live `LiveCitySim` + wire | ✅ full DR (cars+peds) |
| `Sim.IgBridge.Host VizExport` (side-by-side raw-vs-smooth) | manual | `IgBridgeSession`+`FakeIg` | ✅ full DR |

Consequences you are fixing:
1. The **public gallery is steppy** — junction facet-snaps, instant lane changes — because it renders raw
   FCD. DR improvements never reach it.
2. **HTML injection is duplicated** (`Sim.Viz.WriteHtml` at `Program.cs:2313` and `VizExport`'s own
   marker-replace) — two copies of the same glue.
3. Every new feature/demo tends to grow **yet another bespoke producer** (owner's exact pain point).

---

## 2. The goal

One reusable component every demo flows through, so:
- **Vehicles + peds** are both first-class.
- **DR smoothing is automatic** — a demo author supplies a scenario, not a rendering pipeline.
- **DR improvements project everywhere** — change `KinematicReconstructor` once, every gallery page improves.
- The gallery renders **real deterministic engine output** (re-run the engine on the scenario), not the
  committed golden FCD.

---

## 3. Design

### 3.1 The reusable core — `VizReplayBuilder`
A new reusable builder (put it in `src/Sim.Viz/`, e.g. `VizReplayBuilder.cs`; it may depend on
`Sim.Viewer.Motion`, `Sim.Pedestrians.Lod`, `Sim.Replication`, `Sim.LiveCity` — Sim.Viz already references
these on the realism branch, add refs as needed). It runs the DR loop from the guide, generalized:

```
ScenePayload Build(IVizReplaySource source, VizReplayOptions opts)
  // opts: RenderHz (default 10), PlayoutDelaySeconds (default = source step dt), Steps, View bbox override
  drClock = new DrClock(); recon = new KinematicReconstructor { CoarseFeed = true }
  pedRecon = source.PedSource is not null ? new PedRemoteReconstructor(source.PedSource) : null
  for each sim step in source:
      source.Step(); source.VehicleSource.Pump(); pedRecon?  (pumped per render frame below)
      for each render instant tau (advance at RenderHz, tau <= simTime - delay):
          // vehicles: DrClock.ResolveAt(history, tau, lanes) -> recon.Resolve -> CENTER + HeadingDeg
          //           emit 5-tuple [x, y, headingDeg, length, width]   (guide §2/§5, useDataHeading)
          // peds:     pedRecon.Pump(tau + pedDelay); TryGetRenderPose -> disc [x, y, r, kind]  (guide §5b)
  NormalizeVehicleSlots(...); AssignStableDiscSlots(...)
  return new ScenePayload(name, desc, view, network, vdim, renderDt, frames, UseDataHeading: true)
```

This is **verbatim the working `BuildLiveCityDemo` loop**, lifted behind an interface so any source can use
it. Copy that loop; do not re-derive it (it already handles the guide's gotchas: `useDataHeading`, center
not front, upcoming-lane look-ahead, ped wire not `PositionOf`).

### 3.2 The source abstraction — `IVizReplaySource`
```
interface IVizReplaySource {
    void Step();                              // advance one sim step (deterministic; = cfg.Dt / step-length)
    double Dt { get; }                        // sim step seconds
    IReplicationSource VehicleSource { get; } // the vehicle wire (history carries UpcomingLanes -> look-ahead)
    IPedReplicationSource? PedSource { get; } // the ped wire, or null (vehicle-only scenario)
    ILaneShapeSource Lanes { get; }           // z-aware lane geometry (LiveCitySim.LocalLanes = NetworkLaneSource)
    NetworkPayload Network { get; }           // road/junction/crossing polylines for the background
    (double x0,double y0,double x1,double y1) View { get; } // crop bbox (whole net, or a hero crop)
}
```

### 3.3 Two adapters (this design ships both)
1. **`EngineScenarioSource`** — the general one, for the **gallery vehicle demos**. Given a scenario dir
   (`net.xml` + `*.rou.xml` + `*.sumocfg`), it builds a plain `Engine`, loads the net + demand, and each
   `Step()` publishes the engine snapshot onto an `InMemoryReplicationBus` — **exactly the
   `Engine → ReplicationPublisher → InMemoryReplicationBus → VehicleSource` wiring `LiveCitySim` already
   uses** (see `src/Sim.LiveCity/LiveCitySim.cs`: `_vehPublisher.PublishGeometryOnce(...)` +
   `_vehPublisher.PublishStep(snap, _vehBus.Sink)`; `VehicleSource = _vehBus.Source`). No peds
   (`PedSource = null`). Step count / dt come from the scenario `*.sumocfg` exactly as `src/Sim.Run` already
   computes them (`(End-Begin)/StepLength`). **This renders the real deterministic engine trajectory** — the
   same bytes the golden encodes, but reconstructed (smooth) for display instead of read raw.
2. **`LiveCitySource`** — wraps `LiveCitySim` (cars + peds + crossing-yield + high-realism zone). This is
   what `BuildLiveCityDemo` already is; refactor it to implement `IVizReplaySource` and delete the bespoke
   loop (now in `VizReplayBuilder`).

> Peds in the gallery: most gallery demos are vehicle-only, so `PedSource = null` is the common case.
> The `--ped-*` / `--evac-*` scenes and the live-city already have peds; route them through `LiveCitySource`
> (or a small `PedSceneSource`) so they too flow through the one builder. Where a scene has no ped
> **wire** (older `SceneGen` ped builders sample `PositionOf` directly), either (a) publish those peds onto a
> `PedReplicationPublisher` so they get the same wire reconstruction, or (b) keep their existing continuous
> `PositionOf` disc emit as a documented exception — pick per the guide's ped section; wire reconstruction is
> preferred so promotion/demotion and smoothing match the viewers.

### 3.4 One shared HTML writer
Extract the marker-injection (`template.html` + `template.js` replace) into ONE public helper — e.g.
`VizHtml.Write(ReplayData payload, string outPath)` — and route BOTH `Sim.Viz` and
`Sim.IgBridge.Host/VizExport` through it (delete the duplicate in `VizExport`). Keep the CamelCase
`JsonSerializerOptions` (so `UseDataHeading → useDataHeading`). One player, one writer.

### 3.5 Wire it into the gallery
`scripts/gen-demos.sh` currently does `Sim.Run` (make FCD) → `Sim.Viz <dir> --fcd` (render raw FCD). Change
the vehicle-demo bodies to a single new mode, e.g. `Sim.Viz --engine-replay <scenarioDir> <out.html>`, which
builds an `EngineScenarioSource` for that dir and runs `VizReplayBuilder`. **Retire the raw
`BuildFcdScene`/`--fcd` render path** for the gallery (keep `FcdParser` only if some non-engine input still
needs it; the gallery no longer does). `Sim.Run`'s FCD generation is no longer needed for rendering (may
stay for other consumers). Result: every gallery car demo is DR-smooth and shows the real engine run.

---

## 4. `ScenePayload.UseDataHeading` (needed on `main`)
The reference impl added this on the realism branch; if you branch clean from `main`, re-add it:
`internal sealed record ScenePayload(..., bool UseDataHeading = false)` — serialized CamelCase to
`scene.useDataHeading`, which `template.js` already honors (the player branch is on `main` — `VizExport`
relies on it). Emit vehicles as a **5-tuple** `[x, y, headingDeg, length, width]` so the player draws the
emitted body heading + true dims (guide §5.1/§5.4). (Cheapest path: cherry-pick the realism-branch commit
that adds the field + `BuildLiveCityDemo`.)

---

## 5. Tasks (each an independent, verifiable landing)

- **T1 — Shared HTML writer.** Extract `VizHtml.Write(ReplayData, outPath)`; route `Sim.Viz.WriteHtml` and
  `VizExport` through it. **Success:** the whole gallery + the IgBridge side-by-side still render
  byte-comparably (diff a before/after HTML for one scene: only intended changes); no duplicate injection
  code remains.
- **T2 — `VizReplayBuilder` + `IVizReplaySource` + `LiveCitySource`.** Lift `BuildLiveCityDemo`'s loop into
  the builder; make `LiveCitySource` implement the interface; `--live-city-demo` now calls the builder.
  **Success:** `--live-city-demo` output is byte-identical (or visually identical) to the reference impl;
  `useDataHeading` present; cars smooth (junction arcs), peds smooth (no caterpillar).
- **T3 — `EngineScenarioSource` + `--engine-replay <dir> <out>`.** Real deterministic engine run on any
  scenario dir → the builder. **Success:** running it on ≥3 gallery scenarios (a junction, a lane-change,
  a TL scenario) produces DR-smooth HTML; **two runs are byte-identical** (determinism); the rendered
  trajectory matches the committed golden's behaviour (spot-check a few vehicle positions vs `golden.fcd.xml`
  at the same sim time — same trajectory, just resampled/smoothed).
- **T4 — Switch `scripts/gen-demos.sh` gallery bodies to `--engine-replay`.** Retire the `--fcd` raw render
  for vehicle demos. **Success:** `scripts/gen-demos.sh` produces the full gallery; every vehicle page has
  `useDataHeading`/5-tuples; `.github/workflows/demos.yml` still deploys Pages green; a manual open of 2–3
  pages shows smooth motion.
- **T5 — Route ped/evac scenes through the builder** (via `LiveCitySource`/a `PedSceneSource`, or document
  the `PositionOf` exception). **Success:** `--ped-*`/`--evac-*` gallery pages render via the one builder;
  peds smooth; LOD colours (grey/orange/yellow) correct.
- **T6 — Fold `VizExport` (IgBridge side-by-side) onto the builder** (its "raw" scene stays raw by design —
  it's the comparison; its "smooth" scene comes from the builder). **Success:** `Sim.IgBridge.Host` still
  emits `sidebyside.html`; the smooth side now comes from `VizReplayBuilder` (one code path).
- **T7 — Prove the "projects automatically" property + docs.** Make a trivial, reversible tweak to
  `KinematicReconstructor` (e.g. a look-ahead constant), regenerate the gallery, and show the diff touches
  every vehicle page. Revert the tweak. Update `IGBRIDGE-HTML-REPLAY-GUIDE.md` (§6 producer map now = one
  builder) and add a short `VIZ-UNIFICATION-STATUS.md`. **Success:** the diff demonstrates single-source
  smoothing; docs updated.

### Gates on EVERY task
`tests/Sim.ParityTests` 657/4 byte-identical; `Sim.Bench` hash `D96213B7BB4021A7` (both trivially hold — no
`Sim.Core` edits); `dotnet build -c Release` green; `dotnet test` (full solution) green;
`scripts/gen-demos.sh` runs clean; two gen-demos runs byte-identical (determinism).

---

## 6. Tracker
> Executed — see `docs/VIZ-UNIFICATION-STATUS.md` for verification and the three documented deviations.
- [x] T1 shared HTML writer (dedupe `WriteHtml` / `VizExport`)
- [x] T2 `VizReplayBuilder` + `IVizReplaySource` + `LiveCitySource` (fold `BuildLiveCityDemo`)
- [x] T3 `EngineScenarioSource` + `--engine-replay` (real engine output, deterministic)
- [x] T4 gallery (`gen-demos.sh`) → `--engine-replay`; retire raw `--fcd` render; Pages green
      (3 large city-scale demos kept on FCD — size; documented in STATUS)
- [◑] T5 ped/evac scenes → the builder (flagship `live-city` routed; pure-ped demos documented exception)
- [⨂] T6 fold IgBridge `VizExport` smooth side onto the builder (writer unified in T1; scene deliberately
      NOT folded — it would break the v5/v6 diagnostic; see STATUS §T6)
- [x] T7 prove "DR-improvement projects to all demos" + docs

---

## 7. Determinism / parity argument (why this is safe)
This is entirely a **viewer/tool**: `Sim.Viz`, `Sim.IgBridge.Host`, `scripts/`, docs. It **never edits
`Sim.Core`** and **never touches goldens or `tolerance.json`**, so `tests/Sim.ParityTests` and the
`Sim.Bench` hash are untouched by construction. Rendering "real engine output" is safe because the engine is
**deterministic** (no `System.Random`; per-entity seeded RNG): a re-run of a scenario reproduces the golden
trajectory bit-for-bit, so the gallery still shows the certified behaviour — just DR-reconstructed for
display. Verify determinism directly (T3/T4): two runs → byte-identical HTML.

## 8. Reference files (the map)
- **Recipe:** `docs/IGBRIDGE-HTML-REPLAY-GUIDE.md` (cars §1–§5, peds §5b). **Read first.**
- **Working reference impl:** `src/Sim.Viz/SceneGen.cs → BuildLiveCityDemo` + `Program.RunLiveCityDemo`
  (branch `claude/livecity-realism-fixes`) — the loop to lift.
- **Player (one file, do not fork):** `src/Sim.Viz/template.{html,js}` (`useDataHeading` branch,
  `interpolatedVehicles` Catmull-Rom, `interpolatedDiscs`).
- **HTML writer to unify:** `src/Sim.Viz/Program.cs` `WriteHtml` (~2313) + `src/Sim.IgBridge.Host/VizExport.cs`.
- **DR math (reuse, don't reinvent):** `src/Sim.Viewer.Motion/{KinematicReconstructor,DrClock}.cs`;
  peds `src/Sim.Pedestrians/Lod/PedRemoteReconstructor.cs`.
- **Engine→wire wiring to copy:** `src/Sim.LiveCity/LiveCitySim.cs` (`ReplicationPublisher` +
  `InMemoryReplicationBus` + `PublishStep`/`PublishGeometryOnce`; `VehicleSource`/`PedSource`/`LocalLanes`).
- **Engine-on-scenario runner (step count, sumocfg parsing):** `src/Sim.Run/Program.cs`.
- **Scene payload + network cropping helpers:** `src/Sim.Viz/{Payload.cs,SceneGen.cs}` (`ScenePayload`,
  `BuildNetwork`, `CropNetwork`, `NormalizeVehicleSlots`, `AssignStableDiscSlots`).
- **Gallery + CI:** `scripts/gen-demos.sh`, `.github/workflows/demos.yml`.

## 9. How to work
Branch off **`main`** (latest). Design-first is already done (this doc). Land T1→T7 in order, each with its
gates green, committing per task. When done, open a PR to `main`. The realism session will fetch `main`
afterward. Do NOT depend on the `claude/livecity-realism-fixes` branch except to study/cherry-pick
`BuildLiveCityDemo` + the `ScenePayload.UseDataHeading` field.
