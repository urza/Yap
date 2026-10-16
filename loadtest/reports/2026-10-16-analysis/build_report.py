#!/usr/bin/env python3
"""Rebuild the standalone report from the committed CSVs and Markdown. Stdlib only."""
import csv
import hashlib
import html
import json
import math
from pathlib import Path
import statistics

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
COMMIT = '16862052552a72379a3de34f2ccd310dc69eb076'
BASE = f'https://github.com/urza/Yap/blob/{COMMIT}/'
MD = ROOT / '2026-10-16-hz-series.md'
source = MD.read_text(encoding='utf-8')
FILES = [
    ('host.csv', '02 · Account setup', 'on'),
    ('host-idle-db.csv', '02 · Idle crowd', 'on'),
    ('host-test-3.csv', '03 · Fine steps', 'on'),
    ('host-test-5.csv', '05 · Small rooms', 'on'),
    ('host-test-4-big-room.csv', '06 · Big room', 'on'),
    ('host-test-7-media-heavy.csv', '07 · Media heavy', 'on'),
    ('host-test-dm-heavy.csv', '08 · DM heavy', 'on'),
    ('host-test-burst.csv', '09 · Burst', 'on'),
    ('host-tests-all-combined.csv', '10 · Combined', 'off'),
]
datasets = []
manifest = []
for filename, label, persistence in FILES:
    path = ROOT / '2026-10-16-hz-host-csv' / filename
    with path.open(encoding='utf-8-sig', newline='') as f:
        reader = csv.DictReader(f)
        columns = reader.fieldnames
        raw = list(reader)
    rows = [[r[k] if k == 'time' else (float(r[k]) if r[k] else None) for k in columns] for r in raw]
    seconds = [sum(int(v) * factor for v, factor in zip(r[0].split(':'), (3600, 60, 1))) for r in rows]
    assert all(b-a == 1 for a, b in zip(seconds, seconds[1:])), filename
    assert all(len(r) == len(columns) for r in rows)
    stats = {}
    for i, k in enumerate(columns[1:], 1):
        v = [r[i] for r in rows if r[i] is not None]
        stats[k] = dict(min=min(v), max=max(v), mean=statistics.mean(v), missing=len(rows)-len(v))
    cpu_index = columns.index('cpu_pct')
    saturation = sum(r[cpu_index] >= 95 for r in rows)
    datasets.append(dict(file=filename, label=label, persistence=persistence, columns=columns, rows=rows, raw_csv=path.read_bytes().decode('utf-8'),
                         stats=stats, saturated_samples=saturation))
    manifest.append(dict(file=str(path.relative_to(ROOT)), sha256=hashlib.sha256(path.read_bytes()).hexdigest(), rows=len(rows)))

def table_after(marker, header='| Step'):
    text = source[source.index(marker)+len(marker):]
    lines = text.splitlines()
    start = next(i for i, line in enumerate(lines) if line.startswith(header))
    result = []
    for line in lines[start+2:]:
        if not line.startswith('|'): break
        result.append([x.strip() for x in line.strip('|').split('|')])
    return result

client = []
for run, name, marker in [
    (1, 'Idle crowd · DB off', 'Run 1, persistence off (`results/'),
    (2, 'Idle crowd · DB on', 'Run 2, persistence on (`results/'),
    (5, 'Small rooms', 'Summary of the whole run:'),
    (6, 'Big room', 'Final summary of the run:'),
    (7, 'Media heavy', '## Run 7:'),
    (8, 'DM heavy', '## Run 8:'),
    (9, 'Burst', '## Run 9:'),
]:
    for cells in table_after(marker):
        if len(cells) != 13 or ' / ' not in cells[3]: continue
        def number(s): return float(s.replace(',', ''))
        client.append(dict(run=run, name=name, users=int(cells[0].split()[0]), seconds=cells[1],
                           skipped='skipped' in cells[1], rate=number(cells[2]),
                           latency=[number(x) for x in cells[3].split('/')],
                           fan=[number(x) for x in cells[4].split('/')], http=number(cells[5]),
                           rtt=number(cells[6]), errors=int(cells[7].split()[0]),
                           cpu=[number(x) for x in cells[8].split('/')], rss=number(cells[9]),
                           pool=number(cells[10]), depth=number(cells[11]), sweep=number(cells[12])))

assert len(client) == 16, len(client)
assert sum(len(d['rows']) for d in datasets) == 3533
assert datasets[-1]['saturated_samples'] == 164

def fmt(n, decimals=0):
    return f'{n:,.{decimals}f}'

def svg_line(series, labels, colors, title, ymax=None, unit='', width=600, height=210):
    left, right, top, bottom = 54, 12, 12, 30
    w, h = width-left-right, height-top-bottom
    vmax = ymax or max(v for _, vs in series for v in vs if v is not None) * 1.08 or 1
    parts = [f'<svg viewBox="0 0 {width} {height}" role="img" aria-label="{html.escape(title)}" xmlns="http://www.w3.org/2000/svg"><title>{html.escape(title)}</title>']
    for i in range(5):
        value = vmax*i/4
        y = top+h-h*i/4
        tick = f'{value:,.1f}' if vmax < 10 else f'{value:,.0f}'
        parts.append(f'<path d="M{left},{y:.1f}H{width-right}" stroke="#dfe5df" stroke-width="1"/><text x="{left-9}" y="{y+4:.1f}" text-anchor="end" fill="#63736b" font-size="11">{tick}</text>')
    for j, label in enumerate(labels):
        x = left + w*j/(len(labels)-1)
        anchor = 'start' if j == 0 else ('end' if j == len(labels)-1 else 'middle')
        parts.append(f'<text x="{x:.1f}" y="{height-8}" text-anchor="{anchor}" fill="#63736b" font-size="11">{html.escape(label)}</text>')
    for (name, vs), color in zip(series, colors):
        commands = []
        previous = False
        for i, v in enumerate(vs):
            if v is None:
                previous = False
                continue
            x, y = left+w*i/max(1,len(vs)-1), top+h-h*v/vmax
            commands.append(f'{"L" if previous else "M"}{x:.2f},{y:.2f}')
            previous = True
        parts.append(f'<path d="{" ".join(commands)}" stroke="{color}" stroke-width="1.8" fill="none" stroke-linejoin="round"><title>{html.escape(name)}</title></path>')
    return ''.join(parts) + '</svg>'

combined = datasets[-1]
def chart_for(d, keys, colors, title, ymax=None):
    index = d['columns'].index
    series = []
    for k in keys:
        values = [r[index(k)] for r in d['rows']]
        if k == 'proc_cpu_pct':
            values = [None if v is None or i == 0 or d['rows'][i][index('proc_pid')] != d['rows'][i-1][index('proc_pid')] else v/4 for i,v in enumerate(values)]
        series.append((k, values))
    labels = [d['rows'][round((len(d['rows'])-1)*i/4)][0] for i in range(5)]
    return svg_line(series, labels, colors, title, ymax)

def client_rows():
    out=[]
    for c in client:
        state = 'Skipped ramp' if c['skipped'] else ('Backlog' if c['depth']>100000 else ('DB off' if c['run']==1 else 'Kept up*'))
        cls = 'muted' if c['skipped'] else ('bad' if c['depth']>100000 else 'good')
        out.append(f'<tr class="{"skipped" if c["skipped"] else ""}"><th scope="row"><small>R{c["run"]:02d}</small> {c["name"]}</th><td>{c["users"]:,}</td><td>{c["seconds"]}</td><td>{c["rate"]:.1f}</td><td>{fmt(c["latency"][1])}</td><td>{fmt(c["http"])}</td><td>{fmt(c["cpu"][0])}%</td><td>{fmt(c["depth"])}</td><td>{c["errors"]}</td><td><span class="tag {cls}">{state}</span></td></tr>')
    return ''.join(out)

def bars():
    selected=[next(c for c in client if c['run']==r and c['users']==u) for r,u in [(7,300),(2,1000),(5,1500),(6,800),(8,800),(9,500)]]
    out=[]
    for c in selected:
        out.append(f'<div class="bar-row"><div><strong>{c["name"]}</strong><small>{c["users"]:,} users · {c["rate"]:.1f} msg/s</small></div><div class="bar-cell"><b>{fmt(c["latency"][1])} ms</b><i style="width:{c["latency"][1]/5}%"></i></div><div class="bar-cell backlog"><b>{fmt(c["depth"])} writes</b><i style="width:{max(.15,c["depth"]/4470373*100)}%"></i></div></div>')
    return ''.join(out)

def host_rows():
    out=[]
    for d in datasets:
        s=d['stats']
        link=BASE+'loadtest/reports/2026-10-16-hz-host-csv/'+d['file']
        out.append(f'<tr><th scope="row"><a href="{link}">{d["label"]}</a><small>{d["rows"][0][0]}–{d["rows"][-1][0]} UTC · {len(d["rows"])} samples</small></th><td>{s["cpu_pct"]["mean"]:.1f}%</td><td>{s["cpu_pct"]["max"]:.1f}%</td><td>{d["saturated_samples"]}</td><td>{s["proc_cpu_pct"]["max"]/100:.2f}</td><td>{fmt(s["proc_rss_mb"]["max"])}</td><td>{s["net_tx_mb_s"]["max"]:.2f}</td><td>{s["iowait_pct"]["max"]:.1f}%</td><td>{fmt(s["tcp_established"]["max"])}</td></tr>')
    return ''.join(out)

checkpoints = table_after('### Timeline of run 2 at 1000 users', '| Time')
depth = [int(c[4].replace(',', '')) for c in checkpoints]
memory = [int(c[5]) for c in checkpoints]
times = [c[0].split()[0] for c in checkpoints]
# The source timeline is sparse and uneven. Interpolate coordinates by timestamp,
# never invent one-second observations or compute statistics from interpolated data.
def sparse_svg(values, title, color):
    seconds=[sum(int(v)*f for v,f in zip(t.split(':'),(3600,60,1))) for t in times]
    slots=[None]*(seconds[-1]-seconds[0]+1)
    for s,v in zip(seconds,values): slots[s-seconds[0]]=v
    # Draw straight segments between the six reported checkpoints on a true time axis.
    svg=svg_line([('checkpoints',slots)], ['17:25:28','17:26:29','17:27:29','17:28:30','17:29:30'],[color],title,width=600,height=180)
    import re
    matches=re.findall(r'M([0-9.]+),([0-9.]+)',svg.split('stroke="'+color+'"')[0].split('<path d="')[-1])
    path=' '.join(('M' if i==0 else 'L')+x+','+y for i,(x,y) in enumerate(matches))
    svg=re.sub(r'<path d="[ML0-9., ]+" stroke="'+color+'"', '<path d="'+path+'" stroke="'+color+'"',svg)
    circles=''.join(f'<circle cx="{x}" cy="{y}" r="3" fill="{color}"/>' for x,y in matches)
    return svg.replace('</svg>',circles+'</svg>')

col = combined['columns'].index
low_output = sum(r[col('cpu_pct')] >= 95 and r[col('net_tx_mb_s')] <= .05 and r[col('tcp_established')] > 3000 for r in combined['rows'])
assert low_output == 60
assert all(d['stats']['swap_used_mb']['max'] == 0 for d in datasets)
assert sum(d['stats']['proc_rss_mb']['missing'] for d in datasets) == 2
assert all(datasets[i]['rows'][-1][0] < datasets[i+1]['rows'][0][0] for i in range(len(datasets)-1))
data=dict(commit=COMMIT, date='2026-10-16', datasets=datasets, client=client, sources=manifest,
          derived=dict(combined_low_output_samples=low_output, low_output_criterion='cpu_pct >= 95 AND net_tx_mb_s <= 0.05 AND tcp_established > 3000',
                       run2_checkpoint_times=times, run2_checkpoint_depth=depth, run2_checkpoint_working_set_mib=memory),
          methodology='Host means cover entire files, including setup, ramps, cooldown and restarts. One-second sampling; MiB units. Client tables transcribed by parsing committed Markdown; no raw client timelines available.')
(HERE/'analysis.json').write_text(json.dumps(data,indent=2),encoding='utf-8')
tokens={
    'DATA':json.dumps(data,separators=(',',':')).replace('</','<\\/'),
    'CLIENT_ROWS':client_rows(), 'HOST_ROWS':host_rows(), 'BARS':bars(),
    'OPTIONS':''.join(f'<option value="{i}" {"selected" if i==8 else ""}>{d["label"]}</option>' for i,d in enumerate(datasets)),
    'CPU_CHART':chart_for(combined,['cpu_pct','proc_cpu_pct'],['#bd542f','#1c6b58'],'Combined test CPU, percent of four-core host',100),
    'TX_CHART':chart_for(combined,['net_tx_mb_s'],['#317a9b'],'Combined test outbound network, MiB/s'),
    'RSS_CHART':chart_for(combined,['proc_rss_mb'],['#8c6899'],'Combined test Yap resident memory, MiB'),
    'TCP_CHART':chart_for(combined,['tcp_established'],['#a57528'],'Combined test established TCP sockets'),
    'DEPTH_CHART':sparse_svg([v/1e6 for v in depth],'Run 2 outstanding writes in millions, six reported checkpoints','#bd542f'),
    'MEMORY_CHART':sparse_svg(memory,'Run 2 working set in MiB, six reported checkpoints','#8c6899'),
    'SOURCE_MD':html.escape(source), 'BASE':BASE,
    'SOURCE_SHA':hashlib.sha256(MD.read_bytes()).hexdigest(),
    'MIN_MEMORY':f'{min(d["stats"]["mem_available_mb"]["min"] for d in datasets)/1024:.2f}',
}
template=(HERE/'report.template.html').read_text(encoding='utf-8')
for key,value in tokens.items(): template=template.replace('@@'+key+'@@',value)
assert '@@' not in template
(HERE/'index.html').write_text(template,encoding='utf-8')
print(f'Built index.html: {len(template.encode()):,} bytes; {len(client)} client rows; 3,533 host samples.')

