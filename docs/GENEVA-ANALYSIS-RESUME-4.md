# GENEVA-ANALYSIS-RESUME-4 — state after the Entry 59–64 round (supersedes RESUME-3)

**Predecessors:** RESUME-3 (Entries 48–58: partials, ring break, crossing/merge physical-occupant
fixes — read its header UPDATE block for that state). Trail: `JUNCTION-REALISM-SESSION-JOURNAL.md`
**Entries 59–64** (every fix entry has BEFORE predictions and AFTER measurements).

## UPDATE (2026-08-02, post Entries 65-66 — read this first)

**MERGED TO MAIN: PR #20, merge commit `a6bf81f` (no squash), CI green.** Since this doc's body:
Entry 65 (DIAGSTOP witness v2 + E1 closure widening; std-arm diagonal exposure 67→39 = −42%,
ped-heavy flat with residual named; owner verdict "diagonal reduced to acceptable state") and
Entry 66 (HIREALISM pass-through gate, owner-requested: X1 `forbidPassThrough` on all six
ignore-blocker sites, `SetHighRealismRegions`, `LIVECITY_HIREALISM_RADIUS`, 3D host follows the
camera zone, `CITY3D_HIREALISM=0` kill switch; OFF arm byte-identical, ON arm −3.7% arrivals =
accepted honesty cost). CI determinism pin corrected to `A134ED3716DDE7BC` (was rotted at
`BF3794A4704BCD79` since Entry 54). ParityTests now **783**/5. §2's items 1-3 are DONE; next
queue: §2 item 4 (the veh1762 keepClear chain — wedge now aged to 1396 s in the gate-ON arm),
then :34564; owner 3D verdict on the gate (design success condition 4) pending next session.

## 0. Engine state (verify before believing)

Branch **`claude/sumosharp-traffic-bugs-g1y9hl`**, all pushed (head = the Entry 64 BEFORE
commit; `git log` is the authority). **main = `8dba2ac`** (owner-ordered merge of Entries
48–59); the branch is AHEAD of main by Entries 60–64 — the owner fast-forwards main on request
(`git push origin HEAD:main`, clean ff). Gates verified at every commit of this round: full
`dotnet test -c Release` green (ParityTests 782/5 goldens byte-identical, LiveCity 92/92,
Peds 324, Host 6, Viewer.Motion 19, DotRecast 2); `Sim.Bench` hash **`A134ED3716DDE7BC`**
par==single — NEVER moved. Geneva cut: `D:\Work\GenevaCut\geneva_city.sumocfg` (28 276 lanes).
3D: `D:\Work\BIG-master\SumoSpectacle\run-geneva-livecity.bat` after repack (rm
`~/.nuget/packages/sumosharp*` → `demos/City3D/build.sh --pack-only` → build
`demos/City3D/Viewer/Viewer.csproj` -c Release → cp Release→Debug in `.godot/mono/temp/bin`;
set `LIVECITY_F3OCCUPANCY=1` in the env before the bat).

## 1. What shipped this round (all owner-relevant, all gate-verified)

| Entry | What |
| --- | --- |
| 60 | **Class A (late queue-tail swerve), owner-approved design `docs/LANE-CHANGE-LATE-MANEUVER-DESIGN.md`**: E1 `ManeuverLacksRunway` veto (no continuous-maneuver start against a near-stopped leader without runway; sgLeft + strategic sites) + E2 (below `LaneChangeMinSpeed` mid-maneuver: abort-recenter before midpoint, complete past it). Executed late swerves 208→24, 0 sweep-throughs, arrivals flat. Decision side proven SUMO-faithful (90% of targets are short-continuation turn lanes — vanilla commits those at crawl too, just instantly). |
| 61 | Class B ped hypothesis REFUTED (0/416 crowd holds @15k peds). `LIVECITY-JXNHOLD` witness (stopped ≥10 s ON internal lanes — the population HEADSTUCK excludes). |
| 62 | **The strand-clamp WaitingTime LIVELOCK** (the permanent-wedge root): 1.42 m netconvert stub `gen_road_4504_0` has NO outgoing connection; the C4-vii-c clamp froze a car forever AND reset its WaitingTime every step → dead-lane reroute + teleport + others' 60 s ignore-blocker ALL starved (waits to 1066 s). Fix: honest `WaitingTime += dt` at the clamp + `RescueStrandedVehicles` (SERIAL post-execute sibling-snap; par==single by construction). :34991 wedge 125→0 holds. |
| 63 | Merge PHASE 1/2 got SUMO's `gIgnoreJunctionBlocker` skip (MSLink.cpp:1601 applies to ALL link leaders; inert at parity -1). veh903's hold shape cured; aggregates noise-flat (honest). |
| 64 BEFORE | Owner 3D verdict: "greatly reduced, far from eliminated; converging." Residual mechanism + next round specified (§2). |

Instruments committed this round: `LIVECITY-JXNHOLD`, `[lclate]` (late-change commits w/
neighDist), `[sg]` (speed-gain accumulator/stay-rules), `[exec]` (plan→execute seam), `[coop]`,
`[jyocc]`. All TRACEVEH/LCLOG-gated, print-only.

## 2. NEXT ROUND (Entry 64, fully specified — start here)

1. **`LIVECITY-DIAGSTOP` witness**: stopped cars (v<0.5) with an in-progress or just-completed
   maneuver — engine proxy of the owner's metric ("compare standing-car orientation vs lane
   direction as the IG renders it"). Measure the BEFORE count (standard + ped-heavy arms).
2. **E1 closure widening**: the residual diagonals come from commits behind a CREEPING leader
   (queue pulse) — E1 only guards leaders <1 m/s. Widen: commit only if the gap covers ego's
   travel through the whole maneuver assuming the leader stops NOW (leader brakeGap term).
   The rare red-light pure-lateral slide is the same mechanism at a light.
3. Re-measure DIAGSTOP + the standard success set (overlaps ±10%, arrivals ±3%, full sln +
   hash); owner 3D verdict is the closing surface.
4. Then: the 706 s wedge `__veh56 :34994_6_0 crossJxnLeader -> __veh1762 gen_road_7261_1
   keepClear/none` — TRACE veh1762's keepClear chain to ITS root first (crossJxnLeader is
   car-following, deliberately no 60 s recovery — do NOT start with a skip). Then `:34564`
   landed crossing standoffs (RESUME-3 family).

Parked/owner-owned: ped layer full redesign (owner announced: low-power peds + vanilla-SUMO ped
port with curb-wait/crossing/car-yield — ped z=0 bug deprioritized by owner); partials phase 2
(T5); honest-SUMO open-loop comparison.

## 3. Method notes that earned their keep this round

- The reasoned-hypothesis score got WORSE again: Class B's ped theory and BOTH veh282 suspects
  (CoopSpeedAdvice, recycling) died by instrument; the real root (strand-clamp livelock) was
  found only by walking the [exec] seam. Instrument-first remains the law.
- Repro env for everything here: `LIVECITY_CARS=4000 LIVECITY_PEDS=2000 (or 15000 for the
  wedge work) LIVECITY_F3OCCUPANCY=1 LIVECITY_WITNESS=1 LIVECITY_REROUTE=0` + `LIVECITY_LCLOG=1`
  for lane-change work, `LIVECITY_TRACEVEH=__vehN` for traces; deterministic per env+frames.
- `dotnet build src/Sim.Viewer` fails with a LOCKED dll while a capture runs — wait for the
  task notification, don't fight it.
- Entry 63's draft "next wedge" line was WRONG (written before the grep returned) and needed a
  correction commit — never journal an exemplar you haven't read back.
