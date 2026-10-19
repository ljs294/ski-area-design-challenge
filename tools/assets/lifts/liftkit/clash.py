"""The clash sweep (task P3-07, contract M1 "range"): how far each pivot can turn from its rest pose before one of its
parts meets something that doesn't move with it.

Works on one LOD0 asset after separate_flush, in Unity's frame (x = v, y = w, z = u), where the game turns a pivot
by Quaternion.AngleAxis(angle, axis): a pivot's subtree (its own part and every part under it) turns about its axle in
steps until any of its faces meets a face outside the subtree (the body: crossarm, lug plates; or a neighbour's
part), then the contact angle is bisected to `tol` degrees. Parts are built as overlapping solids, so a pair of
faces that already meet at rest never counts (a beam sliding along its lug plates keeps meeting them face to face).
Faces square to the axle (a plate's side, a beam's side flush on its lug plate) are left out: a turn about the axle
slides parts along them and can never carry anything through one without crossing another face first.
Each moving piece is also drawn in by `flush` (1 mm) at both ends along the axle, so a part standing flush beside
another across the axle (a beam between its lug plates) doesn't count as touching it.
Pins, axles, tubes and sockets turn inside what they pass through: they are tagged as their own pieces
("rocker_r1#pin", "#pin" for one in the body) and a piece pair with one of them never counts.
Wheels (sheaves, bullwheels) spin freely: their range is the whole turn.
"""
import math

from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

from . import export, frame


def unity(v_blender):
    u, v, w = frame.lift(v_blender)
    return Vector((v, w, u))


def pieces(a):
    """{piece tag: (verts, polys)} in Unity's frame: the body's tagged pieces (None: the body itself) and parts built on
    their own (named by their part)."""
    out = {}
    body, tagged = a.body.split(pieces=True)
    for tag, mb in list(tagged.items()) + [(None, body)] + list(a.parts.items()):
        if not mb.faces:
            continue
        out[tag] = ([unity(v) for v in mb.verts], [list(f) for f in mb.faces])
    return out


def part_of(tag):
    return None if tag is None else tag.split("#")[0] or None


def subtree(pivots, name):
    kids = {name}
    grew = True
    while grew:
        grew = False
        for k, p in pivots.items():
            if p.get("parent") in kids and k not in kids:
                kids.add(k)
                grew = True
    return kids


def _tree(entries):
    """One BVH over several pieces; returns (tree, polygon index -> piece tag)."""
    verts, polys, owner = [], [], []
    for tag, (vs, ps) in entries:
        base = len(verts)
        verts.extend(vs)
        polys.extend([[base + i for i in p] for p in ps])
        owner.extend([tag] * len(ps))
    return BVHTree.FromPolygons(verts, polys, epsilon=0.0), owner


def across(pcs, axis, cos_tol=0.9999):
    """The pieces without their faces square to `axis` (normal along it)."""
    from .mesh import newell
    out = {}
    for tag, (vs, ps) in pcs.items():
        keep = []
        for p in ps:
            n = newell([vs[i] for i in p])
            if n.length > 1e-12 and abs(n.normalized().dot(axis)) < cos_tol:
                keep.append(p)
        if keep:
            out[tag] = (vs, keep)
    return out


def drawn_in(vs, axis, flush):
    """A piece's vertices drawn in by `flush` toward its middle along `axis` at both ends."""
    t = [p.dot(axis) for p in vs]
    mid = (min(t) + max(t)) / 2
    return [p - axis * ((ti - mid) - math.copysign(max(abs(ti - mid) - flush, 0.0), ti - mid)) for p, ti in zip(vs, t)]


def sweep(a, limit=60.0, step=1.0, tol=0.1, flush=0.001):
    """{pivot: {"range": [min, max], "by": [what stops it at min, at max]}} for one LOD0 asset (already flushed)."""
    pcs = pieces(a)
    result = {}
    for name, piv in a.pivots.items():
        if export.spins(name, piv):
            result[name] = {"range": [-180.0, 180.0], "by": ["spins", "spins"]}
            continue
        u, v, w = piv["axis"]
        axis = Vector((v, w, u)).normalized()
        moving_parts = subtree(a.pivots, name)
        faces = across(pcs, axis)
        moving = [(t, (drawn_in(vs, axis, flush), ps)) for t, (vs, ps) in faces.items() if part_of(t) in moving_parts]
        static = [(t, pc) for t, pc in faces.items() if part_of(t) not in moving_parts]
        if not moving or not static:
            result[name] = {"range": [-limit, limit], "by": ["nothing in reach", "nothing in reach"]}
            continue
        st_tree, st_owner = _tree(static)
        centre = unity(frame.b(piv["pos"]))

        def pairs(deg):
            rot = Matrix.Rotation(math.radians(deg), 3, axis)
            turned = [(t, ([rot @ (p - centre) + centre for p in vs], ps)) for t, (vs, ps) in moving]
            tree, owner = _tree(turned)
            return owner, tree.overlap(st_tree)
        owner, at_rest = pairs(0.0)
        rest = set(at_rest)

        def hits(deg):
            owner, found = pairs(deg)
            return {(owner[i], st_owner[j]) for i, j in found if (i, j) not in rest
                    and "#" not in (owner[i] or "") and "#" not in (st_owner[j] or "")}
        ends, by = [], []
        for sgn in (1.0, -1.0):
            safe, stop, what = 0.0, None, "nothing in reach"
            deg = step
            while deg <= limit + 1e-9:
                new = hits(sgn * deg)
                if new:
                    stop, what = deg, sorted(f"{m} x {s}" for m, s in new)[0]
                    break
                safe = deg
                deg += step
            if stop is not None:
                lo, hi = safe, stop
                while hi - lo > tol:
                    mid = (lo + hi) / 2
                    if hits(sgn * mid):
                        hi = mid
                    else:
                        lo = mid
                safe = lo
            ends.append(round(sgn * safe, 2))
            by.append(what)
        result[name] = {"range": [ends[1], ends[0]], "by": [by[1], by[0]]}
    return result
