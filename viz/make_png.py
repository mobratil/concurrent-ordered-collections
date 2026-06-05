#!/usr/bin/env python3
"""
Render the reader/writer matrix (`--rw --csv` output) as static 3D surface PNGs
with matplotlib. Produces:
  viz/rw_surfaces.png   2x3 grid (rows: READ/WRITE, cols: the three implementations)
  viz/rw_read.png       READ throughput, three surfaces overlaid
  viz/rw_write.png      WRITE throughput, three surfaces overlaid

Usage: python viz/make_png.py viz/rw_data.csv
"""
import csv
import sys

import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

AXIS = [1, 2, 4, 8]
IDX = {v: i for i, v in enumerate(AXIS)}
VARIANTS = [
    ("dotnet-long-long",    "Blues",   ".NET <long,long>\n(no boxing)"),
    ("dotnet-ref-ref",      "Greens",  ".NET <ref,ref>\n(boxing-matched)"),
    ("java-cslm-Long-Long", "Reds",    "Java CSLM<Long,Long>"),
]


def load(path):
    grids = {v: {"read": np.full((4, 4), np.nan), "write": np.full((4, 4), np.nan)}
             for v, _, _ in VARIANTS}
    zmax = 0.0
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            v = r["variant"]
            if v not in grids:
                continue
            wi, ri = IDX[int(r["writers"])], IDX[int(r["readers"])]
            rd, wr = float(r["read_mops"]), float(r["write_mops"])
            grids[v]["read"][wi, ri] = rd
            grids[v]["write"][wi, ri] = wr
            zmax = max(zmax, rd, wr)
    return grids, (int(zmax / 2) + 1) * 2


def setup_axes(ax, title, zmax):
    ax.set_xticks(range(4)); ax.set_xticklabels(AXIS)
    ax.set_yticks(range(4)); ax.set_yticklabels(AXIS)
    ax.set_xlabel("Readers (R)"); ax.set_ylabel("Writers (W)")
    ax.set_zlabel("Mops/s"); ax.set_zlim(0, zmax)
    ax.set_title(title, fontsize=10)
    ax.view_init(elev=22, azim=-130)


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "viz/rw_data.csv"
    grids, zmax = load(path)
    X, Y = np.meshgrid(range(4), range(4))  # X=readers idx, Y=writers idx

    # ---- 2x3 grid of individual surfaces ----
    fig = plt.figure(figsize=(15, 9))
    fig.suptitle("Reader/Writer interference — throughput surfaces "
                 "(W writers + R readers running concurrently)", fontsize=14)
    for col, (v, cmap, label) in enumerate(VARIANTS):
        for row, metric in enumerate(("read", "write")):
            ax = fig.add_subplot(2, 3, row * 3 + col + 1, projection="3d")
            Z = grids[v][metric]
            ax.plot_surface(X, Y, Z, cmap=cmap, edgecolor="0.3",
                            linewidth=0.3, antialiased=True, vmin=0, vmax=zmax)
            setup_axes(ax, f"{label}\n{metric.upper()} throughput", zmax)
    fig.tight_layout(rect=(0, 0, 1, 0.96))
    fig.savefig("viz/rw_surfaces.png", dpi=110)
    plt.close(fig)

    # ---- overlaid comparison, one figure per metric ----
    for metric in ("read", "write"):
        fig = plt.figure(figsize=(9, 7))
        ax = fig.add_subplot(111, projection="3d")
        for v, cmap, label in VARIANTS:
            Z = grids[v][metric]
            ax.plot_surface(X, Y, Z, cmap=cmap, alpha=0.75, edgecolor="0.4",
                            linewidth=0.3, vmin=0, vmax=zmax)
        setup_axes(ax, f"{metric.upper()} throughput — all three overlaid", zmax)
        # manual legend (surfaces don't auto-legend)
        from matplotlib.patches import Patch
        handles = [Patch(facecolor=matplotlib.colormaps[c](0.7), label=l.replace("\n", " "))
                   for _, c, l in VARIANTS]
        ax.legend(handles=handles, loc="upper left", fontsize=8)
        fig.tight_layout(rect=(0.02, 0.02, 0.98, 0.98))
        fig.savefig(f"viz/rw_{metric}.png", dpi=120)
        plt.close(fig)

    print(f"wrote viz/rw_surfaces.png, viz/rw_read.png, viz/rw_write.png (zmax={zmax})")


if __name__ == "__main__":
    main()
