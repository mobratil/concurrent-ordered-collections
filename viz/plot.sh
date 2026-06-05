#!/usr/bin/env bash
# Regenerate the reader/writer 3D visualisations from benchmark data.
#
#   ./viz/plot.sh            # regenerate plots from the existing viz/rw_data.csv
#   ./viz/plot.sh --measure  # re-run ./run-benchmark.sh --rw first, then plot
#
# Interactive HTML needs only Python's stdlib. Static PNGs need matplotlib; this
# script creates a throwaway venv for it if matplotlib isn't already importable.
set -euo pipefail
cd "$(dirname "$0")/.."

CSV=viz/rw_data.csv

if [[ "${1:-}" == "--measure" ]]; then
  echo "Re-measuring reader/writer matrix…"
  ./run-benchmark.sh --rw --csv > /tmp/rw_run.out 2>&1 || true
  { echo "variant,writers,readers,read_mops,write_mops";
    grep -E '^(dotnet-long-long|dotnet-ref-ref|java-cslm-Long-Long),[0-9]' /tmp/rw_run.out; } > "$CSV"
fi

echo "Generating interactive HTML (stdlib only)…"
python3 viz/make_surfaces.py "$CSV" viz/rw_surfaces.html        # 3D surfaces
python3 viz/make_perthread_html.py "$CSV" viz/rw_perthread.html # 2D per-thread

echo "Generating static PNGs (matplotlib)…"
PY=python3
if ! python3 -c "import matplotlib" 2>/dev/null; then
  if [[ ! -x /tmp/vizenv/bin/python ]]; then
    python3 -m venv /tmp/vizenv && /tmp/vizenv/bin/pip install -q matplotlib
  fi
  PY=/tmp/vizenv/bin/python
fi
"$PY" viz/make_png.py "$CSV"          # 3D surface PNGs
"$PY" viz/make_perthread.py "$CSV"    # 2D per-thread PNG

echo "Done. Open the .html files in a browser, or view the .png files."
