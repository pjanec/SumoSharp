# NEED — multi-lane junction passage: vehicles deadlock at the stop line

**For the SUMO parity coding session.** Found immediately after `C2-vi` landed (which fixed
multi-lane route→lane *resolution* at insertion). `C2-vi` unblocked insertion, but a **general
multi-lane (`-L 2`) network now runs to completion yet leaves ~60% of vehicles permanently stuck at
junction stop lines**. Verified on `main@2c1d93e`. This is the real remaining blocker for multi-lane
benchmarks/demos. Parity-track bar (exact `@1e-3`, anchor + golden + gate).

## Reproduced (engine vs SUMO on the identical net+demand)

```
export SUMO_HOME=/usr/local/lib/python3.11/dist-packages/sumo
netgenerate --grid --grid.number=6 --grid.length=250 -L 2 --tls.guess --seed 7 -o net.net.xml
python3 $SUMO_HOME/tools/randomTrips.py -n net.net.xml -e 300 -p 4 --fringe-factor 10 --min-distance 500 --seed 7 -o trips.xml
duarouter -n net.net.xml -r trips.xml -o rou.rou.xml --seed 7 --named-routes --ignore-errors
```
- **Engine** (`dotnet run --project src/Sim.BenchCity -- <dir>`): 75 departed, **27 arrived, 47 stuck**
  (>=120 s at <0.1 m/s), all braked to a junction stop line (pos ~229 on ~250 m edges, speed 0).
- **SUMO** (`sumo -n net.net.xml -r rou.rou.xml --end 600`): **75 inserted, 0 running at end, 0
  teleports**, all arrived, avg duration 180 s. SUMO runs it as ordinary free-to-moderate traffic.

## It is a MULTI-LANE JUNCTION gap (not TLS, not lane-changing)

Three discriminating checks:
1. **Not traffic-light-specific.** Re-running with NO traffic lights (`--tls.set ""`, all priority
   junctions) gives the SAME outcome: 28 arrived, 47 stuck. So it is not a TLS link-state bug — it
   affects priority junctions too.
2. **Not a lane-change / wrong-lane failure.** A stuck example: veh 0 is stuck on `E3D3` **lane 0**
   for a **straight-through** move to `D3C3`, and lane 0 **does** connect onward
   (`<connection from="E3D3" to="D3C3" fromLane="0" toLane="0" dir="s" .../>`). The vehicle is on the
   correct connecting lane with a valid connection — it simply never gets granted passage through the
   junction. (Lane `_1` is used elsewhere, so runtime lane-changing itself works.)
3. **Single-lane junctions work.** Every committed single-lane junction scenario passes (9b, C3, C4,
   rung 10), and the `-L 1` benchmark rungs (city-300/3000) run with **0 stuck** through the same
   kinds of junctions. The only new variable is **2 lanes per edge**.

## Diagnosis (for the parity session to confirm with instrumentation)

The junction right-of-way / passage machinery (`Engine.JunctionYieldConstraint` /
`FindFoeVehicle` / the `MSLink` conflict geometry in `NetworkModel`/`Engine`, the 9b + C4 family)
was built and parity-tested exclusively on **single-lane** junctions. A multi-lane junction has more
links per approach (each lane's connection is a separate link with its own `linkIndex` and internal
lane), and the internal lanes of the two through-lanes can themselves be flagged as mutual foes /
conflicts in the `<request>` matrix. The most likely cause is that a vehicle on a valid connecting
lane is made to yield **forever** to a foe that never clears — e.g. it treats the parallel
through-lane's internal lane (or a companion link at the same junction) as a blocking foe, or the
multi-lane conflict/response bitstring is indexed wrong so a non-conflicting movement reads as
conflicting. `C4-vi` fixed the *far-routed* false-positive foe; this is a separate multi-lane
foe/conflict-resolution defect. Even a straight-through movement (which should have priority) is
blocked, which points at the foe/conflict resolution rather than a legitimate yield.

Suggested first probe: the smallest reproduction is a single 2-lane priority (or TLS) crossroads
with one straight-through vehicle and no real conflicting traffic — confirm whether it still brakes
to the stop line and never proceeds, then read which foe/link `JunctionYieldConstraint` is yielding
to and why (`<request>` response/foes bits for the multi-lane link indices vs. SUMO's `MSLink`).

## Definition of done

1. **General `-L 2` city flows.** The repro above runs in the engine with a stuck-count comparable
   to SUMO's (≈0 on this net), arrived count within the benchmark's aggregate tolerance.
2. **New anchor + golden.** A minimal 2-lane junction (priority and/or TLS) where a through vehicle
   must be granted passage against correctly-resolved multi-lane foes — distinct from the
   single-lane 9b/C4 anchors. `sigma=0`, SUMO golden `--precision 6`, match `lane`/`pos`/`speed`
   `@1e-3`.
3. **Inert / no regressions.** All committed single-lane junction scenarios (9b, C3, C4, rung 10,
   36, 37) stay green (`dotnet test`, currently **156**); `Sim.Bench` hash unchanged.
4. **Gate.** parity-reviewer ACCEPT; faithful to `MSLink`/`MSRightOfWayJunction`/`MSVehicle`.

## Why it matters

This — not C2-vi — is the true gate for multi-lane at scale. With it fixed, the scaled-city
benchmark and the organic demo can run `-L 2+` and finally exercise lane-changing/overtaking; until
then they stay single-lane (`-L 1`), which works and is committed.
