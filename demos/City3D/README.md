# City3D — a 3D city viewer that *consumes* the SumoSharp NuGet packages

A small Godot 4 (.NET / C#) demo that renders a running SumoSharp simulation in 3D — a procedural
box-city generated from the network geometry, width-accurate roads, simplified traffic lights, and
true-size cars moving *smoothly* between sparse sim updates via `SumoSharp.Viewer.Motion`.

**Why it exists.** Unlike the projects under `samples/` (which `<ProjectReference>` into `src/`), this
demo references `SumoSharp.*` via `<PackageReference>` resolved from a **local NuGet feed**. It exists to
validate the *packaged consumer experience* end to end — before anything is ever published to nuget.org —
and to showcase the render-side dead-reckoning motion story in a real 3D engine, first as a single local
viewer and then as a **remote** viewer fed by a decoupled headless host.

Design + tasks: `docs/DEMO-CITY3D-DESIGN.md`, `docs/DEMO-CITY3D-TASKS.md`, `docs/DEMO-CITY3D-TRACKER.md`.

> **Status:** under construction (Stage 0 foundations). Run instructions below are filled in as the
> stages land; this stub is intentionally minimal.

## One-command build (local feed)

```bash
demos/City3D/build.sh            # pack SumoSharp.* → ./local-nuget, then build the demo
demos/City3D/build.sh --remote   # additionally pack the native DDS transport for the remote path
```

`build.sh` runs `dotnet pack` for the SumoSharp packages the demo needs into `demos/City3D/local-nuget/`,
which `nuget.config` pins as the only source for `SumoSharp.*` (everything else comes from nuget.org).
Nothing is published; `local-nuget/` is git-ignored and regenerated. See `docs/DEMO-CITY3D-DESIGN.md`
("Local package feed") for the alternative "download the pack-check.yml CI artifact" feed.

## What is verified where

The 3D view needs a desktop/GPU. In a headless environment you can verify the feed packs, `SumoSharp.*`
resolves from it (and fails without it), the demo assembly builds, and a `godot --headless` smoke run — but
the actual rendered image, believable scale, and the multi-monitor video wall are confirmed on a desktop.
Godot runs first-class on Linux, so a Linux desktop works too.
