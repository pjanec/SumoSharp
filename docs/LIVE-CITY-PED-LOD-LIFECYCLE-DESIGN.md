# DESIGN — live-city ped LOD lifecycle (promote flicker, stuck-ORCA/wander, idle clustering)

**HOW the three ped-LOD-lifecycle fixes work.** The WHAT lives in `docs/LIVE-CITY-PED-LOD-LIFECYCLE-HANDOFF.md`
(owner intent, scope table, boundaries) — this doc does not restate it. Task breakdown is in
`docs/LIVE-CITY-PED-LOD-LIFECYCLE-TASKS.md`; the checklist is `docs/LIVE-CITY-PED-LOD-LIFECYCLE-TRACKER.md`.

Scope: three reported unrealisms — **#3** promote flicker (ped vanishes for a frame on promotion), **#4**
stuck-ORCA / off-route wander (a ped that left the zone stays high-power and walks off the sidewalk), **#6**
idle clustering (low-power peds merge to one junction and idle). Edit surface is entirely
`src/Sim.Pedestrians/Lod/` + the ped-demand config + a demo-only trace harness in `src/Sim.Viz`.

---

## 0. Determinism / parity argument (applies to every change below)

The whole ped/LOD path is inert for the parity and bench goldens: it runs only when `Engine.CrowdSource != null`
(`LiveCitySim.cs:356–360`), and no golden scenario attaches one — parity/bench drive `Engine` directly, never
`LiveCitySim`. So every change here is **parity-inert by construction**; we still re-run all three gates to
prove it (§7). Rules honoured throughout:

- **No `System.Random`.** Every new random draw (#6 destination jitter) uses `Sim.Core.VehicleRng` (SplitMix64)
  seeded per-ped from `(config.Seed, id, dedicated-salt)`, exactly like the existing spawn-timing / O-D / liveliness
  streams in `PedDemand` (`PedDemand.cs:31–47`). A new salt is added so enabling the jitter never perturbs the
  existing streams (the ITERON rule the file already follows).
- **Structural mutations stay deferred to `Step`.** No change adds a mid-call Add/Remove of a crowd agent; the
  demote/promote apply passes keep their existing "collect then apply in ascending-id order" shape.
- **The produce/consume seam is untouched.** `PedLodManager.HighPowerFootprints` (`:99`) and the
  `ICrowdFootprintSource` contract are read verbatim; #3/#4/#6 change promote/demote *internals* and the
  *consumer-side* reconstruction, never the footprint contract the car sessions read.
- **No `IPedNavigation` reshape, no zone redefinition.** `IPedNavigation.FindPath` is called as-is; the realism
  zone geometry/radii (`SetLcRealismZone`, the single `InterestSource`) stay arbitrary-net's surface. #4b's
  off-graph recovery is built from state `PedLodManager` already owns (the ped's last on-graph route), not from a
  new nav query.

---

## 1. Repro-first: the headless LOD-lifecycle trace (Stage 0, gates everything)

The owner rule (and the handoff) is **solid repro before any fix; the DR HTML player may misrender, so prefer
frame-level ground truth over eyeballing.** There is no ped-LOD trace today (only `--live-city-demo`'s HTML and a
car trace). We build one first; it is a *diagnostic*, not a behavioural change, and it decides where each fix
belongs before a line of fix code is written.

**Mechanism.** A new demo-only entrypoint `--live-city-pedtrace <out.csv> [steps]` in `src/Sim.Viz/Program.cs`,
modelled on the existing `--live-city-*` harnesses. It builds the real `LiveCitySim` (same `LiveCityConfig`,
`LIVECITY_PEDS` honoured), steps it headless, and each step dumps **per ped**: `step, now, id, model
(PathArc/ActivityTimeline/FreeKinematic), highIndexValid, stateEnteredAt, outsideSince, worldX, worldY,
onGraph`. To read `stateEnteredAt`/`outsideSince`/`highIndex` — private on `PedEntry` — `PedLodManager` gains a
**read-only diagnostic snapshot** accessor (additive, no behaviour change):

```csharp
public readonly record struct PedLodDiag(int Id, PedDrModel Model, bool HighIndexValid,
    double StateEnteredAt, double OutsideSince, Vec2 Pos);
public IEnumerable<PedLodDiag> DiagnosticSnapshot(double now);   // ascending id; PositionOf per model
```

It also emits, in parallel, the **wire-reconstructed** pose per ped by driving a `PedRemoteReconstructor` off the
sim's `PedSource` (the exact consumer the demo uses) — so the trace shows *both* the server-truth pose and the
IG-rendered pose+visibility every step. The gap between them at a switch instant is #3, made into data.

**What the trace confirms (the open questions the fixes are gated on):**
- **#3** — at the promote step, does the wire-reconstructed ped have a valid, on-body pose and `visible==true`,
  or does it snap to ~origin / go invisible for ≥1 frame? (Confirms producer-defers-first-sample vs consumer gap.)
- **#4a** — for a ped that leaves the zone: does `outsideSince` ever hold `dwellSeconds` continuously, or does it
  keep resetting to NaN (`PedLodManager.cs:391`) at the zone edge? Does the ped ever get outside the (large)
  demote radius at all, or does it wander within it forever?
- **#4b** — after promotion/demotion, is the ped's route `onGraph` (multi-segment, on a sidewalk) or a single
  straight segment to a far destination (the null-FindPath fallback, `:406`/`:430`)?
- **#6** — the distribution of low-power **idle** (Paused) positions: how many distinct clusters, and are they a
  route/destination funnel (many peds share one junction waypoint) or genuinely one destination?

Success-condition assertions (Stage 2–4) are written against this trace's outputs, not against the HTML.

---

## 2. #3 — promote flicker (one-frame vanish on promotion)

**Verified chain.** On the promote step N the server publishes, in order: `DrSwitchEvent(PathArc→FreeKinematic)`
in the promote loop (`PedLodManager.cs:419`), then at the end of the step a `FreeKinematicSample` for the
now-FreeKinematic ped (`:484–490`). The consumer applies them via `PedReplicationReceiver.Drain` in the order
PathArc → Timeline → lifecycle(DrSwitch) → latest-crowd-frame batch (`PedReplicationReceiver.cs:32–74`), and
`HeadlessIg` renders a FreeKinematic ped as `LastPos + LastVel·(now − LastSampleTime)`, **always `visible=true`**
(`HeadlessIg.cs:75`, `:99`). `PedRemoteReconstructor` renders at `RenderTime = latestServerTime − 0.15s`
(playout delay, `PedRemoteReconstructor.cs:100`).

**Root cause (to confirm with the Stage-0 trace, then fix).** The `DrSwitchEvent` is a lifecycle record and is
always delivered; the ped's **first** `FreeKinematicSample`, however, flows through the ped replication
publisher's DR-error / bandwidth governor, which can **defer** a sample whose predicted pose is close to actual.
When the switch is delivered but the first sample is deferred, the IG has `Model=FreeKinematic` with `LastPos`
still `default(Vec2)` (== origin) and `LastSampleTime==0` → `Reconstruct` returns a pose near the origin, far
off-body → the ped "disappears" from view for one-or-more frames, then "reappears as ORCA" when the first real
sample finally lands. This is exactly the reported symptom and is a **consumer/producer handoff gap at the switch
instant**, matching the handoff's lead.

**Fix — consumer-side seed-on-switch (primary, fully in-surface).** In `HeadlessIg.Apply(DrSwitchEvent s)` when
`s.To == FreeKinematic`, if the ped has no FreeKinematic sample yet, **seed** its FreeKinematic state from the
pose it is currently reconstructing under its *previous* (low-power) model at the switch time `s.Time`:
`LastPos = Reconstruct(id, s.Time)` (the PathArc/Timeline pose it still holds), `LastVel = 0`, `LastSampleTime =
s.Time`. Now the moment the switch is observed the ped has a valid on-body pose; the first real sample overwrites
it seamlessly. Symmetric guard already holds the other direction (a demote republishes a PathArc/Timeline leg, so
the low-power branch has a path immediately). This is a few lines in `HeadlessIg.cs`, parity-inert (no golden
wires peds), and needs no producer change.

**Fix — producer-side alternative (only if the trace shows the seed is insufficient).** Force the promoted ped's
**first** `FreeKinematicSample` to bypass the DR-error deferral for the switch step, so the wire always carries a
pose in the same batch as the switch. Kept as a fallback because it touches the publish scheduler; the trace
decides whether the consumer seed alone closes the gap (expected: yes).

**Success condition.** Across every promote (and demote) transition in the trace at `LIVECITY_PEDS=1600`, the
wire-reconstructed ped is `visible==true` **and** within a small radius (≤ ped step distance) of the server-truth
pose on **every** frame — no frame with a missing/origin-snapped pose. Asserted as a unit test on
`HeadlessIg`/`PedRemoteReconstructor` (promote a ped, defer its first sample, assert the rendered pose stays
on-body) and as a whole-run trace invariant.

---

## 3. #4 — stuck-ORCA / off-route wander

Two coupled roots. #4b (route quality) is the *cause of the wander*; #4a (demote trigger) is the *guarantee it
eventually ends*. Fix #4b first — a ped that walks its real route leaves the zone and demotes on its own; #4a is
the backstop for the residual edge-loiter case.

### 3.1 #4b — route restore falls back to a straight line

**Verified.** Both promote (`PedLodManager.cs:406`) and demote (`:430`) do
`_navigation.FindPath(pos, Destination) ?? new[] { pos, Destination }`. When `FindPath` returns null — the frozen
pose `pos` is off the navmesh, which happens precisely for a ped that has *already* wandered off-graph as ORCA —
the fallback is a **single straight segment to a far destination**, ignoring sidewalks. ORCA then steers the ped
straight across, cutting off-route: the visible wander. And because that straight line can keep the ped within the
large demote radius, it feeds #4a (never demotes).

**Off-graph recovery policy (in-surface, no nav-interface change).** A high-power ped's `PedEntry.Path` is the
on-graph steering route set at promotion (a real `FindPath` result from an on-graph pose — the low-power pose at
promotion lies on the ped's low-power sidewalk route, so promotion's `FindPath` almost always succeeds). On
demote, instead of routing from the possibly-off-graph `pos`:

1. Try `routed = FindPath(pos, Destination)` as today.
2. If null, **recover onto the last-good route**: find the nearest vertex `v*` on `e.Path` (the retained on-graph
   polyline) to `pos`, and build `routed = [pos, v*, …tail of e.Path to Destination]`. This is guaranteed
   multi-segment and on-graph (every point past `v*` came from a real `FindPath`), and the lead-in `pos → v*` is
   just the short wander offset. `ReanchorAt(routed, pos)` (`:226`) already guarantees the leg starts exactly at
   `pos` (no positional pop).
3. Only if `e.Path` itself is degenerate (was a straight-line fallback at promotion — rare) does it keep the
   straight line, and #4a's watchdog then guarantees the ped still demotes.

The nearest-vertex-on-polyline and tail-splice are pure geometry helpers added to `PedLodManager` (private,
deterministic). The same recovery is applied at **promotion** so a promoted ped never starts on a straight-line
fallback either. **Open sub-decision for Stage 0:** confirm whether `SumoNavMesh.FindPath` already projects a
slightly-off-mesh `pos` onto the nearest polygon (in which case null is genuinely rare and step 2 is the safety
net) or returns null readily (step 2 is load-bearing). The trace's `onGraph` column measures this directly.

### 3.2 #4a — demote trigger never completes

**Verified.** The demote countdown is a **strict continuous window**: `OutsideSince` is set when the ped is first
`AllOutsideDemote` and **reset to NaN the instant it re-enters any demote radius** (`PedLodManager.cs:391`), and
demote fires only after `now − OutsideSince ≥ dwellSeconds` *continuously* (`:384`). A ped loitering at the zone
edge — or one the camera-driven zone (`SetLcRealismZone`) intermittently re-covers — never accumulates
`dwellSeconds` unbroken → never demotes. With #4b unfixed it also may never get outside the demote radius at all.

**Fix — leaky dwell + hard watchdog (deterministic, parity-inert).** Replace the binary reset with an
**accumulating** dwell on `PedEntry`:

- While `AllOutsideDemote`: `outsideAccum += dt`.
- While inside some demote radius: `outsideAccum = max(0, outsideAccum − dt)` (leak down, do **not** slam to 0).
- Demote when `stateAge ≥ dwellSeconds && outsideAccum ≥ dwellSeconds`.

This preserves the existing hysteresis intent (a ped genuinely re-entering the zone for a sustained spell stays
high) while a ped hovering at the edge still accumulates net outside-time and demotes — the flap test
(`PedLodManagerTests.Demotion_DoesNotFlap_...`) still passes because a stimulus bouncing across the boundary every
step nets ~zero accumulation change and never reaches the threshold from repeated brief exits.

Plus a **hard stuck-ORCA watchdog** as the absolute guarantee: a ped that has been high-power for
`MaxHighPowerSeconds` **and** is currently `AllOutsidePromote` (held only by the hysteresis band, no live promoting
source within promote radius) force-demotes regardless of the accumulator. `MaxHighPowerSeconds` is a new
`PedLodManager` ctor knob (default large enough to never fire for a legitimately-observed ped, e.g. 30 s), and the
watchdog is skipped for `ForcedHighPower` peds (evac-pin semantics preserved). This makes success condition #4a —
"no permanently-stuck ORCA peds" — a hard invariant, not a hope.

**Success conditions.** (a) A ped continuously outside every demote radius for `dwellSeconds` demotes (existing
test still green). (b) A ped that leaves the zone and stays out — even while briefly clipping the edge — demotes
within a bounded time (new test: oscillate a ped at the demote edge with net-outward drift, assert demote). (c) A
ped pinned high by the hysteresis band with no promoting source force-demotes by `MaxHighPowerSeconds` (new test).
(d) In the trace at 1600 peds, zero peds remain FreeKinematic while > demoteRadius from the zone for longer than
`MaxHighPowerSeconds`.

---

## 4. #6 — idle clustering (LOW PRI)

**Verified substrate.** `LiveCitySim` sets `Origins == Destinations == odPoints` (the same shared set,
`LiveCitySim.cs:245–246`), each ped drawing a uniform origin+destination (`PedDemand.cs:206–211`); low-power peds
do not avoid each other (no ORCA), so any shared route waypoint or shared destination is rendered as an exact
overlap — a visual cluster. Idle (Paused) beats are inserted at seeded along-route fractions
(`PedDemand.cs:290–300`), which already varies per ped, so the cluster is most likely a **destination/route
funnel**, not the pause position.

**Diagnosis-gated (Stage 0 trace).** The trace measures the idle-position distribution and whether clusters
coincide with shared destination points or shared route junctions. The fix is chosen from what it shows:

**Primary fix — per-ped seeded destination jitter (deterministic).** In `PedDemand.TrySpawnOne`, after drawing
`destination` from the set, apply a small seeded positional offset within a bounded radius using a **new dedicated
salt** (`DestJitterSalt`), then route to the jittered point; if `FindPath` to the jittered point is null, fall
back to the exact drawn point (no extra draw, deterministic). This spreads arrival/idle spots off the shared node
without enlarging the O-D set or touching nav. Enabled by an additive, opt-in
`PedDemandConfig.DestinationJitterRadius` (0 == off == byte-identical to today — the ITERON rule), wired on in
`LiveCitySim` (demo-only).

**Secondary fix (if the trace shows pause-position, not destination, clustering).** Draw each Pause's idle spot as
a small seeded offset from the along-route pause point (same salt discipline), so idlers near a shared junction
fan out. Chosen only if the trace attributes the cluster to pause positions.

Both are seeded per-ped (same seed → same layout), parity-inert (only reached when `Liveliness`/jitter is
configured, which no golden does), and use no `System.Random`.

**Success condition.** A spread metric over low-power idle positions in the trace — e.g. number of distinct
occupied cells on a coarse grid, or mean nearest-neighbour idle distance — is materially above the current
single-cluster baseline (target set from the measured baseline in Stage 0), with the parity/bench/livecity gates
still green.

---

## 5. Files touched (by concern)

- **Trace + diag (Stage 0):** `src/Sim.Viz/Program.cs` (new `--live-city-pedtrace`), `PedLodManager.cs`
  (additive `DiagnosticSnapshot`).
- **#3:** `src/Sim.Pedestrians/Lod/HeadlessIg.cs` (seed-on-switch); possibly `PedReplicationReceiver.cs` /
  publisher only if the producer fallback is needed (trace-gated).
- **#4:** `src/Sim.Pedestrians/Lod/PedLodManager.cs` (leaky dwell + watchdog on `PedEntry`/`Step`; off-graph route
  recovery helpers + demote/promote apply).
- **#6:** `src/Sim.Pedestrians/Demand/PedDemand.cs` (`DestJitterSalt`, jitter in `TrySpawnOne`,
  `PedDemandConfig.DestinationJitterRadius`); `src/Sim.LiveCity/LiveCitySim.cs` (wire the demo-only knob).
- **Tests:** `tests/Sim.Pedestrians.Tests/Lod/PedLodManagerTests.cs` (demote-completes, watchdog, seed-on-switch,
  on-graph-route), plus a demand spread test in `tests/Sim.Pedestrians.Tests`.

## 6. Boundaries honoured (no-touch)

Per `docs/COORDINATION-livecity-realism-sessions.md` and the handoff §7: no edits to `CrowdLongitudinalConstraint`,
the B6 swerve, `CrossRegimeCoupling`, `ExternalObstacle`, `OrcaCrowd.SetExternalObstacles` (ped–vehicle); no edits
to `ComputeLateralEvasion`/`SuppressHeldCrowdSwerve` or the lateral commit (realism-A/B); no reshaping of
`IPedNavigation`/`SumoRouteGraphNav`, net import, `Engine.RegionPlan`, or the realism-zone surface (arbitrary-net);
no change to the `ICrowdFootprintSource` contract / `HighPowerFootprints` semantics. `OrcaCrowd.Add`/`Remove`
(agent lifecycle) is used as-is — a different surface from the ped–vehicle external-disc feed. `LiveCitySim.cs`
edits stay local to the demand-wiring lines (§5).

## 7. Iron laws / gates (re-run to prove parity-inert)

**Measured baseline on the clean tree (T0.1, 2026-07-25):**
- `dotnet test tests/Sim.ParityTests -c Release` = **661 total / 4 skipped (657 pass, 0 fail)** byte-identical.
- `dotnet run -c Release --project src/Sim.Bench` hash **`D96213B7BB4021A7`** (par == single, confirmed).
- `dotnet test tests/Sim.LiveCity.Tests` = **43/43** (run **without** `--no-build`; not in `Traffic.sln`). Note:
  the handoff/COORDINATION say `27`/`25` — both stale; the suite has grown to 43. **43/43 is the real gate.**
- `dotnet test tests/Sim.Pedestrians.Tests` = **272/272**, plus the new lifecycle/demand tests this work adds.
- No `System.Random`; every new draw seeded per-ped via `VehicleRng`.

## 8. Open decisions carried into implementation (trace-gated)

1. **#3** producer fallback needed, or does consumer seed-on-switch alone close the gap? (Expected: seed alone.)
2. **#4b** does `SumoNavMesh.FindPath` project a slightly-off-mesh pose (null rare) or return null readily
   (recovery load-bearing)? Sets how often step-2 recovery runs.
3. **#4a** `MaxHighPowerSeconds` value — large enough never to fire for a legitimately-observed ped, small enough
   to kill a genuine stuck ORCA promptly; tuned from the trace.
4. **#6** destination-jitter vs pause-spot randomization — chosen from the trace's cluster attribution; jitter
   radius tuned to the spread metric.
