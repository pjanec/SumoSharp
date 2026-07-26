# NEED — `Engine.LinkStateChar` reads the wrong hop for a cont link (misses `getCorrespondingEntryLink`)

**Scope:** `src/Sim.Core/Engine.cs:12414` (`LinkStateChar`), consumed by `ClassifyTeleportKind`
(`Engine.cs:~12401`).
**Found by:** the `isLeader` port design (`docs/F3-ISLEADER-PORT-DESIGN.md` §2c), while establishing
how to reach a junction link's right-of-way state.
**Severity:** LOW — it currently affects a **diagnostic label only**, never a trajectory. Recorded so
it is not silently absorbed by a later change that *does* read it for a behavioural decision.

## The defect

```csharp
private char LinkStateChar(JunctionLink link)
{
    var conn = link.Connection;
    if (conn.Tl is { } tl && conn.LinkIndex is { } li) { return TlLinkStateChar(tl, li, CurrentTime); }
    return conn.State is { Length: > 0 } s ? s[0] : 'M';
}
```

`JunctionLink.Connection` is resolved as `connections.FirstOrDefault(c => c.Via == intLanes[i])`
(`NetworkParser.cs:341`). For a **cont** (two-stage) link, `intLanes[i]` is the **stage-2** lane, so
that connection is the *second* hop — which carries **no `tl` and no `linkIndex`**. Verified on
`scenarios/_repro/synthetic-junction2/grid.net.xml`, junction `2336`, link 18:

| Hop | `from` | `via` | `tl` | `linkIndex` | `state` |
| --- | --- | --- | --- | --- | --- |
| entry | `2417` | `:2336_18_0` | `2336` | `18` | `o` |
| 2nd (what we read) | `:2336_18` | `:2336_42_0` | — | — | `m` |

So `LinkStateChar` falls through to the static `'m'` and never consults the traffic light. All ten
cont links at `2336` (indices 5, 12, 17, 18, 19, 25, 31, 36, 37, 38) are affected.

## Consequence

`ClassifyTeleportKind` decides `Jam` vs `Yield` on `state >= 'A' && state <= 'Z'` (uppercase =
priority). Since a cont link always reports lowercase `'m'`, **every teleport whose next link is a
cont link is labelled `Yield`, regardless of the actual phase** — including one that is genuinely a
red-light jam.

This is worth knowing precisely because a `Yield` label was already once mistaken for evidence about
a *cause*: `F3-SESSION-LOG.md` §9.22 records that D3's framing ("why is a *yield* wait > 120 s?")
rested on a premise the counter never attested. This defect is a second, independent reason not to
read meaning into that label.

## The fix

Port `MSLink::getCorrespondingEntryLink()` (`sumo/src/microsim/MSLink.cpp:1331-1339`): walk back while
the link's from-lane is internal; an entry link returns itself. In our model the entry hop is directly
resolvable as the top-level connection at this junction whose `LinkIndex == i`.

The `isLeader` port introduces exactly this lookup as `EntryConnectionByLink`
(`docs/F3-ISLEADER-PORT-TASKS.md` T2.1), so once that lands the fix is a one-line change in
`LinkStateChar`.

## Success conditions

- A direct test that `LinkStateChar` for cont link 18 at junction `2336` returns the **live TL phase
  char** for `linkIndex 18` at the current time, and specifically **not** `'m'`.
- A test that a red phase on a cont link classifies a teleport as `Jam`, not `Yield`.
- Goldens byte-identical (this touches a diagnostic counter only) and `Sim.Bench` hash unchanged.
