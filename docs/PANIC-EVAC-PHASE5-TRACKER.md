# PANIC-EVAC-PHASE5-TRACKER.md — scale checklist

Status for Phase 5 (`PANIC-EVAC-PHASE5-DESIGN.md` / `-TASKS.md`). Principle: **full city on the parity
engine; evac attaches only to a bounded working region around the incident** (cost ∝ local affected
population, not city size). Staged: Tier 1 now, Tier 2 (heavy opt, 10k target) after Tier-1 measurement.

> **Status:** owner approved staged (Tier 1 now, Tier 2 later; final goal a 10k-vehicle city, but the
> evac stays local). **Tier1-B1 DONE and Opus-reviewed** (427 pass / 3 skip; hash `909605E965BFFE59`
> unmoved; locality proven: 50/371 ever-active vehicles tracked). B1 surfaced a latent parity-core
> reroute bug (multi-lane active reroute read the stale original route → crash); fixed + committed
> separately with a fail-pre-fix/pass-post-fix core regression test (`RungB3MultilaneRerouteRegressionTests`).
> **Tier1-B2 DONE and Opus-reviewed** (viz rendered t=80/180/290; cost profile measured — see S4). **All
> of Tier 1 is complete.** Tier-2 priority is now set by the measured profile (viz payload + auto-track
> scan lead; evac-phase optimization gated on working-region population). Awaiting go for a Tier-2 addendum.

## TIER 1 — realistic organic town + local auto-attach
### S1 — working-region auto-attach
- [x] **T1.1** `EvacConfig.WorkingRadius` (default 250; documented ≥ incident radius + jam margin)
- [x] **T1.2** `EvacDirector` auto-track-by-region (deterministic sort by handle.Index; in-region only; off by default → grid/TLS demos unaffected)

### S2 — organic scenario
- [x] **T2.1** `EvacOrganicScenario` (LoadScenario city-organic-L2 net+demand; incident at junction 415, the busiest interior TLS junction)
- [x] **T2.2** demand-under-director confirmation (peakActive=231 under the director's Tick loop)

### S3 — behavioural tests
- [x] **T3.1** cascade on the organic net (panicked=7, peakOrcaPush=2, pedestrians=5, maxPedDist=274.6m ≫ 0.8·SafeRadius)
- [x] **T3.2** locality (50 tracked ⊊ 371 ever-active; 321 never tracked — the core Phase-5 property)
- [x] **T3.3** containment + determinism (no ped/pusher leaves navmesh; two runs bit-identical)
- [x] **T3.4** suite green (427 pass) + hash gate (`909605E965BFFE59`) + existing grid/TLS evac tests unchanged

### S4 — viz + measurement
- [x] **T4.1** organic viz scene (`SceneGen.BuildEvacOrganic`, `Sim.Viz --evac-organic`; Opus rendered t=80/180/290 — realistic mesh, town-wide congestion 100→212→312 vehicles, incident ring + safe-radius local, evac discs clustered in the working region)
- [x] **T4.2** cost profile at ~400 vehicles (`Sim.EvacProfile`; opt-in `EvacDirector` profiler, off by default) — **dominant evac hotspot = pusher step**

### Tier-1 measured cost profile (the input that scopes Tier 2)
`Sim.EvacProfile`, organic town, 300 ticks, peak 320 / ever-active 372 vehicles, total 1.27 s:

| phase | ms | % tick | note |
|---|---|---|---|
| engine.Step | 1089 | 86.1 % | parity core (not an evac cost) |
| **pusher step** | **81** | **6.4 %** | `DriveOrcaPushers` — dominant EVAC phase |
| other | 40 | 3.2 % | auto-track scan, blocked detector, bookkeeping |
| pedestrian step | 21 | 1.7 % | |
| fear update | 18 | 1.4 % | |
| disc feeds | 6 | 0.4 % | |

**Key conclusion (reframes Tier 2):** at local scale the *entire* evac layer is ~10 % of tick time; the
parity engine dominates. Because the evac layer only ever touches the bounded working region, its
absolute cost does **not** grow with city size — so the O(n²) evac phases (fear/disc/pusher) need
optimizing **only if a denser incident traps a low-thousands local population** (design §1). The
city-size-dependent Tier-2 costs are instead (a) the per-tick auto-track scan (O(city), currently a
full read-buffer scan) and (b) viz payload (2.0 MB for 372×300 → must be managed for 10k).

## TIER 2 — 10k city (heavy optimization; outline, detailed later)
Priority now set by the measured profile above:
- [ ] **viz payload management** for a 10k city (region-crop / decimation / caps, logged) — the clearest city-size-driven cost
- [ ] **auto-track scan** optimization (spatial query instead of full O(city) read-buffer scan each tick) — measure first at 10k
- [ ] **pusher step** (`MixedTrafficCrowd`) spatial hash — first evac phase to optimize, but ONLY if working-region population reaches low-thousands
- [ ] FearField uniform grid + spatial composite CrowdSource/disc feeds (bit-identical) — gated on the same working-region-population trigger
- [ ] enable `OrcaCrowd.UseSpatialHash` at scale
- [ ] 10k-city demo scenario

---

### Proposed batches
- **Tier1-B1:** S1 (T1.1, T1.2) + S2 (T2.1, T2.2) + S3 tests — auto-attach + organic scenario + behavioural tests.
- **Tier1-B2:** S4 (T4.1 viz + T4.2 measurement) — render + measured cost profile.
- **Tier2-B*:** written as an addendum against Tier-1's measured profile.
