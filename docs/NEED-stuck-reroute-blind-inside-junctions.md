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

## Why the test ceiling must NOT simply be raised to 5

The `<= 2` guard protects the **default (flag-OFF) path**, which still measures 2. Raising the shared ceiling
to 5 would silently blind that guard to a real future regression on the path everybody actually runs. If an
ON-path figure needs recording, it belongs in a **separate, explicitly-labelled** test.

Also: the test's inline comment says *"current is 1"* — measured value in this checkout is **2**
(deterministic, re-run twice). The comment is stale; the guard itself is unaffected.

## Fix order (recommended)

1. **Defect 3 first** — audit for a third mis-gated `!egoOnInternal` release path. If a vehicle committed
   inside a cont turn is being held indefinitely, that is the cause and both other defects become moot for
   this scenario.
2. **Defect 1** — allow the stuck rescue (or a sibling mechanism) to act on a vehicle wedged on an internal
   lane. Today it is a hard `return`.
3. **Defect 2** — consider periodic re-evaluation rather than a 2-shot last resort, closer to SUMO's
   `device.rerouting` posture. Larger, and behavioural — needs its own parity argument.

## Success conditions

- `ContTurnInsideJunctionGate = true` yields **≤ 2** teleports on `synthetic-junction2` (ideally 0, as SUMO).
- Vehicles 95, 102, 14, 317 complete their routes rather than teleporting, as they do in SUMO 1.20.0.
- All 661 goldens byte-identical; `Sim.Bench` hash `D96213B7BB4021A7`; the other four gridlock diagnostics green.
- The existing `<= 2` guard is left protecting the OFF path, unraised.
