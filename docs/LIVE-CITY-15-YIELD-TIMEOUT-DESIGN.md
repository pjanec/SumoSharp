# #15 residual fix — junction-yield timeout + SUMO teleport escape valve

**Status: IMPLEMENTED + verified.** Two opt-in, default-off engine knobs wired ON in live-city only.

**OUTCOME (measured, cap 160, 200 s):**
- The safe **junction-yield timeout** alone (impatient gap-force vs *approaching* foes) was **~ineffective**
  (arrivals 81 → 81): the dominant hold is `AdaptToJunctionLeader` — a foe *physically* crossing the
  junction (moving 7–14 m/s) — which cannot be safely overridden (would be a collision), so only the
  minority approaching-foe arm was relaxed. Kept as a correct, harmless knob (`JunctionYieldTimeoutSeconds`).
- **The actual fix = SUMO's own jam teleport** (`CheckJamTeleports`, ALREADY ported, gated on
  `TimeToTeleport>0`), switched on in live-city at **5 s**: a car jammed ≥ 5 s is lifted past the blockage —
  exactly "the driver recovers quickly." Result: **arrivals 81 → 188, stoppedFrac 0.39 → 0.10 (free flow),
  mean speed 6.9 → 10.8 m/s**, `clearStuck` ~30 → ~12–27, **overlaps ~0 (no collisions), car count stable**
  (teleport reinserts, never deletes). Parity **657/4** byte-identical (scenarios/bench never set it), bench
  hash unchanged. Tunable via `LIVECITY_TELEPORT` (5 s flows best; 10 s = arrivals 155 / stopped 0.15).
- **Tradeoff:** teleport is a forward *jump* (SUMO's own behaviour), so a genuinely-stuck car pops ahead
  rather than smoothly pulling away. The GPU session should eyeball whether 5 s vs 10 s reads best.

---

**(original design, retained):**

**Status: DESIGN → implement.** Small, opt-in, default-off engine knob. Fixes the live-city residual
where a car waits far too long yielding to junction cross-traffic (owner: "cars stuck on green while the
road is clear, every run"). Diagnosis trail: `docs/LIVE-CITY-15-ATTEMPT-LOG.md` +
`LIVE-CITY-15-RESIDUAL-FINDINGS.md`.

## What the diagnosis established (so we fix the RIGHT thing)
The genuinely-stopped green cars are **correctly yielding to REAL, MOVING cross traffic** (measured:
bound-foe speed 1.3–13.9 m/s, never a stopped/phantom foe; `egoPrio=-` = minor movements). So there is
no phantom bug and no protected-green bug. The gap vs SUMO is that SUMO's **impatience** forces a gap
after a bounded wait (and teleport breaks true deadlocks); our impatience timescale is SUMO's 300 s
default, so under saturation a minor movement effectively never forces its gap.

## Mechanism (faithful + safe)
SUMO's `MSLink::blockedByFoe` crossing arm already accepts a gap once ego is impatient enough
(`egoImpatience = WaitingTime / timeToImpatience`, ours hard-coded to `/300`). The fix: after a
**short** configurable wait, treat ego as fully impatient for the **approaching-foe** crossing yield —
i.e. ego goes, assuming the approaching foe brakes for it (exactly SUMO's impatience assumption, and
self-consistent because once ego is on the junction it becomes the approaching foe's own
on-junction leader, so that foe car-follows/yields to it).

**Safety — never a collision:** this suppresses ONLY the *approaching*-foe crossing yield
(`takesCrossingYield`). The **on-junction** arm (`AdaptToJunctionLeader`, foe physically on the
crossing) is left completely untouched — a car cannot be released into a foe that is actually in the
box. So "unblock only if the road is physically clear" holds by construction: if a foe is on the
crossing, the on-junction arm still stops ego; only when the crossing is physically clear (foe merely
approaching) does the timeout let ego take the gap. This is the owner's "if the road is clear after a
few seconds, go — like a driver who didn't notice the gap and recovers quickly."

## The knob
- `Engine.JunctionYieldTimeoutSeconds` (double, **default 0 = OFF**). Mirrors the existing demo-realism
  `Engine.LaneChangeMinSpeed` knob pattern (a per-Engine property, off by default, set by the host).
- When `> 0`: in `JunctionYieldConstraint`, the approaching-foe crossing yield is suppressed for any ego
  with `v.WaitingTime >= JunctionYieldTimeoutSeconds`. Evaluated identically in the willPass pre-pass
  and the real pass (a pure snapshot read of `WaitingTime`), so it introduces no new pre/real-pass
  divergence.
- Wired ON in live-city (`LiveCitySim`, default ~5 s; env `LIVECITY_YIELDTIMEOUT`). Left 0 everywhere
  else (scenarios, parity, bench).

## Parity / determinism argument (the iron law)
- Default 0 → the suppression term is *always false* → `JunctionYieldConstraint` is byte-identical →
  every committed golden and the bench trajectory are unchanged. This is the same inert-when-off
  guarantee `LaneChangeMinSpeed=0` gives.
- No `System.Random`; `WaitingTime` is existing deterministic per-vehicle state; the term is a pure
  function of it, so serial == parallel is preserved.

## Success conditions
1. `dotnet test tests/Sim.ParityTests -c Release` = **657 / 4** with the knob at its default 0.
2. `Sim.Bench`: `deterministic=True`, `parallel==single=True`, hash **D96213B7BB4021A7** unchanged.
3. Live-city (`LIVECITY_YIELDTIMEOUT=5`, cap 300) via the `LIVECITY-STUCKCLEAR`/`LIVECITY-GRIDLOCK`
   probes: `junctionYield`-bound stuck cars fall sharply and `ArrivedTotal` rises (toward SUMO's 110),
   while **no vehicle overlaps another** (collision check: min inter-vehicle gap on shared lanes stays
   ≥ 0) — the safety proof that we never released a car into a physical foe.
4. With the knob OFF, live-city numbers are byte-identical to today's.
