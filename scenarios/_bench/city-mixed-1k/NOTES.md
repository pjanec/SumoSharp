# city-mixed-1k — 1000-concurrent mixed-junction multi-lane city

The largest, most feature-complete demo/stress scenario: a synthetic organic city with a
DELIBERATE MIX of junction types and ~1000 peak concurrent vehicles on 2-lane roads.

## Network (1064 junctions)
- **262 traffic-light** junctions
- **131 priority (non-TL)** junctions
- **3 single-lane roundabouts** (scenario-32-style priority rings, spliced into an otherwise
  2-lane organic net -- `--roundabouts.guess` does not fire on `netgenerate --rand`, so the
  rings are spliced from the proven `city-organic/gen-splice-roundabout.py`, chained x3)
- 2 lanes per edge (rings single-lane, the engine-supported roundabout form); ~1.5k lane groups.

## Result (the multi-lane-at-scale + roundabout stress test)
`Sim.BenchCity`, peak concurrent **1069**: 1231 departed, 383 arrived by t=700, **26 stuck**
(>=120 s at <0.1 m/s, ~2%); the rest still in transit at the 700 s window end. SUMO runs the
identical net+demand with **0 teleports**. So the full stack -- multi-lane junction passage
(C4-vii), willPass saturation (C4-viii), multi-hop/intra-edge route->lane (C2-v/vi), AND
single-lane priority roundabouts (C4-iii) -- holds at 1000 concurrent, ~98% flowing. The ~2%
residual stuck grows with density (0 at ~120 concurrent, 4/618 at ~400, 26/1231 at ~1000),
consistent with a small remaining saturation effect, not a hard gridlock.

## Viz
Phone-viewable via hosted artifact (iOS won't run JS from a local file -- see
VIZ_BENCH_ACHIEVEMENTS.md). The full 1000-concurrent FCD is ~90 MB, so the replay is built from
a timestep-downsampled FCD (every 4th step; the viz interpolates so motion stays smooth) ->
~14 MB replay.html. engine.fcd.xml and replay.html are regenerated, never committed (gitignored):
  dotnet run --project src/Sim.Run -c Release -- scenarios/_bench/city-mixed-1k --fcd-out scenarios/_bench/city-mixed-1k/engine.fcd.xml
  (downsample every Nth timestep) then dotnet run --project src/Sim.Viz -- scenarios/_bench/city-mixed-1k --fcd <downsampled>
