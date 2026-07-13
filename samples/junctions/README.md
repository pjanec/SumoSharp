# Junction demo scenarios

Four small, hand-authored networks with **differently shaped junctions**, for exercising the
live dead-reckoning viewer (`Sim.LiveHost`). Each was produced with `netconvert` from authored
node/edge XML (`--no-turnarounds true --junctions.corner-detail 8
--offset.disable-normalization true`) so lane geometry is smooth and the world origin is stable.

They are viewer inputs only — they are **not** parity scenarios and have no golden. The offline
test loop (`dotnet test`) never touches them.

| Folder  | Centre junction        | Legs / lanes            | What it shows |
|---------|------------------------|-------------------------|---------------|
| `cross` | signalized 4-way (`traffic_light`) | N/E/S/W, 2 lanes each (8 edges, 36 lanes) | traffic-light dots cycling; queueing on red; turns through a wide box |
| `tee`   | priority T (`priority`)            | W/E through, S stem     | give-way merges; right/left turns off the stem |
| `bend`  | 90° priority turn (`priority`)     | W↔N, single lane        | a single sharp corner — clearest view of off-tracking |
| `acute` | sharp priority fork (`priority`)   | W trunk splitting NE/SE | very tight turn angles — most dramatic swept-path swing |

## Running

```bash
# swept-path off-tracking (most dramatic on the tight bend / acute fork)
dotnet run --project src/Sim.LiveHost -- samples/junctions/bend/net.net.xml corner
```

then open the printed `http://localhost:<port>/` URL. Swap `bend` for `cross`, `tee`, or `acute`.

**Render-mode argument** (optional, order-independent — anywhere after the net path):

| Arg              | RenderRealism        | Heading                              |
|------------------|----------------------|--------------------------------------|
| *(none)*         | `ParityTangent`      | lane tangent — exactly what the goldens use |
| `chord`          | `ChordHeading`       | SUMO back→front chord heading        |
| `corner` / `offtrack` | `CornerCutCorrected` | chord + swept-path off-tracking bow |

**Seeing the off-tracking bow.** Roughly one in three spawned vehicles is a 12 m truck. Run with
the `corner` mode and, on the `bend` and `acute` junctions, its rear swings **wide** of the lane
centreline through the corner — the swept-path off-tracking the renderer applies in production
render mode. This pose is render-side only; SUMO parity (the committed goldens use the tangent
heading) is untouched — see `docs/SUMOSHARP-DEADRECKONING.md` §6.3.

## Regenerating

The nets are committed; you only need `netconvert` (SUMO) if you edit the source node/edge XML.
These particular nets were generated once and the intermediate `.nod.xml`/`.edg.xml` were not
kept — re-author them from the table above if a shape needs changing.
