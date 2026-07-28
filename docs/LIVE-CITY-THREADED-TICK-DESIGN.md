# LIVE-CITY-THREADED-TICK-DESIGN.md — decouple the engine tick from the render thread

**Status: Stage 1 shipped; Stages 2 and 3 IMPLEMENTED and headlessly gated — the on-screen frame-time
result is the one outstanding item (§8.7).** Read `CLAUDE.md` first, and `LIVE-CITY-PERF-SESSION-LOG.md`
for the measurements this builds on. **§8 records what actually landed and every deviation from §5** —
read it before treating §5 as the description of the code.

## 1. The measured problem

Owner observation (timed with a metronome): a **very visible 100–200 ms hiccup ~110 times per minute**
in the live-city viewer, with smooth rendering in between. 110/min = **1.83 Hz** ≈ the **2 Hz** engine
tick (`dt = 0.5`). It is **not GC** (measured GC pause is ~0.9% of wall with zero gen2 collections).

Cause, in the viewer (`demos/City3D/Viewer/Main.cs:1740-1745`):

```csharp
_liveCityAccumulator += delta;
while (_liveCityAccumulator >= _liveCityDt) { _liveCitySource.Tick(); _liveCityAccumulator -= _liveCityDt; }
```

`Tick()` → `LiveCitySim.Step()` runs **synchronously on the Godot main thread**, so the frame is blocked
for a whole engine step. The `while` makes it worse: if a frame's `delta` covers several `dt`, several
steps run in one frame, so falling behind *compounds*.

**The hiccup magnitude needs no exotic explanation.** It was measured at **~4 000 vehicles + 8 000 peds**
(owner, 2026-07-28), where a 100–200 ms step is simply ordinary step cost — the headless bench measures
114 ms/step at 5 000 cars + 20 000 peds. So the hiccup *is* one engine step, landing on the render thread.
*(An earlier draft of this section attributed it to A21's O(ped-graph) spawn cost at ~160 cars — wrong on
both counts: I took the 160-car default from the startup log rather than asking what the sliders were set
to, and no exotic cause is needed. A21 remains a real spawn-cost bug; its share of this hiccup is
unmeasured.)*

The consequence for this design is mildly reassuring: since the hiccup is just step cost, threading moves
it off the render path in full, and **no engine optimization is a prerequisite** — engine work only shortens
how long the producer thread is busy, which affects the achievable tick rate, not render smoothness.

## 2. Why this is tractable — the seam is already right

Per frame the render side reads **published streams plus a continuously-advancing clock**, NOT live sim
objects:

- cars: `_reconstructor.Reconstruct(_liveCitySource.Source, _liveCitySource.LocalLanes, _playoutDelaySeconds)`
- peds: `_liveCityPedReconstructor.Reconstruct(_liveCitySource.PedSource, pedNow)` where
  `pedNow = _liveCitySource.Time + _liveCityAccumulator`

That `Time + accumulator` expression is already a free-running render clock, deliberately decoupled from
the tick boundary (its comment records a measured ~4-orders-of-magnitude drop in peak per-frame
acceleration when it was introduced). A **playout delay** already exists (`_playoutDelaySeconds`, default
1.0 s, with a UI slider) — precisely the jitter absorber a threaded producer needs.

So this is not a data-access refactor. It is a producer/consumer split plus the hazard fixes in §4.

## 3. Data volume — copying is NOT the problem (measured/derived)

| payload | size | at target |
|---|---|---|
| `VehicleRecord` (`Records.cs:47`) | 4 ints + 5 doubles + `UpcomingLanes` (4 ints) ≈ **72 B** | 5 000 cars ≈ **360 KB/tick** |
| ped samples (high-power only publish per step) | ≈ 40 B | 114 (mega net) … 6 134 (demo net) ≈ **≤450 KB** |

Total **≤ ~800 KB/tick**. A memcpy of that is **~30–60 µs**, about **0.05%** of a 114 ms step. The cost is
irrelevant; the requirement is only that the destination be **preallocated**. The owner's constraint — "if
that requires copying large amounts of vehicle data between threads, this needs to be done without heap
allocations" — is therefore satisfiable, and the design below also *removes* an existing per-step
allocation (`PublishFrame`'s `movers.ToArray()`).

## 4. Hazards — what is NOT thread-safe today (each must be fixed, none may be assumed away)

1. **`InMemoryReplicationBus._queue` is a plain `Queue<Entry>`** (`InMemoryReplication.cs:42`), **not** a
   `ConcurrentQueue`. Cross-thread producer/consumer would corrupt it. (I initially assumed it was
   concurrent — it is not. Verified.)
2. **`PedPublisher._events` is a plain `List<PedEvent>`** (`PedPublisher.cs:50`) appended by the tick and
   read by the render thread ⇒ race. Its elements are **reference types** allocated per ped per step, and
   the list is **never cleared** (log item A6). This is the harder half of zero-alloc.
3. **The render thread currently WRITES to the sim:**
   - `PushLcZone()` → `LiveCitySim.SetLcRealismZone(...)` (camera-driven) mutates sim state, including
     rebuilding the ORCA interest source.
   - `SampleCars()` returns a **shared reused scratch buffer** owned by `LiveCitySim`
     (`LiveCitySim.cs:1110`) — reading it from the render thread races the tick that refills it.
4. **`_source.TlStateByLane`** is a `Dictionary` read every frame while the tick mutates it.

## 5. Target architecture

**One producer thread** runs `LiveCitySim.Step()` in a loop, paced to the configured tick rate. **The
render thread never touches sim state** — it reads only published, immutable-once-published snapshots.

**Publish by triple buffering + atomic swap** (not a lock, not a queue that grows):
- three preallocated `VehicleRecord[]` (+ counts, + sim time, + step index) in a small `FrameSlot` struct;
- the tick thread fills the one slot that is neither *published* nor *held by the consumer*, then
  `Interlocked.Exchange`es it into `_published`;
- the render thread `Interlocked.Exchange`es `_published` into its own `_held`, so the producer can never
  overwrite the slot being read;
- buffers grow only when a count exceeds capacity (warmup only) ⇒ **zero steady-state allocation**.

**Render clock.** Each published slot carries its sim time. The render clock becomes
`publishedSimTime + (nowWall − publishWall)`, minus the existing playout delay, and **must never run past
the newest published sim time + one dt** — otherwise DR extrapolates beyond known state and shows
overshoot/rubber-banding. The playout-delay slider is the tuning knob for this.

**Render → sim writes become messages.** A single-slot volatile "requested LC-realism zone" (last writer
wins) applied by the tick thread at step start; the vehicle name table published with the frame instead of
sampled from the render thread.

**Determinism.** The sim stays single-threaded-per-step and its step sequence is unchanged, so results do
not depend on render timing. Camera-driven zone changes are already non-deterministic w.r.t. user input
(true today); threading only fixes *where* they are applied — at a defined step boundary, not mid-step.
No engine behaviour changes. `LIVECITY_*` gates unaffected. Parity scenarios never construct this host.

## 6. Stages, each independently shippable

### Stage 1 — INSTRUMENT + tick-rate slider (must land first)
The viewer has **no FPS or frame-time readout of any kind**, so every statement about viewer smoothness to
date (including the owner's, and mine) has been an adjective or a metronome. Before/after cannot be
compared without this.
- **1a · frame-time instrument.** On-screen HUD + `--frame-log <path>` CSV: frame ms, FPS, p50/p95/p99,
  **count of frames > 3× p50**, **sim ticks executed this frame**, live car/ped counts. The spike count is
  the headline number — it is what the owner is actually seeing.
- **1b · engine tick-rate slider, 1–20 Hz.** `LiveCitySource.Tick() => _sim.Step()` and `Step()` takes no
  dt, so `LiveCitySim` needs a **settable `Dt`** (or `Step(dt)`). Display **requested vs ACHIEVED** Hz —
  20 Hz needs a ≤50 ms step, but 5 000 cars + 20 000 peds costs ~114 ms, so the honest ceiling there is
  ~8.8 Hz; on Geneva's ~160 cars 20 Hz is easy. Never show a rate that is not being met.
- *Success:* HUD numbers move with load; the CSV shows the **current** ~2 Hz spike pattern (≈110/min,
  100–200 ms) — i.e. the instrument reproduces the owner's metronome observation; slider changes the rate
  and the achieved figure tracks it; `dotnet test -c Release tests/Sim.LiveCity.Tests` 80/80.

### Stage 2 — threaded tick + zero-alloc car handoff (this is where the stutter dies) — **IMPLEMENTED**
- Producer thread + published snapshot (§5); `movers.ToArray()` replaced by a pooled copy; hazards 1, 3, 4
  fixed; render clock from published sim time.
- *Success:* with the Stage-1 instrument, at the same scenario and counts, **frames > 3× p50 drops to ~0**
  and p99 frame time approaches p50; sim time still advances at the achieved rate; no visual regression in
  DR smoothness (specifically no reintroduction of the #7 cruise stutter or backward creep); allocation per
  frame on the render thread unchanged or lower; LiveCity 80/80 + Pedestrians 317/317.
- **Status: the code is in and everything headlessly checkable is green. The on-screen half of the success
  condition is NOT verified** — it needs a GPU, and that is now `docs/handoffs/WIN-GPU-VISUAL-TEST-terrain-
  and-ped-z.md`'s companion item. See §8 for exactly what landed and what was deviated from.

### Stage 3 — ped handoff zero-alloc — **IMPLEMENTED (scoped down, see §8.3)**
- Bound `PedPublisher._events` (log item A6); reuse the per-step batch list; make the ped bus concurrent and
  recycle its payload buffers.
- *Success:* handoff allocation per tick ≈ 0 measured by `Sim.BenchLiveCity`'s per-phase byte accounting;
  ped reconstruction visually and numerically unchanged (paired counters identical).
- **Status: measured 0 new pooled buffers over 60 steps after warmup, retained event history 0 after every
  one of 120 steps (4 151 events genuinely published, peak batch 67), and wire-vs-sim ped poses agreeing to
  0.092 m worst over 15 161 paired samples.** The struct-array/`HeadlessIg` half was deliberately NOT done
  — §8.3 says why.

## 7. Explicit non-goals

Not fixing A21 (O(graph) ped spawn) here — it shrinks the hiccup but threading is the durable fix, and
they are independent. Not changing any engine algorithm. Not touching the parity path.


---

## §8 What was actually implemented, and where it deviates from this design

Stages 2 and 3 landed together with **A22**. Gate at the time of landing: parity **775 pass / 0 fail /
4 skip**, `Sim.Bench` hash **`BF3794A4704BCD79`** (par == single), `Sim.Pedestrians.Tests` **324/324**,
`Sim.LiveCity.Tests` **90/90**, `CityLib.Tests` **187 pass / 3 fail** (the same three pre-existing
`ReconstructorS2Tests` vehicle-reconstructor failures). **No engine algorithm changed.**

### 8.1 The handoff is a LOCK, not a lock-free triple buffer — and the sketch was wrong

§5 proposed three preallocated slots with an `Interlocked.Exchange` handoff cell. That was implemented, and
then **a test killed it**, which is worth recording because the bug is invisible in the sketch:

> With three slots and one `_ready` cell, the consumer claims via `_read = Exchange(ref _ready, _read)`.
> When the consumer polls **faster** than the producer publishes — the normal case, a 60 Hz frame loop
> against a 2 Hz tick — the second claim swaps back the slot it just handed over and returns a **stale**
> one. Observed directly as `step index went backwards: 0 after 1`, and it also pinned `AchievedSimHz` at 0.
> A correct triple buffer needs a validity sentinel plus slot-ownership bookkeeping on both sides.

So the published snapshot is guarded by a plain `lock`, taken **once per rendered frame and once per step**,
always uncontended, protecting a ~100-byte copy against a ~100 ms step. This is the same call already made
for the request slots in §5, for the same reason: there is no performance here to win, and a lock cannot
have the class of bug the hand-rolled version had.

That is only defensible because the payload is small, which is §3's point and the next deviation.

### 8.2 The vehicle records were NOT triple-buffered — the bus was made thread-safe instead

§5 proposed triple-buffering the `VehicleRecord[]`. Instead:

- **`InMemoryReplicationBus._queue` → `ConcurrentQueue`** (hazard 1). The producer only ever enqueues; the
  consumer only ever dequeues; every dictionary `PumpCore` mutates (`_history`, `_tlState`, `_dims`,
  `_names`, `_geometry`) is touched exclusively on the consuming thread. **That one queue is the whole
  cross-thread surface**, which also disposes of hazard 4 (`TlStateByLane` is only written inside `PumpCore`).
- **`PublishFrame`'s `movers.ToArray()` → a pooled buffer** (`ConcurrentQueue<VehicleRecord[]>`), returned
  by `PumpCore` once consumed. Zero steady-state allocation, which is the owner's stated constraint.

This reuses the already-tested reconstruction path rather than building a second one beside it. The cost is
one hazard the design did not name: **a recycled buffer is longer than the frame and still holds the previous
frame's records in its tail**, so `Entry.MoverCount` — not `Movers.Length` — is the authority. The identical
hazard exists on the ped bus's `ActivityTimeline` branch, which slices its payload by length rather than by a
self-describing header. Both have a dedicated test that publishes a big frame then a small one.

What IS published through the lock is everything the render thread used to read by reaching **into** the sim:
sim time, step index, live car/ped counts, the crossing-signal states, the live LC zone, and the achieved
tick rate.

### 8.3 Stage 3 was scoped down: no struct arrays, no `HeadlessIg` batch path

§6 Stage 3 proposed replacing the reference-type `PedEvent` batch with preallocated struct arrays and giving
`HeadlessIg` a parallel struct-batch apply path. That was **not** done, deliberately:

- The ped bus **exists to round-trip through the real wire codecs** — that is its stated purpose, and the
  reason it is not the plain struct hand-off the vehicle bus is. Replacing the payload with struct arrays
  would delete the codec exercise the hermetic round-trip test relies on.
- A second apply path on `HeadlessIg` doubles the surface that must stay in agreement with the first, on the
  exact code the server==IG identity rests on.

The allocation was removed without either. Per tick, the ped handoff was: a fresh `List<PedEvent>`, an
append to a never-cleared history, and a fresh `byte[]` per publish. Now: a reused batch list, a history
**drained and cleared every step** (log item A6), and pooled payload buffers.

**Still open, and stated rather than hidden:** the individual `PedEvent` records are still reference types
allocated per published ped per step. Making them structs is a wide change through
`HeadlessIg`'s pattern matching and many tests, and it is a separate task — the per-step allocation it
would remove is now the only one left on this path.

### 8.4 A22 shipped with Stage 2, because Stage 2 needs it

Both parallel regions — the car plan and the high-power ORCA crowd — defaulted to TPL's every-logical-
processor. Uncapped is right for a headless bench and wrong for an interactive viewer: **a producer thread
that saturates all 24 cores starves the render thread and the display driver, so the frame hitch survives
the very change meant to remove it.** `LiveCityConfig.LeaveCoresFree` (the viewer sets 4) resolves to a
concrete cap for `Engine.MaxParallelism` and `PedLodManager.HighCrowdMaxParallelism`.

Both knobs are scheduling-only, so this must not move a single car or ped — asserted, not assumed:
**11 889 car+ped samples bitwise identical**, uncapped vs capped. `ResolveMaxParallelism()` returns `-1`
for every pre-existing caller, so a bench and the whole test suite are unaffected.

### 8.5 The render clock

`_renderSimClock` advances by real `delta` each frame (so between publishes the DR/timeline playout is as
smooth as the old `Time + accumulator` sum, which was also just wall time) and is **clamped to the newest
published sim time + one dt**. Without the clamp, a producer running below real-time would extrapolate past
computed state — rubber-banding, which is worse than stutter. A far-behind floor snaps it forward after a
real stall. The playout delay (default 1 s ≈ 2 steps at 2 Hz) normally keeps the clamp from binding at all.

### 8.6 Misuse is loud

`Tick()`, `Sample()`, `SampleCars()` and `SampleCrossingSignals()` **throw** once the producer is running.
The first would step the sim twice; the middle two hand back `LiveCitySim`'s own reused scratch buffer. Every
one of those races shows up as garbled cars/peds on screen with nothing to point at, so there is no correct
silent fallback to offer. `Published` / `CopyCrossingSignals` are the threaded reads, and both work in
non-threaded mode too so a caller needs one code path.

### 8.7 What is verified, and what is not

**Verified headlessly** (`ThreadedTickSourceTests`, `ThreadedTickHandoffTests`,
`ReplicationHandoffThreadingTests`): the producer advances without the consumer ticking; published frames are
monotonic and internally coherent (`simTime == stepIndex * dt` from one publish, which no single field could
show); zone/density/tick-rate requests are applied by the producer at a step boundary, last-writer-wins, and
are *not* applied early; both buses survive a concurrent producer/consumer over 2 000 frames with no lost
handles; pooled buffers stop being allocated after warmup; a pooled buffer longer than its payload leaks
neither stale cars nor stale timeline bytes; `Dispose` joins before disposing the sim and 5 start/stop cycles
leak no threads; the ped wire still reconstructs the sim's own poses to 0.092 m.

**NOT verified:** the frame-time result — *"frames > 3× p50 → ~0, p99 approaches p50"* — and the absence of a
DR regression. Both need a GPU and Stage 1's instrument. That is the outstanding item.
