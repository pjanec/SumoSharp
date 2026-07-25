# TASKS-TODO.md — Active work queue (open items only)

The short, live queue. **Completed work + the full detail/characterization of everything below lives in
the archive `TASKS-DONE.md`** — this file is just the open items with pointers. Other sessions:
coordinate here (add/claim items), keep it short, move finished items' detail to `TASKS-DONE.md`.

Iron law (unchanged): `dotnet test tests/Sim.ParityTests -c Release` = **657/4** byte-identical;
`Sim.Bench` hash **`D96213B7BB4021A7`** (par==single); `Sim.LiveCity.Tests` **25/25**; no `System.Random`.

---

## Live-city realism (high-realism-zone demo) — active
Detail: `docs/LIVE-CITY-REALISM-1-2-DESIGN.md` (shipped #1/#2), `docs/LIVE-CITY-REALISM-ATTEMPT-LOG.md`
(trail), `TASKS-DONE.md` → "Realism violations in high-realism zones".

- [ ] **A + B (engine-core, HANDED OFF)** — full brief in `docs/LIVE-CITY-REALISM-AB-DESIGN.md`.
  A: stopped car wiggles sideways (sublane `PosLat` drift) when held at a crosswalk → demo-gated
  freeze-lateral-when-stopped. B: cars close-fast-pass ORCA peds on internal junction lanes → a
  high-realism-zone world-space hard ped-safety guard, unifying with the engine's external-agent
  dodge/stop mechanism (string-name API wants a world-disc redesign). Owner-approved; for a separate session.
- [ ] **Realism #3 — low-power peds DISAPPEAR on promotion** into the pocket (re-appear as ORCA later);
  one-sided `PedLodManager` promote handoff. (task #25)
- [ ] **Realism #4 — ORCA peds leaving the zone STAY ORCA and wander** off-route; demotion doesn't fire /
  doesn't restore the sidewalk route. (task #25; overlaps B's "wandering ORCA" residual)
- [ ] **Realism #5 — ORCA peds don't dodge a car standing on the crosswalk**; needs a car→ped obstacle
  feed (mirror of the ped→car `CrowdSource`). (task #26)
- [ ] **Realism #6 (LOW PRIORITY)** — low-power peds merge to a SINGLE junction point and idle there
  (occasionally recolour ORCA); randomize ped destinations / idle spots.

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
