#!/usr/bin/env python3
"""Render docs/architecture/*.md to docs/architecture/html/*.html.

Reuses the spec's dependency-free Markdown converter (docs/spec/tools/build.py) and adds the two
things an architecture page needs that a spec page does not:

- fenced code with a language tag gets syntax highlighting (cs, js, json, html, css, bash);
- a block that starts with an HTML tag at column 0 is passed through as is, so a page can hold an
  inline SVG diagram or a hand-written figure.

The Markdown is the source. Never edit the HTML.
"""
import html, importlib.util, pathlib, re, sys

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent
OUT = ROOT / "html"


def load_spec_builder():
    """Load docs/spec/tools/build.py by path. Both builders are named build.py, so a plain import
    would resolve to whichever is first on sys.path."""
    path = ROOT.parent / "spec" / "tools" / "build.py"
    spec_ = importlib.util.spec_from_file_location("spec_build", path)
    module = importlib.util.module_from_spec(spec_)
    spec_.loader.exec_module(module)
    return module


spec = load_spec_builder()  # the spec converter: inline(), convert(), page_title(), TEMPLATE

# ---------------------------------------------------------------- highlighting

KEYWORDS = {
    "cs": "abstract as async await base bool break byte case catch char checked class const continue decimal default "
          "delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in "
          "int interface internal is lock long namespace new null object operator out override params private protected "
          "public readonly record ref return sbyte sealed short sizeof stackalloc static string struct switch this throw "
          "true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while with yield init required file",
    "js": "async await break case catch class const continue debugger default delete do else export extends finally for "
          "from function if import in instanceof let new null of return static super switch this throw true false try "
          "typeof undefined var void while with yield",
    "json": "true false null",
    "bash": "if then else elif fi for in do done while case esac function return exit export local echo",
    "css": "",
    "html": "",
}

TOKEN_RULES = {
    # Each rule: (css class, regex). Order matters: comments and strings first, so their content is not re-tokenised.
    "cs": [("c", r"//[^\n]*|/\*[\s\S]*?\*/"), ("s", r'\$?@?"(?:[^"\\]|\\.)*"|\'(?:[^\'\\]|\\.)*\''),
           ("n", r"\b\d[\d_]*(?:\.\d+)?[fFdDmMlLuU]?\b"), ("a", r"\[[A-Z]\w*(?:\([^)]*\))?\]"),
           ("k", None), ("t", r"\b[A-Z]\w*\b")],
    "js": [("c", r"//[^\n]*|/\*[\s\S]*?\*/"), ("s", r'`(?:[^`\\]|\\.)*`|"(?:[^"\\]|\\.)*"|\'(?:[^\'\\]|\\.)*\''),
           ("n", r"\b\d[\d_]*(?:\.\d+)?\b"), ("k", None), ("t", r"\b[A-Z]\w*\b")],
    "json": [("p", r'"(?:[^"\\]|\\.)*"(?=\s*:)'), ("s", r'"(?:[^"\\]|\\.)*"'), ("n", r"-?\b\d+(?:\.\d+)?\b"), ("k", None)],
    "bash": [("c", r"#[^\n]*"), ("s", r'"(?:[^"\\]|\\.)*"|\'[^\']*\''), ("k", None), ("v", r"\$\w+|\$\{[^}]*\}")],
    "css": [("c", r"/\*[\s\S]*?\*/"), ("s", r'"(?:[^"\\]|\\.)*"|\'[^\']*\''), ("p", r"[\w-]+(?=\s*:)"),
            ("t", r"[.#]?[\w-]+(?=[^{}]*\{)"), ("n", r"\b\d+(?:\.\d+)?(?:px|rem|em|%|s|ms|vh|vw|svh)?\b")],
    "html": [("c", r"<!--[\s\S]*?-->"), ("s", r'"[^"]*"'), ("t", r"</?[\w-]+|/?>"), ("p", r"\b[\w-]+(?==)")],
}


def highlight(code: str, lang: str) -> str:
    """Wrap tokens in <span class="x"> where x is c comment, s string, n number, k keyword, t type/tag,
    p property, a attribute, v variable. Unknown languages come back escaped and plain."""
    rules = TOKEN_RULES.get(lang)
    if not rules:
        return html.escape(code)
    parts = []
    for cls, pattern in rules:
        if pattern is None:
            words = KEYWORDS.get(lang, "").split()
            if not words:
                continue
            pattern = r"\b(?:%s)\b" % "|".join(map(re.escape, words))
        parts.append("(?P<%s>%s)" % (cls, pattern))
    token = re.compile("|".join(parts))
    out, pos = [], 0
    for m in token.finditer(code):
        out.append(html.escape(code[pos:m.start()]))
        out.append('<span class="%s">%s</span>' % (m.lastgroup, html.escape(m.group())))
        pos = m.end()
    out.append(html.escape(code[pos:]))
    return "".join(out)


# ---------------------------------------------------------------- blocks the spec converter does not know

def lift_blocks(text: str):
    """Replace fenced code and raw HTML blocks with placeholder paragraphs. Returns (text, blocks)."""
    lines = text.split("\n")
    out, blocks, i = [], [], 0
    while i < len(lines):
        line = lines[i]
        if line.startswith("```"):
            lang = line[3:].strip()
            j = i + 1
            while j < len(lines) and not lines[j].startswith("```"):
                j += 1
            body = "\n".join(lines[i + 1:j])
            label = '<span class="lang">%s</span>' % html.escape(lang) if lang else ""
            blocks.append('<figure class="code">%s<pre><code>%s</code></pre></figure>' % (label, highlight(body, lang)))
            out.extend(["", "\x01%d\x01" % (len(blocks) - 1), ""])
            i = j + 1
            continue
        m = re.match(r"^<([a-zA-Z][\w-]*)", line)
        if m:
            # A raw HTML block runs to the line that closes its first tag, at column 0. Blank lines inside
            # are allowed, so an inline SVG can be written readably. A one-line element closes on its own line.
            tag, close = m.group(1), "</%s>" % m.group(1)
            j = i
            if not (close in line or line.rstrip().endswith("/>")):
                while j < len(lines) and not lines[j].startswith(close):
                    j += 1
            blocks.append("\n".join(lines[i:j + 1]))
            out.extend(["", "\x01%d\x01" % (len(blocks) - 1), ""])
            i = j + 1
            continue
        out.append(line)
        i += 1
    return "\n".join(out), blocks


def convert(text: str) -> str:
    text, blocks = lift_blocks(text)
    body = spec.convert(text)
    return re.sub(r"<p>\x01(\d+)\x01</p>", lambda m: blocks[int(m.group(1))], body)


# ---------------------------------------------------------------- page

EXTRA_CSS = """
figure.code { position: relative; }
figure.code .lang { position: absolute; top: 6px; right: 10px; font: 0.7rem var(--mono); letter-spacing: 0.08em; text-transform: uppercase; color: var(--muted); }
pre .c { color: var(--muted); font-style: italic; }
pre .s { color: #2f7d32; }
pre .n { color: #a4541c; }
pre .k { color: #7a3fb3; font-weight: 500; }
pre .t { color: var(--accent); }
pre .p { color: #0b5e8a; }
pre .a { color: #8a6d00; }
pre .v { color: #a4541c; }
@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) pre .s { color: #8fd18f; } :root:not([data-theme="light"]) pre .n, :root:not([data-theme="light"]) pre .v { color: #f0a86a; } :root:not([data-theme="light"]) pre .k { color: #c99af5; } :root:not([data-theme="light"]) pre .p { color: #7cc4ea; } :root:not([data-theme="light"]) pre .a { color: #e0c860; } }
:root[data-theme="dark"] pre .s { color: #8fd18f; } :root[data-theme="dark"] pre .n, :root[data-theme="dark"] pre .v { color: #f0a86a; } :root[data-theme="dark"] pre .k { color: #c99af5; } :root[data-theme="dark"] pre .p { color: #7cc4ea; } :root[data-theme="dark"] pre .a { color: #e0c860; }
/* Contract tables: a route or a method name never breaks across lines, so the first column grows to fit it.
   Long lists of code spans still wrap between the spans. The wrapper scrolls sideways when a table is wider than the page. */
.tablewrap code { white-space: nowrap; }
.tablewrap td:first-child { min-width: 10rem; }
figure.diagram { padding: 16px; border: 1px solid var(--line); border-radius: 6px; background: var(--surface); }
figure.diagram svg { display: block; max-width: 100%; height: auto; }
"""

TEMPLATE = (spec.TEMPLATE
            .replace("· Yap spec</title>", "· Yap architecture</title>")
            .replace("</style>", EXTRA_CSS.replace("{", "{{").replace("}", "}}") + "</style>")  # TEMPLATE goes through str.format
            .replace("Yap · product specification", "Yap · architecture")
            .replace('aria-label="Spec pages"', 'aria-label="Architecture pages"')
            .replace("docs/spec/{source}", "docs/architecture/{source}")
            .replace("python3 docs/spec/tools/build.py", "python3 docs/architecture/tools/build.py"))

# Page order on the nav. A page missing here is appended alphabetically.
ORDER = ["sketch.md", "plan.md", "message-path.md", "presence.md", "chat-hub.md", "auth-and-program.md", "sign-in.md", "bootstrap-and-events.md", "client.md", "wire-contract.md"]


def pages():
    found = {p.name: p for p in ROOT.glob("*.md")}
    return [found.pop(n) for n in ORDER if n in found] + [found[n] for n in sorted(found)]


def nav_html(current, all_pages):
    items = []
    for p in all_pages:
        title = spec.page_title(p.read_text(encoding="utf-8"))
        cur = ' aria-current="page"' if p == current else ""
        items.append('<a href="%s"%s>%s</a>' % (html.escape(p.stem + ".html"), cur, html.escape(title)))
    return "".join(items)


def main():
    OUT.mkdir(exist_ok=True)
    all_pages = pages()
    for md in all_pages:
        text = md.read_text(encoding="utf-8")
        page = TEMPLATE.format(title=html.escape(spec.page_title(text)), nav=nav_html(md, all_pages),
                               body=convert(text), source=md.name)
        (OUT / (md.stem + ".html")).write_text(page, encoding="utf-8")
        print("built", (OUT / (md.stem + ".html")).relative_to(ROOT.parent.parent))


if __name__ == "__main__":
    sys.exit(main())
