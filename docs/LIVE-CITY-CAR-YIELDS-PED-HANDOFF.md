# HANDOFF — cars yield to pedestrians in their path (crosswalk safety)

**Self-contained brief for a NEW session** that makes a car **STOP for a pedestrian crossing or standing in
its path** instead of weaving around it at speed. Read top-to-bottom; assumes near-zero prior context.
**Per `CLAUDE.md`: design-first** — produce and get agreement on the `design → tasks → tracker` trio in
`docs/` before editing `Engine.cs`. Facts marked **[verified]** were checked against source (file:line) this
session; the repro is already committed.

Suggested branch: **fresh off current `main`**, e.g. `claude/car-yields-crossing-ped`. Doc prefix:
`LIVE-CITY-CAR-YIELDS-PED-*`.

---

## 0. Why this session exists

Task A fixed the stopped-car *wobble* (a car held by a laterally-static ped steering sideways while stopped).
Building the crosswalk repro for it surfaced a **distinct, real unrealism** the fix deliberately does NOT
touch: a car approaching a pedestrian **walking across its lane** does **not stop** for it — it does an
**anticipatory dodge at full speed** (`posLat`→~1.4 m while `Speed`≈5 m/s) and weaves past. The owner's hard
requirement (AB-DESIGN §Task B): *"in the high-realism zone a car must NEVER crash into a ped, nor pass one
at close distance / high speed."* Weaving around a crossing ped at 5 m/s violates that. **This session makes
cars yield.**

This is **not** the wobble and **not** a junction right-of-way bug — it is the vehicle's *lateral evasion*
choosing to swerve around a dodgeable pedestrian rather than stop for it.

## 1. The repro already exists (committed) [verified]

`tests/Sim.ParityTests/CrosswalkCrossingPedTests.cs` (on `main`) — deterministic, offline, on the
`bridge-crossing-normal` fixture (single 7.2 m lane, centreline y=-3.6, +x; car departs x=0, maxSpeed 5; a
`CrowdSource` ped crosses the lane in y). It documents the two cases:

- **`MovingPedCrossingThrough_TheFixIsInert_NoStoppedFloat`** — a ped walking through: the car dodges **at
  speed** (posLat→1.41 while Speed=5), briefly brakes (binder 13, one tick), then proceeds. **It weaves past
  the crossing ped — it does not stop.** This is the behaviour to change.
- The two "stops-mid-crossing" tests capture Task A's wobble (already fixed) — for contrast, not your target.

At demo density (`LIVECITY_PEDS=800`) the same weaving is visible on real crossings. Extend
`CrosswalkCrossingPedTests` with your success assertion: *the car's front does not pass within N m of the ped
while moving faster than V* (a close-fast-pass), and it holds at/behind the crossing until the ped clears.

## 2. Root cause — the crowd-swerve prefers dodging over stopping [verified]

`Engine.ComputeLateralEvasion` (`Engine.cs:9089`). For a **crowd** threat (a `CrowdSource` pedestrian disc),
the code deliberately **skips the stop-and-stay-centred gate** and **prefers the swerve** — see the comment at
`Engine.cs:9253–9256` and the swerve-target selection at `9268–9310`:

> "For a CROWD threat (Q6 option b): PREFER the swerve — skip this stop-and-stay-centred gate so a dodgeable
> crowd agent is gone around (decelerating as needed via `CrowdLongitudinalConstraint`) rather than
> hard-stopped."

So the design **intent** is to dodge a pedestrian rather than stop. `CrowdLongitudinalConstraint`
(`Engine.cs:8582`, binder 13) *does* brake for a ped ego is still laterally overlapping, but once ego commits
to a swerve (off-centre), it is no longer overlapping → the brake releases → ego passes at speed. That is the
weave. **[verified: the repro shows binder 13 for one tick then release as posLat grows.]**

**Task A's `SuppressHeldCrowdSwerve`** (`Engine.cs` property + guard at ~`9270`) already suppresses the swerve
for a **held + laterally-static** ped (`BindingConstraint == 13 && LatSpeed ≈ 0`). **Your change generalises
this to the safety case:** in the high-realism zone, a car must not swerve past *any* ped in its path at
speed — it must yield (stop/creep behind) until the ped clears. The static-only gate is the seam to widen (or
a sibling guard), but carefully — see §3.

## 3. Design direction (yours to make precise)

The owner's Task B framing (AB-DESIGN §Task B, `LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`) is a **world-space
hard ped-safety guard**, NOT a lane-projection test. Options to weigh in your design doc:

- **Suppress-the-swerve-and-let-the-brake-dominate:** inside the realism zone, when a ped disc is in ego's
  swept path, do NOT prefer the swerve (so `CrowdLongitudinalConstraint`'s stop stays engaged and ego holds
  behind the ped). This is the smallest change — the generalisation of `SuppressHeldCrowdSwerve` to moving
  peds, zone-gated. Risk: the "velocity-0 over-brake" lesson (`GateOrcaPedsOnCrossing` cost 15% throughput) —
  a *moving* ped must be *followed/anticipated*, not treated as a dead stop; preserve ped velocity so ego
  yields to where the ped WILL be, and resumes the instant its path is clear.
- **World-space speed cap / emergency brake** keyed on nearest-ped world distance inside the zone, layered
  over `CrowdLongitudinalConstraint` — the explicit "never close-fast-pass" backstop.

**Do NOT reintroduce gridlock:** Task A's repro showed a car will now *wait* behind a ped that lingers in its
lane; a too-aggressive yield stalls traffic. Gate on the realism zone and preserve ped velocity so a
genuinely-crossing ped is yielded-to only while it's actually in the path.

## 4. Parity & safety

Parity-inert by construction: the whole crowd path is gated on `Engine.CrowdSource != null`, which **no golden
or bench attaches** — so `Sim.ParityTests` stays **664/4** byte-identical and `Sim.Bench` hash
**`D96213B7BB4021A7`** (par==single) unchanged. Keep every new car-side reaction behind that gate (or a
demo/zone flag). Run `Sim.LiveCity.Tests` **without** `--no-build` (not in `Traffic.sln`).

## 5. Boundary & coordination (READ — this is the overlap the owner asked about)

**Clear of the two RUNNING sessions at the mechanism level** [verified this session]:
- **F3 junction overlap** (`claude/f3-junction-overlap-handoff-okf5nu`) edits `Engine.cs` **junction** methods
  (`JunctionYieldConstraint` ~6642–7134, `AdaptToJunctionLeader` ~7934, `KeepClearConstraint` ~7221). You edit
  `ComputeLateralEvasion` (~9089–9310) and `CrowdLongitudinalConstraint` (~8582) — **different methods.** Same
  FILE, so rebase often and keep edits localized.
- **ped-LOD-lifecycle** (`claude/livecity-ped-lod-lifecycle-bylitj`) edits `src/Sim.Pedestrians/Lod/` — **no
  overlap.**
- **Shared files** (all sessions touch): `LiveCitySim.cs` (wiring — add your own line/region) and
  `src/Sim.Viz/Program.cs` (diagnostics — own method). Coordinate by rebasing on `main`, not by locking.

**This session OWNS the car→ped yield mechanism (Task B-guard).** It overlaps the **ped–vehicle avoidance**
workstream (`LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`), which is **NOT started**. To keep one-owner-per-
mechanism: **you take Task B-guard (car stops/yields for a ped, incl. this crosswalk case) and the
`ComputeLateralEvasion`/`CrowdLongitudinalConstraint` car-side reaction.** If the ped–vehicle session is later
started, it owns only **C5 (ped-avoids-car disc feed)** + **B-api (`ExternalObstacle`→`WorldDisc` refactor)** —
the ped-side and the API plumbing — and must NOT re-touch the car→ped yield you own. The coordination doc and
that handoff have been updated to record this split.

## 6. Success conditions (refine in your design)

1. **Extend `CrosswalkCrossingPedTests`:** with a ped crossing at speed in front of the car, assert the car
   **yields** — its front never passes within N m of the ped while moving faster than V (no close-fast-pass) —
   and it resumes promptly once the ped clears (no permanent stall).
2. **Demo:** at `LIVECITY_PEDS=800`, extend `--live-city-orcatrace` (or add a check) so **0** moving cars
   close-fast-pass a ped inside the high-realism zone. Outside the zone unchanged.
3. **No new gridlock:** `DenseFlow…NoGridlock` green; `carArrivedTotal` within noise of baseline.
4. **Parity `664/4` byte-identical + bench `D96213B7BB4021A7` + `Sim.LiveCity.Tests` green** — proving the
   yield is inert on goldens (CrowdSource/zone-gated).

## 7. Key files

- `src/Sim.Core/Engine.cs` — `ComputeLateralEvasion` (`:9089`; crowd-swerve prefer-gate `:9253–9310`;
  `SuppressHeldCrowdSwerve` guard ~`:9270`), `CrowdLongitudinalConstraint` (`:8582`, binder 13),
  `SuppressHeldCrowdSwerve` property (realism knobs block, grep it).
- `src/Sim.LiveCity/LiveCitySim.cs` — crowd/zone wiring (`CrowdSource`, the realism-zone `InterestSource`,
  the `SuppressHeldCrowdSwerve` opt-in line — model your zone gate next to it).
- `tests/Sim.ParityTests/CrosswalkCrossingPedTests.cs` — the committed repro to extend.
- `src/Sim.Viz/Program.cs` — `--live-city-orcatrace` (the Task-B repro tool), `--live-city-cartrace`.
- Context: `docs/LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md` §F2 "Crosswalk scope", `docs/LIVE-CITY-REALISM-AB-DESIGN.md`
  §Task B, `docs/LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md` §4, `docs/COORDINATION-livecity-realism-sessions.md`.

## 8. Open questions for your design phase

1. Zone-gate the yield, or make it global for any `CrowdSource` ped? (Global risks throughput; zone matches
   the owner's "high-realism zone" framing.)
2. Widen `SuppressHeldCrowdSwerve`'s gate to include moving peds in-zone, or add a sibling world-space yield
   layered over `CrowdLongitudinalConstraint`? Which keeps ped-velocity anticipation (avoids the over-brake)?
3. How to distinguish "ped genuinely in my path, yield" from "ped clearing / to the side, proceed" without
   lane projection (owner: world-space)? Reuse the swept-footprint / predicted-lateral logic already in
   `ComputeLateralEvasion`.
4. Do you also fold in B-api now (retire the string `ExternalObstacle` onto `WorldDisc`), or leave that to the
   ped–vehicle session and keep this session car-yield-only?
