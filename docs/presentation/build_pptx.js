// Build the SumoSharp feature deck. The 17 generated diagrams carry the explanation; the slide
// text exists to frame each one and to say what the audience should take away.
//
// Palette is taken from the diagrams themselves so deck and diagram read as one system:
// asphalt ground, amber = cars, teal = pedestrians, violet = the realism-zone construct.
const pptx = require("pptxgenjs");
const fs = require("fs");
const path = require("path");

// Title-band-cropped copies: the slide head states the title, so the image must not
// repeat it. Built by the crop step in the README recipe.
const PNG = path.join(__dirname, "build", "png-slides");
const OUT = path.join(__dirname, "build", "SumoSharp-features.pptx");

const INK = "1F2933";      // asphalt -- the dark ground
const DEEP = "161E26";     // deeper ground for title / section / close
const CARD = "2A3440";
const LIGHT = "F5F7FA";
const SLATE_L = "9AA5B1";
const AMBER = "F0B429";    // cars
const TEAL = "2BB3A3";     // pedestrians
const PED_HI = "6FE3D2";   // promoted pedestrians
const ZONE = "C7A4FF";     // the attention / realism-zone construct
const GREEN = "57A773";    // a measured outcome
const RED = "E5534B";      // a limit

const HEAD = "Calibri";    // safe list -- renders true-to-width in QA and ships with Office
const BODY = "Calibri";
const MONO = "Consolas";

const p = new pptx();
p.layout = "LAYOUT_WIDE";  // MUST precede addSlide: 13.333 x 7.5
p.author = "SumoSharp";
p.title = "SumoSharp — what it adds on top of SUMO";

const W = 13.333, H = 7.5, M = 0.55;

// ---------------------------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------------------------

// A fresh options object every time: pptxgenjs converts values to EMU in place, so a shared
// object silently corrupts the second shape that uses it.
const shadow = () => ({ type: "outer", color: "000000", blur: 14, offset: 3, angle: 90, opacity: 0.35 });

function bg(slide, color) {
  slide.background = { color };
}

/** Eyebrow + title, the fixed head of every content slide. */
function head(slide, eyebrow, title, sub) {
  slide.addText(eyebrow.toUpperCase(), {
    x: M, y: 0.3, w: 8, h: 0.28, margin: 0,
    fontFace: MONO, fontSize: 11, bold: true, color: AMBER, charSpacing: 2,
  });
  slide.addText(title, {
    x: M, y: 0.6, w: W - 2 * M, h: 0.62, margin: 0,
    fontFace: HEAD, fontSize: 30, bold: true, color: LIGHT,
  });
  if (sub) {
    slide.addText(sub, {
      x: M, y: 1.22, w: W - 2 * M - 0.3, h: 0.38, margin: 0,
      fontFace: BODY, fontSize: 14, color: SLATE_L,
    });
  }
}

/** A diagram, letterboxed into the space below the head. Returns nothing; sizes to fit. */
function diagram(slide, file, opts = {}) {
  const f = path.join(PNG, file);
  if (!fs.existsSync(f)) throw new Error("missing diagram: " + file);
  const { width, height } = pngSize(f);
  const top = opts.y !== undefined ? opts.y : 1.72;
  const availW = opts.w !== undefined ? opts.w : W - 2 * M;
  const availH = opts.h !== undefined ? opts.h : H - top - 0.75;
  const s = Math.min(availW / width, availH / height);
  const w = width * s, h = height * s;
  slide.addImage({
    path: f, x: (opts.x !== undefined ? opts.x : M) + (availW - w) / 2, y: top + (availH - h) / 2,
    w, h, ...(opts.noShadow ? {} : { shadow: shadow() }),
  });
}

/** Read a PNG's pixel dimensions from the IHDR chunk -- no image library needed. */
function pngSize(file) {
  const b = fs.readFileSync(file);
  return { width: b.readUInt32BE(16), height: b.readUInt32BE(20) };
}

/** The one-line takeaway strip at the foot of a diagram slide. */
function takeaway(slide, text, color = AMBER) {
  slide.addShape(p.ShapeType.roundRect, {
    x: M, y: H - 0.86, w: W - 2 * M, h: 0.56, fill: { color: CARD }, rectRadius: 0.06,
    line: { color, width: 1 },
  });
  slide.addText(text, {
    x: M + 0.22, y: H - 0.86, w: W - 2 * M - 0.44, h: 0.56, margin: 0,
    fontFace: BODY, fontSize: 13.5, bold: true, color: LIGHT, valign: "middle",
  });
}

/** A card of body copy -- used on the slides that argue rather than illustrate. */
function card(slide, o) {
  slide.addShape(p.ShapeType.roundRect, {
    x: o.x, y: o.y, w: o.w, h: o.h, fill: { color: o.fill || CARD }, rectRadius: 0.06,
    line: { color: o.line || CARD, width: o.line ? 1.25 : 0 }, shadow: shadow(),
  });
  let y = o.y + 0.2;
  if (o.kicker) {
    slide.addText(o.kicker.toUpperCase(), {
      x: o.x + 0.24, y, w: o.w - 0.48, h: 0.24, margin: 0,
      fontFace: MONO, fontSize: 10.5, bold: true, color: o.line || AMBER, charSpacing: 1.5,
    });
    y += 0.3;
  }
  if (o.title) {
    slide.addText(o.title, {
      x: o.x + 0.24, y, w: o.w - 0.48, h: o.titleH || 0.42, margin: 0,
      fontFace: HEAD, fontSize: o.titleSize || 19, bold: true, color: o.titleColor || LIGHT,
    });
    y += (o.titleH || 0.42) + 0.08;
  }
  if (o.lines) {
    slide.addText(
      o.lines.map((t, i) => ({
        text: typeof t === "string" ? t : t.text,
        options: {
          bullet: o.bullet ? { code: "2022" } : false,
          breakLine: i < o.lines.length - 1,
          color: typeof t === "string" ? LIGHT : (t.color || LIGHT),
          bold: typeof t === "object" && t.bold,
          paraSpaceAfter: 6,
        },
      })),
      { x: o.x + 0.24, y, w: o.w - 0.48, h: o.y + o.h - y - 0.16, margin: 0,
        fontFace: BODY, fontSize: o.size || 13, color: LIGHT, valign: "top" }
    );
  }
}

/** A big number with a small label under it. */
function stat(slide, x, y, w, value, label, color = LIGHT) {
  slide.addText(value, {
    x, y, w, h: 0.62, margin: 0, fontFace: HEAD, fontSize: 34, bold: true, color,
  });
  slide.addText(label, {
    x, y: y + 0.58, w, h: 0.5, margin: 0, fontFace: BODY, fontSize: 11.5, color: SLATE_L,
  });
}

// ---------------------------------------------------------------------------------------------
// 1. Title
// ---------------------------------------------------------------------------------------------
{
  const s = p.addSlide();
  bg(s, DEEP);
  s.addText("FEATURE PRESENTATION", {
    x: M + 0.15, y: 1.5, w: 8, h: 0.3, margin: 0,
    fontFace: MONO, fontSize: 12, bold: true, color: AMBER, charSpacing: 3,
  });
  s.addText("What SumoSharp adds\non top of SUMO", {
    x: M + 0.15, y: 1.95, w: 9.6, h: 1.9, margin: 0,
    fontFace: HEAD, fontSize: 46, bold: true, color: LIGHT, lineSpacingMultiple: 1.05,
  });
  s.addText("A parity-exact SUMO core, and the layers built on top of it: pedestrians at two levels of "
    + "detail, cars that see them, fidelity that follows the camera, and a wire protocol that sends "
    + "trajectories instead of positions.", {
    x: M + 0.15, y: 3.95, w: 9.0, h: 1.0, margin: 0,
    fontFace: BODY, fontSize: 15, color: SLATE_L,
  });

  // the three colour codes the audience needs for every later slide
  const leg = [["Cars", AMBER], ["Pedestrians", TEAL], ["Promoted to high fidelity", PED_HI],
               ["Realism zone", ZONE]];
  leg.forEach(([t, c], i) => {
    if (c === PED_HI) {
      s.addShape(p.ShapeType.ellipse, { x: M + 0.09 + i * 2.55, y: 5.29, w: 0.29, h: 0.29,
        fill: { color: DEEP }, line: { color: PED_HI, width: 1.5 } });
    }
    s.addShape(p.ShapeType.ellipse, { x: M + 0.15 + i * 2.55, y: 5.35, w: 0.17, h: 0.17, fill: { color: c } });
    s.addText(t, { x: M + 0.42 + i * 2.55, y: 5.26, w: 2.3, h: 0.34, margin: 0,
                   fontFace: BODY, fontSize: 11.5, color: SLATE_L });
  });

  s.addText("Everything here is a proof of concept. Many mechanisms, all working, none perfected.", {
    x: M + 0.15, y: 6.35, w: 10, h: 0.35, margin: 0,
    fontFace: BODY, fontSize: 13, italic: true, color: ZONE,
  });
  s.addNotes("Frame: parity is the foundation, everything else is layered on top and inert when off. "
    + "Colour code on this slide is used consistently on every later diagram -- amber cars, teal peds, "
    + "bright teal for promoted peds, violet for the realism zone. Demos come at the end.");
}

// ---------------------------------------------------------------------------------------------
// 2. How to read the numbers -- the evidence classes, stated before any number appears
// ---------------------------------------------------------------------------------------------
{
  const s = p.addSlide();
  bg(s, INK);
  head(s, "Before any number", "Three kinds of evidence, kept apart",
    "Conflating them is the fastest way to lose a technical audience, so each claim in this deck is labelled.");
  const cw = (W - 2 * M - 0.6) / 3;
  const EV = [
    { line: GREEN, kicker: "Strongest", title: "Owner-verified\nroutine operation",
      figure: "10k + 30k", figLabel: "vehicles + pedestrians, Godot 3-D",
      lines: ["Repeated first-hand use, not a captured run.",
              { text: "The strongest evidence that it works — and the headline scale claim in this deck.",
                color: SLATE_L }] },
    { line: TEAL, kicker: "Reproducible", title: "Instrumented,\nwith a committed tool",
      figure: "0.64 /car/s", figLabel: "measured replication write rate",
      lines: ["The write rate; the coupled-load bench.",
              { text: "Has a tool in the repo and a session log. Anyone can re-run it and get this number.",
                color: SLATE_L }] },
    { line: SLATE_L, kicker: "Narrowest", title: "A single\ncaptured run",
      figure: "0 of 2000", figLabel: "frames over 3x the median",
      lines: ["The GPU smoothness capture.",
              { text: "One measurement with a CSV behind it. True, and true only about that run.",
                color: SLATE_L }] },
  ];
  EV.forEach((e, i) => {
    const x = M + i * (cw + 0.3);
    card(s, { x, y: 1.95, w: cw, h: 3.7, line: e.line, kicker: e.kicker,
      title: e.title, titleH: 0.78, titleSize: 18, titleColor: e.line === SLATE_L ? LIGHT : e.line,
      lines: e.lines });
    stat(s, x + 0.24, 4.4, cw - 0.48, e.figure, e.figLabel, e.line === SLATE_L ? LIGHT : e.line);
  });
  takeaway(s, "The headline scale figure — 10 000 cars and 30 000 pedestrians — is operational experience, "
    + "not a benchmark. Where a reproducible number is needed, this deck says so.", GREEN);
  s.addNotes("Say this out loud before the first number lands. It buys credibility for everything after, "
    + "and it is the honest position: the big scale figure is routine operation, not an instrumented capture.");
}

// ---------------------------------------------------------------------------------------------
// diagram slides
// ---------------------------------------------------------------------------------------------
const DIAGRAM_SLIDES = [
  { file: "01-layering.png", eyebrow: "The premise",
    title: "Parity first — everything else is layered",
    sub: "The algorithms are copied faithfully; only the memory layout and the timing of structural mutations are rebuilt.",
    takeaway: "661 committed goldens matched every step, and every extension is inert when switched off. "
      + "That is what makes the rest of this deck safe to add.", color: GREEN,
    notes: "The goldens are small scenarios -- a handful of vehicles. City-scale runs are validated to a "
      + "statistical aggregate, not byte-for-byte. Both are real; do not blur them if asked." },

  { file: "02-seam.png", eyebrow: "The seam",
    title: "SUMO has no concept of an agent it does not control",
    sub: "External agents are injected lane-relative, and cars react with their ordinary car-following model.",
    takeaway: "Obstacles are frozen once per step, so the outcome never depends on insertion order — "
      + "which is what lets the whole thing survive parallel execution.", color: AMBER,
    notes: "The API is handle-based and generation-validated: a stale handle is an inert no-op, not a crash "
      + "or a write to a recycled slot. This one seam is what sections 3 and 4 hang off." },

  { file: "03-lod.png", eyebrow: "Pedestrians",
    title: "Two levels of detail — and the cheap one exists to look organic",
    sub: "Not a port of SUMO's person model. The low level is a closed-form pose, not a simplified solver.",
    takeaway: "The point of the cheap level is anti-uniformity: SUMO's person model reads as rails, and a "
      + "convoy of evenly spaced people is what a viewer disbelieves first.", color: TEAL,
    notes: "Low power is pose = f(route, seed, width, time): O(1) per pedestrian, zero neighbour queries. "
      + "Honest bound -- at high density roughly 15% can still overlap. High power is full ORCA and never "
      + "overlaps. State the bound before anyone finds it on screen." },

  { file: "05a-weave-walking.png", eyebrow: "Pedestrians",
    title: "The weave — uniform against organic",
    sub: "Same O(1) cost either way. The difference is entirely in what it looks like.",
    takeaway: "Pedestrians should never read as a grid or as rails. Each one keeps its own half of the "
      + "walkable width and scatters within it, from its own seed — no neighbour queries at all.",
    color: TEAL,
    notes: "The top row is the artefact, drawn deliberately uniform. The bottom row is what we do. "
      + "Both cost the same; only one is believable." },

  { file: "05b-weave-standing.png", eyebrow: "Pedestrians",
    title: "The same mechanism where crowds stand still",
    sub: "Bunching gets the same treatment as flowing — and the guarantee is narrower than it sounds.",
    takeaway: "Opposing flows cannot cross — that is structural. Same-direction pedestrians still can "
      + "overlap; assured avoidance means promoting to ORCA.", color: TEAL,
    notes: "Be precise here, it is the claim most likely to be tested on screen. Keep-right puts eastbound "
      + "and westbound on provably different halves, so opposing flows are a property of the construction. "
      + "But there is no minimum-separation enforcement, so an overtake in the same direction can pass "
      + "through. Same-direction avoidance is open work -- say so before someone spots it." },

  { file: "04-hysteresis.png", eyebrow: "Pedestrians",
    title: "Why promotion needs two radii and a dwell",
    sub: "Spatial and temporal hysteresis, for a reason that is visible rather than theoretical.",
    takeaway: "With one shared radius, a pedestrian standing on the boundary flips level every step and "
      + "visibly pops between motion models.", color: TEAL,
    notes: "This is a small slide but it pre-empts an obvious question -- why not one threshold? Because "
      + "the boundary case is the common case, not the rare one." },

  { file: "06-coupling.png", eyebrow: "Coupling",
    title: "What a car can and cannot see",
    sub: "Coupling is a level-of-detail decision. This slide is the envelope, stated before anyone finds its edge.",
    takeaway: "Inside a realism zone: assured, no interpenetration. Outside it: believable, not guaranteed. "
      + "Performance bought with believability, never with correctness.", color: AMBER,
    notes: "The mechanism is unremarkable in the best way -- Krauss car-following unchanged, with a "
      + "pedestrian disc standing in as the leader. Crossing occupancy covers low-power peds WALKING on a "
      + "crossing; promoted and paused peds are excluded. Say the limits before questions force them out." },

  { file: "07-yield.png", eyebrow: "Coupling",
    title: "Yielding on where the pedestrian will be",
    sub: "A current-overlap test cannot see a conflict that has not happened yet.",
    takeaway: "The car reacts to the predicted conflict, not to the present one — which is the difference "
      + "between stopping and arriving at the same moment as the pedestrian.", color: AMBER,
    notes: "Anticipation is why the yield reads as a driver rather than as a trigger volume." },

  { file: "13-attention.png", eyebrow: "Scalability",
    title: "Cost follows attention, not city size",
    sub: "The realism zone tracks the camera. Fidelity is spent where it is observed.",
    takeaway: "A city does not get more expensive because it is large. It gets more expensive where "
      + "someone is looking.", color: ZONE,
    notes: "This is the scalability answer and it is better than 'we made everything fast'. Headline: "
      + "10 000 vehicles and 30 000 pedestrians in routine use, with headroom on the pedestrian side. "
      + "Multiple and overlapping zones are designed, not yet built." },

  { file: "11-spatial.png", eyebrow: "Performance",
    title: "How the work is spread across cores",
    sub: "Two mechanisms, both byte-identical to a serial run. Parallelism is never allowed to cost an answer.",
    takeaway: "Each region owns a disjoint set of lanes, so region tasks are lock-free by construction "
      + "rather than by care — and a vehicle crossing a boundary needs no state transfer at all.", color: AMBER,
    notes: "If asked why the region path is off by default: today's win is modest because the hot phases "
      + "are bound by memory bandwidth on random neighbour access, not by CPU. The hard part -- disjoint "
      + "ownership, free handoff, safety by construction -- is done. A segmented store is what turns it "
      + "into a large win. Also note 8 threads beat 24: an engine that saturates every core starves the "
      + "renderer." },

  { file: "10-threaded.png", eyebrow: "Performance",
    title: "The tick runs on its own thread",
    sub: "The renderer only ever reads a published snapshot, so a frame never waits for an engine step.",
    takeaway: "Capping engine parallelism to protect the renderer was proven trajectory-inert — 11 889 "
      + "samples bitwise identical, capped versus uncapped. Smoothness cost nothing.", color: GREEN,
    notes: "0 of 2000 frames over 3x median, p99 = 1.20x p50, 2 Hz sustained, at 3 858 cars and 20 726 peds. "
      + "That is the single captured run -- label it as such." },

  { file: "14-dr.png", eyebrow: "Integration",
    title: "Dead reckoning: 48 bytes buys a trajectory",
    sub: "The receiver is never told where a car is. It is told enough to work out where it will be.",
    takeaway: "0.64 updates per car per second — about 94× fewer messages than the render rate — with "
      + "motion still reconstructing smoothly at 60 fps. Ambient pedestrians send nothing at all.", color: TEAL,
    notes: "Sent once up front: lane geometry (2.86 MiB for a whole city cut) and per-agent identity and "
      + "dimensions. Per update: car 48 B, reactive ped 18 B, ambient ped 0 B. Sent only when dead "
      + "reckoning would otherwise be wrong. Bandwidth is simply not the constraint -- drop it from the "
      + "argument." },

  { file: "08-lanechange.png", eyebrow: "Integration",
    title: "A counter-intuitive finding about what drives the wire",
    sub: "Half the updates are triggered by a change of lane identity — and almost none of those are lane changes.",
    takeaway: "Only ~0.7% of updates are a real lateral lane change. The rest is cars driving straight onto "
      + "the next lane. It is a property of the network's granularity, not of the traffic.", color: RED,
    notes: "Worth including because it redirects optimisation effort. On a dense urban cut most lanes are "
      + "short internal junction lanes, and position on the wire is measured along a specific lane -- so a "
      + "new lane must be published. No publish threshold reaches it." },

  { file: "09-server-ig.png", eyebrow: "Integration",
    title: "The crowd costs almost nothing on the wire",
    sub: "A closed-form pose is a pure function, so every observer evaluates it and gets bit-identical results.",
    takeaway: "A route or timeline is broadcast once; ambient pedestrians then emit zero per-step bytes. "
      + "Crowd size is decoupled from bandwidth entirely.", color: TEAL,
    notes: "Proven over an in-process byte loopback and over real DDS. This is what makes 30 000 "
      + "pedestrians free on the network." },

  { file: "15-liveliness.png", eyebrow: "City life",
    title: "City life is authored data, not another behaviour loop",
    sub: "Four segment kinds — Walk, Pause, Dwell, Interact — compose into every beat.",
    takeaway: "A living city costs what a walking city costs. Liveliness adds richer one-time data, not a "
      + "per-step behaviour loop — and stays exactly as reconstructable.", color: TEAL,
    notes: "Checking a phone is a Pause with an animation tag. Meeting someone is a paired Interact written "
      + "into both timelines with one agreed meet point. Outdoor tables are a looping door-table-serve "
      + "schedule. Boarding a car removes the person from the crowd entirely. What is NOT built is the "
      + "director that places these across a whole city from venue records -- the vocabulary exists, the "
      + "authoring at scale is next." },

  { file: "12-evac.png", eyebrow: "Beyond traffic",
    title: "Evacuation — on a completely unmodified driving core",
    sub: "Fear spreads as local information: occlusion-gated line of sight, contagion, and unease from being stuck.",
    takeaway: "This is the proof that the layering works. The evacuation layer drives the engine through "
      + "the same public seams any integrator would use — and with panic off, the determinism hash "
      + "does not move.", color: RED,
    notes: "Drivers switch to an aggressive preset and reroute to exits; the streets jam; a boxed-in driver "
      + "noses onto the shoulder, abandons the car, and its occupants flee on foot. Cost follows the "
      + "incident, not the map -- the layer attaches only within a bounded working region." },

  { file: "16-headroom.png", eyebrow: "Where this goes",
    title: "Already fast — and the remaining headroom is already located",
    sub: "Every entry on the list came out of a measurement, which is why it is specific rather than aspirational.",
    takeaway: "There is a lot of headroom and we know exactly where it is. Nothing on that list is a "
      + "mystery — each one is measured and attributed to a named cause.", color: GREEN,
    notes: "Emphasis is on the shape, not the figures. Landed: allocation on the hot path collapsed, GC "
      + "pressure down to a small fraction of wall time, parallel by default at scale and byte-identical "
      + "by test, faster per tick than SUMO even single-threaded. The largest single lever is the crowd "
      + "solver's neighbour cap -- it changes pedestrian trajectories, so it ships opt-in and off by "
      + "default. That is a deliberate choice, not an oversight." },
];

DIAGRAM_SLIDES.forEach((d) => {
  const s = p.addSlide();
  bg(s, INK);
  head(s, d.eyebrow, d.title, d.sub);
  diagram(s, d.file, { y: 1.74, h: H - 1.74 - 0.98 });
  takeaway(s, d.takeaway, d.color);
  s.addNotes(d.notes);
});

// ---------------------------------------------------------------------------------------------
// Current state -- the substrate slide, argued rather than illustrated
// ---------------------------------------------------------------------------------------------
{
  const s = p.addSlide();
  bg(s, INK);
  head(s, "Current state", "A substrate, not a finished product",
    "Many mechanisms, all working, none perfected — and that is the correct state rather than a shortfall.");
  card(s, { x: M, y: 1.9, w: 6.05, h: 2.6, line: AMBER, kicker: "Why this is the right state",
    title: "Polishing the wrong mechanism is the expensive mistake", titleH: 0.72, titleSize: 17,
    lines: ["Which mechanisms matter was not knowable in advance. So each was taken to the point where it "
            + "is proven and honest, then stopped.",
            { text: "The result is unusual breadth at a deliberately uniform depth.", color: AMBER,
              bold: true }] });
  card(s, { x: M + 6.35, y: 1.9, w: 6.05, h: 2.6, line: GREEN, kicker: "Why redirecting is cheap",
    title: "We own every line", titleH: 0.42, titleSize: 17, titleColor: GREEN,
    lines: ["There is no upstream fork to maintain, so none of this is a dependency on someone else's "
            + "roadmap.",
            "The parity gate makes change safe. The seams are already public.",
            { text: "Point at any of it and it becomes production work.", color: GREEN, bold: true }] });
  card(s, { x: M, y: 4.7, w: W - 2 * M, h: 2.15, line: SLATE_L,
    kicker: "The open items, stated plainly", size: 12.5, bullet: true,
    lines: ["Same-direction low-power pedestrians can overlap; assured avoidance means promoting to ORCA.",
            "Outside a realism zone, cars do not see pedestrians that are off a crossing.",
            "Junction discharge trails SUMO's: the halting fraction matches almost exactly and the routes "
            + "are identical, but our cars roll slower. Localised, and being chased with a per-vehicle trace.",
            "A long-standing car-to-car overlap of about 3 m on internal junction lanes — present "
            + "before this work and not a regression.",
            "The full lateral / sublane model is deferred: lane-change timing is landed and parity-exact, "
            + "the lateral position model is not."] });
  s.addNotes("Do not apologise for this slide -- it is the strongest slide in the deck if delivered as a "
    + "choice. The open items are listed because we know them, measured them, and can point at each one. "
    + "That is a different position from not knowing.");
}

// ---------------------------------------------------------------------------------------------
// Demos -- deliberately last
// ---------------------------------------------------------------------------------------------
{
  const s = p.addSlide();
  bg(s, DEEP);
  head(s, "Now the demonstrations", "What to watch for",
    "Deliberately last: they land far harder once you know what the mechanisms are.");
  card(s, { x: M, y: 1.9, w: 6.05, h: 2.45, line: ZONE, kicker: "Impression",
    title: "The real image generator,\non real city terrain", titleH: 0.85, titleSize: 20, titleColor: ZONE,
    lines: ["The point is not scale but plausibility.",
            { text: "Watch for: terrain-following ground; pedestrians at correct heights weaving on the "
              + "pavements rather than marching; cars yielding at crossings.", color: SLATE_L }] });
  stat(s, M + 0.24, 4.6, 5.6, "1 000 + 1 000", "vehicles and pedestrians, on real terrain", ZONE);
  s.addText("Nobody moves on rails, and nothing about the crowd reads as a grid.", {
    x: M + 0.24, y: 5.5, w: 5.6, h: 0.4, margin: 0,
    fontFace: BODY, fontSize: 13, bold: true, color: ZONE,
  });

  card(s, { x: M + 6.35, y: 1.9, w: 6.05, h: 2.45, line: AMBER, kicker: "Performance",
    title: "The Godot 3-D viewer,\nsame city, full scale", titleH: 0.85, titleSize: 20, titleColor: AMBER,
    lines: ["Where the headline figure is real rather than quoted.",
            { text: "Watch for: the camera zone moving and fidelity following it — pedestrians promoting "
              + "to ORCA as it arrives, demoting as it leaves.", color: SLATE_L }] });
  stat(s, M + 6.59, 4.6, 5.6, "10 000 + 30 000", "vehicles and pedestrians, same city", AMBER);
  s.addText("The frame rate does not care how large the city is.", {
    x: M + 6.59, y: 5.5, w: 5.6, h: 0.4, margin: 0,
    fontFace: BODY, fontSize: 13, bold: true, color: AMBER,
  });

  takeaway(s, "Both demos run the same engine. The difference is only where fidelity is being spent.", ZONE);
  s.addNotes("Impression demo first, then performance. Call out the zone boundary explicitly during the "
    + "performance demo -- it is the one thing an audience will not spot unaided.");
}

// ---------------------------------------------------------------------------------------------
// Close
// ---------------------------------------------------------------------------------------------
{
  const s = p.addSlide();
  bg(s, DEEP);
  s.addText("The engine is flexible.\nWhat you have seen is a demonstration.", {
    x: M + 0.15, y: 1.7, w: 11.5, h: 1.6, margin: 0,
    fontFace: HEAD, fontSize: 36, bold: true, color: LIGHT, lineSpacingMultiple: 1.08,
  });
  s.addText("Lots of mechanisms implemented, none of them perfected. Every one of them is ours, so every "
    + "one of them is open — pick the direction and it becomes production work.", {
    x: M + 0.15, y: 3.45, w: 10.4, h: 0.9, margin: 0,
    fontFace: BODY, fontSize: 16, color: SLATE_L,
  });
  const items = [["We own the whole stack", "No upstream fork, no one else's roadmap.", GREEN],
                 ["Parity is a gate, not a hope", "The goldens make change safe to make.", AMBER],
                 ["The seams are already public", "Integrators drive it the same way our own layers do.", TEAL]];
  items.forEach(([t, d, c], i) => {
    const x = M + 0.15 + i * 4.05;
    s.addShape(p.ShapeType.ellipse, { x, y: 4.75, w: 0.2, h: 0.2, fill: { color: c } });
    s.addText(t, { x: x + 0.32, y: 4.62, w: 3.5, h: 0.36, margin: 0,
                   fontFace: HEAD, fontSize: 14.5, bold: true, color: LIGHT });
    s.addText(d, { x: x + 0.32, y: 4.96, w: 3.5, h: 0.6, margin: 0,
                   fontFace: BODY, fontSize: 12, color: SLATE_L });
  });
  s.addText("Everything in this deck is a proof of concept — by choice, and measured, so we know where "
    + "we stand.", {
    x: M + 0.15, y: 6.3, w: 11, h: 0.4, margin: 0,
    fontFace: BODY, fontSize: 13, italic: true, color: ZONE,
  });
  s.addNotes("Close on flexibility and ownership, not on a feature list. The ask is a direction to point at.");
}

p.writeFile({ fileName: OUT }).then(() => console.log("wrote " + OUT));
