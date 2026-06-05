#!/usr/bin/env python3
"""
2D scalability charts: throughput *per thread* of each role, from the --rw matrix.

  per-reader read efficiency = read_mops  / R   (how much each reader thread gets)
  per-writer write efficiency = write_mops / W   (how much each writer thread gets)

A flat line == perfect scaling (each added thread pulls its weight); a downward
slope == contention (added threads step on each other / the other role).

Outputs:
  viz/rw_perthread.png   2x3 grid (rows: read-per-reader, write-per-writer; cols: impl)

Usage: python viz/make_perthread.py viz/rw_data.csv
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
    ("dotnet-long-long",    ".NET <long,long> (no boxing)"),
    ("dotnet-ref-ref",      ".NET <ref,ref> (boxing-matched)"),
    ("java-cslm-Long-Long", "Java CSLM<Long,Long>"),
]
# colour per series (the "other role" thread count)
SERIES_COLOR = {1: "#1f77b4", 2: "#2ca02c", 4: "#ff7f0e", 8: "#d62728"}


def load(path):
    g = {v: {"read": np.full((4, 4), np.nan), "write": np.full((4, 4), np.nan)}
         for v, _ in VARIANTS}
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            v = r["variant"]
            if v not in g:
                continue
            wi, ri = IDX[int(r["writers"])], IDX[int(r["readers"])]
            g[v]["read"][wi, ri] = float(r["read_mops"])
            g[v]["write"][wi, ri] = float(r["write_mops"])
    return g


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "viz/rw_data.csv"
    g = load(path)

    fig, axes = plt.subplots(2, 3, figsize=(15, 8.5), sharex="row")
    fig.suptitle("Per-thread throughput (scalability) — flat = perfect scaling, "
                 "downward = contention", fontsize=14)

    # peak per-thread for shared y-limits per row
    read_eff = {v: g[v]["read"] / np.array(AXIS)[None, :] for v, _ in VARIANTS}     # / R (cols)
    write_eff = {v: g[v]["write"] / np.array(AXIS)[:, None] for v, _ in VARIANTS}   # / W (rows)
    rmax = max(np.nanmax(e) for e in read_eff.values())
    wmax = max(np.nanmax(e) for e in write_eff.values())

    for col, (v, label) in enumerate(VARIANTS):
        # ---- Row 0: read per reader vs R, one line per W ----
        ax = axes[0][col]
        eff = read_eff[v]                       # eff[wi, ri]
        for wi, W in enumerate(AXIS):
            ax.plot(range(4), eff[wi, :], marker="o", color=SERIES_COLOR[W],
                    label=f"W={W}")
        ax.set_title(label, fontsize=11)
        ax.set_xticks(range(4)); ax.set_xticklabels(AXIS)
        ax.set_xlabel("Readers (R)")
        if col == 0:
            ax.set_ylabel("READ Mops/s per reader thread")
        ax.set_ylim(0, rmax * 1.08)
        ax.grid(True, alpha=0.3)
        if col == 0:
            ax.legend(title="writers", fontsize=8)

        # ---- Row 1: write per writer vs W, one line per R ----
        ax = axes[1][col]
        eff = write_eff[v]                      # eff[wi, ri]
        for ri, R in enumerate(AXIS):
            ax.plot(range(4), eff[:, ri], marker="s", color=SERIES_COLOR[R],
                    label=f"R={R}")
        ax.set_xticks(range(4)); ax.set_xticklabels(AXIS)
        ax.set_xlabel("Writers (W)")
        if col == 0:
            ax.set_ylabel("WRITE Mops/s per writer thread")
        ax.set_ylim(0, wmax * 1.08)
        ax.grid(True, alpha=0.3)
        if col == 0:
            ax.legend(title="readers", fontsize=8)

    fig.tight_layout(rect=(0, 0, 1, 0.96))
    fig.savefig("viz/rw_perthread.png", dpi=110)
    plt.close(fig)
    print("wrote viz/rw_perthread.png")


if __name__ == "__main__":
    main()
