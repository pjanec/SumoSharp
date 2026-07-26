# NEED — the stuck-reroute rescue is structurally blind inside junctions (and is one-shot, vs SUMO's continuous rerouting)

**Found by:** F3 session, task **T1.10** — diagnosing why `ContTurnInsideJunctionGate` raises teleports on
`scenarios/_repro/synthetic-junction2`.
**Scope:** `src/Sim.Core/Engine.cs` — `TryRerouteStuckDeadLane` / `TryRerouteFromDeadLane` (~:10159–10590).
**Status:** the reason `ContTurnInsideJunctionGate` cannot yet be default-ON. **Pre-existing**, not caused by
that flag — the flag merely stops masking it.

## The measurement

`LowDensityTeleportTests.SyntheticJunction2_TlPriorityVehiclesDoNotSpuriouslyTeleport`, 2000 s:

| run | total | jam | **yield** | wrongLane |
| --- | --- | --- | --- | --- |
| flag **OFF** (current default) | **2** | 0 | 2 | 0 |
| flag **ON** | **5** | 0 | 5 | 0 |
| **real SUMO 1.20.0** | **0** | 0 | 0 | 0 |

Per-vehicle (`time, vehId, lane, waitingTime, kind`):

- **OFF:** `329, 101, -217_0, 121, Yield` · `606, 238, -217_0, 121, Yield`
- **ON:** `329, 101, -217_0, 121, Yield` · `442, 95, :2336_42_0, …` · `442, 102, :2336_3_0, …` ·
  `500, 14, 249_0, …` · `892, 317, -2437_1, …`

Vehicle **101** teleports identically in both runs — an unaffected control, so that teleport is unrelated to
the flag.

**SUMO ground truth: all five of those vehicles complete their routes normally.** (SUMO run had
`--step-method.ballistic false` added explicitly, because this scenario's `sumocfg` does not pin it — the
same authoring gap found in scenario 44; without it SUMO would run a different integration method than the
engine.)

| veh | depart | arrival | duration | trip waitingTime | **SUMO rerouteNo** |
| --- | --- | --- | --- | --- | --- |
| 101 | 172 | 367 | 195 | 101 | 2 |
| 95 | 162 | 433 | 271 | 152 | **6** |
| 102 | 174 | 497 | 323 | 189 | **8** |
| 14 | 24 | 334 | 310 | 183 | 2 |
| 317 | 539 | 745 | 206 | 112 | 2 |

## Defect 1 (structural) — a vehicle wedged on an internal lane can NEVER be rescued

`Engine.cs:10169`:

```csharp
return;   // inside a junction interior -- no reroute
```

The stuck-reroute bails unconditionally when the vehicle's current lane is internal (`EdgeId[0] == ':'`). So
once a vehicle is wedged *inside* a junction, the only mechanism that could rescue it is categorically
unavailable, and it teleports at the 120 s threshold.

**2 of the 5** ON-teleports are exactly this: vehicle **95** on `:2336_42_0` and vehicle **102** on
`:2336_3_0`. `:2336_42_0` is confirmed to be the **second-stage lane of a cont turn** at junction 2336
(`from ":2336_18" to "-2337" via ":2336_42_0" state="m"`) — precisely the two-stage geometry the flag
addresses.

Vehicle 95 is the clean before/after: byte-identical through its `t=313` reroute in **both** runs; OFF it
gets a *second* rescue at `t=573` and completes; ON it wedges at `t=442` on an internal lane where the rescue
cannot apply, and teleports.

## Defect 2 (scope) — the rescue is one-shot and does not unstick the vehicle

Even where it does fire, `TryRerouteFromDeadLane` only re-plans the **future** route from the vehicle's
current (already-blocked) lane. It does not move the vehicle, and the TL/yield hold that is actually blocking
it is untouched — so `waitingTime` keeps climbing and crosses 120 s anyway. `MaxDeadLaneReroutes = 2` then
exhausts. Vehicles **14** and **317** both get a reroute (t=469; t=861/869) and teleport regardless.

Contrast SUMO: `device.rerouting` runs **continuously** (period 30 s, probability 1.0) — these very vehicles
show `rerouteNo` **2–8** across their trips. Our rescue is a narrow last resort; SUMO's is a standing
behaviour.

## Defect 3 (open question, possibly the real one) — why is the yield wait > 120 s at all?

Real SUMO's vehicle 102 *does* stall to ≈0 for **~10 s** near the same place (t≈440–450) and recovers well
inside the teleport window. Ours waits **> 120 s**. So the interesting question is not only "why is the
rescue unavailable" but **"why does a vehicle committed inside a cont turn never get released?"**

That smells like the *same bug family* as T1.5a/T1.9: a release path that is still mis-gated for a vehicle
inside a cont turn. Both prior instances were `!egoOnInternal` gates that should have been
`!egoInsideJunction`. Worth auditing `JunctionYieldConstraint` / `RedLightConstraint` release paths for a
third instance **before** building new rescue machinery — fixing the cause beats widening the mitigation.

## D3 TESTED AND REFUTED (2026-07-26)

D3 predicted a **third** mis-gated `!egoOnInternal` release path holding a committed vehicle indefinitely.
Four further "am I committed / still approaching?" gates were found and corrected to `!egoInsideJunction`,
all behind `ContTurnInsideJunctionGate`:

| site | gate | why it looked like D3 |
| --- | --- | --- |
| `Engine.cs` cycle-hold arm | `!prePass && v.JunctionCycleHold && !egoOnInternal` | brakes to `egoDistToEntry` — a stop-line hold |
| approaching-foe `takesCrossingYield` | `!(egoOnInternal \|\| …)` | brakes to `approachLane.Length - Pos`; on a cont turn `approachLane` **is** the lane ego stands on, so at its far end the target is ~0.1 m — a standstill re-applied every step, and the teleports are classified `Yield` |
| external-agent arm | `egoOnInternal ? +Inf : StopSpeedFor(approachLane…)` | same stop-line formula |
| `foeWillNotPass` keepClear probe | `!egoOnInternal && FoeKeepClearBlocked(…)` | "am I still approaching?" |

`takesCrossingYield` was the strongest candidate — the arithmetic genuinely does produce a ~0.1 m stop target
for an ego at the end of a first-stage lane.

**Measured result: teleports remain 5 (jam=0, yield=5). D3 is REFUTED.** All 661 goldens stay byte-identical
and the T1.9 freeze fix is unaffected (demo internal-lane stopped vehicle-steps still 206 → 39), so the four
corrections were **kept**: they are the same faithful-port correction applied *consistently*, and the earlier
28-stuck episode showed that applying this fix to only some of its sites is what manufactures artefacts. But
they are a **consistency change, not a fix for the teleports** — do not read them as progress on T1.10.

**Consequence: the teleports really are D1/D2 (rescue coverage), not a mis-gated release path.** The
remaining fix order is **D1 → D2**, and the ">120 s vs SUMO's ~10 s" gap still needs explaining by something
other than these gates — most likely the vehicles are genuinely blocked (red/downstream) and our rescue
simply cannot reach them, which is exactly D1.

## Why the test ceiling must NOT simply be raised to 5

The `<= 2` guard protects the **default (flag-OFF) path**, which still measures 2. Raising the shared ceiling
to 5 would silently blind that guard to a real future regression on the path everybody actually runs. If an
ON-path figure needs recording, it belongs in a **separate, explicitly-labelled** test.

Also: the test's inline comment says *"current is 1"* — measured value in this checkout is **2**
(deterministic, re-run twice). The comment is stale; the guard itself is unaffected.

## Fix order (updated after D3 was refuted)

~~D3 first~~ — **done and refuted**, see the section above. The four gates it found were corrected and kept
for consistency, but the teleport count did not move.

1. **Defect 1 (next)** — allow the stuck rescue (or a sibling mechanism) to act on a vehicle wedged on an
   internal lane. Today `Engine.cs:10169` is a hard `return`. This covers 2 of the 5 vehicles directly.
   **Before coding it, answer the open question below** — D3's refutation means we still do not know *why*
   these vehicles wait > 120 s, and a rescue that fires without knowing that is a mitigation for an
   unidentified cause.
2. **Defect 2** — consider periodic re-evaluation rather than a 2-shot last resort, closer to SUMO's
   `device.rerouting` posture. Larger and behavioural — needs its own parity argument.

### The `Yield` label is NOT evidence of a yield — verified

`ClassifyTeleportKind` (`Engine.cs:~12359`) decides the label like this:

```csharp
if (_network.LinkByInternalLane.TryGetValue(seqLaneId, out var jl))
{
    var state = LinkStateChar(jl.Link);
    return (state >= 'A' && state <= 'Z') ? TeleportKind.Jam : TeleportKind.Yield;
}
```

It scans forward for ego's next junction link and returns `Yield` **iff that link's TL state char is
lowercase (minor)**, `Jam` if uppercase (major). **It never inspects why the vehicle waited.** This is
faithful to SUMO (`MSVehicleControl::registerTeleportYield` is classified the same way), so it is not a
defect — but it means:

> **"yield=5" only says "these 5 vehicles' next junction link is minor". It is a LABEL, not a cause.**

So the D3 framing — *"why is a **yield** wait > 120 s?"* — rested on an unfounded premise. The vehicles may
have been held by anything: a leader, a red light, a downstream jam. Any future reading of this counter must
not infer a mechanism from the bucket name.

### The open question D3 was supposed to answer, and did not

Real SUMO's vehicle 102 stalls ~10 s at the same place and recovers; ours waits > 120 s. That is **still
unexplained** — it is not the four commitment gates. Candidate next probes, in cheapness order:

- Instrument the *actual binding constraint* for veh 95 / 102 / 14 / 317 across their stall on
  `synthetic-junction2` (the T1.8 diagnostic fix makes this trustworthy now — it was not before). That names
  the arm directly instead of guessing, exactly as it did for `__veh127`.
- Check whether the blocker is a red light (`RedLightConstraint`) rather than a junction yield; the `Yield`
  teleport classification may simply be the non-jam default rather than evidence of a yield arm. Verify what
  `ClassifyTeleportKind` actually keys on before trusting that label.
- Compare against SUMO's own per-step speed for those vehicles to locate the first step where the two diverge.

## Success conditions

- `ContTurnInsideJunctionGate = true` yields **≤ 2** teleports on `synthetic-junction2` (ideally 0, as SUMO).
- Vehicles 95, 102, 14, 317 complete their routes rather than teleporting, as they do in SUMO 1.20.0.
- All 661 goldens byte-identical; `Sim.Bench` hash `D96213B7BB4021A7`; the other four gridlock diagnostics green.
- The existing `<= 2` guard is left protecting the OFF path, unraised.
