#!/usr/bin/env python3
"""Compose a full-screen Tea House background at any resolution.

The Gmail Teahouse theme is built from 5 image layers per time-of-day version:

  headertile_bg      - repeating strip across the top (repeat-x)
  canvastile_bg      - repeating tile filling the middle area
  footertile_bg_rside- repeating strip along the bottom (repeat-x)
  header_bg          - one-off element at top-left (the moving prop / moon)
  footer_bg_rside    - the teahouse itself, anchored bottom-right (no-repeat)

This composes all 5 layers at the given width/height, mirroring how Gmail
lays the theme out. Algorithm based on cybojanek/tea_house make.py
(https://github.com/cybojanek/tea_house), extended with header_bg.

Usage:
  python3 compose.py OUT.jpg 1920 1080 [version]
  version default: auto (from current local time)

Requires: pip install pillow
"""
import os
import sys

from PIL import Image

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "images")

VERSIONS = ["midnight", "2am", "314am", "4am", "6am", "8am", "10am",
            "noon", "2pm", "4pm", "6pm", "8pm", "10pm"]
NAMES = ["canvastile_bg", "headertile_bg", "header_bg",
         "footertile_bg_rside", "footer_bg_rside"]

# hour -> version, straight from the theme's own viewer (UndarkAido/TeaHouse)
TIMEMAP = ["midnight", "midnight", "2am", "314am", "4am", "4am", "6am", "6am",
           "8am", "8am", "10am", "10am", "noon", "noon", "2pm", "2pm",
           "4pm", "4pm", "6pm", "6pm", "8pm", "8pm", "10pm", "10pm"]

# header/canvas tiles shared between hours (10am uses 8am's, 2pm uses noon's)
ENV_OF = {"10am": "8am", "2pm": "noon"}


def auto_version():
    import datetime
    h = datetime.datetime.now().hour
    return TIMEMAP[h]


def load(version, name):
    """Load a layer, falling back to the shared environment version."""
    p = os.path.join(BASE, version, name + ".jpg")
    if not os.path.exists(p):
        p = os.path.join(BASE, ENV_OF.get(version, version), name + ".jpg")
    return Image.open(p).convert("RGB")


def compose(width, height, version):
    canvas = Image.new("RGB", (width, height))

    headertile = load(version, "headertile_bg")
    canvastile = load(version, "canvastile_bg")
    footertile = load(version, "footertile_bg_rside")
    footer = load(version, "footer_bg_rside")

    # 1. Top strip: tile headertile across the full width.
    for i in range(width // headertile.width + 1):
        canvas.paste(headertile, (i * headertile.width, 0))

    # 2. Middle: tile canvastile between the header strip and the footer strip.
    overlap_h = height - headertile.height - footertile.height
    if overlap_h > 0:
        for j in range(overlap_h // canvastile.height + 1):
            for i in range(width // canvastile.width + 1):
                canvas.paste(canvastile,
                             (i * canvastile.width,
                              j * canvastile.height + headertile.height))

    # 3. Bottom strip: tile footertile across the full width, starting just
    #    left of the teahouse and extending both directions.
    tile_x = width - footer.width + 120
    for x in range(tile_x, tile_x - footertile.width * (tile_x // footertile.width + 1), -footertile.width):
        canvas.paste(footertile, (x, height - footertile.height))
    x = tile_x
    while x < width:
        canvas.paste(footertile, (x, height - footertile.height))
        x += footertile.width

    # 4. Teahouse, anchored bottom-right.
    canvas.paste(footer, (width - footer.width, height - footer.height))

    # 5. Header prop (moon, lantern, ...), top-left, if this version has one.
    p = os.path.join(BASE, version, "header_bg.jpg")
    if os.path.exists(p):
        header = Image.open(p).convert("RGB")
        canvas.paste(header, (0, 0))

    return canvas


if __name__ == "__main__":
    if len(sys.argv) < 4:
        print(__doc__)
        sys.exit(1)
    out, width, height = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    version = sys.argv[4] if len(sys.argv) > 4 else auto_version()
    if version not in VERSIONS:
        print(f"unknown version {version!r}; choose from {VERSIONS}")
        sys.exit(1)
    compose(width, height, version).save(out, quality=92)
    print(f"wrote {out} ({width}x{height}, {version})")
