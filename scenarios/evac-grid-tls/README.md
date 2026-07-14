# evac-grid-tls — signalized panic-evacuation grid (PARITY-EXEMPT)

A 5x5 traffic-light grid (`netgenerate --grid --grid.number=5 --grid.length=80
--grid.attach-length=60 --default.lanenumber=1 --no-turnarounds --tls.set=B1,B2,B3,C1,C2,C3,D1,D2,D3`),
with outward boundary stubs used as flee exits, same naming scheme as `../evac-grid` (nodes
A0..E4, stubs `left{i}`/`right{i}`/`bottom{i}`/`top{i}`).

`--tls.guess` did not classify any junction as traffic-light on this net (all-equal single-lane
approach speeds fall below its default guess threshold), so the 9 interior junctions (B1, B2, B3,
C1, C2, C3, D1, D2, D3 — everything not on the outer ring) are explicitly typed `traffic_light`
via `--tls.set`, each getting its own generated `<tlLogic>` static program (9 programs total, one
per interior junction). The 16 boundary junctions (A0..E0, A4..E4, A0..A4, E0..E4 minus corners
counted once) stay plain `priority` junctions, same as the original grid.

Nodes sit at x,y in {60, 140, 220, 300, 380} (grid.length=80, offset by attach-length=60); the grid
centre is junction **C2** at **(220, 220)**.

This scenario is **parity-EXEMPT**, exactly like `../evac-grid`: it exercises the external
evacuation layer (`Sim.Evac`, `docs/EVAC-DEMO-TLS.md` / `docs/PANIC-EVAC.md`), **not** the
SUMO-parity core. There is deliberately **no golden** (no `golden.fcd.xml` / `tolerance.json` /
`provenance.txt`) and **no `rou.xml` demand** — the driving core stays byte-identical whether or
not this net exists (determinism hash unchanged), and `EvacTlsScenario`/`EvacTlsDemoTests` spawn
their vehicles at runtime via `Engine.SpawnVehicle`, so the offline test loop needs neither SUMO
nor a committed route file.

Only `net.net.xml` is committed. It was authored once with `netgenerate` (SUMO 1.18, network-side)
purely for geometry + traffic-light programs; the exact SUMO version is irrelevant here because
nothing compares against a SUMO trajectory. `Engine.LoadNetwork` builds the TLS phase machines
from the net's `<tlLogic>` blocks the same way it does for the parity TLS scenarios (`09-traffic-
light`, `35-actuated-tls`), so organized traffic on this net actually stops at reds — no engine
change required.

## Entry/exit stubs (20 each)

Straight-across entries (boundary -> grid) and their opposite exits (grid -> boundary), one pair
per side per row/column index i=0..4:

- horizontal: `left{i}A{i}` -> `E{i}right{i}`, and reverse `right{i}E{i}` -> `A{i}left{i}`
- vertical: `bottom{i}{col(i)}0` -> `{col(i)}4top{i}`, and reverse `top{i}{col(i)}4` -> `{col(i)}0bottom{i}`
  where `col(i)` maps 0..4 -> A..E

See `src/Sim.Evac/EvacTlsScenario.cs` for the enumerated `Routes`/`ExitEdges` arrays.
