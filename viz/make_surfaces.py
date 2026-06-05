#!/usr/bin/env python3
"""
Generate an interactive 3D surface visualisation of the reader/writer matrix
(`run-benchmark.sh --rw --csv` output) as a single self-contained HTML file.

No third-party Python packages required — it emits HTML that pulls plotly.js from
a CDN, so the surfaces are rotatable/zoomable in any browser.

Usage:
    python3 viz/make_surfaces.py viz/rw_data.csv viz/rw_surfaces.html
"""
import csv
import json
import sys

# Axis order (log2 thread counts). Cells are indexed by position so the surface
# is evenly spaced; tick labels restore the real 1/2/4/8 values.
AXIS = [1, 2, 4, 8]
IDX = {v: i for i, v in enumerate(AXIS)}

# One distinct hue per variant so overlaid surfaces stay readable.
VARIANT_STYLE = {
    "dotnet-long-long":      ("#1f77b4", "Blues",  ".NET <long,long> (no boxing)"),
    "dotnet-ref-ref":        ("#2ca02c", "Greens", ".NET <ref,ref> (boxing-matched)"),
    "java-cslm-Long-Long":   ("#d62728", "Reds",   "Java ConcurrentSkipListMap<Long,Long>"),
}
VARIANT_ORDER = ["dotnet-long-long", "dotnet-ref-ref", "java-cslm-Long-Long"]


def load(csv_path):
    """grids[variant][metric] = 4x4 z matrix, z[writer_idx][reader_idx]."""
    grids = {v: {"read": [[None] * 4 for _ in range(4)],
                 "write": [[None] * 4 for _ in range(4)]} for v in VARIANT_ORDER}
    zmax = 0.0
    with open(csv_path, newline="") as f:
        for row in csv.DictReader(f):
            v = row["variant"]
            if v not in grids:
                continue
            wi, ri = IDX[int(row["writers"])], IDX[int(row["readers"])]
            rd, wr = float(row["read_mops"]), float(row["write_mops"])
            grids[v]["read"][wi][ri] = rd
            grids[v]["write"][wi][ri] = wr
            zmax = max(zmax, rd, wr)
    return grids, zmax


def surface_trace(z, colorscale, name, scene, showscale=False, opacity=1.0):
    return {
        "type": "surface", "z": z,
        "x": [0, 1, 2, 3], "y": [0, 1, 2, 3],
        "colorscale": colorscale, "showscale": showscale,
        "opacity": opacity, "name": name, "scene": scene,
        "contours": {"z": {"show": True, "usecolormap": True,
                            "highlightcolor": "#ffffff", "project": {"z": True}}},
        "hovertemplate": "W=%{customdata[0]}  R=%{customdata[1]}<br>%{z:.2f} Mops/s<extra>" + name + "</extra>",
        "customdata": [[[AXIS[wi], AXIS[ri]] for ri in range(4)] for wi in range(4)],
    }


def axis_layout(title):
    return {
        "xaxis": {"title": "Readers (R)", "tickvals": [0, 1, 2, 3], "ticktext": ["1", "2", "4", "8"]},
        "yaxis": {"title": "Writers (W)", "tickvals": [0, 1, 2, 3], "ticktext": ["1", "2", "4", "8"]},
        "zaxis": {"title": "Mops/s"},
        "camera": {"eye": {"x": 1.7, "y": -1.7, "z": 1.0}},
        "aspectmode": "cube",
        "annotations": [],
    }


def main():
    csv_path = sys.argv[1] if len(sys.argv) > 1 else "viz/rw_data.csv"
    out_path = sys.argv[2] if len(sys.argv) > 2 else "viz/rw_surfaces.html"
    grids, zmax = load(csv_path)
    zmax = (int(zmax / 2) + 1) * 2  # round up to even

    plots = []  # (div_id, traces, scenes_layout)

    # --- Overlaid comparison: one plot for READ, one for WRITE ---
    for metric in ("read", "write"):
        traces = []
        for v in VARIANT_ORDER:
            color, scale, label = VARIANT_STYLE[v]
            traces.append(surface_trace(grids[v][metric], scale, label, "scene", opacity=0.80))
        plots.append((f"cmp_{metric}", traces,
                      {"scene": {**axis_layout(metric), "zaxis": {"title": "Mops/s", "range": [0, zmax]}}},
                      f"{metric.upper()} throughput — all three implementations overlaid"))

    # --- Per-config individual surfaces ---
    for v in VARIANT_ORDER:
        color, scale, label = VARIANT_STYLE[v]
        for metric in ("read", "write"):
            traces = [surface_trace(grids[v][metric], scale, label, "scene", showscale=True)]
            plots.append((f"{v}_{metric}", traces,
                          {"scene": {**axis_layout(metric), "zaxis": {"title": "Mops/s", "range": [0, zmax]}}},
                          f"{label} — {metric.upper()} throughput"))

    # --- Emit HTML ---
    parts = ["""<!doctype html><html><head><meta charset="utf-8">
<title>LockFreeSkipListMap — reader/writer 3D surfaces</title>
<script src="https://cdn.plot.ly/plotly-2.35.2.min.js" charset="utf-8"></script>
<style>
  body{font-family:-apple-system,Segoe UI,Roboto,sans-serif;margin:24px;color:#222;background:#fafafa}
  h1{font-size:22px} h2{font-size:17px;margin:28px 0 6px}
  .sub{color:#555;max-width:900px;line-height:1.5}
  .grid{display:grid;grid-template-columns:1fr 1fr;gap:14px}
  .plot{background:#fff;border:1px solid #e3e3e3;border-radius:8px;padding:6px}
  .legend span{display:inline-block;margin-right:18px;font-weight:600}
  .swatch{display:inline-block;width:12px;height:12px;border-radius:2px;margin-right:6px;vertical-align:middle}
  code{background:#eee;padding:1px 5px;border-radius:4px}
</style></head><body>
<h1>LockFreeSkipListMap — reader/writer interference (3D)</h1>
<p class="sub">Each surface is the W&times;R matrix from <code>./run-benchmark.sh --rw</code>:
<b>Writers (W)</b> and <b>Readers (R)</b> threads run concurrently; height is throughput in
<b>millions of ops/sec</b>. Drag to rotate, scroll to zoom. Read surfaces rise toward more
readers; write surfaces rise toward more writers; each role dips as the other grows
(contention).</p>
<div class="legend">"""]
    for v in VARIANT_ORDER:
        color, _, label = VARIANT_STYLE[v]
        parts.append(f'<span><span class="swatch" style="background:{color}"></span>{label}</span>')
    parts.append("</div>")

    parts.append('<h2>Comparison — all implementations overlaid</h2><div class="grid">')
    for div_id, _, _, _ in plots[:2]:
        parts.append(f'<div class="plot"><div id="{div_id}" style="height:460px"></div></div>')
    parts.append("</div>")

    parts.append('<h2>Per-implementation surfaces</h2><div class="grid">')
    for div_id, _, _, _ in plots[2:]:
        parts.append(f'<div class="plot"><div id="{div_id}" style="height:380px"></div></div>')
    parts.append("</div>")

    parts.append("<script>")
    for div_id, traces, layout, title in plots:
        lay = {"title": {"text": title, "font": {"size": 14}},
               "margin": {"l": 0, "r": 0, "t": 34, "b": 0},
               **layout}
        parts.append(f"Plotly.newPlot({json.dumps(div_id)}, {json.dumps(traces)}, "
                     f"{json.dumps(lay)}, {{responsive:true}});")
    parts.append("</script></body></html>")

    with open(out_path, "w") as f:
        f.write("\n".join(parts))
    print(f"wrote {out_path}  (zmax={zmax} Mops/s, {len(plots)} surfaces)")


if __name__ == "__main__":
    main()
