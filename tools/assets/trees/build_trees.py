"""Builds the species library in Blender, with no manual steps (0.5 §3, TR1, TR2, TR4).

    blender -b --factory-startup --python tools/assets/trees/build_trees.py -- [--out DIR] [--render DIR] [--species id,id]

For every species in species.json it makes 3 variants x 3 LODs, deterministically (seeded by species
and variant), and exports one FBX per variant with objects <id>_v<N>_LOD0..2 (Unity groups them into an
LODGroup) plus the generated textures. Trees are a branching skeleton with textured bark, dressed with
alpha-textured cards: needle sprays (conifers), leaf clusters and bare-twig silhouettes (deciduous).
Krummholz is a growth form, not a species: its three variants are a mat, a flag tree and a cushion, and
they reach downwind toward Blender -Y, which is the prefab's local +Z in Unity (see build_krummholz).
With --render it also writes review images.

Mesh data the tree shader reads (see README.md):
  Color (vertex)  R trunk sway weight, G branch flex, B per-branch phase, A leaf/needle flutter
  UV0             texture coordinates
  UV1 "Data"      x = snow mask (0..1), y = seasonal flag (1 = leaf that drops, 0.5 = leaf kept dry
                  through winter, 0 = permanent)
  UV2 "Season"    x = per-card random (0..1): the order leaves come out, turn and fall, and where snow
                  clumps on a card (every card has one)
                  y = height in the crown (0 = crown base, 1 = top)
  Submeshes       0 bark, 1 foliage or leaves, 2 bare twigs (deciduous), 3 winter-kept leaves (beech)
"""
import json
import math
import os
import random
import sys
import zlib

import bpy
import numpy as np
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import textures  # noqa: E402

UP = Vector((0.0, 0.0, 1.0))
GOLDEN = math.radians(137.508)
TEX = 512

# Detail per LOD. Conifer LOD1 and LOD2 are built from branch-cluster cards ("cluster": cards per branch):
# a whole branch of fronds on one card, so mid-distance crowns keep their foliage at a fraction of the
# triangles (the first version thinned the sprays instead and kept 22-49% of the crown: tree realism
# review). "internodal" keeps the short branches between whorls.
# "flare" lists the root-flare rings above the ground (fractions of the flare height, see trunk_rings).
LODS = [
    {"sides": 12, "branchSides": 4, "segments": 4, "spacing": 1.0, "cards": 1.0, "card": 1.0, "level2": 1.0, "internodal": True,
     "flare": [0.35, 1.0, 2.2]},
    {"sides": 6, "branchSides": 3, "segments": 3, "spacing": 1.0, "cards": 0.45, "card": 1.75, "level2": 0.34, "cluster": 2, "internodal": True,
     "flare": [1.0]},
    {"sides": 5, "branchSides": 0, "segments": 2, "spacing": 1.5, "cards": 0.0, "card": 1.0, "level2": 0.0, "cluster": 1, "internodal": False,
     "flare": []},
]

# Trunks start this far below the ground so they meet it on steep slopes too (a trunk ending 0.3 m down
# showed its cut end on the downhill side of 30-degree slopes: tree realism review).
BURIED = 1.0

BARK, FOLIAGE, TWIGS, KEPT = 0, 1, 2, 3

# Performance budgets per tree (triangles), enforced: the build fails if any variant exceeds them.
# Distances are the planned LOD switch points; beyond LOD2 an impostor (2 triangles) takes over.
# A 5 km site holds ~650,000 trees, so these, not the frame budget alone, keep the forest affordable.
BUDGET = [
    {"lod": "LOD0", "until_m": 30, "max_tris": 10000},
    {"lod": "LOD1", "until_m": 80, "max_tris": 2500},
    {"lod": "LOD2", "until_m": 150, "max_tris": 500},
]


def smoothstep(a, b, x):
    t = min(1.0, max(0.0, (x - a) / (b - a)))
    return t * t * (3 - 2 * t)


def lerp(a, b, t):
    return a + (b - a) * t


class MeshBuilder:
    """Collects vertices, faces and the per-vertex / per-corner data the tree shader needs."""

    def __init__(self):
        self.verts, self.wind, self.faces, self.mats, self.uv0, self.data, self.season = [], [], [], [], [], [], []

    def vert(self, p, wind):
        self.verts.append(tuple(p))
        self.wind.append(wind)
        return len(self.verts) - 1

    def normal_z(self, idx):
        pts = [Vector(self.verts[i]) for i in idx]
        n = Vector((0, 0, 0))
        for i in range(len(pts)):  # Newell normal
            a, b = pts[i], pts[(i + 1) % len(pts)]
            n += Vector(((a.y - b.y) * (a.z + b.z), (a.z - b.z) * (a.x + b.x), (a.x - b.x) * (a.y + b.y)))
        return n.normalized().z if n.length > 1e-12 else 0.0

    def face(self, idx, uvs, mat, snow, flag=0.0, season=(0.0, 0.0)):
        self.faces.append(tuple(idx))
        self.mats.append(mat)
        self.uv0.extend(uvs)
        self.data.extend([(snow, flag)] * len(idx))
        self.season.extend([season] * len(idx))

    def tube(self, pts, radii, sides, winds, snow_scale=0.6, v_scale=1.0, flare=None):
        """A tapered bark tube along pts; closes to a point when the last radius is 0. flare = (amount per
        ring, lobes, phase) swells rings into buttress roots: widest on the lobes, never narrower."""
        if sides < 3:
            return
        rings, frame, length = [], None, 0.0
        for i, p in enumerate(pts):
            t = (pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)]).normalized()
            if frame is None:
                side = t.cross(UP if abs(t.z) < 0.99 else Vector((1, 0, 0))).normalized()
            else:
                side = (frame - t * frame.dot(t)).normalized()
            frame = side
            fwd = t.cross(side).normalized()
            if i > 0:
                length += (p - pts[i - 1]).length
            if radii[i] <= 1e-6:
                ring = [self.vert(p, winds[i])] * sides
            else:
                ring = []
                for k in range(sides):
                    a = 2 * math.pi * k / sides
                    r = radii[i]
                    if flare and flare[0][i] > 0:
                        amount, lobes, phase = flare[0][i], flare[1], flare[2]
                        r *= 1 + amount * (0.45 + 0.55 * max(0.0, math.cos(lobes * a + phase)) ** 2)
                    ring.append(self.vert(p + (side * math.cos(a) + fwd * math.sin(a)) * r, winds[i]))
            rings.append((ring, length))
        circ = 2 * math.pi * max(radii[0], 0.02)
        for i in range(len(rings) - 1):
            (r0, l0), (r1, l1) = rings[i], rings[i + 1]
            for k in range(sides):
                k1 = (k + 1) % sides
                u0, u1, v0, v1 = k / sides, (k + 1) / sides, l0 / circ * v_scale, l1 / circ * v_scale
                idx = [r0[k], r0[k1], r1[k]] if r1[k] == r1[k1] else [r0[k], r0[k1], r1[k1], r1[k]]
                uvs = [(u0, v0), (u1, v0), (u0, v1)] if len(idx) == 3 else [(u0, v0), (u1, v0), (u1, v1), (u0, v1)]
                self.face(idx, uvs, BARK, smoothstep(0.2, 0.8, self.normal_z(idx)) * snow_scale)

    def card(self, base, direction, side, length, width, winds, mat, flag=0.0, snow_scale=1.0, season=(0.0, 0.0)):
        """An alpha-textured card: u runs from base along direction, v across side (centred). Cards always show
        the whole texture: the game's snow pattern (SnowPattern) expects the branch line at v = 0.5."""
        d, s = direction.normalized(), side.normalized()
        if d.cross(s).z < 0:  # keep the textured face up so snow lands on it
            s = -s
        a = base - s * width / 2
        b = base + d * length - s * width / 2
        c = base + d * length + s * width / 2
        e = base + s * width / 2
        w0, w1 = winds
        idx = [self.vert(a, w0), self.vert(b, w1), self.vert(c, w1), self.vert(e, w0)]
        snow = smoothstep(0.1, 0.7, self.normal_z(idx)) * snow_scale
        if season == (0.0, 0.0):
            # Cards without a season order still get their own random (snow clumps): a golden-ratio
            # sequence over the card count, so the tree's random stream (its shape) is unchanged.
            season = ((len(self.faces) * 0.6180339887) % 1.0, 0.0)
        self.face(idx, [(0, 0), (1, 0), (1, 1), (0, 1)], mat, snow, flag, season)

    def to_object(self, name, materials):
        mesh = bpy.data.meshes.new(name)
        mesh.from_pydata(self.verts, [], self.faces)
        mesh.update()
        for m in materials:
            mesh.materials.append(m)
        mesh.polygons.foreach_set("material_index", self.mats)
        mesh.polygons.foreach_set("use_smooth", [True] * len(self.faces))
        uv = mesh.uv_layers.new(name="UVMap")
        uv.data.foreach_set("uv", [c for p in self.uv0 for c in p])
        data = mesh.uv_layers.new(name="Data")
        data.data.foreach_set("uv", [c for p in self.data for c in p])
        season = mesh.uv_layers.new(name="Season")
        season.data.foreach_set("uv", [c for p in self.season for c in p])
        mesh.uv_layers.active_index = 0
        col = mesh.color_attributes.new(name="Wind", type="FLOAT_COLOR", domain="POINT")
        col.data.foreach_set("color", [c for w in self.wind for c in w])
        mesh.color_attributes.active_color_name = "Wind"
        obj = bpy.data.objects.new(name, mesh)
        bpy.context.scene.collection.objects.link(obj)
        return obj


def trunk_rings(sp, lod, height, trunk_r, above, seed_key):
    """Ring heights for a trunk and its root flare: one ring BURIED underground, then (if the LOD has
    flare rings) one at the ground and the flare rings, then the trunk's own rings (`above`: heights) clear
    of the flare. Returns (heights, flare amount per ring, (lobes, phase)). The flare swells the base into
    3-5 buttress lobes and fades over roughly three trunk radii (tree realism review: trunks met the
    ground as uniform pipes). LOD2 has no flare rings: its buried ring alone tapers into the trunk."""
    frng = random.Random(zlib.crc32(seed_key.encode()))   # its own stream, so branches are unchanged
    flare_h = max(0.4, 3.0 * trunk_r)
    amount = sp.get("flare", 0.6 if sp["form"] == "conifer" else 0.45) * frng.uniform(0.8, 1.2)
    low = [-BURIED] + ([0.0] + [flare_h * f for f in lod["flare"]] if lod["flare"] else [])
    clear = (low[-1] + 0.3) if lod["flare"] else 0.3
    zs = low + [z for z in above if z > clear]
    amounts = [amount * math.exp(-max(z, 0.0) / flare_h) if z <= low[-1] + 1e-6 else 0.0 for z in zs]
    return zs, amounts, (frng.randint(3, 5), frng.uniform(0, 2 * math.pi))


def horizontal_side(direction, roll):
    """A card's across-vector: horizontal and perpendicular to direction, rolled about it."""
    d = direction.normalized()
    s = d.cross(UP)
    s = s.normalized() if s.length > 1e-6 else Vector((1, 0, 0))
    n = s.cross(d).normalized()
    return s * math.cos(roll) + n * math.sin(roll)


# ---------------------------------------------------------------------------------------------
# Conifers: whorled branches dressed with needle-spray cards.

def conifer_branch(b, sp, rng, lod, base, d, length, elev, h, reach, parity, sway, trunk_r, floor=None, flex=1.0):
    """One conifer branch from base along the horizontal direction d, rising at elev (radians) and drooping
    by the species' droop: a bark tube dressed with needle sprays (LOD0), or one or two branch-cluster cards
    (LOD1-2). h is the height in the crown (0-1); reach is 1 for a whorl branch and less for an internodal
    one; parity alternates LOD2's cluster roll; floor keeps the branch above that height (krummholz); flex
    scales the branch-flex wind weight (G), since the shader bobs branch tips by a fixed distance."""
    phase = rng.random()
    segs = lod["segments"]
    pts = [base + d * length * (s / segs) + Vector((0, 0, math.tan(elev) * length * s / segs - sp["droop"] * length * (s / segs) ** 2))
           for s in range(segs + 1)]
    if floor is not None:
        for p in pts:
            p.z = max(p.z, floor)
    spray_len, spray_w = sp["spray"]

    def branch_point(t, pts=pts, segs=segs):
        f = t * segs
        i = min(int(f), segs - 1)
        return pts[i].lerp(pts[i + 1], f - i)

    def branch_tangent(t):
        return (branch_point(min(1, t + 0.02)) - branch_point(max(0, t - 0.02))).normalized()

    winds = [(sway(p.z), s / segs * flex, phase, 0.0) for s, p in enumerate(pts)]
    if lod.get("cluster"):
        # Mid-distance LODs: the branch as one or two branch-cluster cards (no branch tube: the
        # cluster hides it). A flat card along the branch, and on LOD1 a second one rolled up or
        # down so the crown stays full from the side.
        tan = branch_tangent(0.5)
        lo, hi, extra = sp.get("clusterSpan", (1.1, 3.2, 0.9))
        span = min(hi, max(lo, 0.55 * length + extra)) * (0.8 if reach < 1.0 else 1.0)
        # LOD2 has one card per branch: alternate a strong roll so the crown isn't edge-on from the side.
        roll = rng.uniform(-0.25, 0.25) + (0.0 if lod["cluster"] >= 2 else (0.6 if parity else -0.6))
        ends = [(sway(base.z), 0.0, phase, 0.4), (sway(pts[-1].z), 1.0 * flex, phase, 1.0)]
        b.card(base, tan, horizontal_side(tan, roll), length * 1.08, span, ends, FOLIAGE, snow_scale=0.4)
        if lod["cluster"] >= 2 and reach == 1.0:
            b.card(base + tan * length * 0.08, tan, horizontal_side(tan, roll + rng.choice((-1, 1)) * 1.15), length * 0.95, span * 0.8,
                   ends, FOLIAGE, snow_scale=0.25)
        return
    if lod["branchSides"] and reach == 1.0:
        # Conifer branches are mostly hidden by their sprays: a 3-sided tube through every
        # other point is enough (internodal branches get none).
        r0 = max(0.015, trunk_r * 0.22 * (1 - h * 0.6))
        keep = list(range(0, segs + 1, 2)) if segs > 2 else list(range(segs + 1))
        b.tube([pts[i] for i in keep], [r0 * (1 - i / segs * 0.85) for i in keep], 3, [winds[i] for i in keep], snow_scale=0.4)

    if lod["cards"] == 0:
        # LOD2: two broad crossed spray cards per branch keep the silhouette.
        tan = branch_tangent(0.5)
        for roll in (0.0, 0.9):
            b.card(base, tan, horizontal_side(tan, roll), length * 1.05, length * 0.75,
                   [(sway(base.z), 0.0, phase, 0.4), (sway(pts[-1].z), 1.0, phase, 1.0)], FOLIAGE)
        return

    # Sprays along the branch, alternating sides, plus one at the tip.
    n_sprays = max(2, int(length * sp["spraysPerMetre"] * sp.get("sprayDensity", 1.2) * lod["cards"]))
    size = lod["card"]
    start = 0.55 if sp.get("tufts") else 0.12
    for i in range(n_sprays):
        t = lerp(start, 0.95, (i + rng.random() * 0.5) / n_sprays)
        p = branch_point(t)
        tan = branch_tangent(t)
        side = 1 if i % 2 else -1
        lat = tan.cross(UP)
        lat = lat.normalized() if lat.length > 1e-6 else Vector((1, 0, 0))
        a = math.radians(rng.uniform(25, 55)) * side
        dirn = (tan * math.cos(a) + lat * math.sin(a)).normalized()
        dirn.z -= sp["droop"] * 0.3
        if "sprayLift" in sp:
            dirn.z += sp["sprayLift"]   # upturned sprays (noble fir: stiff, level branches, tips turned up)
        ln = spray_len * 1.5 * size * (1 - 0.3 * t) * rng.uniform(0.85, 1.15)
        wd = spray_w * 1.6 * size * (1 - 0.25 * t)
        roll = rng.uniform(-0.6, 0.6)
        wn = [(sway(p.z), t * flex, phase, 0.5), (sway(p.z), min(1.0, t + 0.2) * flex, phase, 1.0)]
        b.card(p, dirn, horizontal_side(dirn, roll), ln, wd, wn, FOLIAGE, snow_scale=0.38)
        if sp.get("crossed") or sp.get("tufts"):
            b.card(p, dirn, horizontal_side(dirn, roll + 1.4), ln * 0.9, wd * 0.9, wn, FOLIAGE, snow_scale=0.25)
        elif lod["cards"] >= 1.0 and i % 2 == 0:
            # A tilted second spray every other station gives flat sprays volume from the side.
            b.card(p, dirn, horizontal_side(dirn, roll + rng.choice((-1, 1)) * 1.1), ln * 0.75, wd * 0.8, wn, FOLIAGE, snow_scale=0.2)
    tip = pts[-1]
    tan = branch_tangent(0.97)
    b.card(tip - tan * spray_len * 0.3, tan, horizontal_side(tan, 0.0), spray_len * size, spray_w * size,
           [(sway(tip.z), 0.9 * flex, phase, 0.8), (sway(tip.z), 1.0 * flex, phase, 1.0)], FOLIAGE)


def build_conifer(sp, rng, lod, height):
    b = MeshBuilder()
    trunk_r = sp["trunkRadius"] * height
    phase_trunk = rng.random()
    nod = math.radians(sp["nod"] * rng.uniform(0.7, 1.1))
    nod_dir = rng.uniform(0, 2 * math.pi)
    lean = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), 0)) * 0.01
    # The nodding leader bends over the top nod_span of the trunk, nod_reach x height sideways at most
    # (hemlocks; western hemlock's leader droops further and over a longer stretch).
    nod_from, nod_span, nod_reach = sp.get("nodShape", (0.92, 0.08, 0.05))

    def trunk_point(h):
        p = Vector((0, 0, h * height)) + lean * h * height
        if nod > 0 and h > nod_from:
            s = (h - nod_from) / nod_span
            bend = nod * s * s
            p += Vector((math.cos(nod_dir), math.sin(nod_dir), 0)) * math.sin(bend) * nod_reach * height * s
            p.z -= (1 - math.cos(bend)) * nod_reach * height * s
        return p

    def sway(z):
        return min(1.0, max(0.0, z / height)) ** 2

    n = 12
    zs, amounts, (lobes, lobe_phase) = trunk_rings(sp, lod, height, trunk_r, [height * i / n for i in range(1, n + 1)],
                                                  f"{sp['id']}:{height:.4f}")
    tpts = [trunk_point(z / height) for z in zs]
    b.tube(tpts, [trunk_r * max(0.0, 1 - max(z, 0.0) / height) ** 1.1 + (0.012 if z < height - 1e-6 else 0) for z in zs], lod["sides"],
           [(sway(p.z), 0.0, phase_trunk, 0.0) for p in tpts], snow_scale=0.3, flare=(amounts, lobes, lobe_phase))

    crown_base = sp["crownBase"] * height
    r_max = sp["crownRadius"] * height * rng.uniform(0.9, 1.1)
    exponent, age = sp["crownExponent"], 0.0
    if "roundTop" in sp:
        # Old trees round off at the top (noble fir): the taller the variant, the more domed its crown.
        age = smoothstep(0.3, 1.0, (height - sp["height"][0]) / (sp["height"][1] - sp["height"][0]))
        exponent = lerp(exponent, sp["roundTop"], age)

    def crown_radius(z):
        h = (z - crown_base) / (height - crown_base)
        return r_max * max(0.0, 1 - h) ** exponent * (0.6 + 0.4 * smoothstep(0.0, 0.08, h))

    spacing = sp["whorlSpacing"] * lod["spacing"]
    z, k = crown_base, 0
    lo, hi = sp["branchesPerWhorl"]
    top_z = height - max(0.8, 0.05 * height)
    # Main whorls, plus shorter internodal branches halfway between them (real firs and spruces
    # carry both), so the crown has no see-through gaps between tiers.
    tiers = []
    while z < top_z:
        tiers.append((z, rng.randint(lo, hi), 1.0))
        if lod.get("internodal") and sp.get("internodal", True):
            tiers.append((z + spacing * 0.5, max(2, (lo + hi) // 3), 0.7))
        z += spacing * rng.uniform(0.85, 1.15)
    for z, count, reach in tiers:
        if z >= top_z:
            continue
        h = (z - crown_base) / (height - crown_base)
        for j in range(count):
            if rng.random() < 0.08:
                continue
            az = k * GOLDEN + j * 2 * math.pi / count + rng.uniform(-0.25, 0.25) + (0.0 if reach == 1.0 else math.pi / count)
            length = max(0.35, crown_radius(z) * rng.uniform(0.85, 1.1) * reach)
            elev = math.radians(lerp(sp["branchElevation"][1], sp["branchElevation"][0], h))
            conifer_branch(b, sp, rng, lod, trunk_point(z / height), Vector((math.cos(az), math.sin(az), 0)), length, elev, h, reach,
                           (k + j) % 2, sway, trunk_r)
        k += 1

    # Leader: upright sprays to the tip so the top is never bare.
    spray_w = sp["spray"][1]
    if sp.get("leaderCurve"):
        # A drooping leader (western hemlock): crossed sprays follow the bent trunk in short steps.
        h0 = (top_z - 0.3) / height
        steps = 4
        for s in range(steps):
            p0, p1 = trunk_point(lerp(h0, 1.0, s / steps)), trunk_point(lerp(h0, 1.0, (s + 1) / steps))
            seg = p1 - p0
            for i in range(3 if lod["cards"] else 2):
                b.card(p0, seg, horizontal_side(seg, i * math.pi / 3), seg.length + 0.25, spray_w * (0.9 - 0.25 * s / steps),
                       [(sway(p0.z), 0.3 * s / steps, phase_trunk, 0.6), (1.0, 0.3 * (s + 1) / steps, phase_trunk, 1.0)], FOLIAGE,
                       snow_scale=0.6)
        return b
    base = trunk_point((top_z - 0.3) / height)
    up = (trunk_point(1.0) - base).normalized()
    leader = 0.4 if "roundTop" not in sp else 0.4 * (1 - age)   # an old, round-topped crown has no spire
    for i in range(3 if lod["cards"] else 2):
        a = i * 2 * math.pi / 3
        b.card(base, up, Vector((math.cos(a), math.sin(a), 0)), (trunk_point(1.0) - base).length + leader, spray_w * 0.9,
               [(sway(base.z), 0.0, phase_trunk, 0.6), (1.0, 0.3, phase_trunk, 1.0)], FOLIAGE, snow_scale=0.6)
    return b


# ---------------------------------------------------------------------------------------------
# Krummholz: stunted, wind-shaped conifers just below the treeline.

# Downwind, and across the wind, in Blender. The FBX export (axis_forward -Z, axis_up Y,
# bake_space_transform) and Unity's import turn Blender (x, y, z) into Unity (-x, z, -y), the mapping
# the lift pipeline measured (tools/assets/lifts/liftkit/frame.py). So Blender -Y is the prefab's local
# +Z, which the placement code turns to face downwind (ENE, away from the game's WSW wind).
DOWNWIND = Vector((0.0, -1.0, 0.0))
ACROSS = Vector((1.0, 0.0, 0.0))


def build_krummholz(sp, rng, lod, height, form):
    """Krummholz in one of three shapes (form["shape"]), each reaching downwind (DOWNWIND):
      mat      a low, dense teardrop on the ground: a flat top at the snow surface that slopes down to a
               long downwind tail, and a short, steep upwind face with dead stubs
      flag     a stem with branches only on its downwind side, a bare, wind-blasted stretch just above the
               snow, a dense skirt at its foot and a dead spike at the top
      cushion  a rounded dome, a little longer downwind than upwind
    form["reach"] is the branch length at the foot of the foliage (downwind, across the wind, upwind),
    form["stem"] the stem's (radius, length as a share of the height, lean downwind in degrees) and
    form["sway"] scales the trunk-sway and branch-flex wind weights (R and G): krummholz is stiff, a mat most of
    all, and the shader moves branch tips by a fixed distance (12 cm at G = 1), a sixth of a mat's height."""
    b = MeshBuilder()
    shape = form["shape"]
    stem_r, stem_share, stem_lean = form["stem"]
    lean = math.tan(math.radians(stem_lean))
    rd, rs, ru = form["reach"]
    phase_stem = rng.random()
    flex = form["sway"]

    def sway(z):
        return min(1.0, max(0.0, z / height)) ** 2 * form["sway"]

    def toward(phi):
        """The horizontal direction phi radians round from downwind."""
        return (DOWNWIND * math.cos(phi) + ACROSS * math.sin(phi)).normalized()

    def stem_point(z):
        return Vector((0.0, 0.0, z)) + DOWNWIND * lean * max(0.0, z)

    stem_top = height * stem_share
    # Only the flare rings that fit in the lower half of a short stem.
    flare = [f for f in lod["flare"] if f * max(0.4, 3.0 * stem_r) < 0.5 * stem_top]
    zs, amounts, (lobes, lobe_phase) = trunk_rings(sp, dict(lod, flare=flare), stem_top, stem_r, [stem_top * i / 6 for i in range(1, 7)],
                                                  f"{sp['id']}:{height:.4f}")
    spts = [stem_point(z) for z in zs]
    b.tube(spts, [stem_r * max(0.0, 1 - max(z, 0.0) / stem_top) ** 0.8 + (0.006 if z < stem_top - 1e-6 else 0) for z in zs],
           max(5, lod["sides"] - 4), [(sway(p.z), 0.0, phase_stem, 0.0) for p in spts], snow_scale=0.3, flare=(amounts, lobes, lobe_phase))

    def reach(t, phi):
        """Branch length at height t (0-1) in the skirt, phi radians round from downwind."""
        if shape == "cushion":
            fd = fs = fu = max(0.0, 1 - t ** 2.2) ** 0.5
        else:
            # A flat top at the snow surface, sloping down to the downwind tail; a steep upwind face.
            fd, fs, fu = 1 - 0.6 * t * t, 1 - 0.5 * t * t, 1 - 0.4 * t
        side = rs * fs
        c = math.cos(phi)
        return side + (rd * fd - side) * max(0.0, c) ** 1.5 + (ru * fu - side) * max(0.0, -c) ** 1.5

    def dead_stub(z, phi, length):
        """A bare, wind-killed stub on the upwind side (LOD0 and LOD1)."""
        if not lod["branchSides"]:
            return
        base = stem_point(z)
        d = (toward(phi) + UP * 0.25).normalized()
        pts = [base, base + d * length * 0.5 + UP * 0.03, base + d * length]
        b.tube(pts, [0.018, 0.011, 0.0], 3, [(sway(p.z), s / 2 * flex, phase_stem, 0.0) for s, p in enumerate(pts)], snow_scale=0.4)

    # The skirt: tiers of branches all round the stem, long downwind and short upwind. It is the whole
    # mat or cushion; on a flag tree it is the dense foot that the snowpack shelters.
    skirt = height * 0.88 if shape != "flag" else form["skirt"]
    spacing = sp["whorlSpacing"] * lod["spacing"]
    lo, hi = sp["branchesPerWhorl"]
    z, k = 0.06, 0
    while z < skirt:
        t = z / skirt
        count = rng.randint(lo, hi)
        for j in range(count):
            u = (j + rng.random() * 0.6) / count
            phi = (k * GOLDEN + u * 2 * math.pi) % (2 * math.pi) - math.pi
            phi -= 0.3 * math.sin(phi)   # crowd the branches downwind, where they are longest
            if rng.random() < 0.08:
                continue
            length = max(0.25, reach(t, phi) * rng.uniform(0.85, 1.1))
            if abs(phi) > 2.4 and rng.random() < 0.45:
                dead_stub(z, phi, min(length, 0.4))
                continue
            if shape == "cushion":
                elev = math.radians(lerp(-6.0, 24.0, t * t))
            else:
                elev = math.radians(lerp(2.0, 8.0, t))
            conifer_branch(b, sp, rng, lod, stem_point(z), toward(phi), length, elev, t, 1.0, (k + j) % 2, sway, stem_r, floor=0.04,
                           flex=flex)
        z += spacing * rng.uniform(0.85, 1.15)
        k += 1

    if shape == "flag":
        # Above a bare, wind-blasted stretch just over the snow, branches grow only on the downwind side.
        z = skirt + 0.35
        flag_top = height - 0.3
        while z < flag_top:
            t = (z - skirt) / (flag_top - skirt)
            count = 2 if rng.random() < 0.6 or not lod["cards"] else 3
            for j in range(count):
                phi = max(-1.2, min(1.2, rng.gauss(0.0, 0.45)))
                length = form["flag"] * (0.55 + 0.45 * math.sin(math.pi * min(1.0, t * 1.1 + 0.1))) * (1 - 0.35 * t) * rng.uniform(0.8, 1.15)
                elev = math.radians(rng.uniform(-8.0, 6.0))
                conifer_branch(b, sp, rng, lod, stem_point(z), toward(phi), length, elev, 0.5 + 0.5 * t, 1.0, (k + j) % 2, sway, stem_r,
                               flex=flex)
            if rng.random() < 0.55:
                dead_stub(z + 0.05, math.pi + rng.uniform(-0.6, 0.6), rng.uniform(0.15, 0.4))
            z += spacing * 2.0 * rng.uniform(0.85, 1.15)
            k += 1
        # A small tuft just under the dead spike at the top, also downwind.
        for phi in (-0.3, 0.35):
            conifer_branch(b, sp, rng, lod, stem_point(flag_top), toward(phi), 0.5, math.radians(10.0), 1.0, 1.0, k % 2, sway, stem_r,
                           flex=flex)
    return b


# ---------------------------------------------------------------------------------------------
# Deciduous: a recursive branching skeleton with twig and leaf cards.

def crown_envelope(shape, h):
    """Relative crown radius at relative crown height h (0 at the crown base, 1 at the top)."""
    h = min(1.0, max(0.0, h))
    if shape == "narrow":
        return math.sin(math.pi * min(1.0, h * 0.95 + 0.05)) ** 0.8
    if shape == "oval":
        return math.sin(math.pi * min(1.0, h * 0.9 + 0.1)) ** 0.55
    if shape == "spreading":
        return (math.sin(math.pi * min(1.0, h * 0.8 + 0.2)) ** 0.4) * (1.1 - 0.3 * h)
    if shape == "irregular":
        return math.sin(math.pi * min(1.0, h * 0.9 + 0.1)) ** 0.6 * (0.85 + 0.15 * math.sin(h * 11))
    raise ValueError(shape)


def build_deciduous(sp, rng, lod, height):
    b = MeshBuilder()
    trunk_r = sp["trunkRadius"] * height
    crown_base = sp["crownBase"] * height
    r_crown = sp["crownRadius"] * height

    def sway(z):
        return min(1.0, max(0.0, z / height)) ** 2

    def fit(tip):
        """How much to shorten a branch whose tip leaves the crown envelope."""
        h = (tip.z - crown_base) / max(1.0, height - crown_base)
        allowed = r_crown * crown_envelope(sp["crownShape"], h) + 0.3
        d = math.hypot(tip.x, tip.y)
        return max(0.35, allowed / d) if d > allowed and d > 1e-3 else 1.0

    def curve(start, direction, length, upward, segs):
        pts = [start]
        d = direction.normalized()
        step = length / segs
        for _ in range(segs):
            d = (d + UP * upward * 0.35 + Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-0.5, 0.5))) * 0.12).normalized()
            pts.append(pts[-1] + d * step)
        return pts

    def along(pts, t):
        f = t * (len(pts) - 1)
        i = min(int(f), len(pts) - 2)
        return pts[i].lerp(pts[i + 1], f - i), (pts[i + 1] - pts[i]).normalized()

    def dress(pts, phase):
        """Twig cards (winter) and leaf cards (summer and autumn) along a small branch."""
        branch_len = sum((pts[i + 1] - pts[i]).length for i in range(len(pts) - 1))
        if branch_len < 0.6:
            return  # crowded stubs near the top would pile cards into dark clumps
        scale = min(1.0, branch_len / 1.8)
        count = max(1, int(4 * lod["cards"] * scale + 0.5))
        for i in range(count):
            p, tan = along(pts, lerp(0.35, 1.0, (i + rng.random() * 0.5) / count))
            a = rng.uniform(-0.9, 0.9)
            lat = tan.cross(UP)
            lat = lat.normalized() if lat.length > 1e-6 else Vector((1, 0, 0))
            dirn = (tan * math.cos(a) + lat * math.sin(a) + UP * rng.uniform(-0.1, 0.4)).normalized()
            size = lod["card"] * rng.uniform(0.9, 1.25) * (0.55 + 0.45 * scale)
            if i % 2 == 0:
                b.card(p, dirn, horizontal_side(dirn, rng.uniform(-0.8, 0.8)), 1.5 * size, 1.2 * size,
                       [(sway(p.z), 0.7, phase, 0.6), (sway(p.z), 1.0, phase, 1.0)], TWIGS, snow_scale=0.35)
            leaves = 2 if lod["cards"] >= 1.0 else 1   # LOD1: one larger leaf card per twig
            for _ in range(leaves):
                ld = (dirn + Vector((rng.uniform(-0.6, 0.6), rng.uniform(-0.6, 0.6), rng.uniform(-0.2, 0.3)))).normalized()
                kept = "marcescent" in sp and p.z < crown_base + 0.45 * (height - crown_base) and rng.random() < 0.7
                rel = min(1.0, max(0.0, (p.z - crown_base) / (height - crown_base)))
                b.card(p, ld, horizontal_side(ld, rng.uniform(-0.6, 0.6)), 1.1 * size * (2 / leaves) ** 0.5, 1.0 * size * (2 / leaves) ** 0.5,
                       [(sway(p.z), 0.8, phase, 1.0), (sway(p.z), 1.0, phase, 1.0)],
                       KEPT if kept else FOLIAGE, flag=0.5 if kept else 1.0, snow_scale=0.0, season=(rng.random(), rel))

    stems = rng.randint(*sp["stems"])
    for stem in range(stems):
        # Clumped stems (birch) lean apart a little.
        ring = Vector((math.cos(stem * 2.4), math.sin(stem * 2.4), 0))
        offset = Vector((0, 0, 0)) if stem == 0 else ring * 0.35
        lean = UP if stem == 0 else (ring * 0.18 + UP).normalized()
        s_height = height * (1.0 if stem == 0 else rng.uniform(0.8, 0.95))
        split = 0.55 if sp["leaders"] > 1 else 0.92
        trunk = curve(offset - Vector((0, 0, 0.3)), lean, s_height * split + 0.3, 1.0, 8)
        tr = trunk_r * (1.0 if stem == 0 else 0.75)
        ph = rng.random()
        rings = range(0, 9, 1 if lod["cards"] else 2)  # LOD2: half the trunk rings
        zs, amounts, (lobes, lobe_phase) = trunk_rings(sp, lod, height, tr, [trunk[i].z for i in rings if i > 0],
                                                      f"{sp['id']}:{height:.4f}:{stem}")

        def trunk_at(z, trunk=trunk):
            """The point on the trunk polyline at height z (extended straight down below its base)."""
            if z <= trunk[0].z:
                return trunk[0] + (trunk[1] - trunk[0]).normalized() * ((z - trunk[0].z) / max(1e-3, (trunk[1] - trunk[0]).normalized().z))
            for i in range(len(trunk) - 1):
                if trunk[i + 1].z >= z:
                    return trunk[i].lerp(trunk[i + 1], (z - trunk[i].z) / max(1e-6, trunk[i + 1].z - trunk[i].z))
            return trunk[-1]

        span = trunk[-1].z - trunk[0].z
        tube_pts = [trunk_at(z) for z in zs]
        b.tube(tube_pts, [tr * (1 - 0.5 * min(1.0, max(0.0, (z - trunk[0].z) / span))) for z in zs], lod["sides"],
               [(sway(p.z), 0.0, ph, 0.0) for p in tube_pts], snow_scale=0.2, v_scale=0.5, flare=(amounts, lobes, lobe_phase))

        # Leaders continue the trunk (maples, beech and yellow birch fork into several).
        leaders = []
        for li in range(max(1, sp["leaders"])):
            if sp["leaders"] == 1:
                d = lean
            else:
                a = li * 2 * math.pi / sp["leaders"] + rng.uniform(-0.4, 0.4)
                d = (UP + Vector((math.cos(a), math.sin(a), 0)) * rng.uniform(0.35, 0.6)).normalized()
            ln = s_height * (1 - split) * rng.uniform(0.9, 1.05) + (0 if sp["leaders"] == 1 else s_height * 0.05)
            pts = curve(trunk[-1], d, ln, 0.6, 5)
            r0 = tr * 0.5 * (0.8 if sp["leaders"] > 1 else 1)
            b.tube(pts, [r0 * (1 - i / 5) + (0.01 if i < 5 else 0) for i in range(6)], max(3, lod["sides"] - 2),
                   [(sway(p.z), 0.3 * i / 5, ph, 0.0) for i, p in enumerate(pts)])
            leaders.append(pts)

        # Primaries: the lower half from the trunk above the crown base, the rest from the leaders.
        lo, hi = sp["primaries"]
        count = max(4, int(rng.randint(lo, hi) * 1.4 / stems ** 0.8 * (1.0 if lod["cards"] else 0.4)))
        for i in range(count):
            if i < count // 2 or not leaders:
                h = lerp(crown_base, trunk[-1].z, (i + rng.random() * 0.6) / max(1, count // 2))
                t = min(1.0, max(0.0, (h - trunk[0].z) / (trunk[-1].z - trunk[0].z)))
                parent, pr = trunk, tr * 0.8
            else:
                parent, pr = leaders[i % len(leaders)], tr * 0.45
                t = rng.uniform(0.2, 0.85)
            start, _ = along(parent, t)
            if start.z < crown_base - 0.5:
                continue
            az = i * GOLDEN + rng.uniform(-0.3, 0.3)
            ang = math.radians(rng.uniform(*sp["primaryAngle"]))
            d = Vector((math.cos(az) * math.sin(ang), math.sin(az) * math.sin(ang), math.cos(ang)))
            rel = (start.z - crown_base) / max(1.0, height - crown_base)
            length = height * sp["primaryLength"] * rng.uniform(0.8, 1.2) * (1.1 - 0.55 * rel)
            segs = lod["segments"] + 1
            pts = curve(start, d, length, sp["upward"], segs)
            k_fit = fit(pts[-1])
            if k_fit < 1:
                length *= k_fit
                pts = curve(start, d, length, sp["upward"], segs)
            phase = rng.random()
            r0 = max(0.02, pr * 0.55 * (1 - 0.5 * rel))
            b.tube(pts, [r0 * (1 - 0.8 * s / segs) for s in range(segs + 1)], max(3, lod["branchSides"]),
                   [(sway(p.z), s / segs, phase, 0.0) for s, p in enumerate(pts)])

            n2 = int(rng.randint(*sp["secondaries"]) * 1.6 * lod["level2"])
            if n2 == 0:
                # LOD2: large crossed twig cards and a leaf card straight on the primaries.
                mid, tan = along(pts, 0.35)
                for roll in (0.3, 1.6):
                    b.card(mid, tan, horizontal_side(tan, roll), length * 0.95, length * 0.85,
                           [(sway(mid.z), 0.6, phase, 0.8), (sway(pts[-1].z), 1.0, phase, 1.0)], TWIGS, snow_scale=0.3)
                kept = "marcescent" in sp and mid.z < crown_base + 0.45 * (height - crown_base)
                b.card(mid, tan, horizontal_side(tan, 1.2), length * 0.8, length * 0.7,
                       [(sway(mid.z), 0.6, phase, 1.0), (sway(pts[-1].z), 1.0, phase, 1.0)], KEPT if kept else FOLIAGE,
                       flag=0.5 if kept else 1.0, snow_scale=0.0, season=(rng.random(), 0.5))
                continue
            for j in range(n2):
                tj = lerp(0.3, 0.95, (j + rng.random() * 0.5) / n2)
                s0, tan = along(pts, tj)
                a2 = rng.uniform(0.5, 0.9) * (1 if j % 2 else -1)
                lat = tan.cross(UP)
                lat = lat.normalized() if lat.length > 1e-6 else Vector((1, 0, 0))
                d2 = (tan * math.cos(a2) + lat * math.sin(a2) + UP * sp["upward"] * 0.5).normalized()
                p2 = curve(s0, d2, length * rng.uniform(0.3, 0.45) * (1 - 0.4 * tj), sp["upward"], 3)
                if lod["branchSides"]:
                    b.tube(p2, [r0 * 0.4, r0 * 0.25, r0 * 0.12, 0.0], 3,
                           [(sway(p.z), tj + (1 - tj) * s / 3, phase, 0.2) for s, p in enumerate(p2)])
                dress(p2, phase)
    return b


# ---------------------------------------------------------------------------------------------
# Materials and textures

def image(name, pixels, out_dir, data=False):
    """A Blender image from an RGBA numpy array (row 0 = top), saved as PNG beside the FBX files. data:
    not a colour (a normal map)."""
    img = bpy.data.images.get(name)
    if img is None:
        n = pixels.shape[0]
        img = bpy.data.images.new(name, n, n, alpha=True)
        if data:
            img.colorspace_settings.name = "Non-Color"
        pixels = pixels.copy()
        clear = pixels[..., 3] < 0.02
        if clear.any() and (~clear).any():
            # Bleed colour into transparent pixels so filtering never pulls in black fringes.
            pixels[clear, :3] = pixels[~clear, :3].mean(axis=0)
        img.pixels.foreach_set(np.flipud(pixels).astype(np.float32).ravel())
        tex_dir = os.path.join(out_dir, "textures")
        os.makedirs(tex_dir, exist_ok=True)
        img.filepath_raw = os.path.join(tex_dir, name + ".png")
        img.file_format = "PNG"
        img.save()
    return img


def material(name, img, snow_load=1.0, cutout=False, leaf=False, img2=None, normal=None):
    """Preview material: texture, snow from UV1.x on up-facing fronts, winter hide from UV1.y, and an
    optional tangent-space normal map (bark)."""
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    link = nt.links.new

    def node(kind, **props):
        n = nt.nodes.new(kind)
        for k, v in props.items():
            setattr(n, k, v)
        return n

    def math_node(op, a=None, b=None, clamp=False):
        n = node("ShaderNodeMath", operation=op, use_clamp=clamp)
        for i, v in enumerate((a, b)):
            if isinstance(v, (int, float)):
                n.inputs[i].default_value = v
            elif v is not None:
                link(v, n.inputs[i])
        return n.outputs[0]

    out = node("ShaderNodeOutputMaterial")
    bsdf = node("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Roughness"].default_value = 0.8
    tex = node("ShaderNodeTexImage", name="Tex")
    tex.image = img
    data = node("ShaderNodeUVMap", uv_map="Data")
    sep = node("ShaderNodeSeparateXYZ")
    link(data.outputs["UV"], sep.inputs[0])
    load = node("ShaderNodeValue", name="SnowLoad")
    load.outputs[0].default_value = snow_load
    # The game's snow (TreeInstanced.shader): capacity x load x saturate(up x 1.6 + 0.1), where up is the
    # viewed side's facing (Cycles turns the normal toward the viewer, so back faces get none), and on
    # cutout cards SnowPattern: snow along the branch line (v = 0.5), wider toward the branch, in clumps,
    # with the fringes left green. The first previews snowed whole cards, which hid snow bugs.
    shading_normal = node("ShaderNodeSeparateXYZ")
    link(node("ShaderNodeNewGeometry").outputs["Normal"], shading_normal.inputs[0])
    facing = math_node("ADD", math_node("MULTIPLY", shading_normal.outputs["Z"], 1.6), 0.1, clamp=True)
    snow = math_node("MULTIPLY", math_node("MULTIPLY", sep.outputs["X"], load.outputs[0]), facing)
    if cutout:
        uv = node("ShaderNodeSeparateXYZ")
        link(node("ShaderNodeUVMap", uv_map="UVMap").outputs["UV"], uv.inputs[0])
        seed = node("ShaderNodeSeparateXYZ")
        link(node("ShaderNodeUVMap", uv_map="Season").outputs["UV"], seed.inputs[0])
        core = math_node("SUBTRACT", 1.0, math_node("MULTIPLY", math_node("ABSOLUTE", math_node("SUBTRACT", uv.outputs["Y"], 0.5)), 3.0))
        along = math_node("SUBTRACT", 1.1, math_node("MULTIPLY", uv.outputs["X"], 0.55))
        at = node("ShaderNodeCombineXYZ")
        link(math_node("ADD", math_node("MULTIPLY", uv.outputs["X"], 9.0), math_node("MULTIPLY", seed.outputs["X"], 41.0)), at.inputs[0])
        link(math_node("ADD", math_node("MULTIPLY", uv.outputs["Y"], 7.0), math_node("MULTIPLY", seed.outputs["X"], 41.0)), at.inputs[1])
        clumps = node("ShaderNodeTexNoise", noise_dimensions="2D")
        clumps.inputs["Scale"].default_value = 1.0
        clumps.inputs["Detail"].default_value = 0.0
        link(at.outputs["Vector"], clumps.inputs["Vector"])
        pattern = node("ShaderNodeMapRange", interpolation_type="SMOOTHSTEP")
        pattern.inputs["From Min"].default_value, pattern.inputs["From Max"].default_value = 0.3, 0.5
        link(math_node("ADD", math_node("MULTIPLY", core, along), math_node("MULTIPLY", math_node("SUBTRACT", clumps.outputs["Fac"], 0.5), 0.45)),
             pattern.inputs["Value"])
        snow = math_node("MULTIPLY", snow, pattern.outputs["Result"])
    snow = math_node("MINIMUM", snow, 1.0)
    mix = node("ShaderNodeMix", data_type="RGBA")
    mix.inputs["B"].default_value = (0.90, 0.93, 0.95, 1)
    link(tex.outputs["Color"], mix.inputs["A"])
    link(snow, mix.inputs["Factor"])
    link(mix.outputs["Result"], bsdf.inputs["Base Color"])
    if normal is not None:
        ntex = node("ShaderNodeTexImage", name="Normal")
        ntex.image = normal
        nmap = node("ShaderNodeNormalMap", uv_map="UVMap")
        link(ntex.outputs["Color"], nmap.inputs["Color"])
        link(nmap.outputs["Normal"], bsdf.inputs["Normal"])

    surface = bsdf.outputs["BSDF"]
    if leaf:
        # Thin leaves and needles let some light through.
        trans = node("ShaderNodeBsdfTranslucent")
        link(mix.outputs["Result"], trans.inputs["Color"])
        blend = node("ShaderNodeMixShader")
        blend.inputs[0].default_value = 0.25
        link(surface, blend.inputs[1])
        link(trans.outputs["BSDF"], blend.inputs[2])
        surface = blend.outputs["Shader"]
    if cutout:
        # Season controls (the game's tree shader does the same): LeafShow is the fraction of
        # seasonal leaves present (leaf-out and leaf drop, card by card in random order), ColourMix
        # moves cards from Tex to Tex2 (autumn) with a per-card stagger, and Tint lightens new
        # spring leaves.
        season = node("ShaderNodeUVMap", uv_map="Season")
        sep2 = node("ShaderNodeSeparateXYZ")
        link(season.outputs["UV"], sep2.inputs[0])
        show = node("ShaderNodeValue", name="LeafShow")
        show.outputs[0].default_value = 1.0
        colour = node("ShaderNodeValue", name="ColourMix")
        colour.outputs[0].default_value = 0.0
        tint = node("ShaderNodeValue", name="Tint")
        tint.outputs[0].default_value = 0.0
        tex2 = node("ShaderNodeTexImage", name="Tex2")
        tex2.image = img2 or img
        uv0 = node("ShaderNodeUVMap", uv_map="UVMap")
        link(uv0.outputs["UV"], tex.inputs["Vector"])
        link(uv0.outputs["UV"], tex2.inputs["Vector"])
        turn = math_node("MULTIPLY", math_node("SUBTRACT", colour.outputs[0], math_node("MULTIPLY", sep2.outputs["X"], 0.5)), 2.0, clamp=True)
        seasonal = node("ShaderNodeMix", data_type="RGBA")
        link(tex.outputs["Color"], seasonal.inputs["A"])
        link(tex2.outputs["Color"], seasonal.inputs["B"])
        link(turn, seasonal.inputs["Factor"])
        spring = node("ShaderNodeMix", data_type="RGBA", blend_type="MULTIPLY")
        spring.inputs["B"].default_value = (1.35, 1.5, 0.75, 1)
        link(seasonal.outputs["Result"], spring.inputs["A"])
        link(tint.outputs[0], spring.inputs["Factor"])
        link(spring.outputs["Result"], mix.inputs["A"])
        drops = math_node("GREATER_THAN", sep.outputs["Y"], 0.75)
        gone = math_node("MULTIPLY", drops, math_node("GREATER_THAN", sep2.outputs["X"], show.outputs[0]))
        alpha = math_node("MULTIPLY", tex.outputs["Alpha"], math_node("SUBTRACT", 1.0, gone))
        cut = node("ShaderNodeMixShader")
        link(alpha, cut.inputs[0])
        link(node("ShaderNodeBsdfTransparent").outputs["BSDF"], cut.inputs[1])
        link(surface, cut.inputs[2])
        surface = cut.outputs["Shader"]
    link(surface, out.inputs["Surface"])
    return m


def species_materials(sp, out_dir):
    """Returns (materials by submesh for LOD0, for LOD1-2, season images) for a species."""
    seed = zlib.crc32(sp["id"].encode())
    style, colour = sp["bark"]
    albedo, height, strength = textures.bark(style, colour, seed, TEX)
    bark = material(f"{sp['id']}_Bark", image(f"{sp['id']}_bark", albedo, out_dir), 0.6,
                    normal=image(f"{sp['id']}_bark_normal", textures.bark_normal(height, strength), out_dir, data=True))
    if sp["form"] != "deciduous":   # conifers and krummholz
        sheen = sp.get("sheen")
        spray = image(f"{sp['id']}_spray", textures.needle_spray(sp["needles"], sp["foliage"], seed, TEX, sheen), out_dir)
        cluster = image(f"{sp['id']}_cluster", textures.branch_cluster(sp["needles"], sp["foliage"], seed, TEX, sheen), out_dir)
        near = [bark, material(f"{sp['id']}_Foliage", spray, 1.0, cutout=True, leaf=True)]
        far = [bark, material(f"{sp['id']}_Cluster", cluster, 1.0, cutout=True, leaf=True)]
        return near, far, {}
    seasons = {
        "summer": image(f"{sp['id']}_leaves_summer", textures.leaf_card(sp["leaf"], sp["summer"], seed, TEX, sp["twig"]), out_dir),
        "autumn": image(f"{sp['id']}_leaves_autumn", textures.leaf_card(sp["leaf"], sp["autumn"], seed, TEX, sp["twig"]), out_dir),
    }
    if "marcescent" in sp:
        seasons["kept"] = image(f"{sp['id']}_leaves_kept", textures.leaf_card(sp["leaf"], sp["marcescent"], seed + 1, TEX, sp["twig"]), out_dir)
    twigs = image(f"{sp['id']}_twigs", textures.twig_card(seed, TEX, sp["twig"]), out_dir)
    mats = [bark,
            material(f"{sp['id']}_Leaves", seasons["summer"], 0.0, cutout=True, leaf=True, img2=seasons["autumn"]),
            material(f"{sp['id']}_Twigs", twigs, 0.5, cutout=True),
            material(f"{sp['id']}_LeavesKept", seasons.get("kept", seasons["autumn"]), 0.0, cutout=True, leaf=True)]
    return mats, mats, seasons


def tri_count(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def card_area(obj):
    """Square metres of alpha-tested cards (foliage, leaves, twigs): a proxy for GPU overdraw."""
    return round(sum(p.area for p in obj.data.polygons if p.material_index != BARK), 1)


def build_species(sp, out_dir):
    near, far, seasons = species_materials(sp, out_dir)
    made = []
    for v in range(3):
        seed = zlib.crc32(f"{sp['id']}:{v}".encode())
        form = sp["forms"][v] if "forms" in sp else None   # krummholz: one shape per variant
        lo, hi = form["height"] if form else sp["height"]
        height = lerp(lo, hi, random.Random(seed).random())
        if form:
            def builder(s, r, lod, h, form=form):
                return build_krummholz(s, r, lod, h, form)
        else:
            builder = build_conifer if sp["form"] == "conifer" else build_deciduous
        lods = [builder(sp, random.Random(seed), lod, height).to_object(f"{sp['id']}_v{v}_LOD{li}", near if li == 0 else far)
                for li, lod in enumerate(LODS)]
        bpy.ops.object.select_all(action="DESELECT")
        for o in lods:
            o.select_set(True)
        bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, f"{sp['id']}_v{v}.fbx"), use_selection=True,
                                 apply_unit_scale=True, axis_forward="-Z", axis_up="Y", mesh_smooth_type="FACE",
                                 colors_type="LINEAR", use_triangles=True, bake_space_transform=True, path_mode="RELATIVE")
        made.append({"variant": v, "height": round(height, 2), "objects": lods, "triangles": [tri_count(o) for o in lods],
                     "cardAreaM2": [card_area(o) for o in lods],
                     # The prefab's native height in Unity: LOD0's highest point (a drooping leader or a
                     # krummholz's sprays can end below or above the nominal height).
                     "top": round(max(vt.co.z for vt in lods[0].data.vertices), 2)})
        if form:
            made[-1]["shape"] = form["shape"]
    return made, seasons


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    opts = {argv[i][2:]: argv[i + 1] for i in range(0, len(argv) - 1, 2) if argv[i].startswith("--")}
    out_dir = os.path.abspath(opts.get("out", os.path.join(HERE, "out")))
    os.makedirs(out_dir, exist_ok=True)
    species = json.load(open(os.path.join(HERE, "species.json"), encoding="utf-8"))["species"]
    if "species" in opts:
        species = [s for s in species if s["id"] in opts["species"].split(",")]

    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    report, built, seasons = {}, {}, {}
    for sp in species:
        built[sp["id"]], seasons[sp["id"]] = build_species(sp, out_dir)
        report[sp["id"]] = [{k: v for k, v in m.items() if k != "objects"} for m in built[sp["id"]]]
        print(f"{sp['name']}: " + "; ".join(f"v{m['variant']} {m['height']} m, tris {m['triangles']}, cards {m['cardAreaM2'][0]} m2"
                                             for m in built[sp["id"]]), flush=True)
    over = [f"{sid} v{m['variant']} {BUDGET[i]['lod']}: {t:,} tris > {BUDGET[i]['max_tris']:,}"
            for sid, ms in report.items() for m in ms for i, t in enumerate(m["triangles"]) if t > BUDGET[i]["max_tris"]]
    with open(os.path.join(out_dir, "trees.json"), "w") as f:
        json.dump({"budget": BUDGET, "species": report, "overBudget": over}, f, indent=2)
    print("Budget check: " + ("all trees within budget" if not over else "OVER BUDGET"), flush=True)
    for line in over:
        print("  " + line, flush=True)

    if "render" in opts:
        import render_preview
        render_preview.render_all(species, built, seasons, opts["render"], opts.get("shots"))
    return over


if __name__ == "__main__":
    # A tree over budget fails the build (non-zero exit), like a failing test.
    sys.exit(1 if main() else 0)
