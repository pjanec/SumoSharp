# Prompt for the SumoSharp sumo-core session — Issue 2 (RE-DIAGNOSED): teleport misclassification

Copy the fenced block below into a fresh sumo-core session. Issue 2 was re-diagnosed from "junction
deadlock" to a **teleport jam-vs-yield classification / over-teleport** bug. The repro golden and the
SUMO source seam are named. Base off the serve-path branch (it carries the Issue-1/parking fixes and
the committed repro), or main per the owner.

---

```
You are working in the SumoSharp repo (C# port of SUMO 1.20.0, ECS engine, strict parity to vanilla).
Read CLAUDE.md first (iron law: parity; NO existing golden may change; determinism; offline dotnet test
must not need SUMO; port SUMO's algorithms + calculation order faithfully from the vendored sumo/). Base
your work on branch `claude/sumosharp-drop-in-binary-vq7u9p` (it has the Issue-1/parking fixes now
accepted green, plus the committed repro below).

THE BUG (Issue 2, RE-DIAGNOSED — it is NOT a junction deadlock). On a junction-realistic synthetic net,
identical scenario + flags, vanilla vs SumoSharp:
    vanilla   : <teleports total="3"  jam="0"  yield="3" wrongLane="0"/>
    sumosharp : <teleports total="75" jam="75" yield="0" wrongLane="0"/>
Congestion is EQUAL — vanilla has if anything MORE sustained (>=120 s) halts (256 vs 205). So SumoSharp
vehicles are not more stuck. The divergence is teleport ACCOUNTING + over-teleporting: SumoSharp jam-
teleports cars that are legitimately WAITING FOR RIGHT-OF-WAY at an unsignalized junction (a foe stream
has priority), at the 120 s jam cutoff; vanilla classifies those as YIELD and teleports far fewer (3 vs
75). The right_before_left variant reproduces identically (vanilla 0 jam, SumoSharp 65 jam).

REPRO (committed): scenarios/_repro/synthetic-junction/ (net/routes/parking/vTypes + both engines'
summary/statistic/tripinfo + build.py + README + FEEDBACK.md). It is the golden that GATES Issue 2 — the
uniform 8x8 scenarios/_repro/synthetic-parity grid does NOT show this (0 vs 0; it lacks the junction
micro-geometry). Regenerate FCDs with the README command if needed. Reproduce:
    <bin> -c scenario.sumocfg --fcd-output F --summary-output S --tripinfo-output TI --statistic-output ST --end 1000 --no-step-log true

THE SUMO REFERENCE (vendored, read-only). MSLane.cpp:2257-2299 (executeMovements teleport block):
  - Trigger r1: `ttt > 0 && firstNotStopped->getWaitingTime() > ttt` (getWaitingTime is the CONSECUTIVE
    wait, reset when the vehicle moves — MSVehicle::myWaitingTime).
  - Classification of the teleported vehicle:
      wrongLane = !appropriate(firstNotStopped)      -> registerTeleportWrongLane
      else minorLink = link exists && !link->havePriority()  (the vehicle's NEXT link is a MINOR link
                                                              — it is yielding to a priority foe)
          minorLink -> registerTeleportYield
          else      -> registerTeleportJam
  So jam vs yield hinges on whether the stuck front vehicle's next link HAS PRIORITY.

SUMOSHARP TODAY. Engine.cs P1F implements ONLY the jam classification: `CheckJamTeleports` (~Engine.cs:
10124) increments TeleportCountJam for every teleport (Engine.cs:933-937 says outright "total == jam",
yield/wrongLane deferred + reported 0). The consecutive waiting timer is `VehicleRuntime.WaitingTime`
(Engine.cs:~8239, resets on movement — the correct analog of getWaitingTime). `JunctionYieldConstraint`
/ `FindFoeVehicle` already model minor-link yielding in the plan phase, so the "does my next link have
priority?" signal already exists in the engine.

YOUR JOB (two parts — the second is the load-bearing one):
1. CLASSIFY: at the teleport site, decide wrongLane / yield / jam per MSLane.cpp:2273-2295 — a stuck
   front vehicle whose next link is a MINOR link (no priority) is a YIELD teleport. Surface
   TeleportCountYield / TeleportCountWrongLane (StatisticWriter already has the fields).
2. INVESTIGATE THE 75-vs-3 COUNT GAP — this is more than relabeling (a pure relabel would give
   sumosharp jam=0/yield=75, still 75 total vs vanilla's 3). SumoSharp teleports ~24x MORE cars than
   vanilla despite equal congestion. Find why. Likely candidates:
     - WaitingTime reset: does a junction-yield-waiter that inches forward on a foe-gap reset its
       consecutive WaitingTime the way SUMO's getWaitingTime does? If SumoSharp accumulates waiting
       through brief creeps, it will hit 120 s far more often. (Compare vanilla: 256 halted but only 3
       teleported — its waiters keep resetting.)
     - minor-link creep / gap acceptance: do SumoSharp's minor-approach cars advance into gaps like
       vanilla, or sit fully stopped for the whole foe stream?
     - firstNotStopped selection / appropriate(): is SumoSharp selecting the wrong teleport candidate?
   Port SUMO's actual behavior; do not just cap or relabel. The goal is SumoSharp's teleport COUNT and
   category to converge to vanilla's on the synthetic-junction golden.

ADD A REGRESSION GOLDEN: distill a SMALL deterministic scenarios/NN-* (a couple of unsignalized minor-
link junctions with a busy foe stream) with a vanilla-SUMO-1.20.0 golden; assert the teleport
category+count matches vanilla (yield-ish/low, jam ~0). Mirror how scenario 47-teleport-jam asserts
teleports.

GATES (CLAUDE.md): every existing golden byte-identical (this touches the P1F teleport path — verify 47-
teleport-jam and the junction goldens 08/11/26/27/34/38/39/40 stay green, plus determinism D1/D8);
dotnet test fully green; no tolerance loosened. On synthetic-junction: SumoSharp <teleports jam=> drops
to vanilla-ish (0-few) with yield accounting matching vanilla; no-cheating audit still PASSES; mean rel-
speed converges. Report the before/after teleport numbers vs vanilla, the root cause you found for the
count gap, and confirm no existing golden moved.
```
