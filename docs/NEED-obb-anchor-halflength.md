# NEED — the vehicle-OBB helper has TWO bugs: a wrong forward AXIS and a front-bumper anchor

> ## ⚠ SECOND, WORSE BUG FOUND (2026-07-26) — the forward axis is a REFLECTION
>
> `ObbOverlap`'s forward axis `forward = (-sin θ, cos θ)` is **WRONG for every non-axis-aligned heading.**
>
> `LaneGeometry.PositionAtOffset` returns **naviDegree** (`naviDeg = 90 - atan2(dy,dx)·180/π`,
> `src/Sim.Ingest/LaneGeometry.cs:59-60`). Inverting gives the math angle `α = 90 - θ`, so the true unit
> tangent is
>
> ```
> forward = (cos α, sin α) = (+sin θ, cos θ)      <-- CORRECT
> ```
>
> The committed `(-sin θ, cos θ)` is a **reflection about the y-axis, not a sign flip**. Verified numerically:
>
> | segment | naviDeg | true tangent | committed axis | \|dot\| |
> | --- | --- | --- | --- | --- |
> | east | 90° | (1.000, 0.000) | (-1.000, 0.000) | **1.000 OK** (sign only — harmless for a symmetric box) |
> | north | 0° | (0.000, 1.000) | (0.000, 1.000) | **1.000 OK** |
> | NE | 45° | (0.707, 0.707) | (-0.707, 0.707) | **0.000 — PERPENDICULAR** |
> | SE | 135° | (0.707, -0.707) | (-0.707, -0.707) | **0.000 — PERPENDICULAR** |
> | NNE | 26.6° | (0.447, 0.894) | (-0.447, 0.894) | **0.600 — WRONG AXIS** |
>
> Because a box is symmetric about its axes, a pure sign flip is harmless — which is why the error hides on
> N/S/E/W headings and shows up on everything else. **Junction internal lanes are curved, i.e. mostly
> diagonal — exactly where it is wrong.**
>
> **How it evaded the handoff's own warning.** `F3-JUNCTION-OVERLAP-HANDOFF.md` §6 has a "heading-convention
> lesson" about this very class of bug (a naive mapping "rotates every box 90° → 3215→467 when fixed") and
> says the fix was *"validated on veh80 (`angle=90` runs along world X)"*. **90° is precisely the degenerate
> case where both conventions give the same axis.** The validation could not distinguish them.
>
> **Measured consequence:** in the veh95/veh102 check, the committed convention reported a **false positive
> 0.328 m overlap**; the corrected basis reports **0.000 m** (a genuine 2.99 m clear gap), cross-validated two
> independent ways (differencing consecutive `PositionAtOffset` samples on straight segments of both lanes, and
> a direct arc-length walk).
>
> **Scope: every overlap number in this repo that involves a non-axis-aligned heading is unreliable** —
> including the F3 bucket classifications in `F3-JUNCTION-OVERLAP-DESIGN.md` §1 and this session's A/B tables.
> Direction of error is NOT uniform: it can produce both false positives and false negatives, so re-measurement
> is required rather than a correction factor.
>
> **Fix both bugs together, in one commit, and re-baseline every committed overlap threshold**, because the
> two interact (the anchor shift is applied *along* the forward axis, so a wrong axis mis-applies the anchor
> correction as well).
>
> Correct basis: `forward = (sin θ, cos θ)`, `right = (cos θ, -sin θ)`, `centre = (X,Y) - (Length/2)*forward`.

## Bug 2 (original report) — front-bumper pose treated as the box centre

**Found by:** F3 junction-overlap session (`docs/F3-JUNCTION-OVERLAP-DESIGN.md` §1b).
**Scope:** measurement / diagnostics only — **the engine is NOT affected.**
**Severity:** high for *credibility of every overlap number in the repo*, nil for simulation behaviour.

## The bug

The sampled vehicle pose is the **front bumper**:

- `LiveCitySim.Sample()` copies `_lastSnapshot.PosX/PosY`.
- Those are filled at `src/Sim.Core/Engine.cs:2278` from
  `LaneGeometry.PositionAtOffset(lane.Shape, v.Kinematics.Pos, v.Kinematics.LatOffset)`.
- `Kinematics.Pos` is the **front-bumper** arc-length (SUMO `getPositionOnLane()` / FCD convention), and
  `LaneGeometry.PositionAtOffset` returns the point at that arc-length — it subtracts **no** half-length.

But `ObbOverlap` treats `(X, Y)` as the box **CENTRE**, building `±Length/2` about it:

```csharp
var centerGap = Math.Abs((b[0] - a[0]) * axX + (b[1] - a[1]) * axY);
return Half(a, axX, axY) + Half(b, axX, axY) - centerGap;   // Half() = half-extents from centre
```

So **every vehicle box is drawn shifted forward by `Length/2` (2.5 m for the demo's 5.0 m cars).**

## Correct form

```
forward = (-sin θ, cos θ)                      // heading convention already validated -- do NOT change it
centre  = (X, Y) - (Length / 2) * forward
```

## Affected call sites

- `tests/Sim.LiveCity.Tests/DemoCarOverlapInvariantTests.cs` (`ObbOverlap`) — the committed invariant.
- `tests/Sim.LiveCity.Tests/F3JunctionOverlapDiagTests.cs` — the F3 diagnostic (has both variants).
- Historically `RunLiveCityDrCheck` in `src/Sim.Viz/Program.cs` (now deleted; same math, commit `d9b209b`)
  — so any DR-render overlap check added later must not re-inherit the bug.
- Anything else that renders a car box from `Sample()`/`VizReplayBuilder` poses (the 3D viewer / HTML replay
  are likely also drawing cars half a length too far forward — **worth checking separately**).

## Measured impact (demo_city, 200 steps)

| | front-anchor (current) | centre-corrected |
| --- | --- | --- |
| total overlap events | 61 | 97 |
| worst penetration | 3.035 m (`__veh134/__veh38`) | 2.981 m (`__veh73/__veh97`) |
| `BOTH-INTERNAL-DIFFERENT-LANE` (F3) | 8, worst 3.035 m | 13, worst 1.987 m |
| `ONE-INTERNAL-ONE-NORMAL` | 31, worst 1.800 m | 62, worst 2.981 m |

**The headline F3 number is mostly an artifact:** anchored correctly, the famous `__veh134/__veh38` pair
drops **3.035 m → 0.497 m**.

Counter-intuitively the corrected count is *higher* (61 → 97). That is expected: **both** cars shift, so the
correction changes *which* pairs overlap rather than uniformly shrinking penetration. It is therefore not
safe to assume "fixing the anchor makes the numbers better" — it re-baselines them in both directions.

Note also that the recurring **`1.800 m`** figure is exactly the vehicle **width** (L=5.0, W=1.8) — the
minimum-penetration separating axis saturating at full lateral overlap. It is a ceiling artifact of the
metric, not a depth, and it is identical in both variants for co-located pairs (a shared forward shift
cancels when both cars have the same pose).

## Why this matters before anything else

Every committed overlap threshold (`worst > 0.5`, `worst < 4.0`, `pairs <= 7`) was calibrated against
front-anchored boxes. Until this is fixed, **no overlap ceiling in the repo means what it says**, and any
"assert ZERO overlap" work (F4b) would be asserting against a mis-anchored measurement.

## Suggested fix

Extract one shared, tested `VehicleObb(x, y, angleDeg, length, width)` helper that does the back-shift once,
put it where both the tests and any viz/DR consumer can reference it, and **re-baseline every committed
overlap threshold** against it in the same commit. Do not fix the anchor without re-calibrating the ceilings
in the same change, or the invariant tests will flip meaning silently.
