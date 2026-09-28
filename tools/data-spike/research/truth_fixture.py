"""Record a lidar forest truth set as a test fixture (task 07, D4).

Runs the same canopy height model as forest_truth.py over a resort package's core, then writes, per
10 m cell of the core (north-up rows, west-to-east columns):

    band 0: percent of the cell's 1 m cells with trees at least 3 m tall (0-100; 255 = no lidar)
    band 1: the tallest tree in the cell, in 0.25 m steps (0-255, like the package's canopy layer)

to <package>/forest-truth.bin (Git LFS), plus forest-truth.json describing the grid and source.

    python truth_fixture.py --package TestData/jackson-hole-2km \
        --ept https://s3-us-west-2.amazonaws.com/usgs-lidar-public/WY_NConverse_5_2020/ept.json

Needs the same free packages as forest_truth.py (numpy, laspy[lazrs], pyproj).
"""
import argparse
import json
import os
import sys

import numpy as np

from forest_truth import canopy_height_model

CELL = 10
TREE_METRES = 3.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--package", required=True)
    ap.add_argument("--ept", required=True)
    a = ap.parse_args()

    manifest = json.load(open(os.path.join(a.package, "manifest.json"), encoding="utf-8"))
    core = next(layer for layer in manifest["Layers"] if layer["Id"] == "canopy-core")
    W, N, size = core["West"], core["North"], core["Width"]
    assert core["CellSize"] == 1.0 and core["Height"] == size and size % CELL == 0
    E, S = W + size, N - size

    chm, total = canopy_height_model(a.ept, W, S, E, N)
    n = size // CELL
    blocks = chm.reshape(n, CELL, n, CELL)
    with np.errstate(invalid="ignore"):
        trees = np.nanmean((blocks >= TREE_METRES).astype(np.float32) + np.where(np.isnan(blocks), np.nan, 0), axis=(1, 3))
        tallest = np.nanmax(np.where(np.isnan(blocks), -1, blocks), axis=(1, 3))
    share = np.where(np.isnan(trees), 255, np.round(trees * 100)).astype(np.uint8)
    height = np.clip(np.round(np.maximum(tallest, 0) * 4), 0, 255).astype(np.uint8)

    np.stack([share, height]).tofile(os.path.join(a.package, "forest-truth.bin"))
    info = {
        "description": "Lidar forest truth per 10 m cell of the core: band 0 = % of 1 m cells with trees >= 3 m "
                       "(255 = no lidar), band 1 = tallest tree in 0.25 m steps. Bands are stored one after another.",
        "west": W, "north": N, "cellSize": CELL, "columns": n, "rows": n, "bands": 2,
        "source": a.ept, "points": int(total),
        "script": "tools/data-spike/research/truth_fixture.py",
        "truthForestShare": round(float(np.mean(share[share != 255] >= 10) if (share != 255).any() else 0), 3),
    }
    with open(os.path.join(a.package, "forest-truth.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(info, f, indent=2)
        f.write("\n")
    print(json.dumps(info, indent=2))


if __name__ == "__main__":
    sys.exit(main())
