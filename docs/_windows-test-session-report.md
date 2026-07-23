# Windows visual-testing session — report for the dev session

**Branch:** `claude/city3d-live-city-mode-3yf4oc`. **Current at push `5adf4f9`** (this session's camera /
grid / smoothness commits are pushed; the dev session's `becc224` is integrated).
**Role:** a Windows/real-GPU testing session ran the three live-city viewers and reported what looks
right/wrong. This is the handoff. All root causes below are **measured** (frame traces), not guessed —
where a theory was refuted by data, that's called out.

> **To run the harness (esp. for #15):** see `docs/LIVE-CITY-HARNESS-GUIDE.md` — headless run command,
> env knobs, and the exact stopped-fraction / arrival-rate probe used to characterise the gridlock.

> **UPDATE:** Most of the original findings are now RESOLVED. The dev session's `becc224` implemented
> #5/#6/#9/#10/#11; this testing session pushed camera/grid/smoothness (#12–#14) + the TL-crop fix
> (#16). **#7 now VERIFIED FIXED over DDS. Open: #8 (REGRESSED by `bfbf7c9`), #15 (DR-core/engine); #17 partially fixed (per-`Step` publish residual).** The detailed sections further down
> are kept as the evidence trail; the table below is the current truth.

---

## Status at a glance

| # | Item | Status | Owner |
|---|------|--------|-------|
| 1 | Car/building heading (`ScaledLocal`) | ✅ done `7691add` | — |
| 2 | Interactive auto-quit gate | ✅ done `a56954f` | — |
| 3 | 4× MSAA | ✅ done `b8bac86` | — |
| 4 | Peds predictive-DR (LOCAL path) | ✅ done `cfd10bf` | — |
| 5 | DDS producer ran sim 5× real-time | ✅ done `becc224` (wall accumulator) | — |
| 6 | City3D playout delay 0.4 → field + live slider | ✅ done `becc224` (default 1.0) | — |
| 9 | DDS remote peds not interpolated | ✅ done `becc224` (query at continuous render time) | — |
| 11 | `--replay`/`--shot` path resolution | ✅ done `becc224` | — |
| 12 | Unity-editor camera controls + top-down degeneracy | ✅ done `5514599` (this session) | — |
| 13 | Infinite ground grid | ✅ done `7a9a7d9` (this session) | — |
| 14 | High-refresh render judder (60fps cap on 240Hz) | ✅ done `5adf4f9` (this session, uncap) | — |
| 10 | Ped "dance" at junctions | ✅ done `996805f` — VERIFIED this session (no dance at 10× crowd) | — |
| 16 | TL poles rendered outside the cropped road net | ✅ done `6edbad8` (this session, crop TL to road box) | — |
| **7** | **DDS cruise stutter (render-clock HOLD)** | ✅ **VERIFIED FIXED** `bfbf7c9` (dev) — tick-forward-every-frame + rate low-pass; GPU-confirmed this session (no stutter over DDS) | — |
| **8** | **DDS stopped-car backward creep** | 🐛 **REGRESSED by `bfbf7c9`** — accel-aware arc blend *introduces* backward creep the old chord lerp didn't have; GPU-confirmed still visible + hard A/B below | **dev (DrClock, revisit)** |
| **15** | **Live-city junction GRIDLOCK (cars wait on green, never clear)** | 🐛 **OPEN** — engine RoW/discharge + no teleport | **dev/engine** |
| 17 | Viewer GC stalls at high ped counts | ⚠️ PARTIAL — `Sample()`-per-frame fixed this session; per-`Step` publish residual | dev (Sim.LiveCity publish) |

Details on the open items are in their sections below. The ✅ sections are retained as evidence.

---

## ✅ RESOLVED by `becc224` (evidence trail — #5, #6, #9, #11)
*These were provided as local test-box fixes and the dev session reimplemented them properly in
`becc224`; the local duplicates have been dropped. Kept below for the measured rationale.*

### 5. DDS producer runs the sim at `hz×Dt`× real-time → cars 5× fast + jumpy
`Sim.Host.App` (`Program.cs`, `--live-city` loop) did exactly one `sim.Step()` per publish, paced to
`1/hz` wall; each step advances LiveCitySim by `cfg.Dt` (0.5 s). So `--hz 10 → 10×0.5 = 5 sim-s per
wall-s`. The subscriber (`DdsSubscriber.cs:246-247`) also times the DR clock off the wall
`SourceTimestamp`, not the wire `f.Time` — so it replayed 5× fast in a snap-on-correction sawtooth.
*(Confirmed: CARTRACE Δz ≈ 0.3 m then +7.9 m snaps at `--hz 10`; clean 0.28 m/frame at `--hz 2`.)*
**Fix applied here:** advance the sim on a **wall accumulator** (step in `cfg.Dt` increments to catch
up to real elapsed) instead of one step per publish → 1× real-time at ANY `--hz` (verified 0.28
m/frame smooth at `--hz 10`). Interim alternative with no code: run `--hz 2`. A subscriber-side
`f.Time` (not `SourceTimestamp`) change is also correct but insufficient alone.

### 6. City3D playout delay was a fixed 0.4 s → made live-adjustable (slider, default 1.0)
The 2D viewer defaults to 1.0 s; City3D hardcoded `PlayoutDelaySeconds = 0.4`. On the adaptive
per-vehicle DDS feed, 0.4 s is often < a car's packet gap → extrapolate-then-correct back-jitter.
**Fix applied here:** `PlayoutDelaySeconds` const→field (default 1.0) + an HSlider (0–2 s) in the rate
panel, and `BuildRateControlUi` is now also called from `ReadyLiveCityRemote` (it wasn't). Delay 1.0
makes cars "way better" per testing. Recommend upstreaming the slider (useful tuning knob) and the
higher default.

---

## 🐛 Open — confirmed root cause, needs your fix

### 7. DDS constant-speed cruise stutter = one-frame render-clock HOLD (DR core)
**Measured, not theorised.** A velocity-jerk trace over 1187 remote frames @ `--hz 10`, delay 1.0:
**11 jerks, ALL packet-correlated (`wireChanged=True`, 0 without a packet)**, each a ONE-FRAME
progress drop (~0.28 → ~0.18 m) on **constant-speed** cars (spd 12.17 — an acceleration/interpolation
theory was tested and **REFUTED**). Mechanism in `src/Sim.Viewer.Motion/DrClock.cs` `Pump`: the render
clock is **forward-only** — when its estimate dips below the advanced `_renderSim` it HOLDS for a
frame (`:224-227`, `_backSteps++`); the estimate `est = max(clockNow, _latestSim)` (`:211`) uses
`_simRate`, **re-fit on every packet** (`:176-180`). On the stepped ~10 Hz aggregate feed each packet
nudges the fit so `est` momentarily dips below `_renderSim` → one-frame freeze → stutter. **The
playout-delay knob cannot fix this.** Suggested fix: smooth the render-clock rate (low-pass `_simRate`,
or advance by `frameDt*_simRate` and only gently pull toward `est`) so a per-packet re-fit can't freeze
a frame. Shared `Sim.Viewer.Motion` (2D + City3D + IG) — care re parity/byte-identity.

> **✅ VERIFIED FIXED `bfbf7c9` (GPU, this session).** The dev's fix low-passes `_simRate`
> (`SimRateBlend=0.25`) and ticks `_renderSim` forward every frame by the smoothed rate first, then
> pulls the residual gap in by a bounded fraction (`CatchUpFraction=0.35`) instead of holding. Over DDS
> `--hz 10` the constant-speed cruise stutter is **gone** (user-confirmed, no stutter). Isolation A/B
> (below) also confirms the #7 render-clock change is *not* the source of any backward motion.

### 8. Stopped/crawling cars creep BACKWARD — 🐛 REGRESSED by `bfbf7c9` (DR core)
Back-step trace: all back-steps came from a car at spd ~0.07 m/s drifting back ~0.03–0.07 m/frame,
**mostly `wireChanged=False`** (no packet) → it's the reconstruction extrapolating a decel-to-stop car
in reverse (`DrClock.cs ExtrapolateArc` and/or `KinematicReconstructor` damping settle), not a
correction snap.

> **🐛 `bfbf7c9` did NOT fix this — it makes it worse.** GPU-confirmed still visible this session, and
> a **controlled same-machine A/B** using the dev's own `DiagBug8Probe` (real SUMO `09-traffic-light`,
> decel-to-stop, delay=1.0) shows the fix *introduces* backward motion that the old code did not have:
>
> | Build | arc back-steps | world back-steps |
> |---|---|---|
> | **pre-fix** (`bfbf7c9~1`) | **0** (5 runs) | **0** (5 runs) |
> | **post-fix** (`bfbf7c9`) | **1–2** (~0.31 m one-shot at the stop instant) | **~10** (~0.008 m/frame ≈ 5 cm creep) |
> | **#7 kept, only #8 hunk reverted** | **0** (4 runs) | **0** (4 runs) |
>
> **Note `DiagBug8Probe` is `Assert.True(true)` — a diagnostic, not an assertion; its green in the
> `bfbf7c9` test list proves nothing.** The numbers above are from `verbosity=detailed` output. The
> "before" numbers in `bfbf7c9`'s message (0.68 m / 0.03 m/frame / 27 events) came from a *different
> synthetic* setup; on the committed real-SUMO probe the fix regresses.
>
> **Root cause (isolated to the #8 same-lane hunk — reverting just those 3 lines → clean 0/0):** the new
> branch blends `arcFromB = ExtrapolateArc(b.Pos, b.Speed, b.Accel, dtB<0)`, and `ExtrapolateArc` leaves
> the **backward-time (`dtB≤0`) case raw/unclamped** on the stated assumption "the parabola is monotonic
> the correct way." That breaks in exactly the decel-to-stop regime this targeted: with `b.Speed≈0` but
> `b.Accel≈−3.5`, the raw `0.5·accel·dtB²` term is negative and non-monotonic → the blend slides
> backward. The old chord lerp never referenced `b`'s stale decel, so it stayed monotonic and never
> reversed. **Suggested fix for the dev:** clamp the backward-time `ExtrapolateArc` branch the same way
> as forward (freeze at the stop point when projected speed crosses 0), or gate the arc-blend to the
> `accel≈0` / above-crawl-speed case and keep the chord lerp for the decel-to-stop regime.

### 9. DDS peds not interpolated on the remote path (ped session)
Remote peds step ~2×/s. Local/replay query the ped reconstructor at a **continuous** time
(`sim.Time + accumulator`, the `cfd10bf` fix), but the remote path (`Main.cs` `ProcessLiveCityRemote`)
queries the **discrete** wire `LatestCrowdTime` → stepwise, exactly what `cfd10bf`'s own note warns
about. The remote path needs the same continuous-time query.

### 10. Ped "dance" at junctions — ✅ FIXED `996805f` (corner-blend the lateral weave axis; verified no dance at 10× crowd)
Approaching a turn waypoint a ped pauses + moves backward a little, then commits to the new direction
and is smooth after. Suspected: predictive-DR extrapolates past the waypoint assuming continued
straight motion, then corrects when heading flips. Not yet traced.

### 11. `--replay` path is resolved relative to Godot's `--path`
Bootstrap doc shows `--replay out\lc.simrec`, but Godot resolves it against `--path demos\City3D\Viewer`,
so it must be an **absolute** path (or doc/flag should normalise it). Same quirk applies to `--shot`
output.

### 15. Live-city junction GRIDLOCK — cars sit at junctions ON GREEN and never clear (engine)
After the demo runs a while, cars pile up at junctions and **stop — including on a GREEN light, with
no visible reason** — and it does not recover to free flow.

**MEASURED (headless smoke, per-step stopped-fraction = cars moving < 0.1 m/s):** the stall is
**independent of car count** — the tell-tale that it is NOT saturation/spillback:

| t (s) | stoppedFrac @ `LIVECITY_CARS=70` (~64 live) | stoppedFrac @ 160 (~140 live) |
|------:|:--:|:--:|
| 20 | 0.09 (free flow, ~11 m/s) | 0.09 (~11 m/s) |
| 40 | 0.59 | 0.45 |
| 80 | 0.89 | 0.83 |
| 120 | 0.83 | 0.71 |
| 160 | 0.97 (0.5 m/s) | 0.91 (0.75 m/s) |

Both car counts free-flow for ~20 s then collapse to **80–97% stopped by ~80–160 s**, with only
partial recoveries — the **same curve at 70 as at 160**. So lowering `LIVECITY_CARS` does not help;
64 cars in an 840 m grid is genuinely low density.

**Arrival-throughput trace (rules out "cars ran out of destinations / arrived-cars pile up"):** the
engine DOES remove vehicles on arrival (`CommandBuffer` `Arrived`/Destroy + `SimEventKind.Arrived`),
and LiveCitySim refills to the cap, so arrived cars can't accumulate. Counting actual arrivals: over
**200 s with ~64 live cars, total arrivals = 16** (~8% of the hundreds you'd expect), and **~0
arrivals for the entire first 100 s — even while cars were moving at 11.7 m/s** (t=20 s: 91% moving,
0 arrivals). So cars **have** destinations but **almost never reach them** — this is **near-zero
junction throughput**, not an arrival/despawn pile-up. Because trips never complete, cars fill to the
cap and stall.

This points to a **junction discharge / right-of-way bug** (cars can't get *through* junctions — a
foe-check that over-yields, a symmetric right-before-left deadlock, or turn-lane mis-segregation),
visible even during early free-flow. With **no teleport ported** (`grep teleport` = 0 hits in the C#
engine source; phase-1 keeps it off for determinism) nothing ever breaks the jam, so it's terminal.
It matches the **`dense-lane-overlap-fix-5tr4ha`** "stem-through under-discharge" theme directly.

Not a viewer bug — it's `Sim.Core` engine behavior. Dedicated fix/diagnosis branches already exist
(NOT merged into the live-city line); strongest candidates:
- **`claude/dense-lane-overlap-fix-5tr4ha`** — junction/signalized **discharge deficit under density**
  (the "calibration knee"). Landed parity-safe fixes: **`f69a58d`** permissive/minor-crossing yield
  (`MSLink::blockedByFoe` port — over-yield = "waiting on green"), **`ca8d515`** arriving-vehicle
  red-light-brake / signalized-discharge redistribution. Dominant unresolved cause: **turn-lane
  mis-segregation** (`9a77d3b`/`ad8d738`, WIP/partly reverted) — cars not sorting into the correct
  turn lane, stalling at the junction.
- **`claude/c4vii-willpass-gridlock-lktroh`** — **`996493d`** deterministic **right-before-left
  deadlock break** (symmetric conflict cycle → nobody moves; `e7a76f0` diagnoses it). parity-reviewer
  ACCEPT.
- **`claude/sumosharp-junction-row-issue2`** / **`claude/permissive-yield-crossing-wip`** — related
  "blanket crossing-yield vs arrival-time RoW" refinements.

**Recommendation:** integrate the landed pieces (`f69a58d`, `ca8d515`, `996493d`) into the live-city
line and re-test the demo; the turn-lane-segregation work is the deeper unfinished item. All are
parity-gated `Sim.Core` changes — engine session's call. (Interim demo mitigation: lower
`LIVECITY_CARS`, but if it locks at low density that only delays it, not a cure.)

### 17. Viewer GC stalls at high ped counts — ⚠️ PARTIALLY FIXED (per-frame ped-list allocation)
Cranking `LIVECITY_PEDS` to 16000 (100× the tuned 160) makes the viewer hitch **~1 s smooth / ~1 s
hang, periodically** — the classic GC signature (a CPU-bound O(n) cost would be a *steady* slowdown).
Measured (GC counters, ~14k peds): ~20–40 MB per 30 frames, gen2 climbing 9→14 in ~10 s, worst frame
109–148 ms coinciding with gen2 bumps → allocation pressure, not CPU. The low-power ped SIM path is
cheap (design targets ~90k); it's the CONSUMER side allocating per frame.

**Localised by bracketed `GetTotalAllocatedBytes`** (sim-tick / car-recon / Sample / ped-recon):
the per-frame allocation was **100% in `LiveCitySource.Sample()`** (301→631 KB/frame, scaling with
ped count) — NOT the ped reconstruction (0 KB; `PedReconstructor` reuses a scratch list, `PoseSample`
is a struct, `UpdatePeds` is grow-only structs). The viewer calls `Sample()` **every frame just for
the car Handle→Name table (`snap.Cars`)**, but `Sample()` also materialises the entire N-ped list,
which is thrown away.

**Fix applied here (`SampleCars`):** `LiveCitySim.SampleCars()` returns cars only, into a REUSED
buffer; `LiveCitySource` passes it through; the viewer's live path uses it instead of `Sample()`.
Verified: per-frame ped-list allocation gone → total alloc ~halved, gen2 collections ~3× fewer.

**Residual (still OPEN):** worst-frame ~148 ms spikes remain at 16k — a SECOND allocator my per-frame
bracket under-sampled: the **per-`Step` ped publish** (`LiveCitySim.Step` serialises all N peds to the
wire every sim tick, ~0.5 s), which allocates in bursts. Deeper (`Sim.LiveCity`/`PedReplicationPublisher`,
shared with the DDS wire path) and partly inherent to 100×-tuned density. Recommend the dev/engine
session pool the publish buffers; at moderate `LIVECITY_PEDS` the viewer is now smooth.

### 16. TL poles rendered outside the cropped road net — ✅ FIXED `6edbad8` (CropTlLaneHandles: same crop rule as roads)
Roads render **cropped** to the downtown block (`BuildRoadMeshesCropped`, `LiveCityConfig` X0..Y1 =
2055..2895, +60 m margin) for legibility/build-cost. But signal heads are placed for **all**
TL-controlled lanes uncropped (`TrafficLightPlacer.Place(network, tlStateByLane.Keys)`), so TL poles
appear beyond the rendered asphalt. Fix = crop the TL lane handles to the same box before placing.
Demo-side viewer change; the testing session can do it locally.

---

## Not bugs (verified good after the pull)
Peds very smooth on the LOCAL path (predictive-DR). Cars smooth locally (tiny residual render-lag jerk,
acceptable). Heading correct out-of-the-box. MSAA on. Parity gate green throughout (654 passed / 4
skipped) across every pull.

## How things were measured
Frame-by-frame `GD.Print` traces in the viewer (all stripped again): fastest-car world-pos/frame
(speed & snap detection), back-step detector (displacement · heading < 0), velocity-jerk detector with
packet-arrival correlation, and DR extrapolation counts. Producer real-time verified via its own
`t` vs wall-elapsed. Happy to hand over the exact probes if useful.
