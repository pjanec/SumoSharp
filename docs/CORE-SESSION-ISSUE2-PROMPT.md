# Prompt for the SumoSharp sumo-core session — Issue 2: junction deadlock / jam-teleport

Copy the fenced block below into the sumo-core (micro-sim / high-density) SumoSharp session. This is
**not** a serve-path task — it is a core junction/right-of-way parity divergence. The serve-path session
is separately fixing Issue 1 (park-and-stay residency) on the same branch, so coordinate on merge.

---

```
You are working in the SumoSharp repo (C# port of SUMO 1.20.0, ECS engine, strict behavioral parity to
vanilla SUMO). A drop-in acceptance test by the SumoData sub-area pipeline surfaced a CORE micro-sim
divergence — junctions deadlock and jam-teleport far more than vanilla. This is your task. (A sibling
"serve-path" task, Issue 1 = park-and-stay parkingArea residency, is being fixed separately on branch
claude/sumosharp-drop-in-binary-vq7u9p — base your work on that branch or main per the owner; the two
fixes touch different code.)

CONTEXT: read docs/SUMOSHARP-SERVE-PATH-DROP-IN.md §7 (Issue 2). A geometry-free repro is committed at
scenarios/_repro/synthetic-parity/ (an 8x8 netgenerate grid of UNSIGNALIZED PRIORITY junctions, 2
lanes/edge, 120 m edges, dead-end fringe stubs, device.rerouting on, time-to-teleport=120, seed 42,
--end 1000). It is fully synthetic (no real geometry) and re-runnable.

THE DIVERGENCE (Issue 2), measured on that synthetic grid (vanilla SUMO 1.20.0 vs SumoSharp, identical
flags):
  jam-teleports (<teleports jam=>)          vanilla 0    sumosharp 21
  total teleports                           vanilla 4    sumosharp 21
  mean relative speed (over active steps)   vanilla 0.493 sumosharp 0.411
Real-net reference (1.5 km Geneva box): vanilla 1-2 jam-teleports, SumoSharp 33; mean rel-speed 0.84 vs
0.55; halting-at-end 51 vs 175.

WHY IT MATTERS (framing so you prioritise right): the pipeline CALIBRATES density on whatever engine it
runs, so matching vanilla's absolute capacity is NOT required — a lower knee just maps "100% density"
to fewer cars. What does NOT calibrate away and IS the bug:
  - A jam-teleport fires only after a vehicle is wedged >120 s (time-to-teleport) — that is a genuine
    DEADLOCK, not slow traffic. And each teleport is itself a VISIBLE POP (a car jumps onto a travel
    lane), the exact cheat the no-cheating audit otherwise forbids, unless RealismMask hides it.
  - A deadlocked junction looks unrealistic on camera at any density.
21-vs-0 all-`jam` teleports on identical net/demand/seed points at a junction RIGHT-OF-WAY /
GAP-ACCEPTANCE / JUNCTION-BLOCKING / LANE-CHANGE-INTO-GAP divergence, not merely conservative capacity.
Fixing it raises usable density AND removes cheat-events.

HOW TO BISECT (no real net needed):
  1. cd scenarios/_repro/synthetic-parity ; regenerate both engines' FCDs with the command in README.md
     (FCDs are omitted from the committed bundle for size). Vanilla via `sumo`, SumoSharp via the
     `sumosharp` binary (scripts/publish-sumosharp.sh), identical flags.
  2. Diff van.fcd.xml vs ss.fcd.xml and find the FIRST timestep + junction where a vehicle halts (speed
     ~0) in SumoSharp but keeps moving in vanilla. That junction is the seed.
  3. Inspect that junction's right-of-way resolution: unsignalized PRIORITY junction gap-acceptance
     (does the minor-road car take too small / too large a gap?), junction-blocking (does a car enter a
     junction it cannot clear and then wedge cross-traffic?), foe-vehicle detection, and lane-change
     into a junction-approach gap. Compare against the vendored SUMO source under sumo/src/microsim/
     (MSLink / MSLane::isInsertionSuccess / MSVehicle::checkLinkLeader / MSVehicle executeMove
     junction arms) — port the calculation order faithfully.
  4. device.rerouting load-shifting is also active (probability=1, period=30) — rule it in/out by
     testing with rerouting off; if the deadlock persists without rerouting it is pure junction logic.

ADD A REGRESSION GOLDEN: once you can reproduce a single deadlocking junction, distill it to a small
scenarios/NN-* parity scenario (a few junctions, deterministic) with a vanilla-SUMO 1.20.0 golden, and
assert jam-teleports back down to vanilla-ish (0-single-digits) and the net drains. Keep the existing
goldens byte-identical and the determinism hashes unchanged (CLAUDE.md iron law).

ALSO (minor, related): SumoSharp's --statistic-output currently emits only <teleports .../>. Filling
out the full SUMO <statistics> schema (<vehicleTripStatistics>, <safety>, <performance>, <vehicles>)
would make the SumoData side's parity checks turnkey. Nice-to-have, not a blocker for Issue 2.

DEFINITION OF DONE: Issue 2 either fixed (jam-teleports back to vanilla-ish single digits on the
synthetic scenario at a demand vanilla flows through, no frozen-junction deadlocks, mean rel-speed
within tolerance) OR diagnosed to a concrete root cause with a concrete fix plan; a distilled
regression golden added to the parity suite; existing goldens still pass; determinism unchanged. Report
back so SumoData can re-run the definitive acceptance test.
```
