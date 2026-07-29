# MEASURE-WRITE-RATE-RESULTS.md — the irreducible vehicle write rate

**Question.** On a real city cut under LiveCity demand, how many vehicle updates does the *decimated*
replication stream actually need per second, why, and what real-time factor does the producer sustain?

**Answer, in one line.** **~0.64 fires/car/s, flat from 500 to 4000 cars**, costing **125 KiB/s at 4000
cars** — a light stream. About **half** of it is cars crossing a lane boundary while driving normally,
which no threshold can remove; **real lateral lane changes are 0.7%**.

Instrument: `src/Sim.MeasureWriteRate` (committed, so these numbers are reproducible and falsifiable).
Reproduce with `--dataset` or `--sumocfg`, `--cars N`, `--steps N`.

## Provenance and honest limits

- **Dataset:** a Geneva city cut — **22 688 edges / 28 276 lanes / 6 593 junctions / 29 `tlLogic`**. It is
  company-restricted and is **not in this repo**; nothing here references it by path, and it must not become
  persistent test data. Anything committed uses the in-repo fixtures instead.
- **Demand:** `LiveCitySim`'s own procedural car demand. **Pedestrians off** (`--peds 0`) — this study is
  the vehicle stream only.
- **Sim rate:** `dt = 0.5 s` (2 Hz), printed by the tool rather than assumed.
- **Policy:** `DrErrorPublishPolicy` with the shipped tolerances — `PosTol = 0.3 m`, `LatTol = 0.2 m`,
  `MaxInterval = 3.0 s`. The heartbeat alone therefore floors the rate at **0.333 fires/car/s**.
- **Measured on this Linux cloud box, not the target Windows box.** The rate figures are properties of the
  scenario and transfer; the **real-time factor does not** — treat RTF as an ordering, not an absolute.
- ⚠ **The car cap is closed-loop.** `CarTargetConcurrent` inserts only while live < cap, so these runs say
  nothing about capacity or discharge. That is fine here — this measures *write rate at a given occupancy*,
  which is exactly what a cap gives you — but do not read a throughput claim into it.

## The sweep

Each row: 360 measured steps (180 s sim) after the target was reached, plus one full `MaxInterval`
discarded so the first-sighting burst is not counted.

| target | achieved cars | fires/s | **fires/car/s** | mean gap | p95 gap | bytes/s | **RTF** |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 500 | 500 | 316.1 | **0.632** | 1.580 s | 3.000 s | 14.8 KiB/s | 40.2× |
| 1000 | 1000 | 633.4 | **0.633** | 1.579 s | 3.000 s | 29.7 KiB/s | 26.2× |
| 2000 | 2002 | 1 279.0 | **0.639** | 1.565 s | 3.000 s | 60.0 KiB/s | 12.1× |
| 4000 | 4032 | 2 663.2 | **0.660** | 1.513 s | 3.000 s | 124.8 KiB/s | 5.1× |

Framing adds almost nothing: one 16-byte header per non-empty frame, so 124.8 → 124.9 KiB/s at 4000 cars.

**One-time lane geometry: 2 998 640 B (2.86 MiB) for 28 276 lanes.** Sent once, durably, not per step.

### What the shape says

- **fires/car/s is flat** — 0.632 → 0.660 across an 8× increase in cars. The rate is a per-car property of
  how a car moves on this net, not something that compounds with density. Total fires/s is therefore
  linear in car count and predictable.
- **p95 gap is exactly 3.000 s** at every density — i.e. exactly `MaxInterval`. The heartbeat is doing its
  job as a ceiling on staleness, and nothing is starved.
- **Mean gap ~1.55 s** against a 3.0 s heartbeat: a typical car is sent about twice per heartbeat window.
- **Bytes are irrelevant.** 125 KiB/s at 4000 cars. Transport bandwidth is not, and will not become, the
  constraint — this is a per-message/per-object *count* problem or nothing.
- **RTF stays above 1.0** (5.1× at 4000 cars), so the producer keeps up here with peds off. It falls ~8×
  across the sweep, which is the number any consumer-side clock has to lag behind `simT`.

## Per-reason attribution — and the one that surprises people

| reason | 500 | 1000 | 2000 | 4000 |
| --- | ---: | ---: | ---: | ---: |
| `laneChange` | 50.8% | 50.2% | 48.6% | 43.9% |
| `posError` | 23.2% | 23.9% | 25.8% | 31.7% |
| `latError` | 0.0% | 0.0% | 0.0% | 0.0% |
| `heartbeat` | 26.0% | 25.9% | 25.6% | 24.4% |

Cross-checked every run: the policy's own tally equals the sink's record count, so the split accounts for
every emitted record.

`latError` is 0.0% throughout because these runs are discrete-lane (no sublane, no crowd), so the lateral
prediction error is identically zero. `LatTol` is inert here — do not tune it hoping for an effect.

### `laneChange` is NOT cars changing lanes

This is the finding worth internalising. The policy's signal is

```csharp
laneChanged = laneHandle != lastPublishedLane;   // PublishScheduler.cs
```

— **any** change of lane *identity*. A car driving straight onto the next street changes lane identity. A
car crossing an intersection changes it twice: onto the junction's internal lane, then off it. So on a real
urban net most `laneChange` fires are **route progression**, not manoeuvres.

Measured directly, by comparing the two lanes' **edge** ids (2000 cars):

| within `laneChange` (49.6% of all fires) | share of all fires |
| --- | ---: |
| **SAME-EDGE** — both lanes on one edge ⇒ a *real* lateral lane change | **0.7%** |
| **NEW-EDGE** — drove onto the next lane of the route | **48.9%** |
| └ of which **INTERNAL** — entered or left a junction lane | 24.9% |

**Real lateral lane changes are 0.7% of the write rate.** Half the stream is cars driving forward.

Why it is that high on this cut is a property of the *net*, not the traffic: **15 291 of the 28 276 lanes
(54%) are internal junction lanes**, and the **median lane is 13.8 m** long (mean 53.0 m, p10 1.5 m). A car
crossing one ordinary intersection burns several lane identities in a few seconds. The in-repo demo box, with
larger blocks, shows the same mechanism far weaker — `laneChange` 12.7%, of which SAME-EDGE 2.7% — and is
heartbeat-dominated (49.7%) instead.

**And it is irreducible.** The wire carries an arc-position *along a specific lane*, so when the lane
changes the previous `(pos, speed, accel)` is not a valid prediction basis — `PublishScheduler` says so in
place ("arc-pos is only comparable within the same lane; a lane change publishes anyway"). Suppressing that
publish would leave the receiver extrapolating along a lane the car has left.

## What this means for the decision

Reading against the brief's §5 criteria:

- **The stream is light.** 0.64 fires/car/s is just above the 0.33–0.6 "genuinely light" band and nowhere
  near the >1–2 Hz "thresholds too tight" band. A count that looked alarming per-frame was a ~1 s frame:
  ~2000 per frame is ~2000/s, which at 4000 cars is exactly this measurement. **The write count is not the
  wall** — so the wall is the per-object apply plus polyline build, which a consumer-side port removes.
  That supports proceeding with the port.
- **Threshold tuning cannot buy much, and cannot buy half of it.** Bounding what each lever can reach:
  - `PosTol` touches only the `posError` share — 23% at 500 cars, 32% at 4000.
  - `MaxInterval` touches only `heartbeat` — ~25%, and raising it trades liveliness latency linearly.
  - `LatTol` touches **nothing** here (0.0%).
  - The remaining ~44–51% is lane-identity change, of which only 0.7% is a manoeuvre. **No threshold
    reaches it.**

  So even aggressive tuning leaves roughly half the rate untouched. Tune thresholds for their own sake if
  you want, but it is not a substitute for the port and cannot be sold as one.
- **Bandwidth is a non-issue** and should be dropped from the argument entirely.
- **The producer sustains 5.1× real time at 4000 cars** on this box with peds off. Any consumer clock must
  lag `simT`; this sweep gives the ordering, but re-measure on the target box with peds on before sizing it.

**If you did want the rate lower**, the only lever that touches the dominant term is the **net**, not the
policy: coarser lane granularity (fewer, longer edges; fewer internal lanes) directly reduces lane-identity
changes per second. That is a preprocessing decision about the cut, with its own costs, and is a different
conversation from publish tolerances.

## Reproducing

```bash
# in-repo sanity check first -- validates the harness before any restricted data is involved
dotnet run -c Release --project src/Sim.MeasureWriteRate -- \
  --dataset scenarios/_ped/demo_city/box --cars 100 --peds 0 --steps 120

# a real cut (path supplied on the command line; never committed)
dotnet run -c Release --project src/Sim.MeasureWriteRate -- \
  --sumocfg /path/to/city.sumocfg --cars 2000 --peds 0 --steps 360 --warmup 4000 --csv out.csv
```

A `.sumocfg` written on Windows needs its `<net-file>` made relative before it resolves on Linux;
`ScenarioConfigParser` resolves it against the sumocfg's own directory. `NetworkParser` reads **plain XML
only** — no gzip.

## One correction worth recording

The first attempt at the manoeuvre-vs-progression split used `PublishSignals.LaneChangingOrManoeuvring` and
reported **0.0% manoeuvres**, which measured *nothing*: that flag tracks **lateral steering** — sublane
coupling, overtake spill, give-way drift, crowd swerve — every one of which is structurally impossible in a
discrete-lane run with no pedestrians and no `lcOpposite` vType. An ordinary LC2013 lane change does not set
it either. A "0%" that is 0 by construction is exactly the vacuous check `CLAUDE.md` warns about, and it
would have supported the same conclusion for the wrong reason. Comparing edge ids is the test that
discriminates; the 0.7% above is that test.
