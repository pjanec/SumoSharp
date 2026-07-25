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

## F1 — Event 1: "veh80 runs a red / drives through veh120" — RESOLVED: misread + a real F3 overlap; NOT a render bug

**Symptom (owner):** "time≈23 veh120 standing on red, veh80 driving through him and through the crossroad
ignoring red." Originally filed as a DR/player render artifact (a braking car rendered past its stop).

**Resolved (repro-first, authoritative): F1 is NOT a render/DR/player bug — the player is exonerated.**
Four independent lines of evidence, from `--live-city-cartrace`/`--live-city-drcheck` and the source:

1. **The engine respects the light.** `--live-city-cartrace __veh80` (Dt=0.5, so t≈step×0.5): veh80 approaches
   on **red**, brakes 13.89→0.31 m/s and **stops** at **t=28.5** (`pos=(2862.90,2851.60)`, `tl=r`,
   `distCross≈4.1`), then the light goes **`tl=G` at t=29.0** and it proceeds. **No authoritative red-run —
   veh80 stops on red and enters on GREEN.**
2. **The player cannot overshoot position.** `template.js interpolatedVehicles` (~:497–500) **clamps** each
   rendered pose to the AABB of the segment's two real DR endpoints (`clampBox`), on top of centripetal
   Catmull-Rom (α=0.5, :429). So a rendered car cannot be drawn past its next DR frame; and the DR frames
   themselves LAG behind (never ahead). A position overshoot past the stop is structurally impossible.
3. **No light desync.** All **51** demo TLs are `type="static"` `offset="0"` (`scenarios/_ped/demo_city/box/
   net.xml`) — no actuation. The player derives light colour from the static program (`tlLinkState`,
   template.js:166) which "mirrors `Sim.Core/TrafficLightState.cs GetLinkState` exactly" → the rendered light
   is in lock-step with the engine. No stale/adaptive-light mismatch.
4. **The real artifact is a car–car OVERLAP (F3-family), not a red-run.** `--live-city-cartrace __veh120`:
   veh120 sits **motionless** at **`(2862.90,2851.60)`** (angle 270, lane `e_d_garage_stub_d_5_5_1`, `tl=r`)
   the whole window — "standing on red" ✓. veh80 at **t=28.5 is at the IDENTICAL pose** `(2862.90,2851.60)`
   angle 270, then accelerates through junction lane `:d_5_5_6_1` traversing veh120's spot. The authoritative
   OBB check flags **`__veh120 / __veh80` at 1.80 m** penetration (and `__veh134 / __veh80` at 1.80 m — the
   same veh134/veh80 pair that appears in the pre-existing normal-lane overlap set). So veh80's **green**
   junction-crossing path runs straight through **stopped** veh120: "driving through him" is a **real overlap**;
   "ignoring red" is a **misread** (veh80 was on green; it looked like a red-run because it passed through the
   red-stopped veh120 that occupies its crossing path — a garage-stub-into-junction / keep-clear conflict).

**Disposition: DOWNGRADE F1 and FOLD its overlap into F3.** There is nothing to fix in the render/DR/player
layer. The genuine defect (veh80's crossing overlaps stopped veh120/veh134 by ~1.8 m) is a **pre-existing
F3-family junction/keep-clear overlap** — the garage-stub-into-junction sub-case — routed to **core junction
work** with the rest of F3 (§F3). The owner's instinct to distrust the player as a *reporting instrument* was
right (it conflated a real overlap + a light misread into "running a red"), while the player's *math* is sound.

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
**Sub-case (folded in from F1 — the owner's "veh80 drove through veh120"):** a car stopped on a
**garage-stub** approach (veh120 on `e_d_garage_stub_d_5_5_1`, motionless on red) sits in the path of a car
legally crossing the junction on green (veh80 via `:d_5_5_6_1`) — identical pose `(2862.90,2851.60)` at
t=28.5, authoritative OBB overlap `veh80/veh120` and `veh80/veh134` ~1.8 m. A **keep-clear / stub-into-junction**
variant of the same conflict-point family; the crossing car is NOT admitted-clear of the occupied point.
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
3. **F1 — RESOLVED / DOWNGRADED** *(this session)*. Repro-first showed it is NOT a render/DR/player bug:
   engine respects the red (veh80 stops on red, enters on green), the player cannot overshoot position
   (`clampBox`), and the demo TLs are static/offset-0 so the rendered light is in lock-step with the engine.
   The genuine defect is a real car–car overlap (veh80's green crossing runs through stopped veh120/veh134,
   ~1.8 m) → **F3-family**, folded into F3. No render-layer fix. See §F1.
4. **F3 — route to core junction work** *(NOT this session)*. Pre-existing core bug; documented above. Now
   also owns the F1 "garage-stub-into-junction / keep-clear" overlap sub-case (veh80/veh120, veh80/veh134).
5. **F4b — tighten the general no-overlap invariant to ZERO** — deferred until F3 is fixed (only then is
   overlap-free the true baseline). The committed authoritative test stays as an F3 characterization +
   gross-regression tripwire until then.
