# DOCS-HOUSEKEEPING-PLAN.md — how the docs tree is organised, and why

**Status: the plan of record for the 2026-07-28 housekeeping pass.** Read `docs/README.md` for the
resulting map; this file explains the *rules*, so a future pass does not have to re-derive them.

## 1. The problem, measured

`docs/` held **268 markdown files, 6.8 MB**, all created within one month. Most of the growth is a
side effect of the design-first workflow in `CLAUDE.md`: every feature produces a
DESIGN + TASKS + TRACKER triad, and every working session tends to leave a handoff, a resume note, or
a prompt aimed at another session. Those accumulate faster than anything retires them.

Counted by filename pattern: 67 `*-DESIGN.md`, 49 `*-TASKS/TRACKER.md`, 29 handoff/resume/bootstrap/
prompt/sync briefs, 21 `NEED-*` notes, 33 investigation logs.

## 2. The constraint that shapes everything: 156 docs are cited from source code

This repo's house style is to point at a design doc from the code that implements it —
`// docs/LIVE-CITY-THREADED-TICK-DESIGN.md §5 ...`. A grep for `*.md` across `src/`, `tests/` and
`demos/` finds **156 of the 268 docs cited from `.cs` comments**, most of them with the `docs/` prefix
spelled out.

That makes "tidy up by moving files into `docs/archive/`" actively harmful for those 156: a code
comment pointing at a path that no longer exists is worse than a cluttered directory, because the
reader cannot tell whether the doc was deleted, renamed, or never existed. **A broken pointer costs
more than the clutter it removes.**

So the pass is split by that line:

| set | size | treatment |
| --- | --- | --- |
| **code-pinned** — cited from a `.cs` comment | 156 | **stays where it is.** Gets a one-line status banner (§4) so a reader knows immediately whether it is current. |
| **freely movable** — cited only from docs, or not at all | 112 | may be archived or deleted on its merits. |

Additionally pinned regardless of the above, because they are load-bearing navigation:
- the 6 docs `CLAUDE.md` cites by name (`DESIGN.md`, `TASKS.md`, `F3-SESSION-LOG.md`,
  `DENSITY-DIFF-HARNESS-TRACKER.md`, `CONSTRAINT-high-realism-artefact-ladder.md`,
  `NEED-junctionyield-impatience-saturation.md`);
- the 21 docs `README.md` links.

Any move touching those requires updating the citing file in the same commit.

## 3. Verdict rules

Every doc got exactly one verdict. The rules, in the order they were applied:

- **KEEP** — still true, still the thing to read. No banner needed beyond a status line.
- **UPDATE** — useful, but contains a claim that is now false. **A verdict of UPDATE required the
  reviewer to name the false claim**, not to say "possibly outdated"; an unnamed suspicion is not a
  finding. Every UPDATE was then re-verified against the source before being acted on — a subagent's
  staleness report is a lead, not a fact (`CLAUDE.md` §Subagents: the review is the load-bearing step).
- **ARCHIVE** — historically valuable, not current guidance. Moves to `docs/archive/` **only if
  freely movable**; otherwise stays put with an ARCHIVED banner.
- **DELETE** — no residual value. Required naming the superseding doc, or an explicit statement that
  the content is not preserved elsewhere *and* why it is valueless anyway.

### The bias, and why it is deliberate

**When the choice was between ARCHIVE and DELETE, ARCHIVE won.** This is not timidity. `CLAUDE.md`'s
"Measurement discipline" section is built almost entirely out of *refuted hypotheses* recorded in
this class of document, and it says so: five reasoned interventions that turned out inert, a
`addBlockedLink` port that was dead code, an occupancy metric that read 5-of-9 where the causal answer
was 0-of-9. Each of those records exists to stop a future session re-running a dead end. Deleting a
refuted-hypothesis log destroys the only artefact that makes the lesson checkable, and the cost lands
on whoever next has the same idea.

The corollary: **a doc recording a FAILURE is worth more here than a doc recording a success**, because
the success is also in the code and the tests, and the failure is nowhere else.

### The one unacceptable outcome

**Losing genuine open work.** Several trackers carry unticked boxes for real, unstarted items. Before
any such doc is archived, its open items are folded into `docs/TASKS-TODO.md` — the live queue — with a
pointer back to the archived detail. A tidy docs tree that quietly dropped a known bug would be a
strictly worse repo than the untidy one.

## 4. Status banners

Every doc that is not self-evidently current gets a single blockquote immediately under its title:

```
> **STATUS: ARCHIVED (2026-07-28).** <what it was for> · Superseded by `docs/X.md`.
> Kept because <the specific reason — usually: it records a refuted hypothesis or a measurement trail>.
```

The banner is the cheap fix for the code-pinned set: the pointer from the code still resolves, and the
reader learns in one line whether to trust the contents. Statuses used: `CURRENT`, `ARCHIVED`,
`SUPERSEDED by X`, `HISTORICAL TRAIL`, `NEVER IMPLEMENTED`.

`NEVER IMPLEMENTED` matters more than it looks: several designs describe features that were considered
and parked (rail variants, laneless traffic, distributed coupling, panic-evac extensions). They are
legitimate to keep — a considered-and-parked idea saves the next person the analysis — but presenting
them as descriptions of the current system is how a reader ends up looking for code that was never
written.

## 5. What the pass actually did

Seven reviewers read all 268 docs, one batch each, and returned a verdict per doc with a named reason.
Every staleness finding was then **re-verified against the source before being acted on**. That check
earned its keep: one reported that `F3-JUNCTION-OVERLAP-TRACKER`'s T1.6–T1.9 were "done but unticked", and
they are in fact genuinely open and explicitly blocked on each other. Acting on the report would have
ticked four boxes over real work. That case is now recorded in `TASKS-TODO.md` so the next pass does not
repeat it.

Outcome:

| Action | Count | Where it landed |
| --- | --- | --- |
| Moved to `docs/archive/` | 22 | Freely movable, superseded session ephemera. Each stamped with what superseded it *and* why it was kept. |
| Banner-stamped in place | 11 | Code-pinned, so the path must keep resolving. 3 are `HISTORICAL TRAIL` — they carry claims later disproved. |
| Corrected | 16 | Named false claims fixed, including two consumer-facing API docs that would not have compiled. |
| Deleted | **0** | See the bias in §3. |
| Open work folded into `TASKS-TODO.md` | 14 items | Two engine correctness bugs, two config-hygiene items, four pedestrian items, five trailing items, plus the false-positive above. |

The two corrections worth singling out, because they are the class that costs someone real time:
`PEDESTRIAN-NAVMESH-CONTRACT.md` documented a two-argument `FindPath` that no longer exists, and
`EXTERNAL-AGENTS-VIZ.md` — a public integration guide — documented a string-keyed obstacle API against a
handle-based one. Anyone following either would have failed to compile.

Two docs asserted the *reverse* of the truth rather than merely being out of date:
`LIVE-CITY-PED-LOD-LIFECYCLE-DESIGN.md` §3.2 presented a fix that was designed and then dropped, and
`HIGH-DENSITY-PLAN.md` recorded a gate as removed that appears 13 times in `Engine.cs` — and stated a
default the wiring contradicts. Those are the ones a status banner cannot fix; the body had to change.

The resulting map is [`README.md`](README.md).

## 6. What this pass deliberately does NOT do

- **It does not consolidate the triads.** Merging 67 designs into a handful of documents would lose
  the per-feature cross-references from code, and the triad structure is what `CLAUDE.md` mandates for
  new work. The fix for "too many designs" is the index in `docs/README.md`, not fewer files.
- **It does not rewrite investigation logs.** They are append-only records; editing one to read better
  in hindsight would destroy the thing that makes it evidence.
- **It does not touch `docs/reference/`** without first establishing whether that material is ours or
  vendored from elsewhere.
