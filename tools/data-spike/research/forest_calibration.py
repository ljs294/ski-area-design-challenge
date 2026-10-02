"""Per-mountain forest calibration from sampled lidar patches: is a small sample enough?

The game corrects the canopy map with two factors, measured once at Jackson Hole (D4):
- density: lidar tree cover ÷ the canopy map's tree cover, over every 10 m cell;
- dominant height: lidar tallest tree ÷ the canopy map's tallest, per 10 m forest cell (median).

This measures both factors from N square patches of USGS 3DEP lidar, spread over the site's core, and
checks them against a second, disjoint set of patches. The canopy map comes from `acquire forest-dump`
(canopy.u8). Patches are spread in a stratified grid, one per block, at a forested spot.

    python forest_calibration.py --forest <dump dir> --ept <ept.json URL> [--patches 20] [--size 200] [--depth-step N]
    python forest_calibration.py --forest <dump dir> --copc [--patches 20] [--size 200] [--resolution R]

`--depth-step` (EPT) / `--resolution` (COPC) read only the coarser levels of the point cloud, which is how
the downloader would keep dense surveys small. Needs numpy, laspy[lazrs], pyproj, requests (all free).
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

from forest_truth import UA, get, ept_nodes, fill_nearest

NOISE = (7, 18)
GROUND = 2
CELL = 10
TREE_CODE = 12          # 3 m in the canopy map's 0.25 m steps
STAC = "https://planetarycomputer.microsoft.com/api/stac/v1/search"


# ------------------------------------------------------------------------------------------------ the map

def load_canopy(folder):
    info = json.load(open(os.path.join(folder, "forest.json"), encoding="utf-8"))
    w, h = info["canopyWidth"], info["canopyHeight"]
    canopy = np.fromfile(os.path.join(folder, "canopy.u8"), np.uint8).reshape(h, w)
    return info, canopy


def choose_patches(canopy, n, size, seed, blocks=(5, 4), forested=False):
    """
    One patch per block of a stratified grid, at a random spot (seeded). Uniform by default: the map
    under-counts sparse forest most, so patches placed only on forest would bias the density factor.
    """
    rng = np.random.default_rng(seed)
    h, w = canopy.shape
    bx, by = blocks
    tree = (canopy >= TREE_CODE).astype(np.float32)
    out = []
    for k in range(n):
        i, j = k % bx, (k // bx) % by
        x0, x1 = i * w // bx, (i + 1) * w // bx - size
        y0, y1 = j * h // by, (j + 1) * h // by - size
        best = None
        for _ in range(40):
            x, y = int(rng.integers(x0, max(x0 + 1, x1))), int(rng.integers(y0, max(y0 + 1, y1)))
            share = tree[y:y + size, x:x + size].mean()
            if not forested or share >= 0.3:
                best = (x, y)
                break
            if best is None or share > tree[best[1]:best[1] + size, best[0]:best[0] + size].mean():
                best = (x, y)
        out.append(best)
    return out


# ---------------------------------------------------------------------------------------------- the lidar

def ept_points(ept, box_albers, depth_step):
    meta = json.loads(get(ept))
    base = ept.rsplit("/", 1)[0]
    epsg = int(meta["srs"]["horizontal"])
    to_ept = Transformer.from_crs(6350, epsg, always_xy=True)
    W, S, E, N = box_albers
    xs, ys = to_ept.transform([W, W, E, E], [S, N, S, N])
    keys = ept_nodes(base, meta["bounds"], (min(xs), min(ys), max(xs), max(ys)))
    if depth_step:
        deepest = max(int(k.split("-")[0]) for k in keys)
        keys = [k for k in keys if int(k.split("-")[0]) <= deepest - depth_step]
    def read(k):
        las = laspy.read(io.BytesIO(get(f"{base}/ept-data/{k}.laz")))
        return np.asarray(las.x), np.asarray(las.y), np.asarray(las.z), np.asarray(las.classification), np.asarray(las.return_number)
    parts = list(cf.ThreadPoolExecutor(8).map(read, keys))
    if not parts:
        return None
    x, y, z, c, rn = (np.concatenate([p[i] for p in parts]) for i in range(5))
    ax, ay = Transformer.from_crs(epsg, 6350, always_xy=True).transform(x, y)
    return ax, ay, z, c, rn


_token = None


def copc_points(box_albers, resolution):
    global _token
    from pyproj import CRS
    W, S, E, N = box_albers
    to_ll = Transformer.from_crs(6350, 4326, always_xy=True)
    lons, lats = to_ll.transform([W, W, E, E], [S, N, S, N])
    body = json.dumps({"collections": ["3dep-lidar-copc"], "bbox": [min(lons), min(lats), max(lons), max(lats)], "limit": 20}).encode()
    items = json.load(urllib.request.urlopen(urllib.request.Request(STAC, body, {"Content-Type": "application/json"}), timeout=60))["features"]
    if not items:
        return None
    # The newest survey only: overlapping surveys of different years would mix canopies.
    year = max(i["properties"].get("datetime") or i["properties"].get("start_datetime") or "" for i in items)
    items = [i for i in items if (i["properties"].get("datetime") or i["properties"].get("start_datetime") or "") == year]
    if _token is None:
        _token = json.load(urllib.request.urlopen("https://planetarycomputer.microsoft.com/api/sas/v1/token/usgslidareuwest/usgs-3dep-copc"))["token"]
    xs, ys, zs, cs, rns = [], [], [], [], []
    for it in items:
        href = it["assets"]["data"]["href"] + "?" + _token
        with laspy.CopcReader.open(href) as r:
            crs = r.header.parse_crs()
            to_f = Transformer.from_crs(6350, crs.to_2d() if hasattr(crs, "to_2d") else crs, always_xy=True)
            fx, fy = to_f.transform([W, W, E, E], [S, N, S, N])
            b = laspy.copc.Bounds(mins=np.array([min(fx), min(fy)]), maxs=np.array([max(fx), max(fy)]))
            pts = r.query(bounds=b, resolution=resolution) if resolution else r.query(bounds=b)
            if len(pts) == 0:
                continue
            ax, ay = Transformer.from_crs(crs.to_2d() if hasattr(crs, "to_2d") else crs, 6350, always_xy=True).transform(np.asarray(pts.x), np.asarray(pts.y))
            xs.append(ax); ys.append(ay); zs.append(np.asarray(pts.z)); cs.append(np.asarray(pts.classification)); rns.append(np.asarray(pts.return_number))
    if not xs:
        return None
    return np.concatenate(xs), np.concatenate(ys), np.concatenate(zs), np.concatenate(cs), np.concatenate(rns)


def patch_chm(points, W, N, size):
    """The patch's 1 m canopy height model: the highest return per square metre minus the lidar's ground."""
    ax, ay, z, cls, rn = points
    c = np.floor(ax - W).astype(np.int64)
    r = np.floor(N - ay).astype(np.int64)
    ok = (c >= 0) & (c < size) & (r >= 0) & (r < size) & ~np.isin(cls, NOISE)
    c, r, z, cls, rn = c[ok], r[ok], z[ok], cls[ok], rn[ok]
    patch_chm.points = len(z)
    top = np.full((size, size), -np.inf, np.float32)
    np.maximum.at(top, (r, c), z.astype(np.float32))
    g = cls == GROUND
    gsum = np.zeros((size // 2, size // 2)); gcnt = np.zeros((size // 2, size // 2))
    np.add.at(gsum, (r[g] // 2, c[g] // 2), z[g]); np.add.at(gcnt, (r[g] // 2, c[g] // 2), 1)
    if gcnt.sum() == 0:
        return None
    with np.errstate(invalid="ignore", divide="ignore"):
        ground = fill_nearest(np.where(gcnt > 0, gsum / np.maximum(gcnt, 1), np.nan))
    ground = np.repeat(np.repeat(ground, 2, 0), 2, 1)
    chm = np.where(np.isfinite(top), top - ground, np.nan)
    return np.clip(chm, 0, 80)


# -------------------------------------------------------------------------------------------- the factors

def fill_empty(chm, passes=2):
    """Sparse surveys leave 1 m pixels without a return: give them the highest of their neighbours."""
    a = chm.copy()
    for _ in range(passes):
        empty = np.isnan(a)
        if not empty.any():
            break
        p = np.pad(a, 1, constant_values=np.nan)
        stack = np.stack([p[i:i + a.shape[0], j:j + a.shape[1]] for i in range(3) for j in range(3)])
        with np.errstate(all="ignore"):
            m = np.nanmax(stack, axis=0)
        a[empty] = m[empty]
    return a


def factors(pairs):
    """pairs: (map patch codes, lidar CHM) per patch → density and dominant-height factors."""
    map_cover, lidar_cover, ratios = [], [], []
    for m, chm in pairs:
        n = m.shape[0] // CELL
        mb = m[:n * CELL, :n * CELL].reshape(n, CELL, n, CELL)
        lb = fill_empty(chm)[:n * CELL, :n * CELL].reshape(n, CELL, n, CELL)
        valid = np.isfinite(lb).mean(axis=(1, 3)) >= 0.9   # after filling: only cells the survey really missed drop out
        lf = np.nan_to_num(lb)
        map_cover.append((mb >= TREE_CODE).mean(axis=(1, 3))[valid])
        lidar_cover.append((lf >= 3).mean(axis=(1, 3))[valid])
        mt = mb.max(axis=(1, 3)) * 0.25
        lt = lf.max(axis=(1, 3))
        forest = valid & (mt >= 3) & (lt >= 3)
        ratios.append(lt[forest] / mt[forest])
    mc, lc, rr = np.concatenate(map_cover), np.concatenate(lidar_cover), np.concatenate(ratios)
    return {"density": round(float(lc.mean() / max(mc.mean(), 1e-9)), 3),
            "dominantHeight": round(float(np.median(rr)), 3) if len(rr) else None,
            "mapCover": round(float(mc.mean()), 3), "lidarCover": round(float(lc.mean()), 3),
            "cells": int(len(mc)), "forestCells": int(len(rr))}


def measure(info, canopy, patches, size, source, args):
    pairs, points = [], 0
    for (x, y) in patches:
        W = info["canopyWest"] + x
        N = info["canopyNorth"] - y
        box = (W, N - size, W + size, N)
        pts = ept_points(args.ept, box, args.depth_step) if args.ept else copc_points(box, args.resolution)
        if pts is None:
            continue
        chm = patch_chm(pts, W, N, size)
        if chm is None:
            continue
        points += patch_chm.points
        pairs.append((canopy[y:y + size, x:x + size], chm))
    f = factors(pairs) if pairs else {}
    f.update({"patches": len(pairs), "points": int(points), "pointsPerM2": round(points / max(1, len(pairs) * size * size), 2)})
    return f


# ---------------------------------------------------------- tile-aligned sampling (what the downloader would do)

def inscribed_box(xs, ys):
    """The axis-aligned box inside a slightly rotated square (a lidar tile seen in Albers)."""
    xs, ys = sorted(xs), sorted(ys)
    return xs[1], ys[1], xs[2], ys[2]


def tile_patches(info, canopy, n, seed, target=(100, 250), min_density=3.0, ept=None, copc_items=None):
    """
    One lidar tile per patch: the octree level whose tiles are `target` metres across, tiles chosen at random
    (seeded) in a stratified grid over the core. Returns (box in Albers, points, bytes) per patch.
    """
    rng = np.random.default_rng(seed)
    W0, N0 = info["canopyWest"], info["canopyNorth"]
    E0, S0 = W0 + info["canopyWidth"], N0 - info["canopyHeight"]
    out = []
    if ept:
        meta = json.loads(get(ept)); base = ept.rsplit("/", 1)[0]
        epsg = int(meta["srs"]["horizontal"]); cube = meta["bounds"]
        to_e = Transformer.from_crs(6350, epsg, always_xy=True); to_a = Transformer.from_crs(epsg, 6350, always_xy=True)
        # Real width of a tile at depth d (the EPT CRS may be web Mercator, whose metres are stretched).
        cx, cy = to_e.transform((W0 + E0) / 2, (S0 + N0) / 2)
        def real_width(d):
            w = (cube[3] - cube[0]) / 2 ** d
            ax, ay = to_a.transform([cx, cx + w], [cy, cy])
            return abs(ax[1] - ax[0])
        depth = next(d for d in range(30) if real_width(d) <= target[1])
        ex, ey = to_e.transform([W0, W0, E0, E0], [S0, N0, S0, N0])
        core = (min(ex), min(ey), max(ex), max(ey))
        counts = {}
        def walk(key):
            h = json.loads(get(f"{base}/ept-hierarchy/{key}.json"))
            for k, c in h.items():
                d, x, y, _ = map(int, k.split("-"))
                if d > depth + 1:
                    continue
                size = (cube[3] - cube[0]) / 2 ** d
                x0, y0 = cube[0] + x * size, cube[1] + y * size
                if not (x0 < core[2] and x0 + size > core[0] and y0 < core[3] and y0 + size > core[1]):
                    continue
                if c == -1:
                    walk(k)
                elif c > 0:
                    counts[k] = c
        walk("0-0-0-0")
        # A patch is a whole vertical column of tiles: on a mountain one column spans several heights.
        columns = {}
        for k in counts:
            d, x, y, z = map(int, k.split("-"))
            if d == depth:
                columns.setdefault((x, y), []).append(k)
        tiles = sorted(columns)
        size = (cube[3] - cube[0]) / 2 ** depth
        def tile_box(k):
            x, y = k
            x0, y0 = cube[0] + x * size, cube[1] + y * size
            ax, ay = to_a.transform([x0, x0, x0 + size, x0 + size], [y0, y0 + size, y0, y0 + size])
            return inscribed_box(ax, ay)
        inside = [k for k in tiles if (lambda b: b[0] >= W0 and b[2] <= E0 and b[1] >= S0 and b[3] <= N0)(tile_box(k))]
        # Stratify: 5 x 4 blocks over the core, one tile per block.
        picked = []
        for j in range(4):
            for i in range(5):
                bw, bs = W0 + i * (E0 - W0) / 5, S0 + j * (N0 - S0) / 4
                cand = [k for k in inside if bw <= tile_box(k)[0] < bw + (E0 - W0) / 5 and bs <= tile_box(k)[1] < bs + (N0 - S0) / 4]
                if cand:
                    picked.append(cand[int(rng.integers(len(cand)))])
        for k in picked[:n]:
            x, y = k
            keys = sorted(columns[k])
            area = (real_width(depth)) ** 2
            if sum(counts[c] for c in keys) / area < min_density:   # too sparse at this level: add the next level's column
                keys += sorted(c for c in counts if c.startswith(f"{depth + 1}-") and int(c.split("-")[1]) // 2 == x and int(c.split("-")[2]) // 2 == y)
            parts, nbytes = [], 0
            for kk in keys:
                raw = get(f"{base}/ept-data/{kk}.laz"); nbytes += len(raw)
                las = laspy.read(io.BytesIO(raw))
                parts.append((np.asarray(las.x), np.asarray(las.y), np.asarray(las.z), np.asarray(las.classification), np.asarray(las.return_number)))
            x_, y_, z_, c_, r_ = (np.concatenate([p[i] for p in parts]) for i in range(5))
            ax, ay = to_a.transform(x_, y_)
            out.append((tile_box(k), (ax, ay, z_, c_, r_), nbytes, len(keys)))
    return out


def measure_tiles(info, canopy, args, seed):
    pairs, points, nbytes, area, files = [], 0, 0, 0, 0
    for box, pts, b, nfiles in tile_patches(info, canopy, args.patches, seed, ept=args.ept):
        if args.tile_full:   # the same patch at the survey's full density, for comparison
            pts = ept_points(args.ept, box, 0); b = 0
        W, S, E, N = box
        W, N = int(np.ceil(W)), int(np.floor(N))
        size = int(min(np.floor(E) - W, N - np.ceil(S))) // CELL * CELL
        if size < 50:
            continue
        chm = patch_chm(pts, W, N, size)
        if chm is None:
            continue
        x, y = W - info["canopyWest"], info["canopyNorth"] - N
        pairs.append((canopy[y:y + size, x:x + size], chm))
        points += patch_chm.points; nbytes += b; area += size * size; files += nfiles
    f = factors(pairs) if pairs else {}
    f.update({"patches": len(pairs), "tileMetres": round((area / max(1, len(pairs))) ** 0.5), "files": files,
              "megabytes": round(nbytes / 1e6, 1), "pointsPerM2": round(points / max(1, area), 2)})
    return f


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--forest", required=True)
    ap.add_argument("--ept")
    ap.add_argument("--copc", action="store_true")
    ap.add_argument("--patches", type=int, default=20)
    ap.add_argument("--size", type=int, default=200)
    ap.add_argument("--depth-step", type=int, default=0)
    ap.add_argument("--resolution", type=float, default=0)
    ap.add_argument("--forested", action="store_true", help="place patches only on forest (biased; for comparison)")
    ap.add_argument("--tiles", action="store_true", help="tile-aligned sampling: one lidar tile per patch (EPT)")
    ap.add_argument("--tile-full", action="store_true", help="with --tiles: read the same patches at full density")
    ap.add_argument("--out")
    a = ap.parse_args()
    info, canopy = load_canopy(a.forest)
    seed = int(abs(info["centreX"]) + abs(info["centreY"]))
    result = {"site": info["name"], "source": a.ept or "planetary-computer 3dep-lidar-copc",
              "depthStep": a.depth_step, "resolution": a.resolution}
    for label, s in (("setA", seed), ("setB", seed + 1)):
        if a.tiles:
            result[label] = measure_tiles(info, canopy, a, s)
            print(label, json.dumps(result[label]), flush=True)
            continue
        result[label] = measure(info, canopy, choose_patches(canopy, a.patches, a.size, s, forested=a.forested), a.size, None, a)
        print(label, json.dumps(result[label]), flush=True)
    print(json.dumps(result, indent=1))
    if a.out:
        json.dump(result, open(a.out, "w"), indent=1)


if __name__ == "__main__":
    sys.exit(main())
