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


def bake(objects, rays=32, reach=1.0, ground=True, strength=0.8, floor=0.25):
    """Bakes AO for every mesh object in `objects` (one LOD of one asset, world transforms applied)."""
    verts, polys = [], []
    for o in objects:
        m = o.matrix_world
        base = len(verts)
        verts.extend(m @ v.co for v in o.data.vertices)
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
                for d in dirs:
                    w = b1 * d.x + b2 * d.y + nn * d.z
                    hit = tree.ray_cast(origin, w, reach)
                    if hit[0] is not None:
                        blocked += 1.0 - (hit[3] / reach) * 0.5     # nearer hits occlude more
                ao = max(floor, 1.0 - strength * blocked / len(dirs))
                cache[key] = ao
            i = loop.index * 4
            values[i] = values[i + 1] = values[i + 2] = ao
            values[i + 3] = 1.0
        col.data.foreach_set("color", values)
        stats.extend(values[0::4])
    return (min(stats), sum(stats) / len(stats)) if stats else (1.0, 1.0)
