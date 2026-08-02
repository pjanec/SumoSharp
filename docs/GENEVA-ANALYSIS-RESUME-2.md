# GENEVA-ANALYSIS-RESUME-2 — state after the first on-site session (supersedes GENEVA-ANALYSIS-RESUME.md)

**Predecessor:** `GENEVA-ANALYSIS-RESUME.md` (the commissioning brief). This document records
what the on-site session actually did, found, fixed, and left open — read it INSTEAD of the
predecessor; trail: `JUNCTION-REALISM-SESSION-JOURNAL.md` Entries 48–50 (all with BEFORE
predictions and AFTER measurements).

## 0. Engine state (verify before believing)

Branch `claude/sumosharp-traffic-bugs-g1y9hl`, all pushed (remote renamed:
`github.com/pjanec/SumoSharp.git`). Full `dotnet test -c Release` green at head
(ParityTests 782/5 goldens byte-identical, LiveCity 92/92 incl. hour-horizon, Peds 324, Host 6,
Viewer.Motion 19, DotRecast 2); `Sim.Bench` hash **`A134ED3716DDE7BC`** (par==single) unchanged
through every commit of this session. Geneva cut on this machine:
`D:\Work\GenevaCut\geneva_city.sumocfg` (28 276 lanes — see GENEVA-HEADLESS-HARNESS.md §1).

## 1. What landed (all gate-verified, all pushed)

| Commit | What |
| --- | --- |
| `5f16593` | **Headless Geneva unblocked**: `Sim.Viewer --mode live-city --smoke --sumocfg <cfg>` now honours the flag (was parsed and silently ignored — the GENEVA-HEADLESS-HARNESS §0 blocker). The full witness instrument set now runs on the real cut with no GPU. |
| `0d41d9c` | `leaderFollow` (binder 1) records its leader as `BlockerEntityIndex` — chains follow THROUGH queue links. |
| `538b84a` | **DEADLOCK-RING D1** (design §1, diagnostic-only): `WaitingTime` exported; `keepClear` blocker attribution closed; host-side colour-marking cycle scan → `LIVECITY-RING` (age = min member WaitingTime, ≥10 s) and `LIVECITY-CHAINROOT` (acyclic chains ≥5, root waited ≥60 s) at the 20 s witness cadence. |
| `3536e4d` | **Entry-49 fix**: the Entry-40 mutual on-junction tie-break at the adaptToJunctionLeader arm had INVERTED polarity (`IsLeaderByEntryOrder` = SUMO's isLeader = "ego entered later, foe is leader"; the site skipped on the un-negated value, so the LATER entrant skipped and the earlier braked). Negated, matching the corridor-HOLD site. Gate-scoped (`JunctionPhysicalOccupancyGate`, engine default OFF). Trace-proven on the `:35019` pair. |

**Standard capture used throughout** (label: closed-loop, Geneva cut, 4000 cars / 2000 peds,
`LIVECITY_F3OCCUPANCY=1 LIVECITY_WITNESS=1 LIVECITY_REROUTE=0`, 3600 steps = 1800 sim-s,
simHz 2): saturates to stoppedFrac ~0.90, arrivals 2954 (baseline) / 2961 (post-fix). Local logs
in `out/geneva-smoke-4000-reroute0*.log` (not committed).

## 2. The measured ring landscape (D1 on Geneva — the first hard numbers)

Baseline capture: **180 LIVECITY-RING reports**; post-Entry-49: **273**. Three classes:

1. **CURED — the 2-member cont-turn interlock** (`adaptToJxnLeader` ↔ `corridorFollow`, e.g.
   `:35019_17_1@10.5` ↔ `:35019_16_0@2.8`, re-forming at IDENTICAL positions with different
   vehicles, oscillating on the 60 s IGNOREBLOCKER cadence): the Entry-49 polarity inversion,
   trace-proven (Entry 48) and eliminated by the fix (Entry 49). Peak overlap counter also
   improved 57 → 42 (the inversion had been skipping LEGITIMATE physical follows).
2. **OPEN + NOW DOMINANT — the admission ring** (Entry 50, `:35479` traced end-to-end): a bay
   ego admission-held by a foe STANDING on a plain internal lane (SUMO-faithful), that foe
   adaptToJxnLeader-following a third (SUMO-faithful), the third corridor-HELD against the bay
   queue's bodies (**the deliberate BEYOND-SUMO honesty edge — SUMO drives through this overlap;
   `collision.check-junctions=false` does not even detect it**). Grows from a 3-seed to a locked
   12-member ring aging 300+ s. Post-fix: 200 of 273 reports involve the admission arm.
   **This is the localized "why SUMO clears junctions more easily" mechanism for this class.**
3. **OPEN — the block-scale keepClear loop** (8 members through `:30143` + gen_road_726x,
   ~1120 s persistent; keepClear-involving reports flat 67 → 73 across the fix): city-block
   circular blocking; not yet traced. Ring members' keepClear blockers now attributable
   (`538b84a`) — D1 output is the map.

## 3. D2 is IMPLEMENTED and measured (owner gave the go; commit `460e2da`, Entries 51 BEFORE/AFTER)

Behind `LIVECITY_RINGBREAK` (default OFF = byte-identical; env-honoured like F3, NOT in the
forced bundle). Measured on the standard capture: ring reports 273 → 83, **age≥300 rings
140 → 0**, arrivals 2961 → **3144 (+6.2%)**, 180 breaks / 5 escalations / 0 stuckSteps;
hour-horizon flat (2554 vs 2562, stalls 0). Honest miss to close: the instantaneous same-lane
overlap proxy rose 21.0 → 25.0 mean (still below the 29.3 pre-Entry-49 baseline) — the named
follow-up is a per-lane attribution pass near released breakers. Defaults decision pending:
(a) 3D-session eyeball of released-breaker motion, (b) the attribution pass.

## 4. Instruments and traps for the next session (deltas vs the predecessor brief)

- **Headless Geneva witness runs work now** — the §0 blocker in GENEVA-HEADLESS-HARNESS.md is
  FIXED; that doc's driver matrix row for `Sim.Viewer --smoke` is stale on this point.
- `LIVECITY-RING` / `LIVECITY-CHAINROOT` are on stderr under `LIVECITY_WITNESS=1` at the 20 s
  cadence, capped 6 rings / 6 roots per report.
- The witness-snapshot determinism held bit-for-bit across capture reruns (same env + steps ⇒
  same vehicles, positions, timestamps) — TRACEVEH replays are exact.
- `IsLeaderByEntryOrder` semantics (learned the hard way): returns TRUE = ego entered LATER =
  the FOE is the leader (SUMO MSVehicle.cpp:7443-7473 debug prints literally name it). Any
  "earlier entrant skips" guard must use the NEGATED value. Sites now consistent: 7820s (fixed),
  corridor-HOLD (was correct).
- Junction 35479 (class-2 exemplar) is `type="traffic_light"` with conflicting links in TL-off
  state `'o'` — worth remembering when comparing against SUMO TL handling.
- All measurements above are CLOSED-LOOP (LiveCity demand) — capacity/throughput claims vs SUMO
  still need the open-loop density-diff harness (predecessor §3 playbook still applies, untouched
  this session).

## 5. Open items, in priority order

1. **Owner decision on D2** (§3), then implement + A/B (battery/hour-horizon/smoke ladder per
   the design's D3).
2. **Class-3 keepClear block-loop trace** (`:30143` ring members from the D1 output; the
   keepClear blocker attribution is in place).
3. **Honest-SUMO comparison on the Geneva cut** (predecessor §3: open-loop demand, honest flags,
   per-junction clearance at `:35479`/`:30143`/`:35019`) — none of it run yet; the D1 numbers
   give it concrete junctions to compare.
4. The pedestrian-amplifier class (predecessor §2 item 1) — no witness hit in this session's
   captures; keep the eye out on longer horizons.
