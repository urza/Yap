"""Builds docs/chat-path.html from the template and the diagram generator.

Run: python3 docs/chat-path-src/build_chatpath.py
Edit the text in chatpath_template.html and the diagrams in chatpath_diagrams.py, never the
generated HTML: the SVG coordinates are computed, so hand edits are lost on the next build.
"""
import pathlib, re, sys
HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import chatpath_diagrams as d
t = (HERE / 'chatpath_template.html').read_text()
fixes = [
 ('sync.js mergeUpdate() :95-99 (revision check) · :108-118 (add message)', 'sync.js mergeUpdate() :95-99 (revision check) · :105-116 (add message)'),
 ('sync.js mergeUpdate() :73-82</span>', 'sync.js mergeUpdate() :72-82</span>'),
 ('<li>The hub limits <code>Report</code>, <code>Typing</code> and <code>SetStatus</code> to 12 calls at once and then 4 calls per second. A call above the limit does nothing and returns no error.</li>',
  '<li>The hub accepts a burst of 12 calls of <code>Report</code>, <code>Typing</code> or <code>SetStatus</code> per connection. After that, it accepts 4 calls per second. A call above the limit does nothing and returns no error.</li>'),
]
for a, b in fixes:
    assert a in t, a[:60]
    t = t.replace(a, b)
for key, fn in d.ALL.items():
    marker = '{{' + key + '}}'
    assert marker in t, key
    t = t.replace(marker, fn())
assert '{{' not in t
head, body = t.split('</style>\n', 1)
full = ('<!doctype html>\n<html lang="en">\n<head>\n<meta charset="utf-8">\n'
        '<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">\n'
        + head + '</style>\n</head>\n<body>\n' + body + '</body>\n</html>\n')
(HERE.parent / 'chat-path.html').write_text(full)
# Optional: a copy without the document skeleton, for publishing as a Claude artifact.
if len(sys.argv) > 1:
    pathlib.Path(sys.argv[1]).write_text(t)
for tag in ['section', 'figure', 'table', 'div', 'svg', 'pre', 'ul', 'ol']:
    o = len(re.findall(r'<%s[\s>]' % tag, full)); c = full.count('</%s>' % tag)
    if o != c: print('MISMATCH', tag, o, c)
print('built', len(full))
