# TASKS — the density differential harness

Work breakdown for `DENSITY-DIFF-HARNESS-DESIGN.md`. Each task names its design section (never restates it),
the files it touches, its dependencies, and **success conditions the implementor must satisfy**. A task is
closed only when its conditions are verified *first-hand* by the reviewer, not when reported done.

**Standing constraints for every task below**
- `dotnet test` must never invoke SUMO (design §5). A task that makes SUMO a test dependency is failed.
- `dotnet build -c Release` does not build `tests/Sim.LiveCity.Tests` — build that csproj explicitly.
- Behavioural engine changes ship default-OFF and measured. **No task here should need one**; if one seems
  to, stop and escalate rather than widening scope.
- `git commit -F <file>`, never `-m`.

---

## Stage A — the SUMO side (no engine changes)

### A1. Three-column SUMO runner
**Design:** §1 (the three columns and their exact option sets). **Files:** `scripts/run-density-diff.sh` (new).
**Depends:** nothing.

Wrap `sumo` for a given net + route file + step count, in each of the three configurations. S-honest must
pass exactly `--time-to-teleport -1 --time-to-teleport.highways -1 --collision.action warn
--collision.check-junctions true`; S-default must pass **none** of those.

**Success conditions**
1. Running all three on the committed demo net produces `tripinfo`, `summary`, `statistic` and
   `collision-output` files per column, non-empty.
2. **The S-default vs S-honest option sets differ ONLY in those four flags** — asserted by diffing the two
   generated `.sumocfg` files and showing exactly four changed elements. A runner that quietly differs
   elsewhere invalidates the whole cheat-dividend decomposition.
3. `statistic-output` for S-default reports a **nonzero** teleport count at 480 cars while S-honest reports
   **zero**. If S-default teleports zero, the density is too low to be measuring anything — say so and stop.
4. The script fails loudly (non-zero exit, explicit message) when `sumo` is absent. It must never silently
   skip and report success.

### A2. Internal-lane detector generation
**Design:** §3b (why detectors, not `netstate-dump`). **Files:** `scripts/gen-junction-detectors.py` (new).
**Depends:** A1.

Emit an `e2` detector per approach lane and per internal lane of every junction in the net, into an
`.add.xml` the runner passes to SUMO.

**Success conditions**
1. Detector count equals (approach lanes + internal lanes) of the net — asserted against a count parsed
   independently from `net.xml`, not against the generator's own bookkeeping.
2. Output at 7200 steps × 480 cars is **under 200 MB** (the reason detectors were chosen over a full dump).
3. For junctions `d_5_3` and `d_5_4` — the two with today's measured residual — the file contains detectors
   on all their internal lanes, checked by id.

---

### A3. ⚠ OPEN-LOOP demand mode — BLOCKS ALL CAPACITY WORK
**Design:** §1b. **Files:** `src/Sim.DensityDiff/`, and an opt-in inflow mode that does NOT consult occupancy.
**Depends:** nothing. **Priority: before B2/B3/C — those measure the wrong quantity without it.**

The demo's spawn loop is occupancy-capped, so inflow self-throttles and a discharge deficit is structurally
invisible. Add a mode that inserts at a **fixed rate** regardless of how many vehicles are resident.

**Success conditions**
1. At a fixed inflow, resident-vehicle count over time is emitted as a series for both engines.
2. **Non-vacuity against the known contradiction:** at an inflow where vanilla SUMO reaches steady state,
   our column must reproduce the calibration workstream's runaway (a monotonically climbing count that never
   levels off). If it does NOT reproduce it, the two instruments disagree and that must be resolved before
   any number from either is trusted.
3. The report states, per column, **steady-state reached: yes/no**, and if yes the plateau level.
4. Sweeping inflow finds the highest rate at which each column still reaches steady state — that is "max
   density", and it is the number the calibration workstream cannot currently obtain for us.
5. Every emitted metric is labelled with its demand model (`closed-loop` / `open-loop`). Design §1b: a
   capacity claim from closed-loop demand is invalid.

## Stage B — our side

### B1. Demand recorder
**Design:** §2 (record-at-spawn, and its three caveats). **Files:** `src/Sim.LiveCity/LiveCitySim.cs`
(additive, opt-in), `src/Sim.DensityDiff/` (new console project). **Depends:** nothing.

Optional recorder writing a SUMO `.rou.xml` at each insertion, in depart order, with the vType the demo used.

**Success conditions**
1. With the recorder **off**, the demo is **byte-identical** to before: `Sim.LiveCity.Tests` 50/50 and a
   demo run reproduces the same `ArrivedTotal` as the current build. This is the "purely additive" claim and
   it must be demonstrated, not asserted.
2. The emitted file **loads in SUMO with zero route errors** (`sumo --route-files <file>` exits 0 with no
   `Error` on stderr) — the recorder is worthless if SUMO rejects it.
3. Vehicle **count and depart times** in the file match the demo's own insertion log exactly.
4. The three caveats are each **reported as a number**, not omitted: reroutes performed, insertions refused,
   pedestrians present. A report with these silently absent is failed.

### B2. Our metrics, mirroring SUMO's
**Design:** §3a. **Files:** `src/Sim.DensityDiff/`. **Depends:** B1.

**Success conditions**
1. Global metrics emitted in the same schema as the SUMO side, so the comparison is a join, not a re-read.
2. Our **teleport count is 0** and asserted (the ladder forbids it in high realism; a nonzero value is a
   finding, not a footnote).
3. Deterministic: two consecutive runs at the same density produce **identical** metric files.

### B3. Per-junction discharge, our side
**Design:** §3b — and note the §3b distinction (discharge collapsing vs queue growing) is the deliverable.
**Files:** `src/Sim.DensityDiff/`. **Depends:** B2.

**Success conditions**
1. Per-junction, per-60 s: crossings completed, max approach-queue length, max internal occupancy.
2. **Non-vacuity, anchored to a known result:** at 480 cars with gates ON the report must show `d_5_3`/`d_5_4`
   carrying stopped vehicles on internal lanes (the measured §9.118 finding). If it does not, the instrument
   disagrees with an established measurement and is wrong — fix it before proceeding.
3. Crossing counts summed over all junctions are consistent with completed trips (same order of magnitude);
   a mismatch of >2x means the crossing detector is miscounting.

---

## Stage C — the comparison

### C1. Gap decomposition report
**Design:** §1 (the decomposition), §3. **Files:** `src/Sim.DensityDiff/`. **Depends:** A1, A2, B3.

Join the four result sets into one report with the three columns and the two derived gaps.

**Success conditions**
1. Every metric row shows S-default, S-honest, Ours, **cheat dividend** and **real gap** — the two derived
   columns are the point; a report showing only raw columns is failed.
2. **Any row whose deficit disappears between S-default and S-honest is explicitly labelled
   `CHEAT — DO NOT CHASE`.** This is the harness's primary safety property (design §1).
3. S-honest's junction collision count is reported prominently. If nonzero, the report states plainly that
   honest SUMO also buys discharge through interpenetration and that our target is therefore *its throughput
   at zero collisions* — a bar SUMO may not clear.
4. The report is a committed file.

### C2. Density sweep
**Design:** §4. **Depends:** C1.

160 / 320 / 480 / 640.

**Success conditions**
1. All four densities × three columns complete, or any failure is reported with its density and cause
   (a crashed run silently dropped would bias the curve).
2. The report states, for each of Ours and S-honest, **the density at which its throughput curve bends**.
3. It answers the ranking question explicitly: **do the two curves bend at the same density?** Per §4 that
   single answer decides whether the remaining work is an engine gap or an unachievable target — and it is
   the reason this stage exists.

### C3. Ranked work list
**Design:** §1, §4. **Depends:** C2.

**Success conditions**
1. Each entry names the SUMO mechanism, the metric it would move, its onset density, and an estimated cost.
2. Entries that are cheats are listed **separately** as closed won't-fix, with the ladder rung, so they are
   not rediscovered next session.
3. **No entry is a guess.** Each cites a measured row from C1/C2. An entry justified only by source-reading
   belongs in a design doc, not here — this is the exact failure that produced the falsified
   `addBlockedLink` hypothesis.

---

## Explicitly out of scope

- Porting anything. This harness produces the ranked list; the ports are separate work with their own docs.
- The `inTheWay` conflict-geometry port and the `keepClear`/queue-inside-junction fix — both wait for C3 to
  rank them (`F3-SESSION-LOG.md` §6).
- Changing the demo's demand generation. Design §2: the demand is under test.
