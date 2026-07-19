# SumoSharp teleport WITNESS — transferable pattern (geometry-free, shareable)

No real node IDs, coordinates, edge names, or place names. This is the pattern behind the
residual acceptance blocker: on a real ~1.5 km urban box, SumoSharp
(`claude/sumosharp-drop-in-binary-vq7u9p`) fires **36 jam/yield teleports (18 jam + 18 yield)**
where vanilla SUMO 1.20.0 fires **2** (`time-to-teleport=120`, identical net/demand/flags).

## One-line pattern

> A heavy through-corridor is squeezed through a **compound intersection** — several
> closely-spaced priority nodes joined by **very short (< 20 m, some < 2 m) connector edges**,
> plus a traffic-light node — and fed by **ultra-short (5–20 m) minor approaches carrying heavy
> demand**. Queues fill a whole short edge before they can dissipate, so a stopped vehicle has
> **no room to creep**; its jam/yield wait timer never resets and runs straight to
> `time-to-teleport`. Vanilla releases these vehicles at 60–95 s (just under threshold);
> SumoSharp holds them a few seconds longer, tips past 120 s, and teleports.

The defect is not one big bug — it is a **small per-step junction-throughput / yield-release
lag** that a short-connector topology converts into discrete teleport events. Two amplifiers:
marginally slower gap-acceptance/yield-release, and `device.rerouting` steering load into the
congested corridor that vanilla routes around.

## Dominant sub-patterns (ranked, with counts)

### YIELD (18 events) — waiting for right-of-way / green, no room to creep
| # | pattern | movement | foe | approach len | wait | creep | n |
|---|---|---|---|---|---|---|---|
| Y1 | short multi-lane through approach into a **traffic-light** node | through, minor/red phase | cross streams that get green | ≈ 19 m, 3 lanes | full 120 s | dead / ≤1 cell | 10 |
| Y2 | **ultra-short** approach into a priority node whose next link is minor | right turn | major through-corridor | ≈ 5 m, 3 lanes | full 120 s | dead / ≤1 cell | 5 |
| Y3 | short single-lane minor approach | **left turn across the major foe** | busy priority (prio-5) corridor | ≈ 16 m, 1 lane | full 120 s | dead / crept a little | 3 |

### JAM (18 events) — has priority but exit is gridlocked (spill-back)
| # | pattern | movement | cause | approach len | wait | creep | n |
|---|---|---|---|---|---|---|---|
| J1 | priority node whose **exit edge is a short connector that is itself full** | through | can't clear junction → stalls on the internal junction lane | exit < 12 m | full 120 s | dead / ≤ 1 cell | 7 |
| J2 | through movement on a 2-lane approach whose downstream short-connector corridor is backed up | through | major-corridor spill-back | 65–82 m, 2 lanes | full 120 s | crept < 3 m | 7 |
| J3 | misc single events at other cluster nodes | through | corridor congestion | — | 120 s | crept | 4 |

**Key difference between the two:** Yield = next junction link is MINOR (or TL red), vehicle
waits for a foe/green and never gets a long-enough gap. Jam = next link is MAJOR but the exit
connector is full, so the vehicle physically cannot clear. Both hit exactly 120 s because the
short edges give no creep-room to reset the wait timer.

## What the current `synthetic_junction` LACKS (why it no longer reproduces)

`synthetic_junction` is a uniform netgenerate grid: 120 m edges, 2 lanes everywhere, all
`priority` junctions, symmetric, long approaches, no traffic lights, no lane-count changes, no
sub-20 m connectors, no concentrated turn-across-foe demand. On that net a stopped vehicle
always has ~120 m of empty edge to creep into, so its wait timer keeps resetting and it never
reaches `time-to-teleport`. The real box reproduces because of five features the grid omits:

1. **Ultra-short approach/connector edges (5–20 m, some < 2 m)** — the single most important
   feature. No creep-room ⇒ wait timer runs to 120 s.
2. **A compound intersection**: 4–6 priority nodes chained by those short connectors, so
   queues spill back node-to-node (gridlock cascade).
3. **A traffic-light node** in the mix (produces the Y1 through-under-red yields).
4. **Asymmetric priority**: a `prio=5` major corridor that minor `prio=0` approaches must yield
   to, with **heavy left-turn-across-major** demand (Y3).
5. **Heavy demand funneled through the short edges** (single edges carrying 120–160 of ~500
   vehicles) plus `device.rerouting` steering more load in.

## Proposed synthetic recipe (netgenerate / hand-authored knobs)

Goal: vanilla ≈ 0–3 teleports, SumoSharp a clear 18+18-direction jam+yield residual.

**Topology (hand-authored `.nod`/`.edg` via netconvert, not a plain grid):**
- Build a **major corridor** of 4–5 nodes `A–B–C–D–E` on a line. Edge speed 13 m/s, 2 lanes,
  **`priority="5"`**. Make the **internal connector edges B→C, C→D each 1–3 m long** (this is
  the sub-2 m connector that has no analog in the grid). D→E and A→B can be ~60 m.
- At each corridor node attach a **minor approach**, `priority="0"`, length **5–18 m**,
  1–3 lanes, joining with a **turn** movement:
  - one node: **right-turn** minor approach (→ Y2),
  - one node: **left-turn-across-major** minor approach, 1 lane, ~16 m (→ Y3),
  - one node: through minor approach whose **exit is a 5–11 m connector** (→ J1).
- Add **one traffic-light node** at the head of the corridor fed by a **~19 m, 3-lane** through
  approach and 2–3 cross approaches; let the TL program give the through approach a short green
  share so it holds > 120 s under load (→ Y1). `tls.guess`/`--tls.set` on that node only.
- Keep total corridor length short so 2-lane approaches (60–82 m) can **spill back** into the
  short connectors (→ J2).

**Netgenerate alternative (faster, coarser):** `--grid` with **`--grid.length 12`** (tiny
edges) plus a couple of hand-added `prio=5` corridor edges and one TL node
(`--default-junction-type priority`, then `netconvert --tls.set <corridorHead>`); the short
grid length alone recreates the no-creep-room condition. Tune `--grid.length` down until vanilla
stays clean but SumoSharp teleports.

**Demand (deterministic, fixed seed):**
- Heavy through flow on the major corridor (period ~2 s) — the busy foe stream.
- Minor-approach flows sized to **160-ish vehicles each** over the horizon, with explicit
  **left-turn-across-major** and **right-turn** turn-ratios at the tagged nodes.
- `time-to-teleport=120`, `device.rerouting.probability=1.0 period=30 adaptation-steps=18`,
  `routing-algorithm=astar`, `collision.action=none`, `ignore-route-errors=true` — identical to
  the real cfg so the rerouting-amplifier is present.
- **No-cheating sink**: terminate some flows at a `parkingArea` with a long `<stop duration=…>`
  (park-and-stay) and use `departPos="stop"` origins, so the scenario stays audit-clean
  (`audit_nocheat.py` PASS) — no fringe-teleport shortcuts.

**Success test:** run both engines `--end 1000`, read `<teleports total jam yield>` from the
statistic. Target: vanilla ≤ 3, SumoSharp a clear jam+yield split (even ~10 total, both buckets
non-zero, clearly reproduces the direction).
