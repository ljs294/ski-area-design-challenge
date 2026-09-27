"""Review renders for build_trees.py (Cycles). Imported by build_trees.py when --render DIR is given.

Shots:
  trees-conifers.png          the conifers, two variants each, snow-loaded, with a 1.8 m skier for scale
  trees-deciduous-winter.png  the deciduous species bare, with a dusting of snow (beech keeps dry leaves)
  trees-deciduous-autumn.png  the same in autumn colour
  trees-seasons.png           sugar maple and paper birch in winter, summer and autumn
  trees-closeup.png           conifers from a few metres away, to judge needles and bark
  trees-grove-rockies.png     a Jackson Hole mix from a game-like camera
  trees-grove-newengland.png  a Jackson, NH mix from a game-like camera
  trees-lods.png              LOD0 / LOD1 / LOD2 with triangle counts
  trees-data-snow.png         the snow mask (blue = none, white = full load)
  trees-data-wind.png         wind weights (red = trunk sway, green = branch flex, blue = flutter)
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
    s.cycles.transparent_max_bounces = 128
    s.cycles.max_bounces = 8
    s.render.resolution_x, s.render.resolution_y = width, height
    s.render.resolution_percentage = 100
    s.render.image_settings.file_format = "PNG"
    s.view_settings.view_transform = "AgX"
    s.view_settings.look = "AgX - Medium High Contrast"
    world = bpy.data.worlds.get("Sky") or bpy.data.worlds.new("Sky")
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs["Color"].default_value = srgb("#C9DCEE")
    bg.inputs["Strength"].default_value = 0.9
    s.world = world


def clear_scene(keep):
    for o in list(bpy.data.objects):
        if o not in keep:
            bpy.data.objects.remove(o)
    bpy.context.view_layer.material_override = None


def place(obj, loc, rot=0.0, scale=1.0):
    c = obj.copy()
    c.location = loc
    c.rotation_euler = (0, 0, rot)
    c.scale = (scale, scale, scale)
    c.hide_render = False
    bpy.context.scene.collection.objects.link(c)
    return c


def sun(elevation=32, azimuth=-35, strength=3.4):
    d = bpy.data.lights.new("Sun", "SUN")
    d.energy = strength
    d.angle = math.radians(2.0)
    d.color = srgb("#FFF1E0")[:3]
    o = bpy.data.objects.new("Sun", d)
    o.rotation_euler = (math.radians(90 - elevation), 0, math.radians(azimuth))
    bpy.context.scene.collection.objects.link(o)


def slab(x0, x1, y0, y1, depth=1.6, top=SNOW):
    """A diorama base with a rock strata front (A1)."""
    mesh = bpy.data.meshes.new("Slab")
    v = [(x0, y0, 0), (x1, y0, 0), (x1, y1, 0), (x0, y1, 0), (x0, y0, -depth), (x1, y0, -depth), (x1, y1, -depth), (x0, y1, -depth)]
    mesh.from_pydata(v, [], [(0, 1, 2, 3), (4, 5, 1, 0), (5, 6, 2, 1), (6, 7, 3, 2), (7, 4, 0, 3), (7, 6, 5, 4)])
    mesh.materials.append(flat_material("Top_%s" % str(top[:3]), top))
    mesh.materials.append(flat_material("Strata", srgb("#6E6A66")))
    mesh.polygons.foreach_set("material_index", [0, 1, 1, 1, 1, 1])
    o = bpy.data.objects.new("Slab", mesh)
    bpy.context.scene.collection.objects.link(o)


def label(text, x, y, z, size=1.1, colour="#F4F7FA"):
    cu = bpy.data.curves.new("Label", "FONT")
    cu.body = text
    cu.size = size
    cu.align_x = "CENTER"
    o = bpy.data.objects.new("Label", cu)
    o.location = (x, y, z)
    o.rotation_euler = (math.radians(90), 0, 0)
    o.data.materials.append(flat_material("Ink_" + colour, srgb(colour), emission=True))
    bpy.context.scene.collection.objects.link(o)


def skier(x, y):
    """A 1.8 m figure for scale."""
    bpy.ops.mesh.primitive_cylinder_add(radius=0.22, depth=1.5, location=(x, y, 0.75))
    bpy.context.object.data.materials.append(flat_material("Jacket", srgb("#C0392B")))
    bpy.ops.mesh.primitive_uv_sphere_add(radius=0.15, location=(x, y, 1.65))
    bpy.context.object.data.materials.append(flat_material("Skin", srgb("#E0B89A")))


def ortho_camera(cx, cz, scale, tilt=6.0):
    cam = bpy.data.cameras.new("Cam")
    cam.type = "ORTHO"
    cam.ortho_scale = scale
    cam.sensor_fit = "HORIZONTAL"  # ortho_scale is always the frame width
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
    o.rotation_euler = (Vector(target) - o.location).to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.collection.objects.link(o)
    bpy.context.scene.camera = o


def render(path):
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print("rendered", path, flush=True)


def set_season(species, seasons, season):
    """Swap leaf textures and snow for winter, summer or autumn."""
    winter = season == "winter"
    for m in bpy.data.materials:
        if "SnowLoad" in (m.node_tree.nodes if m.node_tree else []):
            base = 0.6 if m.name.endswith("_Bark") else 0.5 if m.name.endswith("_Twigs") else 1.0
            if m.name.endswith("_Leaves") or m.name.endswith("_LeavesKept"):
                base = 0.0
            m.node_tree.nodes["SnowLoad"].outputs[0].default_value = base if winter else 0.0
    for sp in species:
        imgs = seasons.get(sp["id"]) or {}
        if not imgs:
            continue
        leaves = bpy.data.materials[f"{sp['id']}_Leaves"].node_tree.nodes
        leaves["Tex"].image = imgs["autumn" if season == "autumn" else "summer"]
        leaves["WinterHide"].outputs[0].default_value = 1.0 if winter else 0.0
        kept = bpy.data.materials[f"{sp['id']}_LeavesKept"].node_tree.nodes
        kept["Tex"].image = imgs.get("kept", imgs["autumn"]) if season != "summer" else imgs["summer"]


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


def radius_of(obj):
    return max(math.hypot(v.co.x, v.co.y) for v in obj.data.vertices)


def row(entries, gap=1.5, label_size=1.1, label_y=-9.02):
    """Places (object, caption) pairs in a row; returns the row's width."""
    x = 0.0
    for obj, caption in entries:
        r = max(radius_of(obj) * 0.8 + 0.5, 3.5)
        x += r
        place(obj, (x, 0, 0), rot=0.5)
        if caption:
            label(caption, x, label_y, -1.25, size=label_size)
        x += r + gap
    return x - gap


def lineup_shot(entries, path, season, species, seasons, originals, top=SNOW, extra=None, label_size=1.1):
    set_season(species, seasons, season)
    width = row(entries, label_size=label_size)
    if season == "winter":
        skier(width + 2.5, -3)
    slab(-3, width + 5, -9, 9, top=top)
    sun()
    tallest = max(o.dimensions.z for o, _ in entries) + 3
    scale = width + 10
    ratio = (tallest + 5) / scale
    res_x = 3000 if ratio <= 0.6 else int(1800 / ratio)
    setup_render(res_x, int(res_x * ratio))
    ortho_camera(width / 2 + 1, tallest / 2 - 2.0, scale)
    if extra:
        extra()
    render(path)
    clear_scene(originals)


def grove_shot(mix, path, species, seasons, originals, built, season="winter", seed=7):
    set_season(species, seasons, season)
    rng = random.Random(seed)
    spots = []
    while len(spots) < 60:
        p = Vector((rng.uniform(-28, 28), rng.uniform(-28, 28), 0))
        if all((p - q).length > 4.0 for q in spots):
            spots.append(p)
    total = sum(w for _, w in mix)
    for p in spots:
        r = rng.random() * total
        for sid, w in mix:
            r -= w
            if r <= 0:
                break
        v = rng.choice(built[sid])
        place(v["objects"][0], p, rot=rng.uniform(0, 6.28), scale=rng.uniform(0.85, 1.1))
    skier(4, -20)
    slab(-32, 32, -32, 32, depth=4, top=SNOW if season == "winter" else srgb("#6B5A45"))
    sun(elevation=28, azimuth=-50)
    setup_render(2400, 1350, samples=96)
    persp_camera((0, 0, 5), 95, 30, -25)
    render(path)
    clear_scene(originals)


def render_all(species, built, seasons, out_dir, shots=None):
    """shots: optional comma-separated subset, e.g. "conifers,closeup"."""
    os.makedirs(out_dir, exist_ok=True)
    want = (lambda name: True) if not shots else (lambda name: name in shots.split(","))
    originals = [o for m in built.values() for v in m for o in v["objects"]]
    for o in originals:
        o.hide_render = True
    by_id = {sp["id"]: sp for sp in species}
    conifers = [sp for sp in species if sp["form"] == "conifer"]
    deciduous = [sp for sp in species if sp["form"] == "deciduous"]

    def lod0(sid, v=0):
        return built[sid][v]["objects"][0]

    if conifers and want("conifers"):
        entries = [(lod0(sp["id"], v), sp["name"] if v == 0 else "") for sp in conifers for v in (0, 1)]
        lineup_shot(entries, os.path.join(out_dir, "trees-conifers.png"), "winter", species, seasons, originals)
        for kind in ("snow", "wind") if want("data") else ():
            set_season(species, seasons, "winter")
            row(entries)
            slab(-3, 200, -9, 9)
            sun()
            width = sum(max(radius_of(o) * 0.8 + 0.5, 3.5) * 2 + 1.5 for o, _ in entries)
            tallest = max(o.dimensions.z for o, _ in entries) + 3
            setup_render(3000, int(3000 * min(0.6, (tallest + 2) / (width + 10))), samples=16)
            ortho_camera(width / 2 + 1, tallest / 2 - 0.5, width + 10)
            bpy.context.view_layer.material_override = override(kind)
            render(os.path.join(out_dir, f"trees-data-{kind}.png"))
            clear_scene(originals)

    if deciduous and want("deciduous"):
        entries = [(lod0(sp["id"]), sp["name"]) for sp in deciduous]
        lineup_shot(entries, os.path.join(out_dir, "trees-deciduous-winter.png"), "winter", species, seasons, originals)
        lineup_shot(entries, os.path.join(out_dir, "trees-deciduous-autumn.png"), "autumn", species, seasons, originals,
                    top=srgb("#6B5A45"))

    if deciduous and want("seasons"):
        # Seasons: the same trees in winter, summer and autumn, side by side (three renders).
        pick = [sid for sid in ("sugar_maple", "paper_birch") if sid in by_id] or [deciduous[0]["id"]]
        for season in ("winter", "summer", "autumn"):
            entries = [(lod0(sid), f"{by_id[sid]['name']}, {season}") for sid in pick]
            lineup_shot(entries, os.path.join(out_dir, f"trees-season-{season}.png"), season, species, seasons, originals,
                        top=SNOW if season == "winter" else srgb("#6B5A45" if season == "autumn" else "#5E6B3A"))

    if conifers and want("closeup"):
        # Close-up at eye level.
        set_season(species, seasons, "winter")
        for i, sp in enumerate(conifers[:3]):
            place(lod0(sp["id"]), (i * 7.0 - 7.0, 0, 0), rot=0.4 + i)
        skier(3.5, -4)
        slab(-15, 15, -12, 12, top=SNOW)
        sun(elevation=25, azimuth=-60)
        setup_render(2400, 1350, samples=96)
        persp_camera((0, 0, 4.5), 22, 8, -15, lens=35)
        render(os.path.join(out_dir, "trees-closeup.png"))
        clear_scene(originals)

    rockies = [(sid, w) for sid, w in (("douglas_fir", 23), ("engelmann_spruce", 20), ("quaking_aspen", 19),
                                        ("subalpine_fir", 18), ("lodgepole_pine", 12)) if sid in by_id]
    if rockies and want("groves"):
        grove_shot(rockies, os.path.join(out_dir, "trees-grove-rockies.png"), species, seasons, originals, built)
    nh = [(sid, w) for sid, w in (("red_maple", 19), ("yellow_birch", 14), ("sugar_maple", 9), ("american_beech", 8),
                                  ("paper_birch", 4)) if sid in by_id]
    if nh and want("groves"):
        grove_shot(nh, os.path.join(out_dir, "trees-grove-newengland.png"), species, seasons, originals, built)
        grove_shot(nh, os.path.join(out_dir, "trees-grove-newengland-autumn.png"), species, seasons, originals, built, season="autumn")

    # LOD strip.
    picks = [sid for sid in ("subalpine_fir", "douglas_fir", "sugar_maple") if sid in by_id]
    entries = []
    for sid in picks:
        v0 = built[sid][0]
        entries += [(o, f"LOD{li} · {v0['triangles'][li]:,} tris") for li, o in enumerate(v0["objects"])]
    if entries and want("lods"):
        lineup_shot(entries, os.path.join(out_dir, "trees-lods.png"), "winter", species, seasons, originals, label_size=0.9)
