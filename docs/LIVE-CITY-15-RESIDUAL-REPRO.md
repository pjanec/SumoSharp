# Issue #15 residual — headless reproduction spec (for a standalone session)

> **For a fresh/standalone headless session** picking up the **remaining** junction-gridlock deficit in
> the live-city scene, *after* the dense-flow engine merge (`184fb31`) already turned the **terminal
> deadlock** into **jam-and-recover**. The merge helped but did **not** cure #15: on the GPU the jams
> still look **unrealistic** (verdict from the Windows GPU testing session, 2026-07-23). This doc is the
> repro + the exact symptom to chase + the metric to A/B against. Companion to
> `docs/LIVE-CITY-HARNESS-GUIDE.md` (general harness orientation) and the merged dense-flow design docs
> (see "Prior art" below).

## TL;DR — what to reproduce
The live-city downtown scene, run headless, still bogs down at junctions: **cars stop at junctions
*on green* and, while stuck, "float" — slow lateral micro-movements (lane-change churn) instead of
discharging through.** GPU verdict (2026-07-23): **almost the whole city gets stuck unnecessarily —
cars waiting on green lights, only a few still running — and it happens on every run** (a very clear,
reliable case; the *jam* always reproduces even if the exact set of stuck cars may vary). It clears
eventually (no longer *terminal* lock) but throughput is far below free flow.
Strongly suspected: **turn-lane segregation is incomplete** (cars can't get into the correct turn lane,
so they sit and jockey laterally) — the dense-flow branch's own **unfinished** work — compounded by
**no teleport ported** (phase-1), so nothing forcibly breaks a stubborn jam.

## Branch / state
- Branch `claude/city3d-live-city-mode-3yf4oc`, tip must include **`184fb31` "merge(#15): integrate
  dense-flow engine fixes"** (wholesale overlay of the validated engine work from
  `claude/dense-lane-overlap-fix-5tr4ha`: permissive-yield `MSLink::blockedByFoe`, signalized
  discharge, dead-lane reroute, parking/departPos gaps).
- **Parity gate is GREEN and must stay green:** `dotnet test tests/Sim.ParityTests -c Release` →
  **657 passed / 4 skipped**, all prior goldens byte-identical. Any turn-lane fix that moves a golden
  out of `tolerance.json` is reverted or gated behind an explicit fast-mode flag (CLAUDE.md iron law).
- **Scene/dataset:** `scenarios/_ped/demo_city/box/` (committed: `net.xml` + `buildings.json` +
  `pois.json`). Downtown crop `[2055,2055]–[2895,2895]` (~840 m) is where cars spawn/render.

## The repro command (this IS the reproduction — no GPU needed)
```bash
dotnet run --project src/Sim.Viewer -c Release -- --mode live-city --smoke --frames 400
```
Prints a per-40-step `LIVECITY-GRIDLOCK:` trace (added in `dead369`; pure read-back off
`Sample()`/`ArrivedTotal`, never perturbs the sim). **The jam reliably reproduces on every run** (it is
the dominant behaviour, not a rare tail); the engine uses seeded per-entity RNG so a fixed headless run
should be repeatable, but **do not assume the exact set of stuck vehicles is identical run-to-run** —
chase the *signature* below, not specific ids.

### Expected post-merge signature (measured on the Windows box, cap 160, 2026-07-23)
```
LIVECITY-GRIDLOCK: t(s) liveCars stoppedFrac meanSpd aggMove arrivals peds
                    20     129     0.01     10.27   13000      0    160
                    80     137     0.61      4.43   12256      4    160
                   120     138     0.43      7.15   19703     15    160
                   160     139     0.44      6.62   18178     47    160
                   180     139     0.73      3.31    9073     56    160
                   200     126     0.38      6.93   18095     81    160
```
- **The good news the merge bought:** `stoppedFrac` no longer marches monotonically to ~0.95 — it
  **oscillates 0.38–0.73 and recovers**; `arrivals` reach **~81 @200s** (was **38** pre-merge, `dead369`).
- **The residual to kill:** `stoppedFrac` still spikes to **0.6–0.7**, `meanSpd` dips to ~3.3 m/s. That
  is the jamming that reads as unrealistic on the GPU.
- **It is NOT the ped crossing-yield gate and NOT car-count:** `LIVECITY_YIELD=0` is ~identical
  (0.41–0.62, arrivals 80); `LIVECITY_CARS=70` still jams (0.47–0.68, arrivals 36). So the residual is a
  **Sim.Core junction/lane-selection property**, not saturation or the ped gate. Env knobs resolved in
  `LiveCityConfig.ForRepoRoot`: `LIVECITY_CARS`, `LIVECITY_PEDS`, `LIVECITY_YIELD=0`, `LIVECITY_LCMIN`,
  `LIVECITY_HZ`.

## The specific symptom to witness (the smoking gun)
From the GPU (local in-process live-city viewer, `--live-city --camera=close`): **many** cars stuck at
junctions *while the light is green*, each doing slow lateral drift (looks like a perpetual, never-
committed lane change) rather than moving forward. Viewer-side ids seen stuck: **96, 131, 89, 82, 45,
159** — treat these as *examples of the failure*, not fixed targets: the jam is reliable but the exact
stuck set may vary, and viewer ids are not guaranteed to equal engine handles/names.

`LiveCityCar` (`Sim.LiveCity/LiveCitySnapshot.cs`) only exposes `Handle/X/Y/Z/AngleDeg/dims/Name` — no
lane/pos/posLat/speed — so the gridlock probe's `stoppedFrac` (Δ world-XY < 0.05 m) already counts
these floaters as "stopped," but does **not** reveal *why*. To confirm the turn-lane hypothesis, add a
**throwaway** per-vehicle witness (same pattern as the existing probe, in `RunLiveCitySmoke`,
`src/Sim.Viewer/Program.cs`):

1. Flag a car "stuck" when its **net** world displacement over ~10 steps is < ~0.5 m **but** its
   per-step displacement is nonzero (i.e. moving-but-not-progressing = the lateral float).
2. For each stuck car, dump the **engine-authoritative** state — lane id, longitudinal `pos`, `posLat`,
   `speed`, and the **controlling TL phase** for that lane. `LiveCityCar` can't give this; reach into
   `LiveCitySim`'s `_engine` (`Sim.Core.Engine` / `EngineSnapshot`) the way SimSource's authoritative
   snapshot does (`TryGetVehicle` → `LaneId/Pos/PosLat/Speed`; cf. the `--trace-veh`/`AUTHTRACE` hook at
   `Program.cs:~1652`, which is wired for `--scenario` not live-city — port the same read).
3. **Confirming signature:** `speed≈0`, longitudinal `pos` **not advancing**, `posLat` **oscillating**,
   TL phase **green**, and **no leader** within the car-following gap ahead → cars that *could* go but
   are stuck jockeying for a lane = turn-lane segregation failure.

## The metric to A/B any fix against
Keep the `LIVECITY-GRIDLOCK:` probe as the objective gauge, engine swapped underneath (exactly how the
merge itself was validated). A real fix should:
- drive **peak `stoppedFrac` well below ~0.6** (ideally free-flow ~0.1–0.2 sustained), and
- push **`arrivals@200s` substantially past 81** toward the hundreds a free-flowing 840 m grid implies,
- while keeping **`Sim.ParityTests` 657/4 green** and the bench hash unchanged.

## Prior art already on this branch (read these first)
- **`docs/GETBESTLANES-RESUME.md`** — self-contained plan for the **turn-lane segregation** fix (SUMO
  `getBestLanes` / keep-right `stayOnBest` rule 2). This is the prime suspect; the port is **WIP**
  (`9a77d3b` keep-right stayOnBest rule 2 pending review; `ad8d738` LaneQ occupation design).
- **`docs/DENSE-FLOW-THROUGHPUT-DIAGNOSIS.md`** — the diagnosis that localized the deficit to
  **stem-through under-discharge via turn-lane mis-segregation** (not gap-acceptance): see the C1
  witness commits `4633996`/`9343249` (keep-right drift ~15% minor; dominant = low stem-through
  discharge / `crossJxnLeader` regression).
- **`docs/DISCHARGE-YIELD-RESUME.md`** — permissive/minor-crossing yield (`blockedByFoe`), already
  landed (`f69a58d`), context for the discharge model.
- **`docs/ISSUE2-JUNCTION-TELEPORT-DESIGN.md`** / **`ISSUE2-JUNCTION-KEEPCLEAR-DESIGN.md`** — the
  no-teleport gap (why a stubborn jam never breaks) and junction keep-clear.
- **`docs/DENSE-ENGINE-INTEGRATION-{DESIGN,TASKS,TRACKER}.md`** — how the `184fb31` merge was scoped.

## Direct SUMO cross-check (network-enabled; the parity anchor)
SUMO 1.20.0 is available (`sumo --version`). Run SUMO on the same `net.xml` + a comparable demand and
diff **tripinfo/summary** (arrivals, mean speed, timeLoss) against the engine — SUMO discharges these
junctions, so the arrivals/speed gap localizes the missing discharge/lane-selection behavior. Standard
engine-vs-SUMO diagnosis per CLAUDE.md. **The offline `dotnet test` loop must never invoke SUMO** — this
cross-check is an investigation step, not part of the test loop.

## Key files
- `src/Sim.Viewer/Program.cs` — `RunLiveCitySmoke` (the `LIVECITY-GRIDLOCK` probe; extend here for the
  per-vehicle witness).
- `src/Sim.LiveCity/LiveCitySim.cs` — `Step()` (spawn → ped demand → crossing gate → `Engine.Step`),
  `ArrivedTotal`, `_engine`; `LiveCityConfig.cs` — crop/caps/env knobs.
- `src/Sim.Core/` — `Engine.cs`, `VehicleRuntime.cs`, `LaneNeighborQuery.cs`,
  `ActuatedTrafficLightLogic.cs`, `EngineSnapshot.cs`, `CommandBuffer.cs` (arrival = `Arrived`/Destroy;
  **no teleport implemented**).
