# Lane-change smoothing in the DR viewers — design

**Status:** design (design-first; no code until agreed). **Scope:** the dead-reckoning viewers
(`--mode loopback`, `--mode remote`) only. **Builds on:** `SUMOSHARP-VIEWER-DR-SMOOTHING.md`
(the motion pipeline of record) — read §5 (DR pipeline) and §6.1 (arc-window straddle) first; this
doc reuses that vocabulary and does **not** restate it. **Parity:** viewer-only, zero engine/golden
impact (see §6).

---

## 1. Problem (established from data, not assumed)

A vehicle changing lanes "snaps" sideways in the DR viewers instead of sliding across. Investigation
with the `--trace-veh` harness (AUTHTRACE = authoritative snapshot, DRTRACE = DR-reconstructed pose):

- **The engine emits a lane change as a discrete one-step lateral flip.** Scenarios `37-intraedge-
  lanechange`, `43-continuous-lanechange`, and `12-overtake` all show the authoritative `y` jumping a
  full lane-width in a **single sim step** with `posLat = 0` before and after (e.g. `12-overtake`
  `follow`: `y = −4.80 (e0_0)` at t=11 → `y = −1.60 (e0_1)` at t=12). **There are no intermediate
  lateral positions in the stream.** SUMO's own FCD for `43` *does* ramp (−3.73, −2.67) because that
  scenario drives SUMO's continuous-lateral model, but our engine's authoritative snapshot exposes the
  change discretely. Either way, **the DR viewer receives a lane-flip, not a slide.**
- **Sublane vehicles carry a continuous `posLat`** (scenario `61-sublane-sidebyside`: `posLat = 1.50`).
  So *within-lane* lateral drift is already reconstructed correctly by the existing same-lane arc lerp
  (which lerps `PosLat`). The unsolved case is a change that **crosses a lane boundary** (the lane
  handle flips), whether discrete or sublane.
- **The DR path does not reconstruct the slide today.** On `12-overtake`, the authoritative `follow`
  moves to the left lane at t=12, but the DR-rendered `y` stays pinned at −4.80 for the whole run.
  Cause (from code): the two packets bracketing the render instant sit on **sibling** lanes; `b`'s lane
  is *not* in `a`'s downstream `Upcoming`, so `ArcInWindow` returns null and `Resolve` falls back to
  **extrapolating along `a`'s old lane** — the vehicle never leaves it (and would snap when `b` finally
  becomes the current packet).

**Contrast — the local viewer already looks right** because it lerps raw authoritative `(x,y)` between
snapshot pairs (`SUMOSHARP-VIEWER-DR-SMOOTHING.md` §4): a one-step `y` flip becomes a smooth lateral
slide for free. The DR viewer has no `(x,y)` — it must **synthesize** the slide from lane geometry.

## 2. Goal

The DR viewers render a lane change as a smooth lateral slide (position **and** a slight heading tilt
toward the target lane), matching the local viewer's quality — **without** regressing the junction-turn
fix (downstream straddles must still follow the curved lane geometry, no corner-cut).

## 3. Root of the two straddle types

`DrClock.Resolve`'s interpolate branch brackets the render instant with packets `a` (older) and `b`
(newer) on **different** lanes. There are two topologically distinct reasons they differ:

| Straddle type | Geometry | Correct reconstruction |
|---|---|---|
| **Downstream** (junction / turn) | `b`'s lane is *ahead* of `a` on the route — it is in `a`'s `Upcoming` | Interpolate arc-length **within `a`'s forward window**; `PoseResolver` walks the real curved lanes (existing `ArcInWindow`, §6.1 of the pipeline doc). |
| **Lateral** (lane change) | `b`'s lane is a *sibling* beside `a` — **not** in `a`'s `Upcoming` | Slide between the two parallel lane positions: **Cartesian-lerp** the two reconstructed poses. |

Today only the first is handled; the second falls through to extrapolation (§1). The fix is to give the
lateral straddle its own branch.

## 4. Mechanism

### 4.1 Classify in `DrClock.Resolve`
When `a.LaneHandle != b.LaneHandle` (the existing straddle `else`):

1. Try `ArcInWindow(a, b.lane)` (unchanged). If it returns a value → **downstream** → existing
   arc-window interpolation. *(Junction turns keep working exactly as now.)*
2. Otherwise it is a **lateral straddle**. Return *both* bracketing states + the interpolation fraction
   `f`, so the caller can resolve each pose on its own lane and blend. Concretely, reintroduce the
   two-state result shape on `Resolved` (`State` = `a`, `SecondState` = `b`, `Blend = f`,
   `IsLateralStraddle = true`) — the same struct extension prototyped during the junction pass, now
   used **only** for the lateral case (not for every straddle, which was the corner-cut mistake).

`DrClock` needs no new geometry knowledge for this branch — it just forwards both records. It keeps the
`ILaneShapeSource` it already has for `ArcInWindow`.

### 4.2 Blend in `PumpAndBuildVehicleDraws`
When `resolved.IsLateralStraddle`:

1. Resolve `poseA = PoseResolver.Resolve(a-state on a's lane + a.PosLat, a.Upcoming)` and
   `poseB = PoseResolver.Resolve(b-state on b's lane + b.PosLat, b.Upcoming)` — each is the vehicle's
   true world position on its respective lane (includes any sublane `PosLat`).
2. **Position:** `p = lerp(poseA.xy, poseB.xy, f)` (and `z` if 3D). Because sibling lanes are parallel,
   the straight chord between the two positions **is** the lane-change diagonal — no corner-cut, unlike
   a turn.
3. **Heading:** prefer the **chord** direction `naviFromVector(poseB − poseA)`, which points forward
   with a slight lateral tilt (mirrors SUMO's ~85° during a change) — the vehicle visibly *leans* into
   the change rather than crabbing sideways. Fall back to `LerpAngleDeg(poseA.deg, poseB.deg, f)` when
   the chord is too short to be meaningful (`|poseB − poseA| < ~0.5 m`, e.g. a near-stationary change).
4. **Sanity guard:** if `|poseB − poseA|` is implausibly large for one packet interval (handle reuse,
   despawn/respawn, or a genuine teleport) — say `> max(3·laneWidth, speed·gap·1.5)` — **snap** to
   `poseB` instead of drawing a long diagonal across the map. Reuses the "don't smear a correction"
   philosophy of the §5.5 low-pass 7 m snap.

### 4.3 What stays the same
- Downstream/junction straddle → `ArcInWindow` (unchanged; regression-guarded by scenario 44).
- Same-lane interpolation, including sublane `PosLat` drift → unchanged arc + `PosLat` lerp.
- `ChordHeading` realism, auto-delay (seed+slew), extrapolation low-pass → unchanged.
- The only behavioural change is: the arc-window-**null** straddle branch stops extrapolating along the
  old lane and instead blends the two poses (§4.2).

### 4.4 Interaction with the extrapolation low-pass (§5.5 of the pipeline doc)
A lateral-straddle blend is an **interpolation** (`Extrapolated = false`), so the low-pass smoother does
not touch it (correct — the blend is already continuous). When packets are so sparse that the render
instant runs *past* `b` (extrapolation), we are no longer in the straddle branch and behave as today.

## 5. Alternatives considered (and why not)

- **Cartesian-lerp on *every* straddle** (the junction-pass first attempt): corner-cuts real turns.
  Rejected — that is exactly why §4.1 keeps `ArcInWindow` for downstream straddles.
- **Synthesize `PosLat` topologically** (detect sibling lane, ramp a virtual `PosLat` from ±laneWidth
  to 0 across the change): needs a lane→edge / adjacency map the DR stream does not currently carry, and
  reduces to the same visual result as §4.2 for parallel lanes. Deferred; revisit only if the chord
  blend proves inadequate on curved multi-lane edges.
- **Publish intermediate lateral positions from the engine** (make the stream continuous): an engine/
  wire change with parity implications, far outside a viewer task. Out of scope.

## 6. Determinism & parity

Viewer-only. Touches `DrClock` and `PumpAndBuildVehicleDraws` (viewer projects, **out** of
`Traffic.sln`) and *reads* `PoseResolver` (no edit — realism is selected by argument). No engine,
golden, `tolerance.json`, or wire-format change. `dotnet test` is unaffected and needs no SUMO. The
blend is a pure function of the two received packets, so it is independent of thread/arrival order.

## 7. Verification (success is numeric, via the harness)

Using `--trace-veh` on loopback and the lateral/longitudinal decomposition from
`SUMOSHARP-VIEWER-DR-SMOOTHING.md` §7:

- **Lane change is a slide, not a snap** — on `12-overtake` (`follow`) and `07-keep-right-change`, the
  DR-reconstructed lateral coordinate transitions **monotonically** from the old lane centre to the new
  one across the packet interval, with **no single-frame lateral step > 0.5 m** (today: one ~3.2 m
  step, or stuck).
- **Heading tilts** during the change (deg deviates from the straight-lane value and returns), rather
  than the body sliding untilted.
- **No junction regression** — on `44-multilane-junction-turn` (`vN`), max lateral per-frame deviation
  stays **≤ 0.10 m** (the value the junction fix achieved: 0.089 m).
- **Sublane unaffected** — on `61-sublane-sidebyside`, the steady `posLat` offset still renders (no new
  jitter introduced on same-lane vehicles).

## 8. Reusability (3D IG and future viewers)

The classification (§4.1) and blend (§4.2) live in the shared DR code, so any DR viewer inherits them.
For 3D: the chord heading gives yaw directly; interpolate `z` with the same `f`; a lane-change *lean/
roll* animation can be driven from the same lateral-velocity signal. Recorded in
`SUMOSHARP-VIEWER-DR-SMOOTHING.md` §8's pitfalls once implemented.
