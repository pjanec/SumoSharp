# DEADLOCK-RING-DESIGN — detecting and breaking mutual-block rings

**Status: DESIGN FOR OWNER REVIEW — no code until signed off.** Owner ask (Aug 1, with the
Geneva interchange screenshot): two turning streams crossing and blocking each other plus a fan
of lanes behind them — *"such a deadlock needs detection and some way to break."*
Grounding data: the 3D session's rerouting-off HEADSTUCK capture (7 standoff chains, every
blocker stopped dead ON an internal lane; two pairs provably durable across 20+ s report
intervals; 3 of 5 roots hidden one hop further downstream — the reporter now follows two hops).

## 0. What a ring is, in this engine's own terms

Every binding constraint that yields to a specific vehicle already records WHO it yielded to —
`blockerIdx` → `Engine.BlockerEntityIndexes` (Entry 37's chain-diagnostic surface, already read
by the LIVECITY-CHAIN/HEADSTUCK witnesses). A gridlock seed like the photographed interchange is
a **cycle in that blocker graph whose members are all stopped**: A waits on B waits on C waits on
A (directly, or through leaderFollow queue segments). Today nothing traverses that graph inside
the engine; the patience escape (`IgnoreJunctionBlockerSeconds`) cuts single EDGES blind — it
fires on any foe that stood ≥60 s whether or not a ring exists, and it only covers the
junction-yield arms; the observed durable chains are held by `crossJxnLeader` and `leaderFollow`
binders it never touches.

## 1. Stage D1 — DETECTION + WITNESS (diagnostic only, no behavioural change)

End-of-step pass (gated `LIVECITY_WITNESS`/host flag; skippable cost):

1. Collect stopped vehicles (speed < 0.1) with `blockerIdx >= 0`; build the sparse successor map.
2. Walk each unvisited node with the standard colour-marking cycle scan (O(n), no allocation
   beyond two reusable int arrays); a cycle = a candidate ring; members' consecutive-stop times
   (`WaitingTime`, already tracked) give the ring's AGE = min member WaitingTime.
3. Report rings with age ≥ 10 s: `LIVECITY-RING: age=.. size=.. members=[defId lane@pos binder/arm ...]`
   — the deadlock the owner photographed becomes a named, counted, aged object instead of a scene.

**Blocker-attribution gaps to close while wiring D1** (each constraint knows its foe; several
never set `blockerIdx`): `leaderFollow` (same-lane leader — the 3-of-5 hidden-root class),
`crossJxnLeader` (has the leader in hand), `keepClear` (the first stopped vehicle its scan
found). All diagnostic-surface writes, no trajectory reads.

**D1 success conditions:** rings reported on the saturated demo (800-car smoke) and visible in
the owner's Geneva console; zero trajectory change (byte-identical smoke streams with the pass
enabled); cost < 0.5 ms/step at 4000 cars.

## 2. Stage D2 — BREAKING (gated, only after D1 numbers exist)

Principles: no teleports, no interpenetration (the artefact ladder is binding); deterministic;
minimal intervention — ONE member per ring, chosen by the existing total order.

1. A ring CONFIRMED for ≥ `RingBreakSeconds` (proposal: 20 s — far below the blanket 60 s
   patience, because a proven cycle cannot self-resolve) elects its breaker: the member that is
   INSIDE a junction with the earliest `JunctionEntryTime` (the `IsLeaderByEntryOrder` chain —
   total, so exactly one), falling back to the ring member closest to its exit.
2. The breaker gets a per-entity, per-ring **release**: its binding constraint EDGE (the one
   pointing into the ring) is relaxed to the corridor-follow form — creep forward along its own
   path while physical-occupancy geometry still forbids body contact (the jyArm-7/8 machinery:
   it may advance INTO gaps, never THROUGH bodies). Physically: one stream inches through the
   interlock, which is what real drivers do. The release ends when the breaker's blocker edge
   leaves the ring or the breaker clears its junction.
3. If the breaker cannot move at all within `RingBreakSeconds` more (geometry truly wedged —
   bodies interlocked), escalate to the NEXT member in the order; a full cycle of failed
   escalations reports `LIVECITY-RING-STUCK` (the owner sees an honest "this one is geometric")
   rather than silently teleporting.

Gate: `LIVECITY_RINGBREAK` (host) / an engine property, OFF by default until D1 data + a
battery/hour-horizon/smoke ladder pass; the crossing/bay-arm honesty gates stay independent.

## 3. Stage D3 — validation ladder

The standard four surfaces (classifier both nets, batteries both arms, 800-car smoke, hour
horizon) plus: ring count/age distribution BEFORE vs AFTER on the saturated smoke (the D1
witness is the instrument), and the owner's Geneva interlock site specifically. Goldens
byte-identical throughout (everything is gated; D1 is print-only).

## 4. What this deliberately does NOT do

- No global "restart the junction" resets, no vehicle removal, no SUMO teleports.
- No breaking of PHYSICAL interlocks where bodies already overlap-block (D2.3 reports them; the
  upstream fix is the overlap-prevention work that keeps shrinking that class).
- Not a substitute for rerouting/keepClear — those reduce ring FORMATION; this handles the
  residue.
