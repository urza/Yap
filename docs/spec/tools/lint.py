#!/usr/bin/env python3
"""Check the spec pages: unique rule IDs with the right prefix, links that resolve, the page
shape, and no implementation words.

The word list exists because the spec was first derived from two implementations. A page that
says "manifest" or "service worker" describes one of them instead of the product. A line that
needs such a word for a good reason carries the comment <!-- lint: allow --> on the same line or
on the line before it.
"""
import pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
README = ROOT / "README.md"

# Pages with the full component shape, or the feature shape (no Anatomy).
COMPONENT_SECTIONS = ["Anatomy", "Rules", "States", "Wording", "Related"]
FEATURE_SECTIONS = ["Rules", "States", "Wording", "Related"]
FREE_FORM = {"README.md", "yap.md", "general-rules.md"}
FEATURE_PAGES = {"presence.md", "notifications.md", "sessions.md", "offline.md"}

BANNED = [
    r"old page", r"pre-rewrite", r"\brewrite\b", r"\bAstra\b", r"\blegacy\b", r"PR #1",
    r"\bBlazor\b", r"\bSignalR\b", r"\bIndexedDB\b", r"\blocalStorage\b", r"service worker",
    r"\bworker\b", r"\bprotocol\b", r"\bmanifest\b", r"\bepoch\b", r"\bdigest\b", r"\bhub\b",
    r"\boutbox\b", r"\bshell\b", r"\bsnapshot\b", r"\breplica\b", r"\bWebSocket\b", r"\bSQLite\b",
    r"\bdatabase\b", r"\bpoll(s|ing)?\b", r"\btimer\b", r"\bDOM\b", r"\bCSS\b", r"\bJavaScript\b",
    r"\bendpoint\b", r"\bAPI\b", r"\bmiddleware\b", r"\bcookie\b", r"\bbootstrap\b",
]
ID = re.compile(r"\*\*([A-Z]{2,3})-(\d{2,3})\*\*")
LINK = re.compile(r"\[[^\]]+\]\(([^)\s]+)\)")


def prefixes():
    table = {}
    for m in re.finditer(r"^\| \[[^\]]+\]\(([^)]+\.md)\) \| ([A-Z]+) \| ", README.read_text(encoding="utf-8"), re.M):
        table[m.group(1)] = m.group(2)
    return table


def main():
    errors, warnings = [], []
    prefix_of = prefixes()
    pages = sorted(ROOT.glob("*.md"))
    texts = {p.name: p.read_text(encoding="utf-8") for p in pages}
    ids = {}
    for name, text in texts.items():
        lines = text.split("\n")
        # Rule IDs
        for n, line in enumerate(lines, 1):
            for m in ID.finditer(line):
                full = "%s-%s" % (m.group(1), m.group(2))
                want = prefix_of.get(name)
                if name != "README.md" and want and m.group(1) != want:
                    errors.append("%s:%d rule %s has prefix %s, page prefix is %s" % (name, n, full, m.group(1), want))
                if full in ids and ids[full] != (name, n):
                    errors.append("%s:%d rule %s already defined at %s:%d" % (name, n, full, *ids[full]))
                ids.setdefault(full, (name, n))
        # Shape
        if name not in FREE_FORM:
            want = FEATURE_SECTIONS if name in FEATURE_PAGES else COMPONENT_SECTIONS
            heads = [h for h in re.findall(r"^## (.+)$", text, re.M)]
            if [h for h in heads if h in want] != want:
                errors.append("%s: sections must be %s in that order, found %s" % (name, want, heads))
            if not re.match(r"^# .+\n\n[^#\n]", text):
                errors.append("%s: the page must start with a title and a purpose paragraph" % name)
        # Banned words. README.md explains the rule and may name the words.
        for n, line in enumerate(lines, 1):
            if name == "README.md":
                break
            if "lint: allow" in line or (n > 1 and "lint: allow" in lines[n - 2]):
                continue
            if line.startswith("```"):
                continue
            for pat in BANNED:
                if re.search(pat, line, re.I if not pat.startswith("\\b[A-Z]") else 0):
                    errors.append("%s:%d implementation word %r: %s" % (name, n, pat, line.strip()[:90]))
    # Links, after all IDs are known
    anchors = {}
    for name, text in texts.items():
        anchors[name] = {re.sub(r"[^a-z0-9]+", "-", h.lower()).strip("-") for h in re.findall(r"^#{1,3} (.+)$", text, re.M)}
        anchors[name] |= {full for full, (page, _) in ids.items() if page == name}
    planned = {t for t, _ in prefix_of.items() if not (ROOT / t).exists()}
    for name, text in texts.items():
        for n, line in enumerate(text.split("\n"), 1):
            for m in LINK.finditer(line):
                target = m.group(1)
                if target.startswith(("http://", "https://")):
                    continue
                path, _, anchor = target.partition("#")
                path = path or name
                if path.startswith("img/"):
                    if not (ROOT / path).exists():
                        warnings.append("%s:%d screenshot not generated yet: %s" % (name, n, path))
                    continue
                if path not in texts:
                    (warnings if path in planned else errors).append("%s:%d link to %s page %s" % (name, n, "planned" if path in planned else "missing", path))
                    continue
                if anchor and anchor not in anchors[path]:
                    errors.append("%s:%d anchor #%s not found in %s" % (name, n, anchor, path))
    for w in warnings:
        print("warning:", w)
    for e in errors:
        print("error:", e)
    print("%d pages, %d rules, %d errors, %d warnings" % (len(pages), len(ids), len(errors), len(warnings)))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
