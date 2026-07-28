# LIVE-CITY-THREADED-TICK-DESIGN.md — decouple the engine tick from the render thread

**Status: design agreed with the owner, staged for implementation.** Read `CLAUDE.md` first, and
`LIVE-CITY-PERF-SESSION-LOG.md` for the measurements this builds on.

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

### Stage 2 — threaded tick + zero-alloc car handoff (this is where the stutter dies)
- Producer thread + triple-buffered publish (§5); replace `movers.ToArray()` with a copy into the spare
  buffer; fix hazards 1, 3, 4; render clock from published sim time.
- *Success:* with the Stage-1 instrument, at the same scenario and counts, **frames > 3× p50 drops to ~0**
  and p99 frame time approaches p50; sim time still advances at the achieved rate; no visual regression in
  DR smoothness (specifically no reintroduction of the #7 cruise stutter or backward creep); allocation per
  frame on the render thread unchanged or lower; LiveCity 80/80 + Pedestrians 317/317.

### Stage 3 — ped handoff zero-alloc
- Replace the per-ped-per-step reference-type `PedEvent` batch with preallocated struct arrays
  (id/pos/vel/anim/time + count), double-buffered; give `HeadlessIg` a struct-batch apply path alongside
  the existing event path; bound `PedPublisher._events` (log item A6).
- *Success:* handoff allocation per tick ≈ 0 measured by `Sim.BenchLiveCity`'s per-phase byte accounting;
  ped reconstruction visually and numerically unchanged (paired counters identical).

## 7. Explicit non-goals

Not fixing A21 (O(graph) ped spawn) here — it shrinks the hiccup but threading is the durable fix, and
they are independent. Not changing any engine algorithm. Not touching the parity path.
