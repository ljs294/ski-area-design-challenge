"""Builds the stylized species library in Blender, with no manual steps (0.5 §3, TR1, TR2, TR4).

    blender -b --factory-startup --python tools/assets/trees/build_trees.py -- [--out DIR] [--render DIR] [--species id,id]

For every species in species.json it makes 3 variants x 3 LODs, deterministically (seeded by
species and variant), and exports one FBX per variant with objects <id>_v<N>_LOD0..2, which Unity
groups into an LODGroup on import. With --render it also writes review images.

Mesh data the tree shader reads (see README.md):
  Color (vertex)  R trunk sway weight, G branch flex, B per-branch phase, A leaf/needle flutter
  UV0             bark and foliage texture coordinates
  UV1 "Data"      x = snow mask (0..1, faces that can hold snow), y = seasonal leaf flag (1 = drops)
  Submesh 0 bark, submesh 1 foliage (conifers) or leaves (deciduous)
"""
import json
import math
import os
import random
import sys
import zlib

import bpy
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
UP = Vector((0.0, 0.0, 1.0))
GOLDEN = math.radians(137.508)

# Detail per LOD: tube sides, segments per branch, whorl-spacing multiplier, foliage style.
LODS = [
    {"sides": 8, "branchSides": 4, "segments": 5, "spacing": 1.0, "pads": True, "twigs": 1.0, "leafCards": 6},
    {"sides": 6, "branchSides": 3, "segments": 3, "spacing": 1.9, "pads": True, "twigs": 0.5, "leafCards": 3},
    {"sides": 5, "branchSides": 3, "segments": 2, "spacing": 4.0, "pads": False, "twigs": 0.0, "leafCards": 0},
]


def smoothstep(a, b, x):
    t = min(1.0, max(0.0, (x - a) / (b - a)))
    return t * t * (3 - 2 * t)


def lerp(a, b, t):
    return a + (b - a) * t


class MeshBuilder:
    """Collects vertices, faces and the per-vertex / per-corner data the tree shader needs."""

    def __init__(self):
        self.verts, self.wind, self.faces, self.mats, self.uv0, self.data = [], [], [], [], [], []

    def vert(self, p, wind):
        self.verts.append(tuple(p))
        self.wind.append(wind)
        return len(self.verts) - 1

    def face(self, idx, uvs, mat, snow_scale, leaf=0.0, roles=None, snow_from=0.15):
        """roles: optional per-corner snow weights (1 = ridge that carries the load, 0 = underside)."""
        pts = [Vector(self.verts[i]) for i in idx]
        n = Vector((0, 0, 0))
        for i in range(len(pts)):  # Newell normal
            a, b = pts[i], pts[(i + 1) % len(pts)]
            n += Vector(((a.y - b.y) * (a.z + b.z), (a.z - b.z) * (a.x + b.x), (a.x - b.x) * (a.y + b.y)))
        nz = n.normalized().z if n.length > 1e-12 else 0.0
        self.faces.append(tuple(idx))
        self.mats.append(mat)
        self.uv0.extend(uvs)
        if roles is None:
            snow = smoothstep(snow_from, snow_from + 0.6, nz) * snow_scale
            self.data.extend([(snow, leaf)] * len(idx))
        else:  # snow rides on the ridges of upward-facing surfaces only
            gate = smoothstep(-0.05, 0.45, nz) * snow_scale
            self.data.extend([(r * gate, leaf) for r in roles])

    def tube(self, pts, radii, sides, winds, mat, snow_scale, v_scale=1.0, snow_from=0.15):
        """A tapered tube along pts; closes to a point when the last radius is 0."""
        rings, frame = [], None
        length = 0.0
        for i, p in enumerate(pts):
            t = (pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)]).normalized()
            if frame is None:
                side = t.cross(UP if abs(t.z) < 0.99 else Vector((1, 0, 0))).normalized()
            else:  # parallel transport keeps the twist steady
                side = (frame - t * frame.dot(t)).normalized()
            frame = side
            fwd = t.cross(side).normalized()
            if i > 0:
                length += (p - pts[i - 1]).length
            ring = []
            if radii[i] <= 1e-6:
                ring = [self.vert(p, winds[i])] * sides
            else:
                for k in range(sides):
                    a = 2 * math.pi * k / sides
                    ring.append(self.vert(p + (side * math.cos(a) + fwd * math.sin(a)) * radii[i], winds[i]))
            rings.append((ring, length))
        circ = 2 * math.pi * max(radii[0], 0.01)
        for i in range(len(rings) - 1):
            (r0, l0), (r1, l1) = rings[i], rings[i + 1]
            for k in range(sides):
                k1 = (k + 1) % sides
                u0, u1 = k / sides, (k + 1) / sides
                v0, v1 = l0 / circ * v_scale, l1 / circ * v_scale
                if r1[k] == r1[k1]:
                    self.face([r0[k], r0[k1], r1[k]], [(u0, v0), (u1, v0), (u0, v1)], mat, snow_scale, snow_from=snow_from)
                else:
                    self.face([r0[k], r0[k1], r1[k1], r1[k]], [(u0, v0), (u1, v0), (u1, v1), (u0, v1)], mat, snow_scale, snow_from=snow_from)

    def pad(self, pts, widths, thick, winds, mat):
        """A flattened foliage mass (diamond cross-section) along a branch, closing at the tip."""
        rings = []
        for i, p in enumerate(pts):
            t = (pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)]).normalized()
            lat = t.cross(UP)
            lat = lat.normalized() if lat.length > 1e-6 else Vector((1, 0, 0))
            up = lat.cross(t).normalized()
            w = widths[i]
            if w <= 1e-6:
                rings.append([self.vert(p, winds[i])] * 4)
                continue
            rings.append([
                self.vert(p - lat * w, winds[i]),
                self.vert(p + up * w * thick, winds[i]),
                self.vert(p + lat * w, winds[i]),
                self.vert(p - up * w * thick * 0.6, winds[i]),
            ])
        n = len(rings) - 1
        role = [0.0, 0.9, 0.0, 0.0]  # left edge, top ridge, right edge, underside
        for i in range(n):
            r0, r1 = rings[i], rings[i + 1]
            for k in range(4):
                k1 = (k + 1) % 4
                uvs = [(k / 4, i / n), ((k + 1) / 4, i / n), ((k + 1) / 4, (i + 1) / n), (k / 4, (i + 1) / n)]
                if r1[k] == r1[k1]:
                    self.face([r0[k], r0[k1], r1[k]], uvs[:3], mat, 1.0, roles=[role[k], role[k1], 0.5])
                else:
                    self.face([r0[k], r0[k1], r1[k1], r1[k]], uvs, mat, 1.0, roles=[role[k], role[k1], role[k1], role[k]])
        # Close the trunk end so the mass reads solid from any angle.
        r0 = rings[0]
        if r0[0] != r0[1]:
            self.face([r0[3], r0[2], r0[1], r0[0]], [(0, 0), (1, 0), (1, 1), (0, 1)], mat, 1.0, roles=[0, 0, 0, 0])

    def skirt(self, centre, top_r, bottom_r, height, sides, droop, wind, mat):
        """LOD2 conifer tier: a drooping cone skirt with a closed underside."""
        top = [self.vert(centre + Vector((math.cos(2 * math.pi * k / sides) * top_r, math.sin(2 * math.pi * k / sides) * top_r, height)), wind(0.0)) for k in range(sides)]
        bot = [self.vert(centre + Vector((math.cos(2 * math.pi * (k + 0.5) / sides) * bottom_r, math.sin(2 * math.pi * (k + 0.5) / sides) * bottom_r, -droop)), wind(1.0)) for k in range(sides)]
        mid = self.vert(centre + Vector((0, 0, -droop * 0.3)), wind(0.0))
        apex = self.vert(centre + Vector((0, 0, height + 0.05)), wind(0.0))
        for k in range(sides):
            k1 = (k + 1) % sides
            self.face([top[k], bot[k], top[k1]], [(0, 1), (0.5, 0), (1, 1)], mat, 1.0, roles=[0.9, 0.15, 0.9])
            self.face([top[k1], bot[k], bot[k1]], [(1, 1), (0.5, 0), (1, 0)], mat, 1.0, roles=[0.9, 0.15, 0.15])
            self.face([apex, top[k], top[k1]], [(0.5, 1), (0, 0.8), (1, 0.8)], mat, 1.0, roles=[1.0, 0.9, 0.9])
            self.face([mid, bot[k1], bot[k]], [(0.5, 0.5), (1, 0), (0, 0)], mat, 1.0, roles=[0, 0, 0])

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
        mesh.uv_layers.active_index = 0
        col = mesh.color_attributes.new(name="Wind", type="FLOAT_COLOR", domain="POINT")
        col.data.foreach_set("color", [c for w in self.wind for c in w])
        mesh.color_attributes.active_color_name = "Wind"
        obj = bpy.data.objects.new(name, mesh)
        bpy.context.scene.collection.objects.link(obj)
        return obj


# ---------------------------------------------------------------------------------------------
# Conifers: a spire of whorled branches carrying flattened foliage masses (fir, hemlock).

def build_conifer(sp, rng, lod, height, mats):
    b = MeshBuilder()
    trunk_r = sp["trunkRadius"] * height
    phase_trunk = rng.random()
    nod = math.radians(sp["nod"] * rng.uniform(0.7, 1.1))
    nod_dir = rng.uniform(0, 2 * math.pi)
    lean = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), 0)) * 0.012

    def trunk_point(h):  # h in 0..1
        p = Vector((0, 0, h * height)) + lean * h * height
        if nod > 0 and h > 0.9:  # the hemlock's nodding leader
            s = (h - 0.9) / 0.1
            bend = nod * s * s
            p += Vector((math.cos(nod_dir), math.sin(nod_dir), 0)) * math.sin(bend) * 0.05 * height * s
            p.z -= (1 - math.cos(bend)) * 0.05 * height * s
        return p

    def sway(z):
        return min(1.0, max(0.0, z / height)) ** 2

    n = 10
    tpts = [trunk_point(i / n) for i in range(n + 1)]
    tpts[0] = tpts[0] - Vector((0, 0, 0.3))  # sink into the ground
    b.tube(tpts, [trunk_r * (1 - i / n) ** 1.1 + (0.01 if i < n else 0) for i in range(n + 1)], lod["sides"],
           [(sway(p.z), 0.0, phase_trunk, 0.0) for p in tpts], 0, 0.5)

    crown_base = sp["crownBase"] * height
    r_max = sp["crownRadius"] * height * rng.uniform(0.9, 1.1)

    def crown_radius(z):
        h = (z - crown_base) / (height - crown_base)
        return r_max * max(0.0, 1 - h) ** sp["crownExponent"] * (0.55 + 0.45 * smoothstep(0.0, 0.06, h))

    if not lod["pads"]:
        tiers = max(3, int((height - crown_base) / (sp["whorlSpacing"] * lod["spacing"] * 1.2)))
        for i in range(tiers):
            z0 = crown_base + (height - crown_base) * i / tiers
            z1 = crown_base + (height - crown_base) * (i + 1) / tiers
            r = crown_radius(z0) * 1.05
            c = trunk_point(z0 / height)
            b.skirt(c, crown_radius(z1) * 0.35, r, (z1 - z0) * 1.15, 7, r * sp["droop"] * 0.5,
                    lambda f, z=z0: (sway(z), f, rng.random(), f), 1)
        return b

    spacing = sp["whorlSpacing"] * lod["spacing"]
    z, k = crown_base, 0
    leader_z = height - max(1.6, 0.11 * height)  # the top is one slim spire, not tiny branches
    last_z = leader_z
    lo, hi = sp["branchesPerWhorl"]
    while z < leader_z:
        count = rng.randint(lo, hi)
        for j in range(count):
            if rng.random() < 0.08:
                continue  # a few missing branches break up the symmetry
            az = k * GOLDEN + j * 2 * math.pi / count + rng.uniform(-0.25, 0.25)
            length = crown_radius(z) * rng.uniform(0.85, 1.1) * (lod["spacing"] ** 0.15)
            length = max(length, 0.45)
            h = (z - crown_base) / (height - crown_base)
            elev = math.radians(lerp(sp["branchElevation"][1], sp["branchElevation"][0], h))
            base = trunk_point(z / height)
            d = Vector((math.cos(az), math.sin(az), 0))
            phase = rng.random()
            segs = lod["segments"]
            pts, widths, winds = [], [], []
            for s in range(segs + 1):
                t = s / segs
                along = d * length * t
                drop = math.tan(elev) * length * t - sp["droop"] * length * t * t
                pts.append(base + along + Vector((0, 0, drop)))
                w = sp["padWidth"] * length * (0.45 + 0.55 * math.sin(math.pi * min(1.0, 0.25 + t * 0.75))) * (1 - t) ** 0.35
                widths.append(w * (lod["spacing"] ** 0.35) if s < segs else 0.0)
                winds.append((sway(pts[-1].z), t, phase, 0.5 + 0.5 * t))
            b.pad(pts, widths, sp["padThickness"], winds, 1)
        z += spacing * rng.uniform(0.85, 1.15)
        k += 1
    # The leader: a slim foliage spire from the last whorl to the tip, so the top is never bare.
    base_h = last_z / height
    lpts = [trunk_point(lerp(base_h, 1.0, i / 4)) for i in range(5)]
    lpts[-1] = lpts[-1] + (lpts[-1] - lpts[-2]).normalized() * 0.4
    r0 = max(0.22, crown_radius(last_z) * 0.4)
    b.tube(lpts, [r0 * (1 - i / 4) ** 0.9 for i in range(5)], 5,
           [(sway(p.z), 0.0, phase_trunk, 0.5) for p in lpts], 1, 0.8, snow_from=-0.35)
    return b


# ---------------------------------------------------------------------------------------------
# Deciduous: ascending primary branches with twigs; separate leaf cards for the seasons (TR4).

def build_deciduous(sp, rng, lod, height, mats):
    b = MeshBuilder()
    trunk_r = sp["trunkRadius"] * height
    wob = [Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), 0)) * 0.12 for _ in range(4)]

    def trunk_point(h):
        p = Vector((0, 0, h * height))
        for i, w in enumerate(wob):
            p += w * math.sin(math.pi * h * (i + 1) * 0.9)
        return p

    def sway(z):
        return min(1.0, max(0.0, z / height)) ** 2

    n = 10
    tpts = [trunk_point(i / n) for i in range(n + 1)]
    tpts[0] = tpts[0] - Vector((0, 0, 0.3))
    ph = rng.random()
    b.tube(tpts, [trunk_r * (1 - 0.85 * i / n) if i < n else 0.0 for i in range(n + 1)], lod["sides"],
           [(sway(p.z), 0.0, ph, 0.0) for p in tpts], 0, 0.5, v_scale=0.5)

    lo, hi = sp["primaryBranches"]
    count = max(4, int(rng.randint(lo, hi) * (1.0 if lod["twigs"] > 0 else 0.6)))
    for i in range(count):
        h = lerp(sp["crownBase"], 0.93, (i + rng.random() * 0.6) / count)
        base = trunk_point(h)
        az = i * GOLDEN + rng.uniform(-0.3, 0.3)
        ang = math.radians(lerp(sp["branchAngleFromVertical"][1], sp["branchAngleFromVertical"][0], h) + rng.uniform(-6, 6))
        length = height * rng.uniform(*sp["branchLength"]) * (1.15 - h * 0.6)
        d = Vector((math.cos(az) * math.sin(ang), math.sin(az) * math.sin(ang), math.cos(ang)))
        segs = lod["segments"] + 1
        phase = rng.random()
        pts = [base + d * length * (s / segs) + Vector((0, 0, 0.35 * length * math.sin(math.pi * s / segs) * 0.25)) for s in range(segs + 1)]
        r0 = trunk_r * (1 - 0.6 * h) * 0.8
        b.tube(pts, [r0 * (1 - s / segs) + (0.004 if s < segs else 0) for s in range(segs + 1)], lod["branchSides"],
               [(sway(p.z), s / segs, phase, 0.0) for s, p in enumerate(pts)], 0, 0.6)
        twigs = int(rng.randint(*sp["twigsPerBranch"]) * lod["twigs"])
        for t in range(twigs):
            f = lerp(0.35, 0.95, (t + 0.5) / max(1, twigs))
            tb = pts[0].lerp(pts[-1], f)
            taz = az + rng.uniform(-1.2, 1.2)
            td = Vector((math.cos(taz) * 0.7, math.sin(taz) * 0.7, rng.uniform(0.3, 0.9))).normalized()
            tl = length * rng.uniform(0.25, 0.4)
            tpts2 = [tb, tb + td * tl * 0.5 + Vector((0, 0, 0.05)), tb + td * tl]
            b.tube(tpts2, [r0 * 0.55 * (1 - f * 0.4), r0 * 0.3, 0.0], 3,
                   [(sway(p.z), f + (1 - f) * s / 2, phase, 0.2) for s, p in enumerate(tpts2)], 0, 0.6)
            # Leaf cards (summer/autumn only; the leaf flag lets the shader drop them).
            for c in range(lod["leafCards"]):
                lp = tpts2[1].lerp(tpts2[2], rng.random()) + Vector((rng.uniform(-0.3, 0.3), rng.uniform(-0.3, 0.3), rng.uniform(-0.2, 0.3)))
                s = rng.uniform(0.25, 0.4)
                a = rng.uniform(0, math.pi)
                ax, ay = Vector((math.cos(a), math.sin(a), 0.2)) * s, Vector((-math.sin(a), math.cos(a), 0.6)).normalized() * s
                wind = (sway(lp.z), 1.0, phase, 1.0)
                q = [b.vert(lp - ax - ay, wind), b.vert(lp + ax - ay, wind), b.vert(lp + ax + ay, wind), b.vert(lp - ax + ay, wind)]
                b.face(q, [(0, 0), (1, 0), (1, 1), (0, 1)], 1, 0.0, leaf=1.0)
    return b


# ---------------------------------------------------------------------------------------------

def material(name, hex_colour, snow_load=1.0, hide=False):
    """Preview material: the palette tint blended to snow by the mesh's snow mask (UV1.x)."""
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Roughness"].default_value = 0.85
    uv = nt.nodes.new("ShaderNodeUVMap")
    uv.uv_map = "Data"
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    mix = nt.nodes.new("ShaderNodeMix")
    mix.data_type = "RGBA"
    c = tuple(int(hex_colour[i:i + 2], 16) / 255 for i in (1, 3, 5))
    mix.inputs["A"].default_value = (c[0] ** 2.2, c[1] ** 2.2, c[2] ** 2.2, 1)
    mix.inputs["B"].default_value = (0.90, 0.93, 0.95, 1)  # snow lit #F4F7FA (linear)
    mul = nt.nodes.new("ShaderNodeMath")
    mul.operation = "MULTIPLY"
    mul.inputs[1].default_value = snow_load
    nt.links.new(uv.outputs["UV"], sep.inputs[0])
    nt.links.new(sep.outputs["X"], mul.inputs[0])
    nt.links.new(mul.outputs[0], mix.inputs["Factor"])
    nt.links.new(mix.outputs["Result"], bsdf.inputs["Base Color"])
    if hide:
        bsdf.inputs["Alpha"].default_value = 0.0
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return m


def tri_count(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def build_species(sp, out_dir, winter=True):
    made = []
    for v in range(3):
        rng_seed = zlib.crc32(f"{sp['id']}:{v}".encode())
        height = lerp(sp["height"][0], sp["height"][1], random.Random(rng_seed).random())
        if sp["form"] == "conifer":
            mats = [material(f"{sp['id']}_Bark", sp["bark"]),
                    material(f"{sp['id']}_Foliage_v{v}", sp["foliage"][v % len(sp["foliage"])])]
            builder = build_conifer
        else:
            mats = [material(f"{sp['id']}_Bark", sp["bark"], 0.6),
                    material(f"{sp['id']}_Leaves", sp["leaves"], 0.0, hide=winter)]
            builder = build_deciduous
        lods = []
        for li, lod in enumerate(LODS):
            rng = random.Random(rng_seed)  # same seed per LOD: same tree, less detail
            obj = builder(sp, rng, lod, height, mats).to_object(f"{sp['id']}_v{v}_LOD{li}", mats)
            lods.append(obj)
        if out_dir:
            bpy.ops.object.select_all(action="DESELECT")
            for o in lods:
                o.select_set(True)
            bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, f"{sp['id']}_v{v}.fbx"), use_selection=True,
                                     apply_unit_scale=True, axis_forward="-Z", axis_up="Y", mesh_smooth_type="FACE",
                                     colors_type="LINEAR", use_triangles=True, bake_space_transform=True)
        made.append({"variant": v, "height": round(height, 2), "objects": lods,
                     "triangles": [tri_count(o) for o in lods]})
    return made


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    opts = {argv[i][2:]: argv[i + 1] for i in range(0, len(argv) - 1, 2) if argv[i].startswith("--")}
    out_dir = opts.get("out", os.path.join(HERE, "out"))
    os.makedirs(out_dir, exist_ok=True)
    species = json.load(open(os.path.join(HERE, "species.json"), encoding="utf-8"))["species"]
    if "species" in opts:
        species = [s for s in species if s["id"] in opts["species"].split(",")]

    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    report = {}
    built = {}
    for sp in species:
        built[sp["id"]] = build_species(sp, out_dir)
        report[sp["id"]] = [{k: v for k, v in m.items() if k != "objects"} for m in built[sp["id"]]]
        print(f"{sp['name']}: " + "; ".join(f"v{m['variant']} {m['height']} m, tris {m['triangles']}" for m in built[sp["id"]]), flush=True)
    with open(os.path.join(out_dir, "trees.json"), "w") as f:
        json.dump(report, f, indent=2)

    if "render" in opts:
        sys.path.insert(0, HERE)
        import render_preview
        render_preview.render_all(species, built, opts["render"])


if __name__ == "__main__":
    main()
