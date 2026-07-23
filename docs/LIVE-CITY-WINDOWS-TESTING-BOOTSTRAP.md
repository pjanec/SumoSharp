# Bootstrap — run the LIVE-CITY viewers on a Windows box (visual testing session)

> **Purpose of THIS doc / this session's role.** You are a **testing session on a Windows desktop with a real
> GPU**. Your job is to *run the three viewers on the live-city scene and report what looks right/wrong* — the
> feature development happens in a **separate cloud session** on the SAME branch. You **pull** its fixes as they
> land, rebuild, and re-check. You generally do NOT edit code here (if you spot a fix, describe it; the dev
> session makes it). All commands are Windows/PowerShell. `.NET 8 SDK` + `Godot 4.7.1 (.NET/mono)` required (§1).
> Every flag below was read from the code on 2026-07-23 — they are accurate, not guessed.

---

## 0. The branch & the pull-updates loop (the whole point)

Everything lives on **`claude/city3d-live-city-mode-3yf4oc`**. The dev session pushes fixes there; you pull.

```powershell
git fetch origin
git checkout claude/city3d-live-city-mode-3yf4oc
git pull origin claude/city3d-live-city-mode-3yf4oc
```

**When the dev session tells you "pushed a fix" — the refresh loop:**
```powershell
git pull origin claude/city3d-live-city-mode-3yf4oc
# Raylib 2D: nothing else — `dotnet run` rebuilds automatically.
# City3D (Godot): you MUST re-pack + clear the NuGet cache (packages are a fixed 0.1.0, so a stale
#   cached copy is silently reused instead of the rebuilt one). See §3.0 — this is the #1 gotcha.
```

Report issues with a **vehicle id + time** — click any car in any viewer and it highlights + shows its SUMO
id (e.g. `veh_1234`), so "veh_1234 clips the kerb at ~t=40s" is enough for the dev session to pull a trace.

---

## 1. Prereqs (Windows, one-time)

- **.NET 8 SDK** — `dotnet --version` → `8.x`.
- **Godot 4.7.1 — the .NET/mono build, `win64` flavor.** Download manually (the repo's `fetch-godot.sh` is
  Linux-only): `https://downloads.godotengine.org/?version=4.7.1-stable&flavor=stable&slug=mono_win64.zip`
  → you get `Godot_v4.7.1-stable_mono_win64.exe` **plus a sibling `GodotSharp/` folder** (keep them together).
  Below, `$GODOT` = the full path to that `.exe`. Set it once per shell:
  ```powershell
  $GODOT = "C:\path\to\Godot_v4.7.1-stable_mono_win64.exe"
  ```
- You have a GPU → run **windowed**, no Xvfb / `--headless` / software-GL needed.
- The `demos\City3D\*.sh` scripts are bash; this doc gives the raw PowerShell each wraps (git-bash/WSL can run
  the `.sh` directly if you prefer, but the Godot binary must be the Windows build).

## 2. The data — already committed, nothing external needed

The live-city scene runs on **`scenarios\_ped\demo_city\box\`** (in-repo: `net.xml`, `scenario.rou.xml`, …).
The viewers resolve it themselves via the repo root — **you never pass a scenario path** for live-city. The
downtown "hero" block (`[2055,2055]–[2895,2895]`) is auto-cropped. Env knobs (optional, all default to the
tuned values): `LIVECITY_CARS=<n>` (concurrent car cap, default 160), `LIVECITY_YIELD=0` (A/B: turn the
crossing-yield gate off), `LIVECITY_LCMIN=<mps>`.

---

## 3. RUN — the recipes

### 3.1 Raylib 2D live-city (start here — simplest, no packaging)
Just `dotnet run`. Opens a window.
```powershell
# LIVE (real-time coupled cars + pedestrians + crossing-yield):
dotnet run --project src\Sim.Viewer -c Release -- --mode live-city

# RECORD a run to a .simrec (for replay in either viewer). --smoke = headless, fixed length, then exit:
dotnet run --project src\Sim.Viewer -c Release -- --mode live-city --record out\lc.simrec --smoke

# REPLAY that recording, with the timeline slider + play/pause/restart/speed/frame-step controls:
dotnet run --project src\Sim.Viewer -c Release -- --mode live-city --replay out\lc.simrec
```
In the window: **grey/orange/yellow discs = pedestrians** (low-power weave / promoted-ORCA / paused),
**boxes = cars** (speed-colored). **Click a car** → cyan ring + its SUMO id. The HUD (bottom-left) shows live
cars/peds/occupied-crossings counts. In replay, drag the **timeline** to scrub; Space = play/pause; ←/→ = step.
Handy flags: `--sim-rate <x>` (playback speed of the live sim), `--screenshot <png> --frames <n>` (grab a shot
then exit).

### 3.2 City3D 3D — LOCAL (in-process)

**§3.0 Build (do this once, and again after every `git pull` that touches City3D or the packages):**
```powershell
# 0) IMPORTANT: clear stale SumoSharp.* from the global NuGet cache (fixed 0.1.0 version → a prior build's
#    package is otherwise reused instead of your freshly packed one). This is the #1 "my fix didn't show up".
dotnet nuget locals global-packages --clear   # or: Remove-Item $env:USERPROFILE\.nuget\packages\sumosharp.* -Recurse -Force

# 1) pack the SumoSharp.* packages into the demo's local feed (NOTE: SumoSharp.LiveCity is new — include it):
$FEED = "demos\City3D\local-nuget"
Remove-Item -Recurse -Force $FEED -ErrorAction SilentlyContinue; New-Item -ItemType Directory $FEED | Out-Null
foreach ($p in "Sim.Core","Sim.Ingest","Sim.Replication","Sim.Viewer.Motion","Sim.Host","Sim.Pedestrians","Sim.LiveCity") {
  dotnet pack "src\$p\$p.csproj" -c Release -o $FEED
}

# 2) restore + build the Godot C# app (Debug — Godot loads the Debug build):
dotnet restore demos\City3D\Viewer\Viewer.csproj
dotnet build   demos\City3D\Viewer\Viewer.csproj -c Debug
```

**Run (live cars + pedestrians in one 3D scene):**
```powershell
& $GODOT --path demos\City3D\Viewer -- --live-city                    # default camera (framed on the block)
& $GODOT --path demos\City3D\Viewer -- --live-city --camera=overview  # zoom out (entities get small)
& $GODOT --path demos\City3D\Viewer -- --live-city --camera=close     # tight, entities largest
```

**Run (REPLAY a recording in 3D — with the Godot playback panel):**
```powershell
# first produce a recording (§3.1), then:
& $GODOT --path demos\City3D\Viewer -- --live-city --replay out\lc.simrec
```
Cars = boxes; **pedestrians = upright cylinders** colored by regime. A bottom playback panel
(Play/Pause · Restart · «Frame · Frame» · 0.5×/1×/2× · timeline slider · `t = …/…s`) appears in replay mode;
Space/←/→ work too. **Left-click a car** → a ring above it + its id (live shows the real SUMO id; a replay
labels with the vehicle handle — the name-on-the-wire only fills in on the DDS path, §3.3).
Screenshot: add `--shot=out\shot.png --shot-delay=8` (waits 8 s so the scene populates, then writes + exits).

### 3.3 City3D 3D — REMOTE (DDS, two processes)
A standalone producer publishes cars **and** peds over DDS from one process; City3D subscribes. DDS discovery
is LAN/loopback UDP multicast — make sure it isn't firewalled.

**Build the subscriber with DDS compiled in** (adds the DDS binding to the pack + a define):
```powershell
# pack must ALSO include the DDS binding:
dotnet pack src\Sim.Replication.Dds\Sim.Replication.Dds.csproj -c Release -o demos\City3D\local-nuget
# build the Viewer with the remote define:
dotnet build demos\City3D\Viewer\Viewer.csproj -c Debug -p:City3DRemote=true
```
**Producer (combined cars+peds over DDS):**
```powershell
dotnet run --project src\Sim.Host.App -c Release -- --live-city --transport dds --hz 10
```
(Its args: `--live-city` · `--transport dds|inmem` · `--hz <n>` · `--seconds <n>` / `--steps <n>`. Use
`--transport inmem` for a same-process producer self-test — it prints vehicle+ped counts and exits nonzero if
either stream is empty.)
**Subscriber (City3D remote live-city):** in a second shell —
```powershell
& $GODOT --path demos\City3D\Viewer -- --transport=dds --live-city --camera=close
```
Both cars and pedestrians reconstruct live from the wire; roads carry real elevation (Z is on the wire now),
and **click-select shows the real SUMO id** (the name is carried once-per-spawn on the wire). If nothing
appears: confirm the producer is running and multicast/loopback isn't firewalled.

---

## 4. What "good" looks like (the sign-off) — report id + time for anything off
- **Coupling:** cars visibly **stop for pedestrians on a crosswalk** and resume once it clears; peds **wait at
  signalized kerbs** on red. Re-run 2D with `LIVECITY_YIELD=0` to see the A/B difference (cars ignore peds).
- **Junction turns:** one continuous rotation, no yaw snaps; car rides the connecting-lane centerline.
- **Lane changes:** a gentle lateral ease (~1–1.5 s), not an instant sideways jump.
- **Placement:** the box sits centered on the lane (not shoved forward); no creep/jitter at a red light.
- **Peds:** the crowd weaves the sidewalks/crossings without passing through itself.
- **3D elevation:** flat on `demo_city/box` (the net has no Z) — that's expected; Z is plumbed for future 3-D nets.

## 5. Known caveats (already understood — not new bugs)
- **Dense multi-lane car overlaps** at high `LIVECITY_CARS` and **crossing tunneling** (a fast car skipping a
  crosswalk in one coarse step) are **pre-existing engine limitations**, deferred to a separate branch
  (cooperative lane-change port). If you crank `LIVECITY_CARS` high you'll see cars overlap at busy junctions —
  known. Keep it ≤ ~160 for a clean demo.
- **Entity size:** cars (4.5 m) and peds are true-size, so they're small at `--camera=overview`; use the default
  or `--camera=close` to see them clearly.
- The `--mode local` GL-teardown segfault-on-exit seen headless on Linux is **not** a concern on a Windows GPU.

## 6. First 15 minutes (suggested)
1. `git checkout claude/city3d-live-city-mode-3yf4oc && git pull` (§0).
2. `dotnet test tests\Sim.ParityTests -c Release` → **654 passed / 4 skipped** (checkout is clean).
3. Raylib live: `dotnet run --project src\Sim.Viewer -c Release -- --mode live-city` — click a car, watch a
   crosswalk. Then record + replay (§3.1) and scrub the timeline.
4. City3D local (§3.2 build, then `--live-city --camera=close`).
5. City3D remote DDS (§3.3): producer in one shell, subscriber in another.
6. Tell the dev session what looks right/wrong (id + time). It pushes a fix → you `git pull` (+ re-pack for
   City3D, §3.0) → re-check.

---
*Companion to `docs/LIVE-CITY-VIEWERS-{DESIGN,TASKS,TRACKER}.md` (the feature these viewers run). The older
`docs/CITY3D-VIEWER-SESSION-BOOTSTRAP.md` predates the live-city work (cars XOR peds); this doc supersedes it
for the coupled cars+peds goal. Flags verified against the code on branch
`claude/city3d-live-city-mode-3yf4oc` at the time of writing.*
