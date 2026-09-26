"""Review renders for build_trees.py (Cycles). Imported by build_trees.py when --render DIR is given.

Shots:
  trees-lineup.png      every species and variant (LOD0) on a diorama slab, with a 1.8 m skier for scale
  trees-lods.png        LOD0 / LOD1 / LOD2 of variant 0 per species, triangle counts, and aspen in leaf
  trees-grove.png       a mixed grove seen from a game-like camera angle
  trees-data-snow.png   the snow mask the shader reads (blue = none, white = full load)
  trees-data-wind.png   the wind weights (red = trunk sway, green = branch flex, blue = leaf flutter)
"""
import math
import os
import random

import bpy
from mathutils import Vector

SNOW = (0.90, 0.93, 0.95, 1)


def srgb(hex_colour):
    return tuple((int(hex_colour[i:i + 2], 16) / 255) ** 2.2 for i in (1, 3, 5)) + (1,)


def flat_material(name, rgba, emission=False):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = rgba
    bsdf.inputs["Roughness"].default_value = 0.9
    if emission:
        bsdf.inputs["Emission Color"].default_value = rgba
        bsdf.inputs["Emission Strength"].default_value = 1.0
    return m


def setup_render(width, height, samples=64):
    s = bpy.context.scene
    s.render.engine = "CYCLES"
    prefs = bpy.context.preferences.addons["cycles"].preferences
    for kind in ("OPTIX", "CUDA"):
        try:
            prefs.compute_device_type = kind
            prefs.get_devices()
            if any(d.type == kind for d in prefs.devices):
                for d in prefs.devices:
                    d.use = d.type == kind
                s.cycles.device = "GPU"
                break
        except TypeError:
            continue
    s.cycles.samples = samples
    s.cycles.use_denoising = True
    s.cycles.transparent_max_bounces = 128  # many overlapping hidden leaf cards
    s.render.resolution_x, s.render.resolution_y = width, height
    s.render.resolution_percentage = 100
    s.render.image_settings.file_format = "PNG"
    s.view_settings.view_transform = "AgX"
    s.view_settings.look = "AgX - Medium High Contrast"
    world = bpy.data.worlds.get("Sky") or bpy.data.worlds.new("Sky")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = srgb("#DCEBF7")
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.7
    s.world = world


def clear_scene_objects(keep):
    for o in list(bpy.data.objects):
        if o not in keep:
            bpy.data.objects.remove(o)


def place(obj, loc, rot=0.0, scale=1.0, collection=None):
    c = obj.copy()
    c.location = loc
    c.rotation_euler = (0, 0, rot)
    c.scale = (scale, scale, scale)
    c.hide_render = False
    (collection or bpy.context.scene.collection).objects.link(c)
    return c


def sun(elevation=35, azimuth=-35, strength=3.2):
    d = bpy.data.lights.new("Sun", "SUN")
    d.energy = strength
    d.angle = math.radians(2.5)
    d.color = srgb("#FFF1E0")[:3]
    o = bpy.data.objects.new("Sun", d)
    o.rotation_euler = (math.radians(90 - elevation), 0, math.radians(azimuth))
    bpy.context.scene.collection.objects.link(o)


def slab(x0, x1, y0, y1, depth=1.6):
    """Snow-topped diorama base with a rock strata front (A1)."""
    mesh = bpy.data.meshes.new("Slab")
    v = [(x0, y0, 0), (x1, y0, 0), (x1, y1, 0), (x0, y1, 0), (x0, y0, -depth), (x1, y0, -depth), (x1, y1, -depth), (x0, y1, -depth)]
    f = [(0, 1, 2, 3), (4, 5, 1, 0), (5, 6, 2, 1), (6, 7, 3, 2), (7, 4, 0, 3), (7, 6, 5, 4)]
    mesh.from_pydata(v, [], f)
    mesh.materials.append(flat_material("SnowTop", SNOW))
    mesh.materials.append(flat_material("Strata", srgb("#6E6A66")))
    mesh.polygons.foreach_set("material_index", [0, 1, 1, 1, 1, 1])
    o = bpy.data.objects.new("Slab", mesh)
    bpy.context.scene.collection.objects.link(o)
    return o


def label(text, x, y, z, size=1.1, colour="#F4F7FA"):
    cu = bpy.data.curves.new("Label", "FONT")
    cu.body = text
    cu.size = size
    cu.align_x = "CENTER"
    o = bpy.data.objects.new("Label", cu)
    o.location = (x, y, z)
    o.rotation_euler = (math.radians(90), 0, 0)
    o.data.materials.append(flat_material("LabelInk_" + colour, srgb(colour), emission=True))
    bpy.context.scene.collection.objects.link(o)


def skier(x, y):
    """A 1.8 m figure for scale."""
    m = flat_material("Jacket", srgb("#C0392B"))
    bpy.ops.mesh.primitive_cylinder_add(radius=0.22, depth=1.5, location=(x, y, 0.75))
    body = bpy.context.object
    body.data.materials.append(m)
    bpy.ops.mesh.primitive_uv_sphere_add(radius=0.15, location=(x, y, 1.65))
    bpy.context.object.data.materials.append(flat_material("Skin", srgb("#E0B89A")))


def ortho_camera(cx, cz, scale, tilt=8.0):
    cam = bpy.data.cameras.new("Cam")
    cam.type = "ORTHO"
    cam.ortho_scale = scale
    o = bpy.data.objects.new("Cam", cam)
    t = math.radians(tilt)
    o.location = (cx, -300 * math.cos(t), cz + 300 * math.sin(t))
    o.rotation_euler = (math.radians(90) - t, 0, 0)
    bpy.context.scene.collection.objects.link(o)
    bpy.context.scene.camera = o


def persp_camera(target, distance, elevation, azimuth, lens=40):
    cam = bpy.data.cameras.new("Cam")
    cam.lens = lens
    o = bpy.data.objects.new("Cam", cam)
    e, a = math.radians(elevation), math.radians(azimuth)
    o.location = Vector(target) + Vector((math.sin(a) * math.cos(e), -math.cos(a) * math.cos(e), math.sin(e))) * distance
    direction = Vector(target) - o.location
    o.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.collection.objects.link(o)
    bpy.context.scene.camera = o


def render(path):
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print("rendered", path, flush=True)


def override(kind):
    """A view-layer material override that shows the shader's mesh data."""
    m = bpy.data.materials.new("Data_" + kind)
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    em = nt.nodes.new("ShaderNodeEmission")
    if kind == "snow":
        uv = nt.nodes.new("ShaderNodeUVMap")
        uv.uv_map = "Data"
        sep = nt.nodes.new("ShaderNodeSeparateXYZ")
        mix = nt.nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        mix.inputs["A"].default_value = srgb("#1F3F77")
        mix.inputs["B"].default_value = (1, 1, 1, 1)
        nt.links.new(uv.outputs["UV"], sep.inputs[0])
        nt.links.new(sep.outputs["X"], mix.inputs["Factor"])
        nt.links.new(mix.outputs["Result"], em.inputs["Color"])
    else:
        attr = nt.nodes.new("ShaderNodeVertexColor")
        attr.layer_name = "Wind"
        sep = nt.nodes.new("ShaderNodeSeparateColor")
        comb = nt.nodes.new("ShaderNodeCombineColor")
        nt.links.new(attr.outputs["Color"], sep.inputs[0])
        nt.links.new(sep.outputs["Red"], comb.inputs["Red"])
        nt.links.new(sep.outputs["Green"], comb.inputs["Green"])
        nt.links.new(attr.outputs["Alpha"], comb.inputs["Blue"])
        nt.links.new(comb.outputs["Color"], em.inputs["Color"])
    nt.links.new(em.outputs["Emission"], out.inputs["Surface"])
    return m


def lineup(species, built, extra_gap=6.0):
    """Places LOD0 of every variant in a row; returns the row's x extent."""
    x = 0.0
    positions = []
    for sp in species:
        start = x
        for m in built[sp["id"]]:
            obj = m["objects"][0]
            radius = max(abs(v.co.x) for v in obj.data.vertices) + 0.5
            x += radius
            place(obj, (x, 0, 0), rot=0.4)
            positions.append(x)
            x += radius + 1.5
        label(sp["name"], (start + x) / 2, -9.02, -1.25)
        x += extra_gap
    return x - extra_gap


def render_all(species, built, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    originals = [o for m in built.values() for v in m for o in v["objects"]]
    for o in originals:
        o.hide_render = True

    # 1. Lineup.
    width = lineup(species, built)
    skier(width + 3, -2)
    slab(-4, width + 6, -9, 9)
    sun()
    scale = width + 12
    setup_render(3000, int(3000 * 27 / scale))
    ortho_camera(width / 2 + 1, 11.2, scale)
    render(os.path.join(out_dir, "trees-lineup.png"))

    # 4 and 5. The same lineup, showing the mesh data the shader reads.
    for kind in ("snow", "wind"):
        bpy.context.view_layer.material_override = override(kind)
        render(os.path.join(out_dir, f"trees-data-{kind}.png"))
    bpy.context.view_layer.material_override = None
    clear_scene_objects(originals)

    # 2. LOD strip, plus the aspen in leaf to show the separate leaf geometry (TR4).
    x = 0.0
    for sp in species:
        v0 = built[sp["id"]][0]
        for li, obj in enumerate(v0["objects"]):
            r = max(abs(v.co.x) for v in obj.data.vertices) + 0.8
            r = max(r, 4.8)  # room for the label
            x += r
            place(obj, (x, 0, 0), rot=0.4)
            label(f"LOD{li} · {v0['triangles'][li]:,} tris", x, -9.02, -1.25, size=0.9)
            x += r + 1.0
        x += 5
    aspen = [sp for sp in species if sp["form"] == "deciduous"]
    if aspen:
        obj = built[aspen[0]["id"]][0]["objects"][0].copy()
        obj.data = obj.data.copy()
        leaves = [i for i, m in enumerate(obj.data.materials) if "Leaves" in m.name]
        for i in leaves:
            summer = bpy.data.materials.new("Leaves_Summer")
            summer.use_nodes = True
            summer.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = srgb("#6F8F3F")
            obj.data.materials[i] = summer
        r = max(abs(v.co.x) for v in obj.data.vertices) + 0.8
        x += r
        place(obj, (x, 0, 0), rot=0.4)
        label("In leaf (seasons later)", x, -9.02, -1.25, size=0.9)
        x += r
    slab(-3, x + 3, -9, 9)
    sun()
    scale = x + 8
    setup_render(3000, int(3000 * 27 / scale))
    ortho_camera(x / 2, 11.2, scale)
    render(os.path.join(out_dir, "trees-lods.png"))
    clear_scene_objects(originals)

    # 3. A mixed grove from a game-like camera.
    rng = random.Random(7)
    conifers = [sp for sp in species if sp["form"] == "conifer"]
    pool = [(sp, w) for sp in conifers for w in (0.45,)] + [(sp, 0.1) for sp in aspen]
    spots = []
    while len(spots) < 55:
        p = Vector((rng.uniform(-28, 28), rng.uniform(-28, 28), 0))
        if all((p - q).length > 4.2 for q in spots):
            spots.append(p)
    for p in spots:
        r = rng.random() * sum(w for _, w in pool)
        for sp, w in pool:
            r -= w
            if r <= 0:
                break
        v = rng.choice(built[sp["id"]])
        place(v["objects"][0], p, rot=rng.uniform(0, 6.28), scale=rng.uniform(0.8, 1.1))
    skier(4, -20)
    slab(-32, 32, -32, 32, depth=4)
    sun(elevation=28, azimuth=-50)
    setup_render(2400, 1350, samples=96)
    persp_camera((0, 0, 4), 95, 32, -25)
    render(os.path.join(out_dir, "trees-grove.png"))
