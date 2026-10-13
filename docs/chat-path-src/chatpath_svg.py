"""Diagram generator for the chat-path page.

Every diagram is computed on a grid so labels never collide. Text width is estimated from the
monospace face (0.6 em per character), which is why all diagram text is set in IBM Plex Mono.
"""

CW = {11: 6.6, 10: 6.0, 12: 7.2}


def esc(s):
    return s.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')


def wrap(text, maxw, size=11):
    maxc = max(6, int(maxw // CW[size]))
    lines, cur = [], ''
    for word in text.split(' '):
        t = (cur + ' ' + word).strip()
        if len(t) <= maxc:
            cur = t
        else:
            if cur:
                lines.append(cur)
            cur = word
    if cur:
        lines.append(cur)
    return lines


def textw(s, size=11):
    return len(s) * CW[size]


def markers(pid):
    out = []
    for kind in ('fg', 'accent', 'muted', 'sync', 'time'):
        out.append(
            f'<marker id="{pid}-{kind}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" '
            f'orient="auto-start-reverse"><path d="M0,0 L10,5 L0,10 z" class="mk-{kind}"/></marker>')
    return '<defs>' + ''.join(out) + '</defs>'


def label(x, y, lines, size=11, anchor='middle', cls='d-text', bg=True, lh=14):
    """Text block with an opaque backing so it stays readable over lifelines."""
    out = []
    for i, line in enumerate(lines):
        w = textw(line, size)
        bx = {'middle': x - w / 2, 'start': x, 'end': x - w}[anchor]
        ly = y + i * lh
        if bg:
            out.append(f'<rect class="d-bg" x="{bx - 3:.1f}" y="{ly - size + 1:.1f}" width="{w + 6:.1f}" height="{size + 4}" rx="2"/>')
        out.append(f'<text class="{cls}" x="{x:.1f}" y="{ly:.1f}" text-anchor="{anchor}" font-size="{size}">{esc(line)}</text>')
    return ''.join(out)


STYLE = {
    'http': ('d-http', 'fg', ''),
    'reply': ('d-http', 'fg', ' stroke-dasharray="6 4"'),
    'hub': ('d-hub', 'accent', ''),
    'stream': ('d-hub', 'accent', ' stroke-dasharray="6 4"'),
    'call': ('d-call', 'muted', ''),
}


def sequence(pid, lanes, steps, bands=(), width=1000, aria=''):
    """lanes: [(key, title, subtitle)]; steps: dicts with frm, to, text, note, kind."""
    keys = [k for k, _, _ in lanes]
    n = len(lanes)
    left, right = 140, width - 96
    xs = [left + (right - left) * i / (n - 1) for i in range(n)]
    spacing = (right - left) / (n - 1)
    boxw = min(spacing - 16, 176)
    band_at = {b[0]: b for b in bands}
    body, rows = [], []
    y = 84
    for i, st in enumerate(steps):
        if i in band_at:
            y += 22
        a, b = keys.index(st['frm']), keys.index(st['to'])
        kind = st.get('kind', 'call')
        note = st.get('note')
        top = y
        if a == b:
            x = xs[a]
            at_right = a == n - 1
            maxw = (x - 14 - 50) if at_right else (width - 12 - (x + 14))
            lines = wrap(st['text'], maxw, 11)
            nlines = wrap(note, maxw, 10) if note else []
            mid = top + 8
            h = 14 * len(lines) + 13 * len(nlines)
            body.append(f'<rect class="d-act" x="{x - 5:.1f}" y="{mid - 8:.1f}" width="10" height="{max(18, h + 2):.1f}" rx="2"/>')
            tx = x - 14 if at_right else x + 14
            anchor = 'end' if at_right else 'start'
            body.append(label(tx, mid + 4, lines, 11, anchor))
            if nlines:
                body.append(label(tx, mid + 4 + 14 * len(lines), nlines, 10, anchor, 'd-muted', lh=13))
            ay = mid + 1
            y = top + max(30, h + 18)
        else:
            x1, x2 = xs[a], xs[b]
            maxw = abs(x2 - x1) - 20
            lines = wrap(st['text'], maxw, 11)
            nlines = wrap(note, maxw, 10) if note else []
            ay = top + 14 * len(lines) + 4
            cls, mk, dash = STYLE[kind]
            off = 6 if x2 > x1 else -6
            body.append(f'<line class="{cls}" x1="{x1 + off:.1f}" y1="{ay}" x2="{x2 - off:.1f}" y2="{ay}"{dash} marker-end="url(#{pid}-{mk})"/>')
            mx = (x1 + x2) / 2
            body.append(label(mx, top + 10, lines, 11))
            if nlines:
                body.append(label(mx, ay + 15, nlines, 10, 'middle', 'd-muted', lh=13))
            y = ay + 13 * len(nlines) + (22 if nlines else 18)
        rows.append((top, y, ay))
        if any(b[1] == i for b in bands):
            y += 12  # keep the next label clear of the band edge
        body.append(f'<circle class="d-num" cx="24" cy="{ay:.1f}" r="10"/>'
                    f'<text class="d-numtext" x="24" y="{ay + 4:.1f}" text-anchor="middle" font-size="11">{i + 1}</text>')
    height = y + 16
    back = []
    for first, last, text in bands:
        t0 = rows[first][0] - 22
        t1 = rows[last][1] + 2
        back.append(f'<rect class="d-band" x="6" y="{t0}" width="{width - 12}" height="{t1 - t0}" rx="4"/>')
        back.append(f'<text class="d-bandtext" x="44" y="{t0 + 15}" font-size="10">{esc(text)}</text>')
    head = []
    for (k, title, sub), x in zip(lanes, xs):
        head.append(f'<line class="d-life" x1="{x:.1f}" y1="58" x2="{x:.1f}" y2="{height - 6}"/>')
        head.append(f'<rect class="d-lane" x="{x - boxw / 2:.1f}" y="10" width="{boxw:.1f}" height="46" rx="4"/>')
        head.append(f'<text class="d-text" x="{x:.1f}" y="29" text-anchor="middle" font-size="11.5" font-weight="600">{esc(title)}</text>')
        head.append(f'<text class="d-muted" x="{x:.1f}" y="45" text-anchor="middle" font-size="10">{esc(sub)}</text>')
    return (f'<svg viewBox="0 0 {width} {height:.0f}" role="img" aria-label="{esc(aria)}">' + markers(pid)
            + ''.join(back) + ''.join(head) + ''.join(body) + '</svg>')


# ---------- hand-placed helpers for block diagrams ----------

def box(x, y, w, h, title, sub=None, cls='d-lane', size=12):
    subs = [] if sub is None else (sub if isinstance(sub, list) else [sub])
    block = 12 + 13 * len(subs)
    ty = y + (h - block) / 2 + 11
    out = [f'<rect class="{cls}" x="{x}" y="{y}" width="{w}" height="{h}" rx="4"/>',
           f'<text class="d-text" x="{x + w / 2}" y="{ty:.1f}" text-anchor="middle" font-size="{size}" font-weight="600">{esc(title)}</text>']
    for i, line in enumerate(subs):
        out.append(f'<text class="d-muted" x="{x + w / 2}" y="{ty + 14 + i * 13:.1f}" text-anchor="middle" font-size="10">{esc(line)}</text>')
    assert textw(title, size) <= w - 8, f'box title too wide: {title!r} in {w}'
    for line in subs:
        assert textw(line, 10) <= w - 8, f'box text too wide: {line!r} in {w}'
    assert ty + 14 + 13 * (len(subs) - 1) <= y + h - 3 or not subs, f'box too short: {title!r}'
    return ''.join(out)


def arrow(pid, pts, kind='call', text=None, tx=None, ty=None, anchor='middle', note=None):
    cls, mk, dash = STYLE[kind]
    d = 'M' + ' L'.join(f'{x},{y}' for x, y in pts)
    out = [f'<path class="{cls}" d="{d}" fill="none"{dash} marker-end="url(#{pid}-{mk})"/>']
    if text:
        out.append(label(tx, ty, text if isinstance(text, list) else [text], 11, anchor))
    if note:
        out.append(label(tx, ty + 14 * (len(text) if isinstance(text, list) else 1), note if isinstance(note, list) else [note], 10, anchor, 'd-muted', lh=13))
    return ''.join(out)


def svg(pid, w, h, parts, aria):
    return f'<svg viewBox="0 0 {w} {h}" role="img" aria-label="{esc(aria)}">' + markers(pid) + ''.join(parts) + '</svg>'
