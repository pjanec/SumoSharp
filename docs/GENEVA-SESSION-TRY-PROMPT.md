# Prompt for the SumoData / Geneva session — try SumoSharp as the serve-path engine

Copy everything in the fenced block below into the SumoData (Geneva) session. It names the SumoSharp
branch to test **before** it is merged to `main`.

---

```
You are working in the SumoData repo (the sub-area preprocessing pipeline). The SumoSharp engine team
has finished the serve/replay drop-in work and wants you to try SumoSharp as a replacement for the
vanilla `sumo` binary on a REAL produced scenario — the one piece they could not test, because the
Geneva box data is company-restricted and lives on your side.

WHAT CHANGED ON THE SUMOSHARP SIDE (branch, NOT yet on main)
  Repo:   SumoSharp (sibling repo)
  Branch: claude/sumosharp-drop-in-binary-vq7u9p
  It adds a `sumo`-compatible CLI binary called `sumosharp` that accepts the exact serve/replay flag
  shapes your pipeline shells out:
    sumosharp -c <cfg> --summary-output S.xml --statistic-output T.xml --end <N> --no-step-log true
    sumosharp -c <cfg> --tripinfo-output TI.xml --end <N> --no-step-log true
    sumosharp -c <cfg> --fcd-output F.xml --end <N> --no-step-log true
  It supports -c/--configuration, -b/--begin, -e/--end (a TIME), --fcd-output, --summary-output,
  --statistic-output, --tripinfo-output (emits SUMO-schema <tripinfo> WITH arrivalLane), --no-step-log
  (accepted+ignored), tolerates unknown flags, and implements multi-occupant parkingArea (distinct
  off-lane slots; a follower passes a parked car; departPos="stop" pulls out; long-duration stops are
  park-and-stay sinks). It is deliberately NOT named `sumo`, so it never shadows vanilla netconvert/
  randomTrips/duarouter — point your `SUMO_BINARY` override at it.

YOUR TASK — run a real produced scenario through SumoSharp and audit it
  1. Get the SumoSharp branch and build the binary:
       cd <sumosharp-checkout>
       git fetch origin && git checkout claude/sumosharp-drop-in-binary-vq7u9p
       # offline sanity: dotnet test   (should be 600 parity / 3 skipped, all green)
       scripts/publish-sumosharp.sh                 # -> artifacts/sumosharp/<rid>/sumosharp
       export SUMO_BINARY="$(pwd)/artifacts/sumosharp/linux-x64/sumosharp"   # adjust rid/path
     (Alternatively for a quick try without publishing:
       SUMO_BINARY="dotnet $(pwd)/src/Sim.Sumo/bin/Debug/net8.0/sumosharp.dll"
      but the published exe is what production should use — near-zero per-call startup.)

  2. Produce (or reuse) a REAL served scenario for a small Geneva box via your normal tools
     (netconvert/randomTrips/duarouter stay vanilla — only the ENGINE run is SumoSharp), e.g. a
     ~1.5 km box. Then run the serve/replay path with SUMO_BINARY pointed at sumosharp:
       preprocess.py --replay ...           # your usual invocation
     Confirm it runs to completion and produces the tripinfo/fcd/summary/statistic outputs.

  3. Run your audit against SumoSharp's output:
       experiments/subarea/audit_nocheat.py <sub.net.xml> <sub_parking.rou.xml> \
           <sub_parking.add.xml> <sumosharp.tripinfo.xml> [<sumosharp.fcd.xml>]
     Expect: NO-CHEATING AUDIT: PASS (0 birth / 0 death / 0 FCD violations). Parked-forever sink
     vehicles that never leave are expected to be "missing from tripinfo" — that is allowed, not a
     violation.

  4. Compare against a vanilla-SUMO run of the SAME cfg (aggregate parity, not vehicle-for-vehicle):
       run vanilla `sumo -c <cfg> --summary-output ... --tripinfo-output ... --end <N>`
       run `sumosharp -c <cfg> --summary-output ... --tripinfo-output ... --end <N>`
     Diff aggregate flow / mean speed / arrival counts / tripinfo timing. They should match within
     your usual harness tolerance.

WHAT TO REPORT BACK (this is the definitive acceptance the SumoSharp side is waiting on)
  - Did preprocess.py --replay complete on SumoSharp without error? Any flag sumosharp rejected or
    mis-handled (paste the exact argv and stderr)?
  - audit_nocheat.py verdict on SumoSharp's output (PASS/FAIL + the printed birth/death/FCD counts).
  - Aggregate flow/speed/arrival-count/tripinfo-timing delta vs a vanilla-SUMO run of the same cfg —
    within tolerance or not, with numbers.
  - Any parkingArea behavior mismatch: are internal origins/destinations kept off the visible lanes
    (no on-lane pop)? Do multiple vehicles share one parkingArea correctly? Does a through-vehicle get
    blocked behind a parked car (it should NOT)?
  - Any crash/hang, or any output-file schema difference your tools choked on.

KNOWN LIMITATIONS to keep in mind while testing (so a "difference" isn't misread as a bug)
  - parkingArea occupant→lot assignment is STATIC (load-time). It's faithful for park-and-stay sinks +
    departPos="stop" pull-out (your serve shape). A scenario that vacates a lot and lets a LATER
    arrival reuse it within the same area is out of scope (needs SUMO's dynamic reservation) — flag it
    if your produced scenarios do that, but they shouldn't per auto_parking.py.
  - <rerouter>/parkingAreaReroute are not implemented (not on the serve path).
  - Parked lateral (y) position is functional (off-lane, distinct slots), not byte-exact vs SUMO;
    longitudinal pos/lane/speed ARE strict. The audit keys off edges/lanes, not y, so this is fine.
  - departLane="free"/"random" throw (only numeric + "best" parsed) — should be unused by your routes.

If it passes, tell the SumoSharp side "definitive acceptance green on branch
claude/sumosharp-drop-in-binary-vq7u9p" so they can merge to main. If anything fails, send the exact
cfg + argv + output so they can reproduce.
```
