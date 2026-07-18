# HIGH-DENSITY-P2G2-COOPERATIVE-LC-DESIGN.md — coordinated dense lane-change model (config-gated)

**Status:** ANALYSIS + PLAN (owner decision 2026-07-17: implement, but **behind a configuration gate**
so it can be toggled on/off when tuning performance). Item **P2G-2**. This is the coordinated-dense-LC
model that unblocks the faithful multi-lane lane-change work (P2G-3 and the P2-G follower veto) without
gridlocking saturated traffic. Grounds in `sumo/src/microsim/lcmodels/MSLCM_LC2013.cpp`
(`informFollower`/`informLeader`/`amBlockingFollowerPlusNB`/`saveBlockerLength`, the `myVSafes` channel).

## 1. The analysis — why this exists, what it buys, what it costs

### Is the coordinated LC model required for FLOW? NO.
The engine **already flows dense multi-lane traffic without it** — the saturated `-L2` grid drains at
**0 stuck** on the current default; the 2-lane grid ran 74/75 arrived, 0 gridlock. The engine sits in a
self-consistent regime: **modest lane-changing + no coordination → flows**. SUMO sits in a different
stable regime: **aggressive lane-changing + coordination → flows**. The gridlocks this project hit
(follower-veto 0→30 stuck, keep-right continuation 0→90, P2G-3 cross-junction speedGain 0→51 — all in
`docs/HIGH-DENSITY-P2G3-DESIGN.md` §5.3) came from a THIRD, unstable regime: **aggressive lane-changing
+ NO coordination → thrash → jam**. Cooperative LC is the coordination that lets the faithful aggressive
lane-changing flow. **So it is required for PARITY (SUMO-faithful lane distribution), not for flow.**

### What we GAIN
- **SUMO-faithful multi-lane overtaking / lane distribution** — use-the-fast-lane, merge back right,
  cooperative merging. Closes scenario 46's residual and makes multi-lane trajectories track SUMO. This
  is **fidelity/realism**, most valuable for the **on-camera hero area** (X1) where the visible traffic
  should look exactly like SUMO.
- **Real capacity gain, but topology-specific:** cooperative gap-creation genuinely raises the jam
  threshold at **merges / on-ramps / lane-drops** (prevents merge-bottleneck breakdown). Real dense
  SumoData sub-areas have these. For **grid-like nets it buys little** — the engine already flows those.
- It is **NOT** a general "higher density without jam" lever — the density levers are already built
  (rerouting P1-E, teleport valve P1-F, X1 off-camera popping). Cooperative LC does not raise the
  *global* knee for grid traffic.

### What we LOSE
- **Performance:** a moderate per-step cost in the lane-change phase — the cooperative decision
  (`amBlockingFollowerPlusNB`) + a **cross-vehicle speed-advice channel** (`informFollower` → a
  per-vehicle imposed-speed field the car-following reads next step). **Inert on single-lane**, active
  only in multi-lane, fits the existing command-buffer/deferred-write pattern → gateable + localized,
  likely single-digit-% overall. **This is the reason for the config gate** (§2): turn it off to reclaim
  the perf when the faithful lane distribution is not needed.
- **Complexity + regression risk:** the big cost. Deep RoW/LC-core work with cross-vehicle coupling; the
  dense path is violently sensitive (three 0→30/90/51-stuck regressions this project). Multi-session
  effort with a high adversarial-testing bar.

### Product read
For **"a dense sub-area that flows and pops invisibly off-camera"** the engine ALREADY does that —
cooperative LC adds nothing needed. For **"on-camera multi-lane traffic that overtakes/merges exactly
like SUMO"** cooperative LC is the way, bought with perf + careful core work. It is a
**realism/fidelity investment for the visible hero area**, not a capacity enabler.

## 2. The config gate (owner requirement)

A single master switch, default **OFF** = today's byte-identical flowing behaviour; **ON** = the
coordinated dense LC model (cooperative changes + speed-advice channel) AND the currently-blocked
faithful pieces it unblocks (P2G-3 cross-junction speedGain, the P2-G follower-veto half).

- **Runtime Engine property** (like X1's controls — a non-parity behavioural mode, not a sumocfg parity
  key): e.g. `Engine.CoordinatedLaneChange { get; set; } = false;`. Default off keeps every committed
  golden byte-identical AND the saturated-grid diagnostic at 0 stuck.
- Owner steer #1 alignment: parity-faithful-but-slower is the OPT-IN here (inverse of the usual
  fast-mode flag, because in this engine the faithful path is the more expensive one). Document clearly.
- When ON, the parity target is behavioural/statistical on dense nets (not bit-exact — the coordinated
  model is where SUMO's exact per-vehicle draw order would otherwise lock us in); keep a moderate-density
  anchor (e.g. scenario 46) bit-exact under the flag as the faithful anchor.

## 3. Scope when built (design outline — not yet implemented)

1. **Speed-advice channel (`myVSafes` analog):** a per-vehicle deferred "imposed speed cap" written by
   one vehicle's LC decision and consumed by another's car-following NEXT step. Determinism: order-
   independent (MIN of all advice), published via the command-buffer pattern → parallel-safe.
2. **`informFollower`/`informLeader`:** a changing/blocked vehicle asks its target-lane follower to slow
   (writes advice) / adjusts for the leader (`MSLCM_LC2013.cpp:430-740`).
3. **Cooperative change (`amBlockingFollowerPlusNB`, `:1639-1659`):** a vehicle moves over to make room
   for a blocked neighbour (LCA_COOPERATIVE).
4. **Unblock the gated pieces under the flag:** P2G-3 cross-junction speedGain (the
   `TryFindContinuationLeader` change, `docs/HIGH-DENSITY-P2G3-DESIGN.md` §5.2) and the P2-G follower-veto
   half — both currently reverted because they gridlock saturation WITHOUT this coordination.
5. **Gate — adversarial:** the saturated-grid diagnostic must stay at 0 stuck with the flag ON (the
   whole point), and byte-identical with it OFF. Suggested spike: build the `informFollower` speed-advice
   half FIRST and re-run the reverted P2G-3 change under it to measure whether it clears the 0→51 gate and
   what it costs — cheapest signal on feasibility + perf.

## 4. Tracked as
`docs/HIGH-DENSITY-PLAN.md` P2G-2 (config-gated). Depends on nothing new; unblocks P2G-3.
