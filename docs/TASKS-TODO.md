# TASKS-TODO.md — Active work queue (open items only)

The short, live queue. **Completed work + the full detail/characterization of everything below lives in
the archive `TASKS-DONE.md`** — this file is just the open items with pointers. Other sessions:
coordinate here (add/claim items), keep it short, move finished items' detail to `TASKS-DONE.md`.

Iron law (unchanged): `dotnet test tests/Sim.ParityTests -c Release` = **661/4** byte-identical;
`Sim.Bench` hash **`D96213B7BB4021A7`** (par==single); no `System.Random`. `Sim.LiveCity.Tests` =
**43/43** once the arbitrary-net PR lands (25 base + the road-net/route-graph suite it adds; was 25/25).

**In-flight by session** (live-city cluster; full boundary + no-touch lists in
`docs/COORDINATION-livecity-realism-sessions.md`):

| Session | Branch | Status | Scope / tracker |
|---|---|---|---|
| realism-A/B | `claude/livecity-realism-fixes-vr4k4b` | **A DONE (redo)** | Task A (stopped-car lateral wobble): first blanket-freeze fix caused car–car overlaps (reverted); targeted redo shipped — `Engine.SuppressHeldCrowdSwerve` (held static-ped crowd-swerve suppression), guarded by F4a. Parity 661/4, bench `D96213B7BB4021A7`, LiveCity 27/27 |
| ped–vehicle avoidance | `claude/livecity-ped-vehicle-avoidance` | to be started | car↔ped **coupling** only: B + #5 (car→ped disc feed) · `LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`. (#4 moved to ped-LOD-lifecycle — its root is demotion, not coupling.) |
| ped-LOD-lifecycle | `claude/livecity-ped-lod-lifecycle` *(to be started — SAFE to run in parallel now)* | to be started | **ped LOD promote/demote switching** (low↔high power): #3 (promote handoff — ped vanishes) + #4 (demote doesn't fire / route not restored — wandering ORCA) + #6 (idle clustering / randomize destinations). Edit surface = `src/Sim.Pedestrians/Lod/` (+ demand + viz snapshot); **does NOT touch any car-side session's surface** (Engine lateral/longitudinal, OrcaCrowd external-disc, ExternalObstacle API, net import). Brief: **`docs/LIVE-CITY-PED-LOD-LIFECYCLE-HANDOFF.md`**. See "Parallel-safe" note below. |
| arbitrary-net | `claude/discussion-eqp53m` | **complete — PR to main** | net import · `SumoRouteGraphNav` · capability degrade · single zone · `RegionPlan` (+ Engine gate fix) · fixture + tests — all DONE; **C5 seam BLOCKED** (ped–vehicle session) · W4 handed off. Detail: `TASKS-DONE.md` → "Arbitrary road-net import"; `LIVE-CITY-ARBITRARY-NET-{DESIGN,TASKS,TRACKER}.md` |

*W4 (multi-camera zones) = unallocated. Sections below without a session tag are unclaimed backlog —
not a repo-wide board; other `claude/*` branches are not tracked here.*

**Parallel-safe (ped-LOD-lifecycle vs the car-side sessions).** The LOD promote/demote mechanism
(`PedLodManager`, `InterestSource`, route controllers in `src/Sim.Pedestrians/Lod/`) is structurally
separate from every car-side session's no-touch surface. The one shared interface is
`PedLodManager.HighPowerFootprints` → `ICrowdFootprintSource` → `Engine.CrowdSource` — a **produce/consume**
seam: the LOD session *produces* the footprint source, the car sessions (realism-A/B Task A, ped–vehicle
C5) *consume* it. Rule: the LOD session may change promote/demote **internals** (timing, route re-derivation,
the disappear/idle fixes) freely, but must **not** change the `ICrowdFootprintSource` contract or
`HighPowerFootprints` semantics without pinging the car sessions. Two files are touched by more than one
session — coordinate by editing your **own** method/region: `LiveCitySim.cs` (integration wiring) and
`OrcaCrowd.cs` (LOD uses Add/Remove agent lifecycle; ped–vehicle uses `SetExternalObstacles` — different
methods). Parity is untouched either way (the whole ped/LOD path is gated on `CrowdSource != null`, which no
golden attaches → still **661/4** byte-identical).

---

## Live-city realism (high-realism-zone demo) — active
Detail: `docs/LIVE-CITY-REALISM-1-2-DESIGN.md` (shipped #1/#2), `docs/LIVE-CITY-REALISM-ATTEMPT-LOG.md`
(trail), `docs/LIVE-CITY-REALISM-AB-DESIGN.md` (A/B brief), `TASKS-DONE.md` → "Realism violations in
high-realism zones".

**Session ownership (coordinated 2026-07):** this branch (`claude/livecity-realism-fixes-vr4k4b`) owns
**A only** (A now DONE). **B + C5 (#5) are ONE car↔ped coupling workstream** → the **ped–vehicle avoidance**
session (`claude/livecity-ped-vehicle-avoidance`, to be started), NOT this one — one owner for one mechanism.
**The ped LOD promote/demote lifecycle (#3 + #4 + #6) is a SEPARATE, parallel-safe workstream** → the
**ped-LOD-lifecycle** session (`claude/livecity-ped-lod-lifecycle`): its edit surface (`src/Sim.Pedestrians/Lod/`)
does not overlap any car-side session's no-touch list, and it only *produces* the `ICrowdFootprintSource` the
car sessions consume (#4 was previously mis-bucketed with ped–vehicle; its root is the demote trigger + route
restore, not coupling). See the in-flight table's "Parallel-safe" note for the exact boundary. The arbitrary-net session (`claude/discussion-eqp53m`) owned net import +
`SumoRouteGraphNav`/`IPedNavigation` + the single realism zone + `RegionPlan` and has **delivered** them
(PR to main — see `TASKS-DONE.md` → "Arbitrary road-net import"), leaving the seams in place; its C5
enablement is **BLOCKED** for the ped–vehicle session (which will road-net-enable + zone-bound the fed disc
set on the seam left behind). Multi-camera zones (W4) also handed off. Full boundary + no-touch lists:
`docs/COORDINATION-livecity-realism-sessions.md`. Briefs: `docs/LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`,
`docs/LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md`.

- [x] **A — stopped car wiggles sideways at a crosswalk — DONE (targeted redo).** The wobble was
  `ComputeLateralEvasion`'s crowd-swerve steering posLat a full lane-width while a car is held (nearly)
  stopped by a ped (`_sublane` false in the demo, so the SL2015 driver named in the brief is dead code). The
  first attempt (`Engine.FreezeLateralWhenStopped`: freeze ALL lateral commit below `LaneChangeMinSpeed`) was
  **too blunt** and **caused car–car overlaps** — it also pinned cars **mid-lane-change** (straddling → `gap=Infinity`
  → followers creep in → veh17/26, 18/49, 117/26). **Reverted, blanket clamp removed.** **Shipped redo:**
  `Engine.SuppressHeldCrowdSwerve` (default false; demo opt-in **on**, `LIVECITY_HELDSWERVE=0` disables) —
  in the crowd-swerve branch, when ego is HELD (`BindingConstraint == 13`) AND the ped is laterally STATIC
  (`LatSpeed ≈ 0`), recentre and wait in-lane instead of swerving. Only recentres (can't straddle → F2
  structurally impossible); leaves at-speed dodges / passes / lane-changes untouched (empirically: held =
  `binder 13`, passing = `binder 3`). **Guard added:** F4a straddle detector
  (`DemoAuthoritative_NoStoppedCarStraddlesPastItsLane`). Verified: parity **661/4**, bench
  `D96213B7BB4021A7`, LiveCity **27/27**, no new/worse overlap class (worst 3.035 m F3 + max pairs/frame 4
  unchanged; fix adds only 0.74 m / 0.09 m normal-lane overlaps, shallower than 6 pre-existing). Detail:
  `docs/LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md` §F2, `docs/LIVE-CITY-REALISM-AB-DESIGN.md` §Task A.
- [ ] **B — car close-fast-passes ORCA peds on internal junction lanes** *(ped–vehicle avoidance session — to be started)*.
  High-realism-zone world-space hard ped-safety guard (car-stops-before-ped, NOT lane-projection based) +
  unify the string `ExternalObstacle` dodge/stop onto the `WorldDisc` seam. Briefs: AB-DESIGN §Task B,
  `LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`.
- [ ] **Realism #3 — low-power peds DISAPPEAR on promotion** into the pocket (re-appear as ORCA later);
  one-sided `PedLodManager` promote handoff. *(ped-LOD-lifecycle session — parallel-safe, see table note)* (task #25)
- [ ] **Realism #4 — ORCA peds leaving the zone STAY ORCA and wander** off-route; demotion doesn't fire /
  doesn't restore the sidewalk route. *(ped-LOD-lifecycle session — its root is the `PedLodManager` demote
  trigger + route restore, NOT car coupling; fixing demotion also removes the "wandering ORCA near cars"
  symptom the ped–vehicle session cared about. Moved out of the ped–vehicle bucket.)* (task #25)
- [ ] **Realism #5 (= arbitrary-net task "C5"; distinct from Group-C C5 `keepClear` below) — ORCA peds
  don't dodge a car standing on the crosswalk**; needs a car→ped obstacle feed (mirror of the ped→car
  `CrowdSource`). *(ped–vehicle avoidance session)* (task #26)
- [ ] **Realism #6 (LOW PRIORITY)** — low-power peds merge to a SINGLE junction point and idle there
  (occasionally recolour ORCA); randomize ped destinations / idle spots. *(ped-LOD-lifecycle session —
  parallel-safe; ped demand/destination assignment, no car-side surface)*
- [ ] **W4 — multiple / large / overlapping camera realism zones** *(handed off; unallocated — ped–vehicle
  avoidance or a later dedicated session)*. N ped `InterestSource`s, N-zone car LC-realism, `SetLcRealismZones` API, re-point
  the C5 disc-feed bound at the zone union, optional bit-identical `OrcaCrowd` disc index (the one `Sim.Core`
  touch, must stay parity-inert). Handoff: `docs/LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md`.

## Demo integrity (from the 2026-07 replay review — realism-A/B session)
Full evidence + root causes: **`docs/LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md`** (F1–F4). **Resume/handoff state:
`docs/LIVE-CITY-DEMO-INTEGRITY-RESUME.md`** (read first when picking this up fresh). Diagnostics:
`--live-city-cartrace` (authoritative per-car) and `--live-city-drcheck` (DR-render + authoritative overlap
check). **Order finalized after two analyses — F3 is pre-existing CORE (localized vs `main`), and F3 masks
F2 in aggregate → the F2 guard must be targeted:** F4a → F1 → Task A redo (this session); **F3 routed to
core junction work**; F4b deferred until F3 fixed.

- [x] **F4a — targeted F2 straddle guard — DONE** (`DemoAuthoritative_NoStoppedCarStraddlesPastItsLane`).
  Detects F2 by its true signature: `PosLat` frozen unchanged past the lane edge (>1.2 m) for ≥10
  consecutive stopped ticks (raw peak `|PosLat|` can't separate — the crowd-swerve reaches ~5 m both ways).
  Verified: green freeze-off (0 ticks); FAILS freeze-on (58 ticks, Vehicle#19.1 @3.18 m); LiveCity 27/27.
- [ ] **F1 — DR reconstruction renders a braking car through a red/junction** (render-only; engine respects
  the light). Clamp the reconstructed arc so a decelerating car can't render past its stop position + damp
  the look-ahead for near-stopped cars + verify `accel` is published. Files: `DrClock.cs`,
  `DrExtrapolation.Arc`, `KinematicReconstructor.cs`, `VizReplayBuilder.cs`. §F1.
- [x] **F2 — Task A redo — DONE**: targeted crowd-swerve suppression (`Engine.SuppressHeldCrowdSwerve`, NOT a
  blanket lateral freeze), guarded by F4a. Empirically discriminated by `binder 13` (held) vs `binder 3` (passing).
  See the **A** item above + `LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md` §F2. §F2.
- [ ] **F3 — pre-existing junction-overlap engine bug — ROUTE TO CORE JUNCTION WORK (not this session).**
  LOCALIZED: present on `main` too (identical worst pair `veh134/veh38`, 3.035 m) — long-standing, not a
  realism regression. Cars on crossing internal junction lanes overlap ~3 m. Into-occupied / conflict-point
  family (`LANE-CHANGE-OVERLAP-*`, `ISSUE2-JUNCTION-*`, `LIVE-CITY-15-INTO-OCCUPIED-DESIGN.md`). Blocks the
  clean zero-overlap invariant (F4b). §F3.
- [ ] **F4b — general zero-overlap invariant (DEFERRED until F3 fixed).** Tighten the committed authoritative
  test + add a DR-render overlap check, asserting ZERO once F3 is resolved. The current
  `DemoCarOverlapInvariantTests` stays as an F3 characterization + gross-regression tripwire meanwhile. §F4.

## Viewer / demo bugs
- [ ] **Raylib replay: scrubbing the timeline makes cars jerk/jump-back** and never recover. (task #10)

## Deferred (owner will action later)
- [ ] **Detach the live-city DEMO data from the LOCKED regression fixture** — `scenarios/_ped/demo_city/box`
  is both the demo dataset and a committed regression fixture. Detail: `TASKS-DONE.md` → "Deferred — detach
  the live-city DEMO data".

## Parity / realism roadmap — characterized, NOT yet briefed
Future SUMO-parity + realism ladder. Each is a one-liner here; the full characterization (references,
scenarios, scope) is in `TASKS-DONE.md`. Pick one → write its briefing → move the detail's status there.

- [ ] **Group A remaining** — A2 overtaking (speed-gain lane change); A-impatience (junction-yield
  arrival-time gap acceptance, DEFERRED). (`TASKS-DONE.md` → Group A)
- [ ] **Group C — realism beyond the deterministic phase-1 core** (`TASKS-DONE.md` → Group C):
  C1 statistical parity `sigma>0` (do first — unblocks the rest); C2 strategic route-driven lane changes;
  C4 remaining right-of-way (right-before-left, roundabouts, stop signs); C5 junction-blocking avoidance
  (`keepClear`); C6 actuated/adaptive TLs + yellow decision; C7 `speedFactor` distribution; C8 ballistic
  integration + `actionStepLength>1`; C9 cooperative lane changes; C10 continuous lateral changes;
  C11 alt car-following (IDM, ACC/CACC); C12 pedestrians & crossings / public transport.
- [ ] **Group D — FastDataPlane ECS readiness** (make the engine FDP-shaped; readiness not integration).
  (`TASKS-DONE.md` → Group D)
- [ ] **Group E remaining** — opposite-overtake OV deferred items (D1 cross-lane hard-brake backstop,
  D2/D3), see `OV-REMAINING.md` + `TASKS-DONE.md` → Group E "Remaining".

---
*Split from the old monolithic `TASKS.md` (grown to ~2.5k lines). This file = open items; `TASKS-DONE.md`
= archive with full detail. Keep this one short.*
