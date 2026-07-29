#!/usr/bin/env python3
"""Generate the SumoSharp presentation diagram set as standalone SVGs.

Semantic palette -- colour carries meaning across every diagram, so a reader learns it once:
  AMBER = vehicles / the car side        TEAL = pedestrians / the crowd side
  LIGHT = the untouched SUMO parity core  SLATE = structure, plumbing, our additions
  RED   = a problem, a limit, a refuted thing
"""
import math
import pathlib

OUT = pathlib.Path(__file__).parent / "svg"
OUT.mkdir(parents=True, exist_ok=True)

INK = "#1F2933"      # asphalt -- deck background / strong text
SLATE = "#52606D"    # structure
SLATE_L = "#9AA5B1"  # muted
LIGHT = "#F5F7FA"
CARD = "#2A3440"     # card fill on dark
AMBER = "#F0B429"    # CARS
TEAL = "#2BB3A3"     # PEDS
RED = "#E5534B"
GREEN = "#57A773"
PED_HI = "#6FE3D2"   # PEDS, promoted to high power -- same hue family as TEAL, deliberately NOT amber
ZONE = "#C7A4FF"     # the attention / realism-zone construct: neither a car nor a pedestrian
FONT = "Calibri, Arial, sans-serif"
MONO = "Consolas, 'Courier New', monospace"


def svg(w, h, body, bg=INK):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" '
            f'viewBox="0 0 {w} {h}" font-family="{FONT}">'
            f'<rect width="{w}" height="{h}" fill="{bg}"/>{body}</svg>')


def txt(x, y, s, size=15, fill=LIGHT, anchor="start", weight="normal", font=None, style=""):
    # Escape here, not at every call site. A single bare "&" in a label ("Car following & lane changing")
    # makes the whole SVG un-parseable, and the failure surfaces as a column offset in an XML error rather
    # than anything pointing at the label -- so the helper owns it and no future label can reintroduce it.
    s = str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    return (f'<text x="{x}" y="{y}" font-size="{size}" fill="{fill}" text-anchor="{anchor}" '
            f'font-weight="{weight}" font-family="{font or FONT}" {style}>{s}</text>')


def card(x, y, w, h, fill=CARD, stroke="none", r=10, sw=1.5, op=1.0):
    return (f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{r}" fill="{fill}" '
            f'stroke="{stroke}" stroke-width="{sw}" opacity="{op}"/>')


def arrow(x1, y1, x2, y2, color=SLATE_L, w=2.2, dash="", head=True):
    mid = (f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{color}" stroke-width="{w}" '
           f'{"stroke-dasharray=" + chr(34) + dash + chr(34) if dash else ""} '
           f'{"marker-end=" + chr(34) + "url(#ah-" + color.lstrip(chr(35)) + ")" + chr(34) if head else ""}/>')
    return mid


ALL_COLORS = [INK, SLATE, SLATE_L, LIGHT, CARD, AMBER, TEAL, RED, GREEN, PED_HI, ZONE, "#7FD8CE"]


def defs(colors=None):
    # Declare a marker for EVERY palette colour, always. An arrow referencing an undeclared marker
    # renders headless in browsers and hard-crashes cairosvg, and the failure is easy to introduce by
    # adding one arrow in a colour the caller forgot to list -- so remove the chance to get it wrong.
    d = "".join(
        f'<marker id="ah-{c.lstrip(chr(35))}" viewBox="0 0 10 10" refX="9" refY="5" '
        f'markerWidth="6" markerHeight="6" orient="auto-start-reverse">'
        f'<path d="M 0 1 L 10 5 L 0 9 z" fill="{c}"/></marker>' for c in ALL_COLORS)
    return f"<defs>{d}</defs>"


def car(x, y, s=1.0, color=AMBER, rot=0):
    return (f'<g transform="translate({x},{y}) rotate({rot}) scale({s})">'
            f'<rect x="-13" y="-7" width="26" height="14" rx="3" fill="{color}"/>'
            f'<rect x="-5" y="-5" width="9" height="10" rx="1.5" fill="{INK}" opacity="0.45"/></g>')


def ped(x, y, r=5, color=TEAL, op=1.0):
    return f'<circle cx="{x}" cy="{y}" r="{r}" fill="{color}" opacity="{op}"/>'


def ped_hi(x, y, r=7):
    """A PROMOTED pedestrian. Teal family with a light halo -- promotion must be visible WITHOUT
    borrowing amber, which is reserved deck-wide for vehicles."""
    return (f'<circle cx="{x}" cy="{y}" r="{r + 3.5}" fill="none" stroke="{LIGHT}" '
            f'stroke-width="1.3" opacity="0.55"/>'
            f'<circle cx="{x}" cy="{y}" r="{r}" fill="{PED_HI}"/>')


def stats(x, y, items, size=15, gap=None, label_col=None, val_col=None):
    """Lay out `[(label, value), ...]` as separate <text> at explicit x positions.

    NEVER use runs of spaces to space stats inside one string: XML collapses repeated whitespace, so the
    gaps vanish at render time and four stats become one unreadable run-on. Visual QA caught exactly that
    on a customer-facing slide.
    """
    gap = gap or 1140 // max(len(items), 1)
    o = ""
    for i, (lab, val) in enumerate(items):
        cx = x + i * gap
        o += txt(cx, y, lab, size - 3, label_col or SLATE_L)
        o += txt(cx, y + 26, val, size + 4, val_col or LIGHT, weight="bold")
    return o


def ped_band(x0, x1, ymid, half, seed, color=TEAL, n=26, r=7, side=None):
    """Scatter peds across a band with real lateral variance.

    The point of low power is ANTI-UNIFORMITY: SUMO's person model reads as rails, and rows of evenly
    spaced dots would depict exactly the thing this mechanism exists to avoid. `side` = +1 / -1 keeps a
    stream on its own half (that part IS structural) while still scattering inside it.
    """
    s = seed
    o = ""
    for i in range(n):
        s = (1103515245 * s + 12345) & 0x7FFFFFFF
        fx = ((s >> 8) % 10000) / 10000.0
        s = (1103515245 * s + 12345) & 0x7FFFFFFF
        fy = ((s >> 8) % 10000) / 10000.0
        px = x0 + fx * (x1 - x0)
        if side is None:
            py = ymid + (fy - 0.5) * 2 * half
        else:
            py = ymid + side * (0.16 + 0.78 * fy) * half
        o += ped(px, py, r, color, 0.93)
    return o


def title(s, sub=None, w=1280):
    o = txt(56, 62, s, 30, LIGHT, weight="bold")
    if sub:
        o += txt(56, 92, sub, 16, SLATE_L)
    return o


# ----------------------------------------------------------------------------------------------
# 1. Layering: the parity core is untouched; everything else is additive and inert-when-absent
# ----------------------------------------------------------------------------------------------
def d_layering():
    b = defs([AMBER, TEAL, SLATE_L])
    b += title("Everything is layered on an untouched parity core",
               "Each ring only drives the one inside it through public seams. Absent ⇒ byte-identical.")
    cx, cy = 400, 330
    # Ring strokes carry the SAME colour as the legend dot beside them, and each is named on the ring
    # itself -- without that the reader cannot tell which ring the legend is talking about.
    rings = [
        (215, "#243040", "Sim.Evac", SLATE_L),
        (172, "#26333f", "LiveCity", SLATE_L),
        (130, "#2b3a44", "Sim.Pedestrians", TEAL),
        (88, "#31424a", "External agents", AMBER),
    ]
    for r, fill, name, col in rings:
        b += (f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="{fill}" stroke="{col}" '
              f'stroke-width="1.6" opacity="0.95"/>')
        b += f'<rect x="{cx - 62}" y="{cy - r - 11}" width="124" height="21" rx="10" fill="{fill}"/>'
        b += txt(cx, cy - r + 4, name, 11.5, col, "middle", "bold")
    b += f'<circle cx="{cx}" cy="{cy}" r="52" fill="{LIGHT}"/>'
    b += txt(cx, cy - 4, "SUMO", 19, INK, "middle", "bold")
    b += txt(cx, cy + 15, "parity core", 12, SLATE, "middle")
    lx = 700
    labels = [("External agents seam", AMBER, "Cars react to things SUMO never controlled"),
              ("Sim.Pedestrians", TEAL, "Navmesh, O/D demand, two-level LOD, weave"),
              ("LiveCity", SLATE_L, "One net, cars and crowd coupled, realism zones"),
              ("Sim.Evac", SLATE_L, "A whole behavioural model, core unmodified")]
    y = 232
    for name, col, desc in labels:
        b += f'<circle cx="{lx}" cy="{y - 5}" r="7" fill="{col}"/>'
        b += txt(lx + 20, y, name, 17, LIGHT, weight="bold")
        b += txt(lx + 20, y + 21, desc, 14, SLATE_L)
        y += 62
    b += card(lx - 4, y + 4, 520, 62, "#243040", GREEN, 8, 1.2)
    b += txt(lx + 16, y + 30, "661 goldens byte-identical · par == single", 15, GREEN, weight="bold")
    b += txt(lx + 16, y + 50, "The core does not know the outer rings exist.", 13, SLATE_L)
    return svg(1280, 620, b)


# ----------------------------------------------------------------------------------------------
# 2. The seam SUMO does not have
# ----------------------------------------------------------------------------------------------
def d_seam():
    b = defs([AMBER, TEAL, RED, SLATE_L])
    b += title("The seam SUMO does not have",
               "SUMO has no concept of an agent it does not control. Cars here react to injected agents.")
    for i, (x0, head, note) in enumerate([(70, "Stock SUMO", "A closed world: only what SUMO spawns exists."),
                                          (680, "SumoSharp", "Anything can be injected. Cars respond.")]):
        b += card(x0, 130, 530, 380, "#243040", SLATE, 12, 1.2)
        b += txt(x0 + 26, 168, head, 20, LIGHT, weight="bold")
        b += txt(x0 + 26, 192, note, 13, SLATE_L)
        b += f'<rect x="{x0 + 26}" y="250" width="478" height="86" fill="#1a222c"/>'
        b += (f'<line x1="{x0 + 26}" y1="293" x2="{x0 + 504}" y2="293" stroke="{AMBER}" '
              f'stroke-width="2" stroke-dasharray="14 12" opacity="0.5"/>')
        for k in range(3):
            b += car(x0 + 90 + k * 130, 271)
        if i == 1:
            b += ped(x0 + 250, 293, 8)
            b += ped(x0 + 262, 306, 7)
            b += txt(x0 + 236, 340, "injected agent", 12, TEAL, "middle")
            b += arrow(x0 + 220, 271, x0 + 200, 271, AMBER, 2.4)
            b += txt(x0 + 26, 380, "Cars brake, treat it as a leader, veto a lane change,", 14, LIGHT)
            b += txt(x0 + 26, 400, "swerve in-lane, spill to a safe lane — then resume.", 14, LIGHT)
            b += txt(x0 + 26, 436, "Frozen once per step ⇒ order-independent,", 13, GREEN)
            b += txt(x0 + 26, 456, "so it survives parallel execution.", 13, GREEN)
            b += txt(x0 + 26, 488, "Handle-based, generation-validated: a stale", 12, SLATE_L)
            b += txt(x0 + 26, 504, "handle is an inert no-op, never a wrong write.", 12, SLATE_L)
        else:
            b += txt(x0 + 26, 380, "Pedestrians, crowds, live detections:", 14, SLATE_L)
            b += txt(x0 + 26, 400, "not representable. Cars cannot react", 14, SLATE_L)
            b += txt(x0 + 26, 420, "to what the model cannot express.", 14, SLATE_L)
            b += f'<circle cx="{x0 + 250}" cy="293" r="8" fill="none" stroke="{RED}" stroke-width="2"/>'
            b += (f'<line x1="{x0 + 244}" y1="287" x2="{x0 + 256}" y2="299" stroke="{RED}" stroke-width="2"/>'
                  f'<line x1="{x0 + 256}" y1="287" x2="{x0 + 244}" y2="299" stroke="{RED}" stroke-width="2"/>')
    return svg(1280, 560, b)


# ----------------------------------------------------------------------------------------------
# 3. Two-level LOD
# ----------------------------------------------------------------------------------------------
def d_lod():
    b = defs()
    b += title("Two-level pedestrian LOD",
               "The cheap level exists to look ORGANIC, not merely to be cheap — uniform, rail-like crowds "
               "are the thing it removes.")
    b += card(70, 130, 540, 430, "#243040", TEAL, 12, 1.4)
    b += txt(96, 168, "LOW POWER", 18, TEAL, weight="bold")
    b += txt(96, 192, "pose = f(route, seed, width, time)", 14, LIGHT, font=MONO)
    b += txt(96, 224, "Weaves and spreads across the walkable width.", 15, LIGHT)
    b += txt(96, 246, "Keeps its own side, scatters within it.", 15, TEAL, weight="bold")
    b += txt(96, 274, "O(1) per pedestrian, zero neighbour queries.", 13, SLATE_L)
    b += f'<rect x="96" y="300" width="488" height="118" rx="6" fill="#1a222c"/>'
    b += (f'<line x1="96" y1="359" x2="584" y2="359" stroke="{SLATE}" stroke-width="1" '
          f'stroke-dasharray="6 8" opacity="0.5"/>')
    b += ped_band(112, 570, 359, 46, 4242, TEAL, 20, 7, side=-1)
    b += ped_band(112, 570, 359, 46, 9191, "#7FD8CE", 20, 7, side=+1)
    b += txt(96, 440, "No grid. No rails. No convoy.", 14, TEAL, weight="bold")
    b += card(96, 458, 488, 86, "#2e2a22", AMBER, 8, 1.2)
    b += txt(116, 486, "Honest bound", 13, AMBER, weight="bold")
    b += txt(116, 508, "At high density roughly 15% can still overlap —", 12.5, LIGHT)
    b += txt(116, 528, "believable, not collision-free. That is the trade.", 12.5, LIGHT)

    b += card(670, 130, 540, 430, "#243040", PED_HI, 12, 1.4)
    b += txt(696, 168, "HIGH POWER", 18, PED_HI, weight="bold")
    b += txt(696, 192, "full ORCA reciprocal avoidance", 14, LIGHT, font=MONO)
    b += txt(696, 224, "A real agent in a persistent crowd solver.", 15, LIGHT)
    b += txt(696, 246, "Never overlaps. Avoidance is assured.", 15, PED_HI, weight="bold")
    b += txt(696, 274, "The level cars can see and yield to.", 13, SLATE_L)
    b += f'<rect x="696" y="300" width="488" height="118" rx="6" fill="#1a222c"/>'
    for i in range(16):
        rr = 44 * math.sqrt((i + 0.4) / 16)
        th = i * 2.39996323
        b += ped_hi(940 + rr * math.cos(th) * 4.6, 359 + rr * math.sin(th), 7)
    b += txt(696, 440, "Negotiated every step, so nobody interpenetrates.", 14, PED_HI, weight="bold")
    b += card(696, 458, 488, 86, "#1e2a34", GREEN, 8, 1.2)
    b += txt(716, 486, "The guarantee", 13, GREEN, weight="bold")
    b += txt(716, 508, "Promote wherever avoidance must actually hold —", 12.5, LIGHT)
    b += txt(716, 528, "the camera zone, an incident, anywhere you choose.", 12.5, LIGHT)
    return svg(1280, 600, b)


# ----------------------------------------------------------------------------------------------
# 4. Promote / demote hysteresis
# ----------------------------------------------------------------------------------------------
def d_hysteresis():
    b = defs([TEAL, AMBER, RED, SLATE_L])
    b += title("Why promotion needs two radii and a dwell",
               "One shared radius makes a pedestrian standing on it flip level every single step.")
    cx, cy = 340, 330
    b += f'<circle cx="{cx}" cy="{cy}" r="160" fill="{ZONE}" opacity="0.09"/>'
    b += f'<circle cx="{cx}" cy="{cy}" r="160" fill="none" stroke="{ZONE}" stroke-width="1.6" stroke-dasharray="7 6"/>'
    b += f'<circle cx="{cx}" cy="{cy}" r="90" fill="{ZONE}" opacity="0.16"/>'
    b += f'<circle cx="{cx}" cy="{cy}" r="90" fill="none" stroke="{ZONE}" stroke-width="2"/>'
    b += f'<circle cx="{cx}" cy="{cy}" r="9" fill="{LIGHT}"/>'
    b += txt(cx, cy + 32, "interest source", 12, SLATE_L, "middle")
    b += txt(cx, cy - 100, "promote", 13, ZONE, "middle", "bold")
    b += txt(cx, cy - 170, "demote", 13, ZONE, "middle", "bold")
    b += ped_hi(cx - 55, cy + 55, 8)
    b += ped(cx + 120, cy - 60, 7, TEAL)
    b += ped(cx + 210, cy + 30, 7, TEAL)
    b += arrow(cx + 205, cy + 20, cx + 130, cy - 30, SLATE_L, 1.6, "4 4")

    x0 = 700
    b += txt(x0, 168, "Spatial hysteresis", 18, LIGHT, weight="bold")
    b += txt(x0, 194, "Promote inside the inner radius; demote only once", 14, SLATE_L)
    b += txt(x0, 214, "continuously outside the LARGER one. The gap between", 14, SLATE_L)
    b += txt(x0, 234, "them is what stops the oscillation.", 14, SLATE_L)
    b += txt(x0, 282, "Temporal hysteresis", 18, LIGHT, weight="bold")
    b += txt(x0, 308, "A dwell time floors how fast a level may change at all,", 14, SLATE_L)
    b += txt(x0, 328, "so a pedestrian loitering at the boundary is stable.", 14, SLATE_L)
    b += card(x0, 366, 500, 92, "#2e2226", RED, 8, 1.2)
    b += txt(x0 + 20, 394, "Without both:", 14, RED, weight="bold")
    b += txt(x0 + 20, 416, "promote / demote / promote every step — the ped", 13, LIGHT)
    b += txt(x0 + 20, 436, "visibly pops between motion models on screen.", 13, LIGHT)
    return svg(1280, 540, b)


# ----------------------------------------------------------------------------------------------
# 5. THE WEAVE -- the one the owner called out
# ----------------------------------------------------------------------------------------------
def d_weave():
    b = defs([TEAL, RED, GREEN, SLATE_L])
    b += title("The deterministic weave",
               "Opposing flows are kept apart by construction — at O(1) per pedestrian, no neighbour queries.")
    for i, (y0, head, col, note) in enumerate([
            (150, "Uniform — the artefact", RED,
             "Evenly spaced, all on the centreline. This is what reads as rails."),
            (350, "Weave — what we do instead", GREEN,
             "Each ped keeps its own half and scatters within it. Same O(1) cost.")]):
        b += txt(70, y0 + 6, head, 18, col, weight="bold")
        b += txt(70 + 340, y0 + 6, note, 14, SLATE_L)
        b += f'<rect x="70" y="{y0 + 24}" width="1030" height="112" rx="6" fill="#1a222c"/>'
        b += (f'<line x1="70" y1="{y0 + 80}" x2="1100" y2="{y0 + 80}" stroke="{SLATE}" '
              f'stroke-width="1" stroke-dasharray="6 8" opacity="0.7"/>')
        b += txt(1112, y0 + 34, "pavement edge", 11, SLATE_L)
        b += txt(1112, y0 + 84, "centreline", 11, SLATE_L)
        if i == 0:
            # Deliberately uniform: this row exists to show the artefact, so regularity is the point.
            for k in range(14):
                px = 108 + k * 70
                b += ped(px, y0 + 80, 8, TEAL, 0.9)
                b += ped(px + 26, y0 + 80, 8, "#7FD8CE", 0.9)
        else:
            # Real lateral variance on each side of the centreline -- never rows, never a stride.
            b += ped_band(96, 1082, y0 + 80, 40, 7717, TEAL, 28, 8, side=-1)
            b += ped_band(96, 1082, y0 + 80, 40, 3313, "#7FD8CE", 28, 8, side=+1)
        if i == 1:
            b += arrow(120, y0 + 34, 300, y0 + 34, TEAL, 2)
            b += txt(310, y0 + 39, "eastbound", 11, TEAL)
            b += arrow(1060, y0 + 126, 880, y0 + 126, "#7FD8CE", 2)
            b += txt(870, y0 + 131, "westbound", 11, "#7FD8CE", "end")

    # The same spreading applies where crowds BUNCH, not only where they flow: at a kerb on red, a
    # single waiting vertex would stack every pedestrian on one point. The owner called this out
    # specifically, and it is the case a viewer notices first.
    b += txt(70, 536, "And where they stop, not only where they walk", 17, LIGHT, weight="bold")
    b += txt(70, 560, "Waiting at a red crossing is the case a viewer notices first.", 14, SLATE_L)
    for i, (x0, head, col) in enumerate([(70, "One waiting vertex", RED), (655, "Seeded waiting spread", GREEN)]):
        b += card(x0, 578, 555, 168, "#1a222c", col, 10, 1.3)
        b += txt(x0 + 24, 606, head, 15, col, weight="bold")
        b += (f'<line x1="{x0 + 24}" y1="712" x2="{x0 + 530}" y2="712" stroke="{SLATE}" '
              f'stroke-width="2"/>')
        b += txt(x0 + 24, 736, "kerb", 11, SLATE_L)
        b += f'<rect x="{x0 + 250}" y="620" width="58" height="92" fill="{LIGHT}" opacity="0.05"/>'
        b += txt(x0 + 279, 636, "crossing", 10, SLATE_L, "middle")
        if i == 0:
            for k in range(14):
                b += ped(x0 + 279 + (k % 3 - 1) * 2, 700 - (k % 2) * 2, 8, TEAL, 0.5)
            b += txt(x0 + 330, 690, "23 peds on one point", 12, RED)
        else:
            spots = [(-52, -6), (-34, 4), (-18, -10), (-4, 2), (12, -6), (28, 6), (46, -4),
                     (-44, 14), (-26, 18), (-8, 16), (10, 20), (30, 16), (48, 12), (62, 0)]
            for dx, dy in spots:
                b += ped(x0 + 279 + dx, 694 + dy, 7, TEAL, 0.95)
            b += txt(x0 + 358, 690, "busiest 0.5 m cell: 2.5%", 12, GREEN)
    b += card(70, 764, 1140, 74, "#243040", "none", 10)
    b += txt(94, 792, "Both are the same mechanism: a per-pedestrian seeded offset, evaluated not solved.", 14,
             LIGHT, weight="bold")
    b += txt(94, 816, "No neighbour lookups, no iteration, no shared state — every observer derives the "
                      "identical offset independently.", 13, SLATE_L)

    # What the mechanism does and does not guarantee. Stating only the win here would be the exact
    # overstatement the project's own design doc warns against.
    b += txt(70, 872, "What it guarantees, and what it does not", 17, LIGHT, weight="bold")
    b += card(70, 892, 555, 104, "#1e2a34", GREEN, 8, 1.2)
    b += txt(94, 922, "Opposing flows: guaranteed, by construction", 14, GREEN, weight="bold")
    b += txt(94, 946, "The keep-right offset puts east and west on provably", 12.5, LIGHT)
    b += txt(94, 966, "different halves. They cannot cross into each other.", 12.5, LIGHT)
    b += card(655, 892, 555, 104, "#2e2a22", AMBER, 8, 1.2)
    b += txt(679, 922, "Same direction: they can still overlap", 14, AMBER, weight="bold")
    b += txt(679, 946, "There is no minimum-separation enforcement, so one", 12.5, LIGHT)
    b += txt(679, 966, "pedestrian overtaking another can pass through it.", 12.5, LIGHT)
    return svg(1280, 1026, b)


# ----------------------------------------------------------------------------------------------
# 6. The coupling seam + visibility asymmetry
# ----------------------------------------------------------------------------------------------
def d_coupling():
    b = defs()
    b += title("What a car can and cannot see",
               "Coupling is a level-of-detail decision, not a feature list. This is the envelope.")

    # The trade IS the message, so it gets the top half and the full width.
    b += card(70, 130, 555, 258, "#1e2a34", GREEN, 12, 1.5)
    b += txt(96, 170, "INSIDE A REALISM ZONE", 15, GREEN, weight="bold")
    b += txt(96, 200, "Assured", 30, LIGHT, weight="bold")
    b += txt(96, 236, "Pedestrians are promoted to full ORCA.", 14.5, LIGHT)
    b += txt(96, 260, "They negotiate with each other, and cars yield.", 14.5, LIGHT)
    for k, line in enumerate(["No pedestrian interpenetrates another.",
                              "No car passes through a pedestrian.",
                              "Holds anywhere you place a zone."]):
        b += f'<circle cx="104" cy="{292 + k * 26}" r="3.5" fill="{GREEN}"/>'
        b += txt(118, 297 + k * 26, line, 13, LIGHT)

    b += card(655, 130, 555, 258, "#2e2a22", AMBER, 12, 1.5)
    b += txt(681, 170, "OUTSIDE IT", 15, AMBER, weight="bold")
    b += txt(681, 200, "Believable", 30, LIGHT, weight="bold")
    b += txt(681, 236, "Cheap and convincing at distance — by choice.", 14.5, LIGHT)
    b += txt(681, 260, "Performance bought with believability, not correctness.", 14.5, LIGHT)
    for k, line in enumerate(["Same-direction pedestrians can overlap.",
                              "A car can pass over a ped off a crossing.",
                              "On a crossing a car DOES stop."]):
        col = GREEN if line.startswith("On a crossing") else AMBER
        b += f'<circle cx="689" cy="{292 + k * 26}" r="3.5" fill="{col}"/>'
        b += txt(703, 297 + k * 26, line, 13, LIGHT)

    b += card(70, 404, 1140, 52, "#243040", "none", 8)
    b += txt(94, 436, "Stated up front it reads as engineering. Discovered under questioning it reads as a "
                      "defect — so state it up front.", 14.5, LIGHT, weight="bold")

    # The plumbing is demoted to one compact row: unremarkable in the best way.
    b += txt(70, 506, "How it is wired — deliberately unremarkable", 16, SLATE_L, weight="bold")
    for k, (x0, w, head, l1, l2, col) in enumerate([
            (70, 340, "Promoted ped footprints", "High-power ORCA agents only.",
             "Rich, per-agent reactions.", TEAL),
            (440, 340, "Crossing occupancy", "Low-power peds WALKING on a crossing.",
             "Promoted and paused peds excluded.", TEAL),
            (810, 400, "Krauss car-following — unchanged", "A pedestrian disc stands in as the leader.",
             "What is new is WHAT it reacts to, not HOW.", AMBER)]):
        b += card(x0, 524, w, 96, "#243040", col, 8, 1.2)
        b += txt(x0 + 20, 552, head, 14, col, weight="bold")
        b += txt(x0 + 20, 576, l1, 12.5, LIGHT)
        b += txt(x0 + 20, 596, l2, 12.5, SLATE_L)
    b += arrow(410, 572, 436, 572, TEAL, 2)
    b += arrow(780, 572, 806, 572, AMBER, 2.4)
    return svg(1280, 656, b)


# ----------------------------------------------------------------------------------------------
# 7. Anticipatory yield: what a current-overlap test structurally misses
# ----------------------------------------------------------------------------------------------
def d_yield():
    b = defs([AMBER, TEAL, RED, GREEN, SLATE_L])
    b += title("Yielding on where the pedestrian will be",
               "A current-overlap test cannot see a conflict that has not happened yet.")
    for i, (x0, head, col, sub) in enumerate([
            (70, "Overlap only", RED, "Ped is not in my lane yet ⇒ no constraint. Car keeps speed."),
            (680, "Anticipatory", GREEN, "Ped's predicted corridor crosses mine ⇒ yield now.")]):
        b += card(x0, 132, 530, 300, "#243040", SLATE, 12, 1.2)
        b += txt(x0 + 26, 170, head, 19, col, weight="bold")
        b += txt(x0 + 26, 194, sub, 13, SLATE_L)
        b += f'<rect x="{x0 + 26}" y="238" width="478" height="80" fill="#1a222c"/>'
        b += (f'<line x1="{x0 + 26}" y1="278" x2="{x0 + 504}" y2="278" stroke="{AMBER}" '
              f'stroke-width="1.6" stroke-dasharray="12 10" opacity="0.45"/>')
        b += car(x0 + 120, 278)
        b += ped(x0 + 330, 342, 9)
        b += arrow(x0 + 330, 332, x0 + 330, 288, TEAL, 2, "5 4")
        if i == 1:
            b += (f'<rect x="{x0 + 312}" y="240" width="36" height="104" fill="{TEAL}" opacity="0.16"/>')
            b += txt(x0 + 358, 258, "predicted corridor", 11, TEAL)
            b += arrow(x0 + 150, 278, x0 + 300, 278, GREEN, 2.4)
            b += txt(x0 + 26, 372, "Holds at contact, creeps below 1.5 m,", 13, LIGHT)
            b += txt(x0 + 26, 392, "evaluated on predicted clearance so it is", 13, LIGHT)
            b += txt(x0 + 26, 412, "reachable under braking.", 13, LIGHT)
        else:
            b += txt(x0 + 26, 372, "The car arrives at the same time as the", 13, LIGHT)
            b += txt(x0 + 26, 392, "pedestrian. By the time they overlap it is", 13, LIGHT)
            b += txt(x0 + 26, 412, "too late to stop.", 13, LIGHT)
    b += card(70, 460, 1140, 84, "#1e2a34", GREEN, 8, 1.2)
    b += txt(94, 490, "Measured in-zone at 800 pedestrians", 15, GREEN, weight="bold")
    b += stats(94, 512, [("close fast passes", "203 → 14"),
                         ("driving AT a pedestrian", "11 → 0"),
                         ("arrivals", "unchanged")], gap=360)
    return svg(1280, 580, b)


# ----------------------------------------------------------------------------------------------
# 8. "laneChange" is not lane changes -- the write-rate explainer
# ----------------------------------------------------------------------------------------------
def d_lanechange():
    b = defs([AMBER, TEAL, SLATE_L, GREEN])
    b += title("Half the network traffic is cars driving straight on",
               "The wire publishes on a change of lane IDENTITY — which a car crossing a junction does three times.")
    b += f'<rect x="70" y="146" width="1140" height="168" rx="8" fill="#1a222c"/>'
    y = 230
    seg = [("edge A_0", 120, 260, SLATE), (": junction internal", 400, 150, AMBER), ("edge B_0", 570, 260, SLATE)]
    for name, x0, w, col in seg:
        b += f'<rect x="{x0}" y="{y - 26}" width="{w}" height="52" rx="4" fill="{col}" opacity="0.30"/>'
        b += f'<rect x="{x0}" y="{y - 26}" width="{w}" height="52" rx="4" fill="none" stroke="{col}" stroke-width="1.4"/>'
        b += txt(x0 + w / 2, y + 46, name, 12, SLATE_L if col == SLATE else AMBER, "middle", font=MONO)
    b += car(150, y)
    for k, px in enumerate([330, 470, 640]):
        b += f'<circle cx="{px}" cy="{y}" r="13" fill="{AMBER}" opacity="0.9"/>'
        b += txt(px, y + 5, str(k + 1), 13, INK, "middle", "bold")
    b += arrow(180, y, 830, y, AMBER, 2, "", True)
    b += txt(860, y + 5, "3 publishes, zero steering", 15, AMBER, weight="bold")
    b += txt(860, y + 28, "One intersection, one straight-through car.", 12, SLATE_L)

    b += txt(70, 372, "Measured at 2 000 cars — what the 49.6% actually is", 17, LIGHT, weight="bold")
    rows = [("Real lateral lane change (same edge)", "0.7%", GREEN, 0.007),
            ("Drove onto the next lane of the route", "48.9%", AMBER, 0.489),
            ("└ of which entering / leaving a junction", "24.9%", AMBER, 0.249)]
    yy = 404
    for label, pct, col, frac in rows:
        b += txt(94, yy + 14, label, 14, LIGHT)
        b += f'<rect x="640" y="{yy}" width="420" height="20" rx="4" fill="#1a222c"/>'
        b += f'<rect x="640" y="{yy}" width="{max(6, 420 * frac)}" height="20" rx="4" fill="{col}"/>'
        b += txt(1080, yy + 15, pct, 15, col, weight="bold")
        yy += 40
    b += card(70, 534, 1140, 74, "#243040", "none", 8)
    b += txt(94, 562, "So it is not the traffic — it is lane granularity.", 14, LIGHT, weight="bold")
    b += txt(94, 586, "54% of this city cut's lanes are internal junction lanes; the median lane is 13.8 m. "
                      "And it is irreducible: position on the wire is measured along a specific lane.", 13, SLATE_L)
    return svg(1280, 640, b)


# ----------------------------------------------------------------------------------------------
# 9. server == IG
# ----------------------------------------------------------------------------------------------
def d_serverig():
    b = defs([TEAL, GREEN, SLATE_L])
    b += title("The crowd costs almost nothing on the wire",
               "A closed-form pose means every observer can derive it — so it never has to be sent.")
    b += card(70, 150, 380, 300, "#243040", SLATE, 12, 1.2)
    b += txt(96, 188, "Simulation server", 18, LIGHT, weight="bold")
    for i in range(6):
        b += ped(130 + (i % 3) * 60, 250 + (i // 3) * 50, 8, TEAL)
    b += txt(96, 372, "Evaluates pose = f(route, seed,", 13, SLATE_L, font=MONO)
    b += txt(96, 392, "width, t)", 13, SLATE_L, font=MONO)
    b += card(830, 150, 380, 300, "#243040", SLATE, 12, 1.2)
    b += txt(856, 188, "Image generator", 18, LIGHT, weight="bold")
    for i in range(6):
        b += ped(890 + (i % 3) * 60, 250 + (i // 3) * 50, 8, TEAL)
    b += txt(856, 372, "Evaluates the SAME function", 13, SLATE_L, font=MONO)
    b += txt(856, 392, "⇒ bit-identical pose", 13, SLATE_L, font=MONO)
    b += card(478, 232, 324, 60, "#1e2a34", GREEN, 8, 1.4)
    b += txt(640, 258, "route + seed", 15, GREEN, "middle", "bold", font=MONO)
    b += txt(640, 280, "sent ONCE", 12, LIGHT, "middle")
    b += arrow(452, 262, 470, 262, GREEN, 2.4)
    b += arrow(806, 262, 826, 262, GREEN, 2.4)
    b += txt(640, 336, "then zero per-step bytes", 15, GREEN, "middle", "bold")
    b += txt(640, 360, "for every ambient pedestrian", 13, SLATE_L, "middle")
    b += card(70, 486, 1140, 78, "#243040", "none", 8)
    b += txt(94, 514, "Proven bit-identical over an in-process byte loopback and over real CycloneDDS.", 14, LIGHT,
             weight="bold")
    b += txt(94, 540, "The consequence: crowd size is decoupled from bandwidth entirely.", 13, SLATE_L)
    return svg(1280, 600, b)


# ----------------------------------------------------------------------------------------------
# 10. Threaded tick
# ----------------------------------------------------------------------------------------------
def d_threaded():
    b = defs()
    b += title("The tick runs on its own thread",
               "A frame never waits for an engine step. The renderer only ever reads a published snapshot.")

    y0 = 148
    b += f'<rect x="150" y="{y0}" width="1060" height="118" rx="6" fill="#1a222c"/>'
    b += txt(140, y0 + 36, "render", 12, SLATE_L, "end")
    b += txt(140, y0 + 92, "engine", 12, SLATE_L, "end")
    x = 170
    while x < 1180:
        b += f'<rect x="{x}" y="{y0 + 20}" width="22" height="26" rx="3" fill="{SLATE}"/>'
        x += 30
    x = 170
    while x < 1180:
        b += f'<rect x="{x}" y="{y0 + 76}" width="112" height="26" rx="3" fill="{AMBER}" opacity="0.85"/>'
        x += 122
    b += txt(1196, y0 + 132, "every frame lands · the producer thread owns the step", 13, GREEN, "end")

    b += card(70, 316, 555, 138, "#243040", TEAL, 10, 1.3)
    b += txt(94, 348, "The handoff", 15, TEAL, weight="bold")
    b += txt(94, 374, "The engine publishes a snapshot; the renderer reads it.", 13, LIGHT)
    b += txt(94, 396, "Neither ever blocks on the other.", 13, LIGHT)
    b += txt(94, 424, "Engine parallelism is capped so the producer", 12.5, SLATE_L)
    b += txt(94, 442, "cannot starve the renderer.", 12.5, SLATE_L)

    b += card(655, 316, 555, 138, "#1e2a34", GREEN, 10, 1.3)
    b += txt(679, 348, "Capping was proven inert, not assumed", 15, GREEN, weight="bold")
    b += txt(679, 374, "11 889 car and pedestrian samples bitwise", 13, LIGHT)
    b += txt(679, 396, "identical, capped versus uncapped.", 13, LIGHT)
    b += txt(679, 424, "Smoothness did not cost a trajectory.", 12.5, SLATE_L)

    b += card(70, 478, 1140, 84, "#1e2a34", GREEN, 8, 1.2)
    b += txt(94, 508, "Measured on a real city cut at 3 858 cars + 20 726 pedestrians", 15, GREEN, weight="bold")
    b += stats(94, 530, [("frames over 3x median", "0 of 2000"),
                         ("p99 vs p50", "1.20x"),
                         ("sustained in real time", "2 Hz")], gap=360)

    b += card(70, 586, 1140, 74, "#2a2438", ZONE, 8, 1.2)
    b += txt(94, 616, "Next", 14, ZONE, weight="bold")
    b += txt(94, 642, "Extending the same handoff discipline to more consumers, and to the sim-rate and zone "
                      "controls under load. Reach, not repair.", 13, LIGHT)
    return svg(1280, 696, b)


# ----------------------------------------------------------------------------------------------
# ----------------------------------------------------------------------------------------------
# 11. Spatial decomposition -- how the work is actually spread across cores
# ----------------------------------------------------------------------------------------------
def d_spatial():
    b = defs()
    b += title("How the work is spread across cores",
               "Two mechanisms. Both byte-identical to a serial run — parallelism is never allowed to "
               "cost an answer.")

    # Left: the grid. Each region owns a DISJOINT set of lanes, which is the whole trick.
    b += card(70, 132, 470, 340, "#243040", AMBER, 12, 1.4)
    b += txt(94, 168, "SPATIAL DECOMPOSITION", 14, AMBER, weight="bold")
    b += txt(94, 192, "opt-in  ·  --region --region-grid G", 12.5, SLATE_L, font=MONO)
    gx, gy, cell = 122, 212, 56
    tone = ["#2f3a46", "#38424e", "#2a3440", "#414c58",
            "#38424e", "#4a5663", "#2f3a46", "#38424e",
            "#2a3440", "#2f3a46", "#38424e", "#2a3440",
            "#38424e", "#2a3440", "#2f3a46", "#38424e"]
    for r in range(4):
        for c in range(4):
            b += (f'<rect x="{gx + c * cell}" y="{gy + r * cell}" width="{cell - 4}" height="{cell - 4}" '
                  f'rx="3" fill="{tone[r * 4 + c]}" stroke="{SLATE}" stroke-width="0.8"/>')
    # a busy region: dynamic scheduling means whichever thread is free simply picks it up
    b += (f'<rect x="{gx + cell}" y="{gy + cell}" width="{cell - 4}" height="{cell - 4}" rx="3" '
          f'fill="none" stroke="{AMBER}" stroke-width="2.5"/>')
    b += txt(gx + cell * 4 + 20, gy + cell + 26, "congestion", 12, AMBER)
    b += txt(gx + cell * 4 + 20, gy + cell + 44, "concentrates here", 12, AMBER)
    b += txt(94, 456, "Each region owns a DISJOINT set of lanes.", 13.5, LIGHT, weight="bold")

    # Right: the three properties that make it practical.
    for k, (head, l1, l2) in enumerate([
            ("Lock-free by construction, not by care",
             "Disjoint lane ownership means region tasks need no locks",
             "at all. There is no critical section to get wrong."),
            ("Boundary handoff is free",
             "A vehicle crossing out is simply grouped in the next",
             "region next step. No state transfer, no migration."),
            ("It balances itself",
             "Dynamic scheduling over regions: as load concentrates,",
             "a busy region is picked up by whichever thread is free.")]):
        y = 132 + k * 118
        b += card(570, y, 640, 104, "#1e2a34", GREEN, 10, 1.3)
        b += txt(594, y + 30, head, 14.5, GREEN, weight="bold")
        b += txt(594, y + 56, l1, 13, LIGHT)
        b += txt(594, y + 78, l2, 13, LIGHT)

    # The other mechanism -- the one that is actually on by default.
    b += card(70, 502, 555, 132, "#243040", AMBER, 10, 1.3)
    b += txt(94, 532, "Per-vehicle phase parallelism", 14.5, AMBER, weight="bold")
    b += txt(94, 556, "on by default above a few hundred vehicles", 12.5, GREEN)
    b += txt(94, 582, "Plan, export and post-move read only frozen", 13, LIGHT)
    b += txt(94, 602, "start-of-step state and write only their own vehicle.", 13, LIGHT)
    b += txt(94, 622, "Structural change is deferred to a command buffer.", 13, LIGHT)

    # The thread sweep: its SHAPE is the argument, so draw it rather than tabulate it.
    b += card(655, 502, 555, 132, "#243040", "none", 10)
    b += txt(679, 530, "More threads is not automatically better", 14.5, LIGHT, weight="bold")
    bx, by, bw, bh = 700, 546, 74, 62
    for k, (lab, v) in enumerate([("1", 11.48), ("2", 7.90), ("4", 6.34),
                                  ("8", 5.68), ("16", 5.67), ("24", 6.13)]):
        h = bh * (v / 11.48)
        col = GREEN if lab == "8" else (RED if lab == "24" else SLATE)
        b += (f'<rect x="{bx + k * bw}" y="{by + (bh - h) + 14}" width="30" height="{h}" rx="2" '
              f'fill="{col}" opacity="0.9"/>')
        b += txt(bx + k * bw + 15, by + bh + 32, lab, 11, SLATE_L, "middle")
        b += txt(bx + k * bw + 15, by + (bh - h) + 8, f"{v:.1f}", 10, col, "middle")
    b += txt(bx + 6 * bw - 2, by + bh + 32, "threads", 11, SLATE_L)
    b += (f'<line x1="{bx - 6}" y1="{by + bh + 16}" x2="{bx + 6 * bw + 6}" y2="{by + bh + 16}" stroke="{SLATE}" stroke-width="1" opacity="0.6"/>')
    b += txt(1186, by + 6, "8 beats 24. The knee is at 4.", 12.5, GREEN, "end")

    # The honest reading, in the project's own voice.
    b += card(70, 650, 1140, 88, "#2e2a22", AMBER, 8, 1.2)
    b += txt(94, 680, "The honest reading", 14, AMBER, weight="bold")
    b += txt(94, 706, "Today's region win is modest: the dominant phases are bound by MEMORY BANDWIDTH on "
                      "random neighbour access, not by CPU.", 13, LIGHT)
    b += txt(94, 728, "The hard part — disjoint ownership, free handoff, safety by construction — is done. "
                      "A segmented store is what turns it into a large win.", 13, LIGHT)
    return svg(1280, 774, b)


# ----------------------------------------------------------------------------------------------
# 12. Evacuation
# ----------------------------------------------------------------------------------------------
def d_evac():
    b = defs([AMBER, TEAL, RED, SLATE_L, GREEN])
    b += title("Panic evacuation — on a completely unmodified driving core",
               "Fear is local information, not a global flag. The layer only drives public seams.")
    steps = [("Incident", RED, "A localised event."),
             ("Fear spreads", RED, "Line-of-sight gated,\nplus contagion and\njam-unease."),
             ("Flee", AMBER, "Aggressive preset,\nreroute to exits."),
             ("Gridlock", AMBER, "The streets jam."),
             ("Abandon", TEAL, "Boxed-in driver\nnoses onto the\nshoulder, gets out."),
             ("Foot exodus", TEAL, "The crowd streams\nout; cars react to\nit as obstacles.")]
    x = 70
    for i, (name, col, desc) in enumerate(steps):
        b += card(x, 140, 172, 190, "#243040", col, 10, 1.4)
        b += f'<circle cx="{x + 26}" cy="172" r="13" fill="{col}"/>'
        b += txt(x + 26, 177, str(i + 1), 13, INK, "middle", "bold")
        b += txt(x + 48, 177, name, 15, LIGHT, weight="bold")
        for j, line in enumerate(desc.split("\n")):
            b += txt(x + 18, 214 + j * 19, line, 12, SLATE_L)
        if i < len(steps) - 1:
            b += arrow(x + 176, 235, x + 190, 235, SLATE_L, 2)
        x += 194
    b += card(70, 362, 555, 118, "#243040", GREEN, 10, 1.2)
    b += txt(94, 392, "Cost follows the incident, not the map", 15, GREEN, weight="bold")
    b += txt(94, 418, "The layer attaches only within a bounded working", 13, SLATE_L)
    b += txt(94, 438, "region, so a city-scale run pays for the affected", 13, SLATE_L)
    b += txt(94, 458, "neighbourhood while the rest keeps flowing normally.", 13, SLATE_L)
    b += card(655, 362, 555, 118, "#243040", LIGHT, 10, 1.2)
    b += txt(679, 392, "The parity core never learns about any of it", 15, LIGHT, weight="bold")
    b += txt(679, 418, "Sim.Evac drives the engine through the same public", 13, SLATE_L)
    b += txt(679, 438, "seams any integrator uses. With panic off, the", 13, SLATE_L)
    b += txt(679, 458, "determinism hash does not move.", 13, SLATE_L)
    return svg(1280, 510, b)


# ----------------------------------------------------------------------------------------------
# ----------------------------------------------------------------------------------------------
# 13. Scale: cost follows attention
# ----------------------------------------------------------------------------------------------
def d_attention():
    b = defs([AMBER, TEAL, SLATE_L, GREEN])
    b += title("Cost follows attention, not city size",
               "The high-realism zone tracks the camera. Fidelity where it is seen; cheap everywhere else.")
    b += f'<rect x="70" y="140" width="700" height="400" rx="8" fill="#1a222c"/>'
    for gx in range(6):
        for gy in range(4):
            b += f'<rect x="{104 + gx * 112}" y="{174 + gy * 92}" width="86" height="66" rx="3" fill="{SLATE}" opacity="0.18"/>'
    # A small fixed LCG. Modular strides -- even two of them -- still lay down visible rows or columns;
    # only a proper generator looks like a scattered crowd. Seeded, so the diagram is reproducible.
    _s = 20260729
    for _ in range(150):
        _s = (1103515245 * _s + 12345) & 0x7FFFFFFF
        px = 92 + (_s >> 7) % 664
        _s = (1103515245 * _s + 12345) & 0x7FFFFFFF
        py = 168 + (_s >> 7) % 356
        if math.hypot(px - 440, py - 330) > 146:
            b += ped(px, py, 4.5, TEAL, 0.62)
    b += f'<circle cx="440" cy="330" r="132" fill="{ZONE}" opacity="0.12"/>'
    b += f'<circle cx="440" cy="330" r="132" fill="none" stroke="{ZONE}" stroke-width="2"/>'
    # Golden-angle placement: deterministic and evenly spread inside the disc. An arithmetic
    # sequence mod N walks a diagonal instead, which rendered as clumped caterpillars.
    for i in range(18):
        rr = 112 * math.sqrt((i + 0.5) / 18)
        th = i * 2.39996323
        b += ped_hi(440 + rr * math.cos(th), 330 + rr * math.sin(th), 7)
    b += f'<path d="M 440 330 L 300 190 A 190 190 0 0 1 580 190 Z" fill="{LIGHT}" opacity="0.07"/>'
    b += txt(440, 476, "camera realism zone", 13, ZONE, "middle", "bold")
    b += txt(688, 520, "cheap LOD", 12, TEAL, "end")

    x0 = 815
    b += txt(x0, 176, "Inside the zone", 17, ZONE, weight="bold")
    b += txt(x0, 202, "Pedestrians promote to full ORCA.", 13, SLATE_L)
    b += txt(x0, 222, "Cars use cooperative lane changing.", 13, SLATE_L)
    b += txt(x0, 242, "Cars yield to pedestrians in their path.", 13, SLATE_L)
    b += txt(x0, 292, "Outside", 17, TEAL, weight="bold")
    b += txt(x0, 318, "Closed-form pedestrians at O(1).", 13, SLATE_L)
    b += txt(x0, 338, "Cars still stop at crossings.", 13, SLATE_L)
    b += card(x0, 374, 395, 166, "#1e2a34", GREEN, 10, 1.2)
    b += txt(x0 + 22, 404, "Verified in routine use", 15, GREEN, weight="bold")
    # Six bold digits at 30px need far more than 108px before the unit label. Visual QA caught the
    # collision here; the label now sits BELOW its number rather than beside it.
    b += txt(x0 + 22, 438, "10 000", 32, LIGHT, weight="bold")
    b += txt(x0 + 24, 458, "vehicles", 13, SLATE_L)
    b += txt(x0 + 210, 438, "30 000", 32, PED_HI, weight="bold")
    b += txt(x0 + 212, 458, "pedestrians", 13, SLATE_L)
    b += txt(x0 + 22, 496, "in the Godot 3-D viewer, in routine use", 12.5, SLATE_L)
    b += txt(x0 + 22, 516, "— with headroom on the pedestrian side", 12.5, SLATE_L)
    return svg(1280, 580, b)


# ----------------------------------------------------------------------------------------------
# 14. Dead reckoning: what is sent up front, what is sent per agent, and why that is so little
# ----------------------------------------------------------------------------------------------
def d_dr():
    b = defs()
    b += title("Dead reckoning: 48 bytes buys a trajectory, not a sample",
               "The receiver is not told where a car IS. It is told enough to work out where it WILL be.")

    # --- band 1: once, up front ---
    b += txt(70, 148, "ONCE, UP FRONT", 15, LIGHT, weight="bold")
    b += txt(232, 148, "durable — a late-joining viewer gets it without the network file", 13, SLATE_L)
    b += card(70, 162, 555, 132, "#243040", SLATE, 10, 1.3)
    b += txt(94, 192, "Lane geometry", 15, AMBER, weight="bold")
    b += txt(94, 216, "per lane: handle · width · length · centreline points (+z)", 12.5, SLATE_L, font=MONO)
    b += txt(94, 248, "2.86 MiB", 22, LIGHT, weight="bold")
    b += txt(196, 248, "for 28 276 lanes — a whole city, sent once", 13, SLATE_L)
    b += txt(94, 278, "This is what makes 48 B per update enough.", 12.5, GREEN)
    b += card(655, 162, 555, 132, "#243040", SLATE, 10, 1.3)
    b += txt(679, 192, "Per agent, on spawn", 15, AMBER, weight="bold")
    b += txt(679, 216, "handle · vType · length · width · id", 12.5, SLATE_L, font=MONO)
    b += txt(679, 248, "Physical dimensions travel ONCE,", 13, LIGHT)
    b += txt(679, 268, "never in a per-frame packet.", 13, LIGHT)
    b += txt(679, 288, "Pedestrian route / timeline: also once.", 12.5, TEAL)

    # --- band 2: per update ---
    b += txt(70, 340, "PER UPDATE", 15, LIGHT, weight="bold")
    b += txt(190, 340, "and only when dead reckoning would otherwise be wrong", 13, SLATE_L)
    b += card(70, 354, 360, 200, "#2e2a22", AMBER, 10, 1.4)
    b += txt(94, 384, "Car", 15, AMBER, weight="bold")
    b += txt(94, 414, "48 B", 30, LIGHT, weight="bold")
    fields = ["lane + arc-position", "speed + acceleration", "lateral pos + lateral speed",
              "the next 4 lanes ahead"]
    for i, f in enumerate(fields):
        b += f'<circle cx="{100}" cy="{437 + i * 24}" r="3" fill="{AMBER}"/>'
        b += txt(112, 441 + i * 24, f, 12.5, SLATE_L)
    b += txt(94, 540, "= a trajectory the receiver integrates", 12.5, GREEN)

    b += card(450, 354, 360, 200, "#1e2f2c", TEAL, 10, 1.4)
    b += txt(474, 384, "Pedestrian", 15, TEAL, weight="bold")
    b += txt(474, 414, "18 B", 30, LIGHT, weight="bold")
    b += txt(560, 414, "if reactive (ORCA)", 12.5, SLATE_L)
    b += txt(474, 452, "0 B", 30, GREEN, weight="bold")
    b += txt(546, 452, "if ambient", 12.5, SLATE_L)
    b += txt(474, 486, "An ambient pedestrian emits NOTHING", 12.5, LIGHT)
    b += txt(474, 506, "per step — its pose is a function both", 12.5, LIGHT)
    b += txt(474, 526, "ends already evaluate independently.", 12.5, LIGHT)

    b += card(830, 354, 380, 200, "#243040", SLATE, 10, 1.3)
    b += txt(854, 384, "Sent when?", 15, LIGHT, weight="bold")
    reasons = [("the predicted position drifts too far", AMBER),
               ("the lane identity changes", AMBER),
               ("a 3 s liveliness heartbeat elapses", SLATE_L)]
    for i, (r, c) in enumerate(reasons):
        b += f'<circle cx="{860}" cy="{412 + i * 26}" r="3.5" fill="{c}"/>'
        b += txt(874, 416 + i * 26, r, 12.5, SLATE_L)
    b += txt(854, 500, "A genuinely steady car diverges by ~0", 12.5, GREEN)
    b += txt(854, 520, "from its own prediction — so nothing", 12.5, GREEN)
    b += txt(854, 540, "is sent at all.", 12.5, GREEN)

    # --- band 3: the saving ---
    b += txt(70, 600, "WHAT THAT SAVES", 15, LIGHT, weight="bold")
    b += txt(228, 600, "measured on a real city cut, vehicles only", 13, SLATE_L)
    rows = [("Naive: a pose every rendered frame", 1.0, "60 / car / s", RED),
            ("A packet every simulation step", 0.033, "2 / car / s", AMBER),
            ("Dead-reckoned, measured", 0.0107, "0.64 / car / s", GREEN)]
    yy = 620
    for label, frac, val, col in rows:
        b += txt(94, yy + 17, label, 13.5, LIGHT)
        b += f'<rect x="470" y="{yy}" width="520" height="24" rx="4" fill="#1a222c"/>'
        b += f'<rect x="470" y="{yy}" width="{max(7, 520 * frac)}" height="24" rx="4" fill="{col}"/>'
        b += txt(1006, yy + 18, val, 14, col, weight="bold")
        yy += 34
    b += card(70, 730, 555, 76, "#1e2a34", GREEN, 8, 1.2)
    b += txt(94, 758, "94x fewer messages than the render rate", 15, GREEN, weight="bold")
    b += txt(94, 782, "and motion still reconstructs smooth at 60 fps.", 13, LIGHT)
    b += card(655, 730, 555, 76, "#1e2a34", GREEN, 8, 1.2)
    b += txt(679, 758, "4 000 cars = 125 KiB/s. 30 000 ambient peds = 0.", 15, GREEN, weight="bold")
    b += txt(679, 782, "Crowd size is decoupled from bandwidth entirely.", 13, LIGHT)
    return svg(1280, 840, b)


# ----------------------------------------------------------------------------------------------
# 15. Liveliness is DATA, not a per-step behaviour loop
# ----------------------------------------------------------------------------------------------
def d_liveliness():
    b = defs()
    b += title("City life is authored data, not a behaviour loop",
               "Four segment kinds compose into every beat below — and all of it stays low-power.")
    b += txt(70, 148, "THE VOCABULARY", 14, LIGHT, weight="bold")
    kinds = [("Walk", "follow the route"), ("Pause", "stop in place: phone, sip, look"),
             ("Dwell", "stay put — optionally unseen"),
             ("Interact", "a Dwell that names a partner")]
    x = 70
    for name, desc in kinds:
        b += card(x, 162, 272, 82, "#1e2f2c", TEAL, 10, 1.3)
        b += txt(x + 18, 192, name, 16, TEAL, weight="bold")
        b += txt(x + 18, 216, desc, 11.5, SLATE_L)
        x += 285
    b += txt(70, 288, "THE BEATS THEY COMPOSE INTO", 14, LIGHT, weight="bold")
    b += txt(346, 288, "each one a generator that emits an ordinary timeline — no new evaluator", 12.5, SLATE_L)
    beats = [
        ("Checking a phone", "Walk → Pause(\"phone\") → Walk", "A pause carries an animation tag and no\npose of its own, so the walk either side\nstays continuous."),
        ("Meeting, then parting", "paired Interact in BOTH timelines", "Two pedestrians agree one meet point,\ntime and duration, stand ~1.2 m apart,\ntalk, then walk on."),
        ("Serving outdoor tables", "loop: door → table → serve → inside", "Tables visited in a seed-varied order.\nThe dwell inside the building is a real\npose that is simply not drawn."),
        ("Boarding a car", "Walking → Riding", "The person leaves the crowd entirely on\nboarding, and the car drives away with\nmutual avoidance in the lot."),
    ]
    x = 70
    for i, (name, mech, desc) in enumerate(beats):
        b += card(x, 302, 272, 148, "#243040", SLATE, 10, 1.3)
        b += f'<circle cx="{x + 26}" cy="{332}" r="12" fill="{TEAL}"/>'
        b += txt(x + 26, 337, str(i + 1), 12, INK, "middle", "bold")
        b += txt(x + 48, 337, name, 14.5, LIGHT, weight="bold")
        b += txt(x + 18, 366, mech, 11, AMBER if i == 3 else TEAL, font=MONO)
        for j, line in enumerate(desc.split("\n")):
            b += txt(x + 18, 392 + j * 19, line, 11.5, SLATE_L)
        x += 285
    b += card(70, 476, 555, 96, "#1e2a34", GREEN, 8, 1.2)
    b += txt(94, 506, "None of this costs a per-step behaviour loop", 15, GREEN, weight="bold")
    b += txt(94, 532, "Liveliness adds richer one-time DATA, not per-tick work — so a", 12.5, LIGHT)
    b += txt(94, 552, "living city stays as cheap, and as reconstructable, as a walking one.", 12.5, LIGHT)
    b += card(655, 476, 555, 96, "#2e2a22", AMBER, 8, 1.2)
    b += txt(679, 506, "What is next: the director, not the behaviours", 15, AMBER, weight="bold")
    b += txt(679, 532, "The beats exist and are deterministic. What is designed but not", 12.5, LIGHT)
    b += txt(679, 552, "built is placing them automatically from venue records, city-wide.", 12.5, LIGHT)
    return svg(1280, 600, b)


# ----------------------------------------------------------------------------------------------
# 16. Already optimised -- and the remaining levers are named, not mysterious
# ----------------------------------------------------------------------------------------------
def d_headroom():
    b = defs()
    b += title("Already fast — and the remaining headroom is already located",
               "Nothing below is a mystery. Each lever is measured, attributed, and waiting its turn.")
    b += txt(70, 150, "WHAT HAS ALREADY LANDED", 14, GREEN, weight="bold")
    done = [("Allocation collapsed", "the hot path stopped allocating per car, per step"),
            ("GC pressure removed", "pause time down to a small fraction of wall"),
            ("Parallel by default at scale", "and byte-identical to the serial run, by test"),
            ("Engine tick off the render thread", "frame hitches gone at full density"),
            ("Faster per tick than SUMO", "single-threaded, before any parallelism")]
    y = 168
    for name, desc in done:
        b += f'<circle cx="{84}" cy="{y + 8}" r="6" fill="{GREEN}"/>'
        b += txt(102, y + 13, name, 14, LIGHT, weight="bold")
        b += txt(102, y + 33, desc, 12, SLATE_L)
        y += 56
    b += txt(660, 150, "WHAT IS STILL ON THE TABLE", 14, AMBER, weight="bold")
    todo = [("Neighbour cap in the crowd solver", "considers every agent in range where the reference\nimplementation caps at ten — the largest single lever"),
            ("Pedestrian spawn cost", "scales with the walkable graph, not with the spawn"),
            ("Route cache defeated by its own key", "so it never shares between vehicles, and never shrinks"),
            ("Insertion cost under saturation", "quadratic exactly when a user cranks density past capacity"),
            ("Cache layout of the crowd hot loop", "three arrays read together, not yet packed together")]
    y = 168
    for name, desc in todo:
        b += f'<circle cx="{674}" cy="{y + 8}" r="6" fill="{AMBER}"/>'
        b += txt(692, y + 13, name, 14, LIGHT, weight="bold")
        for j, line in enumerate(desc.split("\n")):
            b += txt(692, y + 33 + j * 17, line, 12, SLATE_L)
        y += 56
    b += card(70, 468, 1140, 96, "#243040", "none", 10)
    b += txt(94, 498, "The reason the list is specific rather than aspirational", 15, LIGHT, weight="bold")
    b += txt(94, 524, "Every entry was found by measuring, then attributed to a named cause, then left alone until its turn. "
                      "The largest one changes", 12.5, SLATE_L)
    b += txt(94, 544, "pedestrian trajectories, so it ships opt-in and off by default — a deliberate choice, not an "
                      "oversight.", 12.5, SLATE_L)
    return svg(1280, 600, b)


# ----------------------------------------------------------------------------------------------
# 17. The close: this is a substrate, and the POC bar is a choice
# ----------------------------------------------------------------------------------------------
def d_substrate():
    b = defs()
    b += title("What you have seen is a substrate, not a finished product",
               "Many mechanisms, all working, none polished — because which ones need polishing is your call.")

    # Breadth at a uniform depth. The empty right-hand portion of each bar is the point of the slide:
    # it is deliberate headroom, not an unfinished job.
    areas = [("Car following & lane changing", 0.80, AMBER), ("Junctions & right of way", 0.62, AMBER),
             ("Rail", 0.70, AMBER), ("External agents", 0.66, AMBER),
             ("Pedestrian navigation & demand", 0.72, TEAL), ("Two-level LOD & liveliness", 0.68, TEAL),
             ("Car ↔ pedestrian coupling", 0.55, TEAL), ("Panic evacuation", 0.50, TEAL),
             ("Terrain & 3-D placement", 0.64, SLATE_L), ("Replication & dead reckoning", 0.74, SLATE_L),
             ("Viewers & IG integration", 0.60, SLATE_L)]
    y = 142
    b += txt(70, y, "CAPABILITY", 11.5, SLATE_L, weight="bold")
    b += txt(466, y, "PROOF-OF-CONCEPT BAR", 11.5, GREEN, weight="bold")
    b += txt(1206, y, "PRODUCTION", 11.5, SLATE_L, "end", weight="bold")
    y += 14
    for name, frac, col in areas:
        b += txt(70, y + 15, name, 13, LIGHT)
        b += f'<rect x="440" y="{y + 2}" width="766" height="17" rx="4" fill="#1a222c"/>'
        b += f'<rect x="440" y="{y + 2}" width="{766 * frac}" height="17" rx="4" fill="{col}" opacity="0.92"/>'
        y += 27
    # One honest marker: the bars stop in roughly the same band on purpose.
    b += (f'<line x1="{440 + 766 * 0.66}" y1="152" x2="{440 + 766 * 0.66}" y2="{y + 2}" '
          f'stroke="{GREEN}" stroke-width="1.6" stroke-dasharray="5 5" opacity="0.75"/>')

    b += card(70, 482, 360, 168, "#243040", GREEN, 10, 1.3)
    b += txt(94, 512, "Deliberately uniform", 15, GREEN, weight="bold")
    b += txt(94, 538, "Every mechanism was taken to the", 12.5, SLATE_L)
    b += txt(94, 557, "point where it is proven and honest,", 12.5, SLATE_L)
    b += txt(94, 576, "then stopped. Polishing the wrong", 12.5, SLATE_L)
    b += txt(94, 595, "one is the expensive mistake, and", 12.5, SLATE_L)
    b += txt(94, 614, "we could not yet know which.", 12.5, SLATE_L)

    b += card(450, 482, 360, 168, "#243040", AMBER, 10, 1.3)
    b += txt(474, 512, "Why direction is cheap here", 15, AMBER, weight="bold")
    for i, line in enumerate(["We own every line — no upstream",
                              "fork to maintain.",
                              "The parity gate makes change safe.",
                              "The seams are already public.",
                              "Everything is measured, so we know",
                              "where we actually stand."]):
        b += txt(474, 538 + i * 19, line, 12.5, SLATE_L)

    b += card(830, 482, 380, 168, "#1e2a34", LIGHT, 10, 1.4)
    b += txt(854, 512, "What the demo proved", 15, LIGHT, weight="bold")
    b += txt(854, 538, "That the performance is there and the", 12.5, SLATE_L)
    b += txt(854, 557, "mechanisms compose. Not that any", 12.5, SLATE_L)
    b += txt(854, 576, "one of them is finished.", 12.5, SLATE_L)
    b += txt(854, 608, "Point at any bar above and it", 13, LIGHT, weight="bold")
    b += txt(854, 627, "becomes production work.", 13, LIGHT, weight="bold")
    return svg(1280, 692, b)


DIAGRAMS = {
    "01-layering": d_layering, "02-seam": d_seam, "03-lod": d_lod, "04-hysteresis": d_hysteresis,
    "05-weave": d_weave, "06-coupling": d_coupling, "07-yield": d_yield, "08-lanechange": d_lanechange,
    "09-server-ig": d_serverig, "10-threaded": d_threaded, "11-spatial": d_spatial, "12-evac": d_evac,
    "13-attention": d_attention, "14-dr": d_dr, "15-liveliness": d_liveliness,
    "16-headroom": d_headroom, "17-substrate": d_substrate,
}

if __name__ == "__main__":
    for name, fn in DIAGRAMS.items():
        (OUT / f"{name}.svg").write_text(fn(), encoding="utf-8")
        print(f"wrote {name}.svg")
