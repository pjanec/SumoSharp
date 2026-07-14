# PANIC-EVAC-PHASE5-TIER2-DESIGN.md — scale to a 10k-vehicle city (heavy optimization)

Design (HOW) for **Phase 5, Tier 2** — the addendum promised by `PANIC-EVAC-PHASE5-DESIGN.md` §6, written
against the **measured** Tier-1 cost profile (not a guessed one). Builds on the completed Tier-1
(`EvacOrganicScenario`, auto-track, the `Sim.EvacProfile` measurement). Parity-exempt; determinism
preserved (hash `909605E965BFFE59` unmoved). **Non-goal: any parity-core change** (Phase-5 §7).

## 1. What Tier-1 measurement actually told us — two independent cost axes
The tuned Tier-1 run (organic town, ~174 panic / 151 abandoned / **604 pedestrians**, 300 ticks, 2.95 s)
gives the honest breakdown (`PANIC-EVAC-PHASE5-TRACKER.md`):

| phase | % tick | scales with |
|---|---|---|
| pusher step (`MixedTrafficCrowd`) | 39.7 % | **working-region population** (O(m²) today) |
| pedestrian step (`OrcaCrowd`) | 33.9 % | **working-region population** (O(m²) today) |
| engine.Step (parity core) | 21.2 % | **city size** (already fast — ~15k proven) |
| other / fear / disc feeds | < 5 % | mixed (auto-track scan is O(city)) |

So there are **two separate axes**, and the "10k city" goal stresses both — but differently:
- **City-size axis** (grows with *total* vehicles): the parity engine (already benchmarked to ~15k
  concurrent, `scenarios/_bench/city-15000`, peak 17 639), the **auto-track scan** (`O(city)` per tick),
  and the **viz payload**. The evac layer never processes all 10k (R3 locality — Tier-1's load-bearing
  result: 159 of 371 vehicles never tracked).
- **Working-region-population axis** (grows with the *local* affected count): the two O(m²) crowd solvers.
  A large/dense incident in a 10k city can trap low-thousands locally (design §1), and *that* is what
  makes the pusher/pedestrian steps explode — not the city size.

**The optimization priority follows the measurement, not intuition:** the two crowd solvers first (they
are ¾ of the cost and the thing that actually blows up), then the city-size items, then the rest.

## 2. The core algorithmic win: spatial hashing the two crowd solvers (bit-identical)
Both solvers are LP-style over a nearest-`k` neighbour set, so **the neighbour set AND its order must be
byte-identical** to brute force or trajectories diverge (same reasoning as OrcaCrowd's Q3). The proven
recipe already exists in `OrcaCrowd` and we copy it exactly.

### 2a. `MixedTrafficCrowd` spatial hash (pushers) — #1 hotspot (40 %), net-new
`MixedTrafficCrowd.Plan(i, dt)` currently brute-forces `for j in 0..count` (and a separate walls loop).
Add an **opt-in uniform grid** mirroring `OrcaCrowd.RebuildGrid`/`GridCandidates` exactly:
- `public bool UseSpatialHash { get; set; }` — **default false** (brute-force path byte-identical to
  today; existing grid/TLS/organic demos and their goldens untouched until explicitly enabled).
- Cell edge = `NeighbourDist` (22.0), so a full neighbourhood is always within the 3×3 cell block.
- `RebuildGrid()` once per `Step()` from the frozen start-of-step positions (the PLAN/EXECUTE double
  buffer already freezes them, so this is safe and order-independent across `i`).
- `GridCandidates(i)` gathers the 3×3 block, then **`Array.Sort` ascending by index** before returning —
  this sort is the load-bearing determinism step.
- The candidate list feeds the SAME `Insert(...)` nearest-`k` routine the brute path uses (as OrcaCrowd
  unifies via `GatherAgentNeighbours(useAll:)`), so grid and brute produce an identical set + order by
  construction. **Walls are unchanged** (appended after the capped vehicle set, tested in fixed
  Add order — not spatially indexed; there are few walls).
- Determinism proof: a new `MixedTrafficSpatialHashTests` mirroring `OrcaSpatialHashTests` — build two
  crowds, enable the hash on one, step many times, assert **exact `Position` AND `Heading` equality**
  every step, specifically exercising the `MaxNeighbours` cap and the non-holonomic steering path (the
  ordering-sensitive combinations).

### 2b. Enable `OrcaCrowd.UseSpatialHash` for the pedestrian crowd — #2 hotspot (34 %), ~free
The machinery is done and proven bit-identical; Tier-2 just **enables it on the evac ped crowd**
(`EvacDirector._peds`) behind an `EvacConfig` flag (default off to keep every current demo/test
byte-identical). Verification: with the flag on, the `EvacOrganicDemoTests.ContainmentAndDeterminism`
signature must be **identical** to the flag-off signature (grid == brute), plus the existing
`OrcaSpatialHashTests` continue to guard the general property.

> Note (residual, not in scope unless measured): `OrcaCrowd.QueryNear` and `VehicleMover.QueryNear` (the
> cross-regime disc queries behind `CompositeFootprintSource`, consumed by the lane engine's per-vehicle
> crowd query) are brute-force and NOT accelerated by the agent grid. Tier-1 measured disc feeds < 0.5 %,
> so this is deferred; §5 says re-measure at 10k and only then touch it.

## 3. City-size axis: the 10k demo, viz payload, auto-track scan
### 3a. The 10k host scenario
Two committed-recipe options; the design picks **(A) as the primary, (B) as a stretch**:
- **(A) Reuse `scenarios/_bench/city-15000`** (already committed; `netgenerate --grid --grid.number=24
  --grid.length=500 -L 1`, ~13–17k concurrent). Low risk, zero new generation. It is 1-lane (a documented
  engine limitation blocks multi-lane `--grid` nets), which is fine for a *scale* demo — the evac cascade
  (panic → flee → jam → abandon → peds) does not require multi-lane, and the multi-lane strategic
  lane-change path (the Tier-1 reroute fix) simply doesn't run on 1-lane. A big central incident produces
  the working-region load; the surrounding ~10k cars exercise the city-size axis.
- **(B, stretch) A large 2-lane `--rand` organic net** (scale the `city-organic-L2` recipe). More
  realistic and it exercises multi-lane reroute at scale, but `--rand` at 10k-concurrent is unproven and
  may need a generation spike (iterations/min-distance tuning + the `gen-benchmark.sh` Little's-law period
  loop, which is `--grid`-only today and would need adapting). Gated behind a go/no-go spike (§5); the
  demo does NOT block on it.

### 3b. `EvacCityScenario` (new demo builder)
Analogous to `EvacOrganicScenario`: `LoadScenario` the 10k net + demand, director in auto-track mode,
a large central incident sized to trap a low-thousands working-region population (so the crowd solvers
are genuinely stressed and the §2 hashes have something to bite on), boundary `ExitEdges`. Deterministic.

### 3c. Viz payload management (city-size, first hard cap)
A 10k-car × N-frame payload is tens of MB — the Tier-1 organic run was already 4.3 MB at 372 cars/300
frames. The viz must bound its output **with explicit logging of anything dropped (no silent
truncation)** — options, layered:
- **region-crop**: emit full detail inside the working region + a decimated sample of distant traffic;
- **frame decimation**: keep every k-th frame (front-end already interpolates);
- **per-frame vehicle cap** with a logged count of omitted vehicles.
The exact policy is chosen against the measured payload; the invariant is a `log`/console line stating
what was cropped/decimated/capped.

### 3d. Auto-track scan (city-size, measure-first)
`AutoTrackWorkingRegion` is `O(city)` per tick (full read-buffer scan). No engine spatial index over
vehicle positions exists to replace it. Tier-1 folded it into "other" (< 4 %). **Measure it at 10k
first** (via `Sim.EvacProfile` on the city scenario); optimize (a coarse world-grid pre-filter over the
read buffer, or an incremental entrant set) ONLY if the measurement shows it material. No speculative work.

## 4. Determinism & parity (both new hashes, both scenarios)
- Parity: `Sim.Evac` and the crowd solvers stay off the golden path; the parity engine is unchanged.
  Hash `909605E965BFFE59` unmoved (the `Sim.Bench` gate, re-run every batch).
- Determinism of the new hashes: proven by exact-equality tests (§2a, §2b), the pattern
  `OrcaSpatialHashTests` established. Both flags default OFF, so nothing changes until a demo opts in,
  and when on the output is byte-identical to off.
- No `System.Random`; grids are pure pre-filters (same neighbour set + order), never a behavioural change.

## 5. Sequencing, spikes, and honest gates
1. **Tier2-B1 (core win, self-contained):** §2a `MixedTrafficCrowd` hash + §2b enable `OrcaCrowd` hash for
   peds, both with bit-identical tests, plus a **synthetic heavy-load micro-benchmark** (a few hundred→
   low-thousands agents) showing the O(m²)→O(m·k) speedup with a before/after number. No scenario work.
2. **Spike (go/no-go, timeboxed):** can `--rand` author a 2-lane ~10k organic net without the
   strategic-lane-change/insertion failure? Output: use (B) or fall back to (A). Purely investigative.
3. **Tier2-B2 (the 10k demo + measurement):** §3 `EvacCityScenario` on the chosen net, viz payload
   management, auto-track measured, and a **before/after generation-time profile at 10k** (hashes off vs
   on) that proves the §2 work made the 10k evac tractable — the closing deliverable.

## 6. Success shape (details in the TASKS doc)
- Both hashes bit-identical (exact-equality tests green) and measurably faster on a heavy synthetic load.
- A 10k-vehicle evac demo that generates in bounded time and memory, with a logged viz-payload policy.
- A before/after measurement showing the crowd-solver hotspots reduced at the low-thousands working-region
  scale, with the parity hash and full suite still green throughout.

## 7. Risks
- **`--rand` 10k generation** may not converge (§5 spike gates it; fallback (A) removes the risk).
- **Working-region population may stay small** even at 10k unless the incident is large/central — the
  scenario must be tuned so the crowd solvers are actually exercised (else the hashes have nothing to
  optimize and the demo doesn't prove anything). `EvacCityScenario`'s incident sizing is a first-class
  design parameter, tuned against a measured tracked-count.
- **Viz payload** could still be large after cropping — the cap + logging invariant makes any truncation
  explicit rather than silent.
