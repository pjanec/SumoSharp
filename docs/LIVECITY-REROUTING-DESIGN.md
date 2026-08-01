# LIVECITY-REROUTING-DESIGN — congestion-aware rerouting in the live-city hosts

**Status: DESIGN FOR OWNER REVIEW — no code until signed off.**
Owner ask (Aug 1 3D re-check): *"cars seem to blindly wait in queues as if not looking for
alternative trip when the city is already congested."*
Companion docs: `LIVECITY-REROUTING-TASKS.md` (work breakdown + success conditions),
`LIVECITY-REROUTING-TRACKER.md` (checklist). Backlog origin:
`JUNCTION-REALISM-RESUME.md` §5 item 0-NEW.

## 0. The one-paragraph summary

**The mechanism already exists and is golden-pinned; nothing new is invented.** The engine
carries a complete port of SUMO's `device.rerouting` (the P1E series, `[x]` in
`HIGH-DENSITY-PLAN.md`): per-edge live speed smoothing exactly per `MSRoutingEngine`
(`Sim.Ingest/RerouteEdgeWeights.cs` — ring-buffer moving average, `isDelayed` latch,
`NUMERICAL_EPS` floor), an A* router over live efforts (P1E-3, `NetworkRouter`), periodic
per-vehicle reroute with per-entity seeded equip + jitter phase (P1E-4:
`Engine.cs` search `RerouteEquipRngSalt` / `RegisterPeriodicReroute`; route-slot recycling;
parallel batch over a frozen weight snapshot; serial apply via `CommandBuffer.ReplaceRoute`),
and a committed golden anchor scenario (P1E-5, `scenarios/NN-reroute-congestion`). It is
configured through four sumocfg keys (`ScenarioConfig.cs`: `device.rerouting.probability`,
`.period`, `.adaptation-steps`, `.adaptation-interval`) and is **inert at the default
`period = 0`** — which is why the live-city demo has never shown it. This design is therefore
**pure host wiring + validation**: pass the keys through the LiveCity host (and hence the City3D
viewer), decide the demo defaults with the owner, and validate on the demo's own surfaces.

## 1. What happens today (the defect, precisely)

`LiveCitySim` builds its engine from a synthetic config XML (`LiveCitySim.cs:378`,
`ScenarioConfigParser.ParseXml(...)`) that contains no `device.rerouting.*` keys, so
`ScenarioConfig.ReroutePeriod` stays `0.0` and every device seam in the engine returns
immediately (`_edgeWeights` null, `nextRerouteTime` +infinity). Cars receive one route at
insertion and keep it forever unless a *failure* path fires (dead-lane / wrong-lane reroutes,
`SetDestination`). Nothing re-examines a *valid but congested* route — exactly the owner's
observation.

## 2. Design

### 2.1 Config pass-through (the only engine-facing change — and it is host-side only)

Add to the spliced XML in `LiveCitySim` (next to the existing `time-to-teleport` splice, same
pattern) when rerouting is enabled for the run:

```xml
<device.rerouting.probability value="{prob}"/>
<device.rerouting.period value="{period}"/>
```

driven by two new `LiveCityConfig` fields (`RerouteProbability`, `ReroutePeriodSeconds`) with
env-var overrides in the demo host, mirroring the F3 gate rollout pattern:

| Env var | Meaning | Proposed default |
| --- | --- | --- |
| `LIVECITY_REROUTE` | master switch (`1` = on) | **off** (opt-in for A/B; §2.4) |
| `LIVECITY_REROUTE_PERIOD` | seconds between per-vehicle reroute checks | `60` |
| `LIVECITY_REROUTE_PROB` | fraction of cars equipped with the device | `1.0` |

`adaptation-steps`/`adaptation-interval` stay at SUMO's own defaults (180 / 1.0 s) unless
measurement says otherwise — no new knobs invented. All rows go into `ENV-GATES.md`
(`EnvGateDocumentationTests` enforces completeness).

**No `Sim.Core`/`Sim.Ingest` change of any kind.** The engine defaults are untouched, so every
golden and every existing hash (`5ac89389…` default L2, bench `A134ED37…`) is untouched by
construction — the goldens-inert constraint is satisfied without an argument.

### 2.2 Determinism (constraint from CLAUDE.md — already discharged by P1E)

The device is deterministic by existing construction: equip is drawn from
`VehicleRng.SeedFor(Seed, entityIndex, RerouteEquipRngSalt)` and the per-vehicle phase jitter
from the same per-entity stream — **no `System.Random`, independent of thread order**
(P1E-4 §0.5.1). The batch A* runs over a frozen weight snapshot with serial apply. LiveCity's
existing determinism surfaces (par == single, region plan) are unaffected in kind; T2 in the
tasks doc re-verifies them with the device ON because that configuration has never been run in
this host.

### 2.3 Interplay with LiveCity's existing reroute machinery (analysis, no code)

- **`SetDestination` / external reroutes**: the periodic device routes toward the *current*
  route's final edge; after a `SetDestination` the final edge IS the new destination, so the
  device simply keeps optimizing the path to wherever the car is currently going. Compatible.
- **Dead-lane / wrong-lane rescue reroutes** (`TryReResolveFromActualLane`,
  `TryRerouteStuckDeadLane`): failure-path mechanisms that fire regardless of the device; the
  device *reduces how often cars get into* those states by steering demand away from jams
  earlier. P1E-4's route-slot recycling means both write the same per-vehicle slot — already
  the shipped coexistence semantics in the bare engine (goldens combine device.rerouting with
  parking stops; GAP-4 stop preservation applies to both).
- **Closed-loop demand** (`live < CarTargetConcurrent` insertion): rerouting changes paths, not
  destinations or the population — measurement lesson 4 (label the demand model) applies to the
  validation numbers, and the smoke comparisons in T3 are closed-loop by declaration.

### 2.4 Rollout posture (owner decision point)

Recommendation: **land opt-in first** (`LIVECITY_REROUTE=1` for A/B, exactly how
`LIVECITY_F3OCCUPANCY` was rolled out), validate on the Geneva terrain, then flip the demo
default to ON in a one-line follow-up once the owner confirms the behaviour reads correctly in
3D. Rationale: every measurement-discipline lesson in CLAUDE.md argues for an A/B-able gate
first; the flip is trivial once trusted. The alternative (default-ON immediately with
`LIVECITY_REROUTE=0` kill-switch) is listed for completeness if the owner prefers to see it
immediately in the next 3D build.

### 2.5 What this does NOT cover (named, so it is a decision rather than an omission)

- **Pre-insertion rerouting** (`device.rerouting.pre-period`) — P1E-6, separately tracked;
  demo cars get routed at spawn by LiveCity's own router on free-flow weights. If the owner's
  complaint persists for *newly spawned* cars entering existing jams, P1E-6 is the follow-up.
- **Strategic patience** ("waits in queue" because the alternative is only marginally better):
  SUMO's device replaces the route whenever the recomputed one is cheaper on live efforts; no
  hysteresis beyond the period. We port that behaviour as-is (target the flow, not the method
  — but here SUMO's method IS the flow we want).

## 3. Validation design (detail + success conditions in the TASKS doc)

1. **Inertness**: device OFF ⇒ demo byte-identical (existing smoke/witness outputs unchanged);
   full sln suite green (goldens can't move — engine untouched).
2. **Effect**: 400-car closed-loop smoke A/B (OFF vs ON, both arms' gates set explicitly): with
   congestion present, expect measurably redistributed flow — arrivals not worse, INTERNALSTUCK
   not worse, and route-changed count > 0 (witness line added by T3).
3. **Determinism**: two identical ON runs byte-identical on the witness stream; par == single.
4. **Hour horizon**: `LongHorizonGridlockDiagTests` arms stay green with the device ON
   (new test variant, gated so the suite cost stays bounded).
5. **Owner 3D re-check** on Geneva with `LIVECITY_REROUTE=1` — the real acceptance gate.
