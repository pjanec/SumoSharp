# Weave demos (gallery source)

Two self-contained pages demonstrating the deterministic pedestrian weave (PED-REALISM-1 /
`docs/PEDESTRIAN-LOWPOWER-AVOIDANCE-DESIGN.md`). No build step, no dependencies, no network.

- **`index.html`** — the on/off corridor demo: two counterflowing streams; toggle the weave to see the
  pass-through count collapse, with density + sidewalk-width sliders. Runs a faithful in-browser port of the
  engine's `Sim.Pedestrians.Lod.LateralWeave` (SplitMix64 seeded), self-checked against the C# engine on load.
- **`city.html`** — a recorded weaving-crowd playback: a ~440 m block of the synthetic demo-city (weave on),
  peds threading the real baked sidewalks with the overlap counter live. Data is an embedded snapshot from a
  `SubareaFcdRecorder --weave` run.

## How they reach GitHub Pages

The Pages site is the **auto-generated demo gallery** (`scripts/gen-demos.sh` → `site/`, deployed by the
`demos` GitHub Actions workflow — see `docs/DEMOS.md`), *not* this folder directly. These two files are
registered in `gen-demos.sh` under the **Pedestrians** category via `demo_static`, so running the gallery
build copies them to `site/ped-weave.html` and `site/ped-weave-city.html` and lists them alongside the other
pedestrian demos. To regenerate locally: `scripts/gen-demos.sh` then open `site/index.html`.

Because each ped's pose is a pure function of `(route, seed, width, time)`, both pages replay identically on
reload — the same determinism that makes `server == image-generator` bit-for-bit over the wire.
