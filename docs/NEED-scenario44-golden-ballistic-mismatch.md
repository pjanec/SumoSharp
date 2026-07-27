# NEED — scenario 44's golden was generated with BALLISTIC integration (config missing the Euler pin)

**Found by:** F3 session, while testing whether `scenarios/44-multilane-junction-turn` is a usable repro.
**Scope:** `scenarios/44-multilane-junction-turn/{config.sumocfg, golden.fcd.xml}` — test corpus, not engine.
**Severity:** HIGH for anyone trying to use scenario 44 as an anchor. The golden is unusable as-is.
**Verified with the real SUMO 1.20.0 binary** (the pinned version), not inferred.

## The defect

`scenarios/44-multilane-junction-turn/config.sumocfg` **omits `<step-method.ballistic value="false"/>`**.

SUMO's default integration is **ballistic**, so the committed golden was produced under ballistic
integration — while the engine implements **Euler**, which `CLAUDE.md` names as phase 1's only integration
mode ("Euler integration ... set in each scenario's config").

Every other committed scenario pins the flag. Of 80+ scenario configs plus the `_diag/` set, only
**scenario 44** and **`_diag/c4vii-willpass-grid`** omit it. Even the two scenarios that deliberately
exercise ballistic mode (21 and 42) pin it explicitly to `true`. So this is an omission, not an intent.

## Evidence (reproduced with `/usr/local/lib/python3.11/dist-packages/sumo/bin/sumo`, v1.20.0)

| run | `vN` pos at t=1 | matches |
| --- | --- | --- |
| `sumo -c config.sumocfg` exactly as committed | **1.300** | reproduces `golden.fcd.xml` **byte-for-byte** (only the header timestamp differs) |
| same + `--step-method.ballistic false` | **2.600** | matches the engine's Euler output exactly |

`1.300 = 0.5 · 2.6 · 1²` is the trapezoidal/ballistic first step; `2.600` is the Euler one. Cross-check:
`scenarios/_diag/cont-turn-sequence`, built from the **same net**, pins `ballistic=false` and shows
`pos=2.600000` at t=1.

## Consequence

Unskipping `RungC4viiMultilaneJunctionParityTests` as-is produces a **spurious divergence from step 1**:

```
IsMatch=False, FirstDivergenceStep=1
  lane  maxAbsError=1     rmse=0.29
  pos   maxAbsError=187   rmse=43.9
  speed maxAbsError=8.11  rmse=2.14
  presence: ExtraStep vN@34; MissingStep vS@33-37; MissingStep vW@36-39
```

Divergence begins at t=1, long before any vehicle reaches the junction (steps 17–25), so **none of it is
attributable to the junction bugs the anchor was built to pin**. A 187 m position error would swamp any real
signal.

## Fix

1. Add `<step-method.ballistic value="false"/>` to `config.sumocfg`, matching every other scenario.
2. Regenerate `golden.fcd.xml` (and `golden.state.xml`) via `scripts/regen-goldens.sh` with SUMO **1.20.0**
   — available at `/usr/local/lib/python3.11/dist-packages/sumo/bin/`. **Put that `bin/` first on `PATH`:**
   bare `sumo` resolves to apt's 1.18.0, which is not a valid parity anchor.
3. Update `provenance.txt` (it is pinned to baseline `f378d3a` and is now stale in two ways — see below).
4. Check `_diag/c4vii-willpass-grid` for the same omission.

## Related finding — the skip banner is STALE

While measuring, the documented bugs **A** and **B** were found **not to reproduce at HEAD**:

| provenance claim | measured at HEAD |
| --- | --- |
| (A) left-turn internal path collapses to `:C_3_0`, skipping `:C_16_0` | vN traverses `:C_3_0` → `:C_16_0`; vS traverses `:C_11_0` → `:C_17_0` — **correct** |
| (B) spurious `CE_1→CE_0` change strands vN at pos 189.6, never arrives | vN **arrives at step 34**; never touches `CE_0`; no stopped run at all |
| — | **4/4 vehicles arrive** (vS@32, vN@34, vW@35, vE@37) |

`ContTurnSequenceDiagTests` is green, so a fix for the simple cont-turn case evidently landed after
`f378d3a`. **Scenario 44 is therefore NOT a repro of the cont-turn defect any more**, and its
`[Fact(Skip=...)]` reason plus `provenance.txt` should be rewritten to say so.

Structural note: only **2 of the 4** left turns in this net are actually two-stage
(`NC→CE` via `:C_3_0`→`:C_16_0`, and `SC→CW` via `:C_11_0`→`:C_17_0`); `EC→CS` and `WC→CN` use a single
19.35 m internal lane. So this anchor is mild for cont-turn purposes by construction.

## Do NOT treat the junction-entry timing difference as a finding yet

The engine admits vN+vS together at step 17 and vE+vW together at 19, whereas the golden staggers
17 / 19 / 21 / 21. **This comparison is not valid**: it pits Euler engine output against a **ballistic**
golden, so every timing difference is confounded by the integration mismatch. Re-measure only after the
golden is regenerated per the Fix above; the discrepancy may vanish entirely.

## Instrument

`tests/Sim.ParityTests/Scenario44DefectDiagTests.cs` (always-passing diagnostic) prints the per-step
`step | vehId | laneId | pos | speed | BindingConstraint | JunctionYieldArm` trace, the internal-lane
sequence per vehicle, stopped-on-internal-lane runs, arrivals, and the golden comparison used above.
