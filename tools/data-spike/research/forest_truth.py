"""Score the canopy map and WorldCover against a lidar truth set (data-spike report, D4).

Truth is a 1 m canopy height model from the USGS 3DEP point cloud (public Entwine/EPT on AWS):
the highest non-noise return in each 1 m cell minus the lidar's own ground surface. Both
products are then scored on the spike's 10 m Albers grid, cell for cell.

    python forest_truth.py --grids <dir written by `site --grids`> --ept <ept.json URL> [--out results.json]

Needs: numpy, laspy[lazrs], pyproj (all free). Point clouds are fetched only for the nodes
that overlap the site, at every depth, so a 2 km site reads roughly 20-40 M points.
"""
import argparse
import concurrent.futures as cf
import io
import json
import os
import sys
import urllib.request

import laspy
import numpy as np
from pyproj import Transformer

UA = {"User-Agent": "SkiAreaDesignChallenge-DataSpike/0.1 (+https://github.com/ljs294/ski-area-design-challenge)"}
NOISE = {7, 18}
GROUND = 2


def get(url):
    for attempt in range(3):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=120) as r:
                return r.read()
        except Exception:
            if attempt == 2:
                raise


def ept_nodes(base, cube, box):
    """Every EPT node (any depth) whose cube overlaps box = (minx, miny, maxx, maxy) in EPT coords."""
    def overlaps(key):
        d, x, y, _ = map(int, key.split("-"))
        size = (cube[3] - cube[0]) / 2 ** d
        x0, y0 = cube[0] + x * size, cube[1] + y * size
        return x0 < box[2] and x0 + size > box[0] and y0 < box[3] and y0 + size > box[1]

    found, pending = [], ["0-0-0-0"]
    while pending:
        h = json.loads(get(f"{base}/ept-hierarchy/{pending.pop()}.json"))
        for key, count in h.items():
            if not overlaps(key):
                continue
            if count == -1:
                pending.append(key)
            elif count > 0:
                found.append(key)
    return found


def read_node(base, key):
    las = laspy.read(io.BytesIO(get(f"{base}/ept-data/{key}.laz")))
    return np.asarray(las.x), np.asarray(las.y), np.asarray(las.z), np.asarray(las.classification)


def fill_nearest(a):
    """Fill NaNs by repeated 3x3 mean dilation (ground under dense canopy)."""
    a = a.copy()
    for _ in range(200):
        nan = np.isnan(a)
        if not nan.any():
            break
        p = np.pad(a, 1, constant_values=np.nan)
        stack = np.stack([p[i:i + a.shape[0], j:j + a.shape[1]] for i in range(3) for j in range(3)])
        with np.errstate(all="ignore"):
            m = np.nanmean(stack, axis=0)
        a[nan] = m[nan]
    return a


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--grids", required=True)
    ap.add_argument("--ept", required=True)
    ap.add_argument("--out")
    ap.add_argument("--save-chm", help="optional .npy path for the 1 m canopy height model")
    a = ap.parse_args()

    g = json.load(open(os.path.join(a.grids, "grid.json")))
    n = g["cells"]
    canopy = np.fromfile(os.path.join(a.grids, "canopy10.f32"), np.float32).reshape(n, n)
    cover = np.fromfile(os.path.join(a.grids, "worldcover10.f32"), np.float32).reshape(n, n)
    W, S, E, N = g["west"], g["south"], g["east"], g["north"]

    meta = json.loads(get(a.ept))
    base = a.ept.rsplit("/", 1)[0]
    ept_epsg = meta["srs"].get("horizontal")
    to_ept = Transformer.from_crs(6350, int(ept_epsg), always_xy=True)
    to_alb = Transformer.from_crs(int(ept_epsg), 6350, always_xy=True)
    xs, ys = to_ept.transform([W, W, E, E], [S, N, S, N])
    box = (min(xs), min(ys), max(xs), max(ys))

    keys = ept_nodes(base, meta["bounds"], box)
    print(f"EPT {base.rsplit('/', 1)[1]} (EPSG:{ept_epsg}): {len(keys)} nodes overlap the site", flush=True)

    size = int(round(E - W))
    top = np.full((size, size), -np.inf, np.float32)
    gsum = np.zeros((size // 2, size // 2), np.float64)
    gcnt = np.zeros((size // 2, size // 2), np.int32)
    total = 0
    with cf.ThreadPoolExecutor(8) as pool:
        for x, y, z, cls in pool.map(lambda k: read_node(base, k), keys):
            ax, ay = to_alb.transform(x, y)
            c = np.floor(ax - W).astype(np.int64)
            r = np.floor(N - ay).astype(np.int64)
            ok = (c >= 0) & (c < size) & (r >= 0) & (r < size) & ~np.isin(cls, list(NOISE))
            c, r, z, cls = c[ok], r[ok], z[ok], cls[ok]
            total += len(z)
            np.maximum.at(top, (r, c), z.astype(np.float32))
            gd = cls == GROUND
            np.add.at(gsum, (r[gd] // 2, c[gd] // 2), z[gd])
            np.add.at(gcnt, (r[gd] // 2, c[gd] // 2), 1)
    print(f"{total:,} points in the site ({total / size / size:.1f} per m²)", flush=True)

    with np.errstate(invalid="ignore", divide="ignore"):
        ground2 = np.where(gcnt > 0, gsum / np.maximum(gcnt, 1), np.nan)
    ground2 = fill_nearest(ground2)
    ground = np.repeat(np.repeat(ground2, 2, 0), 2, 1)[:size, :size]
    chm = np.where(np.isfinite(top), top - ground, np.nan)
    chm = np.clip(chm, 0, 80)
    if a.save_chm:
        np.save(a.save_chm, chm.astype(np.float32))

    def cover_frac(threshold):
        t = (chm >= threshold).astype(np.float32)
        t[np.isnan(chm)] = np.nan
        with np.errstate(invalid="ignore"):
            return np.nanmean(t.reshape(n, 10, n, 10), axis=(1, 3))

    result = {"ept": a.ept, "points": total, "pointsPerM2": round(total / size / size, 2), "sizeMetres": size}
    for thr in (2, 3, 5):
        truth = cover_frac(thr)
        valid = ~np.isnan(truth) & ~np.isnan(canopy) & ~np.isnan(cover)
        forest_truth = truth >= 0.10
        meta_forest = canopy >= 0.10
        wc_forest = cover == 10

        def score(pred):
            tp = np.sum(pred & forest_truth & valid)
            fp = np.sum(pred & ~forest_truth & valid)
            fn = np.sum(~pred & forest_truth & valid)
            acc = np.sum((pred == forest_truth) & valid) / valid.sum()
            return {"accuracy": round(float(acc), 3),
                    "precision": round(float(tp / max(1, tp + fp)), 3),
                    "recall": round(float(tp / max(1, tp + fn)), 3),
                    "forestShare": round(float(np.sum(pred & valid) / valid.sum()), 3)}

        m = valid & ~np.isnan(canopy)
        result[f"treesAtLeast{thr}m"] = {
            "truthForestShare": round(float(np.sum(forest_truth & valid) / valid.sum()), 3),
            "truthCanopyCover": round(float(np.nanmean(truth[valid])), 3),
            "metaCanopy": score(meta_forest),
            "worldCover": score(wc_forest),
            "metaCoverMeanAbsError": round(float(np.mean(np.abs(canopy[m] - truth[m]))), 3),
            "metaCoverBias": round(float(np.mean(canopy[m] - truth[m])), 3),
        }
    h = chm[chm >= 3]
    result["truthTreeHeight"] = {"p50": round(float(np.percentile(h, 50)), 1), "p90": round(float(np.percentile(h, 90)), 1)} if h.size else {}
    print(json.dumps(result, indent=2))
    if a.out:
        with open(a.out, "w") as f:
            json.dump(result, f, indent=2)


if __name__ == "__main__":
    sys.exit(main())
