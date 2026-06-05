# Reader/Writer 3D visualisations

3D surface plots of the `--rw` interference matrix: **W writers + R readers** running
concurrently, height = throughput (Mops/s).

**3D surfaces** (aggregate throughput over the W×R grid):

| file | what | needs |
|------|------|-------|
| `rw_surfaces.html` | **interactive** — 8 rotatable/zoomable surfaces (overlaid comparison + per-impl) | a browser (pulls plotly.js from CDN) |
| `rw_surfaces.png`  | static 2×3 grid (READ/WRITE × the three implementations) | — |
| `rw_read.png` / `rw_write.png` | static, all three implementations overlaid | — |

**2D per-thread scalability** (throughput ÷ thread-count of that role):

| file | what | needs |
|------|------|-------|
| `rw_perthread.html` | **interactive** — 6 line charts (read/write efficiency × impl) | a browser (CDN) |
| `rw_perthread.png`  | static 2×3 grid; flat line = perfect scaling, downslope = contention | — |

| `rw_data.csv`      | the source data (`--rw --csv` output) | — |

## Regenerate

```bash
./viz/plot.sh            # rebuild plots from the existing rw_data.csv
./viz/plot.sh --measure  # re-run the benchmark first, then rebuild
```

- The interactive HTML is produced by `make_surfaces.py` using **only the Python
  standard library**.
- The PNGs are produced by `make_png.py`, which needs **matplotlib**; `plot.sh`
  spins up a throwaway venv (`/tmp/vizenv`) for it automatically if needed.

## Reading the surfaces

- **Readers (R)** axis → rises = reads get faster with more reader threads.
- **Writers (W)** axis → rises = writes get faster with more writer threads.
- Each surface dips along the *other* axis — that's read/write contention.
- READ surfaces climb toward the high-R edge; WRITE surfaces climb toward the high-W
  edge. `.NET <long,long>` sits highest (no boxing → less GC pressure under load).
