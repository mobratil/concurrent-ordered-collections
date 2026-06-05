#!/usr/bin/env python3
"""
Interactive 2D per-thread (scalability) line charts from the --rw matrix.
Stdlib only; pulls plotly.js from a CDN. Produces viz/rw_perthread.html.

  per-reader read efficiency = read_mops / R   (line per writer count W)
  per-writer write efficiency = write_mops / W  (line per reader count R)

Usage: python3 viz/make_perthread_html.py viz/rw_data.csv viz/rw_perthread.html
"""
import csv
import json
import sys

AXIS = [1, 2, 4, 8]
IDX = {v: i for i, v in enumerate(AXIS)}
VARIANTS = [
    ("dotnet-long-long",    ".NET &lt;long,long&gt; (no boxing)"),
    ("dotnet-ref-ref",      ".NET &lt;ref,ref&gt; (boxing-matched)"),
    ("java-cslm-Long-Long", "Java CSLM&lt;Long,Long&gt;"),
]
SERIES_COLOR = {1: "#1f77b4", 2: "#2ca02c", 4: "#ff7f0e", 8: "#d62728"}


def load(path):
    g = {v: {"read": [[None] * 4 for _ in range(4)],
             "write": [[None] * 4 for _ in range(4)]} for v, _ in VARIANTS}
    for r in csv.DictReader(open(path, newline="")):
        v = r["variant"]
        if v not in g:
            continue
        wi, ri = IDX[int(r["writers"])], IDX[int(r["readers"])]
        g[v]["read"][wi][ri] = float(r["read_mops"])
        g[v]["write"][wi][ri] = float(r["write_mops"])
    return g


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "viz/rw_data.csv"
    out = sys.argv[2] if len(sys.argv) > 2 else "viz/rw_perthread.html"
    g = load(path)

    # find shared y maxima
    rmax = wmax = 0.0
    for v, _ in VARIANTS:
        for wi in range(4):
            for ri in range(4):
                rmax = max(rmax, g[v]["read"][wi][ri] / AXIS[ri])
                wmax = max(wmax, g[v]["write"][wi][ri] / AXIS[wi])

    plots = []  # (div_id, traces, layout, title)
    for v, label in VARIANTS:
        # read per reader vs R, one trace per W
        traces = []
        for wi, W in enumerate(AXIS):
            traces.append({
                "type": "scatter", "mode": "lines+markers", "name": f"W={W}",
                "x": AXIS, "y": [g[v]["read"][wi][ri] / AXIS[ri] for ri in range(4)],
                "line": {"color": SERIES_COLOR[W]}, "marker": {"color": SERIES_COLOR[W]},
            })
        plots.append((f"{v}_read", traces, {
            "xaxis": {"title": "Readers (R)", "type": "log", "tickvals": AXIS, "ticktext": [str(a) for a in AXIS]},
            "yaxis": {"title": "READ Mops/s per reader", "rangemode": "tozero", "range": [0, rmax * 1.08]},
            "legend": {"title": {"text": "writers"}},
        }, f"{label} — read per reader thread"))

        # write per writer vs W, one trace per R
        traces = []
        for ri, R in enumerate(AXIS):
            traces.append({
                "type": "scatter", "mode": "lines+markers", "name": f"R={R}",
                "x": AXIS, "y": [g[v]["write"][wi][ri] / AXIS[wi] for wi in range(4)],
                "line": {"color": SERIES_COLOR[R]}, "marker": {"color": SERIES_COLOR[R], "symbol": "square"},
            })
        plots.append((f"{v}_write", traces, {
            "xaxis": {"title": "Writers (W)", "type": "log", "tickvals": AXIS, "ticktext": [str(a) for a in AXIS]},
            "yaxis": {"title": "WRITE Mops/s per writer", "rangemode": "tozero", "range": [0, wmax * 1.08]},
            "legend": {"title": {"text": "readers"}},
        }, f"{label} — write per writer thread"))

    parts = ["""<!doctype html><html><head><meta charset="utf-8">
<title>LockFreeSkipListMap — per-thread scalability</title>
<script src="https://cdn.plot.ly/plotly-2.35.2.min.js" charset="utf-8"></script>
<style>
  body{font-family:-apple-system,Segoe UI,Roboto,sans-serif;margin:24px;color:#222;background:#fafafa}
  h1{font-size:22px} h2{font-size:16px;margin:24px 0 6px} .sub{color:#555;max-width:900px;line-height:1.5}
  .grid{display:grid;grid-template-columns:1fr 1fr 1fr;gap:14px}
  .plot{background:#fff;border:1px solid #e3e3e3;border-radius:8px;padding:6px}
  code{background:#eee;padding:1px 5px;border-radius:4px}
</style></head><body>
<h1>Per-thread throughput (scalability)</h1>
<p class="sub">Aggregate throughput &divide; the number of threads of that role, from
<code>./run-benchmark.sh --rw</code>. <b>A flat line = perfect scaling</b> (each added
thread pulls its weight); a <b>downward slope = contention</b>. Top row: read Mops per
reader thread vs reader count (one line per writer count W). Bottom row: write Mops per
writer thread vs writer count (one line per reader count R).</p>
<h2>Read efficiency (per reader thread)</h2><div class="grid">"""]
    for div_id, _, _, _ in [p for p in plots if p[0].endswith("_read")]:
        parts.append(f'<div class="plot"><div id="{div_id}" style="height:340px"></div></div>')
    parts.append('</div><h2>Write efficiency (per writer thread)</h2><div class="grid">')
    for div_id, _, _, _ in [p for p in plots if p[0].endswith("_write")]:
        parts.append(f'<div class="plot"><div id="{div_id}" style="height:340px"></div></div>')
    parts.append("</div><script>")
    for div_id, traces, layout, title in plots:
        lay = {"title": {"text": title, "font": {"size": 13}},
               "margin": {"l": 55, "r": 10, "t": 34, "b": 44}, **layout}
        parts.append(f"Plotly.newPlot({json.dumps(div_id)}, {json.dumps(traces)}, "
                     f"{json.dumps(lay)}, {{responsive:true}});")
    parts.append("</script></body></html>")

    open(out, "w").write("\n".join(parts))
    print(f"wrote {out}  ({len(plots)} charts)")


if __name__ == "__main__":
    main()
