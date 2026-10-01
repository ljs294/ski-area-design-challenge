"""Adversarial audit of the tree library: measures every model the way the game uses it and renders
comparison sheets. It is not part of the build and changes nothing it measures.

    blender -b --factory-startup --python tools/assets/trees/audit_trees.py -- --out DIR [--species id,id] [--sheets a,b]

DIR/audit.json (and the printed summary) holds, per model:
  geometry  degenerate faces, NaNs, foliage below the ground, extents, foliage asymmetry, and how far the
            model reaches past the game's culling sphere (ForestCull: radius 0.6 x height about mid-height)
  data      wind weights per channel on bark and on foliage, UV ranges, snow capacity, atlas halves
  textures  alpha coverage at the game's 0.4 cutoff at mips 0, 2 and 4, and foliage and bark colour (CIELAB)
  fidelity  each LOD's crown coverage relative to LOD0 from the horizon, 22 and 53 degrees, framed exactly as
            TreeImport frames its fidelity views, so it can be checked against Art/Trees/fidelity.json
  shading   TreeShading's crown occlusion (ported from TreeShading.cs) and the TreeLight brightness it gives
  colour    the colour each model reads at a distance in the game's shading (no snow, and winter)
  lod       where the game switches LOD and culls (ForestCull's screen-height rule, 60 degree field of view,
            lodBias 2)
  slope     how far krummholz foliage floats above or sinks into a slope (the game sets a tree's foot on the
            terrain and doesn't tilt it)
Sheets (DIR/sheets, --sheets to pick): silhouettes, game, snow, tops, krummholz, slope, lodpop, stands, textures. --measure 0 skips the measurements (sheets only).

"Game shading" here is an emulation, not Unity: TreeShading's crown normals and occlusion, TreeLight's wrapped
sun and sky, and SnowPattern's snow on sprays, baked per vertex and drawn unlit. It has no shadows, haze or
impostors, so it is for comparing trees with each other, not for judging the final look.
"""
import json
import math
import os
import random
import sys

import bpy
import numpy as np
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import build_trees as bt  # noqa: E402
import render_preview as rp  # noqa: E402

REPO = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
NEW = ("pacific_silver_fir", "western_hemlock", "noble_fir", "krummholz")
CUTOFF = 0.4                                   # TreeImport.FoliageCutoff
FOV, LOD_BIAS = 60.0, 2.0                      # MountainViewer.unity camera; QualitySettings (desktop)
TRANSITIONS = (0.25, 0.10, 0.05, 0.003)        # ForestRenderer.Transitions
SCREEN_SCALE = LOD_BIAS / (2 * math.tan(math.radians(FOV / 2)))
# TreeImport's fidelity views (HemiOctDecode of frames (7,7), (6,6), (5,5)): Unity (x, y) toward the viewer.
VIEWS = {"0": (1.0, 0.0), "22": (5.0, 2.0), "53": (3.0, 4.0)}
SUN = Vector((-0.45, -0.62, 0.64)).normalized()   # toward the sun (Blender), front-left and 40 degrees up
SUN_RGB = np.array([1.0, 0.96, 0.9], np.float32)
SKY_RGB = np.array([0.42, 0.47, 0.56], np.float32)
GROUND_RGB = np.array([0.32, 0.33, 0.35], np.float32)
SNOW_RGB = (0.93, 0.95, 0.98)


# ---------------------------------------------------------------------------------------------
# Mesh data

def arrays(obj):
    """Vertex, face and loop arrays of a mesh (Blender coordinates: z up, metres)."""
    m = obj.data
    nv, nf, nl = len(m.vertices), len(m.polygons), len(m.loops)

    def get(seq, attr, n, width, dtype=np.float32):
        a = np.zeros(n * width, dtype)
        seq.foreach_get(attr, a)
        return a.reshape(-1, width) if width > 1 else a

    d = dict(co=get(m.vertices, "co", nv, 3), mat=get(m.polygons, "material_index", nf, 1, np.int32),
             area=get(m.polygons, "area", nf, 1), fn=get(m.polygons, "normal", nf, 3),
             ls=get(m.polygons, "loop_start", nf, 1, np.int32), lt=get(m.polygons, "loop_total", nf, 1, np.int32),
             lv=get(m.loops, "vertex_index", nl, 1, np.int32), vn=get(m.vertex_normals, "vector", nv, 3))
    d["uv"] = {}
    for layer in m.uv_layers:
        d["uv"][layer.name] = get(layer.data, "uv", nl, 2)
    d["wind"] = get(m.color_attributes["Wind"].data, "color", nv, 4)
    d["face_of_loop"] = np.repeat(np.arange(nf), d["lt"])
    d["centroid"] = np.add.reduceat(d["co"][d["lv"]], d["ls"]) / d["lt"][:, None]
    return d


def kinds(obj, sp):
    """Per material slot: 'bark', 'cutout' or 'hidden', as the game draws it in winter (TreeImport)."""
    out = []
    for slot in obj.material_slots:
        name = slot.material.name
        if name.endswith("_Bark"):
            out.append("bark")
        elif name.endswith("_Leaves") or (name.endswith("_LeavesKept") and "marcescent" not in sp):
            out.append("hidden")
        else:
            out.append("cutout")
    return out


def foliage_faces(d, kind):
    return np.array([kind[i] == "cutout" for i in d["mat"]], bool)


def stats(a):
    return [round(float(a.min()), 4), round(float(a.max()), 4)] if len(a) else None


def geometry(obj, sp):
    d = arrays(obj)
    kind = kinds(obj, sp)
    fol = foliage_faces(d, kind)
    bark = d["mat"] == bt.BARK
    co, area = d["co"], d["area"]
    top = float(co[:, 2].max())
    fc = d["centroid"][fol]
    fa = area[fol]
    out = {"triangles": int((d["lt"] - 2).sum()), "top": round(top, 3),
           "nan": bool(~np.isfinite(co).all()), "degenerateFaces": int((area < 1e-7).sum()),
           "lowestBark": round(float(co[np.unique(d["lv"][np.isin(d["face_of_loop"], np.nonzero(bark)[0])])][:, 2].min()), 3) if bark.any() else None,
           "cardAreaM2": round(float(fa.sum()), 1), "cards": int(fol.sum())}
    if fol.any():
        fv = np.unique(d["lv"][np.isin(d["face_of_loop"], np.nonzero(fol)[0])])
        p = co[fv]
        out["foliageBelowGround"] = round(float(fa[fc[:, 2] < 0].sum() / fa.sum()), 4)
        out["lowestFoliage"] = round(float(p[:, 2].min()), 3)
        out["foliageExtentX"] = stats(p[:, 0])
        out["foliageExtentY"] = stats(p[:, 1])
        cx, cy = (fc[:, 0] * fa).sum() / fa.sum(), (fc[:, 1] * fa).sum() / fa.sum()
        out["foliageCentroid"] = [round(float(cx), 3), round(float(cy), 3), round(float((fc[:, 2] * fa).sum() / fa.sum()), 3)]
        out["maxReach"] = round(float(np.hypot(p[:, 0], p[:, 1]).max()), 3)
    above = co[co[:, 2] >= 0]
    centre = np.array([0, 0, top / 2])
    out["cullSphereReach"] = round(float(np.linalg.norm(above - centre, axis=1).max() / (0.6 * top)), 2)
    # Wind weights and UVs, by what they sit on.
    w = d["wind"]
    bv = np.unique(d["lv"][np.isin(d["face_of_loop"], np.nonzero(bark)[0])])
    fv = np.unique(d["lv"][np.isin(d["face_of_loop"], np.nonzero(fol)[0])]) if fol.any() else np.array([], int)
    out["wind"] = {part: {ch: stats(w[idx, k]) for k, ch in enumerate("RGBA")} for part, idx in (("bark", bv), ("foliage", fv))}
    fl = np.isin(d["face_of_loop"], np.nonzero(fol)[0])
    uv0 = d["uv"]["UVMap"]
    out["uv0Foliage"] = {"u": stats(uv0[fl, 0]), "v": stats(uv0[fl, 1])} if fl.any() else None
    out["snowCapacity"] = {"bark": stats(d["uv"]["Data"][~fl, 0]), "foliage": stats(d["uv"]["Data"][fl, 0])}
    if fol.any():
        # Cards that show only the upper half of a texture: SnowPattern expects whole cards (branch line at v =
        # 0.5), so this must be 0 (the first silver fir used a two-half atlas and lost its snow).
        vmin = np.minimum.reduceat(uv0[:, 1], d["ls"])
        out["upperHalfCardShare"] = round(float(fa[vmin[fol] >= 0.499].sum() / fa.sum()), 3)
    return out


# ---------------------------------------------------------------------------------------------
# Textures

def image_array(name):
    img = bpy.data.images.get(name)
    if img is None:
        return None
    w, h = img.size
    return np.array(img.pixels[:], np.float32).reshape(h, w, 4)[::-1]   # row 0 = top


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def lab(rgb_linear):
    """CIELAB (D65) of a linear sRGB colour: (L*, chroma, hue degrees)."""
    m = np.array([[0.4124, 0.3576, 0.1805], [0.2126, 0.7152, 0.0722], [0.0193, 0.1192, 0.9505]])
    xyz = m @ np.asarray(rgb_linear, np.float64) / np.array([0.95047, 1.0, 1.08883])
    f = np.where(xyz > (6 / 29) ** 3, np.cbrt(xyz), xyz / (3 * (6 / 29) ** 2) + 4 / 29)
    L, a, b = 116 * f[1] - 16, 500 * (f[0] - f[1]), 200 * (f[1] - f[2])
    return [round(float(L), 1), round(float(math.hypot(a, b)), 1), round(float(math.degrees(math.atan2(b, a)) % 360), 0)]


def coverage_mips(alpha, levels=(0, 2, 4)):
    out = {}
    a = alpha
    for k in range(max(levels) + 1):
        if k in levels:
            out[f"mip{k}"] = round(float((a > CUTOFF).mean()), 3)
        a = (a[0::2, 0::2] + a[1::2, 0::2] + a[0::2, 1::2] + a[1::2, 1::2]) / 4
    return out


def texture_metrics(sid, sp):
    out = {}
    names = ["spray", "cluster"] if sp["form"] != "deciduous" else ["twigs"]
    for kind in names + ["bark"]:
        px = image_array(f"{sid}_{kind}")
        if px is None:
            continue
        a = px[..., 3]
        # The images hold display (sRGB) values: decode to linear before averaging.
        opaque = a > CUTOFF if kind != "bark" else np.ones_like(a, bool)
        mean = srgb_to_linear(px[..., :3])[opaque].mean(axis=0)
        entry = {"lab": lab(mean)}
        if kind != "bark":
            entry["coverage"] = coverage_mips(a)
        out[kind] = entry
    return out


# ---------------------------------------------------------------------------------------------
# Rendering helpers

def render_array(path):
    """Renders the scene to path (PNG) and returns it as a float array, row 0 at the top."""
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    img = bpy.data.images.load(path)
    w, h = img.size
    px = np.array(img.pixels[:], np.float32).reshape(h, w, 4)[::-1].copy()
    bpy.data.images.remove(img)
    return px


def measure_setup(width, height, samples=16):
    rp.setup_render(width, height, samples)
    s = bpy.context.scene
    s.render.film_transparent = True
    s.cycles.use_denoising = False
    s.cycles.transparent_max_bounces = 256
    s.render.use_persistent_data = True
    s.view_settings.view_transform = "Standard"
    s.view_settings.look = "None"
    s.render.image_settings.color_mode = "RGBA"


def node_tree(m):
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    return nt


def math_node(nt, op, a=None, b=None, clamp=False):
    n = nt.nodes.new("ShaderNodeMath")
    n.operation, n.use_clamp = op, clamp
    for i, v in enumerate((a, b)):
        if isinstance(v, (int, float)):
            n.inputs[i].default_value = v
        elif v is not None:
            nt.links.new(v, n.inputs[i])
    return n.outputs[0]


def uv_xy(nt, name):
    uv = nt.nodes.new("ShaderNodeUVMap")
    uv.uv_map = name
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(uv.outputs["UV"], sep.inputs[0])
    return sep.outputs["X"], sep.outputs["Y"]


def cut_shader(nt, alpha, surface):
    """Transparent where alpha is at or under the game's cutoff."""
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    if alpha is None:
        nt.links.new(surface, out.inputs["Surface"])
        return
    mix = nt.nodes.new("ShaderNodeMixShader")
    nt.links.new(math_node(nt, "GREATER_THAN", alpha, CUTOFF), mix.inputs[0])
    nt.links.new(nt.nodes.new("ShaderNodeBsdfTransparent").outputs["BSDF"], mix.inputs[1])
    nt.links.new(surface, mix.inputs[2])
    nt.links.new(mix.outputs["Shader"], out.inputs["Surface"])


_MATS = {}


def mask_material(src, kind, mode):
    """Flat materials for measuring: 'cover' (white where the game draws) or 'mask' (grey where it draws)."""
    key = (src.name, kind, mode)
    if key in _MATS:
        return _MATS[key]
    m = bpy.data.materials.new(f"Audit_{mode}_{src.name}")
    nt = node_tree(m)
    em = nt.nodes.new("ShaderNodeEmission")
    if kind == "hidden":
        cut_shader(nt, None, nt.nodes.new("ShaderNodeBsdfTransparent").outputs["BSDF"])
    else:
        alpha = None
        if kind == "cutout":
            tex = nt.nodes.new("ShaderNodeTexImage")
            tex.image = src.node_tree.nodes["Tex"].image
            uv = nt.nodes.new("ShaderNodeUVMap")
            uv.uv_map = "UVMap"
            nt.links.new(uv.outputs["UV"], tex.inputs["Vector"])
            alpha = tex.outputs["Alpha"]
        em.inputs["Color"].default_value = (1, 1, 1, 1) if mode == "cover" else (0.2, 0.2, 0.2, 1)
        cut_shader(nt, alpha, em.outputs["Emission"])
    _MATS[key] = m
    return m


def game_material(src, kind, snow):
    """The game's look, unlit: texture x baked TreeLight brightness ('Lit'), snow from SnowPattern."""
    key = (src.name, kind, "game", snow)
    if key in _MATS:
        return _MATS[key]
    m = bpy.data.materials.new(f"Audit_game{'_snow' if snow else ''}_{src.name}")
    nt = node_tree(m)
    if kind == "hidden":
        cut_shader(nt, None, nt.nodes.new("ShaderNodeBsdfTransparent").outputs["BSDF"])
        _MATS[key] = m
        return m
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = src.node_tree.nodes["Tex"].image
    uvn = nt.nodes.new("ShaderNodeUVMap")
    uvn.uv_map = "UVMap"
    nt.links.new(uvn.outputs["UV"], tex.inputs["Vector"])
    lit = nt.nodes.new("ShaderNodeAttribute")
    lit.attribute_name = "Lit"
    col = nt.nodes.new("ShaderNodeMix")
    col.data_type, col.blend_type = "RGBA", "MULTIPLY"
    col.inputs["Factor"].default_value = 1.0
    nt.links.new(tex.outputs["Color"], col.inputs["A"])
    nt.links.new(lit.outputs["Color"], col.inputs["B"])
    colour = col.outputs["Result"]
    if snow:
        # TreeInstanced: snow = capacity x load x saturate(up x 1.6 + 0.1) x SnowPattern(uv, seed) on foliage.
        cap, _ = uv_xy(nt, "Data")
        geo = nt.nodes.new("ShaderNodeNewGeometry")
        sep = nt.nodes.new("ShaderNodeSeparateXYZ")
        nt.links.new(geo.outputs["Normal"], sep.inputs[0])
        facing = math_node(nt, "ADD", math_node(nt, "MULTIPLY", sep.outputs["Z"], 1.6), 0.1, clamp=True)
        amount = math_node(nt, "MULTIPLY", cap, facing)
        if kind == "cutout":
            u, v = uv_xy(nt, "UVMap")
            seed, _ = uv_xy(nt, "Season")
            core = math_node(nt, "SUBTRACT", 1.0, math_node(nt, "MULTIPLY", math_node(nt, "ABSOLUTE", math_node(nt, "SUBTRACT", v, 0.5)), 3.0))
            along = math_node(nt, "SUBTRACT", 1.1, math_node(nt, "MULTIPLY", u, 0.55))
            comb = nt.nodes.new("ShaderNodeCombineXYZ")
            nt.links.new(math_node(nt, "ADD", math_node(nt, "MULTIPLY", u, 9.0), math_node(nt, "MULTIPLY", seed, 41.0)), comb.inputs[0])
            nt.links.new(math_node(nt, "ADD", math_node(nt, "MULTIPLY", v, 7.0), math_node(nt, "MULTIPLY", seed, 41.0)), comb.inputs[1])
            noise = nt.nodes.new("ShaderNodeTexNoise")
            noise.noise_dimensions = "2D"
            noise.inputs["Scale"].default_value = 1.0
            noise.inputs["Detail"].default_value = 0.0
            nt.links.new(comb.outputs["Vector"], noise.inputs["Vector"])
            raw = math_node(nt, "ADD", math_node(nt, "MULTIPLY", core, along),
                            math_node(nt, "MULTIPLY", math_node(nt, "SUBTRACT", noise.outputs["Fac"], 0.5), 0.45))
            ramp = nt.nodes.new("ShaderNodeMapRange")
            ramp.interpolation_type = "SMOOTHSTEP"
            ramp.inputs["From Min"].default_value, ramp.inputs["From Max"].default_value = 0.3, 0.5
            nt.links.new(raw, ramp.inputs["Value"])
            amount = math_node(nt, "MULTIPLY", amount, ramp.outputs["Result"])
        amount = math_node(nt, "MINIMUM", amount, 1.0)
        bright = nt.nodes.new("ShaderNodeMix")
        bright.data_type, bright.blend_type = "RGBA", "MULTIPLY"
        bright.inputs["Factor"].default_value = 1.0
        bright.inputs["A"].default_value = tuple(c * 1.15 for c in SNOW_RGB) + (1,)
        nt.links.new(lit.outputs["Color"], bright.inputs["B"])
        mix = nt.nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        nt.links.new(amount, mix.inputs["Factor"])
        nt.links.new(colour, mix.inputs["A"])
        nt.links.new(bright.outputs["Result"], mix.inputs["B"])
        colour = mix.outputs["Result"]
    em = nt.nodes.new("ShaderNodeEmission")
    nt.links.new(colour, em.inputs["Color"])
    cut_shader(nt, tex.outputs["Alpha"] if kind == "cutout" else None, em.outputs["Emission"])
    _MATS[key] = m
    return m


def instance(obj, sp, loc=(0, 0, 0), rot=0.0, scale=1.0, mode="cover", snow=False):
    """A copy of obj in the scene with object-level audit materials (the mesh's own are untouched)."""
    c = obj.copy()
    c.location, c.rotation_euler, c.scale = loc, (0, 0, rot), (scale, scale, scale)
    c.hide_render = False
    bpy.context.scene.collection.objects.link(c)
    for slot, kind in zip(c.material_slots, kinds(obj, sp)):
        src = slot.material
        slot.link = "OBJECT"
        slot.material = game_material(src, kind, snow) if mode == "game" else mask_material(src, kind, mode)
    return c


def clear(keep):
    for o in list(bpy.data.objects):
        if o not in keep:
            bpy.data.objects.remove(o)


def look_camera(location, target, ortho_scale=None, lens=50):
    cam = bpy.data.cameras.new("AuditCam")
    if ortho_scale:
        cam.type, cam.ortho_scale = "ORTHO", ortho_scale
    else:
        cam.lens = lens
    o = bpy.data.objects.new("AuditCam", cam)
    o.location = location
    o.rotation_euler = (Vector(target) - Vector(location)).to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.collection.objects.link(o)
    bpy.context.scene.camera = o
    return o


# ---------------------------------------------------------------------------------------------
# Fidelity: TreeImport's frames, coverage only (geometric, so comparable with Unity's report)

def frame(lods):
    """TreeImport.BuildPrefab: the centre and height of LOD0's bounds, and the widest xz reach of any LOD."""
    co0 = arrays(lods[0])["co"]
    lo, hi = co0.min(axis=0), co0.max(axis=0)
    centre = (lo + hi) / 2
    half = max(float(np.hypot(arrays(o)["co"][:, 0] - centre[0], arrays(o)["co"][:, 1] - centre[1]).max()) for o in lods)
    return centre, half, float(hi[2] - lo[2])


def fidelity(sid, sp, lods, tmp, res=160):
    centre, half, height = frame(lods)
    cover = {}
    for key, (ux, uy) in VIEWS.items():
        n = math.hypot(ux, uy)
        s, c = uy / n, ux / n
        d = Vector((-c, 0.0, s))                       # Unity (c, s, 0) toward the viewer, in Blender
        ex, ey = 2 * half * 1.04, (height * c + 2 * half * s) * 1.04
        big = max(ex, ey)
        bpy.context.scene.render.resolution_x = max(8, round(res * ex / big))
        bpy.context.scene.render.resolution_y = max(8, round(res * ey / big))
        dist = height + 2 * half + 10
        cam = look_camera(Vector(centre) + d * dist, Vector(centre), ortho_scale=big)
        cam.data.clip_end = 3 * dist
        cover[key] = []
        for li, o in enumerate(lods):
            inst = instance(o, sp, mode="cover")
            px = render_array(os.path.join(tmp, f"cov_{sid}_{li}_{key}.png"))
            cover[key].append(float(px[..., 3].mean()))
            bpy.data.objects.remove(inst)
        bpy.data.objects.remove(cam)
    out = {"LOD0coverage": [round(cover[k][0], 3) for k in VIEWS]}
    for li in (1, 2):
        out[f"LOD{li}"] = [round(cover[k][li] / max(1e-4, cover[k][0]), 2) for k in VIEWS]
    return out


# ---------------------------------------------------------------------------------------------
# TreeShading (TreeShading.cs), ported: crown radius per band from LOD0's foliage, then per vertex an
# outward crown normal and an occlusion term; and TreeLight's brightness from them.

def crown(lod0, sp):
    d = arrays(lod0)
    fol = d["mat"] != bt.BARK          # TreeShading.Measure: every submesh but bark, hidden leaves included
    fv = np.unique(d["lv"][np.isin(d["face_of_loop"], np.nonzero(fol)[0])])
    p = d["co"][fv]
    bottom, top = float(p[:, 2].min()), float(p[:, 2].max())
    bands = 24
    r = np.hypot(p[:, 0], p[:, 1])
    b = np.clip(((p[:, 2] - bottom) / (top - bottom + 1e-5) * bands).astype(int), 0, bands - 1)
    rad = np.full(bands, -1.0)
    for k in range(bands):
        vals = np.sort(r[b == k])
        if len(vals) >= 4:
            rad[k] = vals[int(len(vals) * 0.9)]
    for k in range(bands):
        if rad[k] < 0:
            lo = k
            while lo >= 0 and rad[lo] < 0:
                lo -= 1
            hi = k
            while hi < bands and rad[hi] < 0:
                hi += 1
            rad[k] = (rad[lo] + rad[hi]) / 2 if lo >= 0 and hi < bands else rad[lo] if lo >= 0 else rad[hi] if hi < bands else 0.5
    for _ in range(2):
        s = rad.copy()
        rad = (np.roll(s, 1) * 1 + 2 * s + np.roll(s, -1)) / 4
        rad[0] = (s[0] + 2 * s[0] + s[1]) / 4
        rad[-1] = (s[-2] + 2 * s[-1] + s[-1]) / 4
    return {"bottom": bottom, "top": top, "radius": rad, "conifer": sp["form"] != "deciduous"}


def radius_at(c, y):
    rad, n = c["radius"], len(c["radius"])
    f = (y - c["bottom"]) / (c["top"] - c["bottom"]) * (n - 1)
    i = np.clip(f.astype(int), 0, n - 2)
    out = rad[i] + (rad[i + 1] - rad[i]) * (f - i)
    return np.where((y < c["bottom"]) | (y > c["top"]), 0.0, out)


def shade(obj, c, sp):
    """(normals, occlusion) per vertex, as TreeShading.Shade writes them into the imported mesh."""
    d = arrays(obj)
    v, face = d["co"], d["vn"] / np.maximum(np.linalg.norm(d["vn"], axis=1, keepdims=True), 1e-9)
    fol_faces = d["mat"] != bt.BARK   # TreeShading.Shade: every submesh but bark
    foliage = np.zeros(len(v), bool)
    foliage[d["lv"][np.isin(d["face_of_loop"], np.nonzero(fol_faces)[0])]] = True
    y = v[:, 2]
    r = np.hypot(v[:, 0], v[:, 1])
    R = radius_at(c, y)
    lean = 0.75 if c["conifer"] else 0.55
    inner = 0.32 if c["conifer"] else 0.62
    up = np.array([0.0, 0.0, 1.0])
    radial = np.where(r[:, None] > 1e-3, np.stack([v[:, 0], v[:, 1], np.zeros_like(r)], 1) / np.maximum(r, 1e-9)[:, None], 0.0)
    h = (c["top"] - c["bottom"]) / len(c["radius"])
    slope = (radius_at(c, np.minimum(c["top"], y + h)) - radius_at(c, np.maximum(c["bottom"], y - h))) / (2 * h)
    depth = np.clip(r / np.maximum(R, 1e-6), 0, 1)
    rise = np.clip((y - c["bottom"]) / (c["top"] - c["bottom"]), 0, 1)
    ao_in = (inner + (1 - inner) * depth ** 0.75) * ((0.78 + 0.22 * rise ** 0.6) if c["conifer"] else (0.9 + 0.1 * rise))
    hull = radial - up * slope[:, None]
    hull /= np.maximum(np.linalg.norm(hull, axis=1, keepdims=True), 1e-9)
    t = np.clip((rise - 0.9) * 10, 0, 1)[:, None]
    omega = np.arccos(np.clip(hull @ up, -1, 1))[:, None]
    so = np.sin(omega)
    slerp = np.where(so > 1e-4, (np.sin((1 - t) * omega) * hull + np.sin(t * omega) * up) / np.maximum(so, 1e-9), hull + (up - hull) * t)
    slerp /= np.maximum(np.linalg.norm(slerp, axis=1, keepdims=True), 1e-9)
    hull_in = np.where((np.linalg.norm(slerp, axis=1) ** 2 < 0.5)[:, None], up, slerp)
    outside = R < 0.05
    ao_out = np.where(y < c["bottom"], 0.9, 1.0)
    hull_out = np.where(((y > c["top"] - 0.5) | (r <= 1e-3))[:, None], up, radial)
    ao = np.where(outside, ao_out, ao_in)
    hull = np.where(outside[:, None], hull_out, hull_in)
    outward = np.where(((face * hull).sum(1) < 0)[:, None], -face, face)
    normals = np.where(foliage[:, None], outward + (hull - outward) * lean, face)
    normals /= np.maximum(np.linalg.norm(normals, axis=1, keepdims=True), 1e-9)
    ground = 0.7 + 0.3 * np.clip((y + 0.2) / 1.7, 0, 1) ** 2 * (3 - 2 * np.clip((y + 0.2) / 1.7, 0, 1))
    ao = np.where(foliage, ao, ao * ground)
    return normals, ao, foliage, d


def bake_lit(obj, c, sp, rot=0.0):
    """Stores TreeLight's brightness per vertex (colour attribute 'Lit') for a copy turned by rot radians."""
    n, ao, foliage, d = shade(obj, c, sp)
    L = np.array([SUN.x * math.cos(-rot) - SUN.y * math.sin(-rot), SUN.x * math.sin(-rot) + SUN.y * math.cos(-rot), SUN.z])
    wrap = np.clip((n @ L + 0.3) / 1.3, 0, 1)
    direct = SUN_RGB[None] * (wrap * (1 + (ao - 1) * 0.6))[:, None]
    sky = (n[:, 2] * 0.5 + 0.5)[:, None]
    ambient = (GROUND_RGB[None] + (SKY_RGB - GROUND_RGB)[None] * sky) * ao[:, None]
    lit = np.clip(direct + ambient, 0, 4)
    m = obj.data
    if "Lit" in m.color_attributes:
        m.color_attributes.remove(m.color_attributes["Lit"])
    attr = m.color_attributes.new(name="Lit", type="FLOAT_COLOR", domain="POINT")
    attr.data.foreach_set("color", np.concatenate([lit, np.ones((len(lit), 1))], 1).astype(np.float32).ravel())
    m.color_attributes.active_color_name = "Wind"
    return ao, foliage, d


# ---------------------------------------------------------------------------------------------
# The audit

def lod_distances(height):
    return [round(height * SCREEN_SCALE / t, 1) for t in TRANSITIONS]


def slope_metrics(lod0, sp, grades=(0, 15, 25, 35)):
    """Krummholz on a slope: foliage under the terrain, and the largest gap under the foliage, with downwind
    pointing downhill, uphill or across the slope. The foot sits on the terrain; the model isn't tilted."""
    d = arrays(lod0)
    fol = foliage_faces(d, kinds(lod0, sp))
    c, a = d["centroid"][fol], d["area"][fol]
    out = {}
    for name, downhill in (("downwind downhill", (0, -1)), ("downwind uphill", (0, 1)), ("across the slope", (1, 0))):
        for g in grades:
            terrain = -math.tan(math.radians(g)) * (c[:, 0] * downhill[0] + c[:, 1] * downhill[1])
            above = c[:, 2] - terrain
            cell = np.floor(c[:, :2] / 0.25).astype(int)
            gaps = {}
            for k, key in enumerate(map(tuple, cell)):
                gaps[key] = min(gaps.get(key, 1e9), above[k])
            out[f"{name} {g}°"] = {"buried": round(float(a[above < 0].sum() / a.sum()), 3),
                                   "largestGap": round(float(max(gaps.values())), 2)}
    return out


def colour_at_distance(sid, sp, lods_by_variant, tmp, snow, elevation=35):
    """The colour a model reads at a distance in the game's shading: LOD0 from elevation degrees,
    coverage-weighted."""
    acc, n = np.zeros(3), 0.0
    for lods in lods_by_variant:
        c = crown(lods[0], sp)
        bake_lit(lods[0], c, sp)
        centre, half, height = frame(lods)
        e = math.radians(elevation)
        d = Vector((-math.cos(e), 0, math.sin(e)))
        ex, ey = 2 * half * 1.04, (height * math.cos(e) + 2 * half * math.sin(e)) * 1.04
        big = max(ex, ey)
        bpy.context.scene.render.resolution_x = max(8, round(128 * ex / big))
        bpy.context.scene.render.resolution_y = max(8, round(128 * ey / big))
        cam = look_camera(Vector(centre) + d * (height + 2 * half + 10), Vector(centre), ortho_scale=big)
        cam.data.clip_end = 4 * (height + 2 * half + 10)
        inst = instance(lods[0], sp, mode="game", snow=snow)
        px = render_array(os.path.join(tmp, f"col_{sid}_{snow}_{elevation}.png"))
        bpy.data.objects.remove(inst)
        bpy.data.objects.remove(cam)
        a = px[..., 3:4]
        acc += (srgb_to_linear(px[..., :3]) * a).sum(axis=(0, 1))
        n += float(a.sum())
    return lab(acc / max(n, 1e-6))


def audit(species, built, out_dir, tmp):
    report = {"settings": {"fov": FOV, "lodBias": LOD_BIAS, "transitions": TRANSITIONS, "cutoff": CUTOFF}, "models": {}}
    unity = {}
    path = os.path.join(REPO, "Assets", "MountainPlanner", "Art", "Trees", "fidelity.json")
    if os.path.exists(path):
        unity = json.load(open(path, encoding="utf-8"))["trees"]
    measure_setup(160, 160)
    for sp in species:
        sid = sp["id"]
        lods_by_variant = built[sid]
        entry = {"form": sp["form"], "textures": texture_metrics(sid, sp), "variants": []}
        for v, lods in enumerate(lods_by_variant):
            ve = {"variant": v, "lods": [geometry(o, sp) for o in lods]}
            top = ve["lods"][0]["top"]
            ve["lodDistances"] = lod_distances(top)
            ve["fidelity"] = fidelity(f"{sid}{v}", sp, lods, tmp)
            u = unity.get(f"{sid}_v{v}")
            if u:
                ve["unityFidelity"] = {"LOD0coverage": u["LOD0coverage"], "LOD1": u["LOD1"]["coverage"], "LOD2": u["LOD2"]["coverage"]}
            c = crown(lods[0], sp)
            ao, foliage, d = bake_lit(lods[0], c, sp)
            fv = foliage
            ve["shading"] = {"meanFoliageAO": round(float(ao[fv].mean()), 3), "darkFoliageShare": round(float((ao[fv] < 0.55).mean()), 3)}
            if sp["form"] == "krummholz":
                ve["slope"] = slope_metrics(lods[0], sp)
            entry["variants"].append(ve)
            print(f"[audit] {sid} v{v}: fidelity {ve['fidelity']}", flush=True)
        entry["colour"] = {"snowFree": colour_at_distance(sid, sp, lods_by_variant, tmp, False),
                           "winter": colour_at_distance(sid, sp, lods_by_variant, tmp, True),
                           "winter20": colour_at_distance(sid, sp, lods_by_variant, tmp, True, elevation=20)}
        report["models"][sid] = entry
    with open(os.path.join(out_dir, "audit.json"), "w") as f:
        json.dump(report, f, indent=1)
    return report


# ---------------------------------------------------------------------------------------------
# Sheets

def sheet_silhouettes(species, built, out, tmp):
    """Every conifer at the same height (25 m), as a black silhouette from the side: what the shape alone says."""
    conifers = [sp for sp in species if sp["form"] == "conifer"]
    x, insts = 0.0, []
    for sp in conifers:
        o = built[sp["id"]][0][0]
        k = 25.0 / max(v.co.z for v in o.data.vertices)
        insts.append(instance(o, sp, loc=(x, 0, 0), scale=k, mode="mask"))
        x += 11.0
    measure_setup(2400, 900, 16)
    look_camera((x / 2 - 5.5, -200, 12.5), (x / 2 - 5.5, 0, 12.5), ortho_scale=x + 2)
    px = render_array(os.path.join(tmp, "sil.png"))
    rgb = np.ones_like(px[..., :3]) * (1 - px[..., 3:4])     # black where covered, white elsewhere
    save(rgb, os.path.join(out, "audit-silhouettes.png"))
    # Overlap of each new tree with the tree it replaces, at the same height (intersection over union).
    clear(keep_list())
    return [sp["name"] for sp in conifers]


def silhouette_iou(species, built, tmp):
    by = {sp["id"]: sp for sp in species}
    masks = {}
    measure_setup(256, 400, 16)
    for sid in ("pacific_silver_fir", "noble_fir", "western_hemlock", "subalpine_fir", "mountain_hemlock", "douglas_fir", "engelmann_spruce"):
        if sid not in built:
            continue
        o = built[sid][0][0]
        k = 25.0 / max(v.co.z for v in o.data.vertices)
        inst = instance(o, by[sid], scale=k, mode="cover")
        look_camera((0, -200, 13), (0, 0, 13), ortho_scale=28)
        masks[sid] = render_array(os.path.join(tmp, f"iou_{sid}.png"))[..., 3] > 0.5
        clear(keep_list())
    out = {}
    for a, b in (("pacific_silver_fir", "subalpine_fir"), ("noble_fir", "subalpine_fir"), ("western_hemlock", "mountain_hemlock"),
                 ("noble_fir", "douglas_fir"), ("pacific_silver_fir", "engelmann_spruce"), ("subalpine_fir", "engelmann_spruce")):
        if a in masks and b in masks:
            out[f"{a} / {b}"] = round(float((masks[a] & masks[b]).sum() / max(1, (masks[a] | masks[b]).sum())), 3)
    return out


_KEEP = []


def keep_list():
    return _KEEP


def save(rgb, path):
    h, w = rgb.shape[:2]
    img = bpy.data.images.new(os.path.basename(path), w, h, alpha=True)
    px = np.ones((h, w, 4), np.float32)
    px[..., :3] = np.clip(rgb, 0, 1)
    img.pixels.foreach_set(px[::-1].ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)
    print("sheet", path, flush=True)


def game_row(entries, path, tmp, snow, width=3000, ground=(0.45, 0.42, 0.36), label_size=1.0):
    """A lineup in the game's shading. entries: (object, species, caption, scale, rot[, LOD0 whose crown
    shades it: TreeShading lights every LOD with LOD0's crown])."""
    x = 0.0
    for entry in entries:
        obj, sp, caption, scale, rot = entry[:5]
        r = max(max(np.hypot(v.co.x, v.co.y) for v in obj.data.vertices) * scale * 0.8 + 0.5, 3.0)
        x += r
        bake_lit(obj, crown(entry[5] if len(entry) > 5 else obj, sp), sp, rot)
        instance(obj, sp, loc=(x, 0, 0), rot=rot, scale=scale, mode="game", snow=snow)
        if caption:
            rp.label(caption, x, -9.02, -1.25, size=label_size)
        x += r + 1.5
    width_m = x - 1.5
    rp.slab(-3, width_m + 3, -9, 9, top=rp.SNOW if snow else (*ground, 1))
    tallest = max(max(v.co.z for v in e[0].data.vertices) * e[3] for e in entries) + 3
    scale = width_m + 6
    ratio = (tallest + 5) / scale
    measure_setup(width, int(width * ratio), 32)
    bpy.context.scene.render.film_transparent = False
    rp.ortho_camera(width_m / 2, tallest / 2 - 2.0, scale)
    # The slab and labels keep their own lit materials: give them light.
    rp.sun()
    rp.render(path)
    clear(keep_list())


def sheet_game(species, built, out, tmp):
    conifers = [sp for sp in species if sp["form"] == "conifer"]
    entries = [(built[sp["id"]][0][0], sp, sp["name"], 1.0, 0.0) for sp in conifers]
    game_row(entries, os.path.join(out, "audit-game-snowfree.png"), tmp, False)
    game_row(entries, os.path.join(out, "audit-game-winter.png"), tmp, True)


def sheet_snow(species, built, out, tmp):
    """The game's snow pattern up close: the middle of each crown from 35 degrees, a few metres away."""
    by = {sp["id"]: sp for sp in species}
    picks = [sid for sid in ("subalpine_fir", "pacific_silver_fir", "noble_fir", "western_hemlock", "mountain_hemlock",
                             "lodgepole_pine", "eastern_white_pine") if sid in built]
    tiles = []
    for sid in picks:
        o = built[sid][0][0]
        bake_lit(o, crown(o, by[sid]), by[sid])
        instance(o, by[sid], mode="game", snow=True)
        top = max(v.co.z for v in o.data.vertices)
        z = top * (0.55 if sid != "noble_fir" else 0.75)
        measure_setup(700, 700, 32)
        bpy.context.scene.render.film_transparent = False
        bpy.context.scene.world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.35, 0.38, 0.42, 1)
        look_camera((-7.5, -7.5, z + 5.2), (0, 0, z), lens=35)
        tiles.append(render_array(os.path.join(tmp, f"snow_{sid}.png"))[..., :3])
        clear(keep_list())
    save(np.concatenate(tiles, 1), os.path.join(out, "audit-snow.png"))
    return picks


def sheet_tops(species, built, out, tmp):
    """The top 30% of each conifer in the game's shading, in winter, from 20 degrees up: the leader and the top
    whorls, where a tree meets the sky (task 09 phase 2: the leader 'fins')."""
    by = {sp["id"]: sp for sp in species}
    picks = [sp["id"] for sp in species if sp["form"] == "conifer" and sp["id"] in built]
    tiles = []
    for sid in picks:
        o = built[sid][0][0]
        bake_lit(o, crown(o, by[sid]), by[sid])
        instance(o, by[sid], mode="game", snow=True)
        top = max(v.co.z for v in o.data.vertices)
        span = 0.3 * top
        centre = Vector((0, 0, top - span / 2))
        measure_setup(600, 700, 32)
        bpy.context.scene.render.film_transparent = False
        bpy.context.scene.world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.35, 0.38, 0.42, 1)
        e = math.radians(20)
        look_camera(centre + Vector((-math.cos(e), 0, math.sin(e))) * (top + 20), centre, ortho_scale=span * 1.15)
        tiles.append(render_array(os.path.join(tmp, f"tops_{sid}.png"))[..., :3])
        clear(keep_list())
    save(np.concatenate(tiles, 1), os.path.join(out, "audit-tops.png"))
    return picks


def sheet_krummholz(species, built, out, tmp):
    """Krummholz in the game's shading next to a subalpine fir scaled to the flag tree's height: how dark the
    forms read once TreeShading's crown occlusion (measured round the stem) is applied."""
    by = {sp["id"]: sp for sp in species}
    if "krummholz" not in built:
        return
    k = by["krummholz"]
    entries = [(m[0], k, ("mat", "flag", "cushion")[v], 1.0, math.pi / 2) for v, m in enumerate(built["krummholz"])]
    if "subalpine_fir" in built:
        o = built["subalpine_fir"][1][0]
        entries.append((o, by["subalpine_fir"], "subalpine fir at 3.2 m", 3.22 / max(v.co.z for v in o.data.vertices), 0.0))
    game_row(entries, os.path.join(out, "audit-krummholz-game.png"), tmp, False, width=2400, label_size=0.45)


def sheet_slope(species, built, out, tmp):
    """Krummholz on a 25 degree slope, foot on the terrain and upright, as the game places it, seen across the
    slope: downwind downhill (left pair: mat, cushion) and downwind uphill (right pair)."""
    by = {sp["id"]: sp for sp in species}
    if "krummholz" not in built:
        return
    grade = math.tan(math.radians(25))
    for rot, xs, text in ((-math.pi / 2, (-12.0, -6.0), "downwind downhill"), (math.pi / 2, (2.0, 8.0), "downwind uphill")):
        for x, v in zip(xs, (0, 2)):
            inst = built["krummholz"][v][0].copy()
            inst.location, inst.rotation_euler, inst.hide_render = (x, 0, x * grade), (0, 0, rot), False
            bpy.context.scene.collection.objects.link(inst)
        mid = sum(xs) / 2
        rp.label(text, mid, -4.2, mid * grade + 3.4, size=0.6)
    x0, x1, depth = -20.0, 14.0, 4.0
    zb = x0 * grade - depth
    v = [(x0, -4, x0 * grade), (x1, -4, x1 * grade), (x1, 4, x1 * grade), (x0, 4, x0 * grade),
         (x0, -4, zb), (x1, -4, zb), (x1, 4, zb), (x0, 4, zb)]
    mesh = bpy.data.meshes.new("Wedge")
    mesh.from_pydata(v, [], [(0, 1, 2, 3), (4, 5, 1, 0), (5, 6, 2, 1), (6, 7, 3, 2), (7, 4, 0, 3), (7, 6, 5, 4)])
    mesh.materials.append(rp.flat_material("Top_slope", rp.SNOW))
    mesh.materials.append(rp.flat_material("Strata", rp.srgb("#6E6A66")))
    mesh.polygons.foreach_set("material_index", [0, 1, 1, 1, 1, 1])
    wedge = bpy.data.objects.new("Wedge", mesh)
    bpy.context.scene.collection.objects.link(wedge)
    rp.sun(elevation=30, azimuth=-35)
    rp.setup_render(1200, 600, 64)
    bpy.context.scene.view_settings.view_transform = "Standard"
    tiles = []
    for mid in (-9.0, 5.0):
        cam = look_camera((mid, -80, mid * grade + 0.8), (mid, 0, mid * grade + 0.8), ortho_scale=13)
        tiles.append(render_array(os.path.join(tmp, f"slope_{mid}.png"))[..., :3])
        bpy.data.objects.remove(cam)
    save(np.concatenate(tiles, 1), os.path.join(out, "audit-slope.png"))
    clear(keep_list())


def sheet_lodpop(species, built, out, tmp):
    """LOD0 beside LOD1 from the side in the game's shading: the crown a tree gains or loses when it switches
    (the fidelity numbers' horizon column)."""
    by = {sp["id"]: sp for sp in species}
    entries = []
    for sid in ("subalpine_fir", "douglas_fir", "pacific_silver_fir", "noble_fir", "western_hemlock"):
        if sid in built:
            lods = built[sid][1]
            entries += [(lods[0], by[sid], f"{by[sid]['name']} LOD0", 1.0, 0.0, lods[0]),
                        (lods[1], by[sid], "LOD1", 1.0, 0.0, lods[0])]
    game_row(entries, os.path.join(out, "audit-lodpop.png"), tmp, False, label_size=1.1)


def sheet_stands(species, built, out, tmp):
    """A Jackson Hole stand (trees already in the game) and a Crystal Mountain stand (mostly new trees) at the
    pixel size the game draws them from 250 m (1080p, 60 degree field of view), each tree at the LOD the game
    would pick (impostors shown as LOD0, which they are pictures of), winter, in the game's shading."""
    by = {sp["id"]: sp for sp in species}
    mixes = {"jackson": (("douglas_fir", 23), ("engelmann_spruce", 20), ("quaking_aspen", 19), ("subalpine_fir", 18), ("lodgepole_pine", 12)),
             "crystal": rp.CRYSTAL}
    distance = 250.0
    px_per_m = 1080 / (2 * distance * math.tan(math.radians(FOV / 2)))
    tiles = []
    for name, mix in mixes.items():
        mix = [(s, w) for s, w in mix if s in built]
        rng = random.Random(5)
        spots = []
        while len(spots) < 45:
            p = Vector((rng.uniform(-22, 22), rng.uniform(-22, 22), 0))
            if all((p - q).length > 4.5 for q in spots):
                spots.append(p)
        total = sum(w for _, w in mix)
        baked = set()
        for p in spots:
            r = rng.random() * total
            for sid, w in mix:
                r -= w
                if r <= 0:
                    break
            v = rng.randrange(3)
            lods = built[sid][v]
            height = max(vt.co.z for vt in lods[0].data.vertices)
            screen = height * SCREEN_SCALE / distance
            li = 0 if screen > TRANSITIONS[0] else 1 if screen > TRANSITIONS[1] else 2 if screen > TRANSITIONS[2] else 0
            o = lods[li]
            if o.name not in baked:
                bake_lit(o, crown(lods[0], by[sid]), by[sid])
                baked.add(o.name)
            instance(o, by[sid], loc=p, mode="game", snow=True)
        rp.slab(-28, 28, -28, 28, depth=3, top=rp.SNOW)
        rp.sun(elevation=40, azimuth=-35)
        size = 64.0
        res = int(size * px_per_m)
        measure_setup(res, res, 64)
        bpy.context.scene.render.film_transparent = False
        e = math.radians(35)
        look_camera((0, -300 * math.cos(e), 300 * math.sin(e)), (0, 0, 6), ortho_scale=size)
        px = render_array(os.path.join(tmp, f"stand_{name}.png"))[..., :3]
        tiles.append(np.kron(px, np.ones((4, 4, 1))))   # nearest-neighbour x4, to see the pixels
        clear(keep_list())
    save(np.concatenate(tiles, 1), os.path.join(out, "audit-stands-250m.png"))


def sheet_textures(species, built, out, tmp):
    """Spray (top row) and branch-cluster (bottom row) textures of every conifer and krummholz, on mid grey."""
    ids = [sp["id"] for sp in species if sp["form"] != "deciduous"]
    size, pad = 256, 10
    sheet = np.full((2 * (size + pad) + pad, len(ids) * (size + pad) + pad, 3), 0.42, np.float32)
    for c, sid in enumerate(ids):
        for r, kind in enumerate(("spray", "cluster")):
            px = image_array(f"{sid}_{kind}")
            if px is None:
                continue
            step = max(1, px.shape[1] // size)
            px = px[::step, ::step][:size, :size]
            y, x = pad + r * (size + pad), pad + c * (size + pad)
            a = px[..., 3:4]
            sheet[y:y + px.shape[0], x:x + px.shape[1]] = px[..., :3] * a + sheet[y:y + px.shape[0], x:x + px.shape[1]] * (1 - a)
    save(sheet, os.path.join(out, "audit-textures.png"))
    return ids


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    opts = {argv[i][2:]: argv[i + 1] for i in range(0, len(argv) - 1, 2) if argv[i].startswith("--")}
    out_dir = os.path.abspath(opts.get("out", os.path.join(HERE, "out", "audit")))
    tmp = os.path.join(out_dir, "tmp")
    sheets_dir = os.path.join(out_dir, "sheets")
    for d in (out_dir, tmp, sheets_dir):
        os.makedirs(d, exist_ok=True)
    species = json.load(open(os.path.join(HERE, "species.json"), encoding="utf-8"))["species"]
    if "species" in opts:
        species = [s for s in species if s["id"] in opts["species"].split(",")]
    # Build into the audit folder (not out/, the import source).
    sys.argv = [sys.argv[0], "--", "--out", os.path.join(out_dir, "build"), "--species", ",".join(s["id"] for s in species)]
    over = bt.main()
    built = {sp["id"]: [[bpy.data.objects[f"{sp['id']}_v{v}_LOD{l}"] for l in range(3)] for v in range(3)] for sp in species}
    for o in bpy.data.objects:
        o.hide_render = True
    _KEEP.extend(bpy.data.objects)
    want = (lambda n: True) if "sheets" not in opts else (lambda n: n in opts["sheets"].split(","))
    report = audit(species, built, out_dir, tmp) if opts.get("measure", "1") != "0" else {}
    extra = {"overBudget": over}
    if want("silhouettes"):
        sheet_silhouettes(species, built, sheets_dir, tmp)
        extra["silhouetteIoU"] = silhouette_iou(species, built, tmp)
    if want("game"):
        sheet_game(species, built, sheets_dir, tmp)
    if want("snow"):
        sheet_snow(species, built, sheets_dir, tmp)
    if want("tops"):
        sheet_tops(species, built, sheets_dir, tmp)
    if want("krummholz"):
        sheet_krummholz(species, built, sheets_dir, tmp)
    if want("slope"):
        sheet_slope(species, built, sheets_dir, tmp)
        if "krummholz" in built:
            k = next(sp for sp in species if sp["id"] == "krummholz")
            extra["slope"] = {f"v{v}": slope_metrics(built["krummholz"][v][0], k) for v in range(3)}
    if want("lodpop"):
        sheet_lodpop(species, built, sheets_dir, tmp)
    if want("stands"):
        sheet_stands(species, built, sheets_dir, tmp)
    if want("textures"):
        sheet_textures(species, built, sheets_dir, tmp)
    report["extra"] = extra
    with open(os.path.join(out_dir, "audit.json"), "w") as f:
        json.dump(report, f, indent=1)
    print("[audit] wrote", os.path.join(out_dir, "audit.json"), flush=True)


if __name__ == "__main__":
    main()
