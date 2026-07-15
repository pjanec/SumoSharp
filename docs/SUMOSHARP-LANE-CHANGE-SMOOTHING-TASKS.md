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

- [ ] **T1** — `DrClock.Resolve` classifies downstream vs lateral straddle; lateral returns two-state result
- [ ] **T2** — `PumpAndBuildVehicleDraws` Cartesian pose blend + chord heading + sanity guard
- [ ] **T3** — regression (44), sublane (61), and guard verification pass the numeric bars in design §7
- [ ] **T4** *(optional)* — heading-tilt polish; interactive "looks NICE" sign-off

**Definition of done:** T1–T3 checked with the numeric success conditions confirmed first-hand from
DRTRACE (not a summary), and an interactive run reviewed by the user.
