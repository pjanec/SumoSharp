# Follow-up: traffic-light approach throughput / flow-rate residual

**Status:** open, tracked follow-up — **not** a merge blocker. The serve-path drop-in was accepted
GREEN by the Geneva acceptance seat (see `docs/SERVE-PATH-PLAN.md` top section); this is the one
minor residual noted at acceptance, to return to in a later performance/parity pass.

## What it is

After the P2-G junction fixes (Bug-1 `<routing>` config parsing, Bug-2 RBL traffic-light exclusion,
Bug-3 red-held-foe `WillPass=false`), SumoSharp no longer gridlocks at traffic-light approaches — the
progressive-halting failure is gone and on-lane halting tracks vanilla. What remains is a **flow-rate /
throughput gap**: SumoSharp clears somewhat fewer through-trips per unit time than vanilla, and its
fair (non-sink) mean relative speed sits a little below vanilla's. The vehicles involved are **moving,
not stalled** — it is a tempo difference, not congestion.

## Measured magnitude

**Real box (Geneva acceptance re-run, 1000 s window):**
- Non-sink through-trips cleared in-window: **vanilla 52 vs SumoSharp 38** (~27% fewer; the ~14
  difference are still in transit at the cutoff, not stuck).
- Fair mean relative speed: **vanilla 0.838 vs SumoSharp 0.725**.
- Non-sink on-lane halting tracks vanilla the whole run (both ≤14, no climb) — confirming this is
  throughput, not gridlock.

**Synthetic witness (`scenarios/_repro/synthetic-junction2`, controlled, fresh binary):**
- Peak on-net halting **85 (SumoSharp) vs 45 (vanilla)** — roughly 1.9×, down from 101 pre-fix.
- Mid-run arrivals lag vanilla by ~6–8 trips (down from ~27 pre-fix).
- On a **minimal 2-car TL case**, SumoSharp already reproduces vanilla's **lane-timing exactly**
  (green permissive left crosses `:C_5_0` at t=9, same as vanilla), with only a **~1.7 m/s
  approach-speed transient over 2 steps** that converges to an exact match by t=11 (SumoSharp slightly
  *faster*, not more cautious).

## Why it's hard to localize right now

On the minimal case the behavior is already at vanilla lane-parity, so there is **no single dominant
mechanism** to point at on the synthetic — the real-box gap looks like the **accumulation of many
small per-approach speed transients** across ~10 TL junctions over 1000 s, and/or second-order
effects (lane choice, insertion order, permissive-green approach-speed profile). A clean next step
needs a **real-box halting/flow trajectory** (geometry-free is fine) to see *which* junctions/movements
accumulate the lag, since the synthetic no longer isolates it.

## Candidate mechanisms to examine (in a later pass)

1. **Permissive-green approach-speed profile.** The minimal case shows SumoSharp entering the junction
   ~1.7 m/s off vanilla for ~2 steps on a `'g'` permissive left. If the minor-link cautious-approach
   arm (`couldBrakeForMinor`, `Engine.cs` ~6196, keyed on the static `request.Response` "is-minor"
   test) differs from SUMO's live-`havePriority()` treatment at TL links, small per-approach speed
   errors would accumulate into the observed flow-rate gap. Compare against SUMO's
   `MSVehicle::planMoveInternal` couldBrakeForMinor for a TL `'g'`/`'G'` link.
2. **Internal-junction (cont-turn) traversal speed.** Cont turns traverse `:C_*` internal lanes; a
   slightly different internal-lane speed/accel would slow turning movements specifically.
3. **Gap-acceptance headway at permissive greens** — whether SumoSharp accepts merge/cross gaps at the
   same threshold vanilla does.
4. **Insertion-order / lane-choice** second-order effects that shift where queues form.

## Guardrails for any fix here

- **Parity is the iron law.** Any change must keep all committed goldens byte-identical (or be gated
  behind an explicit opt-in fast-mode flag). Several of these arms are documented as byte-identical to
  committed priority-junction scenarios (11-priority-junction, 19-onramp-merge) — do not regress them.
- This is a likely candidate for the **performance-optimization pass** the owner anticipates; treat
  throughput parity and speed parity together (a faster-but-wrong flow is still wrong).

## Pointers

- Acceptance record + halting table: `docs/SERVE-PATH-PLAN.md` (top).
- Synthetic witness + controlled A/B/C numbers: `scenarios/_repro/synthetic-junction2/DIFF-SUMMARY.md`.
- The three landed fixes: commits `689463e` (Bug-1+2), `9cd61b8`→`299b17f` (Bug-3, generalized).
