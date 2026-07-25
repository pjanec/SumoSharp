# NEED — demo_city has NORMAL lanes whose geometry overlaps in world space (net authoring)

**Found by:** F3 junction-overlap session (`docs/F3-JUNCTION-OVERLAP-DESIGN.md` §7, N3).
**Scope:** the demo net (`scenarios/_ped/demo_city/box`), **not** the engine.
**Severity:** medium — unfixable in the engine, and it permanently floors any zero-overlap invariant.

## Evidence

The lane pair

```
e_d_6_5_d_5_5_2        (a normal through lane)
e_d_garage_stub_d_5_5_1 (a normal garage-stub lane)
```

produces **8 overlap events, worst 1.800 m** (= the full vehicle width) over 200 steps — and the count and
depth are **identical** in both the front-anchor and centre-corrected OBB variants, so it is not an artifact
of the anchor bug (`NEED-obb-anchor-halflength.md`).

Worst case, step 52:

```
__veh80  lane=e_d_6_5_d_5_5_2         pos=217.83 spd=9.31 tl=r
__veh134 lane=e_d_garage_stub_d_5_5_1 pos= 19.60 spd=0.00 tl=r   pen=1.800 m
```

Both vehicles are on **normal** lanes (no `:` prefix — no internal/junction lane involved), each correctly
positioned on its own lane, each obeying its own lane's rules. Their **lanes** overlap in world space.

## Why this is a net-authoring defect, not an engine defect

A vehicle's position is authoritative in lane-relative coordinates (`lane id`, `pos`); world `(x, y)` is a
pure output-side derivation (`LaneGeometry.PositionAtOffset`, and see the "never feeds back" note at
`src/Sim.Ingest/LaneGeometry.cs:7-9`). If two lanes' shapes occupy the same ground, then two vehicles that are
each perfectly legal on their own lane will render on top of each other. **No longitudinal or junction rule
can prevent this** — there is no conflict record between two *normal* lanes, and SUMO models no interaction
between vehicles on non-adjacent, non-conflicting normal lanes.

SUMO itself only models cross-lane conflict via junction internal lanes and their `<request>` foes matrix
(`MSLink::setRequestInformation`), plus same-edge neighbours under the sublane model. Two unrelated normal
edges crossing in geometry is outside every mechanism SUMO has.

## This directly corrects the F3 handoff

`docs/F3-JUNCTION-OVERLAP-HANDOFF.md` describes this as "**Pattern B** — green ego crosses through a stopped
car on a stub/approach (keep-clear)", attributing it to the junction admission gate via internal lane
`:d_5_5_6_1`. The per-step trace shows otherwise: for steps **51–57** `__veh80` is on
**`e_d_6_5_d_5_5_2`, a normal lane**; only at steps 58–59 does it reach `:d_5_5_6_1`. The bulk of the
veh80/garage-stub family is this normal-lane geometric overlap, **not** a junction admission failure. Pattern
B as written is not an engine bug.

## Fix options (net-side)

1. **Re-author the garage stub** so its shape does not cross `e_d_6_5_d_5_5_2` — the correct fix if the stub
   was hand-placed.
2. **Regenerate the net through `netconvert`** so the stub connects via a proper junction with internal lanes
   and a real `<request>` foes matrix; then the existing junction machinery governs it and the overlap becomes
   a modelled conflict rather than an unmodelled one.
3. **Accept and document it** as a known demo-net wart, and exclude this lane pair from overlap invariants by
   name.

Option 2 is the principled fix (it turns an unmodelled interaction into a modelled one). Option 3 is
acceptable short-term but must be explicit, not silent.

## Consequence for F4b

Until this is fixed, the demo has a **hard floor of 8 overlap events at 1.800 m** that no engine change can
remove. Any "assert ZERO overlap" invariant would be unsatisfiable for reasons that have nothing to do with
the engine's correctness.
