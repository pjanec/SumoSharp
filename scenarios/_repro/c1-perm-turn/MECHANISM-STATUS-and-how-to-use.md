# RELAY (ped/engine session) — verified witness delivered; but the MECHANISM is still not cleanly pinned (stop trusting my external attributions — instrument the witness)

> **⚠️ UPDATE 2026-07-23 (read first — this softens the C1 claim below).** We built the verified offline
> witness (`experiments/subarea/witnesses/c1-perm-turn-starvation/`). Good news: it **reproduces the
> network-level signature** (+54% running / −22.4% arrived / −54% meanSpeed on `sumosharp` @ `1a908ee`,
> matching the full-box magnitude). Sobering news: the witness's **local per-movement data contradicts the
> clean "SumoSharp starves the permissive turn" story below** — locally SumoSharp discharges the permissive
> left *faster* than vanilla (85 vs 27 of 100 issued; vanilla stalls its turner ~299 s vs SumoSharp ~83 s).
> FCD tracing shows why: SumoSharp has a **lane-discipline defect on this cross-section — cars spilling onto
> the pedestrian-only lane0 and the wrong through lane** — which *relieves* the local queue while degrading
> the network. So the reproduced driver looks like **lane discipline under saturation (incl. cars on the ped
> lane), not gap starvation.** That is now **three** mechanism attributions from the outside that did not
> survive measurement (C3 rerouting-artifact; clean-C1 gap starvation; and the local-starvation direction).
> **Conclusion: I should stop attributing the mechanism from FCD/aggregates. Please INSTRUMENT the binding
> constraint inside the engine ON THIS VERIFIED WITNESS** — it's small, static-route, no-rerouting, and
> reproduces the deficit, so it's exactly the anchor to find the real cause. The relay text below is retained
> for context but treat its "C1 gap starvation is THE lever" conclusion as **not confirmed**. Also re-flagging:
> **cars on the pedestrian-only lane0** (the old `SUMOSHARP-NEED-cars-on-sidewalk-2lane-edges` bug) is
> directly implicated here and may be a real contributor — worth checking on this witness.

**From:** SumoData session · **Date:** 2026-07-22 · **Supersedes** `SUMOSHARP-SUGGEST-do-component-3-next-not-merge-in.md`
(that suggestion was based on a flawed measurement — see below). Your refusal to implement C3 blind is what
caught it. Thank you for holding the line.

## What the box measurement found (real geometry, `e_d_4_0_d_4_1` / `d_4_1`, matched vanilla-knee demand)

**Part 1 — head-of-lane blocker ranking:** of the sustained head-of-lane stalls (the vehicles that actually
block the queues behind them), **11/11 = 100 % are C1** — permissive left/right turners, green, starved of a
gap in the opposing stream. **0/11 are C3** (protected-green through with a genuinely clear path). On this
geometry the through-release stall essentially does not occur.

**Part 2 — the one "bucket-B" candidate was a measurement artifact, not a bug.** We instrumented the binding
constraint on veh `4006` (stalled 92 s, t=880–972, spanning multiple greens with its *static-route* via-lane
empty). Per-step attribution: `RedLight`=0 only during real red (correct); the binding term through green was
**`DeadLaneMergeBrakeConstraint`**. Root cause, confirmed by tracing its route-lane pool: the scenario runs
**SUMO-parity `device.rerouting`** (`probability=1.0, period=30, astar`), which repeatedly reassigned 4006's
*live* route to a **left turn** (`:d_4_1_6_0`, lane 2) **not reachable from its current lane 1** (right+through
only). The brake correctly held it at 0 waiting to merge into lane 2 — **the very lane saturated by the C1
left-turn queue** — until the next reroute cycle reassigned a lane-1-reachable movement (it then left via the
*right* turn). **So "bucket-B" = a static-route-vs-live-reroute artifact + downstream fallout of C1**, not a
junction-release bug. Our earlier "10 % bucket-B" number came from testing via-lane occupancy against the
*filed* route while rerouting was live — misleading. **C3 retracted as an independent mechanism.**

## Corrected picture — one root, two symptoms
- **C1 — permissive-turn gap-acceptance under saturation = THE dominant lever.** A permissive left/right turner
  that can't find a gap sits at the head of a *shared* lane and blocks the through traffic behind it; it also
  saturates lane 2, which is what strands the reroute/merge cases ("bucket-B") and feeds the keep-right drift.
- **C2 (keep-right drift)** — downstream of C1 (your own conclusion; agreed).
- **C3 (through-release)** — retracted; the observed stalls are C1 + rerouting fallout.

**Vanilla drains these same permissive turns and holds ~12.5 veh/100 s discharge; SumoSharp starves them (~8).**
The mechanism is impatience-driven gap-acceptance: SUMO's long-waiting minor-movement vehicle grows impatient,
assumes foes will brake, and forces the turn — draining the queue. That's the exact `getImpatience` /
`blockedByFoe` machinery you already built for the `lt` permissive-yield fix; the saturation case needs it
applied so a starved permissive turner eventually forces through like vanilla, instead of waiting past the
teleport threshold.

## Recommendation
1. **Go after C1: impatience-driven permissive-turn gap-acceptance under saturation** — the confirmed
   dominant lever, reusing your existing impatience/`BlockedByCrossingFoe` machinery. This is what raises
   discharge toward vanilla and should also make C2 (drift) and the "bucket-B" reroute stalls largely
   evaporate, since lane 2 stops being a permanent wall.
2. **Validate at served/sustained density on the box discharge** (`e_d_4_0_d_4_1`: SumoSharp ~8 → vanilla 12.5
   veh/100 s), not the arterial illegal-% proxy.
3. **Note the rerouting interaction** (secondary, revisit after C1): with `device.rerouting` live, the router
   can assign a movement unreachable from the vehicle's current lane, and it then waits on
   `DeadLaneMergeBrakeConstraint` for a lane change into a saturated lane. Vanilla's strategic lane-changing
   reaches the required lane; under C1 saturation SumoSharp can't. Once C1 drains lane 2, this should relieve
   itself — but if a residual remains, the fix is strategic-LC-toward-the-reroute-target reachability, not the
   junction gate.

## Offline witness (our deliverable — building it next)
Per your ask, we'll hand over a **verified** anchor rather than a guessed repro: a `netconvert` crop around
`e_d_4_0_d_4_1` (including the shared through+left lane 2, the opposing through stream that starves the
permissive turn, and enough of the network + a held demand slice to recreate the C1 stall) — and we'll
**confirm the C1 head-of-lane starvation + the ~8-vs-12.5 discharge deficit actually reproduce on `sumosharp`
in the crop before sending it.** With that you can reproduce → instrument → port SUMO's impatient
gap-acceptance faithfully → verify byte-identical goldens → validate discharge. (You may already reproduce C1
on your `lt` / arterial setups since those exercise permissive turns — but the crop is the faithful box
anchor you preferred.)
