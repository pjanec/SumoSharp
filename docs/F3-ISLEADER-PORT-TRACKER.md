# F3 — tracker: `MSVehicle::isLeader` port

Design: `docs/F3-ISLEADER-PORT-DESIGN.md` · Tasks (with success conditions):
`docs/F3-ISLEADER-PORT-TASKS.md` · Running record: `docs/F3-SESSION-LOG.md`

A box is ticked **only** when the reviewer has confirmed that task's success conditions first-hand —
read the diff, checked the tests assert the real condition, and re-ran the gate. An implementor's
report of "done" is not sufficient (CLAUDE.md, the orchestration loop).

## Stage 1 — data plumbing (parity-inert by construction)

- [x] **T2.1** `LinkIndexByInternalLane` (both cont stages) + `EntryConnectionByLink`
      (`getCorrespondingEntryLink`) — 5 success conditions, incl. the all-nets sweep.
      **Confirmed** (`8b9f3d6`): diff and tests read first-hand, all four surfaces re-run —
      `Sim.ParityTests` **702/4/0** (689 + 13 new), `Sim.Bench` **`D96213B7BB4021A7`** par == single,
      LiveCity **48/48**, Pedestrians **272/272**, five gridlock diagnostics green by name.
      Review found and fixed a real weakness: the all-nets sweep wrapped each parse in a `catch` and
      asserted only `checkedLinks > 0`, so a parser regression would have skipped every in-loop
      assertion while still passing. Floors re-derived from the measured corpus (134 nets parse, 2927
      RoW junctions, 37426 `intLanes` entries) and set to 120 nets / 30000 links.
- [x] **T2.2** three `long` timestamps on `VehicleRuntime`, assigned at the lane-advance seam,
      **written but never read** — 3 success conditions, incl. `CET == MAX` in the cont bay.
      **Confirmed** (`c4e659b`): `Sim.ParityTests` **705/4/0**, `Sim.Bench` **`D96213B7BB4021A7`**
      identical serial and parallel, LiveCity **48/48**, Pedestrians **272/272**, five diagnostics green.
      Parity-inertness confirmed **structurally**, not just numerically: a field-read audit over `src/`
      shows every read is either inside the test-only accessor `TryGetJunctionEntryTimesForTest`
      (whose sole caller in the repo is the new test file) or a self-contained read-then-write inside
      `AssignJunctionEntryTimestamps`. No planning, yield, lane-change or execution path reads them.
      The measured cont trace matches design §2b exactly: `CET` holds `MAX` for 119 steps in the
      stage-1 bay, then stage 2 stamps `CET=439` while `ET` renews from `ETN=320`.
      Two initially-failing assertions were the **test's** over-strong assumptions, not the port
      (a vehicle can cross two junction boundaries in one step; and veh 102 traverses earlier
      junctions before 2336). Both replaced with a stronger, true whole-trace invariant.

## Stage 2 — the decision (flag-gated, default OFF)

- [x] **T2.3** `IsLeader` + `ResponseFor` + a separately callable tie-break — **confirmed** (`71ce789`):
      297 insertions / **0 deletions**, no caller, both audits clean; predicates verified against
      `MSLink.h:437-454`; ordinal test pins the culture-vs-ordinal disagreement; antisymmetry Theory's
      three constants verified to land in three *different* phase classes.
- [x] **T2.4a** `GapForIsLeader` — the gap derivation incl. `contLane ⇒ -DBL_MAX` and the
      logical-vs-normal predecessor distinction (`12e441d`).
- [x] **T2.4b** wired into arm 5 behind `JunctionIsLeaderGate` (default OFF) (`12e441d`). Flag-off
      preserved by construction: the sole deleted line is `if (!respondsTo` → `else if (!respondsTo`,
      condition and body byte-identical. Flag OFF measured byte-identical on all four surfaces.

## Stage 3 — measure, then decide

- [x] **T2.5** four-surface measurement with the flag ON — **done, and it found a SHORTFALL.** Safe
      everywhere (no golden moved, five diagnostics green, hash `D96213B7BB4021A7` par == single,
      LiveCity 48/48, Pedestrians 272/272) but it does **not** deliver either goal it was chosen for:
      teleports 5 → 4, **veh 95/102 still do not arrive**, F3 buckets **identical** (15 total, **12
      both-moving** unchanged), internal-lane stopped steps 204 vs 206. Root cause then traced — below.
- [ ] **BLOCKED — `MSInternalJunction` is unported.** `docs/NEED-internal-junction-second-stage-admission.md`.
      The trace proves `IsLeader`'s ordering works (`IsLeader(102,95)` false **121/121**) but the call
      site is SUMO's own `isLeader(...) || inTheWay()` disjunction, and `FoeIsInTheWay(102,95)` is
      independently true **121/121** — a symmetric *geometric* fact no ordering can dissolve. The reason
      that geometry exists at all is that a cont turn's **second stage has no admission control**: all
      251 internal junctions parse inert, so veh 95 entered stage 2 without checking any foe. In SUMO
      the bay link must yield to the parent's `response[18]` foe lanes, which include `:2336_3_0`.
      **`isLeader` is necessary but not sufficient — keep it, then port this.**
- [ ] **Owner decision (deferred until the above)** — do `JunctionIsLeaderGate` /
      `ContTurnInsideJunctionGate` go default-ON, and does `IgnoreJunctionBlockerSeconds` stay at `-1`?
      Not yet decidable: with the deadlock unresolved, default-ON still costs teleports.

## Carried out of this workstream (not part of it)

- [ ] `NEED-yield-request-reset-unported.md` — the `MSVehicle.cpp:3720-3731` reset. Needs a faithful
      `mySetRequest`; our `WillPass` omits `leavingCurrentIntersection`, so the obvious wiring would
      blank `ET`/`CET` for every stopped in-junction car. Design §5b.
- [ ] `NEED-linkstatechar-cont-entry-link.md` — `Engine.LinkStateChar` reads a cont link's *second*
      hop, so it returns the static `'m'` instead of the live TL state. Affects the
      `ClassifyTeleportKind` diagnostic label only. Design §2c.

## Parked upstream (log §6 — do not pick these up first)

ORCA/cooperative rescue tier · `checkRewindLinkLanes` · N2 co-located vehicles · N3 net geometry ·
scenario-44 golden · `JunctionPhysicalOccupancyGate` widening
