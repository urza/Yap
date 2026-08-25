#!/usr/bin/env python3
"""Convert the extracted Tea House JPEG layers into WebP for Yap's wwwroot.

Reads  images/<scene>/<layer>.jpg          (the untouched archive, 55 files)
Writes ../../Yap/wwwroot/images/themes/teahouse/<scene>/<layer>.webp

Encoding is chosen per layer role:

  canvastile          -> LOSSLESS
      It is a single flat colour in every scene (see palette.json) and it is the
      layer whose colour must match the CSS --bg-primary fallback *exactly*, or a
      faint edge shows where the tiled layer meets the solid fallback underneath.
      Lossless costs ~0K here, so there is no reason to risk it.

  headertile / footertile -> LOSSY q=95
      These repeat, so the worry was lossy block error landing on the edges where
      a tile abuts its own copy. Measured, it does not: mean abs error at the
      tiling columns is the same as in the interior (e.g. 1.43 vs 1.21 at q95),
      because the sources are *already* JPEG and their block grid is baked in.
      Lossless would have to encode that existing noise and comes out LARGER than
      the source JPEG (footertile 79K -> 106K); q95 is 2-4x smaller instead.

  header_bg / footer_bg_rside -> LOSSY q=88
      Painted once, no edges meeting themselves. footer_bg_rside (the teahouse,
      1020x399) is the heaviest layer in the set and the only place where the
      extra step down from q95 buys real weight.

Usage: python3 build_assets.py [--dry-run]
Requires: pillow
"""
import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "images")
DST = os.path.abspath(os.path.join(HERE, "..", "..", "Yap", "wwwroot",
                                   "images", "themes", "teahouse"))

SCENES = ["midnight", "2am", "314am", "4am", "6am", "8am", "10am",
          "noon", "2pm", "4pm", "6pm", "8pm", "10pm"]

# Layer -> quality. None means lossless. See the module docstring for why each.
LAYERS = {
    "canvastile_bg": None,
    "headertile_bg": 95,
    "footertile_bg_rside": 95,
    "header_bg": 88,
    "footer_bg_rside": 88,
}


def convert(src, dst, quality, dry_run):
    im = Image.open(src).convert("RGB")
    if not dry_run:
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        if quality is None:
            im.save(dst, "WEBP", lossless=True, quality=100, method=6)
        else:
            im.save(dst, "WEBP", quality=quality, method=6)
    return os.path.getsize(src), (os.path.getsize(dst) if not dry_run else 0), im.size


def build_preview(dry_run):
    """Theme-picker thumbnail: one composed scene, downscaled.

    Composed at full size first and then resized, because the layers are laid out
    in absolute pixels - composing straight at thumbnail size would put a 1020px
    teahouse on a 480px canvas and show only its right-hand corner.
    """
    import compose
    scene = "6pm"  # lanterns lit, warm sky: the most recognisable scene at 480px
    im = compose.compose(1440, 810, scene).resize((480, 270), Image.LANCZOS)
    out = os.path.join(DST, "preview.webp")
    if not dry_run:
        os.makedirs(DST, exist_ok=True)
        im.save(out, "WEBP", quality=90, method=6)
    print(f"\npreview: {scene} -> {out} ({os.path.getsize(out)//1024 if not dry_run else 0}K)")


def main():
    dry_run = "--dry-run" in sys.argv
    total_src = total_dst = 0
    n = 0
    print(f"{'scene':10}{'jpeg':>9}{'webp':>9}{'saved':>8}   layers")
    print("-" * 60)
    for scene in SCENES:
        s_src = s_dst = 0
        names = []
        for layer, quality in LAYERS.items():
            src = os.path.join(SRC, scene, layer + ".jpg")
            if not os.path.exists(src):
                continue  # 10am/2pm reuse another scene's env layers; 6am/6pm/8pm have no header_bg
            dst = os.path.join(DST, scene, layer + ".webp")
            a, b, _ = convert(src, dst, quality, dry_run)
            s_src += a
            s_dst += b
            names.append(layer.replace("_bg", "").replace("_rside", ""))
            n += 1
        total_src += s_src
        total_dst += s_dst
        pct = (1 - s_dst / s_src) * 100 if s_src and s_dst else 0
        print(f"{scene:10}{s_src//1024:>8}K{s_dst//1024:>8}K{pct:>7.0f}%   {' '.join(names)}")
    print("-" * 60)
    pct = (1 - total_dst / total_src) * 100 if total_src and total_dst else 0
    print(f"{'TOTAL':10}{total_src//1024:>8}K{total_dst//1024:>8}K{pct:>7.0f}%   {n} files")
    print(f"\nout: {DST}")
    build_preview(dry_run)


if __name__ == "__main__":
    main()
