# Weave demo (GitHub Pages)

A single self-contained page — `index.html` — demonstrating the deterministic pedestrian weave
(PED-REALISM-1 / `docs/PEDESTRIAN-LOWPOWER-AVOIDANCE-DESIGN.md`). No build step, no dependencies, no network:
open the file, or serve this directory statically.

**GitHub Pages:** enable Pages for the repo with source = `/docs` (Settings → Pages), and the demo is served
at `<pages-root>/weave-demo/`. (Or copy `index.html` to a `gh-pages` branch / any static host.)

**What it shows.** Two counterflowing pedestrian streams on one sidewalk. Toggle **Weave On/Off** to see
opposing flows separate (on) versus collapse onto the centreline and pass through each other (off) — with a
live "% of peds overlapping" counter that quantifies it. Sliders vary crowd density and sidewalk width (the
band fills the baked width, ~2× wider on a 4 m arterial than a 2 m local).

**It's the real algorithm.** The page runs a faithful JavaScript port of the engine's
`Sim.Pedestrians.Lod.LateralWeave` (SplitMix64 seeded, pure function of route arc-length), and self-checks a
reference sample against the C# engine on load (`0.4801 = engine ✓`). Because the pose is a pure function of
`(route, seed, width, time)`, it reproduces identically on reload — the same determinism that makes
`server == image-generator` bit-for-bit over the wire.
