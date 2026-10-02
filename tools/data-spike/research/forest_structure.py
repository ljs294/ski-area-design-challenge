"""Forest structure against lidar (NE8): height spread and spacing of treetops, lidar vs the game's forest.

The game's forest is checked the way the lidar sees a forest: from above. Both sides go through the same
treetop detector, the lidar's 1 m canopy height model (CHM) and a synthetic CHM drawn from the generated trees
(a cone per conifer, a dome per broadleaf, crown radius as the game spaces them). Treetops are then grouped by
the game's own 10 m cell classes (conifer share and canopy cover, `acquire forest-dump`).

    # 1. A lidar CHM over an Albers window (EPSG:6350), from a 3DEP EPT or from local LAZ/COPC tiles
    python forest_structure.py chm --ept <ept.json URL> --window W S E N --out jh.npy
    python forest_structure.py chm --laz a.laz b.laz ... --window W S E N --out sugarloaf.npy
    # 2. The generated forest (`acquire forest-dump --package <folder> --out <dir>`) against it
    python forest_structure.py measure --chm jh.npy --forest before=<dir> --forest after=<dir> --out jh.json

Needs numpy, scipy, laspy[lazrs] and pyproj (all free). The CHM method matches forest_truth.py: highest
non-noise return per 1 m cell minus the lidar's own ground (2 m mean of ground returns, gaps filled).
"""
import argparse
import json
import os
import sys

import numpy as np
from scipy import ndimage
from scipy.spatial import cKDTree

NOISE = (7, 18)
GROUND = 2
CELL = 10
MIN_TREE = 3.0
CONIFER_WORDS = ("fir", "spruce", "pine", "hemlock", "larch", "cedar", "krummholz", "juniper")


def crown_radius(h):
    """ForestPlacement.CrownRadius: the game's crown radius for a tree this tall."""
    return np.clip(0.1 * h + 0.6, 1.2, 4.5)


# ---------------------------------------------------------------------------------------------- lidar CHM

def chm_from_laz(paths, W, S, E, N):
    import laspy
    from pyproj import Transformer
    size_x, size_y = int(round(E - W)), int(round(N - S))
    top = np.full((size_y, size_x), -np.inf, np.float32)
    gsum = np.zeros((size_y // 2 + 1, size_x // 2 + 1), np.float64)
    gcnt = np.zeros_like(gsum, np.int32)
    total = 0
    for path in paths:
        with laspy.open(path) as f:
            crs = f.header.parse_crs()
            to_alb = Transformer.from_crs(crs.to_2d() if hasattr(crs, "to_2d") else crs, 6350, always_xy=True)
            for pts in f.chunk_iterator(4_000_000):
                cls = np.asarray(pts.classification)
                keep = ~np.isin(cls, NOISE)
                x, y, z, cls = np.asarray(pts.x)[keep], np.asarray(pts.y)[keep], np.asarray(pts.z)[keep], cls[keep]
                ax, ay = to_alb.transform(x, y)
                c = np.floor(ax - W).astype(np.int64)
                r = np.floor(N - ay).astype(np.int64)
                ok = (c >= 0) & (c < size_x) & (r >= 0) & (r < size_y)
                c, r, z, cls = c[ok], r[ok], z[ok], cls[ok]
                total += len(z)
                np.maximum.at(top, (r, c), z.astype(np.float32))
                g = cls == GROUND
                np.add.at(gsum, (r[g] // 2, c[g] // 2), z[g])
                np.add.at(gcnt, (r[g] // 2, c[g] // 2), 1)
        print(f"{os.path.basename(path)}: {total:,} points so far", flush=True)
    return finish_chm(top, gsum, gcnt), total


def finish_chm(top, gsum, gcnt):
    from forest_truth import fill_nearest
    with np.errstate(invalid="ignore", divide="ignore"):
        ground2 = np.where(gcnt > 0, gsum / np.maximum(gcnt, 1), np.nan)
    ground2 = fill_nearest(ground2)
    ground = np.repeat(np.repeat(ground2, 2, 0), 2, 1)[:top.shape[0], :top.shape[1]]
    chm = np.where(np.isfinite(top), top - ground, np.nan)
    return np.clip(chm, 0, 80).astype(np.float32)


def cmd_chm(a):
    W, S, E, N = a.window
    if a.ept:
        from forest_truth import canopy_height_model
        if round(E - W) != round(N - S):
            raise SystemExit("an EPT window must be square (forest_truth.canopy_height_model)")
        chm, total = canopy_height_model(a.ept, W, S, E, N)
        source = a.ept
    else:
        chm, total = chm_from_laz(a.laz, W, S, E, N)
        source = [os.path.basename(p) for p in a.laz]
    np.save(a.out, chm)
    meta = {"west": W, "south": S, "east": E, "north": N, "source": source, "points": int(total),
            "pointsPerM2": round(total / chm.size, 2), "lidarShare": round(float(np.mean(np.isfinite(chm))), 3)}
    json.dump(meta, open(os.path.splitext(a.out)[0] + ".json", "w"), indent=2)
    print(json.dumps(meta, indent=2))


# ------------------------------------------------------------------------------------------ the game's side

def load_forest(folder):
    info = json.load(open(os.path.join(folder, "forest.json"), encoding="utf-8"))
    trees = np.fromfile(os.path.join(folder, "trees.f32"), np.float32).reshape(-1, 6)
    cells = np.fromfile(os.path.join(folder, "cells.u8"), np.uint8).reshape(info["cellsY"], info["cellsX"], 6)
    return info, trees, cells


def synthetic_chm(info, trees, W, S, E, N):
    """The generated trees seen from above: a cone per conifer, a dome per broadleaf, on a 1 m grid."""
    models = info.get("models", [])
    conifer_model = np.array([any(w in m for w in CONIFER_WORDS) for m in models] or [True] * 64)
    x = trees[:, 0] + info["centreX"]
    y = trees[:, 1] + info["centreY"]
    h, width, model = trees[:, 2], trees[:, 3], trees[:, 4].astype(int)
    m = (x >= W - 6) & (x < E + 6) & (y >= S - 6) & (y < N + 6)
    x, y, h, width, model = x[m], y[m], h[m], width[m], model[m]
    R = crown_radius(h) * width
    conifer = conifer_model[np.clip(model, 0, len(conifer_model) - 1)]
    sx, sy = int(round(E - W)), int(round(N - S))
    out = np.zeros((sy, sx), np.float32)
    col, row = x - W, N - y
    reach = int(np.ceil(R.max())) + 1 if len(R) else 0
    for dy in range(-reach, reach + 1):
        for dx in range(-reach, reach + 1):
            c = np.floor(col).astype(int) + dx
            r = np.floor(row).astype(int) + dy
            d = np.hypot(c + 0.5 - col, r + 0.5 - row) / R
            ok = (d <= 1) & (c >= 0) & (c < sx) & (r >= 0) & (r < sy)
            z = np.where(conifer, h - d * 0.6 * h, h - 0.4 * h * (1 - np.sqrt(np.clip(1 - d * d, 0, 1))))
            np.maximum.at(out, (r[ok], c[ok]), z[ok].astype(np.float32))
    return out, (x, y, h)


# ------------------------------------------------------------------------------------------- measurement

def treetops(chm):
    """Variable-window local maxima: a top must be the highest point within 0.8 crown radii (1–4 m)."""
    s = ndimage.gaussian_filter(np.nan_to_num(chm), 0.7)
    radius = np.clip(np.round(0.8 * crown_radius(s) * 2) / 2, 1, 4)
    is_top = np.zeros(s.shape, bool)
    for rad in np.unique(radius[s >= MIN_TREE]):
        k = int(np.ceil(rad))
        yy, xx = np.mgrid[-k:k + 1, -k:k + 1]
        mx = ndimage.maximum_filter(s, footprint=(xx * xx + yy * yy) <= rad * rad, mode="nearest")
        is_top |= (radius == rad) & (s >= mx) & (s >= MIN_TREE)
    r, c = np.nonzero(is_top)
    return c + 0.5, r + 0.5, np.nan_to_num(chm)[r, c]


def classes_for(info, cells, W, N, shape):
    """Per 1 m pixel of the window: the game cell's (conifer, canopy, stand) bytes."""
    rows, cols = np.mgrid[0:shape[0], 0:shape[1]]
    ax, ay = W + cols + 0.5, N - rows - 0.5
    i = np.floor((ax - info["cellWest"]) / CELL).astype(int)
    j = np.floor((ay - info["cellSouth"]) / CELL).astype(int)
    ok = (i >= 0) & (j >= 0) & (i < cells.shape[1]) & (j < cells.shape[0])
    i, j = np.clip(i, 0, cells.shape[1] - 1), np.clip(j, 0, cells.shape[0] - 1)
    kind = np.where(ok, cells[j, i, 0], 0)
    return kind, cells[j, i, 1], cells[j, i, 2], cells[j, i, 3]


CLASSES = {
    "conifer, dense": lambda con, can: (con >= 204) & (can >= 179),
    "mixed conifer, dense": lambda con, can: (con >= 128) & (con < 204) & (can >= 179),
    "mixed broadleaf, dense": lambda con, can: (con >= 51) & (con < 128) & (can >= 179),
    "broadleaf, dense": lambda con, can: (con < 51) & (can >= 179),
    "all forest, open": lambda con, can: (can > 0) & (can < 179),
}


def height_cv(h, cell):
    """The mean, over cells with at least 3 treetops, of the treetop heights' coefficient of variation."""
    n = np.bincount(cell)
    s1, s2 = np.bincount(cell, h), np.bincount(cell, h * h)
    ok = n >= 3
    mean = s1[ok] / n[ok]
    sd = np.sqrt(np.maximum(s2[ok] / n[ok] - mean * mean, 0))
    return round(float(np.mean(sd / mean)), 3)


def measure(chm, mask_px, valid_px):
    """Treetop statistics over the pixels in mask_px (a boolean 1 m mask of whole 10 m cells)."""
    tx, ty, th = treetops(chm)
    ci, cj = (tx // CELL).astype(int), (ty // CELL).astype(int)
    ny, nx = chm.shape[0] // CELL, chm.shape[1] // CELL
    keep = (ci < nx) & (cj < ny)
    tx, ty, th, ci, cj = tx[keep], ty[keep], th[keep], ci[keep], cj[keep]
    filled = np.nan_to_num(chm)[:ny * CELL, :nx * CELL]
    cell_max = filled.reshape(ny, CELL, nx, CELL).max(axis=(1, 3))
    cell_in = mask_px[:ny * CELL, :nx * CELL].reshape(ny, CELL, nx, CELL).all(axis=(1, 3))
    cell_in &= valid_px[:ny * CELL, :nx * CELL].reshape(ny, CELL, nx, CELL).all(axis=(1, 3)) & (cell_max >= MIN_TREE)
    n_cells = int(cell_in.sum())
    if n_cells < 20:
        return {"cells": n_cells}
    inside = cell_in[cj, ci]
    rel = th[inside] / cell_max[cj[inside], ci[inside]]

    tree = cKDTree(np.c_[tx, ty])
    d, _ = tree.query(np.c_[tx[inside], ty[inside]], k=2)
    nn = d[:, 1]
    local = np.array([len(v) for v in tree.query_ball_point(np.c_[tx[inside], ty[inside]], 15.0)]) / (np.pi * 225)
    ce = nn * 2 * np.sqrt(local)

    # Counts in 5 m quadrats of the class's cells: variance/mean is 1 for random, <1 regular, >1 clumped.
    q = np.zeros((ny * 2, nx * 2), np.int32)
    np.add.at(q, ((ty // 5).astype(int), (tx // 5).astype(int)), 1)
    quad_in = np.repeat(np.repeat(cell_in, 2, 0), 2, 1)
    qc = q[quad_in]
    gaps = (filled.reshape(ny, CELL, nx, CELL) < (cell_max[:, None, :, None] / 3)).transpose(0, 2, 1, 3)
    pct = lambda a, p: round(float(np.percentile(a, p)), 2)
    return {
        "cells": n_cells,
        "treetopsPerCell": round(float(inside.sum() / n_cells), 2),
        "relativeHeight": {f"p{p}": pct(rel, p) for p in (5, 10, 25, 50, 75, 90)},
        "relativeHeightShares": {"under50": round(float(np.mean(rel < 0.5)), 3),
                                 "50to80": round(float(np.mean((rel >= 0.5) & (rel < 0.8))), 3),
                                 "80up": round(float(np.mean(rel >= 0.8)), 3)},
        "heightCV": height_cv(th[inside], (cj * nx + ci)[inside]),
        "nearestNeighbour": {f"p{p}": pct(nn, p) for p in (10, 25, 50, 75, 90)},
        "nearestNeighbourCV": round(float(np.std(nn) / np.mean(nn)), 3),
        "clarkEvansR": round(float(np.mean(ce)), 3),
        "quadratVarianceToMean": round(float(qc.var() / max(qc.mean(), 1e-9)), 3),
        "gapShare": round(float(gaps[cell_in].mean()), 3),
    }


def cmd_measure(a):
    chm = np.load(a.chm)
    meta = json.load(open(os.path.splitext(a.chm)[0] + ".json"))
    W, S, E, N = meta["west"], meta["south"], meta["east"], meta["north"]
    valid = np.isfinite(chm)
    result = {"chm": os.path.basename(a.chm), "window": [W, S, E, N], "lidar": {}, "forests": {}}
    forests = [f.split("=", 1) for f in a.forest]
    info0, _, cells0 = load_forest(forests[0][1])
    kind, con, can, stand = classes_for(info0, cells0, W, N, chm.shape)
    core = kind == 1
    for name, rule in CLASSES.items():
        result["lidar"][name] = measure(chm, core & rule(con, can), valid)
    for label, folder in forests:
        info, trees, cells = load_forest(folder)
        syn, (x, y, h) = synthetic_chm(info, trees, W, S, E, N)
        out = {}
        for name, rule in CLASSES.items():
            out[name] = measure(syn, core & rule(con, can), valid)
        # The generated heights themselves (every tree, seen or not), against the cell's tallest generated tree.
        result["forests"][label] = {"detected": out, "trees": int(len(h))}
        print(f"{label}: done", flush=True)
    print(json.dumps(result, indent=1))
    if a.out:
        json.dump(result, open(a.out, "w"), indent=1)


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("chm")
    c.add_argument("--ept")
    c.add_argument("--laz", nargs="+")
    c.add_argument("--window", nargs=4, type=float, required=True, metavar=("W", "S", "E", "N"))
    c.add_argument("--out", required=True)
    m = sub.add_parser("measure")
    m.add_argument("--chm", required=True)
    m.add_argument("--forest", action="append", required=True, help="label=<forest-dump folder>")
    m.add_argument("--out")
    a = ap.parse_args()
    return cmd_chm(a) if a.cmd == "chm" else cmd_measure(a)


if __name__ == "__main__":
    sys.exit(main())
