# Live-city demo integrity — findings (2026-07)

Investigation triggered by an owner review of a `--live-city-demo` HTML replay (density `LIVECITY_PEDS=800`,
200 steps) that showed cars appearing to run reds and collide. This doc records what was found, the
evidence, root causes, and the fix directions. Tracker items in `TASKS-TODO.md` reference the F-numbers here.

## How these were investigated (tooling added)

- **`--live-city-cartrace`** (`src/Sim.Viz/Program.cs`) — per-tick AUTHORITATIVE state for one car:
  `authSpd`, `lane`, `binder`, `tl` (traffic-light), `gap`, `pos1d` (longitudinal), `posLat` (lateral),
  world `pos`, `angle`. (`tl`/`gap`/`pos1d`/`posLat` added during this investigation.)
- **`--live-city-drcheck <steps> [focusId]`** (`src/Sim.Viz/Program.cs`) — runs the SAME reconstruction the
  HTML replay uses (`VizReplayBuilder` → `DrClock` → `KinematicReconstructor`) and OBB-checks every rendered
  frame for vehicle-footprint overlaps, plus a parallel AUTHORITATIVE pass (engine `Sample()` positions) to
  separate DR-introduced overlaps from real engine collisions. **Basis for the no-overlap invariant (F4).**
  - **Heading-convention lesson (recorded so it isn't re-hit):** the OBB orientation must use
    `forward = (−sinθ, cosθ)` for the emitted heading, NOT `(cosθ, sinθ)`. The naive mapping rotates every
    box 90° (validated against veh80, `angle=90`, which runs along world **X**) → pervasive FALSE overlaps
    (authoritative pair-events 3215 → 467 after the fix). `template.js` itself re-derives heading from the
    path tangent for moving cars, which is why the render looks correct despite the emitted-angle convention.
- **Mobile tap-to-identify** in `template.js` (the click-to-identify feature was desktop-only; tap didn't
  reach the pick logic on Android — now wired via touchend tap detection).

## F1 — Event 1: DR reconstruction renders a braking car through a red / junction (RENDER-ONLY)

**Symptom (owner):** at ~t=23 a car appeared to drive through a junction on red.
**Evidence:** `--live-city-cartrace __veh80` — veh80 does not exist at t=23; when it reaches the junction it
authoritatively decelerates (4.81 → 0.31 m/s) and **stops on red** at the stop line (`pos1d≈225.2`,
`tl=r`), only accelerating once `tl=G`. **The engine respects the light — no authoritative red-run.**
**Root cause:** the HTML replay reconstructs position from published packets via `DrClock`.
`DrExtrapolation.Arc` freezes a decelerating car at its stopping point **only when the packet `accel < 0`**.
A car cruising steadily (accel≈0) then braking hard has a last steady packet with accel≈0, so the
reconstruction coasts/extrapolates it forward at cruise speed **past the stop line into the junction** until
a fresh braking packet snaps it back (the `DrClock` back-step metric exists for exactly this). Additionally
`KinematicReconstructor`'s upcoming-lane look-ahead aims a stopped car's nose down the through-junction lane.
**Note:** `--live-city-cartrace` reads `LiveCitySim.Sample()` = raw AUTHORITATIVE positions, so it cannot
show this overshoot; only `--live-city-drcheck` (the reconstruction path) can.
**UPDATE — F1 NOT reproduced at the DR-frame level; likely a PLAYER artifact.** Characterizing veh80 via
`--live-city-drcheck` (the DR-reconstructed frames): the reconstruction **LAGS ~2.5 m behind** the
authoritative car through the stop window (monotonic, no snap-back) — the OPPOSITE of an overshoot. veh80's
braking is a smooth −4.5 m/s² (not the cruise-then-hard-brake profile the extrapolation-overshoot needs),
and `accel` IS published (`ReplicationPublisher.PublishStep` reads `snap.Accel`), so the decel-clamp is not
starved. So the originally-hypothesized DR-extrapolation overshoot is **unconfirmed**. **New leading
hypothesis (owner: "the player may be wrong — dangerous, can't be trusted"):** the artifact is in the
**PLAYER** — `src/Sim.Viz/template.js`, `interpolatedVehicles` / **Catmull-Rom** between DR frames (~line
458+) — which can overshoot a decelerating stop on its own, and which `--live-city-drcheck` (frame-level)
cannot see. There are THREE layers (authoritative → DR reconstruction → player Catmull-Rom); an artifact can
live in any. **Next step: a SOLID repro at the PLAYER level before ANY fix** (instrument/observe the
Catmull-Rom output for a stopping car; find a cruise-then-hard-brake car). If none, downgrade F1 (render
lags; the "red-run" was a misread or a car-vs-light render desync). Do NOT fix an unreproduced artifact.
Candidate fix files IF confirmed: `template.js` (Catmull-Rom clamp), else `DrClock.cs`/`DrExtrapolation.Arc`/
`KinematicReconstructor.cs`/`VizReplayBuilder.cs`.

## F2 — Task A lateral freeze caused car–car overlaps (REGRESSION, REVERTED → **FIXED via targeted redo**)

Fully documented in `docs/LIVE-CITY-REALISM-AB-DESIGN.md` §Task A. Summary of the reverted attempt:
`Engine.FreezeLateralWhenStopped` (freeze ALL lateral commit below `LaneChangeMinSpeed`) also pinned cars
**mid-lane-change**, leaving them **straddling two lanes** → they report `gap=Infinity` to trailing cars
(laterally invisible to car-following) → followers creep into them → overlaps. A/B-confirmed
(`LIVECITY_FREEZELAT` on/off): veh17/26 (0.00 m), veh18/49, veh117/26 — all resolve with the freeze off.
**Reverted**, then the blanket clamp **removed** entirely.

**Task A redo — DONE (targeted, replaces the blanket freeze).** New flag `Engine.SuppressHeldCrowdSwerve`
(default false; demo opt-in **on** by default, `LIVECITY_HELDSWERVE=0` disables). In
`ComputeLateralEvasion`'s crowd-swerve branch, when ego is HELD by the crowd this step
(`BindingConstraint == 13`) AND the agent is laterally STATIC (`LatSpeed ≈ 0`), it recentres and waits
in-lane instead of steering a full lane-width sideways at ~0 forward speed. **Empirically discriminated
(traced on the two crowd-swerve fixtures):** the wobble case is `binder 13` while it steers; a car swerving
PAST a static ped at speed is `binder 3` throughout (never held) → legitimate dodges/passes/lane-changes are
untouched, only the held static-ped swerve is suppressed. The gate only *recentres* (reduces `|PosLat|`), so
it **cannot straddle** — the F2 mechanism is structurally impossible. Verified:
- The held car's `PosLat` stays `0.000` for all held ticks (was 0→2.0→2.7); the at-speed swerve is byte-identical to fix-off (`HeldCrowdSwerveSuppressionTests`, ParityTests).
- **F4a straddle guard green** (no frozen straddle); parity **661/4** byte-identical; bench `D96213B7BB4021A7`; LiveCity **27/27**.
- **No new/worse overlap class** (lane-classified A/B over 200 steps): worst-overall `3.035 m` (F3 junction, unchanged), max pairs/frame `4` (unchanged); junction pairs `30→30`, normal-lane pairs `7→8` with worst `1.800 m` unchanged. The fix adds only two SHALLOW normal-lane overlaps (`0.74 m`, `0.09 m`) — both shallower than 6 pre-existing normal-lane overlaps — because a car now correctly STOPS for a crosswalk ped (where it used to swerve through) and its follower queues tightly. Total overlap *events* rose `116→178` (same pre-existing overlaps exposed across more frames), but no severity metric worsened.

## F3 — Pre-existing junction-overlap engine bug (REAL, authoritative, NOT Task A)

**Symptom (owner):** cars colliding at/near junctions in the high-realism area.
**Evidence:** `--live-city-drcheck` AUTHORITATIVE pass finds real overlaps (worst ~3.0 m) at **default
density**, independent of Task A. Confirmed case: **veh58 drives through stopped veh159** — veh159 sits
stopped on internal junction lane `:d_4_2_4_1` (`authSpd=0`, `binder=3`) while veh58 drives through on the
**crossing** internal lane `:d_4_2_7_0` (`binder=5`), their footprints overlapping up to ~3 m. Two cars on
crossing internal junction lanes occupy the same space.
**Assessment:** a junction conflict-point / into-occupied admission bug — the same family as
`docs/LANE-CHANGE-OVERLAP-*`, `docs/ISSUE2-JUNCTION-*`, `docs/LIVE-CITY-15-INTO-OCCUPIED-DESIGN.md`. Present
on this branch at default density; **not caused by Task A or the density chosen for the replay.**
**LOCALIZED (resolved):** F3 is **pre-existing on `main`** — running the authoritative overlap check against
`main`'s engine yields the **identical worst overlap** (`3.035 m`, pair `veh134/veh38`, step 197) as this
branch. It is a **long-standing core junction bug, NOT introduced by this session.** The realism-fixes
branch roughly **doubled the count** (main 61 events / 3 pairs-per-frame → this branch 116 / 4) but did not
create the bug or deepen the worst case. **Decision: route F3 to core junction work** (into-occupied /
conflict-point family) — it is out of scope for the realism-A/B session, likely a large engine dig, and
blocks a clean zero-overlap invariant. Repro for whoever takes it: default-density demo, `veh134/veh38`
(default) or `veh58`-through-`veh159` (density 800), cars on crossing internal junction lanes overlapping
~3 m. The realism-branch count amplification is a secondary note (same worst pair → more instances, not
deeper), likely tied to the density/LOD changes; revisit only if it persists after the core fix.

## F4 — No car–car-overlap invariant exists (TEST-COVERAGE GAP)

Nothing asserts the demo keeps cars from overlapping — which is why F2 (a real regression) passed parity
**660/4** + LiveCity **25/25** + bench, and why F3 went unnoticed. The goldens are demo-blind by design;
the Task-A unit test froze a *centered* car so it never exercised the mid-lane-change straddle; the LiveCity
suite guards gridlock/throughput/crossings, not overlaps.
**Required guard:** a demo invariant over N steps at density asserting (a) no two vehicle footprints overlap
beyond a small threshold, on BOTH the authoritative engine positions AND the DR-reconstructed frames (the
owner's requirement — DR-caused overlaps are as bad visually as real ones); ideally also (b) no car crosses
a red stop line, and (c) minGap respected. Build it **fail-first** (it will fail on F3 today), then fixes
turn it green. `--live-city-drcheck` is the reusable engine for the check. Architecture note: the
authoritative check can live in `tests/Sim.LiveCity.Tests` (references `Sim.LiveCity`); the DR-reconstructed
check needs `Sim.Viz` (`VizReplayBuilder`) — no `Sim.Viz` test project exists in `Traffic.sln` today, so
either add one or expose the reconstruction to a project that is in the solution.

## Fixing order (finalized after the F3 localization + F4-masking analysis)

Two analyses reshaped the naive order: (i) F3 is **pre-existing core** (localized above) → route it, don't
block on it; (ii) with F3 present, an aggregate no-overlap invariant **cannot cleanly catch F2** (F3
dominates: freeze-on adds only 467→544 events at density 800, worst penetration F3-pinned at 3.03m) → the
F2 guard must be **targeted** (a straddle detector), not aggregate.

1. **F4a — targeted F2 straddle guard** *(this session)* — **DONE**. Assert no stopped/slow car has `|PosLat|`
   past its lane edge (F2's exact mechanism: a frozen mid-lane-change car straddling). F3-independent, green now,
   actually trips on the freeze regression. This is the guard that protects the Task A redo.
2. **Task A redo (F2)** *(this session)* — **DONE**. Targeted crowd-swerve suppression (`SuppressHeldCrowdSwerve`,
   not a blanket lateral freeze), protected by F4a. Empirically discriminated by `binder 13` (held) vs `binder 3`
   (passing at speed). See §F2 above for the full verification (parity 661/4, bench, LiveCity 27/27, no new overlap class).
3. **F1 — DR overshoot fix** *(this session)*. Render integrity; owner-prioritized; tractable viz/DR work.
   **Repro-first**: F1 is UNCONFIRMED at the frame level (engine respects reds; DR reconstruction LAGS, no
   overshoot) → the PLAYER (Catmull-Rom, `template.js`) is the suspect. No fix without a solid player-level repro.
4. **F3 — route to core junction work** *(NOT this session)*. Pre-existing core bug; documented above.
5. **F4b — tighten the general no-overlap invariant to ZERO** — deferred until F3 is fixed (only then is
   overlap-free the true baseline). The committed authoritative test stays as an F3 characterization +
   gross-regression tripwire until then.
