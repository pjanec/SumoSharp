# Dead-reckoning-error-based publishing — design

**Status:** design (design-first; no code until agreed). **Scope:** the publish side —
`Sim.Replication` (`PublishScheduler` / `PublishPolicy`) + `Sim.Viewer.Core/DdsPublisher`, plus a
small shared-extrapolation extraction. **Builds on:** `SUMOSHARP-DEADRECKONING.md` §7 (the adaptive
scheduler) and `SUMOSHARP-VIEWER-DR-SMOOTHING.md` §5 (the viewer's DR pipeline). **Supersedes** the
delay/extrapolation dimension of `SUMOSHARP-DR-MOTION-JITTER-INVESTIGATION.md` (this is the root-cause
fix that investigation pointed to). **Parity:** publish-side only — zero engine/golden impact.

---

## 1. Problem (data-confirmed)

The publisher's scheduler is **interval + accel-threshold** based (`DefaultPublishPolicy`, as used by
`DdsPublisher`: `FastInterval=1 s`, `SlowInterval=3 s`, `AccelThreshold=0.3 m/s²`, sim ticks at 1 Hz):
a vehicle with `|accel| < 0.3` and not manoeuvring is "steady" and re-sent only every **3 s**.

But "steady by accel threshold" ≠ "the viewer's prediction is still accurate." The viewer dead-reckons
between packets with a **constant-acceleration** arc-length extrapolation (`DrClock.ExtrapolateArc`:
`pos + v·dt + ½·a·dt²`). When a vehicle's real acceleration *changes* during a 3 s gap — e.g. a car
settling from acceleration to its free-flow cap — the constant-accel prediction runs ahead and
**overshoots**. Measured on `12-overtake` (`follow`, straight, lane 2, "constant" 13.9 m/s): during a
3 s gap the resolved position advanced at **16.4 m/s** (the extrapolation was still applying the last
packet's `+~1.5 m/s²`), reaching **~2 m ahead**; the next packet snapped it back 2 m. The viewer's
error-smoothing can only convert that 2 m correction into a **visible slowdown** (it cannot reverse) —
symptom, not cause. At `delay < gap` (any real-time delay), this is unavoidable with interval-based
publishing.

**Constraints (user, hard):** playout delay must stay **< 1 s** (real-time SUMO integration); **no
blanket bandwidth increase** (10k+ vehicles, limited network). So we can neither raise the delay to
`≥ gap` (interpolate) nor shrink the interval globally.

## 2. Idea — publish on prediction error, not on a timer

Publish a vehicle exactly when the receiver's dead-reckoning would be **wrong**, and not otherwise.
The publisher runs the **same** extrapolation the viewer runs, from the **last state it published**,
forward to now; if the true current state has diverged from that prediction by more than a small
tolerance, it publishes (and re-bases the prediction to the new true state). This is the standard
DR/HLA-DIS/netcode "dead-reckoning threshold" technique.

Consequences, matching all three constraints:
- **Genuinely steady vehicle** (constant velocity): prediction stays exact → **no packets** → 10k
  throughput preserved (better than the current 3 s keep-alive, which sends anyway).
- **Any vehicle whose motion diverges** (accel settling, braking, lane change): published on the first
  step its prediction error exceeds tolerance → the viewer's extrapolation is never more than
  ~tolerance off → **smooth at any low delay**, no 2 m overshoot, no slowdown.
- Latency: unchanged and low — this doesn't touch the viewer's delay at all.

## 3. Mechanism

### 3.1 Per-vehicle published reference (new scheduler state)
`PublishScheduler` currently stores only each vehicle's **last-sent time**. Extend it to store the
**last-published DR state** used as the prediction base: `{ pos, speed, accel, posLat, latSpeed,
laneHandle, time }` (the §4.2 record fields the viewer predicts from). Memory: O(live vehicles), same
lifecycle/prune as today.

### 3.2 The DR-error policy (replaces the interval/threshold predicate)
Each step, per candidate vehicle, given its **current** true state and its stored **published**
reference:
1. `dt = now − ref.time`.
2. Predict exactly as the viewer would: `predPos = ExtrapolateArc(ref.pos, ref.speed, ref.accel, dt)`
   (the SAME clamped constant-accel arc math — see §3.3), `predLat = ref.posLat + ref.latSpeed·dt`.
3. **Publish if any:**
   - `laneHandle != ref.laneHandle` (lane change / edge advance — the prediction frame changed), OR
   - `|curPos − predPos| > posTol` (longitudinal error; the dominant case), OR
   - `|curPosLat − predLat| > latTol` (lateral error), OR
   - `now − ref.time ≥ maxInterval` (heartbeat / liveliness — see §3.4), OR
   - first sighting.
4. On publish: send the record and set `ref = current state, time = now`. Otherwise: send nothing,
   keep the old `ref`.
- **Min interval** is inherent (decision runs once per sim step = the 1 Hz granularity; a vehicle can
  publish at most every step). So the finest correctable drift is one step (~1 s) of accel change —
  bounding the residual overshoot to roughly `posTol + one-step drift` (≈ sub-metre), which the
  viewer's error-smoothing then absorbs invisibly. This is the best achievable without finer sim
  steps, and far better than the current up-to-3-s accumulation.

### 3.3 Shared extrapolation (publisher prediction MUST equal viewer prediction)
The overshoot is defined by the *viewer's* extrapolation, so the publisher must predict with the
identical function, or it will publish at the wrong moments. `ExtrapolateArc` currently lives in
`DrClock` (`Sim.Viewer.Core`); `PublishScheduler` is in the lower `Sim.Replication`. Extract the
arc-length extrapolation into a shared static (candidate home: `Sim.Replication`, which both the policy
and — via a thin call — `DrClock` can use; `DrClock` keeps its render-clock logic and calls the shared
math). One source of truth for the DR curve. (Lateral/lane pose reconstruction stays viewer-side; the
publisher only needs the *arc-pos + posLat* prediction for the error metric, not the full world pose.)

### 3.4 Heartbeat & late-join
A perfectly-steady vehicle would never re-publish. Late joiners are already covered by the topic's
`TRANSIENT_LOCAL` durability (the last sample per instance is delivered on join). Keep a **large**
`maxInterval` heartbeat (e.g. 3–5 s) purely as a liveliness/robustness backstop, not as the primary
cadence. This is strictly ≤ today's traffic, so it cannot *increase* steady-state bandwidth.

### 3.5 Bandwidth governor (throughput safety)
`IPublishPolicy` is deliberately swappable. DR-error publishing is naturally throughput-friendly
(steady → silent), but a churny 10k scene could still spike. Design allows an optional cap layer: rank
this step's would-publish vehicles by prediction error (worst first) and cap the count to a per-step
budget, deferring the rest (they publish next step, error only grows so ordering self-prioritises).
Include this as a tunable but default it off; decide from the §5 measurement.

## 4. Determinism & parity

Publish-side only. **No engine, golden, `tolerance.json`, or wire-format change** (same
`VehicleRecord`; only *when* it's sent changes). The offline `dotnet test` loop runs the engine against
committed goldens and **does not publish**, so it is unaffected and still needs no SUMO. Decision is a
pure function of (published ref, current state) — independent of thread/arrival order. **Action:**
check `Sim.Replication` tests for any that assert the interval/threshold behaviour and update them to
the DR-error policy (the scheduler's bookkeeping/prune tests should be behaviour-preserving).

## 5. Verification (numeric)

- **Smoothness (the point):** re-run the `--trace-veh follow` capture on `12-overtake` at a low delay
  (e.g. 0.5–1.0 s). The resolved-position reconciliation at the pass must drop from ~2 m to **≤ posTol
  (~0.3 m)**; rendered speed must no longer dip below ~90 % of true at the pass (no visible slowdown);
  and interactive sign-off.
- **Throughput (the constraint):** on the 10k perf scenario, measure **published records/second**
  under DR-error vs the current interval policy. Bar: **≤** the current policy's rate (expected far
  lower for mostly-steady traffic). Tune `posTol` / governor to hold the budget; log what the governor
  drops (no silent capping).
- **Correctness:** a genuinely constant-velocity vehicle publishes only on the heartbeat; a
  lane-changing / braking vehicle publishes within one step of the divergence; no vehicle goes
  unpublished past `maxInterval`.

## 6. Tunables

| Param | Meaning | Start value |
|---|---|---|
| `posTol` | longitudinal prediction error that triggers a publish | 0.3 m |
| `latTol` | lateral (posLat) prediction error trigger | 0.2 m |
| `maxInterval` | heartbeat / liveliness backstop | 3 s (≤ today) |
| `minInterval` | floor between publishes | 1 step (inherent) |
| governor budget | optional per-step publish cap for 10k | off by default |

## 7. Open decisions (for agreement before coding)

1. **Home of the shared `ExtrapolateArc`** — `Sim.Replication` (proposed) vs `Sim.Core`. Prefer
   `Sim.Replication` (it's DR-transport math, and `Sim.Core` is the parity engine we avoid touching).
2. **Keep `DefaultPublishPolicy`?** Add `DrErrorPublishPolicy` as a new `IPublishPolicy` and switch
   `DdsPublisher` to it (keep the old one for comparison/tests), vs replace outright.
3. **Governor now or later** — implement the cap in this pass, or ship DR-error first and add the
   governor only if the 10k measurement needs it (proposed: measure first).
4. **Tolerances** — 0.3 m / 0.2 m are starting points; final from the §5 smoothness-vs-throughput
   sweep.
