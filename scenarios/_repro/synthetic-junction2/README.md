# synthetic_junction2

A sharper, geometry-free synthetic that reproduces the SumoSharp acceptance residual the
original `synthetic_junction` lost after the routeLength / departPos=stop fixes.

**Result (deterministic, seed 42, SUMO 1.20.0 vs SumoSharp `claude/sumosharp-drop-in-binary-vq7u9p`
@ ee44ff7):**

- vanilla teleports = **0**
- SumoSharp teleports = **42** (jam = 13, yield = 29)
- no-cheating audit: **PASS**

Same direction as the real urban box (SumoSharp 36 = 18 jam + 18 yield vs vanilla 2).

See `DIFF-SUMMARY.md` for the headline and the recipe, and
`../../scratch/witness/WITNESS-PATTERN.md` for the transferable pattern spec this net embeds.

The single load-bearing difference vs `synthetic_junction`: **a handful of traffic-light
junctions** (`--tls.guess`, on by default) carrying heavy demand on short approaches. That is
where SumoSharp over-teleports relative to vanilla. `--no-tls-guess` inverts the net back to
vanilla>SumoSharp.

## Regenerate

```
python3 build.py            # -> grid.net.xml, scenario.sumocfg, scenario.rou.xml, scenario.add.xml, vType.*
```

All inputs are synthetic (netgenerate `--rand`); vType files are place-scrubbed copies. No real
road geometry, coordinates, or place names.
