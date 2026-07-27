# External georeferenced net loading + pedestrian elevation — TASKS & SUCCESS CONDITIONS

Design (the HOW, referenced by § — never copied): `docs/EXTERNAL-NET-LOADING-DESIGN.md`.
Requirements (the WHAT): `docs/EXTERNAL-NET-LOADING-HANDOFF.md`.
Tracker: `docs/EXTERNAL-NET-LOADING-TRACKER.md`. Read `CLAUDE.md` first.

Each task is a **self-contained briefing**: design reference, files touched, dependencies, and
**numeric/observable success conditions**. A task is closed only when Opus has confirmed its success
conditions first-hand (diff read + gate re-run), per CLAUDE.md's orchestration loop.

---

## Standing gate — measured on this branch at `791d3e6`, before any change

| Gate | Command | Baseline (must be unchanged after every `src/` task) |
|---|---|---|
| Parity suite | `dotnet test Traffic.sln -c Release` | **775 passed / 0 failed / 4 skipped** |
| Determinism hash | `dotnet run --project src/Sim.Bench -c Release` | **`hashA = hashPar = BF3794A4704BCD79`**, `deterministic=True`, par == single |
| LiveCity tests | `dotnet test tests/Sim.LiveCity.Tests -c Release` | capture on T1; **not in `Traffic.sln`** — must be built explicitly (design §8/R4, CLAUDE.md #9) |

Toolchain on this VM (re-provision on a fresh session — both are ephemeral):
`apt-get install -y dotnet-sdk-8.0` → SDK **8.0.129**; `pip install eclipse-sumo==1.20.0` → `sumo` /
`netconvert` **1.20.0** at `/usr/local/bin` (matches `SUMO_VERSION`; shadows apt's 1.18.0).

**Every task below is additive.** No existing signature changes, no existing default changes. If any task
cannot meet that bar, STOP and report rather than widening it.

**Real-data tests are opt-in (design §6.1, §8/R7).** Tier-1 tests read `SUMOSHARP_GENEVA_DIR` and
**skip** when it is unset. Nothing in the standing gate may depend on it — a fresh VM has neither SUMO
nor this dataset and must still be 775/0/4.

---

## Stage A — Validation data (must land first; everything else is validated against it)

### A1 — Synthetic georeferenced 3-D pedestrian net fixture
**Design:** §6. **Depends on:** nothing. **Requires:** `netconvert` 1.20.0 on `PATH` (dev-side only —
never invoked by `dotnet test`).
**Touches (all new):** `scenarios/_ped/roadnet_geo3d/{geo3d.nod.xml, geo3d.edg.xml, net.xml,
scenario.net.xml, relative.sumocfg, provenance.txt, README.md}`.

**Do:** author the plain-XML inputs (nodes in **lon/lat**, `z` in a Swiss-like 380–450 m band, ≥1
four-way junction so crossings/walkingareas are generated, ≥1 edge with a real grade), run the §6
`netconvert` recipe, commit the output. `scenario.net.xml` is a byte-identical copy of `net.xml` under the
`preprocess.py` name. `relative.sumocfg` names the net/route with **relative** paths.
`provenance.txt` records the exact command and `netconvert --version` output, and states **"INPUT ONLY —
not a parity golden"** (mirror `scenarios/_ped/roadnet_min/provenance.txt`'s wording and reasoning).

**Success conditions** — a new test asserting each **on the committed file** (no SUMO at test time):
1. `<location>` `projParameter` contains `+proj=utm +zone=32`; `netOffset` is non-zero in both components.
2. ≥1 lane shape has **3 coordinates per vertex**; the parsed `Lane.ShapeZ` is non-null for ≥1 lane and
   spans a range of **≥ 3 m** across the net (i.e. there is real relief, not a constant plane).
3. `PedNetworkParser.Load` yields **≥1 sidewalk, ≥1 crossing, ≥1 walkingarea**.
4. **The fixture must reproduce the measured real-net property (design §3.2):** its `<lane>` elements
   carry a 3rd coordinate for **every** sidewalk, crossing and walkingarea — as `geneve.net.xml` and
   `swiss_roads.net.xml` both do (100 %, measured). This is the precondition for C1 retaining z at all.
   If netconvert produces 2-D crossings here while the real nets have 3-D ones, the fixture is **not
   representative**: fix the recipe (or note the divergence loudly) rather than weakening the assertion.
5. Total committed size of the directory **< 1 MB** (it is a fixture, not a benchmark net).
6. Standing gate unchanged (no `src/` change in this task).

### A2 — Fixture is reachable from the test projects
**Design:** §6. **Depends on:** A1. **Touches:** whatever repo-root resolution the ped/live-city test
projects already use (read it; do not invent a new mechanism).
**Success conditions:** a test loads `scenarios/_ped/roadnet_geo3d/net.xml` through the existing
repo-root helper and `NetworkParser.Parse` succeeds; no absolute path appears anywhere (CLAUDE.md prime
directive 1).

---

## Stage B — Change 1: net/route path resolution

### B1 — `LiveCityConfig.NetPath` / `RoutePath` + ctor resolution
**Design:** §2.1, §2.2. **Depends on:** A1.
**Touches:** `src/Sim.LiveCity/LiveCityConfig.cs` (two new properties),
`src/Sim.LiveCity/LiveCitySim.cs` (`:143` resolution; the `:161` `PedNetworkParser.Load` and `:271`
`CrosswalkSignals.FromNet` call sites must use the **same resolved** path — design §1.1; `:473` route
path).

**Do:** add the two nullable properties and a private static `ResolveNetPath` implementing §2.2's
4-step precedence. `LiveCityScene.Load` stays on `DatasetDir` (design §1.2) — do not change it.

**Success conditions:**
1. `cfg.NetPath` set to the fixture's `net.xml` while `DatasetDir` points at an **unrelated** directory
   ⇒ `new LiveCitySim(cfg)` loads the fixture: `sim.Network.LanesById.Count` equals the count from a
   direct `NetworkParser.Parse` of that file, and `sim.PedestriansEnabled == true`.
2. `DatasetDir` = the fixture dir with **`net.xml` renamed away** (test copies only `scenario.net.xml`
   into a temp dir) and `NetPath` null ⇒ loads via the §2.2 step-3 probe; `PedestriansEnabled == true`.
3. All three net consumers agree: with `NetPath` pointing at the fixture, `CrossingsEnabled == true`
   (proves `PedNetworkParser`/`CrosswalkSignals` followed the override, not `DatasetDir/net.xml`).
   *This is the non-vacuous part of the task — a version that only rewired `NetworkParser.Parse` would
   pass (1) and (2) and still be broken.*
4. `RoutePath` pointing at a route file in another directory ⇒ the scraped spawn-edge set is non-empty
   and **differs** from the derive-from-net fallback set (proves the scrape ran).
5. **Demo regression:** `LiveCityConfig.ForRepoRoot(repoRoot)` + `new LiveCitySim(cfg)`, 200 steps ⇒
   `PeakCars` / `PeakPeds` / `ArrivedTotal` **identical** to the pre-change values (capture them first).
6. Standing gate unchanged (775/0/4, hash `BF3794A4704BCD79`) **and** `tests/Sim.LiveCity.Tests` built
   explicitly and green.

### B2 — `LiveCityConfig.ForSumocfg`
**Design:** §2.3, §0/C4. **Depends on:** B1.
**Touches:** `src/Sim.LiveCity/LiveCityConfig.cs` only. Reuses `Sim.Ingest.ScenarioConfigParser` — **do
not write a new XML parser** (design §1.4), and do not modify `ParseFileList` (it already handles the
real configs' multi-line lists).

**Success conditions:**
1. `ForSumocfg(fixture/relative.sumocfg)` ⇒ `NetPath` and every `RoutePaths` entry are absolute, exist on
   disk, and resolve to the fixture's files; `DatasetDir` == the sumocfg's own directory.
2. **Absolute-path case:** the test writes a `.sumocfg` into a temp dir whose `<net-file>` is an
   **absolute** path to the fixture net ⇒ resolves to that path unchanged (not double-combined with the
   temp dir). Both emitters from the handoff are then covered.
3. A `.sumocfg` with **no `<input>` section** ⇒ `NetPath` stays null and construction still succeeds via
   the `DatasetDir` probe (§2.3's non-throwing rule), rather than throwing.
4. Field-for-field, `ForSumocfg` matches `ForDataset` on every knob except `NetPath`/`RoutePath(s)`
   (assert by reflection over the public properties, so a later-added knob cannot silently drift).
5. `new LiveCitySim(ForSumocfg(...))` steps 100 times and `Sample()` returns **> 0 cars**.
6. **The §0/C4 regression — the reason this design was corrected.** A `.sumocfg` whose `<route-files>`
   lists a vType-only file **first** and the real route file **second** (the exact shape of
   `geneve_Medium.sumocfg`) ⇒ `RoutePaths` has **both** entries in order, and the scraped spawn-edge set
   is non-empty and equal to the set scraped from the real route file alone. A `RouteFiles[0]`
   implementation must **fail** this. Build the fixture config to mirror the real one — six entries,
   three vType files first.
7. Standing gate unchanged.

---

## Stage C — Change 2: pedestrian elevation (retain-not-reconstruct)

> Restructured after the design was corrected: z is **retained** from ingest and carried along the ped's
> own path (design §3.1–§3.4), not recovered by a nearest-lane search. C1–C3 stay inside `Sim.Pedestrians`
> plus the `Sample()` seam; C4–C5 extend that to the wire per the **W1** decision (design §3.6).
> **No new project reference anywhere** (design §3.1).
>
> C4 is the only task in this plan that touches gate-covered code (`Sim.ParityTests`'
> `RungB22ReplicationCodecTests`) and the only one that changes a wire format. Treat its SC3 (decoder
> discrimination) and SC4 (2-D wire byte-identical) as non-negotiable.

### C1 — `PedNetworkParser` retains the third coordinate
**Design:** §3.2, §3.3. **Depends on:** A1.
**Touches:** `src/Sim.Pedestrians/PedNetwork.cs` (three records gain a defaulted trailing elevation
channel), `src/Sim.Pedestrians/PedNetworkParser.cs` (add `ParseShapeZ`, mirroring
`NetworkParser.ParseShapeZ`).

**Do:** `PedLane.ShapeZ`, `PedCrossing.ShapeZ`, `PedWalkingArea.PolygonZ` — all
`IReadOnlyList<double>?`, index-aligned with the existing 2-D shape, **null on a 2-D net**. Defaulted
trailing parameters so every existing constructor call compiles unchanged. Copy `Lane.ShapeZ`'s header
comment discipline: state explicitly that this is output-only and read by no routing/steering/ORCA code.

**Success conditions:**
1. On the A1 fixture, `ShapeZ`/`PolygonZ` is **non-null for every** sidewalk, crossing and walkingarea,
   with `Count == Shape.Count` in every case (index alignment is the whole contract — assert it).
2. Values match the net file: for one hand-picked lane, the parsed elevations equal the 3rd components in
   its `shape=` attribute exactly (parse the XML in the test, don't hardcode).
3. On `scenarios/_ped/demo_city/box/net.xml` (2-D) **every** channel is `null` — not an empty array, not
   zeros. This is what keeps §3.3's parity-inertness claim honest.
4. `PedCrossing.Outline` also gets its z retained, or the task states explicitly why not (it feeds
   crosswalk polygons, which BIG may also need to place).
5. Both TFMs build; `Sim.Pedestrians.csproj` gains **no** new `ProjectReference` (verify by diff).
6. `tests/Sim.Pedestrians.Tests` green; standing gate unchanged.

### C2 — `IPedNavigation.ElevationsAlong` + provider overrides
**Design:** §3.4. **Depends on:** C1.
**Touches:** `src/Sim.Pedestrians/Navigation/INavigation.cs` (default interface method),
`Navigation/Bake/*` + `Navigation/RouteGraph/SumoRouteGraphNav.cs` (overrides).

**Do:** add `ElevationsAlong(path)` as a **default interface method returning all zeros**, following the
existing `HalfWidthsAlong` precedent (`INavigation.cs:51-58`) exactly — same shape, same rationale, so
DotRecast and every test double inherit flat behaviour and need no edit. Override in `SumoNavMesh` and
`SumoRouteGraphNav` from C1's retained channels.

**Success conditions:**
1. **Existing providers untouched:** verify by diff that no test double, and not the DotRecast provider,
   was edited. They must compile and behave identically via the default.
2. Default returns `path.Count` zeros — asserted directly, so a provider without an elevation model is
   provably flat rather than accidentally throwing or returning a short array.
3. `ElevationsAlong(path).Count == path.Count` for every override, on every path the A1 fixture produces
   (assert over ≥50 generated paths, not one).
4. **Correctness on a known grade:** on the A1 fixture, take a path along the fixture's engineered ramp
   and assert the returned elevations increase monotonically and match the node z within **0.05 m** at
   each vertex.
5. **Determinism:** identical path in ⇒ bitwise-identical elevations out, across two independently
   constructed providers.
6. Standing gate unchanged.

### C3 — Ped runtime exposes z, and `LiveCitySim.Sample()` uses it
**Design:** §3.4, §3.5(a). **Depends on:** C2.
**Touches:** `src/Sim.Pedestrians/Lod/PedLodManager.cs` (an elevation accessor beside
`PositionOf(id, now)`, `:394`), `src/Sim.LiveCity/LiveCitySim.cs` (the literal `0.0` at `:1076`).

**Do:** interpolate z between the two path elevations bracketing the ped's existing waypoint cursor,
reusing the fraction steering already computes. **No search of any kind** — if the implementation reaches
for a nearest-lane or nearest-vertex lookup, it has misread the design; stop and report.

**Success conditions:**
1. **3-D:** on the A1 fixture, after stepping until ≥5 peds are live, every sampled `LiveCityPed.Z` is
   non-zero and within the fixture's elevation range.
2. **Exactness beats the superseded design — the point of the redesign.** For a ped walking the
   engineered ramp, sampled z matches the analytically-known lane elevation at its arc position within
   **0.10 m** at ≥10 successive steps. (The nearest-lane mechanism could only have promised "within a
   road width"; assert the tighter bar that retaining z actually buys.)
3. **2-D regression:** on `demo_city/box`, every `LiveCityPed.Z` is exactly `0.0` (bitwise) across 200
   steps, and `PeakPeds`/`ArrivedTotal` are identical to pre-change. City3D / raylib / `VizReplayBuilder`
   bit-identical.
4. **Parity-inertness, asserted not asserted-to:** a ped's 2-D trajectory over 200 steps is **bitwise
   identical** with the elevation channel populated vs. forced null. This is the check that proves §3.3 —
   that z touched no steering, ORCA or routing decision. Without it, "output-only" is a claim, not a fact.
5. **Cost (design §8/R3, now ordinary rather than a risk):** with ≥300 live peds on the A1 fixture,
   report `Sample()` time with the elevation channel on vs. forced null, ≥5 repeats, both absolute
   numbers. Expected to be in the noise — it is one lerp per ped. A result that is *not* in the noise
   means the implementation searched something.
6. Standing gate unchanged **and** `tests/Sim.LiveCity.Tests` built explicitly and green.

### C4 — W1: carry z on the wire (new frame kind 5)
**Design:** §3.6. **Depends on:** C1 (there must be a z to publish). **Unblocked** — the owner chose W1.
**Touches:** `src/Sim.Replication/Records.cs` (`PathArcRecord.PathZ` via an **additive** ctor overload;
keep the 4-arg ctor), `src/Sim.Replication/FrameCodec.cs` (`KindPathArcZ = 5`, `PathArcZRecordSize`,
write/read paths), `src/Sim.Pedestrians/Lod/PedPublisher.cs` + `PedReplicationPublisher.cs` (emit kind 5
only when `PathZ` is non-null), `src/Sim.Pedestrians/Lod/PedReplicationReceiver.cs` (accept **both**
kinds).

**Do:** exactly the layout in §3.6 — `14 B + 12 B/point`, z quantized with the **existing**
`QuantizeCm32`, no new quantization scheme. **Do not** change kind 4's stride and **do not** bump
`FrameCodec.Version` (design §3.6 gives the two measured reasons).

**Success conditions:**
1. `PathArcZRecordSize(n) == 14 + 12 * n` exactly; kind 4's `PathArcRecordSize(n) == 14 + 8 * n`
   **unchanged**.
2. **Round-trip:** a record with z encodes and decodes to the same x, y **and z** within the 1 cm
   quantization step (assert ≤ 0.01 m, not "approximately equal"), over ≥3 paths including a 1-point and a
   many-point path.
3. **Decoder discrimination — the load-bearing one (design §8/R9a).** A kind-4 payload handed to the
   reader yields `PathZ == null` and correct x,y; a kind-5 payload yields populated z. Neither is
   misparsed as the other. Because `ReadHeader` validates no version byte, a stride change would have
   corrupted silently — assert explicitly that a kind-4 frame is **not** read with a 12 B stride.
4. **2-D nets stay byte-identical on the wire (design §8/R9c).** With `PathZ` null, the publisher emits
   **kind 4** and the produced byte buffer is **byte-for-byte identical** to the pre-change output for the
   same input (capture the bytes before the change and compare). Anything less and every existing
   consumer's traffic changed silently.
5. **Gate-covered test read, not assumed (design §8/R9b).** `RungB22ReplicationCodecTests` is in
   `Sim.ParityTests`. Re-run it, and **read** it to confirm it still asserts something real about frame
   sizing rather than passing vacuously through the helper.
6. `PedBandwidthMeter` accounts kind-5 frames at the new size; a 3-D-net measurement shows the expected
   `+4 B × pointCount` per path record and **no** change on a 2-D net. Report both numbers.
7. Standing gate unchanged (775/0/4, hash `BF3794A4704BCD79`); DDS loopback self-test passes; both TFMs
   build.

### C5 — `PedRemoteReconstructor` 5-out-param overload
**Design:** §3.5(b), §3.6. **Depends on:** C4.
**Touches:** `src/Sim.Pedestrians/Lod/HeadlessIg.cs` (interpolate z with the **same arc fraction**
already used for position), `src/Sim.Pedestrians/Lod/PedRemoteReconstructor.cs` (the new overload).

**Do:** z is reconstructed in `HeadlessIg` alongside pos — not by a separate lookup, and not by
re-deriving the arc fraction independently, so pos and z cannot disagree.

**Success conditions:**
1. The existing 4-out-param overload's body is **untouched**, and all **15** call sites (design §1.10)
   compile unedited — verify by diff.
2. The 5-param overload returns the same `pos`/`visible`/`animTag` as the 4-param one for the same id and
   render time.
3. z is sampled at the **smoothed** render position, not the raw wire sample — assert during an active
   correction/catch-up, where the two differ, that z tracks the smoothed pos.
4. **Wire path agrees with the in-process path — the real proof W1 was worth doing.** For the same ped at
   the same sim time, `TryGetRenderPose`'s z and `LiveCitySim.Sample()`'s `LiveCityPed.Z` (C3) agree within
   **0.05 m** (quantization + playout interpolation only) at ≥20 sampled times. Under the rejected W2 this
   bar would have been metres, not centimetres.
5. **Kind-4 / no-z stream:** against a publisher emitting kind 4, the overload returns `z == 0.0` and does
   not throw — the graceful path for a 2-D net.
6. `tests/Sim.Pedestrians.Tests` green; both TFMs build; standing gate unchanged.

---

## Stage D — Change 3: live pedestrian density knobs

### D1 — Mirror the live ped knobs into `PedDemandConfig` each `Step()`
**Design:** §4. **Depends on:** nothing (independent of A–C).
**Touches:** `src/Sim.LiveCity/LiveCitySim.cs` (keep the `PedDemandConfig` in a field; mirror the two
knobs at one fixed point at the top of `Step()`).

**Success conditions:**
1. **The bug is demonstrated first.** A test that mutates `cfg.PedPopulationCap` mid-run and asserts the
   live ped count follows must **fail before** this change and pass after. Record both outcomes — an
   after-only pass does not prove the fix did anything.
2. Raise `cfg.PedPopulationCap` from 20 → 120 at step 100 ⇒ live ped count exceeds 20 within 200 further
   steps and trends toward the new cap.
3. Lower it 120 → 20 mid-run ⇒ no new spawns while over cap; count decays monotonically (no despawn
   storm — existing peds finish their trips).
4. `cfg.PedSpawnRatePerSecond` doubled mid-run ⇒ spawns per step in the following 100 steps increase
   (report both rates).
5. **Car knobs unchanged:** the same test pattern on `cfg.CarTargetConcurrent` passes **before and after**
   (proves design §0/C3's claim that the car half already worked, and that this task did not disturb it).
6. **No unmutated behaviour change:** demo 200-step `PeakPeds`/`ArrivedTotal` identical to pre-change.
7. Standing gate unchanged.

---

## Stage E — Real-net validation (Tier 1) & close-out

### E1 — Opt-in real-data test gate
**Design:** §6.1, §8/R7. **Depends on:** nothing.
**Touches:** a small test helper in `tests/Sim.LiveCity.Tests` resolving `SUMOSHARP_GENEVA_DIR`.
**Success conditions:**
1. Helper returns the three real inputs (`common/swiss_roads.net.xml`, `geneve/tools/geneve.net.xml`,
   `geneve_Medium.sumocfg`) when the var is set and all exist; otherwise signals skip.
2. **With the var unset**, the Tier-1 tests report **Skipped** (not passed, not failed) and the message
   names `SUMOSHARP_GENEVA_DIR`. A vacuous "passes because it did nothing" is a task failure.
3. Standing gate unchanged **with the var unset** — the fresh-VM invariant.

### E2 — Real Geneva cut: end-to-end load through the new API
**Design:** §6.1, §7. **Depends on:** B1, B2, C4, E1. **Gated** on `SUMOSHARP_GENEVA_DIR`.
**Success conditions** (on `geneve/tools/geneve.net.xml`, 44 MB — use the *cut*, not Switzerland, so the
test stays ~15 s):
1. `ForSumocfg(geneve_Medium.sumocfg)` ⇒ resolves `common/swiss_roads.net.xml` and all six route files
   relative to the sumocfg dir; every path exists on disk.
2. `NetPath` = the Geneva cut ⇒ `new LiveCitySim(cfg)` succeeds; `PedestriansEnabled` and
   `CrossingsEnabled` both **true**; lanes == **53 229**, sidewalks == **2 201**, crossings == **221**,
   walkingareas == **2 179** (the measured figures — a drift here means an ingest regression).
3. Ped elevation is live on the real net: every ped lane's retained `ShapeZ` is non-null; step until
   ≥20 peds are live ⇒ every `LiveCityPed.Z` lies within **324.39 – 1062.24 m** (the measured range) and
   none is 0.
4. Ped↔car cross-check on the **real** net, same rule as C4·SC2: report p50/p90/max of |pedZ − carZ|
   over pairs within 10 m; **p90 ≤ 2.0 m**; classify every > 2.0 m outlier as multi-level (expected,
   ≤ 27 such spots nationwide per design §3.3a) or wrong-lane snap (a bug).
5. Cars spawn and move: `Sample()` returns > 0 cars, and `ArrivedTotal` > 0 within 600 steps.
6. Standing gate unchanged with the var unset.

### E3 — Full Switzerland: load-and-scale confirmation
**Design:** §7, §8/R6. **Depends on:** E2. **Gated**, and marked long-running (~2 min).
**Success conditions** (on `common/swiss_roads.net.xml`, 161 MB):
1. `new LiveCitySim(cfg)` with `NetPath` set to it **succeeds** — this is handoff definition-of-done
   item 1, on the real file. lanes == **175 465**, sidewalks == **13 811**, crossings == **735**,
   walkingareas == **13 537**.
2. Reported and recorded in the tracker: total ctor wall time and peak working set. Flag if either
   exceeds the measured baseline (**≈ 80 s / ≈ 1.65 GB**) by > 25 % — that would mean this change added
   cost to the load path, which it must not.
3. All **28 083** ped lanes retain a non-null `ShapeZ` (the measured count); a sampled ped z falls
   within **199.48 – 1633.77 m**.
4. Standing gate unchanged with the var unset.

### E4 — Close-out
**Depends on:** all of the above.
**Success conditions:**
1. Final gate re-run and quoted: parity **775/0/4**, hash **`BF3794A4704BCD79`** (single == parallel),
   `tests/Sim.LiveCity.Tests` + `tests/Sim.Pedestrians.Tests` green, **and** the Tier-1 suite green with
   `SUMOSHARP_GENEVA_DIR` set.
2. A short **BIG-side handoff-back** section in the tracker: the exact new API surface, the §0/C3 and
   §0/C4 corrections they need to know about, the §7 load-time warning with numbers and the
   cut-box-not-full-Switzerland recommendation, and a plain statement of what was not verified.
3. Every unchecked box in the tracker either ticked with first-hand evidence or explicitly listed as
   deferred with a reason. No box ticked on an implementor's report alone (CLAUDE.md orchestration loop).
