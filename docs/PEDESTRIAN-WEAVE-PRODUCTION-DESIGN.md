# Pedestrian weave — production-seam design (PED-REALISM-1 / "(b)")

**Status:** design, pre-implementation. **HOW** for graduating the validated `LateralWeave` from the Sim.Viz
prototypes (A–D2) into the real low-power motion path. The **WHAT** (behaviour, determinism/parity argument,
the density ceiling, the restore mechanism) is `docs/PEDESTRIAN-LOWPOWER-AVOIDANCE-DESIGN.md` (esp. §7, §8,
§10.2, §10.2-bis, §11); this doc does not restate it. Read that first.

This closes the §8 "production requirements the prototype does NOT cover": whole-route continuity, endpoint
anchoring to the actual O/D, smooth junction hand-off, and a per-edge shared width — plus the demote-restore
wiring (§10.2). It deliberately does **not** do the per-ped-demote-timing optimization ("(a)", §10.2-bis wire
cost) — that is a later, non-blocking tuning knob; nothing here is made harder by deferring it.

---

## 1. The load-bearing insight: inject at the SHARED evaluator

Both sides already compute a low-power pose through the **same** pure code:

- **Server:** `PedLodManager.PositionOf(id, now)` → `PathArcMotion.PositionAt(...)` / `ActivityTimeline.PoseAt(...)`.
- **IG / remote:** `PedRemoteReconstructor.TryGetRenderPose` → `HeadlessIg.ReconstructSample(id, t)` (Sim.Replication)
  → the decoded `ActivityTimeline.PoseAt` / `PathArcMotion.PositionAt`.

So if the weave offset is applied **inside `PathArcMotion.Walk` / `ActivityTimeline.Evaluate`** (the shared
evaluator), *both* sides get the identical offset from the identical inputs, and **server==IG holds by
construction** — no new reconstruction logic, no per-ped weave bytes on the wire. The wire's only job is to
carry the *inputs* the evaluator needs (per-ped `seed`, per-leg `halfWidth`, and at a demote seam the `l_r`
scalar); everything else is recomputed. This is the whole reason the prototype used pure functions.

**Consequence for the plan:** the change is "thread three inputs to a function that already exists", not
"teach the reconstructor to weave". The reconstructor (`PedRemoteReconstructor.cs`) and its `Smooth` chase are
**untouched**; because `OffsetWithResume` returns exactly `l_r` at the demote seam, the smoother never sees a
pop to absorb.

### Seam map (from the code, for reference)
| file | seam | role |
|---|---|---|
| `Lod/PathArcMotion.cs:61` `Walk(path, s, out direction)` | **server injection** | already computes arc-length `s` + unit tangent; add `point + direction.PerpCW * off` |
| `Lod/PathArcMotion.cs:17` `PositionAt(path, startTime, speed, now)` | evaluator entry | needs a weave-aware overload carrying `(seed, halfWidth, routeLength, resume?)` |
| `Lod/ActivityTimeline.cs:57` `WalkSegment(Path, Speed)` | data model | gains per-leg `HalfWidth` (+ optional resume `l_r`,`leadIn`); the timeline gains a per-ped `Seed` |
| `Lod/ActivityTimeline.cs:261` `Evaluate` Walk case | evaluator | the `PositionAt` call that layers the offset |
| `Lod/ActivityTimelineWire.cs:49/109` `Encode`/`Decode` | wire | +`ulong seed` (per timeline), +`double halfWidth` (per Walk), +resume fields on a demote leg |
| `Lod/PedLodManager.cs:389-406` demote block | **`l_r` re-injection** | project frozen high-power pose onto the new lane centreline → `l_r`; publish it |
| `Lod/PedLodManager.cs:157/188` `AddPed`/`AddPedLively` | seed + width source | assign per-ped seed; thread lane half-width onto the leg |
| `PedNetwork.cs:21` `PedLane(... Width ...)` | width origin | `Width/2`, with the established `0.5 m` fallback (`WalkablePolygonBaker.cs:63`) |
| `Sim.Replication` `HeadlessIg.ReconstructSample` | IG mirror | **no logic change** — decodes the new fields via the wire, calls the same `PoseAt` |

---

## 2. Data-model changes

1. **Per-ped `Seed` (`ulong`).** Lives on `ActivityTimeline` (per-ped) and on the plain-PathArc `PedEntry`.
   Assigned once at `AddPed*`, from a deterministic source (the ped id mixed with a scenario seed — the same
   SplitMix64 family, never `System.Random`). This is the `seed` argument to every `LateralWeave.Offset`.

2. **Per-leg `HalfWidth` (`double`).** On `WalkSegment` (and the PathArc leg). The corridor half-width the
   offset clamps to. **POC:** one constant per leg (the min lane half-width along the leg — conservative, never
   overflows a narrowing). **Refinement (W5):** piecewise per-edge `halfWidth(s)` when a leg spans edges of
   differing width, smoothed over a short blend at edge joins so the band doesn't step.

3. **Resume fields on a demote leg only:** `ResumeLateral` (`l_r`, `double`) + `LeadIn` (`double`). Absent /
   sentinel on a normally-spawned leg (which uses `Offset`); present on a leg emitted by a demote (which uses
   `OffsetWithResumeOnRoute`). One flag or a `NaN` sentinel distinguishes the two.

No change to `PauseSegment`/`DwellSegment`/`InteractSegment` — the weave is a Walk-only concern.

---

## 3. The evaluator injection (server == IG)

In the shared Walk evaluation, at arc-length `s` along a leg of length `L` with tangent `t̂`:

```
centre = Walk(path, s)                       // existing polyline point
n̂      = t̂.PerpCW                            // right-normal (already available in Walk)
off    = resumeLeg
         ? LateralWeave.OffsetWithResumeOnRoute(sAbs, sSinceDemote, L, seed, halfWidth, l_r, leadIn, P)
         : LateralWeave.Offset(s, L, seed, halfWidth, P)
pose   = centre + n̂ * off
```

- `s` and `t̂` already exist inside `Walk` (`PathArcMotion.cs:90-91`); only `seed`, `halfWidth`, `L`, and the
  multiply are added.
- **Heading:** keep the *tangent* `t̂` as the render heading (the lateral drift is small and slow; using the
  raw tangent avoids a heading wobble and keeps parity with today). Optionally, a later polish derives heading
  from the offset's ds-derivative; not in scope.
- **Whole-route continuity (§8-1):** emit the **entire remaining route as one Walk leg**; `Offset(s, L)` is
  continuous in `s` across edge boundaries and junction curves (prototype B proved the bend), so no seam at
  edges. Width variation is handled by `halfWidth(s)` (W5), not by splitting the leg.
- **Endpoint anchoring (§8-2):** `Offset`'s endpoint taper is a function of the leg's own `(s, L)`. Because the
  leg IS the whole route from true spawn to true arrival, the taper lands the ped exactly on the real O/D — not
  on every internal edge end. A demote leg uses the **arrival-only** taper (`OffsetWithResumeOnRoute`), so it
  converges to the destination but does **not** re-taper at the demote seam.
- **Junction hand-off (§8-3):** at a walkingArea the centreline bends; `t̂` (hence `n̂`) rotates smoothly with
  it, so the offset rides the curve. No special-casing.

**server==IG test:** encode a weave leg, decode it, and assert `HeadlessIg.ReconstructSample == server PositionOf`
bit-for-bit across the leg (the wire uses full doubles, no quantization — `ActivityTimelineWire` already
guarantees this for the existing fields).

---

## 4. Width threading (PedLane → leg)

Width is first-class on `PedLane.Width` but is dropped before the LOD layer — `IPedNavigation.FindPath` returns
a bare `IReadOnlyList<Vec2>`. Threading options:

- **POC (chosen):** after `FindPath` returns the centreline polyline, look up the min half-width of the lanes
  the route traverses (a `routeId/edgeId → Width/2` lookup already implied by the bake), and stamp one
  `HalfWidth` on the leg. Small, additive, no signature churn on `FindPath`.
- **W5 refinement:** return per-vertex/per-edge width alongside the polyline (a small `RouteWithWidth` record),
  and evaluate `halfWidth(s)` piecewise. Only needed once legs routinely span differing-width edges.

Fallback: `0.5 m` half-width when `Width` is unset — matching `WalkablePolygonBaker.cs:63` exactly, so the
motion band and the baked walkable strip agree.

---

## 5. The demote re-anchor (`l_r`) — production specifics

`PedLodManager` demote (`cs:389-406`) already **re-routes from the frozen high-power position** via
`FindPath(frozenPos, dest)` and emits a fresh PathArc leg — structurally exactly the crosser/bystander model.
Two production specifics the prototype glossed:

1. **`l_r` = perpendicular offset of the frozen pose from the NEW leg's centreline**, not a corridor constant.
   `FindPath` snaps the route to lane centrelines; the frozen ORCA pose is generally off-centre. Compute
   `l_r = signdist(frozenPos, centrelineAt(s≈0))` using the leg's start tangent's right-normal. Feeding this to
   `OffsetWithResumeOnRoute(interiorArc, blendDist, ...)` makes `pose(blendDist=0) == frozenPos` exactly
   (no pop), then blends to the lane weave over `LeadIn`.
   - **Guard:** if `|l_r| > halfWidth` (ORCA left the ped well off the sidewalk), clamp the *blend target* but
     still start at the true `l_r` — continuity beats staying in-band for one lead-in.
2. **Absolute-arc vs restart:** a demote that continues the ped's own route uses `OffsetWithResumeOnRoute` with
   `interiorArc = s_r + blendDist` (bystander form, §10.2-bis) so it rejoins its own seeded track; a demote
   onto a genuinely new route (the crosser reached a new POI/edge) uses `OffsetWithResume` (interiorArc==blend).
   The LOD manager knows which (did the destination/edge change?), so it picks the call.

`LeadIn` default: the design's `~8 m` (prototype value); a tunable on the demote.

---

## 6. Wire delta + determinism

`ActivityTimelineWire` is a fixed positional layout of full LE doubles (bit-exact by design). Additions:

- `+ ulong Seed` once per timeline (after `T0`).
- `+ double HalfWidth` per Walk segment (after `speed`).
- `+ (byte hasResume, double ResumeLateral, double LeadIn)` per Walk — only meaningful on a demote leg.

This is a **breaking format bump** (no version tag today): `Encode`/`Decode` and `ActivityTimelineWireTests`
change together. The plain-PathArc publish path (`PublishPathArc`, `PedLodManager.cs:176/404`) carries the same
three inputs via its own record — mirror the addition there. Determinism unchanged: all inputs are exact
doubles/ulongs; the offset is a pure SplitMix64 function; no `System.Random`; independent of thread order.

**CenterShift** (the shared counterflow-interface meander) needs a **scenario-global seed** on the replication
header (broadcast once). Deferred to W5 — the per-ped `Offset` is the core; `CenterShift` is counterflow polish.

---

## 7. Parity & safety (the iron law)

This is **additive to the pedestrian LOD path only**. It does not touch the car-following / lane-change /
junction parity core, so no committed `tolerance.json` can move. The offline gate (`dotnet test`, 649 parity +3
skip) must stay green throughout; the new work adds *ped* unit tests, not parity goldens. The existing ped
LOD/reconstruction tests (`ActivityTimelineWireTests`, promote/demote, P3-3 reconstruction) are the regression
guard — each stage keeps them green. SUMO is not involved (pedestrian weave is a SumoSharp believability layer,
not a SUMO-parity behaviour).

---

## 8. Staged tasks (with success conditions) + tracker

Each task names its design section, the files, and a numeric/observable done-condition.

**W1 — evaluator injection + data model (seed, per-leg halfWidth).** §2, §3.
Files: `PathArcMotion.cs`, `ActivityTimeline.cs`, `ActivityTimelineWire.cs` (seed+width only).
Done: a low-power ped's `PoseAt(now)` equals `centre + n̂·LateralWeave.Offset(s,L,seed,halfWidth)` (new unit
test); `Seed`/`HalfWidth` round-trip through Encode/Decode; **decode→PoseAt == server PoseAt bit-for-bit** over
a leg; all existing ped tests green.

**W2 — width threading from `PedLane`.** §4.
Files: navigation route seam, `PedLodManager.AddPed*`.
Done: a leg over a lane of known `Width` carries `Width/2` (and `0.5` when unset); the offset clamp never
exceeds the baked walkable half-width for a scenario lane (assert against `WalkablePolygonBaker` half).

**W3 — reconstructor server==IG on the weave path.** §1, §3, §6.
Files: `HeadlessIg` (Sim.Replication) decode wiring; `PedRemoteReconstructor` (verify untouched).
Done: a replicated weave ped's `ReconstructSample` matches the server pose to `0` (exact) across a whole leg;
the P3-3 reconstruction demo/test shows the weave with zero DR error on low-power spans.

**W4 — demote re-anchor (`l_r`) with no pop.** §5, §10.2/§10.2-bis of the avoidance doc.
Files: `PedLodManager` demote block, `ActivityTimelineWire` resume fields, evaluator resume branch.
Done: promote→demote of a low-power ped resumes with seam ‖Δ‖ < 1e-9 m; server==IG exact after demote; a
bystander shoved by a promoted neighbour rejoins its own track (extend the promote/demote test with the D2
assertions ported off the Sim.Viz prototype).

**W5 — refinements (deferred, non-blocking).** §3, §4, §6.
Piecewise per-edge `halfWidth(s)` with join smoothing; `CenterShift` scenario-global seed + counterflow
interface; optional heading-from-offset. Gated on need (measured), not required for the core to ship.

### Tracker
- [ ] **W1** evaluator injection + seed/halfWidth data model + server==IG bit-exact unit test
- [ ] **W2** width threaded from `PedLane` (min half-width per leg, 0.5 fallback)
- [ ] **W3** `HeadlessIg` decodes new fields; reconstruction exact on the weave path
- [ ] **W4** demote re-anchor `l_r` (project frozen pose → centreline); no-pop seam < 1e-9; D2 assertions ported
- [ ] **W5** (deferred) piecewise width + `CenterShift` global seed + heading polish

---

## 9. Open questions for sign-off
1. **Seed source:** derive per-ped seed from `hash(pedId, scenarioSeed)` (reproducible across a run) — confirm
   there's a scenario-global seed to mix in, or introduce one.
2. **Width granularity for the POC:** one min-half-width per leg (W2) acceptable as the first cut, with W5 for
   piecewise? (Recommended: yes.)
3. **Heading:** keep raw tangent as render heading for now (recommended), or derive from the offset derivative?
4. **CenterShift:** confirm it's OK to ship the core `Offset` weave first and add the counterflow-interface
   `CenterShift` in W5, rather than together.
