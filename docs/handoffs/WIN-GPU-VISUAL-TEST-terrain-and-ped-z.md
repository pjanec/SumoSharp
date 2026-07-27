# Visual sign-off on a GPU — 3-D terrain, ped elevation, and the baked grid

**Audience:** a Claude Code session on a **Windows desktop with a real GPU** and the **Geneva SumoData**
on local disk. **Branch:** `claude/handoff-docs-implementation-pmdu9z`.

Everything in this work was built and asserted **headlessly** — the numbers are in
`docs/EXTERNAL-NET-VIEWER-DESIGN.md` §4.1/§7.2 and in `demos/City3D/CityLib.Tests`. What no headless
VM can do is tell you whether the scene *looks* right. That is the entire job of this session.

> **Read `CLAUDE.md` at the repo root before changing anything.** Design-first; parity is the iron law;
> never edit `Sim.Core` for viewer work. If you find a bug here, **report it with a screenshot and the
> console line that proves it** — don't fix it ad hoc.

---

## 1. What changed, in one screen

Three things landed, all of them about **height**:

1. **Pedestrian elevation is real and mandatory.** A ped's height comes from the lane it is walking on,
   retained from ingest (`PedLane.ShapeZ`) and carried along its own route — not recovered by asking
   "what surface is nearest this (x, y)?". The 2-D forms of the navigation and render APIs were
   **deleted** (`FindPath(a,b)`, `ElevationsAlong(path)`, the 4-out-param `TryGetRenderPose`), so a
   renderer that forgets the height no longer compiles.
2. **`SumoNavMesh` is no longer a flat provider.** It records which baked polygon each waypoint came
   from and reads the height off that polygon. This matters because City3D's `PedSimSource` routes on
   it.
3. **The viewer's ground datum is no longer flat.** A `TerrainField`, baked on net load from
   `Lane.ShapeZ`, defines a ground height everywhere. The grey grid is baked over the net and draped
   over it; the zone tint subdivides so its interior follows it; POI markers, doors, building bases,
   traffic-light poles and the realism ring follow it for free (they all go through
   `SumoGodotFrame.GroundToGodot`, which now samples the field).

**What did NOT change:** anything on a 2-D net. A net with no `ShapeZ` bakes to a flat field and every
overlay computes exactly what it did before. That is a structural guarantee, not a tolerance — if the
demo city looks different, that is a bug, and item **T0** below is the check for it.

---

## 2. Environment

| Need | Notes |
| --- | --- |
| .NET 8 SDK | `dotnet --version` |
| Godot **4.7.1 (.NET/mono)** | the mono build, not the plain one |
| Geneva SumoData | **on local disk on the Windows box.** If you don't know where, ASK THE OWNER — do not go looking, and do not substitute another dataset. |
| GPU | the point of the session |

Nothing in this repo references the Geneva data by path or environment variable, deliberately: it is
large and access-restricted, so it is never a test dependency. It is an *input you type on the command
line* for this session only.

### Build

```powershell
cd <repo>\demos\City3D
# ⚠ MANDATORY FIRST STEP, see §6: the local packages are always version 0.1.0, so a stale copy in the
# NuGet global cache will silently make you test old code.
Remove-Item -Recurse -Force "$env:USERPROFILE\.nuget\packages\sumosharp.*"
bash build.sh          # or run the dotnet pack/restore/build steps it contains
```

Then the headless suites, so you know the machine agrees with CI before you judge anything visually:

```powershell
cd <repo>
dotnet test Traffic.sln -c Release                                   # expect 775 pass / 0 fail / 4 skip
dotnet run --project src\Sim.Bench -c Release                        # expect hash BF3794A4704BCD79, par == single
dotnet test tests\Sim.LiveCity.Tests -c Release                      # expect 80/80
dotnet test demos\City3D\CityLib.Tests -c Release                    # expect 176 pass / 3 fail (see §6)
```

---

## 3. Run recipes

The viewer takes the scenario **two** ways. A `preprocess.py` cut names its net `scenario.net.xml`, so
it needs `--sumocfg`; a plain SumoData dataset dir (whose net is `net.xml`) takes `--dataset`.

```powershell
# A: a Geneva cut that ships a .sumocfg
godot --path <repo>\demos\City3D\Viewer -- --live-city --sumocfg "<geneva>\<cut>\scenario.sumocfg" --show-zones

# B: a SumoData dataset directory
godot --path <repo>\demos\City3D\Viewer -- --live-city --dataset "<geneva>\<cut>" --show-zones

# C: the committed 3-D fixture — small, always present, no Geneva needed. Do this FIRST.
godot --path <repo>\demos\City3D\Viewer -- --live-city --sumocfg "<repo>\scenarios\_ped\georef_min\scenario.sumocfg" --show-zones

# D: the 2-D demo city — the regression baseline.
godot --path <repo>\demos\City3D\Viewer -- --live-city --show-zones
```

**Controls:** `G` grid · `Z` zone tint · `B` buildings · `P` POIs/doors · `H` cycle realism-zone mode ·
`F`/`Home` reframe · RMB-drag look + `WASD`/`QE` fly (`Shift` sprint) · Alt+LMB orbit · wheel zoom. The
side panel has live **cars** and **peds** density sliders.

**Take screenshots for every checklist item** and put them somewhere the owner can see them.

---

## 4. The checklist

Work top to bottom. **T0 first** — if the 2-D baseline moved, nothing below is interpretable.

### T0 — the 2-D demo is unchanged (recipe D)

- [ ] The demo city renders exactly as it did before this branch. Roads, zone tint, buildings, POIs,
      cars, peds all at the same heights. Nothing sunken, nothing floating.
- [ ] The grid is a flat grey plane just under the roads.
- [ ] ⚠ **Expected difference, not a bug:** the grid is now **finite** (net bbox + 400 m) instead of
      sliding under the camera forever. Fly far out and it ends. If that reads badly on screen, say so —
      it is a deliberate trade (see §5) and the owner may want it changed.
- [ ] Console shows `Main: ground grid baked (25m spacing, N lines, N segments) over TerrainField(flat z=0.00)`.
      **`flat` is the assertion here** — a 2-D net must bake a flat field.

### T1 — the fixture, then Geneva: does the ground follow the hills (recipes C, then A/B)

- [ ] Console at load prints the field, e.g.
      `over TerrainField(12x12 @ 40.0m, 52 measured, z 370.2..397.7)`. On Geneva expect a much larger
      lattice and a wider z range. **Write the printed line into your report** — it is the one number
      that says the bake saw real elevation.
- [ ] Press `G`. The grid is **not a flat sheet**. It rises and falls with the roads.
- [ ] Fly along a street that climbs. The grid stays **just below** the road surface the whole way — it
      does not cut through the road on the way up, nor drop away underneath it on the way down.
- [ ] The grid does **not** slide when you move the camera. It is nailed to the ground.
- [ ] Look along a valley or a slope from a low angle. The grid reads as a draped surface, not as
      terraced steps. *(If you see terracing, that's the fill's relaxation not converging — report the
      location and the printed `CellSize`.)*

### T2 — the zone tint follows the same ground (recipe A/B, `--show-zones` or press `Z`)

- [ ] The tint sits on the ground, not at one flat altitude.
- [ ] **The key one:** find a **large** district that spans a slope. Its *middle* must be on the ground,
      not just its corners. Before this work the interior was a plane through the corner heights, so a
      big district on a hillside had its middle buried. Look for the tint disappearing into the hill
      mid-district — that is the failure this closes.
- [ ] The tint does not z-fight with the roads (it sits 5 cm under them) and the grid does not z-fight
      with the tint (10 cm under).
- [ ] Zones still draw largest-first — a big district does not paint over a small one nested in it.

### T3 — the other ground overlays came along for free (press `P`, `B`, `H`)

All of these route through the same seam, so they should need no separate work — verify that:

- [ ] **POI ground markers** sit on the ground, on slopes as well as flats.
- [ ] **Building-entrance doors** sit at their building's base, not floating or buried.
- [ ] **Procedural building bases** meet the ground. No building hovering, none sunk to its windows.
- [ ] **Traffic-light poles** stand on the road surface, not in the air or through it.
- [ ] **The realism-zone ring** (`H` → Follow) tracks the ground under the camera as you fly over
      changing elevation.

### T4 — pedestrians are at the right height (recipe A/B/C)

This is the part that was owned by the other workstream and came back here; it is the one most worth a
careful look.

- [ ] Peds' feet are **on the pavement**, not on a flat plane through the city. Watch a group walking
      up a hill.
- [ ] Peds and **cars on the same street are at the same height**. A ped on the pavement beside a car
      must not be metres above or below it.
- [ ] Peds crossing at a crossing stay on the crossing surface.
- [ ] Drag the **peds** density slider up. New peds appear at correct heights too — the elevation is
      built per-ped at spawn, so a spawn-time bug only shows on newly spawned peds.
- [ ] If the cut has a **footbridge, an underpass, or a multi-level junction**: peds on the upper deck
      stay on the upper deck and peds beneath stay beneath. This is the stacked-surface case the whole
      provenance change exists for, and the synthetic test for it is a 12.5 m clearance bridge —
      **real geometry is the only place it can be confirmed**. If Geneva has no such spot, say so
      rather than passing the item.

### T5 — it holds up under load and over time

- [ ] Drag **cars** and **peds** to a high density. Nothing sinks, floats, or starts flickering.
- [ ] Note the load time and whether the terrain bake is perceptible in it. The bake is one extra pass
      over lane vertices inside the existing scan, so it should be lost in the noise of parsing — if
      loading got materially slower, that is a finding.
- [ ] Leave it running several minutes. Heights stay stable; nothing drifts.
- [ ] Fly to the far edge of the cut and beyond it. Outside the net the field clamps to the nearest
      edge height — the ground should flatten out, not fall to zero and not explode.

---

## 5. Known and expected, so you don't report them as bugs

1. **The grid is finite.** Net bbox + 400 m. Deliberate: a mesh that is translated under the camera
   cannot be draped over terrain. Flag it if it looks wrong; it is a trade, not an oversight.
2. **The field interpolates ROAD heights.** Terrain far from any road is a smooth fill (a BFS from the
   measured lattice corners plus two smoothing passes), not surveyed ground. Big parks, water, and
   open land are plausible, not correct.
3. **Resolution is a 40 m cell**, growing on very large nets to keep the lattice ≤ 512 corners per axis.
   Fine detail between roads is not represented.
4. **Ground overlays are anchored to the field, not to their own elevation.** Anything that *has* real
   elevation — roads, cars, peds, crosswalk zebra, lane dashes — uses that instead and is unaffected by
   any of the above.
5. **`z == 0.0` is ambiguous by contract** (design §9.1): a ped on a 2-D net and a ped genuinely at 0 m
   report the same thing. Not a bug.

---

## 6. Two traps that have already bitten this work

**Trap 1 — the stale NuGet cache.** `demos/City3D/build.sh --pack-only` writes
`SumoSharp.*.0.1.0.nupkg` at a version that never changes, so NuGet's global cache will happily serve a
**stale** package and City3D will silently build against an old engine. **Always**
`Remove-Item -Recurse -Force "$env:USERPROFILE\.nuget\packages\sumosharp.*"` before repacking. This is
CLAUDE.md measurement-discipline #9's failure mode by a different mechanism, and it masked three real
test failures for an unknown length of time.

**Trap 2 — three `CityLib.Tests` failures are PRE-EXISTING.** Expect
`176 pass / 3 fail`. The three are `ReconstructorS2Tests`:
`Reconstructor_StoppedVehicle_DoesNotCreep`, `…_CenterIsHalfLengthBehindSnapshotFront`,
`…_JunctionTurn_FollowsConnectingLaneArc_Smoothly`. They were confirmed failing on a clean worktree at
the pre-change commit `4bf36e5` with a cleared cache, and they are about **vehicle** reconstruction, not
elevation:

- center sitting 6.484 m behind the front bumper where L/2 = 2.50 m is expected
- 0.1250 m/frame creep while stopped
- 0.996 m of stray off the connecting-lane centreline through a turn

**These may well be visible on screen** — a stopped car drifting, or a car cutting the corner through a
junction. If you see either, that is these bugs, not the terrain work. Noting them with a screenshot
would be genuinely useful; fixing them is a separate task.

---

## 7. Reporting

For each checklist item: **pass / fail / not-applicable**, with a screenshot. For any failure add the
recipe you ran, the console line at load (especially the `TerrainField(...)` line), and where in the cut
you were. Numbers over adjectives — "the tint is ~8 m under the road at the top of this street" beats
"the zones look wrong".

Reference docs, if you need to go deeper:

- `docs/EXTERNAL-NET-VIEWER-DESIGN.md` — §4.1 the mandatory-z API change and why, §7.2 the terrain field
  (mechanism, grid, zones, determinism), §8 the standing limitations.
- `docs/EXTERNAL-NET-VIEWER-TASKS.md` — Stage E, the success conditions each change was accepted against.
- `docs/EXTERNAL-NET-VIEWER-TRACKER.md` — what is done, and the measured numbers.
- `demos/City3D/CityLib.Tests/{TerrainFieldTests,GroundGridBuilderTests,ZoneGroundBuilderTests}.cs` —
  the headless assertions behind T1/T2.
- `tests/Sim.Pedestrians.Tests/PedElevationMultiLevelTests.cs` — the synthetic stacked-deck fixture
  behind T4's last item.
