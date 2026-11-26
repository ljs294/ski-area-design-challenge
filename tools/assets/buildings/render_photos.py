"""Gate 2 review pictures for the buildings (loaded by build_buildings.py --photos DIR): Cycles, as the game draws them
(back faces hidden, the real palette with its metal and gloss), about ten stills per building.

  <id>_photo_<shot>.png   day views on a gravel pad by a pond (a booster: on a bench cut into a hill; a lift hut: on
                          level ground at its review height), close-ups of the doors and a door light, a dusk view
                          with every light record placed as a lamp and the glow masks lit, and one in snow (a hut:
                          dusk in snow too, the lift open)
A hut's `review.livery` paints its fascia and wainscot in a lift's colour; a shot's optional eighth entry repaints it.
The lamps come from the build's own light records (position, aim, cone, lumens, colour temperature), so the dusk
picture shows what the game will place. `--preview 1` renders the same shots in Workbench, to check the framing
without Cycles. The lift review's photo helpers (sky, sun, snow plane, figure, camera) are reused.
"""
import math
import os

import bpy
from mathutils import Vector

from liftkit import frame, materials, palette
from render_review import _setup as workbench_setup, lrr

# shot: (target in the building frame, azimuth from +u toward +v, elevation, distance, lens); see _shots
LAMP_GLOW = 60.0     # a lit lens's emission strength (the game's lamp mask, 3)
WINDOW_GLOW = 4.0    # lit window glass (mask 4), the pumps running
KELVIN_RGB = {3000: (1.0, 0.71, 0.42), 4000: (1.0, 0.82, 0.64)}   # Blender's blackbody, in case a light has no temperature


def _gpu(sc):
    """Cycles on the GPU where there is one (the render lane holds it), else the CPU."""
    try:
        prefs = bpy.context.preferences.addons["cycles"].preferences
    except KeyError:
        return "CPU"
    for kind in ("OPTIX", "CUDA", "HIP", "ONEAPI", "METAL"):
        try:
            prefs.compute_device_type = kind
        except TypeError:
            continue
        prefs.get_devices()
        devs = [d for d in prefs.devices if d.type == kind]
        if devs:
            for d in prefs.devices:
                d.use = d.type == kind
            sc.cycles.device = "GPU"
            return kind
    return "CPU"


def _glow_nodes(mats):
    """Emission on the glow masks (Data UV y): 3 a lamp's lens (4,000 K), 4 window glass (3,000 K). Strengths are the
    GlowLamp and GlowWindow values; 0 by day."""
    for m in mats:
        nt = m.node_tree
        if nt.nodes.get("GlowLamp"):
            continue
        N, L = nt.nodes.new, nt.links.new
        bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
        uv = next(n for n in nt.nodes if n.type == "UVMAP" and n.uv_map == "Data")
        sep = next(lk.to_node for lk in uv.outputs["UV"].links if lk.to_node.type == "SEPXYZ")
        y = sep.outputs["Y"]

        def M(op, a, b):
            n = N("ShaderNodeMath")
            n.operation = op
            for i, x in enumerate((a, b)):
                if isinstance(x, (int, float)):
                    n.inputs[i].default_value = x
                else:
                    L(x, n.inputs[i])
            return n.outputs[0]

        lamp = M("MULTIPLY", M("GREATER_THAN", y, 2.5), M("LESS_THAN", y, 3.5))
        win = M("GREATER_THAN", y, 3.5)
        ls, ws = N("ShaderNodeValue"), N("ShaderNodeValue")
        ls.name, ws.name = "GlowLamp", "GlowWindow"
        ls.outputs[0].default_value = ws.outputs[0].default_value = 0.0
        L(M("ADD", M("MULTIPLY", lamp, ls.outputs[0]), M("MULTIPLY", win, ws.outputs[0])), bsdf.inputs["Emission Strength"])
        bl, bw = N("ShaderNodeBlackbody"), N("ShaderNodeBlackbody")
        bl.inputs["Temperature"].default_value = 4000.0
        bw.inputs["Temperature"].default_value = 3000.0
        col = N("ShaderNodeMix")
        col.data_type = "RGBA"
        L(win, col.inputs["Factor"])
        L(bl.outputs[0], materials._sock(col.inputs, "A"))
        L(bw.outputs[0], materials._sock(col.inputs, "B"))
        L(materials._sock(col.outputs, "Result"), bsdf.inputs["Emission Color"])


def _set_glow(mats, lamp, window):
    for m in mats:
        for name, v in (("GlowLamp", lamp), ("GlowWindow", window)):
            n = m.node_tree.nodes.get(name)
            if n:
                n.outputs[0].default_value = v


def _lamps(a0):
    """One Blender lamp per light record: a spot's power is the point light that gives its lumens inside its cone."""
    out = []
    for lt in a0.lights:
        spot = lt["kind"] == "spot"
        d = bpy.data.lights.new("Photo_" + lt["name"], "SPOT" if spot else "POINT")
        if spot:
            cone = math.radians(lt["coneDeg"])
            d.spot_size = cone
            d.spot_blend = 0.5
            omega = 2 * math.pi * (1 - math.cos(cone / 2))
        else:
            omega = 4 * math.pi
        d.energy = 4 * math.pi * lt["lumens"] / 683.0 / omega
        d.shadow_soft_size = 0.08
        if hasattr(d, "use_temperature"):
            d.use_temperature = True
            d.temperature = lt["kelvin"]
        else:
            d.color = KELVIN_RGB.get(lt["kelvin"], (1.0, 1.0, 1.0))
        o = bpy.data.objects.new(d.name, d)
        bpy.context.scene.collection.objects.link(o)
        aim = Vector(frame.b(tuple(lt["aim"]))).normalized()
        o.location = Vector(frame.b(tuple(lt["pos"]))) + aim * 0.02   # just below the lens, clear of its fixture
        o.rotation_euler = aim.to_track_quat("-Z", "Y").to_euler()
        out.append(o)
    return out


def _plane(name, corners, mat):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([frame.b(p) for p in corners], [], [tuple(range(len(corners)))])
    if mesh.polygons[0].normal.z < 0:
        mesh.flip_normals()
    mesh.materials.append(mat)
    o = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(o)
    return o


def _site(bank_v):
    """Summer ground: meadow, a gravel pad round the house, and the pond past a short bank on the water side (-v)."""
    grass = lrr._photo_material("PhotoGrassMat", (0.16, 0.21, 0.10), 0.95)
    gravel = lrr._photo_material("PhotoGravelMat", (0.33, 0.31, 0.28), 0.95)
    water = lrr._photo_material("PhotoWaterMat", (0.02, 0.05, 0.07), 0.04)
    far, b, low = 300.0, bank_v, -0.7
    return [
        _plane("PhotoGrass", [(-far, b, -0.01), (far, b, -0.01), (far, far, -0.01), (-far, far, -0.01)], grass),
        _plane("PhotoGravel", [(-16.0, b, -0.004), (16.0, b, -0.004), (16.0, 14.0, -0.004), (-16.0, 14.0, -0.004)], gravel),
        _plane("PhotoBank", [(-far, b - 2.0, low), (far, b - 2.0, low), (far, b, -0.01), (-far, b, -0.01)], grass),
        _plane("PhotoPond", [(-far, -far, low + 0.25), (far, -far, low + 0.25), (far, b - 1.2, low + 0.25),
                             (-far, b - 1.2, low + 0.25)], water),
    ]


def _bench_site(st):
    """A booster's ground: a hill whose fall line runs along u (the main climbs from the inlet gable, -u, to the
    discharge, +u), with a level bench cut into it. Uphill a cut face, downhill a fill slope, then the natural grade;
    gravel round the house. Returns the summer ground and a snow copy of the same profile."""
    grass = lrr._photo_material("PhotoGrassMat", (0.16, 0.21, 0.10), 0.95)
    gravel = lrr._photo_material("PhotoGravelMat", (0.33, 0.31, 0.28), 0.95)
    snow = lrr._photo_material("PhotoSnowMat", (0.88, 0.91, 0.95), 0.55)
    far, g, h = 300.0, st["grade"], st["half"]
    up = h / (1.0 - g * st["cut"])        # where the cut face (1 up : cut across) meets the hill above
    down = h / (1.0 - g * st["fill"])     # where the fill slope (1 down : fill across) meets the hill below
    prof = [(-far, -g * far), (-down, -g * down), (-h, 0.0), (h, 0.0), (up, g * up), (far, g * far)]

    def strips(tag, mat, lift):
        return [_plane(f"Photo{tag}{i}", [(u0, -far, z0 + lift), (u1, -far, z1 + lift), (u1, far, z1 + lift),
                                          (u0, far, z0 + lift)], mat)
                for i, ((u0, z0), (u1, z1)) in enumerate(zip(prof, prof[1:]))]
    u0, u1, v0, v1 = st["gravel"]
    ground = strips("Hill", grass, -0.01) + [
        _plane("PhotoGravel", [(u0, v0, -0.004), (u1, v0, -0.004), (u1, v1, -0.004), (u0, v1, -0.004)], gravel)]
    return ground, strips("SnowHill", snow, 0.0)


def _pad_site(pd):
    """A lift hut's ground: meadow and a gravel pad round the hut, level at the review's height (`ground`, below the
    load or unload level where the ground falls away); in winter, snow at the same height."""
    grass = lrr._photo_material("PhotoGrassMat", (0.16, 0.21, 0.10), 0.95)
    gravel = lrr._photo_material("PhotoGravelMat", (0.33, 0.31, 0.28), 0.95)
    far, g = 300.0, pd["ground"]
    u0, u1, v0, v1 = pd["gravel"]
    ground = [_plane("PhotoGrass", [(-far, -far, g - 0.01), (far, -far, g - 0.01), (far, far, g - 0.01), (-far, far, g - 0.01)], grass),
              _plane("PhotoGravel", [(u0, v0, g - 0.004), (u1, v0, g - 0.004), (u1, v1, g - 0.004), (u0, v1, g - 0.004)], gravel)]
    return ground, [lrr._snow_plane(g)]


def _target(a0, t):
    """A shot's target: a building-frame point, or {"mean": [socket or light names], "plus": offset}."""
    if isinstance(t, dict):
        pts = [Vector(a0.sockets[n]) for n in t["mean"]]
        return tuple(sum(pts, Vector()) / len(pts) + Vector(t.get("plus", (0.0, 0.0, 0.0))))
    return tuple(t)


def _shots(a0, spec):
    """The spec's review.photos: (name, target, azimuth, elevation, distance, lens, mode[, livery]); azimuth 0 looks
    from +u (the east gable), 90 from the yard (+v; a hut's front), 270 from the back (-v). Mode: day, dusk, snow or
    dusk_snow. Livery: an sRGB hex for this shot (else the review's `livery`, else none)."""
    base = spec["review"].get("livery")
    return [(p[0], _target(a0, p[1]), *p[2:7], p[7] if len(p) > 7 else base) for p in spec["review"]["photos"]]


def _mode(sc, sun, mode, mats, ground, snow):
    world = sc.world.node_tree.nodes
    bg = next(n for n in world if n.type == "BACKGROUND")
    dusk, snowy = mode.startswith("dusk"), mode.endswith("snow")
    to_sun = {"day": (0.55, 0.62, 0.58), "dusk": (-0.75, 0.35, 0.06), "snow": (0.45, 0.70, 0.45)}["dusk" if dusk else mode]
    sun.rotation_euler = (-Vector(frame.b(to_sun)).normalized()).to_track_quat("-Z", "Y").to_euler()
    if dusk:
        bg.inputs["Color"].default_value = (0.10, 0.14, 0.32, 1.0)
        bg.inputs["Strength"].default_value = 0.05
        sun.data.energy, sun.data.color = 0.25, (1.0, 0.55, 0.32)
        sc.view_settings.exposure = 1.6
        _set_glow(mats, LAMP_GLOW, WINDOW_GLOW)
    else:
        bg.inputs["Color"].default_value = (0.36, 0.56, 0.95, 1.0) if mode == "day" else (0.62, 0.70, 0.86, 1.0)
        bg.inputs["Strength"].default_value = 1.0
        sun.data.energy, sun.data.color = (4.5, (1.0, 0.96, 0.9)) if mode == "day" else (3.0, (0.95, 0.97, 1.0))
        sc.view_settings.exposure = 0.0 if mode == "day" else 0.1
        _set_glow(mats, 0.0, 0.0)
    for o in ground:
        o.hide_render = snowy
    for o in snow:
        o.hide_render = not snowy
    materials.set_snow(mats, 1.0 if snowy else 0.0)


def render_all(built_all, out_dir, mats, preview=False):
    os.makedirs(out_dir, exist_ok=True)
    pal = bpy.data.images["lift_palette"]   # Gate 1's Workbench pass swapped in an opaque copy: Cycles needs the real one
    materials.use_palette(mats, pal)
    _glow_nodes(mats)
    materials.cull_backfaces(mats)
    for key, built in built_all.items():
        a0 = built["assets"][0]
        sc, sun = lrr._photo_scene()
        print(f"{a0.id}: Cycles on {_gpu(sc)}", flush=True)
        objs = lrr.lod_objects(built, 0)
        lrr.restore_uv(objs)
        rv = built["spec"]["review"]
        if "bench" in rv:   # a booster: a bench cut into a hill
            ground, snow = _bench_site(rv["bench"])
        elif "pad" in rv:   # a lift hut: level ground at its review height
            ground, snow = _pad_site(rv["pad"])
        else:               # a pump house: the pond's bank that far past the back wall
            ground = _site(-built["spec"]["footprint"]["width"] / 2000 - rv["bankOut"])
            snow = [lrr._snow_plane(0.0)]
        fig = lrr._photo_figure(tuple(rv["photoFigure"]))
        lamps = _lamps(a0)
        extras = ground + snow + [sun, fig] + lamps
        lrr.show_only(objs + extras)
        if preview:   # the framing only: Workbench, the opaque palette, no lamps
            wsc = workbench_setup()
            wsc.render.resolution_x, wsc.render.resolution_y = 800, 533
            materials.use_palette(mats, materials.opaque_copy(pal))
            lrr.use_palette_uv(objs + extras)
        shoot = lrr._shooter(a0.id, out_dir)
        for name, target, az, el, dist, lens, mode, livery in _shots(a0, built["spec"]):
            _mode(sc, sun, mode, mats, ground, snow)
            materials.set_livery(mats, palette.srgb(livery) if livery else (1.0, 1.0, 1.0))
            if preview:
                materials.use_palette(mats, materials.opaque_copy(pal, livery=livery))
            fig.hide_render = name not in rv["figureIn"]
            shoot(name, target, az, el, dist, lens=lens)
        materials.set_snow(mats, 0.0)
        materials.set_livery(mats, (1.0, 1.0, 1.0))
        _set_glow(mats, 0.0, 0.0)
        lrr._photo_end(sc, extras)
        if preview:
            lrr.restore_uv(objs + extras)
            materials.use_palette(mats, pal)
