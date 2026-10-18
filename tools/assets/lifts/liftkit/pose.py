"""A review-photo stand-in for the game's train posing (task P3-07): turns a head's nested pivots so its sheaves lie on
the rope's curve, to show the models can be posed. The game's own posing lives in the Unity code; this is only for
the Blender photos.

Equalising trains share the rope's load equally, so the rope turns by the same angle at every sheave: with n
sheaves taking the rope from slope s_in to s_out, the rope between sheaves k and k+1 runs at
s_in - k (s_in - s_out) / n. The rope rides the same side of every sheave in a row, so each rope segment is parallel
to the line through the two axles: the pivots' angles are solved (least squares, top pivot first in the hierarchy)
so the axle chords take those slopes. Angles are in the u-w plane, counter-clockwise (u toward w); Unity's
Quaternion.AngleAxis about +X takes the negative.
"""
import math

import numpy as np


def subtree(pivots, top):
    kids, grew = {top}, True
    while grew:
        grew = False
        for k, p in pivots.items():
            if p.get("parent") in kids and k not in kids:
                kids.add(k)
                grew = True
    return kids


def chain(pivots, name):
    out = []
    while name:
        out.append(name)
        name = pivots[name].get("parent")
    return out[::-1]


def place(pivots, angles, name):
    """A pivot's or sheave's (u, w) with the given pivot angles (a dict) applied down its chain of parents."""
    names = chain(pivots, name)
    p = np.array([pivots[name]["pos"][0], pivots[name]["pos"][2]], dtype=float)
    for anc in reversed(names):   # innermost turn first, each about its own rest position, then up the chain
        a = angles.get(anc, 0.0)
        if a == 0.0:
            continue
        c = np.array([pivots[anc]["pos"][0], pivots[anc]["pos"][2]], dtype=float)
        ca, sa = math.cos(a), math.sin(a)
        d = p - c
        p = c + np.array([ca * d[0] - sa * d[1], sa * d[0] + ca * d[1]])
    return p


def solve(pivots, top, rows, s_in, s_out, fixed=(), iters=30):
    """Pivot angles (radians) under `top` that put each row's axle chords at the shared-load slopes. rows: lists of
    sheave pivot names, each ordered along +u. fixed: pivots that don't turn (a range of zero)."""
    free = [k for k in subtree(pivots, top) if not k.startswith("sheave_") and k not in fixed]
    free.sort(key=lambda k: len(chain(pivots, k)))
    targets = []
    for row in rows:
        n = len(row)
        targets.append([s_in - (k + 1) * (s_in - s_out) / n for k in range(n - 1)])

    def residual(x):
        ang = dict(zip(free, x))
        r = []
        for row, tg in zip(rows, targets):
            pts = [place(pivots, ang, s) for s in row]
            for k in range(len(row) - 1):
                d = pts[k + 1] - pts[k]
                r.append(math.atan2(d[1], d[0]) - tg[k])
        r.extend(1e-3 * v for v in x)   # the redundant links (s6) stay near rest
        return np.array(r)
    x = np.zeros(len(free))
    for _ in range(iters):
        r = residual(x)
        jac = np.zeros((len(r), len(x)))
        for j in range(len(x)):
            dx = np.zeros(len(x))
            dx[j] = 1e-6
            jac[:, j] = (residual(x + dx) - r) / 1e-6
        step = np.linalg.lstsq(jac, -r, rcond=None)[0]
        x = x + step
        if np.abs(step).max() < 1e-9:
            break
    err = residual(x)[:sum(len(t) for t in targets)]
    return dict(zip(free, x)), float(np.abs(err).max())


def rope_path(pivots, angles, row, rho, s_in, s_out, side, far=8.0):
    """The rope past a posed row, (u, w) points: in at s_in tangent to the first sheave, over (side +1: the rope on
    top of a support row) or under (side -1: a hold-down row) each sheave, out at s_out."""
    n = len(row)
    slopes = [s_in] + [s_in - (k + 1) * (s_in - s_out) / n for k in range(n - 1)] + [s_out]
    pts = []
    for k, s in enumerate(row):
        c = place(pivots, angles, s)
        for a in (slopes[k], slopes[k + 1]):
            pts.append(c + side * rho * np.array([-math.sin(a), math.cos(a)]))
    first = pts[0] - far * np.array([math.cos(s_in), math.sin(s_in)])
    last = pts[-1] + far * np.array([math.cos(s_out), math.sin(s_out)])
    return [first] + pts + [last]
