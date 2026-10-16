# Yap stress-test analysis — 16 October 2026

Open [index.html](index.html) directly in a browser. The report is one standalone
file: all charts, server samples, original CSV exports, styles and scripts are
embedded. It works offline, adapts to mobile screens and has a print/PDF layout.
The findings, tables and initial charts also work without JavaScript.

The report analyzes the nine host CSVs and the client/operator Markdown report
added in evidence commit `16862052552a72379a3de34f2ccd310dc69eb076`.
It distinguishes recomputed host statistics from reported client metrics and
interpretation, and documents missing evidence and source inconsistencies.

## Rebuild

Run from this directory, with Python 3 (standard library only):

```sh
python3 build_report.py
```

- `build_report.py` reads the sibling source CSV directory and Markdown report.
- `report.template.html` contains the report text, layout and interactive explorer.
- `index.html` is the generated, shareable report.
- `analysis.json` contains the raw samples, original CSV text, computed summaries,
  parsed client tables, derived diagnostics and source SHA-256 hashes.

Host aggregates cover entire files, including setup, ramps, cooldown and restarts;
they are not plateau-only measurements. No raw client timelines were committed,
so client percentiles are taken from the Markdown, not recomputed from raw events.
The exact application deployment commit was not supplied.

## Validation

The builder checks the 3,533 samples, contiguous timestamps, non-overlapping
capture intervals, 16 parsed client rows, zero swap usage, two missing process
samples, 164 combined-run samples at ≥95% CPU and the explicitly defined 60-sample
high-CPU/low-output signature.

Browser checks covered all nine capture selections, sample scrubbing, the missing
process row, CSV/JSON downloads, valid chart coordinates, internal navigation,
JavaScript errors, offline requests, desktop and 390/320-pixel layouts, the
JavaScript-disabled fallback, and PDF generation. Application code was unchanged.
