#!/usr/bin/env bash
# One-off generator of flags/<cc>.20.png and flags/<cc>.40.png from flag-icons (MIT, lipis/flag-icons)
# 4x3 SVGs; not part of the build — the PNGs are committed. Usage: make-flags.sh <flag-icons package dir>
# Needs rsvg-convert and python3 with Pillow. Only codes listed in CountryNames.cs are kept.
set -euo pipefail
src=${1:?flag-icons package dir}; cd "$(dirname "$0")"
mkdir -p flags licenses
codes=$(grep -o '{ *"[A-Z][A-Z]"' CountryNames.cs | grep -o '[A-Z][A-Z]' | tr 'A-Z' 'a-z' | sort -u)
for cc in $codes; do
  svg="$src/flags/4x3/$cc.svg"; [[ -f $svg ]] || continue
  for w in 20 40; do rsvg-convert -w $w -h $((w*3/4)) "$svg" -o "flags/$cc.$w.png"; done
done
python3 - <<'P'
import glob
from PIL import Image
for f in glob.glob("flags/*.png"):
    im = Image.open(f).convert("RGB")
    im.quantize(colors=64, method=Image.Quantize.MEDIANCUT).save(f, optimize=True)
P
cp "$src/LICENSE" licenses/FLAG_ICONS_LICENSE.txt
du -cb flags/*.png | tail -1
