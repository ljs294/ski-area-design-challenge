"""Review renders for build_trees.py (Cycles). Imported by build_trees.py when --render DIR is given.

Shots:
  trees-conifers.png          the conifers, two variants each, snow-loaded, with a 1.8 m skier for scale
  trees-deciduous-winter.png  the deciduous species bare, with a dusting of snow (beech keeps dry leaves)
  trees-deciduous-autumn.png  the same in autumn colour
  trees-season-*.png          sugar maple and paper birch through five stages: winter, spring leaf-out,
                              summer, peak autumn, late-autumn leaf drop
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


def label(text, x, y, z, size=1.1, colour="#F4F7FA", flat=False, rot=0.0):
    """A caption: upright facing -Y, or lying flat (read from above) and turned by rot."""
    cu = bpy.data.curves.new("Label", "FONT")
    cu.body = text
    cu.size = size
    cu.align_x = "CENTER"
    o = bpy.data.objects.new("Label", cu)
    o.location = (x, y, z)
    o.rotation_euler = (0, 0, rot) if flat else (math.radians(90), 0, rot)
    o.data.materials.append(flat_material("Ink_" + colour, srgb(colour), emission=True))
    bpy.context.scene.collection.objects.link(o)


def skier(x, y, z=0.0):
    """A 1.8 m figure for scale."""
    bpy.ops.mesh.primitive_cylinder_add(radius=0.22, depth=1.5, location=(x, y, z + 0.75))
    bpy.context.object.data.materials.append(flat_material("Jacket", srgb("#C0392B")))
    bpy.ops.mesh.primitive_uv_sphere_add(radius=0.15, location=(x, y, z + 1.65))
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


# Season stages, as the game's tree shader will drive them (see the review page, "Seasons").
STAGES = {
    "winter":      dict(show=0.0, colour=0.0, tint=0.0, kept="kept"),
    "spring":      dict(show=0.35, colour=0.0, tint=1.0, kept="kept"),
    "summer":      dict(show=1.0, colour=0.0, tint=0.0, kept="summer"),
    "autumn":      dict(show=1.0, colour=1.0, tint=0.0, kept="autumn"),
    "late-autumn": dict(show=0.2, colour=1.0, tint=0.0, kept="kept"),
}


def set_season(species, seasons, season):
    """Drive leaves, colour and snow for a season stage."""
    stage = STAGES[season]
    winter = season == "winter"
    for m in bpy.data.materials:
        nodes = m.node_tree.nodes if m.node_tree else []
        if "SnowLoad" in nodes:
            base = 0.6 if m.name.endswith("_Bark") else 0.5 if m.name.endswith("_Twigs") else 1.0
            if m.name.endswith("_Leaves") or m.name.endswith("_LeavesKept"):
                base = 0.0
            nodes["SnowLoad"].outputs[0].default_value = base if winter else 0.0
        if "LeafShow" in nodes:
            nodes["LeafShow"].outputs[0].default_value = stage["show"]
            nodes["ColourMix"].outputs[0].default_value = stage["colour"]
            nodes["Tint"].outputs[0].default_value = stage["tint"]
    for sp in species:
        imgs = seasons.get(sp["id"]) or {}
        if not imgs:
            continue
        kept = bpy.data.materials[f"{sp['id']}_LeavesKept"].node_tree.nodes
        img = imgs.get(stage["kept"]) or imgs["autumn"]
        kept["Tex"].image = img
        kept["Tex2"].image = img


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
    """Places (object, caption) or (object, caption, scale) entries in a row; returns the row's width."""
    x = 0.0
    for entry in entries:
        obj, caption = entry[:2]
        scale = entry[2] if len(entry) > 2 else 1.0
        r = max(radius_of(obj) * scale * 0.8 + 0.5, 3.5)
        x += r
        place(obj, (x, 0, 0), rot=0.5, scale=scale)
        if caption:
            label(caption, x, label_y, -1.25, size=label_size)
        x += r + gap
    return x - gap


def lineup_shot(entries, path, season, species, seasons, originals, top=SNOW, extra=None, label_size=1.1, depth=1.6):
    set_season(species, seasons, season)
    width = row(entries, label_size=label_size)
    if season == "winter":
        skier(width + 2.5, -3)
    slab(-3, width + 5, -9, 9, top=top, depth=depth)
    sun()
    tallest = max(e[0].dimensions.z * (e[2] if len(e) > 2 else 1.0) for e in entries) + 3
    scale = width + 10
    ratio = (tallest + 5) / scale
    res_x = 3000 if ratio <= 0.6 else int(1800 / ratio)
    setup_render(res_x, int(res_x * ratio))
    ortho_camera(width / 2 + 1, tallest / 2 - 2.0, scale)
    if extra:
        extra()
    render(path)
    clear_scene(originals)


def grove_shot(mix, path, species, seasons, originals, built, season="winter", seed=7, camera=((0, 0, 5), 95, 30, -25)):
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
    persp_camera(*camera)
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
        grounds = {"winter": SNOW, "spring": srgb("#6F7A45"), "summer": srgb("#5E6B3A"), "autumn": srgb("#6B5A45"),
                   "late-autumn": srgb("#5E5040")}
        for season in STAGES:
            entries = [(lod0(sid), f"{by_id[sid]['name']}, {season.replace('-', ' ')}") for sid in pick]
            lineup_shot(entries, os.path.join(out_dir, f"trees-season-{season}.png"), season, species, seasons, originals,
                        top=grounds[season], label_size=0.9)

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

    render_task09(species, built, seasons, out_dir, want, originals)
    render_phase2(species, built, seasons, out_dir, want, originals)


# ---------------------------------------------------------------------------------------------
# Task 09 review (Crystal Mountain's species and krummholz). Each shot renders when its species are built.
#   trees-compare-<id>.png   a new tree's three variants beside the look-alike it replaces, scaled to the
#                            same height as the game would scale it; no snow, to judge colour
#   trees-new.png            the new conifers, three variants each, snow-loaded, with the skier
#   trees-grove-crystal*.png Crystal Mountain's mix by biomass, winter and snow-free
#   trees-closeup-new.png    the new conifers' lower crowns and trunks from eye level
#   trees-sprays-<id>.png    under a lower branch, looking up and out (the sprays and their undersides)
#   trees-lods-new.png       LOD0 / LOD1 / LOD2 with triangle counts
#   trees-krummholz-*.png    the three forms from the side (downwind to the right) and from above in the
#                            prefab's own frame, and a treeline scene in winter and without snow
#   trees-textures-new.png   spray, cluster, bark and bark normal textures of each new model

LOOKALIKE = {"pacific_silver_fir": "subalpine_fir", "noble_fir": "subalpine_fir", "western_hemlock": "mountain_hemlock"}
CRYSTAL = (("pacific_silver_fir", 30), ("mountain_hemlock", 19), ("douglas_fir", 12), ("western_hemlock", 11),
           ("subalpine_fir", 7), ("noble_fir", 6), ("engelmann_spruce", 3))
GROUND = srgb("#6B6455")


def top_of(obj):
    return max(v.co.z for v in obj.data.vertices)


def arrow(start, direction, length, text, flat=False, size=0.9, colour="#E8702A"):
    """A wind arrow from start along a horizontal direction, lying on the snow (flat) or standing upright
    facing the camera, with a caption."""
    d = Vector(direction).normalized()
    side = Vector((-d.y, d.x, 0)) if flat else Vector((0, 0, 1))
    w, head = 0.18 * size, 0.6 * size
    s0 = Vector(start)
    tip = s0 + d * length
    neck = tip - d * head
    verts = [s0 - side * w, neck - side * w, neck - side * w * 3, tip, neck + side * w * 3, neck + side * w, s0 + side * w]
    mesh = bpy.data.meshes.new("Arrow")
    mesh.from_pydata([tuple(v) for v in verts], [], [(0, 1, 5, 6), (2, 3, 4)])
    mesh.materials.append(flat_material("Arrow_" + colour, srgb(colour), emission=True))
    o = bpy.data.objects.new("Arrow", mesh)
    bpy.context.scene.collection.objects.link(o)
    if text:
        mid = s0 + d * length * 0.5
        if flat:
            label(text, mid.x, mid.y - size * 1.4, mid.z + 0.02, size=size * 0.9, colour=colour, flat=True)
        else:
            label(text, mid.x, mid.y, mid.z + size * 0.6, size=size, colour=colour)


def slope(x0, x1, y0, y1, grade, top=SNOW):
    """A snow slope rising toward +Y at grade (rise per metre), with a strata front."""
    mesh = bpy.data.meshes.new("Slope")
    z0, z1 = y0 * grade, y1 * grade
    v = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z1), (x0, y1, z1), (x0, y0, z0 - 3), (x1, y0, z0 - 3), (x1, y1, z0 - 3), (x0, y1, z0 - 3)]
    mesh.from_pydata(v, [], [(0, 1, 2, 3), (4, 5, 1, 0), (5, 6, 2, 1), (6, 7, 3, 2), (7, 4, 0, 3), (7, 6, 5, 4)])
    mesh.materials.append(flat_material("Top_%s" % str(top[:3]), top))
    mesh.materials.append(flat_material("Strata", srgb("#6E6A66")))
    mesh.polygons.foreach_set("material_index", [0, 1, 1, 1, 1, 1])
    o = bpy.data.objects.new("Slope", mesh)
    bpy.context.scene.collection.objects.link(o)


def texture_sheet(ids, path, size=256, cols=("spray", "cluster", "bark", "bark_normal")):
    """A contact sheet of each model's textures (rows: models; columns: spray, cluster, bark, bark normal by
    default) over a mid grey, so the alpha cut-outs show. A model without a column's texture leaves it grey."""
    import numpy as np
    pad = 12
    sheet = np.full((len(ids) * (size + pad) + pad, len(cols) * (size + pad) + pad, 4), 0.42, np.float32)
    sheet[..., 3] = 1
    for r, sid in enumerate(ids):
        for c, kind in enumerate(cols):
            img = bpy.data.images.get(f"{sid}_{kind}")
            if img is None:
                continue
            n = img.size[0]
            px = np.array(img.pixels[:], np.float32).reshape(n, n, 4)[::-1]   # row 0 = top
            step = max(1, n // size)
            px = px[::step, ::step][:size, :size]
            y, x = pad + r * (size + pad), pad + c * (size + pad)
            region = sheet[y:y + px.shape[0], x:x + px.shape[1]]
            a = px[..., 3:4]
            region[..., :3] = px[..., :3] * a + region[..., :3] * (1 - a)
    out = bpy.data.images.new("Sheet", sheet.shape[1], sheet.shape[0], alpha=True)
    out.pixels.foreach_set(np.flipud(sheet).ravel())
    out.filepath_raw = path
    out.file_format = "PNG"
    out.save()
    print("rendered", path, flush=True)


def render_task09(species, built, seasons, out_dir, want, originals):
    by_id = {sp["id"]: sp for sp in species}
    new = [sid for sid in ("pacific_silver_fir", "western_hemlock", "noble_fir") if sid in built]

    def lod0(sid, v=0):
        return built[sid][v]["objects"][0]

    if want("compare"):
        for sid in new:
            look = LOOKALIKE[sid]
            if look not in built:
                continue
            target = top_of(lod0(sid))
            k = target / top_of(lod0(look))
            entries = [(lod0(look), f"today: {by_id[look]['name'].lower()}", k)]
            entries += [(lod0(sid, v), by_id[sid]["name"] if v == 1 else "") for v in range(3)]
            lineup_shot(entries, os.path.join(out_dir, f"trees-compare-{sid}.png"), "summer", species, seasons, originals,
                        top=GROUND, label_size=1.3)

    if new and want("new"):
        entries = [(lod0(sid, v), by_id[sid]["name"] if v == 1 else "") for sid in new for v in range(3)]
        lineup_shot(entries, os.path.join(out_dir, "trees-new.png"), "winter", species, seasons, originals, label_size=1.3)

    mix = [(sid, w) for sid, w in CRYSTAL if sid in built]
    if len(mix) == len(CRYSTAL) and want("crystal"):
        cam = ((0, 0, 8), 125, 27, -25)
        grove_shot(mix, os.path.join(out_dir, "trees-grove-crystal.png"), species, seasons, originals, built, camera=cam)
        grove_shot(mix, os.path.join(out_dir, "trees-grove-crystal-snowfree.png"), species, seasons, originals, built,
                   season="summer", camera=cam)

    if new and want("closeup"):
        set_season(species, seasons, "winter")
        for i, sid in enumerate(new):
            place(lod0(sid), (i * 9.0 - 9.0, 0, 0), rot=0.4 + i)
        skier(4.5, -4)
        slab(-18, 18, -14, 14, top=SNOW)
        sun(elevation=25, azimuth=-60)
        setup_render(2400, 1350, samples=96)
        persp_camera((0, 0, 5.5), 26, 8, -15, lens=35)
        render(os.path.join(out_dir, "trees-closeup-new.png"))
        clear_scene(originals)

        # The lower crown from below, lit low from behind the camera: the undersides of the sprays.
        set_season(species, seasons, "summer")
        sun(elevation=8, azimuth=0, strength=4.0)
        setup_render(1200, 1200, samples=64)
        for i, sid in enumerate(new):
            tree = place(lod0(sid), (0, 0, 0), rot=0.3)
            base = built[sid][0]["height"] * by_id[sid]["crownBase"]
            persp_camera((0, 0, base + 4.0), 8.0, -18, 0, lens=28)
            render(os.path.join(out_dir, f"trees-sprays-{sid}.png"))
            bpy.data.objects.remove(tree)
        clear_scene(originals)

    if new and want("lods"):
        entries = []
        for sid in new + (["krummholz"] if "krummholz" in built else []):
            v = 1 if sid == "krummholz" else 0
            m = built[sid][v]
            entries += [(o, f"LOD{li} · {m['triangles'][li]:,}", 4.0 if sid == "krummholz" else 1.0) for li, o in enumerate(m["objects"])]
        lineup_shot(entries, os.path.join(out_dir, "trees-lods-new.png"), "winter", species, seasons, originals, label_size=1.1,
                    depth=4.5)   # the krummholz, shown 4x, is buried 4 m

    if "krummholz" in built and want("krummholz"):
        forms = built["krummholz"]
        # From the side: downwind (Blender -Y, Unity +Z) turned to point right (+X).
        set_season(species, seasons, "winter")
        x = 0.0
        for m in forms:
            place(m["objects"][0], (x, 0, 0), rot=math.pi / 2)
            label(f"{m['shape']} · {m['top']:.1f} m", x + 1.0, -4, -0.6, size=0.45)
            x += 7.0
        skier(x - 2.5, -1.5)
        arrow((-2.0, -3.0, 3.6), (1, 0, 0), 4.0, "wind (downwind)", size=0.45)
        slab(-4, x, -5, 5, depth=1.0)
        sun(elevation=30, azimuth=-35)
        setup_render(2600, 900, samples=96)
        ortho_camera(x / 2 - 2.5, 1.6, x + 4, tilt=4)
        render(os.path.join(out_dir, "trees-krummholz-side.png"))
        clear_scene(originals)

        # From above, in the prefab's own frame (no rotation): the foliage reaches toward Blender -Y.
        x = 0.0
        for m in forms:
            place(m["objects"][0], (x, 0, 0))
            label(m["shape"], x, 2.2, 0.05, size=0.5, flat=True)
            x += 5.5
        arrow((x - 1.5, 1.5, 0.05), (0, -1, 0), 3.5, "", flat=True, size=0.5)
        label("downwind: Blender -Y = prefab +Z", x / 2 - 1.5, -4.3, 0.05, size=0.45, flat=True, colour="#E8702A")
        slab(-4, x + 0.5, -5, 3.2, depth=1.0)
        sun(elevation=45, azimuth=-35)
        setup_render(2400, 1000, samples=64)
        cam = bpy.data.cameras.new("Cam")
        cam.type = "ORTHO"
        cam.ortho_scale = x + 5
        o = bpy.data.objects.new("Cam", cam)
        o.location = (x / 2 - 1.5, -0.9, 50)
        bpy.context.scene.collection.objects.link(o)
        bpy.context.scene.camera = o
        render(os.path.join(out_dir, "trees-krummholz-top.png"))
        clear_scene(originals)

        # A treeline: stunted firs and spruces below, krummholz above, all flagged the same way.
        grade = math.tan(math.radians(18))
        for season, name in (("winter", "trees-krummholz-treeline.png"), ("summer", "trees-krummholz-treeline-snowfree.png")):
            set_season(species, seasons, season)
            rng = random.Random(11)
            spots = []
            while len(spots) < 70:
                p = Vector((rng.uniform(-24, 24), rng.uniform(-18, 26), 0))
                if all((p - q).length > 2.6 for q in spots):
                    spots.append(p)
            for p in spots:
                p.z = p.y * grade
                band = (p.y + 18) / 44 + rng.uniform(-0.12, 0.12)   # 0 at the bottom, 1 at the top
                if band < 0.35:
                    sid = rng.choice([s for s in ("subalpine_fir", "engelmann_spruce") if s in built] or ["krummholz"])
                    m = rng.choice(built[sid])
                    k = (0.45 + 0.5 * (0.35 - band)) if sid != "krummholz" else 1.0
                    place(m["objects"][0], p, rot=rng.uniform(0, 6.28), scale=k)
                else:
                    # Flag trees lower, cushions between, mats highest.
                    v = 1 if band < 0.55 else (2 if band < 0.75 else 0)
                    place(forms[v]["objects"][0], p, rot=math.pi / 2 + rng.uniform(-0.15, 0.15), scale=rng.uniform(0.85, 1.15))
            arrow((-14, 6, 6 * grade + 0.1), (1, 0, 0), 7.0, "wind", flat=True, size=1.2)
            skier(2, -12, -12 * grade)
            slope(-30, 30, -22, 30, grade, top=SNOW if season == "winter" else srgb("#6F6A52"))
            sun(elevation=28, azimuth=-50)
            setup_render(2400, 1350, samples=96)
            persp_camera((0, 2, 3), 62, 24, -18, lens=40)
            render(os.path.join(out_dir, name))
            clear_scene(originals)

    if new and want("textures"):
        texture_sheet(new + (["krummholz"] if "krummholz" in built else []), os.path.join(out_dir, "trees-textures-new.png"))


# Task 09 phase 2: the New England models (docs/plans/new-england-species-plan.md, NE5).
PHASE2 = ("eastern_white_pine", "northern_red_oak", "black_cherry")
TODAY = {"eastern_white_pine": "lodgepole_pine", "northern_red_oak": "sugar_maple", "black_cherry": "quaking_aspen"}
# A lower-slope New England stand (southern NH: Gunstock, King Pine), with the shared models standing in for
# eastern hemlock (western hemlock) and white ash (sugar maple), by share of biomass.
NEW_ENGLAND = (("northern_red_oak", 20), ("eastern_white_pine", 15), ("red_maple", 15), ("western_hemlock", 10),
               ("sugar_maple", 10), ("yellow_birch", 8), ("american_beech", 7), ("black_cherry", 6), ("paper_birch", 4))


def render_phase2(species, built, seasons, out_dir, want, originals):
    by_id = {sp["id"]: sp for sp in species}
    new = [sid for sid in PHASE2 if sid in built]
    if not new:
        return

    def lod0(sid, v=0):
        return built[sid][v]["objects"][0]

    if want("ne-compare"):
        # Today's stand-in, scaled to the new tree's height as the game scales it, then the three variants.
        for sid in new:
            look = TODAY[sid]
            if look not in built:
                continue
            k = top_of(lod0(sid)) / top_of(lod0(look))
            entries = [(lod0(look), f"today: {by_id[look]['name'].lower()}", k)]
            entries += [(lod0(sid, v), by_id[sid]["name"] if v == 1 else "") for v in range(3)]
            lineup_shot(entries, os.path.join(out_dir, f"trees-ne-compare-{sid}.png"), "winter", species, seasons, originals,
                        label_size=1.3)

    if want("ne-new"):
        entries = [(lod0(sid, v), by_id[sid]["name"] if v == 1 else "") for sid in new for v in range(3)]
        lineup_shot(entries, os.path.join(out_dir, "trees-ne-new.png"), "winter", species, seasons, originals, label_size=1.3)

    broad = [sid for sid in new if by_id[sid]["form"] == "deciduous"]
    if broad and want("ne-seasons"):
        grounds = {"summer": srgb("#5E6B3A"), "autumn": srgb("#6B5A45")}
        for season, top in grounds.items():
            entries = [(lod0(sid, v), f"{by_id[sid]['name']}, {season}" if v == 1 else "") for sid in broad for v in range(3)]
            lineup_shot(entries, os.path.join(out_dir, f"trees-ne-{season}.png"), season, species, seasons, originals,
                        top=top, label_size=1.1)

    if want("ne-closeup"):
        # Each new tree's trunk and lower crown at eye level, a few metres away: bark and foliage.
        for sid in new:
            set_season(species, seasons, "winter")
            place(lod0(sid), (0, 0, 0), rot=0.6)
            skier(2.2, -2.5)
            slab(-12, 12, -10, 10, top=SNOW)
            sun(elevation=22, azimuth=-55)
            setup_render(1600, 1200, samples=96)
            persp_camera((0, 0, 5.0), 14.0, 8, -20, lens=35)
            render(os.path.join(out_dir, f"trees-ne-closeup-{sid}.png"))
            clear_scene(originals)

    mix = [(sid, w) for sid, w in NEW_ENGLAND if sid in built]
    if len(mix) == len(NEW_ENGLAND) and want("ne-grove"):
        cam = ((0, 0, 8), 125, 27, -25)
        grove_shot(mix, os.path.join(out_dir, "trees-grove-ne-lower.png"), species, seasons, originals, built, camera=cam)
        grove_shot(mix, os.path.join(out_dir, "trees-grove-ne-lower-autumn.png"), species, seasons, originals, built,
                   season="autumn", camera=cam)

    if want("ne-lods"):
        entries = []
        for sid in new:
            m = built[sid][0]
            entries += [(o, f"LOD{li} · {m['triangles'][li]:,}") for li, o in enumerate(m["objects"])]
        lineup_shot(entries, os.path.join(out_dir, "trees-ne-lods.png"), "winter", species, seasons, originals, label_size=1.1)

    if want("ne-textures"):
        conifers = [sid for sid in new if by_id[sid]["form"] == "conifer"]
        if conifers:
            texture_sheet(conifers, os.path.join(out_dir, "trees-ne-textures-conifers.png"))
        if broad:
            texture_sheet(broad, os.path.join(out_dir, "trees-ne-textures-broadleaves.png"),
                          cols=("leaves_summer", "leaves_autumn", "leaves_kept", "twigs", "bark", "bark_normal"))
