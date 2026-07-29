#!/usr/bin/env bash
# Build the slide deck from the committed diagram set. Run from anywhere.
#
# Three steps, in order, because each depends on the last:
#   1. regenerate the SVGs            (gen_svg.py)
#   2. rasterise, and crop the title band off each one -- the slide head states the title, so the
#      image must not repeat it; PowerPoint's SVG support is also version-dependent, hence PNG
#   3. build the .pptx                (build_pptx.js)
#
# Then verify. `validate.py` catches the chart and slide-XML faults PowerPoint refuses and every
# other tool accepts. `qa_render.py` exists because LibreOffice cannot load any file in the CI
# sandbox -- even a plain .txt -- so the usual soffice -> pdftoppm route for visual QA is
# unavailable; it renders the shipped package's real geometry instead.
set -euo pipefail
cd "$(dirname "$0")"
OUT=build
mkdir -p "$OUT/png" "$OUT/png-slides"

python3 gen_svg.py

python3 - <<'PY'
import pathlib
import cairosvg
from PIL import Image

CROP = int(110 * 1.6)   # 110 SVG units of title band; the lowest content any diagram draws is y=130
out = pathlib.Path("build")
for f in sorted(pathlib.Path("svg").glob("*.svg")):
    cairosvg.svg2png(url=str(f), write_to=str(out / "png" / f"{f.stem}.png"), scale=1.6)
for f in sorted((out / "png").glob("*.png")):
    im = Image.open(f)
    im.crop((0, CROP, im.width, im.height)).save(out / "png-slides" / f.name)

# 05 is the one diagram too tall to read on a single slide, so the deck shows it in two parts.
im = Image.open(out / "png-slides" / "05-weave.png")
CUT = int((505 - 110) * 1.6)
im.crop((0, 0, im.width, CUT)).save(out / "png-slides" / "05a-weave-walking.png")
im.crop((0, CUT, im.width, im.height)).save(out / "png-slides" / "05b-weave-standing.png")
print(f"rasterised and cropped into {out}/png-slides")
PY

# pptxgenjs is not vendored in the repo -- install it into build/ on first run rather than
# committing node_modules. NODE_PATH lets the generator resolve it from there.
if ! NODE_PATH="$PWD/build/node_modules" node -e "require('pptxgenjs')" 2>/dev/null; then
  echo "installing pptxgenjs into build/ ..."
  (cd build && npm install --silent --no-package-lock --no-save pptxgenjs)
fi
NODE_PATH="$PWD/build/node_modules" node build_pptx.js
python3 "${PPTX_SKILL:-/root/.claude/skills/pptx}/scripts/office/validate.py" build/SumoSharp-features.pptx
python3 qa_render.py build/SumoSharp-features.pptx
echo
echo "deck:        build/SumoSharp-features.pptx"
echo "QA renders:  build/qa/slide-*.png  (inspect these -- they are the shipped geometry)"
