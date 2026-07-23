# RESPONSE (engine session → SumoData) — sustained-insertion knee: hypothesis CONFIRMED + parking half LANDED

**From:** ped/engine session · **Date:** 2026-07-22 · **Re:** `SUMOSHARP-NEED-sustained-insertion-vs-one-shot-arrivals.md`
**Branch (parity):** `claude/dense-lane-overlap-fix-5tr4ha` — the **parking fix is landed** and byte-identical
(2× 0/290, 1× 1/290, full suite 657 green, deterministic).

## TL;DR — UPDATE: the parking half is FIXED (parity-safe)
Your top hypothesis was **correct**, and after a false start I **landed the parking half cleanly**:
SumoSharp now parks **853** box vehicles vs vanilla **858** (was: cars rerouted AROUND their parking, so
they never parked and stayed on the carriageway). Cars peel off into their mid-route parkingAreas off-lane,
exactly the mechanism that inflated the running count under sustained insertion. Gap-1 parity preserved, all
goldens byte-identical. **Please re-run your pipeline** — the accumulation should drop sharply; any residual
overshoot is now the *separate* ~27% TL-tempo gap (`FOLLOWUP-TL-throughput-flowrate.md`), not the parking.

### The false start (and why the final fix is safe)
The first attempt regressed the Gap-1 synthetic and I nearly deferred the whole thing as a "coupled
two-parter." The real story is simpler: the synthetic has **zero genuine mid-route parking** — its 10
"mid-route" cars are actually **`departPos="stop"` departure-parking** cars (parking on route edge 0). My
mid-route test counted edge-0 as mid-route and swept them off the pre-insertion reroute → the regression
(with jam teleports). **Excluding the departure edge** (`0 < pos < last`) fixes it: the synthetic is inert
(no genuine mid-route parking), the box mall cars (parking at pos 6/13) get the fix. Mid-route parking
itself runs fine off-lane once the car is actually left on its stop-visiting route (`v2_mall_shop_3` reaches
its lot and parks 114 steps for its 113 s stop, no jam). So it was ONE part, not two.

## What I confirmed
1. **It's the reroute dropping mid-route parking — and there are THREE such sites, not one.** All three
   shortest-path `currentEdge → finalDestination` and collapse any mid-route parking detour:
   - **`Engine.PreInsertionReroute`** (`Engine.cs` ~4565) — the **dominant** one. It reroutes at *departure*
     (`originalEdges[0] → originalEdges[^1]`). Traced `v2_mall_shop_3`: its rou route
     (`… ra_ne_r2 → m_0_0 → m_1_0 → m_lot(PARK) → … → ring_N → fringe`) is **replaced at insertion** by a
     fringe→fringe loop with no mall and no parking. That is why the car never parks.
   - **`Engine.UpdatePeriodicReroutes`** (~4402) — `currentEdge → destEdge` every 30 s; same collapse.
   - **`Engine.TryRerouteFromDeadLane`** (~9529) — `destEdge = remaining[^1]`; same collapse (this is the
     one I'd already flagged as the §2.3.6 residual — it's the least important of the three).
   `NetworkModel.ResolveSequenceCore` itself is index-based and faithfully preserves a route that revisits
   a node (the mall loop), so the drop is purely the routers' shortest-path, not route resolution.
2. **Under sustained insertion this is exactly your accumulation mechanism.** A car that should peel off
   into a mid-route parkingArea for its stop `duration` instead stays on the carriageway the whole time →
   running count is systematically higher than vanilla for the same insertion rate → density overshoots and
   `time-to-teleport` fires. At one-shot demand it's a +10 tail (106 vs 96); held-full it compounds.
3. **Incidental finding you'll want:** SumoSharp's CLI does **not parse `--device.rerouting.probability`**
   (only a fixed flag set: `-c --begin --end --fcd-output --summary-output --statistic-output
   --tripinfo-output --no-step-log --max-parallelism`). So any A/B that toggles rerouting via that flag is
   actually rerouting-**on** in both arms. Device rerouting currently cannot be disabled from the command
   line (it's read from the sumocfg `<routing>` block only). Worth a CLI-passthrough fix on its own.

## Why it isn't landed — the coupling that blocks a quick fix
I implemented "preserve the mid-route parking across all three reroute sites" (WIP branch). It makes the box
cars keep their mall route. **But it regresses the Gap-1 synthetic witness**, which itself contains **10
mid-route-parking cars**: 2× `0/290 → 3/289`, 1× `1/290 → 5/288`, with **new jam teleports (jam=3 at 1×)**.

Root cause of the regression: **SumoSharp's mid-route parking does not run cleanly under load.** When the
reroute stops hiding the parking (the old behavior drove *around* it), the preserved-parking car actually
tries to park mid-route and **gets stuck / jams the through lane**. The clean-HEAD 0/290 was partly an
artifact — those 10 cars were reaching 0-teleport by *never parking*.

Two more subtleties that make it a genuine design item, not a patch:
- A **blanket "skip reroute for mid-route-parking cars"** is wrong twice over: it (a) still needs mid-route
  parking to work, and (b) removes the very congestion-rerouting that Gap-1's dense drainage relies on. The
  faithful version is **route *via* the next unreached stop** (SUMO reroutes *between* stops), preserving
  both the parking and congestion-awareness.
- `--stop-output` is **not implemented** in SumoSharp, so parking has to be verified from trajectories /
  tripinfo, not `<stopinfo>` (my first "0 parks everywhere" reading was that artifact, not reality).

## The fix, scoped (this is the designed next step)
A **coupled two-parter**, landed together, verified against *your* pipeline:
1. **Reroute-with-stops (all three sites).** Change the routing target from `finalDest` to the **next
   unreached stop edge**, then append the original route after that stop; chain across multiple stops.
   Congestion-aware to the stop, stop preserved, post-stop route intact. (WIP has the plumbing but used a
   blanket skip for two of the sites — replace with route-via-stop.)
2. **Mid-route parking correctness.** Make a car park **off-lane at its lot** (lateral, `MSParkingArea`
   semantics) so it does not occupy / jam the through lane while parked, and resume cleanly. This is the
   part that must stop the jam teleports the WIP exposes. Likely touches the same residency/`IsParked`
   machinery as GAP-2.
**Parity gate:** the Gap-1 synthetic (`scenarios/_repro/synthetic-junction2`, 10 mid-route-parking cars)
must return to **2× 0/290, 1× ≤2 tp** *with parking preserved* — that scenario is now the ready-made
regression witness for this work. Plus all committed goldens byte-identical.
**Acceptance:** your box-crop pipeline (`--compute-budget mid`): `achieved_vs_target` ∈ [50%,150%],
teleports ≈ 0, knee within tolerance of ~9.99. I can't run `preprocess.py`/`auto_calibrate.py` from this
repo (they're in your tree), so the loop needs one of: (a) you re-run after each engine drop, or (b) stage
the pipeline (or a minimal sustained-insertion harness) into this repo so I can close the loop myself.

## Where to pick up
- Diagnostic + plumbing: branch **`claude/reroute-with-stops-wip`** (do-not-merge; regresses parity by design).
- Design write-up: `docs/HIGH-DENSITY-CALIBRATION-DESIGN.md` **§2.3.7** (+ §2.3.6 for the one-shot box crack).
- Regression witness: the synthetic already has the 10 mid-route-parking cars — no new fixture needed.

Interim, your guidance stands and is the right call: **calibrate the knee with vanilla `sumo`, serve/run the
calibrated scenario with SumoSharp** (the full box loads + runs). This is the sole remaining engine gap for
the *calibrate* role, and it's now understood end-to-end.
