# NEED — mutual `AdaptToJunctionLeader` deadlock: two cars inside one junction car-follow each other forever

**Found by:** F3 session, task **T1.10**, by instrumenting the real binding constraint (only trustworthy after
the T1.8 stale-diagnostic fix).
**Scope:** `src/Sim.Core/Engine.cs` — `JunctionYieldConstraint` arm **5** (`AdaptToJunctionLeader`).
**Status:** **this is the actual reason `ContTurnInsideJunctionGate` cannot be default-ON.** It supersedes the
D1/D2 rescue-gap framing in `NEED-stuck-reroute-blind-inside-junctions.md` as the *primary* cause.
**Severity:** HIGH — a permanent two-car deadlock that only time-to-teleport resolves.

## Measured (scenarios/_repro/synthetic-junction2, flag ON)

Two of the five teleporting vehicles are a **mutual, symmetric deadlock** at junction `2336`:

| veh | lane | frozen pos | speed | stalled steps | binder | arm |
| --- | --- | --- | --- | --- | --- | --- |
| **95** | `:2336_42_0` | 1.90 | **exactly 0.000** | **121** (t=323→443) | `10 junctionYield` **100%** | **`5 adaptToJunctionLeader`, every step** |
| **102** | `:2336_3_0` | 15.99 | **exactly 0.000** | **121** (t=323→443) | `10 junctionYield` **100%** | **`5 adaptToJunctionLeader`, every step** |

**They are each other's foe**, cross-verified in both directions via the logged foe speed:
95's `foeSpeed` at t=323 = 7.875 = 102's own speed at t=322; 102's `foeSpeed` at t=323 = 4.102 = 95's own
speed at t=322. Both hit the 120 s teleport threshold at the same instant. Neither ever moves (not even a
creep — exactly 0.000 throughout).

The other two teleports are a **different, secondary** cause: vehicles **14** and **317** are held by
`7 redLight` for 31/40 steps (77.5%), the rest by `5 deadLaneMerge` / `2 crossJxnLeader`. Notably both **wasted
a genuine green window** (9–11 steps at `tl=G`) while held at 0 speed by `deadLaneMerge`/`crossJxnLeader`, then
were caught by the following red long enough to time out. Plausibly downstream casualties of the 95/102
deadlock; worth its own investigation, but not this NEED.

## Why the deadlock is structurally unbreakable in our engine

Arm 5 is plain car-following against a foe physically on a crossing internal lane. It has **no notion of
right-of-way and no escape hatch** — and that is *deliberate*. `Engine.cs:7252-7256`:

> *"Only this approaching arm is suppressed; the on-junction AdaptToJunctionLeader arm above is untouched, so
> a car is never released into a foe physically on the crossing."*

So `JunctionYieldTimeoutSeconds` (our impatience escape) applies **only to arm 6**, never arm 5. Arm 1
(`JunctionCycleHold`) has explicit right-before-left cycle-breaking; arm 5 has nothing. Once both vehicles are
mutually stopped, each sees a permanently-stopped leader and neither can restart. Ever.

That design is safe **only if something prevents the symmetric state from forming**. SUMO has three things we
lack.

## What SUMO does that we do not — THREE mechanisms, and one of them is tiny

| SUMO mechanism | Where | What it does |
| --- | --- | --- |
| **`isLeader()` entry-time ordering** | `MSVehicle.cpp:7348-7483` | *Prevents formation.* Whoever entered the junction first does not yield (tie-break: speed, then vehicle id). Exactly one of a mutual pair yields. |
| **`JUNCTION_BLOCKAGE_TIME` request revocation** | `MSVehicle.cpp:3487`, `#define JUNCTION_BLOCKAGE_TIME 5 // s` (`:119`) | *Breaks it if formed.* `\|\| leader->getWaitingTime() > TIME2STEPS(JUNCTION_BLOCKAGE_TIME)` → `setRequest = false`. **After 5 seconds.** |
| **`gIgnoreJunctionBlocker`** | `MSLink.cpp:1601` | `if (leader->getWaitingTime() < MSGlobals::gIgnoreJunctionBlocker)` — a foe waiting longer than the threshold is **skipped entirely** in `getLeaderInfo`, so it stops constraining anyone. |

### This closes the question D3 could not answer

The open question was: *why does our vehicle wait > 120 s where SUMO's equivalent stall resolves in ~10 s?*

**Because SUMO breaks exactly this deadlock after 5 seconds** (`JUNCTION_BLOCKAGE_TIME`), and we have no
equivalent at all — so we wait for the 120 s teleport. The ~10 s SUMO recovery observed for vehicle 102 is
consistent with a 5 s blockage timer plus re-acceleration. **The mechanism is identified; nothing here is a
guess.**

## Why the flag exposes it (it does not cause it)

With the corrected `egoInsideJunction` predicate, **both** vehicles on crossing internal lanes of the same
junction are correctly classified as inside it. The yield arms — which carry impatience/timeout escapes — no
longer apply to either, leaving only arm 5, which has none. So the flag does not create the deadlock; it
routes both cars into the one arm that cannot escape. The underlying gap (no `isLeader`, no blockage timer) is
**pre-existing**.

## RESOLVED (measured) — `--ignore-junction-blocker 5` fixes it

Implemented as `Engine.IgnoreJunctionBlockerSeconds` + the CLI/cfg option
`--ignore-junction-blocker TIME` / `<processing><ignore-junction-blocker value="…"/></processing>`
(`SumoShim`, `ScenarioConfig`). Measured on `scenarios/_repro/synthetic-junction2`, 2000 s, **all four cases
on the same SumoShim harness** as `LowDensityTeleportTests`:

| ignore-blocker | `ContTurnInsideJunctionGate` | teleports (total / jam / yield) |
| --- | --- | --- |
| absent (−1) | OFF — today's shipped default | **2** / 0 / 2 |
| absent (−1) | ON | **5** / 0 / 5 |
| **5** | **ON** | **2** / 0 / 2 |
| 5 | OFF | 2 / 0 / 2 |

**Vehicles 95 and 102 — which never arrived at all in the (−1, ON) case — now complete their routes**
(647 s and 587 s), and 317 improves 1100 s → 919 s. Confirmed by diffing `--tripinfo-output` arrived-vehicle
sets: `{95, 102}` is exactly the difference, and the (−1,OFF) / (5,ON) / (5,OFF) cases produce identical
arrived sets.

**Why the knob reads the right signal** (checked, not assumed): `Engine.cs:9826-9830` is
`v.WaitingTime = haltedLowAccelThisMove && !stoppedAtStopThisMove ? v.WaitingTime + dt : 0.0` — it accumulates
whenever the vehicle is halted with low acceleration and resets otherwise, with **no internal-lane-based
reset**, matching `MSVehicle::updateWaitingTime` (`MSVehicle.cpp:4081-4088`). So a car frozen inside a
junction does reach the threshold.

### ⚠ Methodology note worth keeping

A first A/B drove `engine.LoadScenario(...)` + `engine.Run(2000)` **directly** and showed the knob having
**no effect** (1→1, 4→4) with a different baseline (4, not 2). That was a **harness mismatch**, not a result:
the direct path does not go through `SumoShim`'s config/engine wiring, so its numbers are not comparable to
`LowDensityTeleportTests`. The committed test now drives the shim path and says so in its header. Do not mix
the two harnesses.

### The T1.10 blocker is resolved — and the combination is fully green

Probed with **both** `IgnoreJunctionBlockerSeconds = 5` and `ContTurnInsideJunctionGate = true` as defaults:

- **all 661 goldens byte-identical**;
- **all five gridlock diagnostics pass**, `LowDensityTeleportTests` included (the blocker);
- `Sim.Bench` hash **`D96213B7BB4021A7`**, par==single;
- `Sim.LiveCity.Tests` 48/48.

The only failure was `IgnoreJunctionBlockerTests.DefaultIsMinusOne_AndIsNeverIgnore`, which asserts the
default IS −1 and therefore *must* fail when the default is changed — i.e. there is no hidden regression.

**Both flags are nevertheless left at their shipped defaults (OFF / −1)**, because flipping them changes
outward-facing default behaviour and takes the knob away from SUMO's own default. That is an owner decision,
not a test outcome. The change is one line each if approved.

**Honest caveat on faithfulness:** enabling the knob is a SUMO-*optional* deviation from SUMO's *default*.
SUMO avoids this deadlock forming via `isLeader()`, which is still unported — so this is the pragmatic floor,
and `isLeader()` remains the faithful fix (and is independently needed by T1.6).

## Recommended fix order

1. **Port the `JUNCTION_BLOCKAGE_TIME` escape first** (`MSVehicle.cpp:3487`). Small, local to arm 5's call
   site, directly targets the measured deadlock, and is a faithful port of a named SUMO constant (5 s). This is
   the cheapest thing that could make the flag default-on.
2. **Then `isLeader()`** (`MSVehicle.cpp:7348-7483`) — prevents the symmetric state forming at all. Needs new
   per-vehicle junction entry-time state plus the entry-time → speed → vehicle-id tie-break. **This is the same
   port T1.6 needs for the true-F3 residue, so one piece of work unblocks both.**
3. Separately investigate why **14/317** sit at 0 speed through a green window held by
   `deadLaneMerge`/`crossJxnLeader`.

## Success conditions

- `ContTurnInsideJunctionGate = true` gives **≤ 2** teleports on `synthetic-junction2` (SUMO gives 0), with
  vehicles 95 and 102 completing their routes as they do in SUMO (95 arrives t=433, 102 t=497).
- A **direct** test that two vehicles mutually blocking inside one junction resolve within the blockage
  timeout — not merely an aggregate teleport count.
- All 661 goldens byte-identical, or any shift justified by a live-SUMO diff (SUMO 1.20.0 is at
  `/usr/local/lib/python3.11/dist-packages/sumo/bin/`; put it FIRST on `PATH`).
- `Sim.Bench` hash `D96213B7BB4021A7`; the other four gridlock diagnostics stay green.

## Note on the escape's parity risk

Adding a waiting-time escape to arm 5 deliberately *releases a car toward a foe physically on the crossing* —
precisely what the existing comment says it is avoiding. SUMO accepts that trade (5 s), so it is
SUMO-faithful, but it is a real behavioural change and must be measured against the full gate and the
overlap buckets, not just the teleport count. It may trade teleports for junction overlaps; measure both.
