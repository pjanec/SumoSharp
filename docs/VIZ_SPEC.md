# VIZ_SPEC.md — Offline replay & visualization tool (`Sim.Viz`)

> **STATUS: ARCHIVED (2026-07-28)** — The original design brief for the offline `Sim.Viz` replay tool. Its core interpolation model (plain linear FCD interpolation) is superseded for most gallery pages by the DR / kinematic-reconstruction pipeline in `docs/VIZ-UNIFICATION-STATUS.md` (`VizReplayBuilder`, `KinematicReconstructor`), and the tool has grown well past this spec. Kept in place (code-pinned) as the record of the original done-condition.


A self-contained briefing for building the replay tool. Read `DESIGN.md` and `CLAUDE.md`
first for project rules. This is a **route-1 visualization**: it produces a single
self-contained HTML file (data inlined) that you commit to the repo and open from a phone
browser via GitHub. No server, no inbound networking — consistent with the volatile-VM /
everything-persists-in-the-repo model.

## Purpose and scope

Play back one simulation run: render the road network (lanes, junctions, traffic lights)
and the vehicles as oriented boxes at true scale, advancing in real time from start to end,
with playback controls and Google-Maps-style zoom/pan. **Single trajectory only** — this
visualizes our engine's output (or a SUMO golden, since both are FCD). No golden-vs-engine
overlay, no per-vehicle inspector. Keep it focused.

## CRITICAL: do not trust remembered data structures

The engine's internal vehicle-state components **have changed** since earlier design notes.
Therefore:

- The viz tool consumes the **FCD XML file format** (SUMO-standard, stable), NOT the
  engine's internal C# component structs. This insulates it from ongoing `Sim.Core`
  redesign. Never reach into engine structs from the viz.
- When wiring the engine to *emit* FCD for the viz (if that path isn't already complete),
  **read the current `Sim.Core` component definitions and the current trajectory/FCD
  emission code in the repo** to see what fields exist now. Do not assume any prior field
  names (`Transform`, `Kinematics`, etc.) still apply — verify against the checkout.
- If any needed value (e.g. vehicle width) isn't in the current FCD output, extend the
  emission or join from `.rou.xml` — but confirm the present state first.

## Inputs (all committed, per scenario)

The tool reads three files from a scenario directory and inlines what it needs:

1. **`*.net.xml`** — static network geometry. Extract per lane: the `shape` polyline
   (list of x,y points) and `width` (default 3.2 m if absent); per junction: the `shape`
   polygon; per edge: which lanes belong to it and their index (for lane-marking placement);
   traffic-light data: `<tlLogic>` (phases: `duration` + `state` string, plus program
   `offset`) and `<connection>` entries carrying `tl`, `linkIndex`, `from`/`to` lane — to
   place each signal head and know which `state` character drives it.
2. **FCD file** (engine output or `golden.fcd.xml`) — per timestep, per vehicle: `id`,
   `x`, `y`, `angle`, `speed`, and the vehicle `type`/`id` needed to look up dimensions.
   Confirm the current FCD schema in the repo; SUMO FCD uses `<timestep time=...>` with
   child `<vehicle .../>` elements.
3. **`*.rou.xml`** — `<vType>` definitions for `length`, `width`, and `vClass` (→ color and
   box size). Join FCD vehicle → its type → dimensions. (If the current FCD already carries
   length/width, use that and skip the join — check first.)

## Architecture

Keep the JS/HTML front-end as a **committed static template** (`src/Sim.Viz/template.html`
+ `template.js`); the exporter injects a single `<script>const REPLAY_DATA = {...}</script>`
block and writes the result to the scenario dir (e.g. `scenarios/<name>/replay.html`).
Building HTML by string-concatenation in C# is worse to maintain — fill a template.

- **`Sim.Viz` (C# console tool):** reads net + fcd + rou (reuse `Sim.Ingest` where it
  already parses net/rou — but re-check its current API), builds a compact JSON payload
  (network geometry pre-flattened to arrays; per-vehicle a dimension record; trajectory as
  per-timestep arrays), inlines it into the template, writes `replay.html`. One command:
  `dotnet run --project src/Sim.Viz -- <scenarioDir>`.
- **Front-end:** Canvas 2D, vanilla JS, no external libs (self-contained). All rendering,
  camera, clock, and controls live here.

## Coordinate & camera model

SUMO nets can carry large offsets (UTM coords in the hundreds of thousands). Do **not** bake
offsets into geometry. Render in world coordinates and apply a camera transform
(`scale`, `translateX/Y`) to the canvas. On load, compute the network bounding box and set
the camera to fit-to-view (centered, scaled to fill with margin). All screen↔world mapping
goes through one transform so zoom/pan are pure matrix ops.

- **Zoom:** mouse wheel and two-finger pinch. Zoom is anchored at the cursor / pinch
  midpoint (the world point under the cursor stays fixed) — standard Google-Maps feel.
- **Pan:** left-drag and one-finger drag.
- Note the canvas Y axis is screen-down while SUMO Y is up — flip Y in the transform so the
  network isn't rendered upside down.

## Real-time clock (the spine — build this correctly first)

Replay advances by **wall-clock time**, decoupled from both the FCD step size and the render
frame rate:

- FCD timesteps may be 1.0 s or 0.1 s apart; the render loop runs on `requestAnimationFrame`
  (~60 fps). Between FCD steps, **interpolate** each vehicle's position and angle so motion
  is smooth. A vehicle present at step k but not k+1 (arrived/left) stops being drawn.
- Maintain `simTime`. Each frame: `simTime += realDeltaSeconds * speedMultiplier` (when
  playing). Map `simTime` to the bracketing FCD steps and interpolate.
- **Play/Pause:** pause freezes `simTime` (stop accumulating) until pressed again.
- **Restart:** set `simTime = simStart`, resume from the beginning.
- **Speed control:** multiplier (e.g. 0.25×, 0.5×, 1×, 2×, 4×, 8×); 1× is real time and the
  default.
- **Time slider:** a range input spanning `[simStart, simEnd]`. Dragging sets `simTime`
  directly (scrub/jump to any point). While dragging, treat as paused; release resumes prior
  play/pause state. Keep the slider synced to `simTime` during normal playback.

## Rendering layers (draw back-to-front)

1. **Junctions** — fill each junction `shape` polygon (dark gray), so intersections read as
   solid areas under the lanes.
2. **Lanes** — for each lane, render its centerline `shape` as a filled band of the lane's
   `width` (offset the polyline to both sides, or draw a thick stroke of width = lane width
   in world units). Mid-gray road color. This width-accurate rendering is what makes
   overtaking and lane changes visible.
3. **Lane markings** — dashed white lines between adjacent lanes of the same edge; solid
   edge boundary optional. Enough to distinguish lanes visually.
4. **Traffic lights (SUMO-native look).** For fixed-time lights, replay the `<tlLogic>`
   directly (self-contained, exact for phase-1 fixed programs): given `simTime`, the program
   `offset`, and the cumulative phase `duration`s, find the active phase and its `state`
   string. For each controlled `<connection>` (has `tl` + `linkIndex`), draw a small signal
   head at the **stop-line end of the `from` lane**, colored by `state[linkIndex]`:
   `G`/`g` green, `y`/`Y` yellow, `r` red, `u` red-yellow, `o`/`O` off. This mirrors SUMO's
   per-link signal heads at lane ends.
   - Note: actuated lights won't match a static tlLogic replay (their switch times depend on
     traffic). Phase-1 scenarios are fixed-time, so this is exact. If actuated lights arrive
     later, switch to reading actual switch times from a `--tls-state`/tls-switch output;
     leave a TODO, don't build it now.
5. **Vehicles** — for each visible vehicle, draw an oriented filled rectangle of its true
   `length` × `width` (world units), centered on its interpolated `(x, y)`, rotated by
   `angle`. SUMO's FCD `(x,y)` is typically the **front-center** of the vehicle and `angle`
   is degrees clockwise from north — **verify against the current FCD/engine convention in
   the repo** and offset the box so it sits correctly relative to the reference point. Color
   by `vClass` (car, bus, truck, motorcycle, bicycle, pedestrian…) with a small fixed
   palette; a bus/truck box is visibly larger than a car because dimensions are real.

## HUD & controls (minimal)

A fixed control bar: Play/Pause, Restart, speed selector, and the time slider with a
`simTime` readout (e.g. `t = 47.3 s / 120.0 s`). A small legend mapping vClass → color. A
live vehicle-count is a nice touch. Nothing else — no inspector.

## Suggested extras (build only if cheap; otherwise defer)

- **Color-by-speed toggle** — recolor boxes on a speed heatmap to spot shockwaves/stop-and-go.
  High value for traffic analysis, low cost. Worth including.
- **Fading trajectory trail** behind each vehicle — helps read lane-change/overtake paths.
  Defer unless quick.
- **Keyboard shortcuts** (space = play/pause, R = restart, arrows = step) — cheap, convenient.

## Done-condition

`dotnet run --project src/Sim.Viz -- scenarios/01-single-free-flow` produces a committed
`scenarios/01-single-free-flow/replay.html` that: opens standalone in a mobile browser from
GitHub; renders the network with width-accurate lanes and junctions; plays the single vehicle
as a correctly-oriented, true-size box in real time from start to end; supports
play/pause/restart, speed change, and slider scrubbing; and zooms (cursor-anchored) and pans
by touch and mouse. Commit the exporter, the template, and the generated `replay.html`.

## Placement in the queue

This is an independent tool — it depends only on FCD + net + rou, all of which exist once an
engine run produces trajectories. Natural slot: **after Task 3** (first engine trajectory),
so there's something moving to watch. It does not block the parity ladder and can be built in
parallel with later rungs by a separate Sonnet session if desired. It is not on the
`dotnet test` parity path; treat it as a utility, not a parity gate.
