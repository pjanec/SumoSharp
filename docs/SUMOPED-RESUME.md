# SUMOPED — session resume / cold-start page

**Read this first if you are picking up the SUMO pedestrian port.** Written at the end of the design
session (2026-08-02) for compaction and for a fresh VM.

---

## 1. Status in one paragraph

Branch `claude/sumo-ped-port-sumosharp-gnhiip`, 14 commits, working tree clean, all pushed.
The deliverable so far is a **design-first doc set + two tools — no engine code**. Everything is
marked **PROPOSAL** and is waiting on owner sign-off before B0 starts. The vehicle gate is untouched:
nothing in `src/` has been modified, so `Sim.ParityTests` is still 782/5 with 661 goldens
byte-identical and the bench hash is still `A134ED3716DDE7BC`.

**The immediate next task** (owner's instruction, end of session): *"take one more look at the design
and look for gaps and weakly specified spots."* Not implementation — a review pass. §7 has my own
candidate list to start from, but the point is to find what is *not* on it.

## 2. The doc set, in reading order

| doc | what it is |
| --- | --- |
| `SUMOPED-REQUIREMENTS.md` | the WHAT — R1–R12, non-goals, the committed scenario set |
| `SUMOPED-ALGORITHM.md` | **read before porting anything** — what the striping model does, and every knob **measured** |
| `SUMOPED-DESIGN.md` | the HOW — 1062 lines; net model, storage/perf, the stepper, coupling, API, determinism |
| `SUMOPED-PROCESS.md` | the METHOD — four verification ladders, the stage gate, the divergence protocol |
| `SUMOPED-COVERAGE.md` | how we know the goldens cover the model; the 3-tier ladder; the 8 axes |
| `SUMOPED-BRANCH-INVENTORY.md` | the 148-branch denominator of the coverage claim |
| `SUMOPED-TASKS.md` | SP-0.0 … SP-7.6 with success conditions; standing rules S-a…S-g; batches B0–B10 |
| `SUMOPED-TRACKER.md` | the checklist + every number to keep pinned |

Also touched: `docs/SUMOSHARP-API.md` gained decisions **D19–D27** and a **§12b** (both PROPOSED —
SP-7.6 must flip §12b to landed, or the banner rots into looking like shipped API).

Tools committed: `scripts/render-ped-fcd.py` (golden → real Sim.Viz HTML, `--manifest` for multi-scene)
and `scripts/sumoped-knob-sweep.py` (knob sensitivity, `--pin-rng` + `--lat-edge`).

## 3. ⚠ Environment is EPHEMERAL — re-establish before doing anything

None of this survives a VM restart:

```bash
# SUMO source. CLAUDE.md expects it at /sumo.
git clone --depth 1 --branch v1_20_0 https://github.com/eclipse-sumo/sumo.git /home/user/sumo-src
ln -sfn /home/user/sumo-src /sumo

# SUMO binary. apt ships 1.18 -- the WRONG version. Use pip.
python3 -m pip install eclipse-sumo==1.20.0     # -> /usr/local/bin/sumo
sumo --version                                   # must say 1.20.0

# Only if you need headless render verification:
python3 -m pip install playwright
# do NOT run `playwright install`; use the pre-installed browser:
#   executable_path="/opt/pw-browsers/chromium-1194/chrome-linux/chrome", args=["--no-sandbox"]
```

**Everything I generated this session lived in the scratchpad and is gone**: the nets, demands, goldens
and the 16-scene HTML. That is deliberate — none of it was committed because scenario authoring is
SP-0.2/SP-0.2b's job and must be done properly. The **recipes are committed**: `SUMOPED-DESIGN.md`
**Appendix A** (the 1-car-1-ped priority-junction fixture, verbatim) and **Appendix B** (the Tier C
saturated fixture + the full generation command line). Regenerating takes minutes.

## 4. Measurements that took work — do not re-derive, do not re-litigate

All first-hand, all recorded in the docs with their commands. Cited here so a fresh session knows they
exist before spending an hour rediscovering one.

| finding | value |
| --- | --- |
| ped determinism knob | `--pedestrian.striping.dawdling 0` + `speedDev="0"` ⇒ **exactly 1.388889 m/s** every step. Only 2 RNG sites in the model, one on the default path |
| person-bearing SUMO outputs | **seven**, not one. `--person-summary-output` (per-step, incl. `jammed`, 54 KB/300 s) and `--collision-output` are the two that change the plan |
| exact parity at saturation | 300 s, 10,068 vehicle + **30,549 person** FCD rows, two runs **byte-identical** |
| `posLat` for persons | **not emitted**, ever — but `angle` encodes `mySpeedLat`; inverting it recovers exactly `0.6401` (stripeWidth) and `0.5556` (`vMax·0.4`) |
| `myAmJammed` recovery | stderr `is jammed` event **+** the exact `vMax/4 = 0.3472` speed signature (1983 samples in the jam golden) |
| ped DR vs vehicle DR | peds ~**6×** easier (p95 0.64 m vs 4.08 m); walkingarea chord interpolation p95 **0.175 m**, the *best* of all four classes |
| biggest knob | `jamtime` 300→10 ⇒ jammed **24 → 381** |
| most destructive vType knob | ped `width` 1.00 ⇒ collisions **8 → 122** |
| crossing priority A/B | `priority=false` ⇒ peds stopped on curb **91%**; `priority=true` ⇒ **0%**, 68 cars fully stop |
| jam-regime collisions | 80 records / 29 pairs, max `colliderSpeed` 2.60, only 1 of 80 above 0.1 m/s |
| ped vType defaults | `length 0.215  width 0.478  minGap 0.25  maxSpeed 10.44 (cap)  desiredMaxSpeed 1.3889  speedDev 0.1` |

## 5. Corrections made mid-session — do not re-introduce

Each of these was written confidently, then disproved by measurement. They are the most likely things
for a fresh session to "fix" back to the wrong answer.

1. **R5 "cars never cross a ped" — false of the oracle.** At jam density SUMO collides vehicles with
   peds by design (squeeze-through ignores collision gating). R5 is now parity-with-SUMO's-collision-set
   (R5a), a committed baseline (R5b), and improvement **later** as a gated deviation (R5c).
2. **"`posLat` absent ⇒ lateral weakly observable" — false.** `angle` carries `mySpeedLat`.
3. **"Persons need a bespoke DR extrapolator for the walkingarea Bezier" — false.** Measured the
   opposite; share the vehicle path, `DrModel` needs no new member.
4. **"`t − DELTA_T` means peds read a lagged light" — false.** `MSLink::opened` reads `haveRed()`
   **current**; the backdated time is only the arrival time in the foe-conflict comparison.
5. **"Persons should mirror vehicles' current `List<VehicleRuntime>` AoS" — wrong-headed.** Vehicles
   are AoS from migration cost; persons are greenfield. Lane-bucketed SoA.
6. **A metric that selected its own answer.** "Turner stopped %" conditioned on steps that already had
   ≥3 stopped peds reported 79–95% (looked like total failure); unconditioned it is **29%**.
7. **An unpinned knob-sweep baseline is noise**, because `dawdling` defaults to 0.2 and draws from a
   process-global stream — changing any option shifts the draw sequence.

## 6. Load-bearing design decisions (so a review knows what is deliberate)

- Persons go on the concrete **`Engine`**, not `IEngine`; `PedestrianWorld` (ORCA) untouched. D19–D21.
- **Lane-bucketed SoA**, hot/cold split, pooled `Obstacle` scratch. **Zero heap alloc on the step path
  is mandatory** (S-f) — SUMO's ~6 `std::vector`s per ped per step is ≈180 k allocations/simulated
  second if transliterated.
- **Vehicles yield via a phantom junction leader** (null vehicle, gap −1) injected into the existing
  junction-leader path — *not* the crowd-disc binders 13/16, which stay for the ORCA layer.
- **Extend the core `Sim.Ingest` net reader**; do not add a fourth parser.
- Cross-population contract (§6.6): **peds read vehicles at *t−1*, vehicles read peds at *t*** — no
  cycle. Exactly one thing needs a retained previous-step buffer: the **junction approach index**.
- Performance deviations are allowed but gated (§4.4, S-g): named `PD-n`, default OFF, ≥1.3× speedup,
  quantified delta, both surfaces, visual A/B, determinism preserved, owner sign-off. Ledger currently
  **empty and says so**.
- Only one named deviation from SUMO exists so far: the **dawdling RNG stream** (per-entity seeded
  instead of SUMO's process-global order).

## 7. Next task — the gap review. My own candidate list

The owner asked for a review pass for gaps and weakly specified spots. Starting candidates, *not*
exhaustive — the value of the review is finding what is missing from this list:

- **Person demand parsing is thin.** `<personFlow>`, `departPos`/`arrivalPos` semantics, and the
  `<walk from/to>` vs `<walk edges>` distinction are used in the scenarios but barely specified in the
  design. The `from`/`to` form invokes SUMO's **intermodal router** at insertion — is that in scope?
  (§5.5 only covers the *junction-local* router.)
- **Person insertion/departure** is under-specified vs the vehicle side's queued-insertion semantics —
  what happens when a sidewalk is full at depart time?
- **`MaxStripes` fallback path** is stated but its behaviour under a very wide lane is not pinned.
- **Arrival / `arrivalPos` / stopping-place obstacle** (`OBSTACLE_ARRIVALPOS`, incl. the "stop full"
  blocked variant) is in the inventory but has no scenario and no task.
- **Multi-stage persons** — `<person>` with more than one `<walk>`, or walk→stop→walk. R-N5 excludes
  ride/board, but *consecutive walks* are not obviously excluded and are cheap.
- **Error/edge paths**: no sidewalk on an edge, disconnected route, `getNextLane` finding no route.
  These are `HIDDEN`-branch stderr warnings in the inventory; none has a task.
- **The `Sim.Viz` overlay (SP-7.3)** is specified as a scene family but the *comparison* semantics
  (time alignment, which golden, what a mismatch looks like) are hand-waved.
- **Tier C count** — owner said 3 or 4, I recommended 4 (saturated TL, jam regime, narrow crossing,
  turning-heavy); the requirements table still says 2–3.
- **`regen-goldens.sh` changes** are described in prose across three tasks; no single place says exactly
  what the script must do for `_sumoped`.
- **Nothing specifies what happens when SUMO and we disagree and SUMO looks wrong** — the artefact
  ladder (`CONSTRAINT-high-realism-artefact-ladder.md`) is cited for R5c but not wired into the
  divergence protocol.

## 8. Traps specific to this work

- `apt` SUMO is **1.18**. Wrong version. Goldens from it are worthless.
- `--crossings.guess` at an uncontrolled node **always** gives `priority="false"` (`NBNode.cpp:2788`) —
  the ped-priority zebra regime is silently absent unless crossings are declared explicitly.
- Opposing ped streams at a 4-arm junction use **different crossings**, so crossing counterflow does
  **not** arise from "peds both ways" demand; it must be forced.
- Straight-through car demand never exercises the **exit-crossing yield**; you need turning flows.
- `Sim.Harness/FcdParser.cs:24` filters on `Elements("vehicle")` — `<person>` rows are silently dropped,
  so a person harness is new code, not a tolerance change.
- `SimulationSnapshot.Count` means **vehicles** and must not be repurposed.
- An XML comment containing `--` is illegal and SUMO will reject the routes file with a parse error that
  does not mention comments.
