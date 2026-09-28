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
}
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
    c = spec["common"]
    rope = c["ropeElevation"] / 1000
    hg = c["lineGauge"] / 2000
    if key in ("drive", "return") and chairs:
        u_bw = built["assets"][0].pivots["bullwheel"]["pos"][0]
        # chairs: travel +u on the right rope, -u on the left rope, and one at the far end of the wheel
        pos = [((u_bw + 3.0, hg, rope), 0.0), ((u_bw + 3.0, -hg, rope), math.pi), ((u_bw - hg, 0.0, rope), math.pi / 2)]
        # chair frame: +u travel, +v outboard. In Blender, lift +u = -y; yaw rotates about z.
        extras = hang_chairs(chairs, [(p, _yaw_for(p, u_bw)) for p, _ in pos])
        extras.append(figure((1.5 if key == "drive" else 2.5, 3.2 if key == "drive" else -2.6, 0.0)))
    elif key == "chair":
        extras.append(figure((0.0, 1.6, -3.04)))
    g = ground(60, 0.0 if key != "chair" else -3.04)
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
    for name, (az, el) in {"threequarter_line": (35, 18), "threequarter_back": (215, 22), "low": (120, 6)}.items():
        a, e = math.radians(az), math.radians(el)
        d = Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
        eye = centre + d * radius * 2.4
        cam.location = frame.b(tuple(eye))
        look = frame.b(tuple(centre)) - cam.location
        cam.rotation_euler = look.to_track_quat("-Z", "Y").to_euler()
        render(os.path.join(out_dir, f"{key}_clay_{name}.png"))
    for o in extras:
        if o.name.startswith("ReviewChair"):
            bpy.data.objects.remove(o)
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
        render(os.path.join(out_dir, f"{prefix}_photo_{name}.png"))
    return shoot


def chair_photos(built, spec, out_dir, mats):
    """Cycles "photos" of the chair: hung on a rope stub over snow at the load level, sky and sun light, from the
    front, sides and back, close-ups of the grip and seat, a line of chairs at the stress-layout spacing, and
    one after snowfall."""
    sc, sun = _photo_scene()
    load = spec["common"]["ropeElevation"] / 1000   # grip on the rope, seat over the 0.00 load level
    objs = lod_objects(built, 0)
    bpy.ops.mesh.primitive_cylinder_add(radius=spec["common"]["ropeDiameter"] / 2000, depth=200, vertices=12,
                                        location=frame.b((40.0, 0.0, 0.0)), rotation=(math.pi / 2, 0, 0))
    rope = bpy.context.active_object
    rope.name = "PhotoRope"
    rope.data.materials.append(_rope_material())
    extras = [rope, _snow_plane(-load), sun]   # show_only hides everything else, lights included
    fig = _photo_figure((0.4, 1.7, -load))
    show_only(objs + extras)
    restore_uv(objs)
    shoot = _shooter("chair", out_dir)
    whole = (0.0, 0.0, -1.35)
    shoot("front_left", whole, 32, 8, 8.0)
    shoot("front_right", whole, -35, 12, 8.0)
    shoot("front", whole, 0, 4, 7.6)
    shoot("side", whole, 90, 3, 7.6)
    shoot("back", whole, 205, 14, 8.0)
    shoot("high", whole, 40, 42, 8.5)
    shoot("rider_view", (0.05, -0.03, -2.35), 12, 18, 3.4, lens=35)
    shoot("grip_hanger", (0.0, 0.05, -0.55), 55, 10, 2.4)
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


def terminal_photos(key, built, spec, out_dir, mats, chairs):
    """Cycles "photos" of a terminal on snow: the haul rope looped round the bullwheel with chairs on it
    (13.8 m apart) and a 1.8 m figure; hero, back, side, approach and high views, close-ups of the bullwheel and
    the entry, one after snowfall and, for the drive, the hood in other livery colours."""
    sc, sun = _photo_scene()
    c = spec["common"]
    rope_w, hg = c["ropeElevation"] / 1000, c["lineGauge"] / 2000
    objs = lod_objects(built, 0)
    a0 = built["assets"][0]
    u_bw = a0.pivots["bullwheel"]["pos"][0]
    u_out = a0.sockets["rope_right_out"][0]
    # rope loop: in along the left rope, round the back of the wheel, out along the right rope
    far = 90.0
    pts = [(far, -hg, rope_w)] + [(u_bw - hg * math.sin(t), -hg * math.cos(t), rope_w)
                                   for t in (math.pi * k / 24 for k in range(25))] + [(far, hg, rope_w)]
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
    extras = [rope, _snow_plane(0.0), sun]
    hung = []
    if chairs:
        pos = [(u_bw - hg, 0.0, rope_w)]
        pos += [(u_bw + 3.0 + 13.8 * k, hg, rope_w) for k in range(4)]
        pos += [(u_bw + 9.9 + 13.8 * k, -hg, rope_w) for k in range(4)]
        hung = hang_chairs(chairs, [(p, _yaw_for(p, u_bw)) for p in pos])
    fig = _photo_figure((1.2, hg + 1.4, 0.0) if key == "return" else (1.5, -(hg + 1.4), 0.0))
    show_only(objs + extras + hung + [fig])
    restore_uv(objs)
    lo, hi = bounds_lift(objs)
    lo.z = max(lo.z, 0.0)
    centre = (lo + hi) / 2
    radius = (hi - lo).length / 2
    shoot = _shooter(key, out_dir)
    shoot("hero", tuple(centre), 35, 12, radius * 2.3, lens=40)
    shoot("back", tuple(centre), 215, 18, radius * 2.3, lens=40)
    shoot("side", tuple(centre), 90, 5, radius * 2.3, lens=40)
    shoot("approach", (centre.x, 0.0, 2.2), 4, 3, radius * 3.2, lens=45)
    shoot("high", tuple(centre), 60, 45, radius * 2.4, lens=40)
    if key == "drive":   # under the hood: from low, looking up at the wheel
        shoot("bullwheel", (u_bw, 0.0, rope_w), 150, -7, 9.5, lens=35)
    else:
        shoot("bullwheel", (u_bw, 0.0, rope_w), 30, 35, 8.5, lens=35)
    shoot("entry", (u_out, 0.0, rope_w + 0.6), -35, 12, 9.0, lens=35)
    materials.set_snow(mats, 1.0)
    shoot("snowfall", tuple(centre), 35, 12, radius * 2.3, lens=40)
    materials.set_snow(mats, 0.0)
    if key == "drive":
        for name, rgb in (("blue", (0.08, 0.25, 0.62)), ("green", (0.08, 0.40, 0.18)), ("yellow", (0.85, 0.62, 0.08))):
            materials.set_livery(mats, rgb)
            shoot(f"livery_{name}", tuple(centre), 35, 12, radius * 2.3, lens=40)
        materials.set_livery(mats, (0.72, 0.12, 0.09))
    for o in hung:
        bpy.data.objects.remove(o)
    _photo_end(sc, extras + [fig])


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
            if key in ("drive", "return", "chair"):
                clay_views(key, built[key], spec, out_dir, chairs)
    if "lods" in shots:
        lod_lineup({k: v for k, v in built.items() if k in ("drive", "return", "chair")}, out_dir)
    if "data" in shots:
        for key in built:
            data_views(key, built[key], out_dir)
    materials.use_palette(mats, packed)
    if "photos" in shots or "terminal_photos" in shots:
        for key in built:
            if key == "chair" and "photos" in shots:
                chair_photos(built[key], spec, out_dir, mats)
            elif key in ("drive", "return"):
                terminal_photos(key, built[key], spec, out_dir, mats, chairs)
    print(f"Review renders in {out_dir}", flush=True)
