"""Baked ambient occlusion into the "AO" colour attribute (R), per face corner.

For every corner, `rays` directions on the hemisphere around the corner's normal (a Fibonacci spiral, so the
result is deterministic) are cast against the whole asset at that LOD plus the ground plane (w = 0 for
terminals, the rope for chairs is ignored). Cosine-weighted hits within `reach` metres darken the corner
(scaled by `strength`, never below `floor`); rays start a little way in from the corner toward its face's centre.
The shader multiplies the result into its occlusion.
"""
import math

import bmesh
from mathutils import Vector
from mathutils.bvhtree import BVHTree

GOLDEN = math.pi * (3 - math.sqrt(5))


def hemisphere(n):
    """n unit directions around +Z, cosine-distributed (denser near the pole)."""
    out = []
    for i in range(n):
        z = math.sqrt(1 - (i + 0.5) / n)          # cosine-weighted
        r = math.sqrt(max(0.0, 1 - z * z))
        a = GOLDEN * i
        out.append(Vector((r * math.cos(a), r * math.sin(a), z)))
    return out


def frame_to(n):
    n = n.normalized()
    t = Vector((1, 0, 0)) if abs(n.x) < 0.9 else Vector((0, 1, 0))
    b1 = n.cross(t).normalized()
    b2 = n.cross(b1)
    return b1, b2, n


def bake(objects, rays=32, reach=1.0, ground=True, strength=0.8, floor=0.25, ghosts=()):
    """Bakes AO for every mesh object in `objects` (one LOD of one asset, world transforms applied). ghosts: heights
    (m) at which a cast-only copy of the whole LOD also occludes, as for a mast section stacked between two others;
    then every ray is also cast mirrored top to bottom, so a corner at the section's foot and its twin at the top
    sample mirrored directions and agree (the spiral itself isn't symmetric)."""
    mirror = bool(ghosts)
    verts, polys = [], []
    for dz in (0.0, *ghosts):
        for o in objects:
            m = o.matrix_world
            base = len(verts)
            verts.extend(m @ v.co + Vector((0.0, 0.0, dz)) for v in o.data.vertices)
            polys.extend([base + i for i in p.vertices] for p in o.data.polygons)
    if ground:
        s = 60.0
        base = len(verts)
        verts.extend([Vector((-s, -s, 0)), Vector((s, -s, 0)), Vector((s, s, 0)), Vector((-s, s, 0))])
        polys.append([base, base + 1, base + 2, base + 3])
    tree = BVHTree.FromPolygons(verts, polys, epsilon=0.0)
    dirs = hemisphere(rays)
    stats = []
    for o in objects:
        mesh = o.data
        mw = o.matrix_world
        nm = mw.to_3x3().inverted().transposed()
        mesh.calc_loop_triangles()
        corner_normals = [nm @ c.vector for c in mesh.corner_normals]
        # sample a little way in from each corner toward its face's centre: corners sit on edges where parts meet
        # or overlap, and rays starting there begin inside the neighbouring steel
        centre = {}
        for poly in mesh.polygons:
            c = mw @ poly.center
            for li in poly.loop_indices:
                centre[li] = c
        col = mesh.color_attributes.get("AO")
        values = [1.0] * (4 * len(mesh.loops))
        cache = {}
        for loop in mesh.loops:
            n = corner_normals[loop.index].normalized()
            p = mw @ mesh.vertices[loop.vertex_index].co
            p = p.lerp(centre[loop.index], 0.15)
            key = (round(p.x, 4), round(p.y, 4), round(p.z, 4), round(n.x, 3), round(n.y, 3), round(n.z, 3))
            if key in cache:
                ao = cache[key]
            else:
                b1, b2, nn = frame_to(n)
                origin = p + nn * 0.01
                blocked = 0.0
                cast = []
                for d in dirs:
                    w = b1 * d.x + b2 * d.y + nn * d.z
                    cast.append(w)
                    if mirror:
                        wm = Vector((w.x, w.y, -w.z))
                        cast.append(wm if wm.dot(nn) > 1e-6 else w)
                for w in cast:
                    hit = tree.ray_cast(origin, w, reach)
                    if hit[0] is not None:
                        blocked += 1.0 - (hit[3] / reach) * 0.5     # nearer hits occlude more
                ao = max(floor, 1.0 - strength * blocked / len(cast))
                cache[key] = ao
            i = loop.index * 4
            values[i] = values[i + 1] = values[i + 2] = ao
            values[i + 3] = 1.0
        col.data.foreach_set("color", values)
        stats.extend(values[0::4])
    return (min(stats), sum(stats) / len(stats)) if stats else (1.0, 1.0)


def tiling_error(objects, height, eps=1e-4):
    """The largest AO difference between a corner at a stacking section's foot (w = 0) and the matching corner at its
    top (w = height): same plan position and normal. 0 when the section tiles without a band (contract M5)."""
    # (corners on faces at the joint, like a stringer's end caps, face up or down and have no twin: not compared)
    feet, tops = {}, {}
    for o in objects:
        mesh = o.data
        mw = o.matrix_world
        col = mesh.color_attributes.get("AO")
        values = [0.0] * (4 * len(mesh.loops))
        col.data.foreach_get("color", values)
        for loop in mesh.loops:
            p = mw @ mesh.vertices[loop.vertex_index].co
            n = mesh.corner_normals[loop.index].vector
            key = (round(p.x, 4), round(p.y, 4), round(n.x, 3), round(n.y, 3), round(n.z, 3))
            if abs(p.z) < eps:
                feet.setdefault(key, []).append(values[loop.index * 4])
            elif abs(p.z - height) < eps:
                tops.setdefault(key, []).append(values[loop.index * 4])
    if not feet.keys() & tops.keys():
        raise ValueError("no corner at the section's foot has a twin at its top: nothing to compare")
    worst = 0.0
    for key in feet.keys() & tops.keys():
        worst = max(worst, abs(sum(feet[key]) / len(feet[key]) - sum(tops[key]) / len(tops[key])))
    return worst
