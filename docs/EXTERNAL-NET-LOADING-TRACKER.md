# External georeferenced net loading + pedestrian elevation — TRACKER

Checklist for `docs/EXTERNAL-NET-LOADING-TASKS.md` (design: `docs/EXTERNAL-NET-LOADING-DESIGN.md`;
requirements: `docs/EXTERNAL-NET-LOADING-HANDOFF.md`).

A box is ticked **only** after Opus confirms the task's success conditions first-hand — diff read, tests
read for non-vacuity, gate re-run — never on an implementor's summary (CLAUDE.md orchestration loop).

**Standing gate, measured on this branch at `791d3e6` before any change:**

| Gate | Baseline |
|---|---|
| `dotnet test Traffic.sln -c Release` | **775 passed / 0 failed / 4 skipped** |
| `dotnet run --project src/Sim.Bench -c Release` | **`hashA = hashPar = BF3794A4704BCD79`**, par == single |
| `dotnet test tests/Sim.LiveCity.Tests -c Release` | *to capture at T-B1 (not in `Traffic.sln`)* |

Toolchain (ephemeral — re-provision each session): dotnet SDK **8.0.129** (apt `dotnet-sdk-8.0`);
`sumo`/`netconvert` **1.20.0** (`pip install eclipse-sumo==1.20.0`, matches `SUMO_VERSION`, shadows apt 1.18.0).

---

## Status: design agreed, implementation not started

## Stage A — Validation data
- [ ] **A1** synthetic georeferenced 3-D ped-net fixture `scenarios/_ped/roadnet_geo3d/` (6 SCs; SC4 is the §8/R1 measurement)
- [ ] **A2** fixture reachable from the test projects via the existing repo-root helper, no absolute paths

## Stage B — Change 1: net/route path resolution
- [ ] **B1** `LiveCityConfig.NetPath`/`RoutePath` + ctor resolution across **all three** net consumers (SC3 is the non-vacuous one)
- [ ] **B2** `LiveCityConfig.ForSumocfg` — relative + absolute + no-`<input>` cases, reflection drift-guard vs `ForDataset`

## Stage C — Change 2: pedestrian elevation
- [ ] **C1** `IPedElevationSource` seam in `Sim.Pedestrians` (both TFMs; **no** new project reference)
- [ ] **C2** `NetPedElevationSource` in `Sim.LiveCity` (analytic 4.0/2.0 m checks, determinism, ring cap)
- [ ] **C3** `PedRemoteReconstructor` 5-out-param overload (15 existing call sites unedited)
- [ ] **C4** `LiveCitySim.PedElevation` + real Z in `Sample()` (3-D non-zero; 2-D bitwise `0.0`; ped↔car ≤ 2 m; frame cost < 5%)

## Stage D — Change 3: live pedestrian density knobs
- [ ] **D1** mirror `PedPopulationCap`/`PedSpawnRatePerSecond` into `PedDemandConfig` each `Step()` (SC1: **fails before, passes after**)

## Stage E — Scale proxy & close-out
- [ ] **E1** large-net load proxy script (ephemeral net, not committed) — honest proxy for the 168 MB Swiss net
- [ ] **E2** close-out: final gate quoted, BIG-side handoff-back, deferred items named

---

## Measurements to record here as they are taken

These are the numbers the design says must be measured rather than assumed. Fill in with the actual
figures — an empty row is not a pass.

| # | Question | Design ref | Result |
|---|---|---|---|
| M1 | Do netconvert-generated `crossing`/`walkingarea` lanes carry `ShapeZ` on a 3-D net? Counts per category. | §8/R1, A1·SC4 | *pending* |
| M2 | Which §3.2 lane-set branch does the A1 fixture select (ped-lanes-only / all-Z-lanes / none)? | §3.2, C2·SC6 | *pending* |
| M3 | Max \|pedZ − carZ\| for ped/car pairs within 10 m on the A1 fixture. | §3.5(b), C4·SC2 | *pending* |
| M4 | `Sample()` cost with elevation on vs. off at ≥300 peds (both absolute numbers, ≥5 repeats). | §8/R3, C4·SC4 | *pending* |
| M5 | Large-net proxy: file size, both parse times, ctor time, peak working set. | §7, E1·SC1 | *pending* |
| M6 | Demo 200-step `PeakCars`/`PeakPeds`/`ArrivedTotal` before vs. after Stages B–D. | B1·SC5, D1·SC6 | *pending* |

---

## Known limits of what this work can verify

Stated up front so the close-out cannot quietly overclaim (design §7):

- **`swiss_roads.net.xml` (168 MB) is not in this repo** and is never loaded here. "Loads the real Swiss
  net" is **BIG-side verification**; E1 provides a generated-net proxy only.
- **A cut Geneva box is not in this repo either.** The A1 fixture reproduces the *properties* the handoff
  documents (UTM32N `projParameter`, non-zero absolute `netOffset`, 3-D lane shapes, guessed
  sidewalks/crossings/walkingareas) — it is not the real cut output.
- Nothing in Stages A–E is a **parity golden**. There is no SUMO trajectory comparison in this work; the
  fixture is an input-only regression fixture, exactly like `scenarios/_ped/roadnet_min`.
