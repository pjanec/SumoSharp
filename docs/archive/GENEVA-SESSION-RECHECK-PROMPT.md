# Prompt for the SumoData / Geneva session — VERIFY FROM MAIN (post-merge integration check)

> **STATUS: ARCHIVED (2026-07-28)** — One-shot prompt asking the Geneva/SumoData session to re-verify the drop-in engine from freshly-merged `main`. The re-run it requested is recorded as GREEN in `docs/SERVE-PATH-PLAN.md`. Kept as a reusable recipe if the drop-in path ever needs re-accepting.


Copy everything in the fenced block below into the SumoData (Geneva) session. You already accepted this
drop-in GREEN from the pre-merge branch; it is **now merged to `main`**. This is a fresh-checkout
confidence check that `main` reproduces that GREEN. NOTE: the serve-path code on `main` is
byte-identical to what you accepted (the shim runs pure parity; the junction fixes are the same commits;
everything added since — a lane-change-mode merge, and retiring an unused cooperative-LC layer — is inert
for the serve path and kept all 622 committed goldens byte-identical), so it **should reproduce the same
numbers**. Acceptance metric is still the **clearing / halting curve**, not the teleport count.

---

```
You are working in the SumoData repo (the sub-area preprocessing pipeline). You previously ran a
ship-acceptance of SumoSharp as a drop-in for the vanilla `sumo` binary and correctly REJECTED it:
the real box progressively gridlocked (on-net halting climbed to ~89% of running vehicles vs vanilla's
~11%, mean relative speed 0.55 vs 0.84), and the teleport count massively understated that. You noted
the fast-follow had to be a PRE-MERGE blocker, and that the acceptance had to be judged on the HALTING
curve, not the teleport count. The SumoSharp side has now landed the junction fixes. Please re-run.

WHAT IS ON MAIN (already merged and accepted; this run just confirms main reproduces it)
  Repo:   SumoSharp (sibling repo)
  Branch: main   (HEAD afec614 or later -- the drop-in is merged)
  Three faithful, golden-safe fixes to the traffic-light junction handling landed. All 622 committed
  parity goldens stay byte-identical; each fix mirrors a specific SUMO mechanism:
    - Bug-1: the config parser now reads device.rerouting.* / routing-algorithm from a <routing>
             section (SUMO's canonical layout), not only from <processing>. Rerouting was silently
             inert for canonical configs before -- vehicles could not route around jams vanilla routes
             around.
    - Bug-2: the deterministic right-before-left cycle resolver no longer fires at traffic_light
             junctions (it is SUMO's RNG-deadlock-abort analogue, which only applies to uncontrolled
             equal-priority links) -- it was spuriously holding green movements a full signal cycle.
    - Bug-3: a foe held at a RED light no longer makes a crossing (green) vehicle yield to it. SUMO's
             MSLink::opened only yields to foes that currently hold right-of-way; the engine was
             yielding to a red-held foe that was still rolling toward its stop line. This was the
             dominant driver of the progressive gridlock you measured.
  Measured on a geometry-free synthetic (10 TL junctions, the SumoSharp side's stand-in for your box):
  teleports 24->10, peak on-net halting 101->85 (vanilla 45), mid-run trips-cleared gap vs vanilla
  roughly halved. NOT fully closed on the synthetic (peak halting 85 vs 45), and the SumoSharp side
  cannot see your real box -- so your re-run is the acceptance signal.

BUILD THE BINARY FRESH -- AND DO NOT RUN A STALE ONE (this bit the SumoSharp side hard)
  cd <sumosharp-checkout>
  git fetch origin && git checkout main && git pull
  git log --oneline -1        # confirm HEAD is afec614 or later
  dotnet test                 # offline sanity: 623 parity / 3 skipped, all green
  # Build the shim FRESH and point SUMO_BINARY at THIS build's output, not any older publish:
  dotnet build src/Sim.Sumo/Sim.Sumo.csproj -c Release
  export SUMO_BINARY="dotnet $(pwd)/src/Sim.Sumo/bin/Release/net8.0/sumosharp.dll"
  # (Or scripts/publish-sumosharp.sh for a self-contained exe -- but if you publish, DELETE any old
  #  artifacts/sumosharp/<rid>/ first and re-publish, and verify the timestamp. A stale self-contained
  #  publish silently running instead of your fresh build produced hours of wrong measurements on the
  #  SumoSharp side. Whatever you point SUMO_BINARY at, confirm its mtime is from THIS checkout.)

THE ACCEPTANCE MEASUREMENT -- the clearing / halting curve, not teleports
  Run the SAME real box (same net + demand + flags) through BOTH engines to t_end, emitting a summary:
    vanilla:   sumo      -c <cfg> --end <N> --no-step-log true --summary-output van.sum.xml --statistic-output van.stat.xml --tripinfo-output van.ti.xml
    sumosharp: <SUMO_BINARY> -c <cfg> --end <N> --no-step-log true --summary-output ss.sum.xml  --statistic-output ss.stat.xml  --tripinfo-output ss.ti.xml
  Then compare, over time (e.g. every 100 s), from the two summary files:
    - running    (should be near-identical -- same vehicles inserted)
    - halting    (THE metric: does SumoSharp's halting track vanilla's, or climb monotonically?)
    - meanSpeedRelative  (fair-flow speed; SumoSharp's should stay close to vanilla's, not collapse)
    - trips cleared (arrived) over time -- does SumoSharp clear the demand on vanilla's schedule, or
      lag and leave vehicles stuck?
  Note: SumoSharp counts park-and-stay SINK vehicles as "halting" while SUMO excludes parked vehicles,
  so the raw end-of-run halting has a fixed offset equal to your parked-sink count -- use trips-cleared
  and the MID-RUN halting trajectory as the un-confounded signals, and subtract the parked floor if you
  compare end-state halting directly.

WHAT TO REPORT BACK (geometry-free is fine and preferred -- summaries/statistics, never the box itself)
  - The halting trajectory table: t, van running/halting/relSpd, ss running/halting/relSpd (the same
    shape you sent last time -- that table is exactly what settles this).
  - Trips cleared over time, both engines, and the final arrived count.
  - Teleports both engines (as a secondary indicator only -- you already established it understates).
  - no-cheating audit verdict on ss.ti.xml (PASS/FAIL + birth/death/FCD counts).
  - Your verdict: is the progressive gridlock GONE (halting tracks vanilla instead of climbing to
    ~89%)? Does the residual (SumoSharp still halts somewhat more than vanilla) clear the "believable
    dense traffic" bar for the visible product, or not?

If the halting curve tracks vanilla (as expected -- the serve-path code is byte-identical to what you
accepted), tell the SumoSharp side "main verified green (HEAD afec614)" to confirm the merged source of
truth reproduces. If it does NOT reproduce -- anything different from your prior GREEN -- that points at
an environment/build discrepancy (likely a stale binary again, or a pipeline change on your side); send
the halting trajectory + trips-cleared tables (geometry-free) and the exact SUMO_BINARY path + its mtime
so the SumoSharp side can reconcile.
```
