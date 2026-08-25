#!/usr/bin/env python3
"""Author the app's background-pattern tiles as SVG mask data-URIs.

The shipped patterns in wwwroot/app.css (--pattern-waves/-drops/-specks/
-specks-dense/-seigaiha) are generated here. To change a motif: edit PATTERNS,
run this script, open preview.html to judge the result over each theme's
colours, then paste the emitted "--pattern-*" lines over the ones in app.css.

Each tile is sparse: a few soft motifs scattered at varied positions/rotations,
none touching, none crossing the tile edge - which is ALL that "seamless"
requires; it does not mean the motifs form a continuous line or grid.

The URIs are masks: only alpha matters (shapes are drawn opaque black), the
colour comes from the consuming element's background-color, which is what lets
one shape serve every theme. The preview exaggerates tint alpha to 0.5 so the
shapes are judgeable; production alphas live in themes.css (~0.05-0.11).
"""
import urllib.parse

def uri(w, h, body):
    svg = f"<svg xmlns='http://www.w3.org/2000/svg' width='{w}' height='{h}'>{body}</svg>"
    return "url(\"data:image/svg+xml," + svg.replace('<','%3C').replace('>','%3E').replace('#','%23') + "\")"

S = "stroke='black' fill='none' stroke-linecap='round'"
TILDE = "M0 0 q6 -6 12 0 t12 0"
DROP  = "M0 -6 C3 -2 5 1 5 3.5 A5 5 0 1 1 -5 3.5 C-5 1 -3 -2 0 -6 Z"
ARCS  = (f"<path d='M-14 0 a14 14 0 0 1 28 0' {S} stroke-width='1.5'/>"
         f"<path d='M-9 0 a9 9 0 0 1 18 0' {S} stroke-width='1.5'/>"
         f"<path d='M-4.5 0 a4.5 4.5 0 0 1 9 0' {S} stroke-width='1.5'/>")

PATTERNS = {
  # little waves - five tildes rising bottom-left -> top-right (rotate(-45) in
  # SVG's y-down coordinates), sizes spread 0.6..1.25, and each stroke painted
  # with an end-fading gradient: stops go transparent -> opaque -> transparent,
  # and a mask reads alpha, so the tips melt away instead of stopping.
  "waves": uri(104,104,
    "<defs><linearGradient id='f'>"
    "<stop offset='0' stop-color='black' stop-opacity='0'/>"
    "<stop offset='0.3' stop-color='black'/>"
    "<stop offset='0.7' stop-color='black'/>"
    "<stop offset='1' stop-color='black' stop-opacity='0'/>"
    "</linearGradient></defs>"
    + "".join(
      f"<g transform='translate({x} {y}) rotate({r}) scale({sc})'>"
      f"<path d='{TILDE}' stroke='url(#f)' fill='none' stroke-linecap='round' stroke-width='{w}'/></g>"
      for x,y,r,sc,w in [
        (10,34,-45,1.25,1.5), (48,26,-41,0.8,1.9), (76,58,-48,1.0,1.7),
        (24,82,-44,0.6,2.2), (62,96,-46,0.9,1.8)])),
  # raindrops - four sizes, slight tilts
  "drops": uri(80,80,
    f"<g transform='translate(18 20)'><path d='{DROP}' fill='black'/></g>"
    f"<g transform='translate(56 32) rotate(14) scale(0.72)'><path d='{DROP}' fill='black'/></g>"
    f"<g transform='translate(32 60) rotate(-11) scale(0.85)'><path d='{DROP}' fill='black'/></g>"
    f"<g transform='translate(66 66) scale(0.55)'><path d='{DROP}' fill='black'/></g>"),
  # phosphor specks - scattered soft dots, varied radii
  "specks": uri(64,64, "".join(
    f"<circle cx='{x}' cy='{y}' r='{r}' fill='black'/>" for x,y,r in
    [(8,10,1.4),(28,6,1.0),(50,14,1.8),(14,34,1.0),(40,38,1.5),(60,52,1.1),(22,56,1.6)])),
  # denser variant for cypherpunk DMs ("encrypted = denser")
  "specks-dense": uri(64,64, "".join(
    f"<circle cx='{x}' cy='{y}' r='{r}' fill='black'/>" for x,y,r in
    [(8,10,1.4),(28,6,1.0),(50,14,1.8),(14,34,1.0),(40,38,1.5),(60,52,1.1),(22,56,1.6),
     (46,60,1.2),(58,28,1.0),(6,48,1.2),(34,22,1.1),(52,44,1.4)])),
  # seigaiha fragments - nested wave-crest arcs, scattered
  "seigaiha": uri(96,96,
    f"<g transform='translate(26 30) rotate(-6) scale(0.85)'>{ARCS}</g>"
    f"<g transform='translate(70 68) rotate(4) scale(0.85)'>{ARCS}</g>"
    f"<g transform='translate(74 20) scale(0.6)'><path d='M-14 0 a14 14 0 0 1 28 0' {S} stroke-width='1.5'/>"
    f"<path d='M-9 0 a9 9 0 0 1 18 0' {S} stroke-width='1.5'/></g>"),
}

BGS = [("discord-dark","#36393f","hsl(0 0% 100% / 0.5)"),
       ("cypherpunk","#07090a","hsl(140 100% 60% / 0.5)"),
       ("cyberpunk","#0d0820","hsl(190 90% 55% / 0.5)"),
       ("teahouse-noon","#e0e0a0","hsl(60 20% 20% / 0.5)")]

css_rules = "".join(
    f".pat-{name}{{-webkit-mask-image:{css};mask-image:{css}}}\n"
    for name, css in PATTERNS.items())

cells = ""
for name in PATTERNS:
    row = "".join(
        f'<div class="cell" style="background-color:{bg}">'
        f'<div class="pat pat-{name}" style="background-color:{tint}"></div>'
        f'<span>{label}</span></div>'
        for label,bg,tint in BGS)
    cells += f"<h2>{name}</h2><div class='row'>{row}</div>"

open("preview.html","w").write(f"""<!doctype html><meta charset=utf-8><style>
body{{margin:0;font:12px sans-serif;background:#222;color:#ddd;padding:12px}}
.row{{display:flex;gap:8px;margin-bottom:16px}}
.cell{{position:relative;width:330px;height:210px;border-radius:6px;overflow:hidden}}
.pat{{position:absolute;inset:0}}
.cell span{{position:absolute;left:8px;bottom:6px;opacity:.6}}
h2{{margin:6px 0}}
{css_rules}</style>{cells}""")
print("preview.html written")
# NOTE: tint alpha is exaggerated (0.5) so the shapes are judgeable; production stays ~0.05-0.11

print()
print("/* --- paste-ready --- */")
for name, css in PATTERNS.items():
    print(f"    --pattern-{name}: {css};")
