# TRACKER — the density differential harness

At-a-glance status. Task IDs and success conditions live in `DENSITY-DIFF-HARNESS-TASKS.md`; mechanisms in
`DENSITY-DIFF-HARNESS-DESIGN.md`. **A box is ticked only when the reviewer has verified that task's success
conditions first-hand** — never on an implementor's report (`CLAUDE.md` §orchestration loop).

## Stage A — the SUMO side
- [x] **A1** three-column SUMO runner — `scripts/run-density-diff.sh`. SC1 ✅ (all four outputs, both
      columns, non-empty) · SC2 ✅ (**config diff is exactly 4 elements**, asserted in-script, run fails
      otherwise) · SC4 ✅ (fatal, non-zero exit when `sumo` is absent) ·
      **SC3 ⏸ BLOCKED ON B1, and the block is itself a finding — see below.**
- [ ] **A2** internal-lane + approach-lane detector generation

## Stage B — our side
- [ ] **B1** demand recorder → SUMO `.rou.xml` (additive, opt-in, provably inert when off)
- [ ] **B2** our global metrics, same schema as SUMO's
- [ ] **B3** our per-junction discharge / queue / occupancy

## Stage C — the comparison
- [ ] **C1** gap-decomposition report (cheat dividend vs real gap)
- [ ] **C2** density sweep 160 / 320 / 480 / 640
- [ ] **C3** ranked work list (measured entries only; cheats listed as won't-fix)

---

## The question this whole tracker exists to answer

> **Do our throughput curve and *honest* SUMO's bend at the same density?**

- **Same density** ⇒ the net/demand saturates; 480 in this crop is not achievable by anyone, and the target
  is wrong rather than the engine.
- **Ours bends earlier** ⇒ a real mechanism gap, ranked by its onset density.

Everything before C2 is instrument-building. **Nothing should be ported until C3 exists.**

---

## ⚠ A1/SC3: the committed demo route file CANNOT measure density (found by the guard, not by luck)

SC3 requires S-default to teleport **> 0** at high density, on the reasoning that a zero means the density is
too low to be measuring anything. Measured on `scenarios/_ped/demo_city/box/scenario.rou.xml`:

| | s-default | s-honest |
| --- | --- | --- |
| inserted | 861 | 861 |
| **teleports** | **0** | 0 |
| **collisions** | **0** | 0 |
| trips COMPLETED | **96** | 96 |
| still "running" at horizon | **765** | 765 |

**Why:** every route in that file ends `<stop parkingArea="pa_…" duration="100000"/>` — the cars **park
permanently**. It is a *parking* scenario, not a throughput one. 765 of 861 are parked-but-"running", which
is why only 96 trips complete and why nothing ever gets stuck enough to teleport.

**So this demand cannot be used as the shared input**, and A1's smoke run says nothing about SUMO's
high-density behaviour in either direction. **B1 (the demand recorder) is not optional convenience — it is
the only way to get comparable demand**, exactly as design §2 argues. Do not be tempted to reuse this file.

The one thing it does establish: the runner works end to end and the cheat-isolation assertion holds.

## Known-answer anchors (an instrument that misses these is wrong, not interesting)

| Anchor | Expected | Source |
| --- | --- | --- |
| S-default teleports at 480 cars | **> 0** | `time-to-teleport` defaults to 300 |
| S-honest teleports | **0** | `--time-to-teleport -1` |
| Our teleports | **0** | ladder rung 4, currently measured 0 |
| Stopped cars on `d_5_3`/`d_5_4` internal lanes at 480, gates ON | **present** | `F3-SESSION-LOG.md` §9.118 |
| Our determinism | two runs identical | already re-verified twice |
