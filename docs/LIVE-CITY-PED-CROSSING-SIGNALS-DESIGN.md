# Pedestrian crosswalk TL indicators (City3D viewer) — design + tasks

> **Owner request:** the live-city 3D viewer shows car traffic-light heads but nothing for **pedestrian
> crosswalks**. Add a small **pole-mounted signal head at each TL-controlled crossing** (≈half the car
> head's height, smaller box), red/green by the crossing's live TL state — reusing the existing car
> signal-head render path. (Option "B" of two considered; tinting the zebra red/green was "A" — rejected
> as more mesh restructuring for a less natural look. See the chat rationale.)

## Why it isn't free today
Car heads are driven by `IReplicationSource.TlStateByLane` (lane handle → signal byte). That map is built
from `Engine.BuildTlControlledLanes`, which **skips internal lanes** (`if (lane.Id.StartsWith(':'))
continue;`) — and SUMO crossings ARE internal lanes (`:<node>_c<idx>_0`). So crossing TL state never
reaches the viewer. The engine DOES have it: `Engine.TryGetTlLinkState(tlId, linkIndex)` "reaches ... the
crossing links" (Engine.cs § mid-2100s). We just need to enumerate the controlled crossings and project
their state — read-only, parity-inert (empty for any net without controlled crossings ⇒ every golden
unchanged).

## Enumeration mechanism (parity-critical half)
For each internal **crossing** lane (`CrosswalkBuilder.IsCrossingLaneId(lane.Id)` — `^:.+_c\d+(_\d+)?$`):
- `NetworkModel.LinkIndexByInternalLane[lane.Id]` → `(Junction junction, int linkIndex)`.
- The controlling TL id: `NetworkModel.EntryConnectionByLink[(junction.Id, linkIndex)].Tl` (the entry
  connection carries the live `tl`/`linkIndex`/`state`). Keep only crossings where `Tl` is set AND
  `TlLogicsById.ContainsKey(tl)` (TL-controlled; uncontrolled/priority crossings get no head).
- Store `(int LaneHandle, string TlId, int LinkIndex)` once at load (mirror `_tlControlledLanes` →
  add `_tlControlledCrossings`). **Verify empirically on the demo net** (`scenarios/_ped/demo_city/box`)
  that this yields a non-empty set with valid `(tl,linkIndex)` and live states in `{r,y,g,G,...}` — the
  mechanism above is the intended path but MUST be confirmed by a probe/test before it's trusted.
- Per-call state: `TlLinkStateChar(tlId, linkIndex, CurrentTime)` (same call the car path uses).

**Determinism/parity:** a pure read over state `Step` already produced; no Step mutation; `_tlControlled
Crossings` empty for nets without controlled crossings ⇒ `Sim.ParityTests` 657/4 **byte-identical**,
bench hash unchanged.

## API contract (the seam between the two halves)
- **Engine:** `IReadOnlyList<(int LaneHandle, char State)> SampleControlledCrossingSignals()` — enumerated
  once, state projected per call (or fill a reused buffer). `LaneHandle` indexes `Network.LanesByHandle`
  for geometry.
- **`Sim.LiveCity.LiveCitySim`:** passthrough `SampleCrossingSignals()` → the engine list.
- **`CityLib.LiveCitySource`:** passthrough `SampleCrossingSignals()` (+ `Network` already exposed).

## Viewer half (rendering — reuses the car head path)
- New `CityLib.PedSignalPlacer` (or a `TrafficLightPlacer.PlaceCrossing` overload): for each crossing
  `LaneHandle`, place a **mini** `SignalHead` at the crossing lane's stop-line end (`Shape[^1]`, same
  convention as the car placer), with **half** `HeadHeightMeters` and a smaller head box.
- `Main.cs`: build the mini heads once (like `BuildTrafficLights`), then each frame set each head's
  emissive colour from `SampleCrossingSignals()` using the SAME state→colour mapping the car heads use
  (`UpdateTrafficLights`): red for `r`, green for `g`/`G`, amber for `y`. Local live path first (the
  crossing state comes straight off `LiveCitySource`); remote/replay can follow later via the wire.

## Tasks & success conditions
- **T1 (parity half — delegated):** engine enumeration + `SampleControlledCrossingSignals()` +
  LiveCitySim/LiveCitySource passthrough. **Success:** `Sim.ParityTests` **657/4 byte-identical**;
  bench hash unchanged; a new `Sim.LiveCity.Tests` fact asserts the demo net enumerates ≥1 controlled
  crossing and their states are valid TL chars that CHANGE over a multi-minute run (not all constant).
- **T2 (viewer half):** placer + mini heads + per-frame colour. **Success:** in the local 3D viewer the
  crossings show red/green heads that flip with the signal; heads are visibly smaller/lower than the car
  heads; no per-frame GC spike at 1000 peds / 300 cars.
