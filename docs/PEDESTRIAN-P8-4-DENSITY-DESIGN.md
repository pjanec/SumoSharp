# PEDESTRIAN-P8-4-DENSITY-DESIGN.md — dialable ped-density knob + crossing-throughput guard (HOW)

**Status: P8-4a knob DONE; P8-4b dynamic crossing guard DEFERRED.** Realizes
`COORDINATION-pedestrian-x-subarea.md` §3 row 5 ("Density is calibrated & dialable — expose a ped-density
knob + safe range; crowds must not deadlock crossings so hard the calibrated cars gridlock") and tracker
`PEDESTRIAN-TRACKER.md` Stage P8 **P8-4**. Consumes the calibrated crop manifest (`SubareaManifest`,
`manifest.density.knee_veh_lkm`, `manifest.lane_km`) and the baked walkable network. Additive + inert:
nothing on the existing demand path changes unless a caller asks the knob for a cap/rate.

## 1. Why length, not area, is the anchor

The SumoData vehicle side calibrates **density = vehicles per lane-km** (`knee_veh_lkm` at `lane_km`), so the
box's whole notion of "how loaded is this crop" is a *per-length* density on a known network length. The ped
knob mirrors that exactly — **pedestrians per walkable-km** — rather than peds/m²:

- It is the *same* model the cars are calibrated on, so "dial the peds to match how loaded the cars are" is a
  like-for-like comparison, and the two knobs read the same way in the manifest.
- Walkable **area** would have to sum baked polygons, which **overlap** at junctions (walkingArea over
  sidewalk); summing overlaps *inflates* area, and an inflated area inflates the cap for a target density —
  the *unsafe* direction. Sidewalk **length** (sum of sidewalk-lane polyline lengths) is overlap-free and
  monotone, so it is both simpler and conservative.

Walkable length `L_km` = Σ over `network.Sidewalks` of the lane-shape polyline length, ÷ 1000.

## 2. The knob (P8-4a)

`PedDensityKnob` is a pure, deterministic value that maps a **dial** to a `(PopulationCap, SpawnRatePerSecond)`
pair for `PedDemandConfig`:

- **Dial** `DensityFraction d ∈ [0, 1]` — `0` = empty, `1` = the **safe maximum**. Values outside [0,1] are
  clamped (the "safe range" enforcement: the knob will never emit a cap above its safe ceiling).
- **Safe reference** `SafePedsPerWalkableKm` (documented default `SafePedsPerWalkableKmDefault = 250.0`).
  Chosen conservatively: at ~2 m effective sidewalk width that is ~0.5 ped/m² — LoS C ("free/steady flow",
  well below the ~1.5–2 ped/m² onset of a stop-and-go jam), so a full-dial crowd flows rather than locking a
  crossing. This is the *static* half of the crossing-throughput guard (see §3).
- **Cap:** `PopulationCap = round(d · SafePedsPerWalkableKm · L_km)`, floored at 0.
- **Rate (Little's law):** to *sustain* `N` live peds whose mean trip lasts `T` seconds, the arrival rate is
  `λ = N / T`. So `SpawnRatePerSecond = PopulationCap / MeanTripSeconds` (`MeanTripSeconds` default 90 s; a
  caller with a measured mean trip time for its box passes it). `PedDemand` already holds the live population
  *at* the cap and refills on arrival, so the rate only sets how fast it climbs to the cap and how promptly a
  freed slot refills — the cap is the load-bearing safety number.

`PedDensityKnob.ForNetwork(network, dial, manifest?, meanTripSeconds?)` computes `L_km` from the sidewalks and
returns the pair; `PedDensityKnob.Apply(config, ...)` returns a copy of a `PedDemandConfig` with `PopulationCap`
/ `SpawnRatePerSecond` overwritten (additive: callers that don't invoke it are unaffected).

## 3. Crossing-throughput guard

**The invariant:** a full-dial (`d=1`) crowd must not saturate a crossing so hard the *calibrated* vehicle
flow gridlocks. Two layers:

- **Static ceiling (P8-4a, LANDED with the knob):** `SafePedsPerWalkableKm` is set in the LoS-C band, so even
  at `d=1` the *aggregate* pedestrian presence stays in free-flow density — crossings discharge peds faster
  than they arrive, leaving vehicle green-time usable. This is the conservative, always-on guarantee and needs
  no per-tick machinery or vehicle-side coupling.
- **Dynamic per-crossing guard (P8-4b, DEFERRED):** throttle/deny spawns that would feed a *specific* crossing
  already at its discharge capacity, measured against that crossing's vehicle-calibrated headroom. This needs
  the live vehicle-flow seam at the crossing (the calibrated car side, **SumoData-owned**) and the P4
  vehicle-yields-at-crossing behaviour (watch item §7) — coordinating those is out of scope for a unilateral
  ped-session change. Deferred until that seam exists; the static ceiling holds the invariant until then.

## 4. Determinism & inertness

- Pure arithmetic on committed inputs (sidewalk lengths, manifest, dial) — no RNG, no engine state, no SUMO.
  Same inputs → same `(cap, rate)` bit-for-bit.
- Inert: the knob only produces numbers a caller chooses to feed into `PedDemandConfig`. The default demand
  path (and every committed golden) is untouched — this is a helper, not a hook.

## 5. Tasks & success conditions

- [x] **P8-4a — `PedDensityKnob`** (walkable-length density model + Little's-law rate + safe-range clamp).
  *Success:* cap is monotone non-decreasing in the dial and **0 at d=0**; d>1 clamps to the d=1 cap (safe
  ceiling never exceeded); rate = cap / meanTrip; walkable length computed from the box sidewalks matches the
  manifest scale; deterministic. A test drives the committed box (`L_km` from its 48.6-lane-km crop) and wires
  the knob's output through `PedDemand` to show the live population tracks the dialed cap.
  **Done** (`PedDensityKnob.cs`, `PedDensityKnobTests.cs`, 4 tests): box walkable length 16.205 km over 168
  sidewalks (deterministic, sane band); cap monotone in dial & 0 at d=0; rate = cap/meanTrip (Little's law);
  d>1 clamps to the d=1 ceiling, d<0 → 0; `Apply` carries every non-density field through; applied at dial=1
  the live population climbs to *exactly* the dialed cap and never exceeds it.
- [ ] **P8-4b — dynamic per-crossing guard** *(DEFERRED — needs the vehicle-calibration seam + P4; §3).*

## 6. Invariants

- Additive/inert: no committed ped golden changes; the knob is opt-in.
- Conservative by construction: area is never inflated (length-based); the safe ceiling sits in the LoS-C band;
  `d` is clamped to [0,1] so the emitted cap can never exceed the safe maximum.
- No `System.Random`; the knob is pure arithmetic.
