# HIGH-DENSITY-X1-DESIGN.md — attention-aware / camera-based selective popping

**Status:** design (owner picked X1 next, 2026-07-17). Item **X1** from `SUMOSHARP-HIGH-DENSITY-FEATURES.md`
§5 (the flagship non-parity extra). **No SUMO golden** — SUMO's controls are global; this is the reason to
own an engine. Validated functionally + statistically, not against a golden. Depends on P1-F (teleport
action — done) and the insertion path (done).

## 1. WHAT (the capability) — reference the spec for the WHY

Spend the pop budget non-uniformly. A **realism mask** over the network follows the camera / player
attention. In the **high-realism zone** (visible) enforce strict no-cheating — no teleport, no on-lane
spawn/despawn. In the **low-realism zone** (off-camera) allow cheating — teleport eagerly, hold-then-pop.
Net effect: the gridlock knee becomes a property of the *visible* area only; jams and their resolution
are pushed off-camera, so the <1% pops that buy density become invisible. (Full rationale: spec §5 / §X1.)

## 2. HOW (mechanism, data structures, determinism)

### 2.1 The mask (immutable snapshot + volatile swap — lock-free, deterministic within a step)
- `RealismMask`: an **immutable** snapshot holding a `bool[] popForbidden` and `bool[] teleportForbidden`
  indexed by **edge handle** (dense, hot-path-free of strings). `MayPop(edgeHandle)` / `MayTeleport(edgeHandle)`
  return `!forbidden[h]`. An edge absent from the visible set is permissive (true).
- Engine field `private volatile RealismMask? _realismMask;` (default `null` = fully permissive).
- External setter (called from the camera/host thread, any time):
  `Engine.SetVisibleEdges(IReadOnlyCollection<string> visibleEdgeIds, bool forbidTeleport = true, bool forbidPop = true)`
  builds a NEW immutable `RealismMask` (resolving ids → handles once) and assigns it to `_realismMask`.
  Because the snapshot is fully built before the volatile assignment and never mutated after, a step that
  captures `_realismMask` once at its top reads a consistent view — no torn reads, no lock. This IS the
  "double-buffer" (front = the ref the step captured; back = whatever the setter assigns next). A step
  captures `var mask = _realismMask;` once at `Step()` top and passes it to the gated phases, so the mask
  cannot change mid-step even if the camera thread writes concurrently.
- Convenience: `SetVisibleEdges(null)` / a clear method resets to permissive (`_realismMask = null`).

### 2.2 Gate points (both already-existing pop actions)
1. **Teleport** (`CheckJamTeleports`): when selecting `_jamCandidates`, SKIP a frontmost-stuck vehicle whose
   current edge has `MayTeleport == false` (visible). The vehicle is simply held (stays jammed, keeps
   accumulating WaitingTime) — the no-cheating fallback. Off-camera candidates teleport as today. One
   `if (mask is not null && !mask.MayTeleport(edgeHandle)) continue;` in the candidate loop.
2. **On-lane spawn** (`InsertDepartingVehicles`): if a candidate's depart edge has `MayPop == false`
   (visible), do NOT insert it this step — treat exactly like a blocked lane (hold, retry next step). The
   vehicle waits until the edge leaves the visible zone (or is dropped by `max-depart-delay` if set). This
   keeps a vehicle from popping into the middle of a visible lane; it may still enter once off-camera or at
   a fringe edge that is never in the visible set.

### 2.3 Off-camera de-jam despawn action (IN SCOPE — owner-requested)
A dedicated **aggressive off-camera de-jam**: remove the frontmost jam blocker on an off-camera lane
*before* it reaches the (global) `time-to-teleport` threshold, so off-camera regions never build standing
jams and the visible area stays fed/drained. Distinct from the P1-F teleport (which fires at
`time-to-teleport` and jumps the vehicle downstream / removes it): de-jam despawn is a MORE eager,
off-camera-only, plain removal.

- **Controls are runtime Engine properties, NOT sumocfg** (X1 is a host capability, not a parity scenario —
  it must never touch the parser or add a golden surface):
  - `Engine.DejamDespawnTime { get; set; } = 0.0;` — seconds a frontmost blocker may wait on an off-camera
    lane before it is despawned. **`<= 0` disables the action entirely (default) → inert.**
  - `Engine.DejamDespawnBudgetPerStep { get; set; } = int.MaxValue;` — cap on despawns per step (the pop
    budget accounting; unlimited by default).
  - `Engine.DejamDespawnCount { get; }` — running tally (observability / tests).
- **Mechanism** — a new serial phase `DejamDespawn(time)`, called right after `CheckJamTeleports` in
  `Step()`, guarded by `if (DejamDespawnTime <= 0) return;` (so inert unless enabled):
  1. Reuse the frontmost-non-stopped-per-lane scan (the same `_jamFrontmost` machinery `CheckJamTeleports`
     builds) — or a private copy — to find each lane's frontmost blocker.
  2. For each blocker whose `WaitingTime > DejamDespawnTime` AND whose edge is off-camera
     (`mask is null || mask.MayPop(edgeHandle)`), despawn it (the existing pending/active removal idiom:
     `Inserted = Arrived = true`, generation bump), in ascending `EntityIndex` order, up to
     `DejamDespawnBudgetPerStep` this step; increment `DejamDespawnCount`.
  3. On a VISIBLE lane a blocker is never despawned (no-cheating) — it is held, exactly like the teleport
     gate. With no mask set, every edge is off-camera (permissive), so the action still requires
     `DejamDespawnTime > 0` to do anything.
- **Determinism:** serial phase (like `CheckJamTeleports`/insertion), ascending-EntityIndex order, reads
  the once-captured mask + immutable network; writes only each despawned vehicle's own fields. Inert unless
  `DejamDespawnTime > 0`, so byte-identical for every committed golden.

## 3. Determinism / parity argument (additive · inert-by-default · byte-identical)

- Default `_realismMask == null` → both gates are `mask is null || mask.MayX(...)` → **no gate ever fires**,
  so every phase is byte-identical and the whole feature is inert. No committed golden is touched (the full
  `dotnet test` suite is the gate).
- Gates are pure reads of the immutable snapshot + the vehicle's own edge handle; they only ever *suppress*
  an action (teleport/insert), never add one. Suppression is deterministic (same candidate order, just a
  skip), so the teleport-jam and insertion phases stay deterministic and thread-safe.
- The mask is set on a serial seam (captured once per step); the volatile immutable-snapshot swap is the
  standard lock-free publish already used for read-state — no new concurrency hazard in the parallel plan
  phase (which does not read the mask).

## 4. Success conditions (functional / statistical — NOT parity)

1. **Unit tests on the mask (deterministic):**
   - `MayPop`/`MayTeleport` return false for visible edges, true for others and for the null/permissive mask.
   - Teleport gate: on a small scripted jam (teleport ON, one lane jammed past `time-to-teleport`), with the
     jammed edge marked visible → **zero teleports** (`TeleportCount == 0`, vehicle held); with it off-camera
     → the teleport fires (`TeleportCount == 1`). Same scenario, only the mask differs.
   - Spawn gate: a vehicle whose depart edge is visible is NOT inserted while visible; once the edge is
     cleared from the mask it inserts. Assert via `GetLifecycle` / trajectory presence.
   - De-jam despawn: with `DejamDespawnTime` set below `time-to-teleport`, a frontmost blocker on an
     OFF-camera jammed lane is despawned (`DejamDespawnCount` increments, vehicle leaves the trajectory)
     before it would teleport; the SAME jam on a VISIBLE lane is NOT despawned (held); `DejamDespawnTime <= 0`
     despawns nothing (inert). `DejamDespawnBudgetPerStep` caps despawns per step.
2. **Inert-by-default:** full suite stays green + byte-identical (561 → 561 + new X1 unit tests); no golden
   changes. `Sim.Bench` hash unchanged.
3. **Statistical dense-run (functional test, scripted moving camera):** on a dense scenario with teleport
   ON and a camera that pans across edges over the run: (a) **zero** teleports and zero on-lane spawns occur
   on any edge while it is visible (assert by cross-checking teleport/insertion events against the
   per-step visible set); (b) the net still drains (arrived ≈ demand, comparable to the unmasked run);
   (c) as the camera pans, teleports **migrate** to the newly-hidden region (teleports on an edge only
   occur in steps where it is off-camera). "(3) visible density exceeds the global knee" is reported as a
   measured comparison (visible-zone density under the mask vs the global no-cheat density), not a hard
   assert, to avoid a brittle threshold.

## 5. Explicitly deferred

- Per-zone rerouting aggressiveness (spec X2) — out of scope for X1.
- Camera-frustum geometry (world box → visible edge set) lives in the HOST, not the engine; the engine's
  contract is the edge-id set. A test-side helper builds the set from a moving box for the statistical test.
