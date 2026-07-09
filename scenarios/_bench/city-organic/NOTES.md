# city-organic — organic road-graph demo scenario notes

Demo/benchmark tooling for the Sim.Viz replay tool. **Not** a parity test -- not wired into
`dotnet test`. See `provenance.txt` for exact commands, seed, and file hashes.

## What's in this network

Generated via `netgenerate --rand` (45 iterations) into an irregular street layout, then
hand-spliced with one single-lane priority roundabout. Final network:

- 49 real junctions: 27 organic priority junctions, 9 dead-ends (network fringe), 9
  traffic-light junctions (`--tls.guess --tls.guess.threshold 50`), plus 4 new priority
  junctions forming the roundabout ring.
- 118 real edges, single lane each (`--default.lanenumber 1`, see "Engine capability note"
  below for why).
- Irregular junction angles/spacing, varied block shapes -- looks like a small town, not a
  grid (no `--grid` option was used).
- bbox ~1525m x 1304m.

Both traffic lights **and** the roundabout are included per an explicit correction from the
task's coordinator mid-session: the engine supports both (TLS is rung 10 / C6; single-lane
priority roundabouts are parity-anchored at `scenarios/32-roundabout` and
`scenarios/33-roundabout-solo`, exercising C4-iii's junction arrival-time right-of-way), so
they should be included rather than avoided.

## The roundabout: why it's hand-built, not `--roundabouts.guess`

`netgenerate --rand`'s randomized expansion is essentially tree-like (each iteration adds one
new edge/node); it does not organically produce the short closed cycles that
`netgenerate`/`netconvert`'s `--roundabouts.guess` looks for. Verified directly: 10 different
seeds (1,2,3,5,7,11,13,17,42,99) x `--rand.connectivity 0.6` (higher than default, to encourage
loops) x `--roundabouts.guess` all produced **zero** `<roundabout>` elements. A synthetic 4-node
square ring (90 degree corners) also wasn't detected by the guesser; only after switching to an
8-node octagon ring (smoother curve) did `--roundabouts.guess` recognize it -- but by then it was
easier to just hand-build the roundabout directly with the *exact* topology already proven to
work in the engine, rather than reverse-engineer the guesser's detection heuristic.

**Construction** (`gen-splice-roundabout.py`, committed alongside this file):
1. Export the base organic net to plain XML (`netconvert -s net_base.net.xml -p organic
   --plain-output.lanes`).
2. Parse `organic.nod.xml`/`organic.edg.xml`, find the longest interior bidirectional edge pair
   X<->Y with length in [90m, 220m] (skip anything touching a `dead_end` node). This run picked
   nodes `366`<->`226` (edges `-367`/`367`), length 217.6m.
3. Delete that edge pair. Add a 4-node ring (radius 20m, same as scenarios/32-roundabout) at
   the segment's midpoint:
   - `rbX_S` (X-facing side): hosts **both** the entry from X and the exit to X.
   - `rbX_N` (Y-facing side): hosts **both** the entry from Y and the exit to Y.
   - `rbX_E`, `rbX_W`: pure ring vertices (no spokes), just for the ring's round shape.
   - Ring edges priority=10, single lane, 8.33 m/s; spoke edges priority=1, single lane,
     13.89 m/s -- identical priority scheme to scenarios/32-roundabout /
     33-roundabout-solo, so circulating traffic keeps right-of-way over entering traffic via
     the same engine mechanism those scenarios anchor.
   - Explicit `connections_final.con.xml`: 4 ring-continuity connections + at each of
     `rbX_S`/`rbX_N`, one entry-merge connection (onto the ring) and one exit-diverge
     connection (off the ring) -- 8 connections total, cloning scenarios/32-roundabout's
     connection pattern (that scenario labels its 4 ring nodes N/E/S/W with one movement
     each; here two of the four nodes each carry both an entry and an exit, since this
     roundabout replaces a real bidirectional through-route rather than 4 independent
     one-way legs).
4. Rebuild: `netconvert -n nodes_final.nod.xml -e edges_final.edg.xml -x
   connections_final.con.xml --no-turnarounds -o net.net.xml`. **First attempt failed**
   (`Error: Could not insert connection between 'rbX_ring_NW' and 'rbX_366_out' after build`)
   because of a geometry bug: the first version of the script put node X's exit spoke on the
   ring node facing *away* from X (so the spoke edge would have had to cross back through the
   ring), which netconvert rejected as a bad connection angle. Fixed by keeping both of a
   node's spokes (its entry *and* its exit) on the ring vertex that geometrically faces that
   node, per the construction above.

Confirmed the resulting `net.net.xml` runs cleanly end-to-end in `Sim.Run` (see "Engine run"
below) -- no exceptions from either the 9 traffic-light junctions or the roundabout.

## Engine capability note (single lane, not new -- matches city-30's existing finding)

Kept `--default.lanenumber 1` throughout (including the roundabout ring/spokes). This mirrors
the already-documented gap in `scenarios/_bench/city-30/NOTES.md`: multi-hop route-to-lane
resolution (`NetworkModel.ResolveLaneSequence` / `Engine.TryStrategicLaneChange`, C2-ii) is a
single-look-ahead scoped port in `Sim.Core`, so a multi-lane net can throw
`InvalidDataException: No <connection> found` for some multi-edge routes. Not re-litigated here
(would require touching `Sim.Core`, which this task explicitly rules out) -- single lane avoids
it entirely since every lane then has exactly one outgoing connection per direction.

## Demand tuning (Little's-law-style sweep)

`randomTrips.py --fringe-factor 20 --min-distance 400` biases demand to boundary-to-boundary
through-traffic (fixes the "cars born/removed at random mid-block junctions" look). Insertion
`period` was swept manually against SUMO reference runs (`--summary-output`), watching for the
`gen-benchmark.sh`-style collapse signature (halting/running > 0.5 **and** meanSpeedRelative <
0.2 at the run's end == runaway gridlock, not just normal TLS/priority-junction queuing):

| period (s) | end (s) | peak running | tail-mean running | halting/running (last step) | meanSpeedRelative (last step) | verdict |
|---|---|---|---|---|---|---|
| 0.7 | 600 | 555 | 494.5 | 0.67 | 0.14 | collapsing |
| 1.0 | 600 | 343 | 304.2 | 0.64 | 0.20 | collapsing (borderline) |
| 1.3 | 900 | 278 | 255.1 | 0.76 | 0.18 | collapsing |
| 1.5 | 900 | 210 | 200.3 | 0.61 | 0.26 | stable plateau (running settles ~198-204 from t=780-840) |
| 1.7 | 900 | 173 | 155.9 | 0.55 | 0.27 | stable, on the low side |
| 2.0 | 900 | 130 | 121.5 | 0.51 | 0.36 | stable, low |

Selected **period=1.5s**, **end=800s** (fill time + a clear plateau window within the
committed config, per the spec's 600-900s guidance). Final SUMO reference run on the exact
committed net+rou+config: peak running=210, tail-mean running=194.2, arrived=316/515 loaded,
mean trip duration=229.8s. A meaningful fraction of vehicles are halted at any instant
(TLS red phases + roundabout entry-yield + priority-junction yields across 9 signals + the
roundabout in a fairly dense single-lane net) -- this is normal queuing, not gridlock: arrived
count grows steadily throughout the run and running plateaus rather than diverging.

## Engine run vs target

`dotnet run --project src/Sim.Run -c Release -- scenarios/_bench/city-organic --fcd-out
scenarios/_bench/city-organic/engine.fcd.xml` completes all 800 steps with **zero exceptions**.
Engine-measured concurrency (from the emitted FCD): peak concurrent = 182, tail-mean concurrent
(last third of the run) = 165.1 -- both within the requested 150-300 peak-concurrent band
(engine's number differs somewhat from SUMO's own 210/194.2 reference, as expected given the
engine is a distinct, phase-1-parity-scoped port, not a byte-identical reimplementation; no
parity claim is made here since this scenario isn't in the `dotnet test` path).

## Viz

`dotnet run --project src/Sim.Viz -c Release -- scenarios/_bench/city-organic --fcd
scenarios/_bench/city-organic/engine.fcd.xml` wrote `replay.html`: lanes=382, junctions=49,
vehicles=514 (total across the whole run), steps=800, t=[0,799]. File size ~10.8 MB (larger
than city-30's ~1.2 MB replay.html, proportional to ~7x the concurrency and slightly more
steps/junctions -- everything is embedded inline so it's still fully self-contained/offline).

Sanity-checked with headless Chromium via Playwright (`/opt/node22/lib/node_modules`, viewport
390x844, deviceScaleFactor 3): page loads with zero `pageerror`/console-error events; the
on-page vehicle-count readout climbs from single digits at t~8s to 147 vehicles at t~450s
(8x playback) and the canvas visibly renders the irregular street layout with traffic-light
junction markers and moving vehicles. The roundabout itself (radius 20m) is far too small to
be visually distinguishable at full-network zoom in a screenshot, but it is present in the
network data and the engine traverses it without error.
