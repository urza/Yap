#!/usr/bin/env python3
"""Render docs/spec/*.md to docs/spec/html/*.html.

The spec uses a small, fixed subset of Markdown: ATX headings, paragraphs, '-' and '1.' lists
(one level of nesting by two spaces), GFM tables, fenced code, '>' quotes, block images and
inline code, bold, italics and links. Nothing else is supported on purpose: the pages must stay
readable as plain text, and a converter without dependencies keeps the build runnable anywhere
python3 is. Rule paragraphs that start with a bold ID such as **IB-04** get that ID as an anchor.
"""
import html, pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "html"

TEMPLATE = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title} · Yap spec</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Sans+Condensed:wght@600;700&family=IBM+Plex+Sans:wght@400;500;600&family=IBM+Plex+Mono:wght@400;500;600&display=swap">
<style>
:root {{
  --bg: #f3f5f7; --surface: #ffffff; --surface-2: #e9edf1;
  --fg: #182029; --muted: #56606e; --line: #d4dae2; --line-strong: #a9b3c0;
  --accent: #0d6a87; --code-bg: #f8f9fb;
  --display: "IBM Plex Sans Condensed", "Arial Narrow", Arial, sans-serif;
  --body: "IBM Plex Sans", "Segoe UI", system-ui, sans-serif;
  --mono: "IBM Plex Mono", ui-monospace, "Cascadia Mono", Consolas, Menlo, monospace;
  color-scheme: light;
}}
@media (prefers-color-scheme: dark) {{
  :root:not([data-theme="light"]) {{
    --bg: #0e1115; --surface: #151a20; --surface-2: #1d232b; --fg: #e3e7ed; --muted: #97a1b0;
    --line: #29303a; --line-strong: #3f4855; --accent: #5bb7d2; --code-bg: #11151a; color-scheme: dark;
  }}
}}
:root[data-theme="dark"] {{
  --bg: #0e1115; --surface: #151a20; --surface-2: #1d232b; --fg: #e3e7ed; --muted: #97a1b0;
  --line: #29303a; --line-strong: #3f4855; --accent: #5bb7d2; --code-bg: #11151a; color-scheme: dark;
}}
* {{ box-sizing: border-box; }}
body {{ margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.55 var(--body); -webkit-font-smoothing: antialiased; }}
main {{ max-width: 980px; margin: 0 auto; padding: 36px 16px 96px; display: flex; flex-direction: column; gap: 22px; }}
h1, h2, h3 {{ font-family: var(--display); line-height: 1.15; margin: 0; text-wrap: balance; }}
h1 {{ font-size: clamp(2rem, 4.5vw, 2.8rem); font-weight: 700; }}
h2 {{ font-size: clamp(1.4rem, 2.6vw, 1.8rem); font-weight: 600; padding-top: 22px; margin-top: 14px; border-top: 1px solid var(--line-strong); }}
h3 {{ font-size: 1.12rem; font-weight: 600; margin-top: 6px; }}
p, ul, ol {{ margin: 0; max-width: 76ch; }}
ul, ol {{ padding-left: 1.3em; display: flex; flex-direction: column; gap: 6px; }}
li > ul, li > ol {{ margin-top: 6px; }}
a {{ color: var(--accent); }}
code {{ font-family: var(--mono); font-size: 0.88em; background: var(--surface-2); padding: 0.05em 0.3em; border-radius: 3px; overflow-wrap: anywhere; }}
pre {{ margin: 0; padding: 12px 16px; overflow-x: auto; border: 1px solid var(--line); border-radius: 6px; background: var(--code-bg); font: 0.82rem/1.5 var(--mono); }}
pre code {{ background: none; padding: 0; font-size: inherit; }}
blockquote {{ margin: 0; padding: 10px 16px; border-left: 3px solid var(--accent); background: var(--surface); border-radius: 4px; max-width: 76ch; }}
.eyebrow {{ font-family: var(--mono); font-size: 0.74rem; letter-spacing: 0.1em; text-transform: uppercase; color: var(--muted); }}
nav.pages {{ display: flex; flex-wrap: wrap; gap: 6px 8px; }}
nav.pages a, nav.pages span {{ display: inline-block; padding: 3px 10px; border: 1px solid var(--line); border-radius: 4px; text-decoration: none; color: var(--fg); background: var(--surface); font-size: 0.86rem; }}
nav.pages span {{ color: var(--muted); border-style: dashed; }}
nav.pages a[aria-current] {{ border-color: var(--accent); color: var(--accent); }}
.rule {{ border: 1px solid var(--line); border-left: 3px solid var(--accent); background: var(--surface); border-radius: 4px; padding: 10px 14px; max-width: 76ch; scroll-margin-top: 16px; }}
.rule .id {{ font-family: var(--mono); font-size: 0.8rem; color: var(--accent); margin-right: 8px; }}
.rule .why {{ color: var(--muted); }}
figure {{ margin: 0; display: flex; flex-direction: column; gap: 8px; }}
figure img {{ display: block; max-width: 100%; height: auto; border: 1px solid var(--line); border-radius: 6px; background: var(--surface); }}
figcaption {{ font-size: 0.9rem; color: var(--muted); max-width: 80ch; }}
.tablewrap {{ overflow-x: auto; border: 1px solid var(--line); border-radius: 6px; background: var(--surface); }}
table {{ border-collapse: collapse; width: 100%; font-size: 0.9rem; }}
th, td {{ text-align: left; vertical-align: top; padding: 8px 12px; border-bottom: 1px solid var(--line); }}
th {{ font-family: var(--display); font-weight: 600; background: var(--surface-2); white-space: nowrap; }}
tbody tr:last-child td {{ border-bottom: 0; }}
footer {{ font-size: 0.82rem; color: var(--muted); border-top: 1px solid var(--line); padding-top: 14px; }}
</style>
</head>
<body>
<main>
<div class="eyebrow">Yap · product specification</div>
<nav class="pages" aria-label="Spec pages">{nav}</nav>
{body}
<footer>Source: <code>docs/spec/{source}</code>. Edit the Markdown, then run <code>python3 docs/spec/tools/build.py</code>.</footer>
</main>
</body>
</html>
"""


def pages():
    return sorted(p for p in ROOT.glob("*.md"))


def out_name(md: pathlib.Path) -> str:
    return "index.html" if md.name == "README.md" else md.stem + ".html"


def page_title(text: str) -> str:
    m = re.search(r"^# (.+)$", text, re.M)
    return m.group(1).strip() if m else "Untitled"


def nav_html(current: pathlib.Path, index_text: str) -> str:
    # The Pages table in README.md is the one list of pages, planned ones included.
    items = ['<a href="index.html"%s>Index</a>' % (' aria-current="page"' if current.name == "README.md" else "")]
    for m in re.finditer(r"^\| \[([^\]]+)\]\(([^)]+\.md)\) \| [A-Z]+ \| ([^|]+) \|$", index_text, re.M):
        title, target, status = m.group(1), m.group(2), m.group(3).strip()
        if (ROOT / target).exists():
            cur = ' aria-current="page"' if current.name == target else ""
            items.append('<a href="%s"%s>%s</a>' % (html.escape(target[:-3] + ".html"), cur, html.escape(title)))
        else:
            items.append('<span title="%s">%s</span>' % (html.escape(status), html.escape(title)))
    return "".join(items)


def inline(text: str) -> str:
    """Escape, then apply inline marks. Code spans are protected first so their content stays literal."""
    codes = []

    def keep(m):
        codes.append("<code>%s</code>" % html.escape(m.group(1)))
        return "\x00%d\x00" % (len(codes) - 1)

    text = re.sub(r"`([^`]+)`", keep, text)
    text = html.escape(text, quote=False)
    text = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", text)
    text = re.sub(r"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", r"<em>\1</em>", text)
    text = re.sub(r"\[([^\]]+)\]\(([^)\s]+)\)", lambda m: '<a href="%s">%s</a>' % (link(m.group(2)), m.group(1)), text)
    text = re.sub(r"\x00(\d+)\x00", lambda m: codes[int(m.group(1))], text)
    return text


def link(target: str) -> str:
    if target.startswith(("http://", "https://", "#")):
        return html.escape(target)
    path, _, anchor = target.partition("#")
    if path == "README.md":
        path = "index.html"
    elif path.endswith(".md"):
        path = path[:-3] + ".html"
    return html.escape(path + ("#" + anchor if anchor else ""))


RULE = re.compile(r"^\*\*([A-Z]{2,3}-\d{2,3})\*\*\s+(.*)$", re.S)


def paragraph(text: str) -> str:
    m = RULE.match(text)
    if m:
        body = inline(m.group(2)).replace("<em>Why:</em>", '<em class="why">Why:</em>')
        return '<p class="rule" id="%s"><span class="id">%s</span>%s</p>' % (m.group(1), m.group(1), body)
    return "<p>%s</p>" % inline(text)


def table(rows):
    cells = [[c.strip() for c in re.sub(r"<!--.*?-->", "", r).strip().strip("|").split("|")] for r in rows]   # a lint comment in a row is not a cell
    head, body = cells[0], cells[2:]
    out = ['<div class="tablewrap"><table><thead><tr>']
    out += ["<th>%s</th>" % inline(c) for c in head]
    out.append("</tr></thead><tbody>")
    for r in body:
        out.append("<tr>" + "".join("<td>%s</td>" % inline(c) for c in r) + "</tr>")
    out.append("</tbody></table></div>")
    return "".join(out)


def list_html(items):
    """items: list of (indent, marker, text). One level of nesting."""
    def render(level_items):
        ordered = level_items[0][1][0].isdigit()
        out = ["<ol>" if ordered else "<ul>"]
        i = 0
        while i < len(level_items):
            indent, _, text = level_items[i]
            children = []
            i += 1
            while i < len(level_items) and level_items[i][0] > indent:
                children.append(level_items[i])
                i += 1
            out.append("<li>%s%s</li>" % (inline(text), render(children) if children else ""))
        out.append("</ol>" if ordered else "</ul>")
        return "".join(out)
    return render(items)


def convert(text: str) -> str:
    lines = text.split("\n")
    out, i = [], 0
    while i < len(lines):
        line = lines[i]
        if not line.strip():
            i += 1
            continue
        if line.startswith("<!--"):
            while "-->" not in lines[i]:
                i += 1
            i += 1
            continue
        if line.startswith("```"):
            j = i + 1
            while j < len(lines) and not lines[j].startswith("```"):
                j += 1
            out.append("<pre><code>%s</code></pre>" % html.escape("\n".join(lines[i + 1:j])))
            i = j + 1
            continue
        m = re.match(r"^(#{1,3}) (.+)$", line)
        if m:
            level = len(m.group(1))
            title = m.group(2).strip()
            anchor = re.sub(r"[^a-z0-9]+", "-", title.lower()).strip("-")
            out.append('<h%d id="%s">%s</h%d>' % (level, anchor, inline(title), level))
            i += 1
            continue
        if line.startswith("|"):
            j = i
            while j < len(lines) and lines[j].startswith("|"):
                j += 1
            out.append(table(lines[i:j]))
            i = j
            continue
        if line.startswith(">"):
            j = i
            while j < len(lines) and lines[j].startswith(">"):
                j += 1
            inner = "\n".join(l[1:].lstrip() for l in lines[i:j])
            out.append("<blockquote>%s</blockquote>" % convert(inner))
            i = j
            continue
        m = re.match(r"^!\[([^\]]*)\]\(([^)]+)\)$", line)
        if m:
            src = m.group(2)
            if not src.startswith(("http://", "https://")):
                src = "../" + src
            out.append('<figure><img src="%s" alt="%s" loading="lazy"><figcaption>%s</figcaption></figure>'
                       % (html.escape(src), html.escape(m.group(1)), inline(m.group(1))))
            i += 1
            continue
        m = re.match(r"^(\s*)([-*]|\d+\.) (.+)$", line)
        if m:
            items = []
            while i < len(lines):
                m = re.match(r"^(\s*)([-*]|\d+\.) (.+)$", lines[i])
                if m:
                    items.append((len(m.group(1)), m.group(2), m.group(3)))
                elif lines[i].startswith("  ") and items and lines[i].strip():
                    indent, marker, text = items[-1]
                    items[-1] = (indent, marker, text + " " + lines[i].strip())
                else:
                    break
                i += 1
            out.append(list_html(items))
            continue
        j = i
        while j < len(lines) and lines[j].strip() and not re.match(r"^(#{1,3} |```|\||>|!\[|\s*([-*]|\d+\.) |<!--)", lines[j]):
            j += 1
        out.append(paragraph(" ".join(l.strip() for l in lines[i:j])))
        i = j
    return "\n".join(out)


def main():
    OUT.mkdir(exist_ok=True)
    index_text = (ROOT / "README.md").read_text(encoding="utf-8")
    for md in pages():
        text = md.read_text(encoding="utf-8")
        page = TEMPLATE.format(title=html.escape(page_title(text)), nav=nav_html(md, index_text),
                               body=convert(text), source=md.name)
        (OUT / out_name(md)).write_text(page, encoding="utf-8")
        print("built", OUT.relative_to(ROOT.parent.parent) / out_name(md))


if __name__ == "__main__":
    sys.exit(main())
