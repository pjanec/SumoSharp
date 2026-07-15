# Lane-change smoothing — tasks & tracker

Task breakdown + checkable tracker for `SUMOSHARP-LANE-CHANGE-SMOOTHING-DESIGN.md` (the design is the
source of truth for *how*; this file is *what*, in what order, and *done-when*). Viewer-only; all
verification is via the `--trace-veh` harness on loopback (no `dotnet test` / SUMO involvement).

## Conventions
- Files: `src/Sim.Viewer.Core/DrClock.cs`, `src/Sim.Viewer/Program.cs` only.
- Build: `dotnet build src/Sim.Viewer/Sim.Viewer.csproj -c Release`.
- Verify: `dotnet run -c Release --no-build --project src/Sim.Viewer -- --mode loopback --trace-veh <id> --screenshot <png> --frames <n> scenarios/<dir>`, then the lateral/longitudinal analysis of the DRTRACE (design §7).

---

## Stage 1 — lateral-straddle classification & blend

### T1 — `DrClock.Resolve`: split the straddle into downstream vs lateral (design §4.1)
- **Files:** `DrClock.cs`.
- **Do:** in the `a.LaneHandle != b.LaneHandle` branch, keep the `ArcInWindow` attempt; when it returns
  a value → downstream (unchanged). When it returns **null**, no longer extrapolate — return a
  two-state result (`State=a`, `SecondState=b`, `Blend=f`, `IsLateralStraddle=true`). Restore the
  two-state fields on `Resolved` for this case only.
- **Success conditions:**
  1. Same-lane and downstream-straddle paths are byte-for-byte behaviourally unchanged (a
     `44-multilane-junction-turn` `vN` trace is identical to pre-change: max lateral ≤ 0.10 m).
  2. A lateral straddle sets `IsLateralStraddle` and carries both records (assert via a `--trace-veh`
     run on `12-overtake` `follow`: the change instant now reports the lateral-straddle path, not
     `extrap=True`).

### T2 — `PumpAndBuildVehicleDraws`: Cartesian pose blend + chord heading + guard (design §4.2)
- **Files:** `Program.cs`.
- **Do:** when `resolved.IsLateralStraddle`, resolve `poseA`/`poseB` on their own lanes (each with its
  `PosLat`), then `p = lerp(poseA, poseB, f)`; heading = chord `naviFromVector(poseB−poseA)` with the
  short-chord fallback to `LerpAngleDeg`; snap to `poseB` if `|poseB−poseA|` exceeds the §4.2 sanity
  bound. Non-straddle path unchanged.
- **Success conditions:**
  1. `12-overtake` `follow`: DR lateral coordinate moves **monotonically** old-lane→new-lane across the
     packet interval with **no single-frame lateral step > 0.5 m** (baseline: one ~3.2 m step / stuck).
  2. `07-keep-right-change`: same smooth-slide property on its change(s).
  3. Heading tilts during the change and returns to the straight value after.

---

## Stage 2 — regression & edge coverage

### T3 — regression + sublane + sanity-guard verification (design §7)
- **Files:** none (verification only; fixes fold back into T1/T2 if a check fails).
- **Success conditions:**
  1. **No junction regression:** `44-multilane-junction-turn` `vN` max lateral per-frame ≤ 0.10 m.
  2. **Sublane unaffected:** `61-sublane-sidebyside` `v0` shows its steady `posLat=1.5` offset with no
     new per-frame lateral jitter (> 0.2 m) introduced.
  3. **Guard works:** a despawn/respawn or teleport (handle reuse) snaps rather than drawing a long
     diagonal (construct or find a case; confirm no multi-metre diagonal in the DRTRACE).

---

## Stage 3 (optional polish — only if Stage 1–2 look good on screen)

### T4 — heading-tilt tuning / brief low-pass on the lane-change heading
- **Files:** `Program.cs`.
- **Do:** if the chord-derived tilt reads too abrupt or too subtle, apply a short heading low-pass
  during the lateral straddle (analogous to the local viewer's τ≈0.25 s heading filter) and/or clamp
  the tilt magnitude. Interactive/visual judgement.
- **Success condition:** user confirms the lane change "looks NICE" on the interactive viewer (the same
  bar the junction turn cleared).

---

## Tracker

- [x] **T1** — `DrClock.Resolve` classifies downstream vs lateral straddle; lateral returns two-state result.
      Also discovered & fixed the *real* root cause of the "stuck on old lane" behaviour: the wire's
      `Upcoming[0]` (lane-sequence pool) lags the record's own `LaneHandle` after a same-edge tactical
      change, so `PoseResolver` sampled the OLD lane's geometry — `NormalizeUpcoming` re-anchors index 0
      to the record's `LaneHandle` (viewer-side read fix, no Sim.Core touch).
- [x] **T2** — `PumpAndBuildVehicleDraws` Cartesian pose blend + chord heading + sanity guard (guard gated
      on the bracket's REAL span `resolved.PacketSpan`, not the smoothed EMA, so a slow-sampled slide is
      not wrongly snapped).
- [~] **T3** — regression & coverage (verified first-hand from DRTRACE):
    - [x] **44 junction regression** — max lateral/frame **0.081 m** ≤ 0.10 m ✅ (NormalizeUpcoming safe).
    - [x] **61 sublane** — max lateral/frame **0.000 m**, steady offset preserved ✅.
    - [~] **12-overtake** — now traverses BOTH lanes with an 85° chord tilt (was stuck at one lane);
      **residual 1.24 m step on ENTRY** to the slide (the prior frame was extrapolating on the old lane
      and overshot `pos` ~16 m; the slide itself is smooth). Slide property met; entry-step > 0.5 m bar.
    - [ ] **07-keep-right** — startup/acceleration-dominated (veh0 departs from standstill on a single
      1000 m edge; bracketing packets ~65 m apart during accel). Not a clean steady-state lane-change
      signal; the guard snaps the large longitudinal bracket. Revisit with a mid-run change scenario.
- [ ] **T4** *(polish — NEXT, user-prioritised 2026-07-15 live review)* — the abrupt part is the
      **initial ORIENTATION (heading) jump when the lane change STARTS**, not the position: the frame the
      lateral straddle engages, `pdeg` snaps from the straight-lane heading to the chord tilt (~85°) in one
      step. Fix the HEADING discontinuity first (position slide already reads fine). Candidates: low-pass the
      lane-change heading toward the chord tilt (like the local viewer's τ≈0.25 s render-heading filter in
      `BuildLocalVehicleDraws`), and/or ease the tilt in over the first frames of the straddle rather than
      snapping to it, and/or ensure interpolation-entry (delay) so the prior frame isn't an extrapolated
      straight-heading pose. Then interactive sign-off. (Position entry-step ~1.24 m is secondary — same
      extrapolation-reconciliation family as the junction pass's residual, not the reported annoyance.)

**Status:** core mechanism (T1+T2) done and visually confirmed smooth by the user on the interactive
viewer; no regressions (44/61). Remaining work is the entry-transition polish (T4) and a cleaner
mid-run lane-change test to replace 07.
