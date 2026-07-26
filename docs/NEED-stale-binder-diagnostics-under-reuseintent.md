# NEED — `BindingConstraint` / `JunctionYieldArm` diagnostics go STALE under `ReuseIntent`

**Found by:** F3 session, while trying to root-cause a 95-step mid-junction freeze.
**Scope:** `src/Sim.Core/Engine.cs` — diagnostic (#15) fields, not simulation behaviour.
**Severity:** HIGH **for investigation integrity.** It does not affect trajectories, but it silently
misattributes root causes — it already sent this session down a wrong path, and it will do so again.

## The defect

Two interacting facts, both verified in code:

1. The diagnostics are written **only on the real pass**:
   ```csharp
   // Engine.cs:5183
   if (!prePass) v.BindingConstraint = binder; // diagnostic (#15): argmin of the fold, never read by sim
   ```
   and likewise `v.JunctionYieldArm` / `v.JunctionYieldFoeSpeed` inside `JunctionYieldConstraint`
   (`if (!prePass) { ... }`).

2. The **willPass / plan-fusion optimisation skips the real pass entirely**. Once a vehicle is
   fusion-eligible and did not take an approaching-foe crossing yield in the pre-pass
   (`CrossingYieldTaken == false ⇒ ReuseIntent == true`), `PlanMovements` reuses the pre-pass Intent and
   returns/continues without ever calling `ComputeMoveIntent(prePass: false)` (`Engine.cs:4955`, `:4972`).

**Consequence: while `ReuseIntent` is active, `BindingConstraint` and `JunctionYieldArm` are frozen at
whatever the last real pass wrote — indefinitely.** They report a constraint that may have stopped binding
many seconds earlier.

## Measured evidence (live-city demo, `__veh127`)

Traced with temporary instrumentation printing the LIVE values alongside the stored diagnostics:

```
t=47.50 prePass=True  lane=e_d_4_4_d_3_4_1 pos=233.023 seen=7.6470 armFired=True  jyArmThisCall=2
t=48.00 prePass=True  lane=:d_3_4_5_0      pos=3.197   seen=4.0730 armFired=False jyArmThisCall=0  prior(Binder=10,JyArm=2)
t=49.50 prePass=True  lane=:d_3_4_5_0      pos=7.169   seen=0.1010 armFired=False jyArmThisCall=0  prior(Binder=10,JyArm=2) spd=0.398
t=50.00..96.50  prePass=True EVERY call, pos=7.169, spd=0.000, seen=0.1010,
                armFired=False, jyArmThisCall=0,  prior(Binder=10,JyArm=2)  <-- STALE, never refreshed
t=97.00 prePass=True  lane=:d_3_4_20_0     pos=0.476   spd=1.154  (still stale 10/2 — already MOVING again)
t=98.50 prePass=False lane=e_d_3_4_d_3_5_1 pos=1.927   spd=5.054  <-- first real pass in ~52 s, at the NEXT junction
```

Every call for 95 consecutive steps is `prePass=True`. The stored `Binder=10 / JyArm=2` was captured around
step 92 **while still approaching with a large `seen`**, and then never updated — including for four steps
*after* the vehicle had resumed moving.

## Why this is damaging, concretely

`docs/NEED-contturn-stuck-in-junction.md` originally attributed that freeze to
`JunctionYieldConstraint`'s cautious-approach arm, on the strength of "binder 10 / arm 2, 95/95 steps". The
live trace shows the opposite: **`armFired = False` and `jyArmThisCall = 0` for all 95 steps** — the arm never
bound, and `JunctionYieldConstraint` returns `+Inf` throughout. The attribution was an artefact of stale
diagnostics, and two follow-up hypotheses (H-A downstream-junction, H-B `seen` double-count) were built on it
and both refuted.

This is the **second instrument-level defect** found this session, after the OBB half-length anchor
(`docs/NEED-obb-anchor-halflength.md`). Both distorted the picture before any engine bug was reached.

## Fix options

1. **Write the diagnostics on the pre-pass too when the pre-pass is what actually decides the Intent.** The
   cleanest framing: the fields should reflect *the pass whose Intent was used*. When `ReuseIntent` is true,
   the pre-pass Intent IS the final Intent, so its binder is the correct diagnostic value. This is the
   faithful fix and keeps the fields meaningful in every state.
2. Or add an explicit `IntentFromPrePass` / `DiagStaleSince` marker so consumers can tell a live value from a
   carried-over one, and make every diagnostic reader check it.
3. At minimum, **document the hazard at both write sites and on the `CarAuthWitness` fields** that surface
   them (`Binder`, `JyArm`, `JyFoeSpeed`), so the next investigator does not repeat this.

Option 1 is preferred: a diagnostic that is silently wrong is worse than one that is absent.

**Note on parity:** these fields are explicitly "never read by sim" (comment at `Engine.cs:5183`), so
correcting when they are written is **behaviour-neutral** and cannot move a golden. It only needs the
diagnostic-consuming tests re-baselined.

## Success conditions

- With `ReuseIntent` active, `BindingConstraint` / `JunctionYieldArm` reflect the **current** step's decision
  (or are explicitly flagged stale).
- A direct test: a vehicle in the fusion-reuse state must not report a binder that the live computation says
  is non-binding. `__veh127` around steps 98–192 is a ready-made fixture — the stored value says
  `10/cautiousApproach`, the truth is "no junction-yield arm binds".
- `Sim.ParityTests` goldens byte-identical and `Sim.Bench` hash `D96213B7BB4021A7` unchanged (guaranteed by
  construction — these fields are write-only w.r.t. the simulation).

## Knock-on: the veh127 freeze is now UNEXPLAINED

Since `JunctionYieldConstraint` returns `+Inf` during the freeze, the cause is **some other constraint arm**,
not yet identified. Re-running the attribution is blocked on this NEED: the tool one would naturally use is
the very thing that is broken. **Fix this first, then re-attribute the freeze.**
