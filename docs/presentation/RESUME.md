# RESUME — the presentation deck, mid-flight

> **EPHEMERAL. Delete this file when the deck ships.**
> This is a working note for whoever picks the deck back up — including a future me after a context
> compaction. It is committed for one reason only: the VM is volatile and only committed files
> survive (CLAUDE.md prime directive 2). The docs housekeeping pass in PR #17 archived 22 files
> exactly like this one. Do not let this become the 23rd: when the PPTX is built and accepted,
> `git rm docs/presentation/RESUME.md`.

---

## 1. What this deliverable is

The user asked for a features presentation of SumoSharp — "all that extra stuff on top of SUMO".
Three artefacts, two of which exist:

| Artefact | State | Path |
| --- | --- | --- |
| Diagram set (SVG, generated) | **18 exist, 17 is the target after the pending edits** | `docs/presentation/svg/*.svg`, from `gen_svg.py` |
| Written companion (Markdown, more prose) | **done** | `docs/presentation/PRESENTATION.md` |
| PPTX deck | **not started** | — |

**Audience:** both stakeholders ("why is this more than SUMO?") *and* engineers who will build
against it. The stakeholders are highly technical, so do not dumb it down.

**Framing, in the user's words:** be honest about limits, but do not sell this as a "limited
solution" — the limits are the current POC state and everything is open for improvement, because
*we own the whole stack*. The closing beat is: this is a **substrate**, not a finished product;
many mechanisms are implemented, none is perfected.

**Demo order:** demos come **last**, once the audience knows what to expect. Two of them —
an *impression* demo (real IG + Geneva terrain, ~1k cars / 1k peds) and a *performance* demo
(local Godot 3-D, Geneva scenario, full scale).

---

## 2. The pending work, in order

### 2.1 Diagram edits still outstanding

The user reviewed the rendered set and gave this feedback verbatim. Diagram 03 is **done**
(commit `752d0b6`); the rest are open:

> 03 - the low power image is confusing. they are not a single line on the sidewalk, and also not
> two lines (forward and ooposite direction), they weave and spread and keep thir side but the
> still can overlap a bit (around 15%) if too dense. High power never overlap. the goal of low
> power was to avoid sumos "rail-like" uniform movement. peds should never be presented as uniform
> grid or rails. / 06 unclear to the audience ; the fidelity trade on the bottom is the message /
> 10 no need to say we left render thread. history is not imporant, current state and future is /
> remove 11 completely / remove 13 completely / is there mentioned spatial optimization of
> vehicles, not sure how it works but not all parts of the city are calculated serially, it is
> somhow spread

Concretely:

- [x] **03 `d_lod`** — rebuilt with `ped_band` organic scatter. Committed `752d0b6`.
- [ ] **05 `d_weave`** — *same defect as 03 and not yet fixed.* It currently draws two neat rows,
      which is the rails artefact the mechanism removes. Use `ped_band(..., side=±1)`. The contrast
      to draw is **SUMO-like uniform/rails vs organic side-keeping spread**, not one row vs two.
- [ ] **06 `d_coupling`** — restructure so the **fidelity trade is the message**, not a footnote at
      the bottom. Right now the coupling plumbing dominates and the trade is an afterthought.
- [ ] **10 `d_threaded`** — drop the before/after history ("we left the render thread"). Current
      state and future only.
- [ ] **Delete 11 (`d_terrain`) and 13 (`d_discipline`) completely** — remove the functions, the
      entries in the `DIAGRAMS` list, and the `svg/11-*.svg` / `svg/13-*.svg` files, and renumber.
- [ ] **NEW spatial-parallelism diagram** — the user asked whether spatial optimization of vehicles
      is covered. It is, in `PRESENTATION.md` §6, but has no diagram. See §4.3 below for the facts.
- [ ] Renumber after the two deletions and the one addition: **18 → 17**.

Optional / deferred, my own notes rather than user requests:
- **04 hysteresis** — decide whether to show the real defaults (70/100 m live-city, 6/13 m City3D,
  1 s dwell) or keep it schematic. Real numbers invite "why those?"; schematic invites "so it's
  hand-wavy?". I lean schematic in the deck, real numbers in the Markdown.
- **08 lanechange** — full version in `PRESENTATION.md`, one line in the deck.

### 2.2 Then build the PPTX

Recipe that works in this environment:

```bash
DECK=/tmp/claude-.../scratchpad/deck          # scratchpad, NOT the repo
cd "$DECK" && npm i pptxgenjs                  # already vendored in node_modules there
node build_pptx.js                             # to be written
python3 /mnt/skills/public/pptx/scripts/office/validate.py out.pptx
```

Load the **`pptx` skill** first. Its gotchas that already bit this deck:
- set `pres.layout` **before** adding slides;
- **no `#` prefix** in hex colours;
- **never share an options object** between two `addText` calls — pptxgenjs mutates it;
- **runs of spaces do not space anything** — XML collapses repeated whitespace. Use the
  `stats`-style multi-column layout (see `gen_svg.py`), one text box per column.

The SVGs must be **rasterised to PNG** for embedding (`cairosvg`, `scale=1.4` is what the review
renders used). Render and *look at* every slide before declaring it done — visual QA has caught
three real defects on this deck already (see §3).

### 2.3 Then republish the review artifact

The user's review page lives at
`https://claude.ai/code/artifact/2ac4a874-a5d6-4eb8-93a0-6c359a847d51`.
Republish it (same URL — pass `url:`) after the diagram set changes, so their review copy is not
stale. Built by `build_review.py` in the scratchpad deck dir; it also produces
`sumosharp-diagrams.zip` (SVG + PNG + a standalone page), which is what the user asked to be
handed the whole set as.

---

## 3. The generator: traps that are fixed in the helper layer — keep them there

`docs/presentation/gen_svg.py` is a single self-contained script, no dependencies beyond the stdlib
(`cairosvg` is only for previewing). Four hazards are handled **once, in the helpers**, precisely so
call sites cannot reintroduce them. If you add a helper, hold the same line.

1. **XML escaping happens in `txt()`, not at call sites.** One bare `&` — it was
   "Car following & lane changing" — makes the entire SVG un-parseable and hard-crashes `cairosvg`.
2. **`defs()` declares an arrow marker for EVERY palette colour, always.** An undeclared marker is
   not a soft fallback; it is a crash. That is why `ALL_COLORS` exists.
3. **`stats()` never spaces columns with runs of spaces.** XML collapses them, producing a run-on
   line. This shipped once and had to be fixed in review.
4. **A modular stride is not a scatter.** `(i*53)%200` walks a diagonal and `(i*a+b)%n` lays down
   columns — both read as *structure*, which is the opposite of the point when drawing crowds. Use
   the seeded LCG in `ped_band` for rectangles and a **golden-angle** spiral for discs.

**The colour code is load-bearing.** `AMBER = cars`, `TEAL = peds`, `PED_HI` = promoted peds,
`LIGHT` = untouched SUMO parity core, `SLATE` = our plumbing, `RED` = a limit or a refuted thing,
`ZONE` = the attention/realism-zone construct. Slide 1 teaches this legend; using amber for a
pedestrian anywhere later trains the audience to misread it. That exact violation was found on
diagrams 03, 04 and 14 and fixed — do not regress it. `PED_HI` is deliberately a *brighter teal*,
never amber.

Regenerate with `cd docs/presentation && python3 gen_svg.py`. It is idempotent and rewrites all SVGs.

---

## 4. Facts the deck depends on — and the ones I got wrong

Everything here was verified first-hand this session. Several entries exist because I asserted the
wrong thing first; they are the reason to check rather than recall.

### 4.1 Corrections — do not regress these

| Claim | The correction |
| --- | --- |
| "777 tests pass" | **777 pass / 0 fail / 4 skip is `Sim.ParityTests` alone**, not the solution. Whole `Traffic.sln` ≈ **1120 pass / 0 fail / 4 skip** across 5 projects (ParityTests 777, Pedestrians 324, IgBridge 11, Host 6, DotRecast 2). Plus `Sim.LiveCity.Tests` 90/90 and `CityLib.Tests` 186 pass / 4 skip — **neither is in `Traffic.sln`**. I propagated the wrong reading into the README, commit messages and the merged PR body before catching it. `docs/TASKS-TODO.md` is the authority. |
| "peds never collide once they keep their side" | The weave guarantee covers **opposing flows only** — keep-right puts them on provably different halves. **Same-direction overtaking still overlaps**; there is no minimum-separation enforcement at low power. The repo README was the source of the overstatement. |
| "cars yield to low-power peds on crossings" | True but narrower than it sounds: crossing occupancy counts **low-power peds *walking* on a crossing**. It excludes promoted peds and paused peds. |
| "661 goldens byte-identical" | True, and the bar is strict — `pos`/`speed` to 1e-3 and **`lane` by exact string match**. But goldens are *small* scenarios (2–5 vehicles, ~40 steps) and cannot contain a saturated junction. |
| "city-3000 is byte-identical" | It is **35% aggregate agreement**, not byte-identical. Different evidence class entirely. |
| Bench hash | `BF3794A4704BCD79`, par == single. |

### 4.2 Scale — three distinct evidence classes, never blur them

`PRESENTATION.md` opens with a table that separates these, and it must stay separated:

1. **Owner-verified routine operation** — **10 000 vehicles + 30 000 pedestrians** in the Godot 3-D
   viewer. Repeated first-hand operational experience, *not* an instrumented capture. Peds likely
   have significant further headroom; 10k cars is considered enough. **This is the headline number.**
2. **Instrumented headless bench** (reproducible, has a CSV and a session log) — 5 000 cars +
   20 000 peds at ~114 ms/step, RTF ~4.4× at 2 Hz, 0/60 spikes.
3. **Single GPU threaded-tick capture** (has a CSV) — 3 858 cars + 20 726 peds, 0/2000 spikes,
   p99 = 1.20× p50, 2 Hz sustained. Use this for the *smoothness* claim specifically.

The user's steer on numbers: *"do not spend too much on real numbers; the emphasis is on that the
engine is already quite optimized but still lots of space for further optimization."*

### 4.3 Spatial parallelism — facts for the new diagram

`Engine.RegionPlan`, driven by `--region [--region-grid G]`. A G×G grid over the network where
**each region owns a disjoint set of lanes**, which is what makes it lock-free *by construction*
rather than by careful locking. Boundary handoff is free: a vehicle crossing out is simply regrouped
into the next region on the following step, with no state transfer. Byte-identical output. **Off by
default.** The win is modest today because the hot phases are **memory-bandwidth-bound on random
neighbour access**, not compute-bound — which is also the honest answer to "why is it off?".

Measured thread sweep (same workload): serial 11.48 / 2t 7.90 / 4t 6.34 / 8t 5.68 / 16t 5.67 /
**24t 6.13**. Note 8 threads beats 24, and the knee is at 4. That shape *is* the story: the
headroom left is in memory layout, not in more cores.

### 4.4 Other numbers the deck cites

- `DrErrorPublishPolicy`: PosTol 0.3 m, LatTol 0.2 m, MaxInterval 3.0 s. Publish reasons are
  short-circuited in that order, and `src/Sim.Replication/PublishPolicy.cs` now counts each.
- `FrameCodec`: HeaderSize 16, VehicleRecordSize 48, PedFreeKinematicRecordSize 18,
  CrowdRecordSize 32.
- Constraint binders: 3 = `FreeFlowDesiredSpeedConstraint`, 13 = `CrowdLongitudinalConstraint`,
  16 = `CrowdYieldConstraint`.
- Write-rate measurements and their method: `docs/MEASURE-WRITE-RATE-RESULTS.md`, instrument at
  `src/Sim.MeasureWriteRate/`.
- Live-city extensions the user wants represented: peds pausing to check a phone, meeting at a side
  spot then leaving, visiting open-air restaurants, boarding a car at a parking place and driving
  away. Backed by `PauseSegment(Dur, AnimTag)` with tags "sip"/"phone"/"look", `SocialPlanner`
  (DefaultMeetOffset 0.6, DefaultDuration 4.0), `WaiterScenario`, `PersonRideController`
  (Walking→Riding), `LotCoupling`.

---

## 5. Hard constraints

**Geneva data — the user's words, preserve exactly:**

> geneva is just example ang big and restricyed cant be used as persistent tesy datA. no sense is
> reading env vaer where geneva data is. if persistent test needed, simple syntehetic small always
> present test data must be used.

So: **nothing in the repo may reference the Geneva dataset by path or by env var.** It exists only
in the session scratchpad and dies with the VM. Any persistent test needs small synthetic committed
data. The deck may *say* "Geneva" as the demo scenario; the repo may not *point at* it.

**Ask questions as plain chat text**, never the interactive question widget (CLAUDE.md
§Interaction preferences).

**Delegate volume, keep judgment.** But note the failure mode that cost three delegations in one
session: *delegate building an instrument, never delegate waiting for one.* A subagent that starts
a long background run ends its turn and the result is lost. End a delegation at "compiles,
verified, committed" and run the measurement yourself.

**Verify before trusting a "done".** Every subagent report this session was checked first-hand and
one was wrong (F3 T1.6–T1.9 reported done, actually open and blocked). Related trap that produced
a *wrong report from me*: I ran `dotnet run --no-build` against a binary predating the agent's last
edit and reported two working samples as vacuous. `dotnet build -c Release` does **not** build
`tests/Sim.LiveCity.Tests` or `demos/City3D/CityLib.Tests` — they are not in `Traffic.sln`.

---

## 6. Where things are

- Branch **`claude/handoff-docs-implementation-pmdu9z`**, pushed. PR #17 is **already merged** to
  main at `0d385a8`; the branch was restarted from merged main afterwards, so do not stack onto
  merged history.
- Commits on the branch since the merge: `ab504f2` write-rate measurement, `5f7f995` diagram set,
  `4a823e0` two more diagrams + fidelity trade, `86df276` + `b691110` fact-check corrections,
  `8129f5b` closing slide, `c975a59` `PRESENTATION.md`, `752d0b6` rebuilt LOD diagram.
- Scratchpad deck dir (**dies with the VM**):
  `/tmp/claude-0/-home-user-SumoSharp/db1d896a-b3e1-5d2f-baa9-dc9b1653d82b/scratchpad/deck/` —
  holds `build_review.py`, `node_modules/` (pptxgenjs), `png/`, `review.html`,
  `sumosharp-diagrams.zip`, and `FACTS.md` (whose content is now folded into §4 above, so the
  scratchpad copy is expendable).
- `gen_svg.py` and every SVG are **committed and in sync** as of `752d0b6`. There is no
  uncommitted generator work outstanding — that was true before `752d0b6` and is the single most
  important thing this file was written to prevent recurring.
