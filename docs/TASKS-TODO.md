# TASKS-TODO.md — Active work queue (open items only)

The short, live queue. **Completed work + the full detail/characterization of everything below lives in
the archive `TASKS-DONE.md`** — this file is just the open items with pointers. Other sessions:
coordinate here (add/claim items), keep it short, move finished items' detail to `TASKS-DONE.md`.

Iron law (unchanged): `dotnet test tests/Sim.ParityTests -c Release` = **657/4** byte-identical;
`Sim.Bench` hash **`D96213B7BB4021A7`** (par==single); `Sim.LiveCity.Tests` **25/25**; no `System.Random`.

**In-flight by session** (live-city cluster; full boundary + no-touch lists in
`docs/COORDINATION-livecity-realism-sessions.md`):

| Session | Branch | Status | Scope / tracker |
|---|---|---|---|
| realism-A/B | `claude/livecity-realism-fixes-vr4k4b` | **A DONE** (`03986a7`) | Task A (stopped-car lateral wobble) — shipped; no further active task (B handed off) |
| ped–vehicle avoidance | `claude/livecity-ped-vehicle-avoidance` | to be started | car↔ped coupling: B + #4 + #5 · `LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md` |
| arbitrary-net | `claude/discussion-eqp53m` | active | net import · `SumoRouteGraphNav` · single zone · `RegionPlan` · C5 seam · `LIVE-CITY-ARBITRARY-NET-{TASKS,TRACKER}.md` (on that branch) |

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
one owner for one mechanism. The arbitrary-net session (`claude/discussion-eqp53m`) owns net import +
`SumoRouteGraphNav`/`IPedNavigation` + the single realism zone + `RegionPlan`, delivers the seams, and has
marked its C5 enablement **BLOCKED** pending the ped–vehicle session (it will later only road-net-enable +
zone-bound the fed disc set). Multi-camera zones (W4) also handed off. Full boundary + no-touch lists:
`docs/COORDINATION-livecity-realism-sessions.md`. Briefs: `docs/LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`,
`docs/LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md`.

- [x] **A — stopped car wiggles sideways at a crosswalk — DONE** (`03986a7`). Demo-gated
  `Engine.FreezeLateralWhenStopped` clamp at the lateral commit choke (`Engine.cs` ~9587), parity-inert
  (flag default false). **Diagnosis correction:** the demo sets no `lateral-resolution` (`_sublane`
  false), so the SL2015 sublane driver named in the brief is dead code; the real lateral path is
  `ComputeLateralEvasion`'s crowd-swerve — the commit-choke clamp is path-agnostic and handles it. Guard:
  `tests/Sim.ParityTests/StoppedCarLateralFreezeTests.cs` (freeze-OFF reproduces the wobble, freeze-ON
  freezes it). Gates: parity 660/4, LiveCity 25/25, bench `D96213B7BB4021A7`. Real-demo `veh218` posLat
  swing 3.0 m → 0.00, resumes cleanly. Note: a stopped car now waits rather than swerving around an
  obstacle until moving ≥ `LaneChangeMinSpeed` (intended crosswalk-hold behavior; boundary with Task B).
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
