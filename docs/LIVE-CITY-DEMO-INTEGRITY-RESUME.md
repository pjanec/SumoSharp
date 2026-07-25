# RESUME — live-city demo integrity + Task A (post-compaction handoff)

Self-contained state for resuming this work after a context reset. Read this + the cross-refs, then continue.
**Guiding principle (owner, firm): a SOLID REPRO before fixing anything. And the PLAYER may be untrustworthy —
if it misrenders, visual observations through it are suspect; prefer authoritative/frame-level ground truth.**

## Branch, gates, identity

- Branch: **`claude/livecity-realism-fixes-vr4k4b`**, HEAD `24d17fd` (based on the old `claude/livecity-realism-fixes`
  tip, reset early in the session). Docs are also synced to `main`.
- **Gates (all currently green, verified first-hand):**
  - `dotnet test tests/Sim.ParityTests -c Release` = **661/4** byte-identical (657 base + 4 from `HeldCrowdSwerveSuppressionTests`; was 660 with the old 3-fact `StoppedCarLateralFreezeTests`, now removed).
  - `dotnet run --project src/Sim.Bench -c Release` → hash **`D96213B7BB4021A7`** (par==single).
  - `dotnet test tests/Sim.LiveCity.Tests -c Release` = **27/27** — run **WITHOUT** `--no-build` (this project is NOT in `Traffic.sln`).
- Commit identity: `git config user.email noreply@anthropic.com && git config user.name Claude`. The 21 inherited
  realism commits show as "unverified" — they are NOT mine, do NOT rewrite them (shared, already pushed).
- **Doc → main sync is MERGE-SAFE only:** `git checkout -B tmp origin/main; git checkout <branch> -- <doc>; commit; push tmp:main`.
  A blind overwrite once clobbered the arbitrary-net session's TODO edits (reconciled). Always re-base on fresh `origin/main`.

## Session map (coordination)

Three sessions; only THIS one is active. Full boundary: `docs/COORDINATION-livecity-realism-sessions.md`.
- **realism-A/B (this branch)** — owns Task A (stopped-car crosswalk wobble) + the demo-integrity findings F1/F2/F4.
- **ped–vehicle avoidance** (`claude/livecity-ped-vehicle-avoidance`, NOT started) — owns Task B + C5 + wandering-ORCA.
  Brief: `docs/LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`.
- **arbitrary-net** (`claude/discussion-eqp53m`) — MERGED to main (PR #11). Done.
- Live queue: `docs/TASKS-TODO.md` (on branch and main). Findings: `docs/LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md` (F1–F4).
  Task A brief: `docs/LIVE-CITY-REALISM-AB-DESIGN.md` §Task A.

## Findings state (see LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md for evidence)

- **F1 — RESOLVED / DOWNGRADED (not a render/player bug). DONE.** Repro-first, authoritative: the engine
  respects the red (veh80 stops on red at t=28.5, enters on GREEN t=29.0); the player **cannot** overshoot
  position (`template.js` `clampBox` clamps every rendered pose to the AABB of its two real DR endpoints); and
  all 51 demo TLs are `static`/`offset=0`, so the player's `tlLinkState` mirrors the engine's `GetLinkState`
  exactly (no light desync). The owner's "veh80 drove through veh120 ignoring red" = a **misread** (veh80 was on
  green) **+ a REAL car–car overlap** (veh80's green crossing traverses stopped veh120's pose `(2862.90,2851.60)`;
  authoritative OBB `veh80/veh120` and `veh80/veh134` ~1.8 m) → **F3-family**, folded into F3 (keep-clear /
  garage-stub-into-junction sub-case). The player's *math* is sound; the owner's distrust of it as a *reporting
  instrument* was right. No render-layer fix. See FINDINGS §F1.
- **F2 — Task A blanket lateral freeze caused car–car overlaps. REVERTED → FIXED (targeted redo). DONE.** The old
  `Engine.FreezeLateralWhenStopped` (freeze all lateral commit below `LaneChangeMinSpeed`) pinned cars MID-LANE-CHANGE →
  straddle → `gap=Infinity` → overlaps. **Reverted and the blanket clamp removed.** Replaced by
  `Engine.SuppressHeldCrowdSwerve` (default false; demo opt-in **on** by default, `LIVECITY_HELDSWERVE=0` disables): in
  `ComputeLateralEvasion`'s crowd-swerve branch, when ego is HELD (`BindingConstraint == 13`) AND the ped is laterally
  STATIC (`LatSpeed ≈ 0`), recentre + wait in-lane. Only recentres → cannot straddle. **Empirically discriminated**
  (traced the two crowd-swerve fixtures): held = `binder 13`, at-speed pass = `binder 3`; the fix touches only the former.
  Verified: parity 661/4, bench, LiveCity 27/27, F4a green, no new/worse overlap class (worst 3.035 m F3 + pairs/frame 4
  unchanged; adds only 0.74/0.09 m normal-lane overlaps, shallower than 6 pre-existing). See FINDINGS §F2.
- **F3 — pre-existing junction-overlap engine bug. REAL, on `main` too → ROUTED to core junction work (NOT this session).**
  Cars on crossing internal junction lanes overlap ~3 m (identical worst pair `veh134/veh38` 3.035 m on main and this
  branch; this branch ~2x'd the count). Into-occupied / conflict-point family. Blocks a clean zero-overlap invariant.
- **F4a — targeted straddle guard. DONE.** `DemoAuthoritative_NoStoppedCarStraddlesPastItsLane` in
  `tests/Sim.LiveCity.Tests/DemoCarOverlapInvariantTests.cs`. F2's true signature is PERSISTENCE (raw peak posLat can't
  separate — crowd-swerve reaches ~5 m both ways): PosLat **frozen unchanged past the lane edge (>1.2 m) for ≥10
  consecutive stopped ticks**. Verified: green freeze-off (0 ticks), FAILS freeze-on (58 ticks). This guards the Task A redo.
- **F4b — general zero-overlap invariant. DEFERRED until F3 fixed** (only then is overlap-free the true baseline). The
  committed `DemoCarOverlapInvariantTests` authoritative test holds the line as an F3 characterization + gross tripwire.

## Diagnostics I added (this branch, `src/Sim.Viz/Program.cs` + `template.js`)

- `--live-city-cartrace <steps> <id> [lo hi]` — AUTHORITATIVE per-tick (via `LiveCitySim.Sample()`/`WitnessAuthoritative()`):
  `authSpd, tl, gap, pos1d, posLat, pos=(x,y), angle, binder`. NOTE: `Sample()` returns raw AUTHORITATIVE poses, not DR.
- `--live-city-drcheck <steps> [focusId]` — runs `VizReplayBuilder` (DR reconstruction) + OBB overlap check per frame,
  plus a parallel AUTHORITATIVE pass. focus dumps recon pose per frame. **Checks DR FRAMES, NOT the player's Catmull-Rom.**
- **OBB heading convention (critical): forward = `(-sinθ, cosθ)`, NOT `(cosθ, sinθ)`** (which rotates boxes 90° → pervasive
  false overlaps). Validated: veh80 `angle=90` runs along world X.
- `template.js`: mobile **tap-to-identify** added (was desktop-click only; touch handlers preventDefault'd the synth click).

## Key architecture facts (so I don't re-derive)

- Demo has **no `lateral-resolution`** → `Engine._sublane == false` → the active lateral driver is
  **`Engine.ComputeLateralEvasion`** (~9089), NOT `ComputeSublaneLateral` (dead code in the demo).
- Crowd coupling: `Engine.CrowdSource` (`ICrowdFootprintSource`, ~764), `CrowdLongitudinalConstraint` (~8582, binder 13),
  `CrossRegimeCoupling` (both directions). **All gated on `CrowdSource != null` → parity-inert** (no golden sets it).
- `LiveCitySim.Sample()` = AUTHORITATIVE; `VizReplayBuilder`/`DrClock`/`KinematicReconstructor` = DR reconstruction;
  `template.js` player = Catmull-Rom between DR frames. THREE layers — an artifact can live in any of them.
- `accel` IS published (`ReplicationPublisher.PublishStep` reads `snap.Accel`). `DrExtrapolation.Arc` decel-clamps only when
  packet `accel < 0`.

## Task A redo — ✅ DONE (shipped). Design of record below.

**Status: implemented and verified** (see §F2 above for numbers). What shipped: `Engine.SuppressHeldCrowdSwerve`
gate in `ComputeLateralEvasion` (the crowd-swerve branch, right after the non-crowd stop gate ~9266); removed the
reverted blunt clamp in `ExecuteMoves`; renamed `FreezeLateralWhenStopped → SuppressHeldCrowdSwerve`; demo opt-in
on by default at `LiveCitySim.cs`; new `tests/Sim.ParityTests/HeldCrowdSwerveSuppressionTests.cs` (4 facts, replaced
`StoppedCarLateralFreezeTests`). The empirical discriminator (binder 13 held vs binder 3 passing) was confirmed by
tracing both fixtures before writing the fix. Original design (kept for the record):

Goal: kill the crosswalk wobble (posLat oscillating while a car is held stopped by a ped) WITHOUT the F2 collateral
(the blanket freeze pinned lane-changes/recentering). All lines `src/Sim.Core/Engine.cs`.

- **Mechanism:** the wobble is `ComputeLateralEvasion`'s crowd-swerve TARGET flip-flopping — the laterally-static-ped
  tie-break at **9291–9293**, returned at **9309–9310** — while ego is held by that ped.
- **Fix:** in the crowd-swerve target branch (**9268–9310**), when **ego is held by the crowd this step**
  (`v.BindingConstraint == 13`, already set at **5183** before the lateral call) **AND the ped is laterally static**
  (`th.LatSpeed ≈ 0` — the flip-flop branch), **suppress the swerve** (hold / recentre) instead of returning the
  oscillating `chosen` target. Reading `v.BindingConstraint` needs no signature change (prePass short-circuits at 5324).
- **REMOVE the reverted blunt clamp** at **9599–9603** (the `FreezeLateralWhenStopped` commit-choke freeze).
- **DO NOT TOUCH:** recenter returns **9153 / 9247 / 9265**; lane-change start/hold/complete **11258, 11314–17, 10938–74**.
- **Keep moving-ped dodging:** a ped with real `th.LatSpeed` (9291–9292) has a vacating side → still dodge it (avoid the
  velocity-0 over-brake lesson). Only the static-ped flip-flop is suppressed.
- **Parity:** inert by construction (crowd branch gated on `CrowdSource`). Can reuse the `FreezeLateralWhenStopped` flag or
  add a sibling; LiveCitySim opt-in at `LiveCitySim.cs:262`.
- **Verify (in order):** (1) a SOLID repro that the wobble exists and is killed — the wobble = a held car's `posLat`
  oscillating; original substrate `LIVECITY_PEDS=300 --live-city-cartrace 400 __veh218 370 380` (pre-fix showed posLat
  swing). Confirm the redo makes posLat stop oscillating while `authSpd≈0` AND the car still resumes/recenters.
  (2) **F4a stays green** (no straddle). (3) `--live-city-drcheck` overlaps not worse. (4) parity 660/4 + bench + LiveCity green.

## Fixing order (finalized)

1. ~~**Task A redo**~~ — ✅ **DONE** (guarded by F4a; see §F2).
2. ~~**F1**~~ — ✅ **RESOLVED / DOWNGRADED** (not a render bug; misread + F3 overlap, folded into F3).
3. **F3** — routed to core junction work (not this session); now also owns the F1 keep-clear sub-case.
4. **F4b** — zero-overlap invariant, deferred until F3 fixed.

**This session (realism-A/B) is now complete:** Task A (F2) shipped; F1 resolved; F3 routed out; F4b deferred.

## Open threads / notes

- No background agents running at handoff. Working tree clean at `24d17fd`.
- Scratchpad logs from investigations are ephemeral (VM); the committed docs are the record.
- When resuming: re-read `TASKS-TODO.md` "Demo integrity" section + this doc, confirm gates still green, then start the Task A redo.
