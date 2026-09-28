"""Review renders for the lift assets (loaded by build_lifts.py --render DIR [--shots ...]).

Shots:
  gate1   orthographic side / end / plan elevations of each terminal (Workbench, transparent background) with a
          <name>_meta.json beside each (mm per pixel, pixel of the model origin, image axes), plus clay 3/4 views
          with chairs hung on the rope and a 1.8 m figure for scale.
  lods    every LOD of every asset side by side, with triangle counts.
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
    restore_uv(objs)


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
                extras = drawing_chairs(built[key], spec, chairs) if key in ("drive", "return") and chairs else []
                ortho_views(key, built[key], out_dir, extras)
                for o in extras:
                    bpy.data.objects.remove(o)
        for key in built:
            if key in ("drive", "return", "chair"):
                clay_views(key, built[key], spec, out_dir, chairs)
    if "lods" in shots:
        lod_lineup({k: v for k, v in built.items() if k in ("drive", "return", "chair")}, out_dir)
    materials.use_palette(mats, packed)
    print(f"Review renders in {out_dir}", flush=True)
