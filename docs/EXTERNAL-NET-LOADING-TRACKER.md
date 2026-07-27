# External georeferenced net loading + pedestrian elevation — TRACKER

Checklist for `docs/EXTERNAL-NET-LOADING-TASKS.md` (design: `docs/EXTERNAL-NET-LOADING-DESIGN.md`;
requirements: `docs/EXTERNAL-NET-LOADING-HANDOFF.md`).

**Consumer-facing API contract: `docs/EXTERNAL-NET-LOADING-API-CONTRACT.md`** — the frozen signatures the
parallel Godot/City3D and BIG sessions code against, with a per-task gating table (§11). If an
implementation task has to change a signature, **update that file and note it here** — consumers are
building against it.

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

## Status: Stages A/B/D ADOPTED from the viewer session · Stage C is ours, not started

**Reconciled 2026-07-27** with the Godot City3D session
(`claude/handoff-docs-implementation-pmdu9z` @ `371339a`), which had already implemented B1, B2, D1 and a
better fixture. Full reply: `docs/handoffs/SYNC-reply-to-viewer-session.md`.

**Verified by me on their branch, first-hand:** parity **775/0/4**, determinism hash
**`BF3794A4704BCD79`** (par == single), `Sim.Pedestrians.Tests` 277/277, and their C2 ped-Z revert is
clean (no `ShapeZ`/`ElevationsAlong`/`PolygonZ`/`out double z` anywhere in `src/Sim.Pedestrians/`). They
had not reported the hash — a `NetworkParser` change can move it; it did not.

**§3.6 decided by the owner (2026-07-27): W1 — z travels on the wire**, under a **new frame kind 5**
(`14 B + 12 B/point`, z on the existing int32-cm quantization), leaving kind 4's layout untouched. The
publisher emits kind 4 whenever there is no z, so 2-D nets stay byte-identical on the wire. Rationale and
the two measured reasons for a new kind rather than a `Version` bump: design §3.6. No open design
questions remain; Stage A can begin.

## Stage A — Validation data — **SUPERSEDED, adopted from the viewer session**
- [x] ~~**A1** synthetic fixture `scenarios/_ped/roadnet_geo3d/`~~ → **replaced by their
  `scenarios/_ped/georef_min`, which is better.** Verified: UTM32N `projParameter`,
  `netOffset="-187497.01,-5046275.45"`, 20 crossings / 24 walkingareas / 195 ped lanes, 3-coord shapes,
  **28 m** elevation span (A1 asked ≥3 m). A real `netconvert --keep-edges.in-boundary` crop (mirrors the
  actual cut pipeline, which my synthetic recipe did not) sitting at ~91850, 73956 (stress-tests float
  precision). It found the `NetworkParser` bug below, which earns its keep.
- [x] ~~**A2** fixture reachable from test projects~~ → done by their `ExternalNetLoadingTests`.

## Stage B — Change 1: net/route path resolution — **ADOPTED from the viewer session**
- [x] **B1** their `LiveCityConfig.NetPath`/`RoutePath`/`RoutePaths` + `public ResolveNetPath()`. Matches
  contract §4's four-step order exactly. **Correction to my own §1.1:** there are **four** net consumers,
  not three — I had missed `_engine.LoadNetwork` (`:370`); all four use the resolved path.
- [x] **B2** their `ForSumocfg` — unions **all** route files (§0/C4). Two follow-ups sent: the
  `RoutePath = routes[^1]` guess is wrong on both real configs (last entry is `personFlows.rou.xml` /
  `vTypeDist.config.xml`), and I **conceded** their throw-on-missing-`<net-file>` over my non-throwing
  B2·SC3.
- [x] **(theirs) `NetworkParser` multi-lane cont-bay fix** — blessed after independent review + gate.
  Follows `Connection.FromLane` per stage and matches the previous hop on exact lane id, not edge.

## Stage C — Change 2: pedestrian elevation (**redesigned** — retain, don't reconstruct)
- [ ] **C1** `PedNetworkParser` retains the 3rd coordinate → `PedLane.ShapeZ` / `PedCrossing.ShapeZ` / `PedWalkingArea.PolygonZ` (2-D net ⇒ **null**, not zeros)
- [ ] **C2** `IPedNavigation.ElevationsAlong` default interface method + `SumoNavMesh` / `SumoRouteGraphNav` overrides (existing providers unedited)
- [ ] **C3** ped runtime exposes z; `LiveCitySim.Sample()` uses it (SC4 = **2-D trajectory bitwise identical** with z on vs. off — the proof z is output-only)
- [ ] **C4** W1 wire extension: `KindPathArcZ = 5` + `PathArcRecord.PathZ` (SC3 decoder discrimination · SC4 **2-D wire byte-identical** · only task touching gate-covered code)
- [ ] **C5** `PedRemoteReconstructor` 5-out-param overload; `HeadlessIg` interpolates z on the **same arc fraction** as pos (SC4: wire z agrees with in-process z within **0.05 m**)

## Stage D — Change 3: live pedestrian density knobs — **ADOPTED with one fix outstanding (theirs)**
- [x] **D1a** their `PedDemand.SetPopulationCap`/`SetSpawnRatePerSecond` + `_spawnScheduleDirty`.
  Their **rate-0 one-way-door fix is a genuine improvement on my §4**, which specified plain cfg-mirroring
  and would have had that bug (pending `+Infinity` wait that `SpawnDue`'s clamp cannot rescue). Folded in.
- [ ] **D1b** *(theirs to fix)* make `cfg` authoritative: `SetPedDensity` writes `_cfg` first, and `Step()`
  mirrors `_cfg` → demand at one fixed point. Today `SetPedDensity` writes only `_demand`, so **mutating
  `cfg.PedPopulationCap` still does nothing** — the §0/C3 defect and the BIG handoff's explicit
  requirement — and `cfg` goes stale while `SetCarDensity` *does* write `cfg` (car/ped asymmetry).

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
| M2 | Ped-lane geometry volume (sized the superseded index; now sizes the retained `ShapeZ` arrays) | §3.2, §8/R2 | Geneva **98 897** ped-lane segments / **103 498** vertices; Switzerland **860 276** / **888 359**. Retained as `double[]`: **< 1 MB** / **~7 MB** against the 572 MB / 1.65 GB the parsed net already costs. **R2 dissolved** — there is no index. |
| M5 | Real-net load cost | §7, §8/R6 | Geneva 44 MB: parse 9.2 s + ped 1.2 s + crosswalk 1.3 s = **11.6 s**, 53 229 lanes, **572 MB**. Switzerland 161 MB: parse **67.7 s** + ped 6.5 s (+~5 s) ≈ **80 s**, 175 465 lanes, **1 652 MB**. Ctor makes **four** net passes (not two); pass 1 is ~85 % of cost. |
| M7 | Georeference & elevation, real files | §5 | Both nets: `netOffset="-388091.80,-5257586.90"` **identical**, `projParameter="+proj=utm +zone=32 +ellps=WGS84 …"`. Cut preserves the absolute UTM offset exactly; only `convBoundary` shrinks. Elevation span **199.48–1633.77 m** (CH), **324.39–1062.24 m** (Geneva). |
| M8 | Does `RouteFiles[0]` name a real route file? | §0/C4 | **NO.** `geneve_Medium.sumocfg`'s first entry is `common/vType.config.xml` — 107 `<vType>`, **0 routes**. Real routes are entries 4–5 (600 and 1 000 routes). Design corrected to scrape **all** route files. |
| M9 | Is a 2-D (horizontal-only) elevation lookup ambiguous on real nets? | §8/R8 | **Moot for the current design** (a ped now takes z from the path it walks, so there is nothing to disambiguate); retained because it **bounds the error of option W2** in §3.6. Ped-lane vertices bucketed at 2 m, cells spanning > 3 m of z: Geneva **7 / 78 083** (0.009 %, worst 6.3 m); Switzerland **27 / 679 340** (0.004 %, worst 12.6 m). Several of the 27 are *intra*-lane (same lane id both extremes). |
| M3 | Ped z vs. the analytically-known lane elevation on the fixture ramp; and \|pedZ − carZ\| on the real net | C3·SC2, E2·SC4 | *pending — needs C3. The redesign raises the bar: **≤ 0.10 m** against known geometry, where the superseded search could only promise "within a road width".* |
| M4 | `Sample()` cost, z on vs. off at ≥300 peds | §8/R3, C3·SC5 | *pending — needs C3. Expected in the noise (one lerp per ped); a measurable cost means the implementation searched something.* |
| M10 | Is z genuinely output-only? 2-D ped trajectory over 200 steps, z populated vs. null | §3.3, C3·SC4 | *pending — needs C3. Must be **bitwise identical**. This is what turns "parity-inert" from a claim into a fact.* |
| M6 | Demo 200-step `PeakCars`/`PeakPeds`/`ArrivedTotal`, before vs. after | B1·SC5, D1·SC6 | *pending — capture before touching `src/`* |
| M11 | Wire bytes/ped-path: kind 4 vs kind 5, on a 2-D and a 3-D net | §3.6, C4·SC6 | *pending — needs C4. Expect **exactly 0** change on a 2-D net and **+4 B × pointCount** on a 3-D one.* |

---

## Known limits of what this work can verify

Stated up front so the close-out cannot quietly overclaim:

- **The real dataset is ephemeral and must never be committed** (205 MB of third-party data; CLAUDE.md
  committed-vs-ephemeral split). Tier-1 tests are opt-in via `SUMOSHARP_GENEVA_DIR` and **skip** without
  it. A fresh VM must still be 775/0/4 with neither SUMO nor this data present (design §8/R7).
- **No `preprocess.py` cut and no absolute-path `.sumocfg` are among the received files** — the real
  Geneva configs use *relative* paths. The absolute branch stays covered by a synthesised temp-dir config
  (B2·SC2), not by real output.
- **W1 changes a wire format.** Kind 4 is preserved and 2-D output stays byte-identical, but any external
  consumer pinned to a hand-rolled PathArc decoder (rather than `FrameCodec`) would need the kind-5 case
  added to read 3-D ped paths. In-repo consumers are covered by C4/C5.
- **The ~80 s / 1.65 GB full-Switzerland load is pre-existing** and out of scope here (design §7, §8/R6).
  This work must not make it worse; it is not chartered to make it better.
- Nothing in Stages A–E is a **parity golden**. There is no SUMO trajectory comparison in this work; the
  Tier-2 fixture is an input-only regression fixture, exactly like `scenarios/_ped/roadnet_min`.
