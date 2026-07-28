# Live-city realism COORDINATOR — session handoff

**You are the next live-city-realism session.** Start from **`main` HEAD** (this doc was written at
`d4e1819`). Your job: (1) keep driving live-city **realism** (car↔ped behaviour + ped-LOD in the
high-realism zone) to done, (2) act as the **coordinator** for the other in-flight sessions, and (3) be
ready to **show the owner the current live-aim state as a ZIPPED Sim.Viz HTML replay** (~1000 peds / ~300
cars). Read `docs/TASKS-TODO.md` first every time — it is the live board; this doc is the orientation.

---

## 0. First 10 minutes (startup checklist)
1. `git fetch origin main`; branch off it (e.g. `claude/livecity-realism-coordinator`).
2. Read **`docs/TASKS-TODO.md`** top-to-bottom (short, live) + its **"In-flight by session"** table, and
   **`docs/COORDINATION-livecity-realism-sessions.md`** (boundary + no-touch lists). `docs/TASKS-DONE.md`
   is the archive (completed work + full roadmap detail) — consult for detail, don't read end-to-end.
3. Confirm your baseline gates (below). ⚠ **`Sim.LiveCity.Tests` is NOT in `Traffic.sln`** — build that
   csproj explicitly or you test stale code.
4. Generate + zip the 1k/300 replay (§3) and send it to the owner as the current-state snapshot.
5. Pick / coordinate the next realism item (§2); keep the trackers current.

## Gates / iron law (run after every change; realism changes are parity-inert but VERIFY)
- `dotnet test tests/Sim.ParityTests -c Release` = **775/4**, all **661 goldens byte-identical**.
- `dotnet run --project src/Sim.Bench -c Release` → hash **`BF3794A4704BCD79`**, `par == single`.
  (Bench runs `_bench/highway-dense`, no SUMO ref — a re-pinned tripwire, not a verified-correct value; the
  parity statement is the goldens. It last moved with PR #13 when 7 junction gates defaulted ON.)
- `dotnet test tests/Sim.LiveCity.Tests -c Release` = **90/90** (build the csproj explicitly first).
- `dotnet test tests/Sim.Pedestrians.Tests -c Release` = **324/324**. No `System.Random`.
  (These two counts and the goldens count above are a dated snapshot; the live gate lives in
  `docs/TASKS-TODO.md`.)
- Why inert: every car↔ped path is gated on `Engine.CrowdSource != null`, which no committed golden/bench
  attaches. Keep new behaviour behind that gate (or a demo-only flag) and parity stays byte-identical.

---

## 1. Where realism stands (main `d4e1819`)
**Closed:** #1/#2 (crossing-yield), **A** (stopped-car sideways wobble → `SuppressHeldCrowdSwerve`),
**B-guard** (car stops for a ped in its path in the zone, PR #15); F1/F2/F4a; arbitrary-net
import (PR #11); junction-correctness + 7 gates ON (PR #13). Crowd-disc query buffer `MaxCrowdDiscs=256` +
viewer click-to-identify are in (cherry-picked from `claude/livecity-realism-fixes`).

**#3 (peds vanish on promotion) / #4 (ORCA stay-ORCA/wander) / #6 (idle-clustering into one point) are
NOT closed by this handoff — correction.** This passage previously claimed all three closed via "PR #14";
`docs/TASKS-TODO.md`'s in-flight table is the authority for session status and lists the owning
**ped-LOD-lifecycle** session (`claude/livecity-ped-lod-lifecycle-bylitj`) as **STARTED**, not merged —
consult that table, not this line, for the current state. Separately, per
`docs/LIVE-CITY-PED-LOD-LIFECYCLE-DESIGN.md` §3.2 (owner-ratified), **#4's proposed leaky-dwell/watchdog
fix was DROPPED** because no stuck-ORCA ever reproduced, so #4 will never close as "fixed" in the sense
this line implied — the finding was that #3's wire bug was the actual cause of the visible wander, and
`OutsideSince` demotion already worked correctly.

**Still open — realism:**
- **#5 — ORCA peds don't dodge a car standing on the crosswalk.** Needs a **car→ped obstacle feed** (the
  mirror of the ped→car `CrowdSource`). Owner = *ped–vehicle avoidance* session. Brief:
  `docs/LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`.
- **Out-of-zone cars are BLIND to peds.** Outside the LC-realism zone a car sees a ped only if it's on a
  crossing (peds promote to `HighPower` only in-zone). Extending yield net-wide is a **ped-LOD feed**
  decision with a real perf cost — it **bounds how far car-side ped safety can go**, and it's coupled to the
  `OrcaCrowd.QueryNear` full-scan (which only becomes a perf problem once you feed the whole population).
  Unallocated; natural fit = ped-LOD-lifecycle or ped–vehicle.
- **W4 — multiple / large / overlapping camera realism zones** (unallocated). N `InterestSource`s, N-zone car
  LC-realism, `SetLcRealismZones`, re-point the ped-disc feed at the zone union. Handoff:
  `docs/LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md`.

**Adjacent, bigger (blocks the clean realism invariant but is core-junction work):**
- **F3 — pre-existing junction-overlap engine bug** (~3 m car overlaps on crossing internal lanes; present
  on `main` long before realism). Route to core junction work. `docs/F3-JUNCTION-OVERLAP-HANDOFF.md`.
- **Junction DISCHARGE / density** — our cars *roll* ~8 m/s where SUMO rolls ~11 (trips 248 s vs 181 s, same
  stopping). Active session; next step **TRACE-1** (per-vehicle SUMO-oracle diff in `jyArm 2`).
  `docs/F3-SESSION-LOG.md`, `docs/DENSITY-DIFF-HARNESS-*`.
- **F4b — zero-overlap invariant** (deferred until F3).

**Cleanup / perf (low priority, all in TASKS-TODO):** `MaxCrowdDiscs` 256→64 (measured 64 suffices);
`OrcaCrowd.QueryNear` grid (only if out-of-zone-blindness is fixed — the two are coupled); one home for the
vehicle-pose convention (`VehicleObb.cs` vs `VehicleFootprint.cs`); a stale handoff brief citing diagnostics
that aren't on main; live-city test env-var isolation.

---

## 2. Coordinator role
Several `claude/*` sessions work the live-city cluster in parallel. Keep them de-conflicted:
- **The board is `docs/TASKS-TODO.md`** (keep it SHORT) + **`docs/TASKS-DONE.md`** (move finished detail
  here). The **"In-flight by session"** table + **`docs/COORDINATION-livecity-realism-sessions.md`** carry
  each session's branch, scope, and **no-touch** list — keep them current as sessions start/finish.
- **Rules:** one owner per mechanism; edit your own method/region in shared files (`LiveCitySim.cs` wiring,
  `OrcaCrowd.cs` lifecycle vs external-obstacle methods); **do not change shared contracts**
  (`ICrowdFootprintSource` / `PedLodManager.HighPowerFootprints`) without pinging the consuming sessions —
  the LOD session *produces* the footprint source, the car sessions *consume* it (a produce/consume seam).
- When you route or finish work: tick the checkbox, update the table, add the design/handoff-doc reference,
  and drop the completed detail into `TASKS-DONE.md`.

---

## 3. Show the owner the live-aim state — ZIPPED replay (PROVEN recipe)
The owner's file-delivery channel caps at **30 MiB**. A dense/long replay HTML exceeds it, so **zip it** —
the JSON compresses ~5× (measured). The owner unzips and opens the `.html`.

```bash
# Sim.LiveCity.Tests is not in the sln, but Sim.Viz is — build it:
dotnet build src/Sim.Viz -c Release
# ~1000 peds, ~300 cars, N steps (dt=0.5 s -> N/2 seconds of sim):
LIVECITY_PEDS=1000 LIVECITY_CARS=300 \
  dotnet run --project src/Sim.Viz -c Release --no-build -- --live-city-demo <out>.html 320
# zip (owner unzips -> opens the .html):
( cd "$(dirname <out>.html)" && zip -9 <out>.html.zip "$(basename <out>.html)" )
# deliver <out>.html.zip via SendUserFile (display:attach).
```
**Measured sizes:** 1027 peds / 294 cars / **160 s** (320 steps) → **47 MB html → 9.7 MB zip**. Doubling to
**320 s** (640 steps) → ~95 MB → ~20 MB zip (still under 30 MiB). If a run would exceed ~28 MiB zipped,
shrink via: fewer steps (duration), lower `LIVECITY_PEDS`/`LIVECITY_CARS`, or lower `RenderHz` in
`VizReplayOptions` (default 10; the player Catmull-Rom-interpolates so 6 Hz still looks smooth).

**What the replay is:** the REAL `LiveCitySim` + `LiveCityConfig` driven through `VizReplayBuilder`, DR-smoothed
exactly like the 2D/3D viewers (`DrClock` + `KinematicReconstructor` for cars, `PedRemoteReconstructor` for
peds) — **a fix verified in this replay transfers to the City3D/raylib demo.** Colours: **grey** = low-power
ped, **orange** = ORCA/high-power, **yellow** = paused; boxes = cars. **Click a car → amber ring + its
`__vehN` id** (matches diagnostic trace names). Env knobs: `LIVECITY_CARS`, `LIVECITY_PEDS` (spawn rate
auto-scales), `LIVECITY_YIELD`, `LIVECITY_LCMIN`, `LIVECITY_MERGEGAP/MERGEDEFER`.

---

## 4. Key files & tools
- **Replay/demo:** `src/Sim.Viz/Program.cs` (`RunLiveCityDemo`, `--live-city-demo <out> [steps]`;
  `--live-city-pedtrace` = ped-LOD diagnostic), `VizReplayBuilder.cs` (the one DR builder; emits `VehIds`
  for click-to-identify), `LiveCitySource.cs`, `Payload.cs` (`ScenePayload.VehIds`), `template.{html,js}`
  (Canvas player; amber click-ring).
- **Sim host:** `src/Sim.LiveCity/LiveCitySim.cs`, `LiveCityConfig.cs` (`LIVECITY_*` env knobs).
- **Car↔ped engine seam:** `Engine.CrowdSource = Composite(PedLodManager.HighPowerFootprints,
  CrossingOccupancySource)`; `Engine.CrowdLongitudinalConstraint` (brake) + the B-guard
  (`src/Sim.Core/VehicleFootprint.cs`, `Bridge/WorldDiscQuery.cs`, nearest-first `QueryNear`);
  `Engine.MaxCrowdDiscs` (=256); `Engine.SuppressHeldCrowdSwerve` (Task A).
- **Ped LOD:** `src/Sim.Pedestrians/Lod/` (`PedLodManager`, `InterestSource`), `Demand/PedDemand.cs`.
- **Diagnostics ON MAIN:** `--live-city-pedtrace`. (My legacy `--live-city-{yieldtrace,orcatrace,cartrace}`
  + the `--live-city-drcheck` referenced in some briefs live on **unmerged** session branches
  (`claude/livecity-realism-fixes`, car-yields-ped) — port selectively if you need them; don't assume
  they're on main.)
- **Docs:** `TASKS-TODO.md`, `TASKS-DONE.md`, `COORDINATION-livecity-realism-sessions.md`,
  `LIVE-CITY-REALISM-{1-2-DESIGN,AB-DESIGN,ATTEMPT-LOG}.md`, `LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`,
  `LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md`, `F3-JUNCTION-OVERLAP-HANDOFF.md`,
  `LIVE-CITY-CAR-YIELDS-PED-{DESIGN,HANDOFF}.md`, `DENSITY-DIFF-HARNESS-*`, `F3-SESSION-LOG.md`.
