"""Station fit check for detachable terminals: does a chair get through the station?

The round 3 review of the Sessellift D6 found chairs passing through the rail's hangers, the horns, the floor and the
drive's bend-sheave brackets, and nothing in the build caught it: clash.py only turns an asset's own parts. Here the
chair (its LOD0, every face) rides the terminal's `paths["chairs"]` every STEP metres, grip on the path, the chair's
u along the travel and its v outboard (right of the travel, away from the loop's middle), then on out along both ropes
past the hood's line end. At each pose its faces are intersected with the terminal's: the body, each "#piece" and each
moving part at rest (the bullwheel). A piece named in CONTACTS is one the station is built to bear on a zone of the
grip (the running wheels on the rail, the tyre plate on the tyres, the lever's top roller on the opening rails); that
zone may touch it, nothing else may. Every other gap must be at least `clear`. The bullwheel is also turned through
one spoke pitch (45 degrees) against everything static.

Lift frame (u, v, w) metres throughout; chair frame: u travel, v outboard, w up, origin the grip on the rope.
"""
import math

import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

from . import frame

CLEAR = 0.050   # least gap between a passing chair and any station part (m)
STEP = 0.050    # pose spacing along the path (m)
OUT = 2.5       # how far past the hood's line end the chair is followed along the ropes (m)
CONTACTS = {"#rail": "wheels", "#tyres": "tyre plate", "#openrail": "top roller"}
# pieces built to guide a passing grip closely: these zones may come near them (no `clear` needed) but never into them
GUIDES = {"#horns": ("guide roller", "grip and arm"), "#bend_sheaves": ("grip and arm", "wheels"),
          "#tyres": ("grip and arm",), "#openrail": ("grip and arm",), "#rail": ("grip and arm",)}


def zone(p):
    """The part of the detachable chair a chair-frame point (m) belongs to (sessellift_detach_grip.json's layout)."""
    u, v, w = (c * 1000.0 for c in p)
    if w > -150 and v < 800:
        if abs(u) < 60 and (v - 552) ** 2 + (w - 271) ** 2 < 95 ** 2:
            return "top roller"
        if 115 <= v <= 205 and w > 85:
            return "tyre plate"
        if 80 <= v <= 190 and -100 <= w <= 85 and 91 <= abs(u) <= 291:
            return "wheels"
        if v >= 680:
            return "guide roller"
        return "grip and arm"
    if w > -1700 and v > -70:
        return "hanger"
    return "chair body"


def _tree(points, faces):
    return BVHTree.FromPolygons([Vector(p) for p in points], faces, all_triangles=False, epsilon=0.0)


def _lift_points(mb):
    return np.array([frame.lift(v) for v in mb.verts], dtype=float)


def _route(path, rope_w, u_out):
    """The path ordered so that outboard is right of the travel, extended at both ends out along the ropes."""
    pts = [Vector(p) for p in path]
    mid = Vector((sum(p.x for p in pts) / len(pts), 0.0, 0.0))
    votes = 0.0
    for a, b in zip(pts, pts[1:]):
        t = Vector((b.x - a.x, b.y - a.y, 0.0))
        if t.length > 1e-9:
            n = Vector((-t.y, t.x, 0.0))
            votes += n.dot(Vector(((a.x + b.x) / 2 - mid.x, (a.y + b.y) / 2, 0.0)))
    if votes < 0:
        pts.reverse()
    return [Vector((u_out, pts[0].y, rope_w))] + pts + [Vector((u_out, pts[-1].y, rope_w))]


def _samples(route, step):
    """(origin, travel, outboard) every `step` along the route."""
    out = []
    for a, b in zip(route, route[1:]):
        d = b - a
        ln = d.length
        if ln < 1e-9:
            continue
        flat = Vector((d.x, d.y, 0.0)).normalized()
        n = Vector((-flat.y, flat.x, 0.0))
        k = max(1, int(ln / step))
        for i in range(k):
            out.append((a + d * (i / k), flat, n))
    out.append((route[-1], out[-1][1], out[-1][2]))
    return out


def station(asset, chair, clear=CLEAR, step=STEP):
    """Problems found sending `chair` (an Asset at LOD0) through `asset` (a detachable terminal at LOD0), and a summary."""
    body, pieces = asset.body.split(pieces=True)
    groups = {"structure": body, **pieces, **asset.parts}   # moving parts (the bullwheel) where they rest
    trees = {}
    for name, mb in groups.items():
        if not mb.faces:
            continue
        pts = _lift_points(mb)
        trees[name] = (_tree(pts, [list(f) for f in mb.faces]), pts.min(0) - clear, pts.max(0) + clear,
                       [pts[list(f)].mean(0) for f in mb.faces])
    cv = _lift_points(chair.body)
    cf = [list(f) for f in chair.body.faces]
    fzone = [zone(cv[f].mean(0)) for f in cf]
    vzone = [zone(p) for p in cv]
    s = asset.sockets
    outs = [k for k in s if k.startswith("rope_") and k.endswith("_out")]
    rope_w = s[outs[0]][2]
    u_out = max(s[k][0] for k in outs) + OUT
    poses = _samples(_route(asset.paths["chairs"], rope_w, u_out), step)

    clashes, near = {}, {}
    w_up = np.array((0.0, 0.0, 1.0))
    for pi, (o, t, n) in enumerate(poses):
        x = (np.array(o)[None, :] + np.outer(cv[:, 0], np.array(t)) + np.outer(cv[:, 1], np.array(n))
             + np.outer(cv[:, 2], w_up))
        lo, hi = x.min(0), x.max(0)
        ct = None
        for name, (tree, blo, bhi, centres) in trees.items():
            if (hi < blo).any() or (lo > bhi).any():
                continue
            if ct is None:
                ct = _tree(x, cf)
            ok = CONTACTS.get(name)
            bad = [(i, j) for i, j in ct.overlap(tree) if fzone[i] != ok]
            if bad:
                key = (name, ", ".join(sorted({fzone[i] for i, _ in bad})))
                e = clashes.setdefault(key, [0, centres[bad[0][1]]])
                e[0] += 1
                continue
            if pi % 2:
                continue
            inside = np.nonzero(np.all((x >= blo) & (x <= bhi), axis=1))[0]
            for i in inside:
                if vzone[i] == ok or vzone[i] in GUIDES.get(name, ()):
                    continue
                r = tree.find_nearest(Vector(x[i]), clear)
                if r[0] is not None:
                    key = (name, vzone[i])
                    if key not in near or r[3] < near[key][0]:
                        near[key] = (r[3], tuple(r[0]))

    problems = []
    for (name, zones), (count, c) in sorted(clashes.items()):
        problems.append(f"fit: chair ({zones}) passes through {name} at {count} of {len(poses)} poses, e.g. at "
                        f"u {c[0] * 1000:.0f}, v {c[1] * 1000:.0f}, w {c[2] * 1000:.0f}")
    for (name, z), (d, c) in sorted(near.items()):
        problems.append(f"fit: chair ({z}) passes {name} {d * 1000:.0f} mm away (< {clear * 1000:.0f}) at "
                        f"u {c[0] * 1000:.0f}, v {c[1] * 1000:.0f}, w {c[2] * 1000:.0f}")

    piv = asset.pivots.get("bullwheel")
    if piv and "bullwheel" in groups:
        wheel = groups["bullwheel"]
        pts = _lift_points(wheel)
        pos, ax = Vector(piv["pos"]), Vector(piv["axis"]).normalized()
        wf = [list(f) for f in wheel.faces]
        hit = {}
        for deg in range(0, 46, 5):
            rot = Matrix.Rotation(math.radians(deg), 3, ax)
            wt = _tree([rot @ (Vector(p) - pos) + pos for p in pts], wf)
            for name, (tree, _lo, _hi, centres) in trees.items():
                if name != "bullwheel" and name not in hit:
                    pairs = wt.overlap(tree)
                    if pairs:
                        hit[name] = centres[pairs[0][1]]
        problems += [f"fit: the turning bullwheel meets {name} at u {c[0] * 1000:.0f}, v {c[1] * 1000:.0f}, w {c[2] * 1000:.0f}"
                     for name, c in sorted(hit.items())]
    return problems, {"poses": len(poses), "clashes": len(clashes), "tight": len(near)}
