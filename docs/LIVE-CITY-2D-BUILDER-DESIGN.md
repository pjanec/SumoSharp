# Live-city 2D builder — design + task plan (Phase 1)

**Goal.** The combined `sim_viz` demo SumoData's spec asks for: on a block of the demo-city, **dense car
traffic + a large weaving pedestrian crowd**, with a **high-realism ORCA zone** (LOD contrast) and **cars
that visibly stop for pedestrians on crossings**. This is Phase 1 of the live-city effort.

Reference: `SUMOSHARP-LIVE-CITY-DEMO-SPEC.md` (SumoData's wiring spec), `SUMOSHARP-LIVE-CITY-DECISIONS.md`
(the locked contract). This doc is the HOW; it does not restate the WHAT.

**Order & blocking (answers the owner's question).** Phase 1 (this doc) is **not blocked by the
crossing-yield engine feature.** It reaches "cars stop for crossing peds" through the *existing*, already-
proven seam: a per-crossing `InterestSource` promotes a pedestrian the moment it steps onto a crosswalk →
the ped enters the manager's high-power `OrcaCrowd` → `Engine.CrowdSource` sees it → the existing laneless-
RVO braking path stops the car (proven in the crossing-gate scene, speed 13.9→0, emergent). The dedicated
**Option-B deterministic occupancy** (Phase 2, `docs/…CROSSING-YIELD-DESIGN.md`, to be written) is a
**performance + robustness** enhancement that plugs into the *same* seam — it lets cars yield to *every*
low-power crossing ped without paying to promote them, and closes the promotion-timing/coverage gap. So the
sequence is **builder → crossing-yield → City3D**, as requested.

Phases 2 (crossing-yield Option B) and 3 (City3D combined + semantic) get their own design docs when we
reach them; this doc covers Phase 1 only.

---

## 1. Mechanism

New scene builder `SceneGen.BuildLiveCity(boxDir)` (a copy of `BuildDenseCity`, so `--ped-dense-city`
stays byte-identical) + a `Program.cs` mode `--live-city <out.html>`. Everything is a **demo scene**:
`Engine.CrowdSource` stays null for every committed golden, so parity/determinism are untouched.

### Substrate
- Net + car flow + weave crowd: exactly as `BuildDenseCity` (demo-city box, dense local car flow on real
  drivable edges, `PedDemand`+`PedLodManager` with `EnableWeave = true`).
- **Crop:** the dining-district fallback bbox `[3100, 1900, 3900, 2700]` (Decisions Q7), replaced by
  SumoData's co-located downtown hero bbox when it lands (one constant to change). NOTE the current
  auto-densest-block pick lands in downtown (no hero venues) — for the live-city we pin the crop, we don't
  auto-pick.

### W-A — promote peds (seed `InterestField`)
Register **static** `InterestSource`s once before the step loop (no per-step churn):
- **Per-crossing** (targeted): enumerate crossing polygons inside the crop — the net's internal edges
  `function="crossing"` (id `:<node>_c<idx>`), whose `crossingEdges` attribute also gives the vehicle
  edge(s) each one spans. Place an `InterestSource` at each crossing centroid with
  `PromoteRadius ≈ crossing half-width + ~15 m` and `DemoteRadius ≈ 2×` (spec §8). Effect: a ped stepping
  onto a crosswalk promotes to ORCA → visible to cars → car yields **there**.
- **High-realism zone** (showcase pocket): one large `InterestSource` over a chosen district polygon
  (`zones.json`, e.g. `zone_dining`/`zone_downtown` centroid + radius). Effect: that pocket runs full ORCA
  (reactive, collision-avoiding) while the rest of the crop stays low-power weave — the visible LOD
  contrast.

### W-B — expose the high-power crowd and couple it to the engine
- **New accessor** on `PedLodManager`: `public ICrowdFootprintSource HighPowerFootprints => _highCrowd;`
  (`_highCrowd` is already `OrcaCrowd : ICrowdFootprintSource`; read-only; inert for every existing
  consumer — no behavior change, parity-safe).
- In the builder: `engine.CrowdSource = manager.HighPowerFootprints;`
- **Step order:** step the ped crowd **before** `engine.Step()` each tick (`demand.Step(...)` then
  `engine.Step()`), so the engine's `CrowdSource` query sees current ped poses (the `CrossRegimeCoupling`
  discipline). For demo speeds per-step order suffices; if a fast car clips peds, adopt the sub-stepped
  frozen-snapshot exchange `CrossRegimeCoupling` uses. Braking (not swerve) is the target: `CrowdSource != null`
  engages the virtual-leader/brake path, which is what "cars **stop** for crossing peds" needs.

### W-C — colour discs by LOD regime
Replace the walk/pause-only colouring with regime-aware colouring (pattern already at `SceneGen.cs:446`,
`:687`):
```csharp
var model = manager.ModelOf(id);                                   // PedDrModel
int kind = model == PedDrModel.FreeKinematic ? KindPedHighPower     // 10 orange — ORCA
         : animTag == ActivityTimeline.WalkAnimTag ? KindPedLowPower // 9 grey — low-power weave
         : KindPedPaused;                                            // 14 yellow — paused/dwell
```
So low-power weavers render grey, promoted ORCA peds orange, paused yellow — you can *see* a ped turn
orange as it enters the zone / steps onto a crossing.

---

## 2. Determinism & parity (the invariants)
- **Parity-inert:** `Engine.CrowdSource` is null for every committed golden; this is a new demo scene, no
  engine source change except the one read-only `HighPowerFootprints` accessor (inert). `dotnet test`
  parity goldens + the determinism hash stay green; the weave stays behind `EnableWeave`.
- **Deterministic:** seeded `PedDemand` + SplitMix64 car-flow picks (as `BuildDenseCity`); interest sources
  are static; no `System.Random`. Run-to-run byte-identical.

## 3. Files touched
- `src/Sim.Viz/SceneGen.cs` — new `BuildLiveCity` (copy of `BuildDenseCity` + W-A/W-B/W-C); a small crossing
  enumerator (parse net internal `function="crossing"` lanes in the crop → centroids).
- `src/Sim.Viz/Program.cs` — `--live-city` dispatch + a `RunLiveCity` (mirrors `RunPedDenseCity`, prints
  peak cars / peak peds / peak high-power / min car-speed-while-ped-on-crossing).
- `src/Sim.Pedestrians/Lod/PedLodManager.cs` — add `HighPowerFootprints` accessor (~near `_highCrowd`,
  line 91).
- `scripts/gen-demos.sh` — register `--live-city` under Pedestrians (or hold until the hero crop lands).

## 4. Success conditions (acceptance — spec §5)
1. **LOD split visible:** produced HTML contains BOTH kind 9 (grey, low-power) and kind 10 (orange,
   high-power) discs; peds turn orange in the zone / on crossings. *Verify:* parse the payload for both kinds.
2. **Cars yield to crossing peds:** at least one vehicle measurably decelerates toward ~0 while a promoted
   ped occupies a crossing in its lane, and resumes after it clears. *Verify:* the run's printed
   min-car-speed-while-ped-on-crossing drops to ~0, or a screenshot shows a car stopped at an occupied
   crosswalk.
3. **Density:** dense cars (≈100+ concurrent) + a large crowd (≥150 concurrent) coexist, weave on (ambient
   peds don't pass through each other).
4. **Determinism:** same bytes run-to-run.
5. **Parity untouched:** full `dotnet test` + ped suite green; hash unmoved.

## 5. Task list & tracker (Phase 1)
- [ ] **T1 — `HighPowerFootprints` accessor** on `PedLodManager` (+ a trivial test that it returns the
      live high-power crowd). *Done when:* accessor compiles, inert for existing consumers, ped suite green.
- [ ] **T2 — crossing enumerator:** parse the crop's `function="crossing"` internal lanes → centroids (+
      `crossingEdges` for later Phase 2). *Done when:* returns the expected crossing count for the fallback crop.
- [ ] **T3 — `BuildLiveCity` scene:** copy `BuildDenseCity`; pin the fallback crop; W-A (per-crossing +
      zone sources); W-B (`engine.CrowdSource = manager.HighPowerFootprints`, ped-before-engine step order);
      W-C (regime colouring). *Done when:* renders, payload has kind 9 **and** 10.
- [ ] **T4 — `--live-city` mode + diagnostics** (peak cars/peds/high-power, min-car-speed-on-crossing).
      *Done when:* the run prints numbers that satisfy §4.1–§4.3.
- [ ] **T5 — verify acceptance:** screenshot a car stopped at an occupied crosswalk + the grey/orange LOD
      split; confirm determinism (two runs, identical bytes); confirm `dotnet test` + ped suite green.
- [ ] **T6 — (deferred to hero-crop landing)** register in `gen-demos.sh`; swap the crop constant to
      SumoData's downtown hero bbox; re-verify.

## 6. Explicitly out of scope for Phase 1
- The Option-B **deterministic low-power occupancy** (cars yield to un-promoted peds cheaply) — that's
  Phase 2; Phase 1 yields via per-crossing promotion, which is correct behaviour at demo scale.
- Semantic set-pieces (enter buildings, dine at terraces, meet) rendered in 2D — those ride the R2 consumer
  and are primarily a **City3D / Phase 3** concern; the 2D acceptance bar (spec §5) does not require them.
- Anything needing SumoData's regen (outdoor tables, hero crop) — Phase 1 runs on the fallback crop now and
  picks those up when they land (T6).
