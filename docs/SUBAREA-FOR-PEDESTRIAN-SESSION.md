# Sub-area believable-traffic approach — brief for the pedestrian-modelling session

**Purpose.** You (the pedestrian/crowd session, working in the SumoSharp repo) are building
high-volume pedestrian modelling on the SumoSharp engine. This note explains the **sub-area
vehicle-traffic system** built in the sibling **SumoData** repo, links its design docs, and states the
**compatibility requirements** your pedestrian work must honour so both run in the *same* user-selected
sub-area with consistent realism. It is self-contained — you don't need the SumoData repo to read it,
but the deep detail lives there (see §5 links).

Written 2026-07-18 from the SumoData side. Companion to `COORDINATION-pedestrian-x-highdensity.md`
(shared-code collision protocol) — this note is about **product/data compatibility**, not code seams.

---

## 1. What the sub-area system does (one screen)

A user selects **any box** on a large city road network; we produce **believable vehicle traffic
inside it, fast, fully automatically**, under one hard rule:

> **No visible cheating.** A vehicle may appear/disappear only at the box **FRINGE** (roads cut by the
> selection boundary) or **off-road inside a parkingArea** (garage/lot). Never popping on a visible
> travel lane. Believable = *realistically dense but still flowing* (no permanent gridlock).

Pipeline (all vanilla-SUMO today; being ported to SumoSharp — see §4):
```
crop box out of the city net   -> creates the FRINGE (the only visible entry/exit)
deduce per-edge demand weights  from road-network TOPOLOGY (no demand files, no census):
    source=residential(local streets), sink=commercial(betweenness+parking), through=capacity
randomTrips(weights, fringe-factor) -> believable O/D; through-traffic enters/leaves at the fringe
auto_parking: internal origins/destinations -> parkingArea SINKS (the no-cheating layer)
calibrate: find the box's MAX believable density (gridlock/pop "knee") -> user dials 0-100%
run (device.rerouting + bounded teleport valve) -> record FCD -> self-contained Sim.Viz replay
```
Key results: strict no-cheating clears ~2.7 veh/lane-km; **rerouting + a 120 s teleport valve** reaches
~7 veh/lane-km at <1 % "pops". Density is calibrated per box (a knee that drops with box size). An
interactive Flask demo (`experiments/subarea/demo/`) does select→preprocess→replay end to end.

The engine features that make this work — `device.rerouting`, bounded `time-to-teleport`, parkingArea
sinks, and the **X1 RealismMask** (attention-aware "pop only where the camera isn't looking") — are
exactly the high-density work now landed in SumoSharp `main` (see `SUMOSHARP-HIGH-DENSITY-FEATURES.md`,
which you already have).

---

## 2. Why this matters to pedestrians

If the product shows a selected sub-area with **both** cars and crowds, pedestrians must obey the
**same sub-area framing and the same no-cheating rule**, or they'll break the illusion the vehicle
side works hard to preserve (a pedestrian popping into existence mid-sidewalk on camera is exactly the
cheat we forbid for cars). The two subsystems also share the cropped net, the coordinate frame, the
camera/visibility signal, and a density budget. The requirements in §3 make that alignment explicit.

Your architecture is already well-placed for this: you run a **separate** engine (ORCA crowd) that
reads the **same `net.xml`** sidewalk/`crossing`/walkingArea geometry via `PedNetworkParser`, and
car↔ped coupling is additive (`CrowdSource`/`CrowdLongitudinalConstraint`), so nothing here asks you to
change that. These are data/behaviour-contract requirements, not code-merge requests.

---

## 3. Compatibility requirements (what pedestrian modelling should honour)

1. **Same cropped sub-area net + coordinate frame.** Consume the *cropped* box `net.xml` our pipeline
   emits (SUMO net XY metres), not a whole-city net. Your SUMO-geometry bake already reads the right
   elements; please **verify it against an actual cropped sub-area net** (crossings/walkingAreas cut by
   the box boundary produce dangling stubs — the pedestrian "fringe"). *Status: architecturally fine,
   not yet verified against a crop.*

2. **The FRINGE is the pedestrian no-cheating boundary too.** Pedestrians may enter/leave the visible
   box only at the **sidewalk/crossing fringe** (walkable edges cut by the crop) or at **legitimate
   internal sinks** (building entrances, transit stops, parking board/alight points), or **off-camera**
   (see req 4). A pedestrian must not spawn/despawn on a visible sidewalk segment. *Status: unaddressed
   — the sim-LOD promotion/demotion machinery governs compute/network detail, not visual legitimacy of
   appearance. This is the biggest ped↔sub-area gap.*

3. **Auto-deduced pedestrian demand, analogous to our vehicle deduction.** We synthesise vehicle demand
   from topology (no data files): residential-weighted origins, commercial/attractor-weighted
   destinations, a fringe factor for through-movers. Pedestrians need the parallel: O/D deduced from
   walkable-space + land-use (sidewalk density, POIs, transit/parking, plazas), spawning at the fringe
   or sinks. *Status: not built — your `PEDESTRIAN-TASKS.md` P2-3 ("OD demand") is the placeholder.
   Either build it there or we can share our weight-deduction approach (`deduce_weights.py`) as a
   template.* Until then, hand-authored `personFlows` (the repo has some per city) are the stopgap.

4. **Share the RealismMask / camera visibility signal.** The vehicle side gates jam-teleport, on-lane
   spawn, and off-camera de-jam despawn by a **visible-edge set** (`Engine.SetVisibleEdges`,
   `RealismMask.cs`). Pedestrian spawn/despawn/LOD should read the **same** visibility signal so that
   "cheat only where nobody's looking" is consistent across cars and crowds — one camera frustum → one
   shared visible set → both engines gate on it. (You already treat the camera as a sim-LOD interest
   source; extend it to also gate *appearance legitimacy*, not just LOD.)

5. **Density is calibrated and dialable — coordinate the crowd budget.** We calibrate a vehicle-density
   "knee" per box and expose a 0–100 % slider. Crowds have their own believable-but-not-locked ORCA
   density limit. When both share a box: (a) pedestrians must not gridlock crossings so hard that they
   deadlock the cars (whose gridlock knee we calibrated), and (b) ideally the demo's density control
   maps to *both*. At minimum, expose a pedestrian-density knob and document its safe range like we do
   for vehicles.

6. **Slot into the produced scenario + manifest.** Our preprocessing emits a self-contained
   `scenario.sumocfg` + `manifest.json`. Pedestrian demand should attach as additional route/person
   input referenced by (or alongside) that scenario, and — stretch — the replay (`sim_viz.py`) should
   be extendable to render pedestrians from the same FCD-style trajectory stream, so one replay shows
   both. Keep pedestrian outputs in the same self-contained/offline spirit.

7. **Watch item (from the coordination note): P4.** If/when your Stage P4 adds *engine* vehicle-yields-
   at-crossing inside the lane engine, it changes real vehicle behaviour at signalised crossings — which
   can shift our **calibration knee** for any box containing such crossings. It's an additive foe-source
   (fine), but ping the vehicle side to **re-calibrate** affected boxes when it lands.

---

## 4. Engine status (shared context)

The vehicle pipeline still runs on **vanilla SUMO 1.20.0** in production, but the SumoSharp port now
implements the calibration-critical features (`device.rerouting`, `time-to-teleport`, symbolic
departs, `vTypeDistribution`, multi-file cfg, summary/statistic/fcd outputs) and the X1 RealismMask,
all golden-verified. Remaining before SumoSharp fully replaces vanilla SUMO on the *serve/replay* path:
a thin `sumo -c …`-shaped CLI adapter, `--tripinfo-output` with `arrivalLane`, and multi-occupant
`parkingArea`. Detail + evidence: `PREPROCESSING-ENGINE-REQUIREMENTS.md` §0c (SumoData repo).

---

## 5. Design docs (SumoData repo: `BagiraSystems/SumoData`, `docs/` + `experiments/subarea/`)

These are in the **SumoData** repo (not SumoSharp). Ask the owner for access or a copy if you can't
reach them; the essentials are summarised above.

- `docs/SUBAREA-METHOD.md` — **canonical method** (believability, weight deduction, no-cheating,
  required road-net data incl. parking/garages, halo=not-needed, findings, backlog, §9b known debt).
- `docs/SUBAREA-REAL-NET-FINDINGS.md` — validation on the real Geneva net (no-cheating audit, bugs,
  §8d the attention-aware-popping idea that became X1).
- `docs/RESULTS-gridlock-sweep.md` — the density knee + rerouting + teleport-valve numbers.
- `docs/RESULTS-autocalibrate.md` — per-box calibration + timings + the Windows 24-core results.
- `docs/PREPROCESSING-ENGINE-REQUIREMENTS.md` — the vanilla-SUMO→SumoSharp replacement checklist +
  **§0c** the current SumoSharp status re-assessment.
- `experiments/subarea/` — the tools: `deduce_weights.py`, `auto_parking.py`, `auto_calibrate.py`,
  `preprocess.py` (orchestrator, `--progress`/manifest/`--replay`/`--compute-budget`), `sim_viz.py`
  (+ `templates/`), `make_overview.py`, and `demo/` (the interactive Flask demo).
- Already in **SumoSharp** `main` (you can read these directly): `docs/SUMOSHARP-HIGH-DENSITY-FEATURES.md`
  (the engine feature spec that drove the high-density work) and `docs/COORDINATION-pedestrian-x-highdensity.md`.
