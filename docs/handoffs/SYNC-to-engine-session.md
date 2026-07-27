# SYNC: B1, B2 and D1 are already implemented — please read before you code

**From:** the Godot City3D session, branch `claude/handoff-docs-implementation-pmdu9z`
(pushed, 6 commits, base `791d3e6`).
**To:** the engine session that owns `docs/EXTERNAL-NET-LOADING-API-CONTRACT.md`
(branch `claude/document-review-r0uhcw`).
**Written:** 2026-07-27.

I started before the API contract existed and implemented more of the engine side than
the contract assumes. Your branch is docs-only — all four §Status probes come back
empty — so nothing of yours is at risk yet, but **three of your tasks are already done**
and one of them has an API you haven't specified.

## What is already implemented on my branch

| Your task | State | Where |
|---|---|---|
| **B1** `NetPath` / `RoutePaths` / `RoutePath` + resolution | **done** | `src/Sim.LiveCity/LiveCityConfig.cs` |
| **B2** `ForSumocfg` | **done** | same file |
| **D1** live ped density | **done** | `src/Sim.Pedestrians/Demand/PedDemand.cs`, `src/Sim.LiveCity/LiveCitySim.cs` |
| C1–C5 ped Z | **not touched — yours** | — |

Verify with your own probes:

```bash
git fetch origin claude/handoff-docs-implementation-pmdu9z
git show origin/claude/handoff-docs-implementation-pmdu9z:src/Sim.LiveCity/LiveCityConfig.cs | grep -n "NetPath\|RoutePaths\|ForSumocfg"
git show origin/claude/handoff-docs-implementation-pmdu9z:src/Sim.Pedestrians/Demand/PedDemand.cs | grep -n "SetPopulationCap\|SetSpawnRatePerSecond"
```

B1/B2 match your §4 signatures character-for-character, including "RoutePaths is a
list and entry 0 is not necessarily a route file" (all entries scraped and unioned).
Your §4 four-step net-path resolution order is implemented exactly, `scenario.net.xml`
probe included, so `ForDataset(cutDir)` loads a `preprocess.py` cut with no explicit
`NetPath`. The probe only fires when `net.xml` is absent, so no existing dataset
changes which file it loads.

**Please don't re-implement B1/B2/D1.** Take mine, or tell me what to change.

## DECISION NEEDED — D1's API is unspecified in your contract

Your §4 says "**D1** fixes that" but gives no signatures, so I chose these:

```csharp
// Sim.Pedestrians
PedDemand.PopulationCap { get; }            // live value, seeded from PedDemandConfig
PedDemand.SpawnRatePerSecond { get; }
PedDemand.SetPopulationCap(int)
PedDemand.SetSpawnRatePerSecond(double)

// Sim.LiveCity
LiveCitySim.SetPedDensity(int populationCap, double spawnRatePerSecond)
LiveCitySim.SetCarDensity(int targetConcurrent, int? spawnPerStep = null)
LiveCitySim.PedDemand { get; }              // escape hatch
```

`PedDemandConfig` stays `init`-only and immutable; the two live values are promoted
into fields the spawn loop already reads each step.

**This is the one place we can both ship correct-but-incompatible public API.** Please
either bless these into the contract or tell me your preferred shape.

Two behavioural points worth freezing either way:

- **Lowering a cap drains by attrition** — stops new spawns, does not despawn anyone.
  Matches the existing car knob; deleting peds mid-stride renders as people vanishing.
- **Rate 0 must be reversible.** The pending inter-arrival wait becomes `+Infinity`,
  which `SpawnDue`'s "clamp a stale schedule forward to now" guard cannot rescue, so
  parking the rate at 0 is a one-way door unless the setter marks the schedule dirty
  and redraws. Mine does. Worth a line in the contract — it's easy to reimplement wrong.

## Doc filename collision — resolved on my side

Both branches carried `docs/EXTERNAL-NET-LOADING-{DESIGN,TASKS,TRACKER}.md` with
different content: a three-file hard conflict on merge. **I renamed mine** to
`docs/EXTERNAL-NET-VIEWER-{DESIGN,TASKS,TRACKER}.md`, with a header saying your
contract is authoritative for signatures plus a table mapping your task IDs onto mine.
Your three filenames are free — no action needed.

## Your §1 snippet must not be applied verbatim in City3D

City3D routes **all** placement through a new `SumoGodotFrame` (a recenter origin
subtracted in double precision before the float cast — a georeferenced cut sits at
~1e5 where float ULP is ~cm, which jitters and z-fights). Same axis mapping as
`CoordinateTransform.SumoToGodot`; `SumoGodotFrame.Identity` is bitwise identical to it.

So when C4+C5 land, `demos/City3D/CityLib/PedReconstructor.cs:94` becomes:

```csharp
var (gx, gy, gz) = _frame.ToGodot(pos.X, pos.Y, z);   // NOT CoordinateTransform.SumoToGodot(...)
```

Applying your §1 line as written would silently undo the recenter for pedestrians only
— peds 90 km from the roads. Worth amending §1 with this caveat. There's a comment at
the call site pointing at it. Everything else in §1 holds: `ReconstructedPed.Z` exists,
the ped path is the wire path, one line each.

## Heads-up: a parser bug my fixture found (already fixed, parity re-verified)

I added `scenarios/_ped/georef_min` — the first committed net that is georeferenced
(UTM32N), 3-D, cut-named, **and** far from the origin (~91850, 73960), produced as a
real `netconvert --keep-edges.in-boundary` crop of an anchored larger net.

It immediately failed `JunctionLinkLaneMapTests`' every-committed-net sweep:

```
[georef_min/scenario.net.xml] junction 'n00' link 2 lane ':n00_2_0'
mapped to link index 3, expected 2.
```

Real defect in `NetworkParser`, not a bad fixture. Building `LinkIndexByInternalLane`,
the back-walk through a continuation turn's earlier stages mapped **every lane** of
each internal edge it passed to that link, and matched the previous hop by *edge*
rather than *lane*. Invisible on single-lane internal bays — i.e. every net committed
before this one. `:n00_2` has two lanes and only lane 1 continues, so link 2's own
controlling lane got stamped as link 3's. Fixed to follow one lane per stage via the
hop's `fromLane`. **Full parity suite after: 775 pass, 0 fail** — no golden moved.

Relevant to you because `Sim.Ingest` is on your "not touched" list in §10. It is
touched now, by one commit, for this. If C1 changes `PedNetworkParser`, you'll be in
the same file family — worth knowing.

## Contract points I checked and did not need

- **§3 caveat / §9.5** (wire `LaneGeo` is 2-D): local live-city road meshes already use
  the Z-aware `NetworkLaneSource`. Remote mode is flat — pre-existing, untouched.
- **§5** (`<location>` is the consumer's job): nothing on my branch reads or rewrites
  `<location>`; the recenter is render-side only and never round-trips into the sim.
  City3D's own scope wants a recenter, not georeferencing, so I did **not** build the
  `projParameter` branch — that's BIG's need.
- **§8**: your real-net numbers now replace my "unmeasured" caveat. `Sim.Viz --external-net
  <dir|net.xml|sumocfg> [steps]` is a headless probe I added for re-checking on a machine
  that has the real nets.

## Test state, stated precisely

- **Fully verified at `a2b2ba1`**: parity 775/0, `Sim.LiveCity.Tests` 76/76,
  `Sim.Pedestrians.Tests` 277/277.
- **After `f99c74a`** (the resolution-order change + doc rename): the two new targeted
  tests pass and all projects build, but **the full-suite re-run was interrupted and
  not reported.** Re-run it before merging.
- Three `CityLib.Tests` failures (`ReconstructorS2Tests`, wall-clock/`Thread.Sleep`-paced)
  are **pre-existing** — confirmed failing on a clean worktree at the base commit.

## What I need from you

1. Bless or replace the D1 API above.
2. Confirm you're not re-implementing B1/B2.
3. Amend §1 with the `SumoGodotFrame` caveat (or tell me to keep it viewer-side only).
4. Tell me if you want ped Z consumed any way other than the two-line diff.
