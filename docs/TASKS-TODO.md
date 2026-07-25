# TASKS-TODO.md — Active work queue (open items only)

The short, live queue. **Completed work + the full detail/characterization of everything below lives in
the archive `TASKS-DONE.md`** — this file is just the open items with pointers. Other sessions:
coordinate here (add/claim items), keep it short, move finished items' detail to `TASKS-DONE.md`.

Iron law (unchanged): `dotnet test tests/Sim.ParityTests -c Release` = **657/4** byte-identical;
`Sim.Bench` hash **`D96213B7BB4021A7`** (par==single); no `System.Random`. `Sim.LiveCity.Tests` =
**43/43** once the arbitrary-net PR lands (25 base + the road-net/route-graph suite it adds; was 25/25).

**In-flight by session** (live-city cluster; full boundary + no-touch lists in
`docs/COORDINATION-livecity-realism-sessions.md`):

| Session | Branch | Status | Scope / tracker |
|---|---|---|---|
| realism-A/B | `claude/livecity-realism-fixes-vr4k4b` | **A REOPENED** — fix reverted | Task A (stopped-car lateral wobble): first fix caused car–car overlaps, reverted (`c30dee6`+); needs targeted redesign |
| ped–vehicle avoidance | `claude/livecity-ped-vehicle-avoidance` | to be started | car↔ped coupling: B + #4 + #5 · `LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md` |
| arbitrary-net | `claude/discussion-eqp53m` | **complete — PR to main** | net import · `SumoRouteGraphNav` · capability degrade · single zone · `RegionPlan` (+ Engine gate fix) · fixture + tests — all DONE; **C5 seam BLOCKED** (ped–vehicle session) · W4 handed off. Detail: `TASKS-DONE.md` → "Arbitrary road-net import"; `LIVE-CITY-ARBITRARY-NET-{DESIGN,TASKS,TRACKER}.md` |

*W4 (multi-camera zones) = unallocated. Sections below without a session tag are unclaimed backlog —
not a repo-wide board; other `claude/*` branches are not tracked here.*

---

## Live-city realism (high-realism-zone demo) — active
Detail: `docs/LIVE-CITY-REALISM-1-2-DESIGN.md` (shipped #1/#2), `docs/LIVE-CITY-REALISM-ATTEMPT-LOG.md`
(trail), `docs/LIVE-CITY-REALISM-AB-DESIGN.md` (A/B brief), `TASKS-DONE.md` → "Realism violations in
high-realism zones".

**Session ownership (coordinated 2026-07):** this branch (`claude/livecity-realism-fixes-vr4k4b`) owns
**A only**. **B + C5 (#5) + the wandering-ORCA residual (#4) are ONE car↔ped coupling workstream** → the
**ped–vehicle avoidance** session (`claude/livecity-ped-vehicle-avoidance`, to be started), NOT this one —
one owner for one mechanism. The arbitrary-net session (`claude/discussion-eqp53m`) owned net import +
`SumoRouteGraphNav`/`IPedNavigation` + the single realism zone + `RegionPlan` and has **delivered** them
(PR to main — see `TASKS-DONE.md` → "Arbitrary road-net import"), leaving the seams in place; its C5
enablement is **BLOCKED** for the ped–vehicle session (which will road-net-enable + zone-bound the fed disc
set on the seam left behind). Multi-camera zones (W4) also handed off. Full boundary + no-touch lists:
`docs/COORDINATION-livecity-realism-sessions.md`. Briefs: `docs/LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`,
`docs/LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md`.

- [ ] **A — stopped car wiggles sideways at a crosswalk — REOPENED** (first fix reverted). The wobble is
  `ComputeLateralEvasion`'s crowd-swerve oscillating posLat while a car is held (nearly) stopped by a ped
  (`_sublane` false in the demo, so the SL2015 driver named in the brief is dead code). The first attempt
  (`Engine.FreezeLateralWhenStopped`: freeze ALL lateral commit below `LaneChangeMinSpeed`, `03986a7`) was
  **too blunt** and **caused car–car overlaps** — A/B-confirmed: it also pinned cars **mid-lane-change**
  (straddling two lanes → `gap=Infinity` to trailing cars → followers creep in → overlap: veh17/26 0.00m,
  veh18/49, veh117/26). **Reverted** (demo opt-in off; Engine flag now default-off, `LIVECITY_FREEZELAT=1`
  to experiment). **Redesign direction:** suppress only the held-car **crowd-swerve** (don't try to dodge
  a ped you're stopped for) while leaving lane-change completion / recentering intact — NOT a blanket
  lateral freeze. **Missing guard to add first:** a demo-level "no two vehicle footprints overlap" (+ red
  respected, + minGap) invariant over N steps at density — none exists today, which is why this slipped
  past parity 660/4 + LiveCity 25/25. Repro/brief: `docs/LIVE-CITY-REALISM-AB-DESIGN.md` §Task A.
- [ ] **B — car close-fast-passes ORCA peds on internal junction lanes** *(ped–vehicle avoidance session — to be started)*.
  High-realism-zone world-space hard ped-safety guard (car-stops-before-ped, NOT lane-projection based) +
  unify the string `ExternalObstacle` dodge/stop onto the `WorldDisc` seam. Briefs: AB-DESIGN §Task B,
  `LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`.
- [ ] **Realism #3 — low-power peds DISAPPEAR on promotion** into the pocket (re-appear as ORCA later);
  one-sided `PedLodManager` promote handoff. (task #25)
- [ ] **Realism #4 — ORCA peds leaving the zone STAY ORCA and wander** off-route; demotion doesn't fire /
  doesn't restore the sidewalk route. *(ped–vehicle avoidance bucket; overlaps B's "wandering ORCA" residual)* (task #25)
- [ ] **Realism #5 (= arbitrary-net task "C5"; distinct from Group-C C5 `keepClear` below) — ORCA peds
  don't dodge a car standing on the crosswalk**; needs a car→ped obstacle feed (mirror of the ped→car
  `CrowdSource`). *(ped–vehicle avoidance session)* (task #26)
- [ ] **Realism #6 (LOW PRIORITY)** — low-power peds merge to a SINGLE junction point and idle there
  (occasionally recolour ORCA); randomize ped destinations / idle spots.
- [ ] **W4 — multiple / large / overlapping camera realism zones** *(handed off; unallocated — ped–vehicle
  avoidance or a later dedicated session)*. N ped `InterestSource`s, N-zone car LC-realism, `SetLcRealismZones` API, re-point
  the C5 disc-feed bound at the zone union, optional bit-identical `OrcaCrowd` disc index (the one `Sim.Core`
  touch, must stay parity-inert). Handoff: `docs/LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md`.

## Demo integrity (from the 2026-07 replay review — realism-A/B session)
Full evidence + root causes: **`docs/LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md`** (F1–F4). Diagnostics:
`--live-city-cartrace` (authoritative per-car) and `--live-city-drcheck` (DR-render + authoritative overlap
check). **Order finalized after two analyses — F3 is pre-existing CORE (localized vs `main`), and F3 masks
F2 in aggregate → the F2 guard must be targeted:** F4a → F1 → Task A redo (this session); **F3 routed to
core junction work**; F4b deferred until F3 fixed.

- [ ] **F4a — targeted F2 straddle guard (DO FIRST, this session).** Assert no stopped/slow car has
  `|PosLat|` past its lane edge (F2's exact mechanism: a frozen mid-lane-change car straddling). Clean of
  F3, green now, actually trips on the freeze regression — the guard that protects the Task A redo. §F4.
- [ ] **F1 — DR reconstruction renders a braking car through a red/junction** (render-only; engine respects
  the light). Clamp the reconstructed arc so a decelerating car can't render past its stop position + damp
  the look-ahead for near-stopped cars + verify `accel` is published. Files: `DrClock.cs`,
  `DrExtrapolation.Arc`, `KinematicReconstructor.cs`, `VizReplayBuilder.cs`. §F1.
- [ ] **F2 — Task A redo** (fix reverted): targeted crowd-swerve suppression (NOT a blanket lateral freeze),
  guarded by F4a. See the reopened **A** item above + `LIVE-CITY-REALISM-AB-DESIGN.md` §Task A. §F2.
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
