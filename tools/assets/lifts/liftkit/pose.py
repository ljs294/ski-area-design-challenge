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
    return carry(pivots, angles, name, (pivots[name]["pos"][0], pivots[name]["pos"][2]))


def carry(pivots, angles, name, uw):
    """A point (u, w) riding with pivot `name`, moved by the pivot angles of its chain (its own turn included)."""
    names = chain(pivots, name)
    p = np.array(uw, dtype=float)
    for anc in reversed(names):   # innermost turn first, each about its own rest position, then up the chain
        a = angles.get(anc, 0.0)
        if a == 0.0:
            continue
        c = np.array([pivots[anc]["pos"][0], pivots[anc]["pos"][2]], dtype=float)
        ca, sa = math.cos(a), math.sin(a)
        d = p - c
        p = c + np.array([ca * d[0] - sa * d[1], sa * d[0] + ca * d[1]])
    return p


def solve(pivots, top, rows, s_in, s_out, fixed=(), iters=30, anchor=None):
    """Pivot angles (radians) under `top` that put each row's axle chords at the shared-load slopes. rows: lists of
    sheave pivot names, each ordered along +u. fixed: pivots that don't turn (a range of zero). anchor: (sheave, axle
    height) held fast, as where a terminal's row takes the rope level from the bullwheel at the bullwheel's height;
    the slopes then share the turn as evenly as that allows."""
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
        if anchor:   # a fixed entry: the rows share the turn by the turn over each sheave, the first held fast
            r = []
            for row in rows:
                pts = [place(pivots, ang, s) for s in row]
                slopes = [s_in] + [math.atan2(*(b - a)[::-1]) for a, b in zip(pts, pts[1:])] + [s_out]
                r.extend(b - a - (s_out - s_in) / len(row) for a, b in zip(slopes, slopes[1:]))
            r.extend(1e-3 * v for v in x)
            r.append(1e3 * (place(pivots, ang, anchor[0])[1] - anchor[1]))
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
    err = residual(x)[:sum(len(t) for t in targets)] if not anchor else residual(x)[-1:] / 1e3
    return dict(zip(free, x)), float(np.abs(err).max())


def rope_path(pivots, angles, row, rho, s_in, s_out, side, far=8.0):
    """The rope past a posed row, (u, w) points: in at s_in tangent to the first sheave, over (side +1: the rope on
    top of a support row) or under (side -1: a hold-down row) each sheave, out at s_out. Between sheaves it runs
    parallel to the line through their posed axles (it rides the same side of both)."""
    centres = [place(pivots, angles, s) for s in row]
    slopes = [s_in] + [math.atan2(*(b - a)[::-1]) for a, b in zip(centres, centres[1:])] + [s_out]
    pts = []
    for k, c in enumerate(centres):
        for a in (slopes[k], slopes[k + 1]):
            pts.append(c + side * rho * np.array([-math.sin(a), math.cos(a)]))
    first = pts[0] - far * np.array([math.cos(s_in), math.sin(s_in)])
    last = pts[-1] + far * np.array([math.cos(s_out), math.sin(s_out)])
    return [first] + pts + [last]


def turns(pivots, angles, row, s_in, s_out):
    """How far the rope turns over each sheave of a posed row (radians): its share of the row's load."""
    centres = [place(pivots, angles, s) for s in row]
    slopes = [s_in] + [math.atan2(*(b - a)[::-1]) for a, b in zip(centres, centres[1:])] + [s_out]
    return [b - a for a, b in zip(slopes, slopes[1:])]


def band(pivots, top, row, s_in, anchor, outs=range(0, 41), slack=1e-4, ranges=None):
    """The exit slopes (whole degrees in `outs`, signed as s_out) at which a row posed by solve(anchor=...) keeps
    every sheave on the rope (each takes a turn of the rope's own sense, to `slack` radians) with every pivot inside
    its range: `ranges` {pivot: [min, max]} in Unity degrees as lifts.json has them (the clash sweep clamped to the
    stops; a pose angle is the negative), else its stop (degrees either way; pivots without one are free). Returns
    {"from", "to", "count", "worst": the largest pivot turn in the band (degrees), "shares": [min, max] turn per
    sheave over the band (degrees)}."""
    ok, worst, lo, hi = [], 0.0, None, None
    for deg in outs:
        s_out = math.radians(deg)
        ang, _ = solve(pivots, top, [row], s_in, s_out, anchor=anchor)
        tu = turns(pivots, ang, row, s_in, s_out)
        sense = 1.0 if s_out >= s_in else -1.0
        on = all(sense * t >= -slack for t in tu)
        if ranges:
            inside = all(ranges[k][0] - 1e-6 <= -math.degrees(a) <= ranges[k][1] + 1e-6 for k, a in ang.items())
        else:
            inside = all(abs(math.degrees(a)) <= pivots[k].get("stop", 360.0) + 1e-6 for k, a in ang.items())
        if on and inside:
            ok.append(deg)
            worst = max([worst] + [abs(math.degrees(a)) for a in ang.values()])
            lo = min([lo] + [math.degrees(t) for t in tu] if lo is not None else [math.degrees(t) for t in tu])
            hi = max([hi] + [math.degrees(t) for t in tu] if hi is not None else [math.degrees(t) for t in tu])
    if not ok:
        return {"from": None, "to": None, "count": 0}
    return {"from": ok[0], "to": ok[-1], "count": len(ok), "worst": round(worst, 2), "shares": [round(lo, 2), round(hi, 2)]}
