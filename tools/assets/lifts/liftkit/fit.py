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
TOUCH = 0.003   # a contact zone must come within this of its piece somewhere, and never sink deeper into it (m)
THUMP = 0.050   # how far a passing grip may ride into a line sheave: real grips lift it as they pass (round 3, P5-14)
TRAIN = ("sheave_", "rocker_", "yoke_", "eq_", "link_", "block_")   # a head's sheave trains: built to pass grips closely
# pieces built to guide a passing grip closely: these zones may come near them (no `clear` needed) but never into them
GUIDES = {"#horns": ("guide roller", "grip and arm"), "#bend_sheaves": ("grip and arm", "wheels"),
          "#tyres": ("grip and arm",), "#openrail": ("grip and arm",), "#openrail_hangers": ("top roller", "grip and arm"),
          "#rail": ("grip and arm",), "#rail_posts": ("wheels",)}


def zone(p):
    """The part of the detachable chair a chair-frame point (m) belongs to (sessellift_detach_grip.json's layout)."""
    u, v, w = (c * 1000.0 for c in p)
    if w > -150 and v < 800:
        if abs(u) < 60 and (v - 552) ** 2 + (w - 271) ** 2 < 95 ** 2:
            return "top roller"
        if 115 <= v <= 205 and w > 85:
            return "tyre plate"
        if 113 <= v <= 160 and -100 <= w <= 85 and 91 <= abs(u) <= 291:   # the treads only
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


def _sign():
    """+1 if a BVH normal of a frame.lift mesh points out of a solid, else -1 (lift frame is a mirror of Blender's)."""
    from .mesh import MeshBuilder, Style
    from . import prims
    mb = MeshBuilder()
    prims.box(mb, (0, 0, 0), (1, 1, 1), Style())
    t = _tree(_lift_points(mb), [list(f) for f in mb.faces])
    loc, n, _, _ = t.find_nearest(Vector((0.5, 0.5, 1.2)))
    return 1.0 if (Vector((0.5, 0.5, 1.2)) - loc).dot(n) > 0 else -1.0


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
    touch = {}   # contact piece -> [closest approach, deepest signed distance] of its zone
    sign = _sign()
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
            inside = np.nonzero(np.all((x >= blo) & (x <= bhi), axis=1))[0]
            for i in inside:
                if vzone[i] == ok:
                    r = tree.find_nearest(Vector(x[i]), clear)
                    if r[0] is not None:
                        sd = sign * (Vector(x[i]) - r[0]).dot(r[1])
                        e = touch.setdefault(name, [1e9, 1e9])
                        e[0], e[1] = min(e[0], abs(sd)), min(e[1], sd)
                    continue
                if vzone[i] in GUIDES.get(name, ()):
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
        problems.append(f"fit: chair ({z}) passes {name} {d * 1000:.1f} mm away (< {clear * 1000:.0f}) at "
                        f"u {c[0] * 1000:.0f}, v {c[1] * 1000:.0f}, w {c[2] * 1000:.0f}")

    for name, zone_ in CONTACTS.items():   # a contact must bear, not float and not sink
        if name in trees and name in touch:
            near_, deep = touch[name]
            if near_ > TOUCH:
                problems.append(f"fit: the grip's {zone_} never reach {name} (closest {near_ * 1000:.1f} mm)")
            if deep < -TOUCH:
                problems.append(f"fit: the grip's {zone_} sink {-deep * 1000:.1f} mm into {name}")
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


def tower(asset, chair, rope_r, sheave_r, clear=CLEAR, step=STEP, reach=3.0):
    """Problems found sending `chair` (an Asset at LOD0) past a line tower head (an Asset at LOD0) on both ropes. The
    rope is laid as the head's trains lay it: over each support sheave (under each hold-down one) and straight on for
    `reach` past the row's ends. The grip pitches with the rope, the hanger hangs plumb from its hinge (contract M3).
    The sheaves are contacts for the grip (it rides over them; a real grip lifts a sheave by a few millimetres: it may
    not sink deeper than TOUCH); everything else needs `clear`."""
    body, pieces = asset.body.split(pieces=True)
    groups = {"structure": body, **pieces}
    trees = {}
    for name, mb in groups.items():
        if not mb.faces:
            continue
        pts = _lift_points(mb)
        trees[name] = (_tree(pts, [list(f) for f in mb.faces]), pts.min(0) - clear, pts.max(0) + clear,
                       [pts[list(f)].mean(0) for f in mb.faces])
    cv = _lift_points(chair.body)
    cf = [list(f) for f in chair.body.faces]
    grip = np.zeros(len(cv), dtype=bool)
    for f, tg in zip(chair.body.faces, chair.body.tags):
        if tg is not None and tg.split("#")[0] == "grip":
            grip[list(f)] = True
    fzone = [zone(cv[f].mean(0)) for f in cf]
    vzone = [zone(p) for p in cv]
    hinge = np.array(chair.pivots["hanger"]["pos"])
    sign = _sign()
    problems, touch, clashes, near = [], {}, {}, {}
    up_w = np.array((0.0, 0.0, 1.0))
    for side in (-1.0, 1.0):
        rows = {}
        for k, p in asset.pivots.items():
            if k.startswith("sheave_") and p["pos"][1] * side > 0:
                rows.setdefault(k.rstrip("0123456789"), []).append(p["pos"])
        if not rows:
            continue
        rope_w = asset.sockets["rope_right" if side > 0 else "rope_left"][2]
        # the row the rope rests on: a support row under it if there is one, else the hold-down row over it
        under = [k for k in rows if max(p[2] for p in rows[k]) < rope_w]
        key = under[0] if under else next(iter(rows))
        axles = sorted(rows[key])
        upd = 1.0 if under else -1.0
        line = [Vector((p[0], p[1], p[2] + upd * (sheave_r + rope_r))) for p in axles]
        d0, d1 = (line[0] - line[1]).normalized(), (line[-1] - line[-2]).normalized()
        route = [line[0] + d0 * reach] + line + [line[-1] + d1 * reach]
        if side < 0:   # outboard is right of the travel: +u on the +v rope, -u on the -v rope
            route.reverse()
        for a_, b_ in zip(route, route[1:]):
            dd = b_ - a_
            ln = dd.length
            if ln < 1e-9:
                continue
            t3 = dd.normalized()
            flat = Vector((t3.x, t3.y, 0.0)).normalized()
            nrm = Vector((-flat.y, flat.x, 0.0))
            up3 = nrm.cross(t3).normalized()
            if up3.z < 0:
                up3 = -up3
            steps = max(1, int(ln / step))
            for i in range(steps):
                o = np.array(a_ + dd * (i / steps))
                hw = o + hinge[1] * np.array(nrm) + hinge[2] * np.array(up3)
                xg = o[None, :] + np.outer(cv[:, 0], np.array(t3)) + np.outer(cv[:, 1], np.array(nrm)) + np.outer(cv[:, 2], np.array(up3))
                xh = (hw[None, :] + np.outer(cv[:, 0], np.array(flat)) + np.outer(cv[:, 1] - hinge[1], np.array(nrm))
                      + np.outer(cv[:, 2] - hinge[2], up_w))
                x = np.where(grip[:, None], xg, xh)
                lo, hi = x.min(0), x.max(0)
                ct = None
                for name, (tree, blo, bhi, centres) in trees.items():
                    if (hi < blo).any() or (lo > bhi).any():
                        continue
                    sheave = name.startswith("sheave_")
                    if ct is None:
                        ct = _tree(x, cf)
                    train = name.startswith(TRAIN)
                    bad = [(i_, j) for i_, j in ct.overlap(tree) if not (sheave and fzone[i_] in ("grip and arm", "wheels"))]
                    if bad:
                        k2 = (name.rstrip("0123456789"), ", ".join(sorted({fzone[i_] for i_, _ in bad})))
                        e = clashes.setdefault(k2, [0, centres[bad[0][1]]])
                        e[0] += 1
                        continue
                    for vi in np.nonzero(np.all((x >= blo) & (x <= bhi), axis=1))[0]:
                        r = tree.find_nearest(Vector(x[vi]), clear)
                        if r[0] is None:
                            continue
                        if sheave and vzone[vi] in ("grip and arm", "wheels"):
                            sd = sign * (Vector(x[vi]) - r[0]).dot(r[1])
                            touch[name] = min(touch.get(name, 1e9), sd)
                            continue
                        if train and vzone[vi] != "hanger" and vzone[vi] != "chair body":
                            continue
                        k2 = (name.rstrip("0123456789"), vzone[vi])
                        if k2 not in near or r[3] < near[k2][0]:
                            near[k2] = (r[3], tuple(r[0]))
    for (name, zones), (count, c) in sorted(clashes.items()):
        problems.append(f"fit: chair ({zones}) passes through {name} at {count} poses, e.g. at "
                        f"u {c[0] * 1000:.0f}, v {c[1] * 1000:.0f}, w {c[2] * 1000:.0f}")
    for (name, z), (d, c) in sorted(near.items()):
        problems.append(f"fit: chair ({z}) passes {name} {d * 1000:.1f} mm away (< {clear * 1000:.0f}) at "
                        f"u {c[0] * 1000:.0f}, v {c[1] * 1000:.0f}, w {c[2] * 1000:.0f}")
    for name, (tree, *_r) in trees.items():   # the sheaves must turn clear of the head's own steel (Gate 1)
        if name.startswith("sheave_") and "structure" in trees and trees["structure"][0].overlap(tree):
            problems.append(f"fit: {name} meets the head's structure")
    deep = min(touch.values(), default=0.0)
    if deep < -THUMP:
        problems.append(f"fit: the grip rides {-deep * 1000:.1f} mm into a sheave (> {THUMP * 1000:.0f})")
    return problems, {"clashes": len(clashes), "tight": len(near), "thump": round(-deep * 1000, 1)}
