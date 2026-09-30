"""Review renders for the lift assets (loaded by build_lifts.py --render DIR [--shots ...]).

Shots:
  gate1   orthographic side / end / plan elevations of each terminal (Workbench, transparent background) with a
          <name>_meta.json beside each (mm per pixel, pixel of the model origin, image axes), plus clay 3/4 views
          with chairs hung on the rope and a 1.8 m figure for scale.
  lods    every LOD of every asset side by side, with triangle counts.
  photos  Cycles photos: the chair (angles, close-ups, a line of chairs, snowfall) and each terminal on snow
          with its rope loop and chairs (hero, back, side, approach, high, bullwheel, entry, snowfall, livery);
          terminal_photos renders only the terminals' sets; LIFT_PHOTOS=hero,back limits either to those shots.
Blender's metadata stamp is off so the PNGs carry no paths.
"""
import json
import math
import os

import bpy
from mathutils import Matrix, Vector

from liftkit import frame, materials

MM_PER_PX = 8.0
FIGURE_H = 1.8

# (asset key, view name, image right, image up) in the lift frame; the directions match the source elevations.
ORTHO = {
    "drive": [("side", "-u", "+w"), ("end", "+v", "+w"), ("plan", "-u", "+v")],
    "return": [("side", "+u", "+w"), ("end", "+v", "+w"), ("plan", "+u", "-v")],
    "chair": [("front", "-v", "+w"), ("side", "+u", "+w")],
    "chairworks_chair_detach": [("front", "-v", "+w"), ("side", "+u", "+w")],
    "chairworks_chair_fixed": [("front", "-v", "+w"), ("side", "+u", "+w")],
    "sle_stick_gun_10": [("side", "+u", "+w"), ("end", "+v", "+w"), ("plan", "+u", "-v")],
    "sle_stick_gun_20": [("side", "+u", "+w"), ("end", "+v", "+w"), ("plan", "+u", "-v")],
    "sle_stick_gun_30": [("side", "+u", "+w"), ("end", "+v", "+w"), ("plan", "+u", "-v")],
    "sle_ground_gun": [("side", "+u", "+w"), ("end", "+v", "+w"), ("plan", "+u", "-v")],
    "monta_drive": [("side", "+u", "+w"), ("end", "+v", "+w"), ("plan", "+u", "-v")],
    "monta_return": [("side", "+u", "+w"), ("end", "+v", "+w"), ("plan", "+u", "-v")],
}
TERMINALS = ("drive", "return", "monta_drive", "monta_return")
CHAIRS = ("chair", "chairworks_chair_detach", "chairworks_chair_fixed")


def chair_rope(built):
    """A chair's rope height above the snow (m)."""
    return built["assets"][0].dims.get("ropeW", 3039) / 1000
AXES = {"u": Vector((1, 0, 0)), "v": Vector((0, 1, 0)), "w": Vector((0, 0, 1))}


def axis(spec):
    return AXES[spec[1]] * (1 if spec[0] == "+" else -1)


def scene_setup(engine="BLENDER_WORKBENCH"):
    sc = bpy.context.scene
    sc.render.engine = engine
    sc.render.use_stamp = False
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.render.resolution_percentage = 100
    if engine == "BLENDER_WORKBENCH":
        sh = sc.display.shading
        sh.light = "STUDIO"
        sh.color_type = "TEXTURE"
        sh.show_object_outline = True
        sh.object_outline_color = (0.05, 0.05, 0.05)
        sh.show_cavity = True
        sh.cavity_type = "WORLD"
        sh.show_shadows = False
    sc.view_settings.view_transform = "Standard"
    return sc


def use_palette_uv(objs):
    for o in objs:
        if o.type == "MESH" and "Palette" in o.data.uv_layers:
            o.data.uv_layers["Palette"].active = True
            o.data.uv_layers["Palette"].active_render = True


def restore_uv(objs):
    for o in objs:
        if o.type == "MESH" and "UVMap" in o.data.uv_layers:
            o.data.uv_layers["UVMap"].active = True
            o.data.uv_layers["UVMap"].active_render = True


def show_only(objs):
    keep = set(o.name for o in objs)
    for o in bpy.context.scene.objects:
        hide = o.name not in keep
        o.hide_render = hide
        o.hide_viewport = hide


def lod_objects(built, lod):
    return [o for o in built["objects"] if o.type == "MESH" and o.name.endswith(f"_LOD{lod}")]


def camera(name="ReviewCam"):
    cam = bpy.data.objects.get(name)
    if cam is None:
        cam = bpy.data.objects.new(name, bpy.data.cameras.new(name))
        bpy.context.scene.collection.objects.link(cam)
    bpy.context.scene.camera = cam
    return cam


def place_ortho(cam, right_l, up_l, centre_l, width_m, height_m):
    r, u = frame.b(right_l).normalized(), frame.b(up_l).normalized()
    back = r.cross(u)
    rot = Matrix((r, u, back)).transposed()
    cam.matrix_world = Matrix.Translation(frame.b(centre_l) + back * 60) @ rot.to_4x4()
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = max(width_m, height_m)
    cam.data.clip_start, cam.data.clip_end = 0.1, 200
    sc = bpy.context.scene
    sc.render.resolution_x = int(round(width_m * 1000 / MM_PER_PX))
    sc.render.resolution_y = int(round(height_m * 1000 / MM_PER_PX))


def bounds_lift(objs):
    pts = []
    for o in objs:
        mw = o.matrix_world
        pts += [frame.lift(mw @ Vector(c)) for c in o.bound_box]
    lo = Vector(tuple(min(p[i] for p in pts) for i in range(3)))
    hi = Vector(tuple(max(p[i] for p in pts) for i in range(3)))
    return lo, hi


def render(path):
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def drawing_chairs(built, spec, chairs):
    """Chairs where the source elevations draw them: one on each rope over the pier, one at the far end of
    the bullwheel."""
    c = spec["common"]
    rope, hg = c["ropeElevation"] / 1000, c["lineGauge"] / 2000
    u_bw = built["assets"][0].pivots["bullwheel"]["pos"][0]
    pts = [(0.0, hg, rope), (0.0, -hg, rope), (u_bw - hg, 0.0, rope)]
    return hang_chairs(chairs, [(p, _yaw_for(p, u_bw)) for p in pts])


def ortho_views(key, built, out_dir, extras=()):
    objs = lod_objects(built, 0)
    show_only(objs + list(extras))
    use_palette_uv(objs + list(extras))
    lo, hi = bounds_lift(objs + list(extras))
    lo -= Vector((0.5, 0.5, 0.5))
    hi += Vector((0.5, 0.5, 0.5))
    cam = camera()
    for view, rs, us in ORTHO[key]:
        r, u = axis(rs), axis(us)
        corners = [Vector((x, y, z)) for x in (lo.x, hi.x) for y in (lo.y, hi.y) for z in (lo.z, hi.z)]
        rr = [c.dot(r) for c in corners]
        uu = [c.dot(u) for c in corners]
        width, height = max(rr) - min(rr), max(uu) - min(uu)
        centre_r, centre_u = (max(rr) + min(rr)) / 2, (max(uu) + min(uu)) / 2
        # centre point in the lift frame: along r and u only
        centre = r * centre_r + u * centre_u
        # square pixels: ortho_scale covers the longer side, so pad the shorter to match the aspect
        place_ortho(cam, tuple(r), tuple(u), tuple(centre), width, height)
        name = f"{key}_{view}"
        render(os.path.join(out_dir, name + ".png"))
        w_px = bpy.context.scene.render.resolution_x
        h_px = bpy.context.scene.render.resolution_y
        meta = {"view": name, "mmPerPx": MM_PER_PX, "size": [w_px, h_px], "right": rs, "up": us,
                "originPx": [w_px / 2 - centre_r * 1000 / MM_PER_PX, h_px / 2 + centre_u * 1000 / MM_PER_PX]}
        with open(os.path.join(out_dir, name + "_meta.json"), "w") as f:
            json.dump(meta, f, indent=2)
        with open(os.path.join(out_dir, name + "_lines.json"), "w") as f:
            json.dump({"segments": edge_lines(objs + list(extras), r, u, centre, w_px, h_px)}, f)
    restore_uv(objs)


def edge_lines(objs, r_lift, u_lift, centre_lift, w_px, h_px, crease=0.6):
    """The model as line art for this orthographic view, like the source drawings: silhouette edges (one
    face turned toward the viewer, the other away), creases sharper than `crease` radians and open
    boundaries, projected to image pixels. Each segment is [x0, y0, x1, y1, visible]; hidden stretches (behind
    other geometry, found by casting rays toward the viewer along the edge) are kept so they can be drawn dashed."""
    import bmesh
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    back = frame.b(tuple(r_lift)).cross(frame.b(tuple(u_lift))).normalized()   # toward the viewer (Blender)
    k = 1000.0 / MM_PER_PX

    def project(p_blender):
        lp = Vector(frame.lift(p_blender)) - Vector(tuple(centre_lift))
        return (w_px / 2 + lp.dot(r_lift) * k, h_px / 2 - lp.dot(u_lift) * k)

    segs = []
    for o in objs:
        if o.type != "MESH":
            continue
        bm = bmesh.new()
        bm.from_mesh(o.data)
        bm.transform(o.matrix_world)
        bm.normal_update()
        for e in bm.edges:
            faces = [f for f in e.link_faces if f.normal.length > 0.5]
            keep = False
            if len(faces) == 1:
                keep = True
            elif len(faces) >= 2:
                n1, n2 = faces[0].normal, faces[1].normal
                if (n1.dot(back) > 0) != (n2.dot(back) > 0):
                    keep = True
                elif n1.angle(n2, 0.0) > crease:
                    keep = True
            if not keep:
                continue
            a, b = e.verts[0].co.copy(), e.verts[1].co.copy()
            # visibility along the edge (every ~5 cm), so a long edge running under a plate is split there
            n = max(1, min(40, int((b - a).length / 0.05)))
            vis = [not bpy.context.scene.ray_cast(depsgraph, a + (b - a) * ((i + 0.5) / n) + back * 0.004, back, distance=200.0)[0]
                   for i in range(n)]
            i = 0
            while i < n:
                j = i
                while j + 1 < n and vis[j + 1] == vis[i]:
                    j += 1
                (x0, y0), (x1, y1) = project(a + (b - a) * (i / n)), project(a + (b - a) * ((j + 1) / n))
                segs.append([round(x0, 2), round(y0, 2), round(x1, 2), round(y1, 2), 1 if vis[i] else 0])
                i = j + 1
        bm.free()
    return segs


def figure(at_lift):
    """A 1.8 m scale figure (capsule) standing at a lift-frame point."""
    name = "ScaleFigure"
    o = bpy.data.objects.get(name)
    if o is None:
        bpy.ops.mesh.primitive_cylinder_add(radius=0.22, depth=FIGURE_H - 0.3, vertices=16)
        body = bpy.context.active_object
        bpy.ops.mesh.primitive_uv_sphere_add(radius=0.14, segments=16, ring_count=8, location=(0, 0, (FIGURE_H - 0.3) / 2 + 0.16))
        head = bpy.context.active_object
        bpy.ops.object.select_all(action="DESELECT")
        body.select_set(True)
        head.select_set(True)
        bpy.context.view_layer.objects.active = body
        bpy.ops.object.join()
        o = bpy.context.active_object
        o.name = name
        m = bpy.data.materials.new("FigureMat")
        m.diffuse_color = (0.15, 0.35, 0.75, 1)
        o.data.materials.append(m)
    o.location = frame.b(at_lift) + Vector((0, 0, (FIGURE_H - 0.3) / 2))
    return o


def hang_chairs(chair_objs, positions):
    """Linked duplicates of the chair LOD0 at (lift point, heading angle) positions; returns the new objects."""
    made = []
    src = chair_objs[0]
    for i, (p, yaw) in enumerate(positions):
        o = src.copy()
        o.name = f"ReviewChair{i}"
        o.matrix_world = Matrix.Translation(frame.b(p)) @ Matrix.Rotation(yaw, 4, "Z")
        bpy.context.scene.collection.objects.link(o)
        made.append(o)
    return made


def ground(size=40.0, z=0.0):
    o = bpy.data.objects.get("ReviewGround")
    if o is None:
        bpy.ops.mesh.primitive_plane_add(size=size)
        o = bpy.context.active_object
        o.name = "ReviewGround"
        m = bpy.data.materials.new("GroundMat")
        m.diffuse_color = (0.93, 0.95, 0.98, 1)
        o.data.materials.append(m)
    o.location = (0, 0, z)
    return o


def clay_views(key, built, spec, out_dir, chairs):
    sc = scene_setup()
    sc.display.shading.show_shadows = True
    sc.display.shading.light = "STUDIO"
    sc.render.film_transparent = False
    sc.world = sc.world or bpy.data.worlds.new("World")
    objs = lod_objects(built, 0)
    extras = []
    c = spec.get("common", {})   # a snow gun's spec has no lift of its own
    rope = built["assets"][0].dims.get("rope", c.get("ropeElevation", 0)) / 1000
    hg = c.get("lineGauge", 0) / 2000
    if key in TERMINALS and chairs:
        u_bw = built["assets"][0].pivots["bullwheel"]["pos"][0]
        # chairs: travel +u on the right rope, -u on the left rope, and one at the far end of the wheel
        pos = [((u_bw + 3.0, hg, rope), 0.0), ((u_bw + 3.0, -hg, rope), math.pi), ((u_bw - hg, 0.0, rope), math.pi / 2)]
        # chair frame: +u travel, +v outboard. In Blender, lift +u = -y; yaw rotates about z.
        extras = hang_chairs(chairs, [(p, _yaw_for(p, u_bw)) for p, _ in pos])
        extras.append(figure((1.5 if key.endswith("drive") else 2.5, 3.2 if key.endswith("drive") else -2.6, 0.0)))
    elif key in CHAIRS:   # the snow under the chair: its rope's height below the grip
        extras.append(figure((0.0, 1.6, -chair_rope(built))))
    elif key.startswith("sle_stick"):   # beside the post, clear of the lance's lean (the ground gun's close views
        extras.append(figure((-1.0, -1.2, 0.0)))   # leave it out: it would stand in front of the camera)
    g = ground(60, 0.0 if key not in CHAIRS else -chair_rope(built))
    extras.append(g)
    show_only(objs + extras)
    use_palette_uv(objs + extras)
    lo, hi = bounds_lift(objs)
    centre = (lo + hi) / 2
    radius = (hi - lo).length / 2
    cam = camera("ClayCam")
    cam.data.type = "PERSP"
    cam.data.lens = 35
    cam.data.clip_end = 500
    sc.render.resolution_x, sc.render.resolution_y = 1600, 1000
    if key.startswith("sle_stick"):   # tall and thin: frame what stands above the snow, from further out
        lo = Vector((lo.x, lo.y, max(lo.z, 0.0)))
        centre, radius = (lo + hi) / 2, (hi - lo).length / 2
    dist = 3.0 if key.startswith("sle_stick") else 2.4
    for name, (az, el) in {"threequarter_line": (35, 18), "threequarter_back": (215, 22), "low": (120, 6)}.items():
        a, e = math.radians(az), math.radians(el)
        d = Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
        eye = centre + d * radius * dist
        cam.location = frame.b(tuple(eye))
        look = frame.b(tuple(centre)) - cam.location
        cam.rotation_euler = look.to_track_quat("-Z", "Y").to_euler()
        render(os.path.join(out_dir, f"{key}_clay_{name}.png"))
    for o in extras:
        if o.name.startswith("ReviewChair"):
            bpy.data.objects.remove(o)
    restore_uv(objs)


def sle_views(key, built, out_dir):
    """Snow guns up close in their palette colours: the stick gun's head and base and the whole gun; the ground gun
    from the angles of the owner's photos of a real unit (front three-quarter, the mount from the side, the tripod
    from behind, the hose block from behind and above)."""
    sc = scene_setup()
    sc.display.shading.show_shadows = True
    sc.render.film_transparent = False
    sc.world = sc.world or bpy.data.worlds.new("World")
    objs = lod_objects(built, 0)
    g = ground(40, 0.0)
    show_only(objs + [g])
    use_palette_uv(objs)
    cam = camera("GunCam")
    cam.data.type = "PERSP"
    cam.data.clip_start, cam.data.clip_end = 0.01, 200
    sc.render.resolution_x, sc.render.resolution_y = 1200, 900
    nozzle = Vector(built["assets"][0].sockets["nozzle"])
    if key == "sle_ground_gun":   # name: (azimuth from +u toward +v, elevation, distance, target, lens)
        # the valve paddle and the mount are on the gun's left (-v), as in the owner's photos
        views = {"side": (242, 22, 2.0, Vector((0.02, 0.03, 0.9)), 40),
                 "front": (320, 18, 2.2, Vector((0.05, 0.03, 0.85)), 40),
                 "mount": (270, 6, 1.0, Vector((-0.03, 0.02, 1.07)), 45),
                 "behind": (165, 36, 3.0, Vector((0.0, 0.0, 0.7)), 35),
                 "hose_block": (160, 42, 1.3, Vector((-0.12, 0.03, 1.08)), 45)}
    else:
        pivot = Vector(built["assets"][0].pivots["lance"]["pos"])   # on the base mast's top
        views = {"head": (60, 12, 1.4, nozzle - Vector((0.3, 0.0, 0.05)), 45),
                 "base": (305, 12, 2.9, Vector((0.05, 0.0, pivot.z * 0.6)), 40),   # the mast, from the valve side (-v)
                 "whole": (40, 12, nozzle.z * 1.6 + 2.0, Vector((nozzle.x * 0.45, 0.0, nozzle.z * 0.5)), 35)}
    for name, (az, el, dist, tgt, lens) in views.items():
        cam.data.lens = lens
        a, e = math.radians(az), math.radians(el)
        d = Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
        cam.location = frame.b(tuple(tgt + d * dist))
        cam.rotation_euler = (frame.b(tuple(tgt)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        render(os.path.join(out_dir, f"{key}_view_{name}.png"))
    restore_uv(objs)


def _yaw_for(p, u_bw):
    """Heading so the chair's +u (travel) follows the counter-clockwise loop and +v points outboard."""
    u, v, _ = p
    if abs(v) < 1e-6:   # at the far end of the wheel: travelling from the left rope to the right rope (+v)
        travel = Vector((0, 1, 0))
    else:
        travel = Vector((1, 0, 0)) if v > 0 else Vector((-1, 0, 0))
    tb = frame.b(tuple(travel))
    # chair +u maps to Blender -y; rotate -y onto tb about z
    return math.atan2(tb.y, tb.x) - math.atan2(-1.0, 0.0)


def lod_lineup(built_all, out_dir):
    sc = scene_setup()
    sc.render.film_transparent = False
    for key, built in built_all.items():
        n = len(built["assets"])
        objs = []
        lo, hi = bounds_lift(lod_objects(built, 0))
        span = (hi.y - lo.y) + 1.5 if key != "chair" else 3.0
        placed = []
        for lod in range(n):
            group = [o for o in built["objects"] if o.type == "MESH" and o.name.endswith(f"_LOD{lod}")]
            for o in group:
                placed.append((o, o.matrix_world.copy()))
                o.matrix_world = Matrix.Translation(frame.b((0, span * lod, 0))) @ o.matrix_world
            objs += group
        show_only(objs)
        use_palette_uv(objs)
        blo, bhi = bounds_lift(objs)
        centre = (blo + bhi) / 2
        cam = camera("LodCam")
        cam.data.type = "ORTHO"
        cam.data.ortho_scale = (bhi.y - blo.y) * 1.1
        sc.render.resolution_x, sc.render.resolution_y = 2000, 800
        a, e = math.radians(200), math.radians(20)
        d = Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
        cam.location = frame.b(tuple(centre + d * 80))
        cam.rotation_euler = (frame.b(tuple(centre)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        cam.data.clip_end = 300
        render(os.path.join(out_dir, f"{key}_lods.png"))
        for o, mw in placed:
            o.matrix_world = mw
        restore_uv(objs)


def data_views(key, built, out_dir):
    """The baked ambient occlusion (vertex colour R), flat-lit, from two 3/4 angles."""
    sc = scene_setup()
    sc.render.film_transparent = False
    sh = sc.display.shading
    sh.light = "FLAT"
    sh.color_type = "VERTEX"
    sh.show_cavity = False
    sh.show_shadows = False
    objs = lod_objects(built, 0)
    show_only(objs)
    for o in objs:
        if o.type == "MESH" and "AO" in o.data.color_attributes:
            o.data.color_attributes.active_color_name = "AO"
    lo, hi = bounds_lift(objs)
    centre = (lo + hi) / 2
    radius = (hi - lo).length / 2
    cam = camera("DataCam")
    cam.data.type = "PERSP"
    cam.data.lens = 35
    cam.data.clip_end = 500
    sc.render.resolution_x, sc.render.resolution_y = 1400, 900
    for name, (az, el) in {"line": (35, 20), "back": (215, 25)}.items():
        a, e = math.radians(az), math.radians(el)
        d = Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
        cam.location = frame.b(tuple(centre + d * radius * 2.2))
        cam.rotation_euler = (frame.b(tuple(centre)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        render(os.path.join(out_dir, f"{key}_ao_{name}.png"))


def _photo_material(name, rgb, rough, metallic=0.0):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (*rgb, 1.0)
    bsdf.inputs["Roughness"].default_value = rough
    bsdf.inputs["Metallic"].default_value = metallic
    return m


def _photo_scene():
    """Cycles with a clear-sky world and a low sun from the front left; returns the scene and the sun."""
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.samples = 128
    sc.cycles.use_denoising = True
    sc.render.use_stamp = False
    sc.render.film_transparent = False
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGB"
    sc.render.resolution_x, sc.render.resolution_y = 1600, 1066
    try:
        sc.view_settings.view_transform = "AgX"
    except TypeError:
        sc.view_settings.view_transform = "Filmic"
    world = bpy.data.worlds.get("PhotoWorld") or bpy.data.worlds.new("PhotoWorld")
    world.use_nodes = True
    bg = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
    bg.inputs["Color"].default_value = (0.36, 0.56, 0.95, 1.0)
    bg.inputs["Strength"].default_value = 1.0
    sc.view_settings.exposure = 0.35
    sc.world = world
    sun = bpy.data.objects.get("PhotoSun")
    if sun is None:
        sun = bpy.data.objects.new("PhotoSun", bpy.data.lights.new("PhotoSun", "SUN"))
        sc.collection.objects.link(sun)
    sun.data.energy = 4.5
    sun.data.color = (1.0, 0.96, 0.9)
    sun.data.angle = math.radians(1.5)
    to_sun = frame.b((0.55, -0.45, 0.70)).normalized()   # front-left and high, in the lift frame
    sun.rotation_euler = (-to_sun).to_track_quat("-Z", "Y").to_euler()
    sun.hide_render = False
    return sc, sun


def _photo_end(sc, extras):
    for o in extras:
        o.hide_render = True
    sc.view_settings.exposure = 0.0
    sc.render.engine = "BLENDER_WORKBENCH"


def _snow_plane(w):
    bpy.ops.mesh.primitive_plane_add(size=600, location=frame.b((0.0, 0.0, w)))
    snow = bpy.context.active_object
    snow.name = "PhotoSnow"
    snow.data.materials.append(_photo_material("PhotoSnowMat", (0.88, 0.91, 0.95), 0.55))
    return snow


def _rope_material():
    return _photo_material("PhotoRopeMat", (0.05, 0.05, 0.05), 0.45, 0.6)


def _photo_figure(at):
    fig = figure(at)
    fig.data.materials.clear()
    fig.data.materials.append(_photo_material("PhotoFigureMat", (0.12, 0.28, 0.62), 0.6))
    fig.hide_render = False
    return fig


def _shooter(prefix, out_dir):
    cam = camera("PhotoCam")
    cam.data.type = "PERSP"
    cam.data.sensor_width = 36
    cam.data.clip_start, cam.data.clip_end = 0.05, 800

    only = [s for s in os.environ.get("LIFT_PHOTOS", "").split(",") if s]   # optional subset, e.g. "hero,back"

    def shoot(name, target, az, el, dist, lens=50):
        if only and name not in only:
            return
        a, e = math.radians(az), math.radians(el)
        d = Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
        eye = Vector(target) + d * dist
        cam.data.lens = lens
        cam.location = frame.b(tuple(eye))
        cam.rotation_euler = (frame.b(tuple(target)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        if os.environ.get("LIFT_PICK"):   # debug: name what the camera sees at "x,y;x,y" pixels
            _pick(cam, os.environ["LIFT_PICK"])
        render(os.path.join(out_dir, f"{prefix}_photo_{name}.png"))
    return shoot


def _pick(cam, spec):
    sc = bpy.context.scene
    bpy.context.view_layer.update()
    rx, ry = sc.render.resolution_x, sc.render.resolution_y
    tl, bl = cam.data.view_frame(scene=sc)[3], cam.data.view_frame(scene=sc)[2]
    tr = cam.data.view_frame(scene=sc)[0]
    for xy in spec.split(";"):
        x, y = (float(s) for s in xy.split(","))
        local = tl + (tr - tl) * (x / rx) + (bl - tl) * (y / ry)
        origin = cam.matrix_world.translation
        d = (cam.matrix_world.to_3x3() @ local).normalized()
        hit, loc, nrm, idx, obj, _ = sc.ray_cast(bpy.context.evaluated_depsgraph_get(), origin, d)
        print(f"PICK {xy}: {obj.name if hit else None} face {idx} at lift {frame.lift(tuple(loc)) if hit else None} n {tuple(round(c, 2) for c in nrm) if hit else None}", flush=True)


def chair_photos(built, spec, out_dir, mats, key="chair"):
    """Cycles "photos" of a chair: hung on a rope stub over snow at the load level, sky and sun light, from the
    front, sides and back, close-ups of the grip and seat, a line of chairs at the stress-layout spacing, and
    one after snowfall."""
    sc, sun = _photo_scene()
    a0 = built["assets"][0]
    if "common" in spec:   # grip on the rope, seat over the 0.00 load level
        load = spec["common"]["ropeElevation"] / 1000
    else:                  # a chair without a lift of its own yet: its seat 0.47 m over the snow
        load = -a0.dims["seatW"] / 1000 + 0.47
    rope_d = spec.get("common", {}).get("ropeDiameter", 42)
    objs = lod_objects(built, 0)
    bpy.ops.mesh.primitive_cylinder_add(radius=rope_d / 2000, depth=200, vertices=12,
                                        location=frame.b((40.0, 0.0, 0.0)), rotation=(math.pi / 2, 0, 0))
    rope = bpy.context.active_object
    rope.name = "PhotoRope"
    rope.data.materials.append(_rope_material())
    extras = [rope, _snow_plane(-load), sun]   # show_only hides everything else, lights included
    fig = _photo_figure((0.4, 1.7, -load))
    show_only(objs + extras)
    restore_uv(objs)
    shoot = _shooter(key, out_dir)
    whole = (0.0, 0.0, -1.35)
    shoot("front_left", whole, 32, 8, 8.0)
    shoot("front_right", whole, -35, 12, 8.0)
    shoot("front", whole, 0, 4, 7.6)
    shoot("side", whole, 90, 3, 7.6)
    shoot("back", whole, 205, 14, 8.0)
    shoot("high", whole, 40, 42, 8.5)
    shoot("rider_view", (0.05, -0.03, -2.35), 12, 18, 3.4, lens=35)
    shoot("grip_hanger", (0.0, 0.05, -0.55), 55, 10, 2.4)
    shoot("grip", (0.0, 0.12, 0.0), 40, 16, 1.25, lens=45)
    shoot("grip_back", (0.0, 0.12, 0.0), 215, 12, 1.25, lens=45)
    shoot("grip_under", (0.0, 0.18, -0.08), 70, -16, 1.6, lens=45)   # from below and outboard, as riders see it
    shoot("seat_and_bar", (-0.05, -0.03, -2.25), 150, 28, 3.0)
    show_only(objs + extras + [fig])
    shoot("scale", (0.0, 0.8, -1.5), 20, 6, 9.5, lens=40)
    # a line of chairs at the stress-layout spacing (13.8 m), seen along the rope
    line = hang_chairs(objs, [((13.8 * k, 0.0, 0.0), 0.0) for k in range(1, 5)])
    show_only(objs + extras + line)
    shoot("line", (27.6, 0.0, -1.4), 8, 3, 43.0, lens=50)
    for o in line:
        bpy.data.objects.remove(o)
    show_only(objs + extras)
    materials.set_snow(mats, 1.0)
    shoot("snowfall", whole, 32, 14, 8.0)
    materials.set_snow(mats, 0.0)
    _photo_end(sc, extras + [fig])


def gun_photos(key, built, out_dir, mats):
    """Cycles "photos" of a snow gun on snow with a 1.8 m figure: the whole gun, close-ups (the stick gun's head
    and base; the ground gun from its valve side, its mount and from behind) and one after snowfall."""
    sc, sun = _photo_scene()
    objs = lod_objects(built, 0)
    snow = _snow_plane(0.0)
    extras = [snow, sun]
    nozzle = Vector(built["assets"][0].sockets["nozzle"])
    ground = key == "sle_ground_gun"
    fig = _photo_figure((0.2, 1.6, 0.0) if ground else (-1.0, -1.2, 0.0))
    show_only(objs + extras + [fig])
    restore_uv(objs)
    shoot = _shooter(key, out_dir)
    if ground:   # the valve paddle and the mount are on the gun's left (-v), as in the owner's photos
        mid = (0.02, 0.03, 0.85)
        shoot("hero", mid, 242, 20, 2.8, lens=40)
        shoot("front", mid, 320, 16, 2.8, lens=40)
        shoot("mount", (-0.03, 0.02, 1.07), 270, 6, 1.15, lens=45)
        shoot("behind", (0.0, 0.0, 0.8), 165, 30, 3.2, lens=35)
        shoot("head", (0.1, 0.0, 1.2), 300, 25, 1.3, lens=45)
        whole = mid
    else:
        pivot = Vector(built["assets"][0].pivots["lance"]["pos"])   # on the base mast's top
        whole = (nozzle.x * 0.45, 0.0, nozzle.z * 0.5)
        shoot("hero", whole, 35, 10, nozzle.z * 1.55 + 2.0, lens=35)
        shoot("side", whole, 90, 4, nozzle.z * 1.7 + 2.0, lens=35)
        shoot("head", tuple(nozzle - Vector((0.3, 0.0, 0.05))), 60, 12, 1.6, lens=45)
        shoot("head_back", tuple(nozzle - Vector((0.3, 0.0, 0.05))), 230, 16, 1.6, lens=45)
        shoot("base", (0.05, 0.0, pivot.z * 0.6), 305, 12, 3.2, lens=40)   # the mast, from the valve side (-v)
    materials.set_snow(mats, 1.0)
    if ground:
        shoot("snowfall", whole, 242, 20, 2.8, lens=40)
    else:
        shoot("snowfall", whole, 35, 10, nozzle.z * 1.55 + 2.0, lens=35)
    materials.set_snow(mats, 0.0)
    _photo_end(sc, extras + [fig])


SLE_LINEUP = (("sle_ground_gun", -2.6), ("sle_stick_gun_10", 0.0), ("sle_stick_gun_20", 2.6), ("sle_stick_gun_30", 6.2))


def sle_lineup(built_all, out_dir, mats):
    """All the SLE guns side by side on snow with a 1.8 m figure, seen from the side and from three-quarter."""
    sc, sun = _photo_scene()
    snow = _snow_plane(0.0)
    objs = []
    for key, du in SLE_LINEUP:
        _move(built_all[key], (du, 0.0, 0.0))
        objs += lod_objects(built_all[key], 0)
    fig = _photo_figure((-4.2, 0.0, 0.0))
    show_only(objs + [snow, sun, fig])
    restore_uv(objs)
    shoot = _shooter("sle", out_dir)
    shoot("lineup", (3.0, 0.0, 3.6), -90, 6, 20.0, lens=40)
    shoot("lineup_threequarter", (3.0, 0.0, 3.2), -55, 12, 22.0, lens=40)
    for key, du in SLE_LINEUP:
        _move(built_all[key], (-du, 0.0, 0.0))
    _photo_end(sc, [snow, sun, fig])


def sle_heads(built_all, out_dir, mats, stick="sle_stick_gun_20"):
    """The two heads at one scale: the ground gun (its gun, without the tripod) lifted level with the stick gun's
    head and set 0.9 m behind it, seen from the side and from three-quarter."""
    sc, sun = _photo_scene()
    sg, gg = built_all[stick], built_all["sle_ground_gun"]
    tip_s = Vector(sg["assets"][0].sockets["nozzle"])
    tip_g = Vector(gg["assets"][0].sockets["nozzle"])
    off = tuple(tip_s - tip_g - Vector((0.9, 0.0, 0.0)))
    _move(gg, off)
    objs = lod_objects(sg, 0) + [o for o in lod_objects(gg, 0) if o.name.startswith("sle_ground_gun_gun")]
    show_only(objs + [sun])
    restore_uv(objs)
    shoot = _shooter("sle", out_dir)
    mid = tuple(tip_s - Vector((0.75, 0.0, 0.12)))
    shoot("heads_side", mid, 90, 3, 3.0, lens=50)
    shoot("heads_threequarter", mid, 55, 14, 3.0, lens=50)
    _move(gg, tuple(-x for x in off))
    _photo_end(sc, [sun])


HILL_FROM, HILL_SLOPE = 12.0, math.radians(12.0)   # where the ground starts rising past the return, and how steeply


def _hill_w(u):
    """Ground height up the line from the return terminal (flat to HILL_FROM, then HILL_SLOPE)."""
    return max(0.0, u - HILL_FROM) * math.tan(HILL_SLOPE)


def _hillside():
    """The slope the line climbs past the return terminal: a snow sheet from HILL_FROM up the line."""
    u0, u1, half = HILL_FROM, 300.0, 300.0
    mesh = bpy.data.meshes.new("PhotoHill")
    corners = [(u0, -half, 0.0), (u1, -half, _hill_w(u1)), (u1, half, _hill_w(u1)), (u0, half, 0.0)]
    mesh.from_pydata([frame.b(p) for p in corners], [], [(0, 1, 2, 3)])
    if mesh.polygons[0].normal.z < 0:
        mesh.flip_normals()
    hill = bpy.data.objects.new("PhotoHill", mesh)
    bpy.context.scene.collection.objects.link(hill)
    hill.data.materials.append(_photo_material("PhotoSnowMat", (0.88, 0.91, 0.95), 0.55))
    return hill


def _rope_out_path(spec, a0, rope_w, far):
    """The rope from the bullwheel up the line, as (u, w): level to the hold-down row when the terminal has one
    (the return), round its arc and then climbing at the exit angle; otherwise level."""
    if "rope_right_hold" not in a0.sockets:
        return [(far, rope_w)]
    if "entryTrains" not in spec.get("return", {}):   # a terminal without the Sessellift's return arc (Monta): level
        u_hold = a0.sockets["rope_right_hold"][0]      # to the first sheave, straight to where the rope leaves the
        u_out, w_out = a0.sockets["rope_right_out"][0], a0.sockets["rope_right_out"][2]   # last one, then on up
        theta = math.radians(a0.dims.get("ropeExitDeg", 0.0))
        s = (far - u_out) / math.cos(theta)
        return [(u_hold, rope_w), (u_out, w_out), (u_out + s * math.cos(theta), w_out + s * math.sin(theta))]
    from liftkit import parts as lk_parts
    et = spec["return"]["entryTrains"]
    asm = lk_parts.line_spec(spec)
    arc, theta = lk_parts.hold_rope(asm, et["count"], asm["arcs"][et["arc"]], a0.sockets["rope_right_hold"][0], rope_w)
    eu, ew = arc[-1]
    s = (far - eu) / math.cos(theta)
    return arc + [(eu + s * math.cos(theta), ew + s * math.sin(theta))]


def _w_on(path, u, rope_w):
    """Rope height at u along a (u, w) path that starts level at rope_w."""
    prev = (-1e9, rope_w)
    for p in path:
        if u <= p[0]:
            f = 0.0 if p[0] == prev[0] else (u - prev[0]) / (p[0] - prev[0])
            return prev[1] + f * (p[1] - prev[1])
        prev = p
    return path[-1][1]


def terminal_photos(key, built, spec, out_dir, mats, chairs):
    """Cycles "photos" of a terminal on snow: the haul rope looped round the bullwheel with chairs on it
    (13.8 m apart) and a 1.8 m figure; hero, back, side, approach and high views, close-ups of the bullwheel and
    the entry, one after snowfall and, for the drive, the hood in other livery colours. At the return, the line
    leaves the station climbing: the rope bends up under the hold-down rows and runs up a hillside, and a
    close-up looks at the hold-down row from the side."""
    sc, sun = _photo_scene()
    c = spec["common"]
    objs = lod_objects(built, 0)
    a0 = built["assets"][0]
    rope_w = (a0.dims["rope"] if "rope" in a0.dims else c["ropeElevation"]) / 1000   # a terminal's own rope
    hg = c["lineGauge"] / 2000
    u_bw = a0.pivots["bullwheel"]["pos"][0]
    u_out = a0.sockets["rope_right_out"][0]
    climbing = "rope_right_hold" in a0.sockets
    # rope loop: in along the left rope, round the back of the wheel, out along the right rope
    far = 400.0 if climbing else 90.0
    path = _rope_out_path(spec, a0, rope_w, far)
    pts = ([(u, -hg, w) for u, w in reversed(path)]
           + [(u_bw - hg * math.sin(t), -hg * math.cos(t), rope_w) for t in (math.pi * k / 24 for k in range(25))]
           + [(u, hg, w) for u, w in path])
    curve = bpy.data.curves.new("PhotoRopeCurve" + key, "CURVE")
    curve.dimensions = "3D"
    curve.bevel_depth = c["ropeDiameter"] / 2000
    curve.bevel_resolution = 2
    sp = curve.splines.new("POLY")
    sp.points.add(len(pts) - 1)
    for i, p in enumerate(pts):
        sp.points[i].co = (*frame.b(p), 1.0)
    rope = bpy.data.objects.new("PhotoRope" + key, curve)
    sc.collection.objects.link(rope)
    rope.data.materials.append(_rope_material())
    extras = [rope, _snow_plane(0.0), sun] + ([_hillside()] if climbing else [])
    hung = []
    if chairs:
        pos = [(u_bw - hg, 0.0, rope_w)]
        pos += [(u, hg, _w_on(path, u, rope_w)) for u in (u_bw + 3.0 + 13.8 * k for k in range(4))]
        pos += [(u, -hg, _w_on(path, u, rope_w)) for u in (u_bw + 9.9 + 13.8 * k for k in range(4))]
        hung = hang_chairs(chairs, [(p, _yaw_for(p, u_bw)) for p in pos])
    fig = _photo_figure((1.2, hg + 1.4, 0.0) if key.endswith("return") else (1.5, -(hg + 1.4), 0.0))
    show_only(objs + extras + hung + [fig])
    restore_uv(objs)
    lo, hi = bounds_lift(objs)
    lo.z = max(lo.z, 0.0)
    centre = (lo + hi) / 2
    radius = (hi - lo).length / 2
    shoot_at = _shooter(key, out_dir)

    def shoot(name, target, az, el, dist, lens=50):   # up the line, lift the camera clear of the hillside
        while climbing and el < 60:
            a, e = math.radians(az), math.radians(el)
            eye_u, eye_w = target[0] + dist * math.cos(a) * math.cos(e), target[2] + dist * math.sin(e)
            if eye_w > _hill_w(eye_u) + 2.0:
                break
            el += 2
        shoot_at(name, target, az, el, dist, lens)

    shoot("hero", tuple(centre), 35, 12, radius * 2.3, lens=40)
    shoot("back", tuple(centre), 215, 18, radius * 2.3, lens=40)
    shoot("side", tuple(centre), 90, 5, radius * 2.3, lens=40)
    shoot("approach", (centre.x, 0.0, 2.2), 4, 3, radius * 3.2, lens=45)
    shoot("high", tuple(centre), 60, 45, radius * 2.4, lens=40)
    if climbing:   # the hold-down row from outboard of the right rope, and the line leaving up the hill
        u_hold = a0.sockets["rope_right_hold"][0]
        if key.startswith("monta_"):   # the Monta's booms run outboard of the trains: look out at them from between the ropes
            shoot("holddown", ((u_hold + u_out) / 2, hg, rope_w + 0.3), -90, 4, 3.5, lens=30)
        else:
            shoot("holddown", ((u_hold + u_out) / 2, hg, rope_w + 0.3), 90, -2, 6.5, lens=40)
        shoot("leaving", (u_out + 10.0, 0.0, _w_on(path, u_out + 10.0, rope_w) - 1.0), 200, 6, 32.0, lens=40)
    if key.endswith("drive"):   # under the hood: from low, looking up at the wheel
        shoot("bullwheel", (u_bw, 0.0, rope_w), 150, -7, 9.5, lens=35)
    else:
        shoot("bullwheel", (u_bw, 0.0, rope_w), 30, 35, 8.5, lens=35)
    shoot("entry", (u_out, 0.0, rope_w + 0.6), -35, 12, 9.0, lens=35)
    materials.set_snow(mats, 1.0)
    shoot("snowfall", tuple(centre), 35, 12, radius * 2.3, lens=40)
    materials.set_snow(mats, 0.0)
    if key.endswith("drive"):
        for name, rgb in (("blue", (0.08, 0.25, 0.62)), ("green", (0.08, 0.40, 0.18)), ("yellow", (0.85, 0.62, 0.08))):
            materials.set_livery(mats, rgb)
            shoot(f"livery_{name}", tuple(centre), 35, 12, radius * 2.3, lens=40)
        materials.set_livery(mats, (0.72, 0.12, 0.09))
    for o in hung:
        bpy.data.objects.remove(o)
    _photo_end(sc, extras + [fig])


def _move(built, offset_lift):
    """Moves every root object of a built asset by a lift-frame offset (children follow their parents)."""
    d = frame.b(offset_lift)
    for o in built["objects"]:
        if o.parent is None:
            o.location = o.location + d


def _stack(mast, foot_w, n, at_lift=(0.0, 0.0, 0.0)):
    """n linked copies of the mast section's LOD0, stacked from foot_w; returns them."""
    src = lod_objects(mast, 0)[0]
    made = []
    for i in range(n):
        o = src.copy()
        o.name = f"ReviewMast{len(bpy.data.objects)}"
        o.location = frame.b((at_lift[0], at_lift[1], foot_w + i * 1.0 + at_lift[2]))
        bpy.context.scene.collection.objects.link(o)
        made.append(o)
    return made


def _reference(path_spec, shift_lift):
    """An optional local reference OBJ (mm, Z up) for side-by-side views: "path;cx;cy;flip" puts its mast
    centre (cx, cy) on the lift frame's origin, x along u (reversed if flip), y along v."""
    path, cx, cy, flip = path_spec.split(";")
    before = set(bpy.data.objects)
    bpy.ops.wm.obj_import(filepath=path, forward_axis="Y", up_axis="Z")
    objs = [o for o in bpy.data.objects if o not in before and o.type == "MESH"]
    su = -1.0 if flip == "1" else 1.0
    # lift (u, v, w) = (su * (x - cx), y - cy, z) / 1000;  blender = (-v, -u, w)
    mt = Matrix(((0, -0.001, 0, float(cy) / 1000), (-0.001 * su, 0, 0, su * float(cx) / 1000), (0, 0, 0.001, 0), (0, 0, 0, 1)))
    mt = Matrix.Translation(frame.b(shift_lift)) @ mt
    grey = bpy.data.materials.new("ReferenceGrey")
    grey.diffuse_color = (0.72, 0.62, 0.5, 1)
    for o in objs:
        o.data.transform(mt)
        o.data.materials.clear()
        o.data.materials.append(grey)
    return objs


def tower_views(built_all, spec, out_dir):
    """Workbench review of the tower kit: a full tower (base, sections, support-8 head) from three sides next to
    a 1.8 m figure, optionally beside a local reference model (LIFT_TOWER_REF), a line-up of the six heads, and
    close-ups of the support-8 and combination assemblies."""
    sc = scene_setup()
    sc.display.shading.show_shadows = True
    sc.render.film_transparent = False
    sc.world = sc.world or bpy.data.worlds.new("World")
    base, mast = built_all["tower_base"], built_all["tower_mast"]
    heads = [k for k in built_all if k.startswith("tower_") and k not in ("tower_base", "tower_mast")]
    foot = base["assets"][0].sockets["mast_foot"][2]
    n = int(os.environ.get("LIFT_TOWER_SECTIONS", "9"))
    cam = camera("TowerCam")
    everything = [o for b in built_all.values() for o in b["objects"]]
    use_palette_uv([o for o in everything if o.type == "MESH"])

    def shoot(name, objs, az, el, lens=35, ortho=False, res=(1600, 1200), pad=1.08, dist=2.6):
        show_only(objs)
        bpy.context.view_layer.update()   # matrix_world lags a changed location until the layer updates
        lo, hi = bounds_lift([o for o in objs if o.type == "MESH" and o.name != "ReviewGround"])
        centre = (lo + hi) / 2
        a, e = math.radians(az), math.radians(el)
        d = Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
        sc.render.resolution_x, sc.render.resolution_y = res
        radius = (hi - lo).length / 2
        cam.location = frame.b(tuple(centre + d * radius * (dist if not ortho else 4)))
        cam.rotation_euler = (frame.b(tuple(centre)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        cam.data.clip_end = 500
        if ortho:
            cam.data.type = "ORTHO"
            rot = cam.rotation_euler.to_matrix()
            right, up = rot.col[0], rot.col[1]
            pts = [frame.b((x, y, z)) for x in (lo.x, hi.x) for y in (lo.y, hi.y) for z in (lo.z, hi.z)]
            w_ = max(p.dot(right) for p in pts) - min(p.dot(right) for p in pts)
            h_ = max(p.dot(up) for p in pts) - min(p.dot(up) for p in pts)
            cam.data.ortho_scale = max(w_, h_ * res[0] / res[1]) * pad
        else:
            cam.data.type = "PERSP"
            cam.data.lens = lens
        render(os.path.join(out_dir, f"tower_{name}.png"))

    at = {k: Vector((0.0, 0.0, 0.0)) for k in heads}

    def place(k, target):   # moves a head (bodies, pivots, sockets) from where it is to target (lift frame)
        target = Vector(target)
        _move(built_all[k], tuple(target - at[k]))
        at[k] = target

    for i, k in enumerate(heads):   # park every head out of view
        place(k, (0.0, 80.0 + 12.0 * i, 0.0))

    # a full breakover (support-8) tower at the origin
    s8 = built_all["tower_b8"]
    place("tower_b8", (0.0, 0.0, foot + n))
    stack = _stack(mast, foot, n)
    fig = figure((-1.2, 1.0, 0.0))
    g = ground(40, 0.0)
    tower = lod_objects(base, 0) + stack + lod_objects(s8, 0) + [fig, g]
    rope = s8["assets"][0].dims["rope"] / 1000 + foot + n
    print(f"Review tower: {n} sections, rope {rope:.2f} m above grade", flush=True)
    shoot("full_threequarter", tower, 215, 14)
    shoot("full_front", tower, 180, 0, ortho=True, res=(1200, 1600))
    shoot("full_side", tower, 90, 0, ortho=True, res=(1200, 1600))
    ref = os.environ.get("LIFT_TOWER_REF")
    if ref:
        refs = _reference(ref, (0.0, 7.0, 0.0))
        shoot("compare_front", tower + refs, 180, 0, ortho=True, res=(1800, 1600))
        shoot("compare_threequarter", tower + refs, 215, 12, res=(1800, 1400))
        for o in refs:
            bpy.data.objects.remove(o)
    for o in stack:
        bpy.data.objects.remove(o)

    # heads alone, and close-ups of one rope's assembly
    for k, tag in (("tower_b8", "b8"), ("tower_c8", "c8"), ("tower_d8", "d8")):
        if k not in built_all:
            continue
        place(k, (0.0, 0.0, 0.0))
        objs = lod_objects(built_all[k], 0)
        shoot(f"head_{tag}_threequarter", objs, 215, 22)
        shoot(f"head_{tag}_under", objs, 140, -18)
        right = [o for o in objs if "_sheave_r" in o.name]
        bpy.context.view_layer.update()
        if right:
            shoot(f"assembly_{tag}_out", objs, 20, 10, lens=50, res=(1600, 1000))
            lo, hi = bounds_lift(right)
            c = (lo + hi) / 2
            for name, az, el in ((f"assembly_{tag}_close", 25, 12), (f"assembly_{tag}_inboard", 205, 18)):
                a, e = math.radians(az), math.radians(el)
                d = Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
                show_only(objs)
                cam.data.type = "PERSP"
                cam.data.lens = 50
                cam.location = frame.b(tuple(c + d * 5.0))
                cam.rotation_euler = (frame.b(tuple(c)) - cam.location).to_track_quat("-Z", "Y").to_euler()
                sc.render.resolution_x, sc.render.resolution_y = 1600, 1000
                render(os.path.join(out_dir, f"tower_{name}.png"))
        place(k, (0.0, 80.0 + 12.0 * heads.index(k), 0.0))

    # line-up of every head on two sections, left to right in the heads table's order
    placed = []
    for i, k in enumerate(heads):
        spot = (0.0, (i - (len(heads) - 1) / 2) * 6.0, 0.0)
        place(k, (spot[0], spot[1], 2.0))
        placed += lod_objects(built_all[k], 0) + _stack(mast, 0.0, 2, at_lift=spot)
    shoot("heads_lineup", placed, 195, 14, lens=35, res=(2000, 1000), dist=2.3)
    shoot("heads_lineup_side", placed, 158, 12, lens=35, res=(2000, 1000), dist=2.3)
    for o in [x for x in placed if x.name.startswith("ReviewMast")]:
        bpy.data.objects.remove(o)
    for k in heads:
        place(k, (0.0, 0.0, 0.0))
    restore_uv([o for o in everything if o.type == "MESH"])


def _rope_over_head(spec, variant, rope_w, v, far=60.0):
    """The rope's path past a tower head (lift frame points): on a support head it rides over the sheaves' arc and
    leaves along its tangents, on a hold-down head it passes under the arc, on a combination it runs straight."""
    from liftkit import parts as lk_parts
    t = spec["tower"]
    cfg = t["heads"][variant]
    a = lk_parts.line_spec(spec)
    if "combo" in cfg:
        return [Vector((-far, v, rope_w)), Vector((far, v, rope_w))]
    mode = "support" if "support" in cfg else "hold"
    radius = a["arcs"][cfg["arc"]]
    ends = lk_parts.arc_axles(a, cfg[mode], radius)
    rr = radius + a["sheave"]["dia"] / 2 + a["ropeR"]           # the rope's circle, about the axle circle's centre
    phi = math.asin(ends[-1][0] / radius)
    sg = 1.0 if mode == "support" else -1.0                       # support: centre below, rope over the top
    centre_w = rope_w - sg * rr
    pts = []
    for k in range(-12, 13):
        p = phi * k / 12
        pts.append(Vector((rr * math.sin(p), v, centre_w + sg * rr * math.cos(p))))
    for s in (-1, 1):   # tangents beyond the end sheaves
        p = s * phi
        e = Vector((rr * math.sin(p), v, centre_w + sg * rr * math.cos(p)))
        d = Vector((math.cos(p), 0.0, -sg * math.sin(p)))
        pts.insert(0 if s < 0 else len(pts), e + d * (s * far))
    return pts


def tower_photos(built_all, spec, out_dir, mats, chairs):
    """Cycles "photos" of each tower head type on a full tower: base, mast sections to a rope about 9.5 m above the
    snow, the haul rope over (or under) the sheaves with a chair on each side, and a 1.8 m figure; a hero view,
    a close-up of the head and a view up from under the line."""
    sc, sun = _photo_scene()
    base, mast = built_all["tower_base"], built_all["tower_mast"]
    heads = [k for k in built_all if k.startswith("tower_") and k not in ("tower_base", "tower_mast")]
    foot = base["assets"][0].sockets["mast_foot"][2]
    hg = spec["common"]["lineGauge"] / 2000
    snow = _snow_plane(0.0)
    fig = _photo_figure((0.9, -1.1, 0.0))
    bpy.context.view_layer.update()
    for k in heads:
        b = built_all[k]
        variant = k[len("tower_"):]
        rope_head = b["assets"][0].sockets["rope_right"][2]
        n = max(3, round(9.5 - foot - rope_head))
        lift = foot + n
        _move(b, (0.0, 0.0, lift))
        stack = _stack(mast, foot, n)
        rope_w = lift + rope_head
        ropes = []
        for side in (-1, 1):
            pts = _rope_over_head(spec, variant, rope_w, side * hg)
            curve = bpy.data.curves.new(f"TowerRope{side}", "CURVE")
            curve.dimensions = "3D"
            curve.bevel_depth = spec["common"]["ropeDiameter"] / 2000
            curve.bevel_resolution = 2
            sp = curve.splines.new("POLY")
            sp.points.add(len(pts) - 1)
            for i, p in enumerate(pts):
                sp.points[i].co = (*frame.b(tuple(p)), 1.0)
            ro = bpy.data.objects.new(f"TowerRope{side}", curve)
            sc.collection.objects.link(ro)
            ro.data.materials.append(_rope_material())
            ropes.append((ro, pts))
        hung = []
        if chairs:   # a chair on each rope, 7 m out, on the rope's path
            for (ro, pts), side, u in ((ropes[0], -1, -7.0), (ropes[1], 1, 7.0)):
                pa = min(pts, key=lambda p: abs(p.x - u))
                hung += hang_chairs(chairs, [((pa.x, pa.y, pa.z), 0.0 if side > 0 else math.pi)])
        objs = lod_objects(base, 0) + stack + lod_objects(b, 0)
        show_only(objs + [snow, fig, sun] + [r for r, _ in ropes] + hung)
        restore_uv(objs)
        bpy.context.view_layer.update()
        shoot = _shooter(f"tower_{variant}", out_dir)
        head_c = (0.0, 0.0, rope_w)
        top = rope_w + 2.2                                  # the portal beam is about 1.9 m over the rope
        shoot("hero", (0.0, 0.0, top / 2), 32, 8, top * 1.9 + 4.0, lens=40)
        shoot("head", head_c, 28, 12, 7.5, lens=40)
        shoot("under", head_c, 160, -28, 7.0, lens=32)
        for o in hung + [r for r, _ in ropes] + stack:
            bpy.data.objects.remove(o)
        _move(b, (0.0, 0.0, -lift))
    _photo_end(sc, [snow, fig])


def head_match(drive, tower_head, spec, out_dir):
    """The drive terminal's entry head and a tower head from the same camera, placed relative to each crossbeam's
    centre, so the two can be compared directly."""
    sc = scene_setup()
    sc.display.shading.show_shadows = True
    sc.render.film_transparent = False
    sc.world = sc.world or bpy.data.worlds.new("World")
    cb = spec["drive"]["crossbeam"]
    tw = spec["tower"]
    centre_drive = Vector(((cb["uFrom"] + cb["uTo"]) / 2000, 0.0, (cb["bottom"] + cb["top"]) / 2000))
    ow = tw["head"]["cap"]["thick"] / 1000 - cb["bottom"] / 1000
    centre_tower = Vector((0.0, 0.0, (cb["bottom"] + cb["top"]) / 2000 + ow))
    bpy.context.view_layer.update()
    cam = camera("HeadCam")
    cam.data.type = "PERSP"
    cam.data.lens = 35
    cam.data.clip_end = 300
    sc.render.resolution_x, sc.render.resolution_y = 1600, 1100
    for key, built, centre in (("drive_entry", drive, centre_drive), ("tower_b8", tower_head, centre_tower)):
        objs = lod_objects(built, 0)
        show_only(objs)
        use_palette_uv(objs)
        for name, (az, el, dist) in {"left": (330, 18, 8.5), "right": (25, 10, 8.5)}.items():
            a, e = math.radians(az), math.radians(el)
            d = Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
            target = centre + Vector((0.0, 0.0, 0.6))
            cam.location = frame.b(tuple(target + d * dist))
            cam.rotation_euler = (frame.b(tuple(target)) - cam.location).to_track_quat("-Z", "Y").to_euler()
            render(os.path.join(out_dir, f"headmatch_{name}_{key}.png"))
        restore_uv(objs)


def render_all(built, spec, out_dir, shots, mats):
    out_dir = os.path.abspath(out_dir)   # Blender resolves relative render paths against the .blend, not the cwd
    os.makedirs(out_dir, exist_ok=True)
    shots = (shots or "gate1,lods").split(",")
    chairs = lod_objects(built["chair"], 0) if "chair" in built else None
    packed = bpy.data.images["lift_palette"]
    materials.use_palette(mats, materials.opaque_copy(packed))
    if "gate1" in shots:
        for key in built:
            if key in ORTHO:
                scene_setup()
                # terminals alone: the source elevations draw chairs in places that don't match one static
                # model pose, and the chair has its own views
                ortho_views(key, built[key], out_dir)
        for key in built:
            if key in TERMINALS + CHAIRS or key.startswith("sle_"):
                clay_views(key, built[key], built[key].get("spec", spec), out_dir, chairs)
            if key.startswith("sle_"):
                sle_views(key, built[key], out_dir)
    if "lods" in shots:
        lod_lineup({k: v for k, v in built.items() if k in ("drive", "return", "chair")}, out_dir)
    if "data" in shots:
        for key in built:
            data_views(key, built[key], out_dir)
    if "towers" in shots and "tower_base" in built and "tower_mast" in built and "tower_b8" in built:
        tower_views({k: v for k, v in built.items() if k.startswith("tower_")}, spec, out_dir)
        if "drive" in built:
            head_match(built["drive"], built["tower_b8"], spec, out_dir)
    materials.use_palette(mats, packed)
    if "photos" in shots or "terminal_photos" in shots:
        for key in built:
            if key in CHAIRS and "photos" in shots:
                chair_photos(built[key], built[key].get("spec", spec), out_dir, mats, key)
            elif key.startswith("sle_") and "photos" in shots:
                gun_photos(key, built[key], out_dir, mats)
            elif key in TERMINALS:
                terminal_photos(key, built[key], built[key].get("spec", spec), out_dir, mats, chairs)
    if "photos" in shots and all(k in built for k, _ in SLE_LINEUP):
        sle_lineup(built, out_dir, mats)
        sle_heads(built, out_dir, mats)
    if ("photos" in shots or "tower_photos" in shots) and "tower_base" in built and "tower_mast" in built:
        tower_photos({k: v for k, v in built.items() if k.startswith("tower_")}, spec, out_dir, mats, chairs)
    print(f"Review renders in {out_dir}", flush=True)
