# Viz-unification — status

Execution status of `docs/VIZ-UNIFICATION-DESIGN.md` (the unified HTML-replay generator). This is the
at-a-glance record of what landed, how it was verified, and the few deliberate deviations from the design
(each with its rationale). See the design doc for the WHY/HOW; this doc is the WHAT-happened.

## The one-line outcome

Every gallery **vehicle** demo now renders the **real deterministic engine trajectory**, DR-reconstructed
smooth, through **one** builder (`VizReplayBuilder`). Change the shared reconstruction once and every one of
those pages improves for free — proven below.

## Task tracker

| Task | Status | Notes |
|---|---|---|
| **T1** Shared HTML writer (`VizHtml.Write`) | ✅ done | gallery + IgBridge side-by-side both route through it; byte-identical verified |
| **T2** `VizReplayBuilder` + `IVizReplaySource` + `LiveCitySource` | ✅ done | `--live-city-demo` payload byte-identical to the reference impl |
| **T3** `EngineScenarioSource` + `--engine-replay` | ✅ done | real deterministic engine run; golden trajectory spot-check matches to 2 dp |
| **T4** gallery (`gen-demos.sh`) → `--engine-replay` | ✅ done (1 documented exception) | small/medium vehicle demos smoothed; 3 city-scale kept on FCD (size) |
| **T5** ped/evac scenes → the builder | ◑ partial (design-sanctioned) | flagship car+ped `live-city` routed through the builder; pure-ped demos keep their existing render (documented exception) |
| **T6** fold IgBridge `VizExport` smooth side onto the builder | ⨂ deliberately not folded | writer already unified (T1); folding the scene would break the v5/v6 diagnostic — see below |
| **T7** prove DR-projection + docs | ✅ done | single-constant tweak reprojects to every turning vehicle page; this doc + guide §6 updated |

## What was built

- **`src/Sim.Viz/VizHtml.cs`** — the single marker-injection HTML writer. `Program.WriteHtml` and
  `Sim.IgBridge.Host/VizExport` both call it; the duplicated inject glue (design §1.2) is gone.
- **`src/Sim.Viz/IVizReplaySource.cs`** — the source abstraction (`Step`/`Dt`/`VehicleSource`/`PedSource`/
  `Lanes`/`Network`/`View`).
- **`src/Sim.Viz/VizReplayBuilder.cs`** — the one DR-smoothing loop, lifted from the reference
  `BuildLiveCityDemo`: cars via `DrClock.ResolveAt` + `KinematicReconstructor` (center + emitted heading +
  upcoming-lane look-ahead), peds via `PedRemoteReconstructor` off the wire. Emits the `useDataHeading`
  5-tuple vehicle scene + 4-tuple LOD-coloured ped discs.
- **`src/Sim.Viz/LiveCitySource.cs`** — `IVizReplaySource` over the real `LiveCitySim` (cars + peds).
- **`src/Sim.Viz/EngineScenarioSource.cs`** — `IVizReplaySource` that runs a plain deterministic `Engine`
  on a scenario dir and publishes each step onto an in-memory replication bus (the same wiring
  `LiveCitySim` uses). Feeds `--engine-replay <scenarioDir> <out>`.
- **`ScenePayload.UseDataHeading`** (Payload.cs) — serialized `useDataHeading`; the player already honors it.
- **`scripts/gen-demos.sh`** — `demo_golden`/`demo_run` now call `--engine-replay`; `live-city` calls
  `--live-city-demo`.

## Verification (how each gate was checked first-hand)

- **Parity / iron law:** no `Sim.Core` edit, no golden or `tolerance.json` touched (viewer/tool only) —
  parity is safe by construction. `tests/Sim.ParityTests` was **657 passed / 4 skipped** after T1 and is
  unaffected by the later viewer-only tasks (`Sim.ParityTests` does not reference `Sim.Viz`).
- **T1:** `--ped-dodge` HTML byte-identical before/after (sha256 match).
- **T2:** the `--live-city-demo` embedded `REPLAY_DATA` payload (5,883,948 B) is **byte-identical** to the
  reference branch `claude/livecity-realism-fixes`; two runs byte-identical (determinism); `useDataHeading`
  present. (The full HTML differs only by an unrelated `descToggle` template feature this design doesn't touch.)
- **T3:** golden trajectory spot-check on `11-priority-junction` at t=29 — rendered CENTER
  `vMajor(369.86,198.4,90°)` / `vMinor(201.6,323.16,0°)` equals the golden front-center minus half-length
  to 2 dp; two runs byte-identical.
- **T4:** `gen-demos.sh` produces **54/54, 0 skipped**; **two full runs byte-identical** (0 differing files);
  every `--engine-replay` page carries `useDataHeading`; `scenarios/` left pristine; `site/` is gitignored.
- **T7 (DR-projection proof):** with the gallery at baseline, bumping the single shared constant
  `KinematicReconstructor.LookAheadLengthFactor` 0.5→0.9 and regenerating changed **every turning vehicle
  page** — roundabout, on-ramp merge, multilane junction turn, keep-right lane change — while leaving
  `priority-junction` (straight-through crossing, nothing to anticipate) untouched, which confirms the
  look-ahead is a real geometric mechanism sourced from one place, not noise. Reverting the constant restored
  every page to byte-identical with the pre-tweak snapshot. One source, whole gallery.

## Deliberate deviations from the design (with rationale)

1. **T4 — the 3 "City scale" demos stay on raw FCD** (`demo_run_fcd`), not `--engine-replay`. At
   hundreds-to-~1000 concurrent vehicles over 500–700 s, a 10 Hz DR-reconstructed page is tens-to-hundreds of
   MB (`city-signalized` is already 14.7 MB at 1 Hz FCD; 10 Hz ≈ 150 MB), and individual junction arcs are
   invisible among the specks. Those pages showcase aggregate flow at scale, not per-vehicle turn smoothness,
   so raw FCD is the right size/value tradeoff and keeps GitHub Pages mobile-friendly. `warm-start`
   (needs `Engine.WarmUp`) and the external-agent demos (`external-agents`, `reroute` — non-plain-engine
   input) also keep their existing paths. All other vehicle demos are DR-smoothed.
2. **T5 — the flagship car+ped scene (`live-city`) is routed through the builder; the pure-pedestrian
   `--ped-*` demos keep their existing dedicated renders.** The design explicitly permits this
   ("keep their existing continuous `PositionOf` disc emit as a documented exception"). `ped-remote` is
   already wire-reconstructed; the others use the continuous `PositionOf` emit, which is smooth enough for
   scenes with no cars. Migrating each pure-ped builder to a `PedSceneSource` behind `IVizReplaySource` is the
   preferred future step but was scoped out here to avoid a large, higher-risk rewrite of many distinct ped
   builders.
3. **T6 — the IgBridge side-by-side scene is NOT folded onto `VizReplayBuilder`.** The design assumed
   `VizExport` was "raw vs smooth (smooth from the builder)". The code has since evolved into a **v5-vs-v6
   comparator of two IgBridge emit-pipeline variants** (`Sim.IgBridge.Host/Program.cs`): both sides are
   `FakeIg` reconstructions of `IgBridgeSession.AllEmitted`, and A/B-ing them is the tool's entire purpose.
   `VizReplayBuilder` is a *different* reconstruction path (`DrClock.ResolveAt` + `KinematicReconstructor`,
   resampled — no `IgBridgeSession`/`FakeIg`/emit-cadence/playout), so routing either side through it would
   replace exactly the thing the diagnostic measures. The real duplication the design flagged (§1.2, the HTML
   inject glue) was already removed in **T1** — `VizExport` renders through the shared `VizHtml.Write`. So the
   "one writer" goal is met; the "one scene path" part is deliberately declined for this tool because it would
   break its measurement.

## Known minor quirk (not a regression)

`--engine-replay` (and any builder-driven replay) drops a vehicle from the reconstruction one render-frame
before its true last golden appearance at end-of-life: the in-memory bus clears a vehicle's whole DR history
the instant the engine snapshot no longer contains it, and that clear can land inside the same render batch
still resolving `tau` up to the vehicle's final instant. This is a one-render-step-early disappearance at the
very end of a vehicle's life — not a positional error or teleport — and it lives in the shared
`VizReplayBuilder`/`InMemoryReplication` lifecycle (the same mechanism `LiveCitySource` already relied on),
so it is out of scope for the adapter work here. Worth a follow-up if it ever reads as visible.
