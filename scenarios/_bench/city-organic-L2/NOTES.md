# city-organic-L2 — big multi-lane organic demo

A synthetic organic town (`netgenerate --rand`, 274 junctions, 1186 edges), **2 lanes per edge**,
with traffic lights and realistic boundary-to-boundary through-traffic (~406 peak concurrent, 618
trips). See `provenance.txt` for exact commands + seed.

## Engine result (multi-lane at scale — the C4-vii/C4-viii validation)

`Sim.BenchCity`: 618 departed, 220 arrived by t=500, **only 4 stuck** (>=120 s at <0.1 m/s); the
rest are still in transit at the 500 s window end (peak concurrent 406). SUMO runs the identical
net+demand with 0 teleports. This exercises the full junction / lane-change / right-of-way stack
(multi-hop route->lane resolution C2-vi, intra-edge lane change C2-v, multi-lane junction passage
C4-vii, and the willPass saturation pre-pass C4-viii) on a 274-junction multi-lane network at ~400
concurrent — not just the small test grids.

## Viz

`replay.html` (committed) is the phone-viewable replay, viewed as a hosted artifact (iOS does not
run JS from a local file — see VIZ_BENCH_ACHIEVEMENTS.md). `engine.fcd.xml` is regenerated, never
committed (gitignored). Regenerate the replay with:
  dotnet run --project src/Sim.Run  -c Release -- scenarios/_bench/city-organic-L2 --fcd-out scenarios/_bench/city-organic-L2/engine.fcd.xml
  dotnet run --project src/Sim.Viz  -c Release -- scenarios/_bench/city-organic-L2 --fcd scenarios/_bench/city-organic-L2/engine.fcd.xml
