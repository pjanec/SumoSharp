# F3 — tracker: `MSVehicle::isLeader` port

Design: `docs/F3-ISLEADER-PORT-DESIGN.md` · Tasks (with success conditions):
`docs/F3-ISLEADER-PORT-TASKS.md` · Running record: `docs/F3-SESSION-LOG.md`

A box is ticked **only** when the reviewer has confirmed that task's success conditions first-hand —
read the diff, checked the tests assert the real condition, and re-ran the gate. An implementor's
report of "done" is not sufficient (CLAUDE.md, the orchestration loop).

## Stage 1 — data plumbing (parity-inert by construction)

- [ ] **T2.1** `LinkIndexByInternalLane` (both cont stages) + `EntryConnectionByLink`
      (`getCorrespondingEntryLink`) — 5 success conditions, incl. the all-nets sweep
- [ ] **T2.2** three `long` timestamps on `VehicleRuntime`, assigned at the lane-advance seam,
      **written but never read** — 3 success conditions, incl. `CET == MAX` in the cont bay

## Stage 2 — the decision (flag-gated, default OFF)

- [ ] **T2.3** `IsLeader` + `ResponseFor` + a separately callable tie-break — 5 success conditions,
      incl. the ordinal-comparison and no-indirect-link guards
- [ ] **T2.4** wire into arm 5 behind `JunctionIsLeaderGate` (default OFF); flag-off path unchanged
      character-for-character — 3 success conditions

## Stage 3 — measure, then decide

- [ ] **T2.5** full four-surface measurement with the flag ON; F3 buckets re-measured and reported
      either way; log updated
- [ ] **Owner decision** — do `JunctionIsLeaderGate` / `ContTurnInsideJunctionGate` go default-ON,
      and does `IgnoreJunctionBlockerSeconds` stay at SUMO's `-1`?

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
