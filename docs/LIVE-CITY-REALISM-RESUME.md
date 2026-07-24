# Live-city REALISM fixes — RESUMPTION doc

**Read this first after a compaction / fresh session.** Self-contained current state of the live-city
realism-violation work. Branch: **`claude/livecity-realism-fixes`** (off `main` @ `b70c068`, the merged
live-city milestone; pushed). Running trail: `docs/LIVE-CITY-REALISM-ATTEMPT-LOG.md` (read its top "HOW TO
REPRODUCE" + the newest PROGRESS LOG entries).

---

## 1. THE TASK — 5 owner-observed realism violations (from `docs/TASKS.md`)
1. **Cars drive THROUGH peds on crosswalks** (high-realism zone) — no yield/dodge. ← **IN PROGRESS (analysis done, reframed)**
2. **Low-realism crossings not marked 'occupied'** when low-power peds cross — cars don't stop.
3. **Low-power peds DISAPPEAR on promotion** into the high-realism zone.
4. **ORCA peds leaving the zone STAY ORCA and wander** (off-sidewalk, no route, never demote).
5. **ORCA peds don't dodge a SUMO car standing on the crosswalk**.

Task tracker: #24 (defects 1+2) in_progress · #25 (3+4) pending · #26 (5) pending.

---

## 2. WORKFLOW / OWNER'S BINDING RULES
- **Same as the demo, NO cheating.** Repro + verify ONLY on the real `LiveCitySim` + `LiveCityConfig` path
  (see §3). A fix must transfer directly to the City3D/raylib demo.
- **Solid repro → EXTENSIVE per-entity analysis from state dumps → design → implement.** Verify every
  hypothesis in the per-entity data BEFORE chasing it. No ad-hoc coding.
- **Visual verification = Sim.Viz HTML replays** delivered to the owner (via SendUserFile, `display:render`).
  **No 3D needed** (proven). Mobile: the replay description is collapsed by default (tap "▸ info").
- **Parity is the iron law.** `dotnet test tests/Sim.ParityTests -c Release` = **657/4** byte-identical;
  `Sim.Bench` hash **`D96213B7BB4021A7`**, parallel==single; no `System.Random`. **All fixes are demo-gated
  in `Sim.LiveCity`/`Sim.Viz` — NEVER edit `Sim.Core` or goldens.** (Engine untouched so far this branch.)

---

## 3. TOOLCHAIN (built this session — use these)
Build once: `dotnet build src/Sim.Viz -c Release`. Then (`--no-build`):
- **FAITHFUL replay (the visual channel):**
  `dotnet run --project src/Sim.Viz -c Release --no-build -- --live-city-demo <out.html>`
  → `SceneGen.BuildLiveCityDemo`: drives the REAL `LiveCitySim`(`LiveCityConfig.ForRepoRoot`) and renders
  with the SAME DR motion reconstruction the 2D/3D viewers use — cars via `DrClock.ResolveAt` +
  `KinematicReconstructor` (CENTER + emitted heading, **5-tuple** `[x,y,headingDeg,len,wid]` +
  `ScenePayload.UseDataHeading=true`, upcoming-lane look-ahead), peds via `PedRemoteReconstructor` off
  `sim.PedSource`. **Owner-confirmed it matches the 3D viewer.** RenderHz=10, steps=160 → ~5.7 MB (mobile).
  The recipe + gotchas: `docs/IGBRIDGE-HTML-REPLAY-GUIDE.md` (cars §1–§5, peds §5b). Deliver the HTML to the
  owner with `SendUserFile`.
- **DEFECT-#1 per-entity yield analysis:**
  `dotnet run --project src/Sim.Viz -c Release --no-build -- --live-city-yielddump 200`
  → runs the real `LiveCitySim`, classifies every car-within-2.5 m-of-a-crossing-ped from raw `Sample()`
  states by motion direction: `stoppedYield` / moving-`beside` / moving-`inPath` (+ fast flag). Prints
  aggregate + worst events. Diagnostic-only. Uses new `LiveCitySim.CrossingCentroids`.
- **Focused subsystem testbeds:** `--ped-crossing-gate` (car↔crossing yield), `--ped-lod-promotion`
  (promote/demote, defects 3/4), `--ped-dodge` (ORCA obstacle avoid, defect 5).
- **RETIRED for realism work:** the batch `--live-city` (`SceneGen.BuildLiveCity`) — it DIVERGED from the
  demo (own Engine/LOD, no `LiveCityConfig`). Do not use it to verify fixes.

---

## 4. DEFECT #1 — analysis so far (this is where we stopped)
**Faithful `--live-city-yielddump 200` (100 s):** 152 raw "car within 2.5 m of a crossing-ped" →
- **56 stoppedYield** (car <0.5 m/s next to ped) = cars that DID yield ✓
- **70 moving BESIDE/behind** the ped (passing someone crossing the other way) = benign
- **26 moving IN-PATH** (driving AT a ped ahead) = **the real defect-#1 failures**; only **3 fast (>=6 m/s)**.

**Reframing:** the 2.5 m metric over-counts ~6×. Cars mostly DO react (56 stopped, 26 braking-in). The real
issue = **~5–10 encounters/100 s where a car brakes but noses its FRONT onto the ped** (too-tight stop
margin / late brake), SLOW not fast tunnelling. Worst cars decelerate across ticks (veh45 5.2→0.7, veh81
2.6→1.6) yet end ~0.2 m from the ped = visually "going over" them.

**Leading hypothesis (NOT yet confirmed — verify before designing):** `Engine.CrowdLongitudinalConstraint`
(`src/Sim.Core/Engine.cs` ~line 8572; lateral gate ~8602) brakes only when the ped disc is inside the car's
NARROW wheel-path corridor (`|latOff − ego.LatOffset| < egoHalf + discR`). A ped walking ACROSS the road
enters that corridor only when almost in front → brake triggers LATE → car noses in. A human yields for a
ped ANYWHERE on the crossing ahead.

**NEXT STEP (still analysis):** (a) instrument WHEN the brake first engages vs the ped's lateral offset
(confirm late-trigger); (b) confirm these peds are on GREEN (batch metric said ~97% ped-on-green). THEN
design — likely fix: **brake for a ped anywhere on a crossing polygon ahead of the car, not just the wheel
corridor** (demo-gated, parity-safe; would be a new demo-gated widening in the crowd-brake, gated OFF on
goldens). **Open question posted to owner:** does the reframing (nose-in on ~5–10 encounters, not gross
blow-through) match their 3D observation? Their answer weights the late-trigger fix vs the 3 tunnelling cases.

---

## 5. KEY MECHANISM FACTS (for the analysis)
- **Car→ped yield:** `Engine.CrowdSource = CompositeFootprintSource(PedLodManager.HighPowerFootprints,
  CrossingOccupancySource)` when `cfg.YieldEnabled`. Cars brake via `Engine.CrowdLongitudinalConstraint`:
  query CrowdSource discs near ego → project onto ego's lane → brake if ahead AND laterally overlapping
  ego's footprint (the narrow-corridor gate = the suspect).
- **Crossing occupancy:** `CrossingOccupancySource.Update(_movingLowPowerPositions)` marks a disc where a
  ped is on a crossing poly. `_movingLowPowerPositions` = peds with `ModelOf(id) != FreeKinematic &&
  AnimTag == WalkAnimTag`. ⇒ **ORCA (promoted) peds go via HighPowerFootprints; walking low-power peds via
  crossing discs; a PAUSED low-power ped ON a crossing is in NEITHER** (likely relevant to defect #2).
- **Ped LOD:** `PedLodManager` + the `InterestField` high-realism zone (`SetLcRealismZone`, camera-driven in
  viewers; static crop-centre pocket headless). `PositionOf(id,t)`: PathArc/ActivityTimeline continuous in
  `t`; **FreeKinematic (ORCA) ignores `t`** (returns live ORCA pos). Ped smoothing → reconstruct off
  `sim.PedSource` via `PedRemoteReconstructor` (NOT `PositionOf` at a lagged time — that = the "caterpillar").

---

## 6. SIDE THREADS (done / handed off — don't lose)
- **Guides pushed to `main`** (docs-only): `docs/IGBRIDGE-HTML-REPLAY-GUIDE.md` (the DR-replay recipe, now
  complete with peds §5b) and `docs/IGBRIDGE-INTEGRATION-GUIDE.md` (extended: run the real `LiveCitySim`
  host §9b; requirement §9c = the integrated host must read ANY SUMO scenario from a folder).
- **`docs/VIZ-UNIFICATION-DESIGN.md` (on `main`)** — a self-contained bootstrap for a SEPARATE session to
  build ONE reusable HTML-replay generator that renders REAL deterministic engine output (not goldens),
  serves cars+peds, and applies DR smoothing automatically so it projects to the whole GitHub-Pages gallery.
  When that lands on `main`, **fetch `main` into this branch** and the smoothing/features flow through for free.
- IgBridge itself (code + 9 docs) is fully in our branch/main via the milestone merge; only the two guides
  above had to be vendored.

---

## 7. KEY FILES
- `src/Sim.Viz/SceneGen.cs` — `BuildLiveCityDemo` (faithful DR replay: cars+peds reconstruction loop).
- `src/Sim.Viz/Program.cs` — `RunLiveCityDemo`, `RunLiveCityYieldDump` (the diagnostic), `WriteHtml`.
- `src/Sim.Viz/Payload.cs` — `ScenePayload.UseDataHeading` (added).
- `src/Sim.Viz/template.{html,js}` — the shared player (`useDataHeading` branch; description collapsed on mobile).
- `src/Sim.LiveCity/LiveCitySim.cs` — the real host: `VehicleSource`/`PedSource`/`LocalLanes`,
  `SetLcRealismZone`, `CrossingCentroids` (added), `Sample()`.
- `src/Sim.Core/Engine.cs` — `CrowdLongitudinalConstraint` (~8572), `CrowdSource` (~764). **READ-ONLY for
  analysis; do not edit (parity).**
- `src/Sim.Pedestrians/Crossing/CrossingOccupancySource.cs`, `src/Sim.Pedestrians/Lod/{PedLodManager,
  PedRemoteReconstructor}.cs`, `src/Sim.Viewer.Motion/{KinematicReconstructor,DrClock}.cs`.
- `scenarios/_ped/demo_city/box/` — the demo dataset (also a locked regression fixture; `box/README.md`).
