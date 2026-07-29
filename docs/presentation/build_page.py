#!/usr/bin/env python3
"""Render PRESENTATION.md as a single self-contained reading page with the diagrams inlined.

Two things need care and neither is cosmetic:
  * every generated SVG declares the SAME marker ids (`ah-<hex>`), so pulling 16 of them into one
    DOM collides. Namespace the ids per figure on the way in.
  * no markdown library is available, so the subset actually used by the document is converted here.
"""
import html
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parent
REPO = ROOT.parent.parent          # repo root, resolved from this file
SRC = ROOT / "PRESENTATION.md"
SVG = ROOT / "svg"
OUT = ROOT / "build/presentation.html"
OUT.parent.mkdir(exist_ok=True)

# What each diagram argues -- the caption, not a restatement of the title inside the image.
CAPTIONS = {
    "01": "Every extension is a ring outside an untouched parity core.",
    "02": "SUMO has no concept of an agent it does not control; this does.",
    "03": "The cheap level exists to look organic, not merely to be cheap — with its overlap bound stated.",
    "04": "Why promotion needs two radii and a dwell, or a pedestrian flips level every step.",
    "05": "Uniform against organic, and the same mechanism where crowds stand still.",
    "06": "What a car can and cannot see: assured inside a realism zone, believable outside it.",
    "07": "A current-overlap test cannot see a conflict that has not happened yet.",
    "08": "Half the network traffic is cars driving straight on — `laneChange` is not lane changes.",
    "09": "Server and image generator agree bit-for-bit, so ambient crowd costs nothing on the wire.",
    "10": "The tick runs on its own thread; a frame never waits for an engine step.",
    "11": "Disjoint lane ownership, free boundary handoff — and why 8 threads beat 24.",
    "12": "A whole behavioural model on a completely unmodified driving core.",
    "13": "Cost follows attention, not city size. Carries the headline scale figure.",
    "14": "What is sent up front, what per agent, and why 48 bytes buys a trajectory.",
    "15": "City life is authored data, not another behaviour loop.",
    "16": "Already fast — and the remaining headroom is already located.",
    "17": "The closing beat: a substrate, not a finished product.",
}

STEM = {p.name.split("-")[0]: p.name for p in sorted(SVG.glob("*.svg"))}


SEEN = set()


def figure(nums):
    """Emit each diagram at most once -- several are cross-referenced from more than one section."""
    out = ""
    for n in nums:
        if n not in CAPTIONS or n not in STEM or n in SEEN:
            continue
        SEEN.add(n)
        s = (SVG / STEM[n]).read_text(encoding="utf-8")
        # Namespace every id so 16 diagrams can share one document.
        for mid in sorted(set(re.findall(r'id="(ah-[0-9A-Fa-f]{6})"', s)), reverse=True):
            s = s.replace(f'id="{mid}"', f'id="d{n}-{mid}"').replace(f"url(#{mid})", f"url(#d{n}-{mid})")
        s = s.replace("<svg ", '<svg role="img" preserveAspectRatio="xMidYMid meet" ', 1)
        cap = inline(CAPTIONS[n])
        out += (f'<figure class="fig"><div class="fig-frame">{s}</div>'
                f'<figcaption><span class="fig-n">Diagram {n}</span>{cap}</figcaption></figure>\n')
    return out


def inline(t):
    """Inline spans. Escape first, then reintroduce only the markup we intend."""
    t = html.escape(t, quote=False)
    t = re.sub(r"`([^`]+)`", r"<code>\1</code>", t)
    t = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", r"<a href='\2'>\1</a>", t)
    t = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", t)
    t = re.sub(r"(?<![*\w])\*([^*]+)\*(?!\*)", r"<em>\1</em>", t)
    return t


# Diagrams the prose does not announce with a leading marker, anchored to the block they belong with.
ANCHORS = {
    "temporal hysteresis": "04",             # 3.2, the interest field
    "no publish threshold reaches it": "08",  # the lane-identity finding in section 9
}


def anchored(text):
    return [n for key, n in ANCHORS.items() if key in text]


def cells(line):
    return [c.strip() for c in line.strip().strip("|").split("|")]


def convert(md):
    lines = md.split("\n")
    out, i = [], 0
    pending = []          # figures queued by a *Diagram NN.* marker, flushed after the block
    n_sec = 0

    def flush():
        nonlocal pending
        if pending:
            out.append(figure(pending))
            pending = []

    while i < len(lines):
        ln = lines[i]

        # A diagram marker: pull the numbers out, drop the marker, keep any prose that followed it.
        m = re.match(r"\s*\*Diagrams?\s+([\d,\s]+?)\.\*\s*(.*)$", ln)
        if m:
            pending += [x.strip().zfill(2) for x in m.group(1).split(",") if x.strip()]
            ln = m.group(2)
            if not ln.strip():
                i += 1
                flush()
                continue

        if ln.strip() == "---":
            flush()
            i += 1
            continue

        if ln.startswith("# "):
            i += 1
            continue      # the page header carries the title

        if ln.startswith("## "):
            flush()
            t = ln[3:].strip()
            sm = re.match(r"(\d+)\.\s*(.*)$", t)
            if sm:
                n_sec += 1
                num, rest = sm.group(1).zfill(2), sm.group(2)
                out.append(f'<section id="s{num}"><p class="eyebrow">&sect;&thinsp;{num}</p>'
                           f"<h2>{inline(rest)}</h2>")
            else:
                out.append(f'<section id="appendix"><p class="eyebrow">Appendix</p><h2>{inline(t)}</h2>')
            i += 1
            continue

        if ln.startswith("### "):
            flush()
            t = ln[4:].strip()
            sm = re.match(r"([\d.]+)\s+(.*)$", t)
            if sm:
                out.append(f'<h3><span class="h3-n">{sm.group(1)}</span>{inline(sm.group(2))}</h3>')
            else:
                out.append(f"<h3>{inline(t)}</h3>")
            i += 1
            continue

        if ln.startswith("> "):
            block = []
            while i < len(lines) and lines[i].startswith(">"):
                block.append(lines[i].lstrip(">").strip())
                i += 1
            txt = " ".join(x for x in block if x)
            # A caution reads differently from a finding, so mark it differently.
            kind = "caution" if txt.startswith("**Don&#") or txt.startswith("**Don't") else "finding"
            out.append(f'<aside class="note {kind}">{inline(txt)}</aside>')
            pending += anchored(txt)
            flush()
            continue

        if ln.startswith("- "):
            items = []
            while i < len(lines) and (lines[i].startswith("- ") or
                                      (lines[i].startswith("  ") and lines[i].strip() and items)):
                if lines[i].startswith("- "):
                    items.append(lines[i][2:].strip())
                else:
                    items[-1] += " " + lines[i].strip()
                i += 1
            out.append("<ul>" + "".join(f"<li>{inline(x)}</li>" for x in items) + "</ul>")
            flush()
            continue

        if ln.startswith("|"):
            rows = []
            while i < len(lines) and lines[i].startswith("|"):
                rows.append(lines[i])
                i += 1
            head = cells(rows[0])
            body = [cells(r) for r in rows[2:]]
            headless = not any(h for h in head)
            t = ['<div class="tw"><table>']
            if not headless:
                t.append("<thead><tr>" + "".join(f"<th>{inline(c)}</th>" for c in head) + "</tr></thead>")
            t.append("<tbody>")
            for r in body:
                t.append("<tr>" + "".join(f"<td>{inline(c)}</td>" for c in r) + "</tr>")
            t.append("</tbody></table></div>")
            out.append("".join(t))
            flush()
            continue

        if not ln.strip():
            i += 1
            continue

        para = [ln.strip()]
        i += 1
        while i < len(lines) and lines[i].strip() and not re.match(r"^(#|>|-\s|\||---)", lines[i]):
            para.append(lines[i].strip())
            i += 1
        text = " ".join(para)
        out.append(f"<p>{inline(text)}</p>")
        # A mid-sentence cross-reference like "(*diagram 07*)" stays in the prose; the figure follows.
        for m2 in re.finditer(r"\(\*diagrams?\s+([\d,\s]+?)\*\)", text, re.I):
            pending += [x.strip().zfill(2) for x in m2.group(1).split(",") if x.strip()]
        pending += anchored(text)
        flush()

    flush()
    return "\n".join(out)


body = convert(SRC.read_text(encoding="utf-8"))
# Close each section before the next opens.
body = body.replace("<section id=", "</section>\n<section id=").replace(
    "</section>\n<section id=", "<section id=", 1) + "</section>"

# The document's own preamble restates the audience and posture that the page header already carries,
# and living outside a <section> it also escapes the reading column. Drop the duplication, keep the
# evidence-class table, and put what remains in a real section.
cut, rest = body.split("<section id=", 1)
for dead in [r"<p>The written companion to the slide deck\..*?</p>",
             r"<p><strong>Audience:</strong>.*?</p>"]:
    cut = re.sub(dead, "", cut, flags=re.S)
cut = cut.strip()
body = (f'<section id="intro"><p class="eyebrow">How to read the numbers</p>{cut}</section>\n'
        f"<section id={rest}")

TOC = [
    ("01", "Parity first"), ("02", "The seam"), ("03", "Pedestrians"), ("04", "Coupling"),
    ("05", "Attention"), ("06", "Across cores"), ("07", "A real city"), ("08", "Smooth motion"),
    ("09", "Dead reckoning"), ("10", "Evacuation"), ("11", "Rail &amp; integration"),
    ("12", "Headroom"), ("13", "Current state"), ("14", "Demonstrations"),
]
toc = "".join(f'<li><a href="#s{n}"><span>{n}</span>{t}</a></li>' for n, t in TOC)

CSS = """
:root{
  --bg:#F6F8F9; --surface:#FFFFFF; --ink:#1B242C; --body:#333F4A; --muted:#5C6873;
  --rule:#DBE2E7; --rule-soft:#E9EEF1;
  --amber:#8A5D02; --amber-mark:#F0B429; --teal:#146F65; --red:#B33A30; --green:#356B48;
  --fig-bg:#1F2933;
}
@media (prefers-color-scheme:dark){
  :root{
    --bg:#11171C; --surface:#192128; --ink:#EAF0F4; --body:#C4CFD8; --muted:#94A1AC;
    --rule:#2A343E; --rule-soft:#222C35;
    --amber:#F0B429; --amber-mark:#F0B429; --teal:#2BB3A3; --red:#E5534B; --green:#57A773;
  }
}
:root[data-theme="dark"]{
  --bg:#11171C; --surface:#192128; --ink:#EAF0F4; --body:#C4CFD8; --muted:#94A1AC;
  --rule:#2A343E; --rule-soft:#222C35;
  --amber:#F0B429; --amber-mark:#F0B429; --teal:#2BB3A3; --red:#E5534B; --green:#57A773;
}
:root[data-theme="light"]{
  --bg:#F6F8F9; --surface:#FFFFFF; --ink:#1B242C; --body:#333F4A; --muted:#5C6873;
  --rule:#DBE2E7; --rule-soft:#E9EEF1;
  --amber:#8A5D02; --amber-mark:#F0B429; --teal:#146F65; --red:#B33A30; --green:#356B48;
}

--sans: ui-sans-serif, system-ui, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;

*{box-sizing:border-box}
body{
  margin:0; background:var(--bg); color:var(--body);
  font-family:Charter,"Bitstream Charter","Sitka Text",Cambria,Georgia,"Liberation Serif",serif;
  font-size:17.5px; line-height:1.62; -webkit-font-smoothing:antialiased;
}
.sans,h1,h2,h3,.eyebrow,figcaption,th,.chip,.toc,.rail,.kicker{
  font-family:ui-sans-serif,system-ui,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif;
}
code,.mono{font-family:ui-monospace,"SF Mono","Cascadia Mono",Menlo,Consolas,"Liberation Mono",monospace}

/* One column token owns every text block, so headings, prose, notes and tables all share a left
   edge; figures break out wider around the same centre. Mixing centred prose with wrap-edge
   headings reads as a zigzag, so nothing here is allowed to opt out. */
/* --col must be absolute: `ch` resolves against each element's OWN font, so a `ch` token
   gives the display face a far wider column than the body face and the left edges diverge. */
.wrap{--col:43rem; max-width:1240px; margin:0 auto; padding:0 28px 120px}
.col,.kicker,.lede,.chips,.toc,.eyebrow,h1,h2,h3,
section > p,section > ul,section > .note,.tw,footer{max-width:var(--col); margin-inline:auto}

/* progress hairline -- the only motion on the page */
#prog{position:fixed; top:0; left:0; height:2px; width:0; background:var(--amber-mark); z-index:50}

header.top{padding:76px 0 40px; border-bottom:1px solid var(--rule)}
.kicker{font-size:12.5px; letter-spacing:.16em; text-transform:uppercase; color:var(--amber);
        margin:0 auto 18px}
h1{font-size:clamp(32px,4.4vw,46px); line-height:1.07; letter-spacing:-.025em; color:var(--ink);
   margin:0 auto 20px; text-wrap:balance; font-weight:800}
.lede{font-size:19.5px; color:var(--body); margin:0 auto 26px; text-wrap:pretty}
.chips{display:flex; flex-wrap:wrap; gap:9px; margin:0 auto 8px; padding:0; list-style:none}
.chip{font-size:12.5px; letter-spacing:.02em; padding:5px 11px; border-radius:999px;
      border:1px solid var(--rule); color:var(--muted); background:var(--surface)}
.chip b{color:var(--ink); font-weight:600}

.toc{margin:34px auto 0; padding:0; list-style:none; columns:2; column-gap:30px}
.toc li{break-inside:avoid; margin:0 0 2px}
.toc a{display:flex; gap:12px; align-items:baseline; text-decoration:none; color:var(--body);
       font-size:14.5px; padding:5px 8px; border-radius:5px}
.toc a:hover{background:var(--rule-soft); color:var(--ink)}
.toc a span{font-family:ui-monospace,Menlo,Consolas,monospace; font-size:12px; color:var(--amber);
            font-variant-numeric:tabular-nums}
@media(max-width:640px){.toc{columns:1}}

section{padding:60px 0 4px; border-top:1px solid var(--rule-soft)}
section:first-of-type{border-top:0}
.eyebrow{font-family:ui-monospace,Menlo,Consolas,monospace; font-size:12.5px; letter-spacing:.1em;
         color:var(--amber); margin:0 auto 10px; font-variant-numeric:tabular-nums;
         text-transform:uppercase}
h2{font-size:clamp(24px,2.7vw,30px); line-height:1.15; letter-spacing:-.02em; color:var(--ink);
   margin:0 auto 26px; text-wrap:balance; font-weight:750}
h3{font-size:18.5px; color:var(--ink); margin:40px auto 14px; letter-spacing:-.008em; font-weight:700;
   display:flex; gap:11px; align-items:baseline}
.h3-n{font-family:ui-monospace,Menlo,Consolas,monospace; font-size:13px; color:var(--teal);
      font-weight:500; font-variant-numeric:tabular-nums}
/* One rule owns the reading column. A later `margin` shorthand here would silently reset
   margin-inline and un-centre every paragraph, so width and margin are set together. */
section > p{margin:0 auto 20px}
strong{color:var(--ink); font-weight:650}
em{font-style:italic}
a{color:var(--teal)}
code{font-size:.87em; background:var(--rule-soft); padding:1.5px 5px; border-radius:4px; color:var(--ink)}

ul{margin:0 auto 24px; padding-left:0; list-style:none}
section > ul > li{position:relative; padding-left:22px; margin:0 0 11px}
section > ul > li::before{content:""; position:absolute; left:3px; top:.62em; width:6px; height:6px;
  border-radius:1px; background:var(--amber-mark); opacity:.85}

.note{margin:0 auto 26px; padding:18px 22px; border-radius:3px;
      background:var(--surface); border:1px solid var(--rule); border-left:3px solid var(--amber-mark);
      font-size:16.5px}
.note.caution{border-left-color:var(--red)}
.note.finding{border-left-color:var(--amber-mark)}

.tw{max-width:calc(var(--col) + 150px)!important; margin:4px auto 32px; overflow-x:auto}
table{width:100%; border-collapse:collapse; font-size:15px;
      font-family:ui-sans-serif,system-ui,"Segoe UI",Roboto,Arial,sans-serif}
th{text-align:left; font-size:11.5px; letter-spacing:.09em; text-transform:uppercase; color:var(--muted);
   font-weight:600; padding:0 16px 9px 0; border-bottom:1px solid var(--rule)}
td{padding:13px 16px 13px 0; border-bottom:1px solid var(--rule-soft); vertical-align:top;
   font-variant-numeric:tabular-nums}
td:first-child{color:var(--ink)}
tr:last-child td{border-bottom:0}

.fig{margin:34px auto 40px; max-width:1180px}
.fig-frame{background:var(--fig-bg); border-radius:6px; overflow:hidden; border:1px solid var(--rule);
           line-height:0}
.fig svg{width:100%; height:auto; display:block}
figcaption{margin:12px 0 0; font-size:13.5px; color:var(--muted); display:flex; gap:12px;
           align-items:baseline; flex-wrap:wrap}
.fig-n{font-family:ui-monospace,Menlo,Consolas,monospace; font-size:11.5px; letter-spacing:.07em;
       text-transform:uppercase; color:var(--amber); white-space:nowrap}
figcaption code{background:none; padding:0}

footer{margin-top:70px; padding-top:26px; border-top:1px solid var(--rule);
       font-size:14.5px; color:var(--muted)}
a:focus-visible,.toc a:focus-visible{outline:2px solid var(--amber-mark); outline-offset:2px}
@media (prefers-reduced-motion:reduce){*{transition:none!important; animation:none!important}}
"""
CSS = CSS.replace("--sans: ui-sans-serif, system-ui, \"Segoe UI\", Roboto, "
                  "\"Helvetica Neue\", Arial, sans-serif;\n", "")

page = f"""<title>SumoSharp — what it adds on top of SUMO</title>
<style>{CSS}</style>
<div id="prog"></div>
<div class="wrap">
<header class="top">
  <p class="kicker">Feature presentation &middot; written companion</p>
  <h1>What SumoSharp adds on top of SUMO</h1>
  <p class="lede">The deck is for the room; this is for reading afterwards. Same spine, more prose —
  and every claim labelled with the kind of evidence behind it.</p>
  <ul class="chips">
    <li class="chip"><b>Audience</b> &nbsp;technical stakeholders, and engineers building against it</li>
    <li class="chip"><b>Posture</b> &nbsp;proof of concept — many mechanisms, none perfected</li>
    <li class="chip"><b>Diagrams</b> &nbsp;17, generated from one script</li>
  </ul>
  <ol class="toc">{toc}</ol>
</header>
{body}
<footer>
  Source: <code>docs/presentation/PRESENTATION.md</code>. Diagrams generated by
  <code>docs/presentation/gen_svg.py</code>. The slide deck built from the same set is
  <code>SumoSharp-features.pptx</code>.
</footer>
</div>
<script>
const p=document.getElementById('prog');
addEventListener('scroll',()=>{{const h=document.body.scrollHeight-innerHeight;
p.style.width=(h>0?(scrollY/h)*100:0)+'%';}},{{passive:true}});
</script>
"""
OUT.write_text(page, encoding="utf-8")
print(f"wrote {OUT}  {len(page)/1024:.0f} KiB")
print("figures:", page.count('class="fig"'), " sections:", page.count("<section id="))
