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

## Stage A — Validation data (Tier 2, committed synthetic — design §6.2)
- [ ] **A1** synthetic georeferenced 3-D ped-net fixture `scenarios/_ped/roadnet_geo3d/` (6 SCs; SC4 must reproduce the measured branch-1 property)
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

## Stage E — Real-net validation (Tier 1) & close-out
- [ ] **E1** opt-in `SUMOSHARP_GENEVA_DIR` test gate — **skips** (not passes) when unset
- [ ] **E2** real Geneva cut (44 MB) end-to-end through `ForSumocfg`/`NetPath` + real ped Z
- [ ] **E3** full Switzerland (161 MB) loads — handoff definition-of-done item 1, on the real file
- [ ] **E4** close-out: final gate quoted, BIG-side handoff-back, deferred items named

---

## Measurements

Taken **2026-07-27** on the real dataset (`geneve.7z`, held ephemerally in the session scratchpad —
never committed). Probed with existing public APIs only, no feature code. M1/M2/M5 are therefore
**answered before implementation starts**, which is why the design could be corrected up front.

| # | Question | Design ref | Result |
|---|---|---|---|
| M1 | Do real `crossing`/`walkingarea` lanes carry `ShapeZ`? | §3.2, §8/R1 | **YES — 100 %, both nets.** Geneva: sidewalks 2 201, crossings 221, walkingareas 2 179, **all** with `ShapeZ`, **0** missing from `LanesById`. Switzerland: 13 811 / 735 / 13 537, same — 0 missing, 0 without Z. **R1 closed.** |
| M2 | Which §3.2 lane-set branch do real nets select? | §3.2 | **Branch 1 (ped-lanes-only index)**, both nets. Index size 98 897 segs (Geneva, 30 % of all Z segs) / 860 276 (Switzerland, 52 %). **R2 closed** — ≈14 MB of index vs. the 1.65 GB the parsed net already costs. |
| M5 | Real-net load cost | §7, §8/R6 | Geneva 44 MB: parse 9.2 s + ped 1.2 s + crosswalk 1.3 s = **11.6 s**, 53 229 lanes, **572 MB**. Switzerland 161 MB: parse **67.7 s** + ped 6.5 s (+~5 s) ≈ **80 s**, 175 465 lanes, **1 652 MB**. Ctor makes **four** net passes (not two); pass 1 is ~85 % of cost. |
| M7 | Georeference & elevation, real files | §5 | Both nets: `netOffset="-388091.80,-5257586.90"` **identical**, `projParameter="+proj=utm +zone=32 +ellps=WGS84 …"`. Cut preserves the absolute UTM offset exactly; only `convBoundary` shrinks. Elevation span **199.48–1633.77 m** (CH), **324.39–1062.24 m** (Geneva). |
| M8 | Does `RouteFiles[0]` name a real route file? | §0/C4 | **NO.** `geneve_Medium.sumocfg`'s first entry is `common/vType.config.xml` — 107 `<vType>`, **0 routes**. Real routes are entries 4–5 (600 and 1 000 routes). Design corrected to scrape **all** route files. |
| M3 | Max \|pedZ − carZ\| for pairs within 10 m | §3.5(b), C4·SC2, E2·SC4 | *pending — needs C4* |
| M4 | `Sample()` cost, elevation on vs. off at ≥300 peds | §8/R3, C4·SC4 | *pending — needs C4* |
| M6 | Demo 200-step `PeakCars`/`PeakPeds`/`ArrivedTotal`, before vs. after | B1·SC5, D1·SC6 | *pending — capture before touching `src/`* |

---

## Known limits of what this work can verify

Stated up front so the close-out cannot quietly overclaim:

- **The real dataset is ephemeral and must never be committed** (205 MB of third-party data; CLAUDE.md
  committed-vs-ephemeral split). Tier-1 tests are opt-in via `SUMOSHARP_GENEVA_DIR` and **skip** without
  it. A fresh VM must still be 775/0/4 with neither SUMO nor this data present (design §8/R7).
- **No `preprocess.py` cut and no absolute-path `.sumocfg` are among the received files** — the real
  Geneva configs use *relative* paths. The absolute branch stays covered by a synthesised temp-dir config
  (B2·SC2), not by real output.
- **The ~80 s / 1.65 GB full-Switzerland load is pre-existing** and out of scope here (design §7, §8/R6).
  This work must not make it worse; it is not chartered to make it better.
- Nothing in Stages A–E is a **parity golden**. There is no SUMO trajectory comparison in this work; the
  Tier-2 fixture is an input-only regression fixture, exactly like `scenarios/_ped/roadnet_min`.
