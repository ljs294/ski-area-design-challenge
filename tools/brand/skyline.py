"""The skyline of the demo mountain as the title camera sees it (logo theme "Peaks", task P2-03.1).

Reads the bundled Jackson Hole package's height grids (the game's own GridFile format: chunked, a byte-plane float
predictor, raw Deflate), samples them every 8 m, projects every sample from each of the title drift's four views (the
camera of App/Flow/TitleDrift.cs: orbit target, distance, yaw clockwise from grid north, pitch down, 60 degrees vertical
field of view) and keeps the topmost point in every screen column. Writes each skyline as JSON (x 0..1 across the frame,
y in screen units from the top) for the logo board to draw from.

Usage: python tools/brand/skyline.py <package folder> <out.json>
"""
import json
import math
import struct
import sys
import zlib

import numpy as np

CHUNK_ROWS = 256


def read_grid(path, step):
    with open(path, 'rb') as f:
        data = f.read()
    magic, version, typ, comp, w, h, west, north, cell, chunk_rows, chunks = struct.unpack_from('<IHBBiidddii', data, 0)
    assert magic == 0x5247504D and typ == 1 and comp == 1 and chunk_rows == CHUNK_ROWS, (hex(magic), typ, comp)
    off = struct.calcsize('<IHBBiidddii')
    lengths = struct.unpack_from(f'<{chunks}i', data, off)
    off += 4 * chunks
    rows_out = []
    for c in range(chunks):
        packed = data[off:off + lengths[c]]
        off += lengths[c]
        raw = np.frombuffer(zlib.decompress(packed, -15), dtype=np.uint8)
        rows = min(CHUNK_ROWS, h - c * CHUNK_ROWS)
        planar = np.cumsum(raw.reshape(rows, w * 4), axis=1, dtype=np.uint8).reshape(rows, 4, w)
        be = (planar[:, 0].astype(np.uint32) << 24) | (planar[:, 1].astype(np.uint32) << 16) | \
             (planar[:, 2].astype(np.uint32) << 8) | planar[:, 3].astype(np.uint32)
        vals = be.view(np.float32)
        first = c * CHUNK_ROWS
        keep = [r for r in range(rows) if (first + r) % step == 0]
        rows_out.append(vals[keep][:, ::step])
    grid = np.vstack(rows_out)
    return grid, west, north, cell * step


def points(path, step, cx, cy):
    g, west, north, cell = read_grid(path, step)
    h, w = g.shape
    xs = west + (np.arange(w) + 0.5) * cell - cx
    zs = north - (np.arange(h) + 0.5) * cell - cy
    X, Z = np.meshgrid(xs, zs)
    ok = np.isfinite(g) & (g > -1000)
    return X[ok], g[ok].astype(np.float64), Z[ok]


# The title drift's views over Jackson Hole (TitleDrift.JacksonHole): target x, z, distance, yaw, pitch.
VIEWS = [(-900, 300, 4200, 290, 14), (-1550, 600, 1900, 262, 16), (-1800, -500, 2600, 300, 13), (-300, 1300, 3000, 245, 15)]


def skyline(P, view, aspect=32 / 9, columns=1600, vfov=60.0):
    X, Y, Z = P
    tx, tz, dist, yaw, pitch = view
    near = (X - tx) ** 2 + (Z - tz) ** 2 < 60 ** 2
    ty = float(np.median(Y[near])) if near.any() else float(np.median(Y))
    yr, pr = math.radians(yaw), math.radians(pitch)
    fwd = np.array([math.sin(yr) * math.cos(pr), -math.sin(pr), math.cos(yr) * math.cos(pr)])
    eye = np.array([tx, ty, tz]) - fwd * dist
    right = np.array([math.cos(yr), 0.0, -math.sin(yr)])
    up = np.cross(fwd, right)   # x east, y up, z north is left-handed
    rel = np.stack([X - eye[0], Y - eye[1], Z - eye[2]], axis=1)
    zc = rel @ fwd
    front = zc > 50
    xc, yc, zc = rel[front] @ right, rel[front] @ up, zc[front]
    f = 1 / math.tan(math.radians(vfov) / 2)
    sx = xc / zc * f / aspect          # -1..1 across the frame
    sy = -yc / zc * f                  # -1..1 down the frame
    inside = np.abs(sx) <= 1
    col = ((sx[inside] + 1) / 2 * (columns - 1)).round().astype(int)
    top = np.full(columns, np.inf)
    np.minimum.at(top, col, sy[inside])
    return [(i / (columns - 1), float(top[i])) for i in range(columns) if np.isfinite(top[i])]


def main():
    folder, out = sys.argv[1], sys.argv[2]
    manifest = json.load(open(f'{folder}/manifest.json', encoding='utf-8'))
    cx, cy = manifest['Site']['CentreX'], manifest['Site']['CentreY']
    core = points(f'{folder}/heights-core.grid', 8, cx, cy)
    ring = points(f'{folder}/heights-ring.grid', 4, cx, cy)
    P = tuple(np.concatenate([a, b]) for a, b in zip(core, ring))
    lines = [skyline(P, v) for v in VIEWS]
    json.dump({'site': manifest['Site']['Name'], 'views': VIEWS, 'skylines': lines}, open(out, 'w'))
    print('points', len(P[0]), 'columns', [len(l) for l in lines])


if __name__ == '__main__':
    main()
