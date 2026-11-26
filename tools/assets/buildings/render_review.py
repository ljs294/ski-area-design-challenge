"""Gate 1 review renders for the buildings (loaded by build_buildings.py --render DIR), Workbench only.

  <id>_<view>.png       orthographic elevations (yard, water, both gables) and the plan, transparent background, with a
                        <id>_<view>_meta.json beside each: mm per pixel, the pixel of the building origin, the image axes,
                        and every light and socket projected to pixels (the sheet compositor draws dimensions on them)
  <id>_clay_<name>.png  clay 3/4 views on a pale ground with a 1.8 m figure (and, where the spec's review lists
                        `machines`, groomer and pickup scale boxes; a sign's text lettered on its board)
  <id>_lods.png         every LOD side by side
Blender's metadata stamp is off so the PNGs carry no paths. The helpers come from the lift review
(tools/assets/lifts/render_review.py), loaded under its own name.
"""
import importlib.util
import json
import math
import os

import bpy
from mathutils import Matrix, Vector

from bldgkit import LIFTS
from liftkit import frame, materials

_spec = importlib.util.spec_from_file_location("lift_render_review", os.path.join(LIFTS, "render_review.py"))
lrr = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(lrr)

MM_PER_PX = 10.0
lrr.MM_PER_PX = MM_PER_PX   # place_ortho reads it
# (view, image right, image up) in the building frame; each looks at its wall from outside
# (the lift frame is a reflection of Blender's: the viewer stands at -(right x up) in it, liftkit/frame.py)
ORTHO = [("yard", "+u", "+w"), ("water", "-u", "+w"), ("gable_east", "-v", "+w"), ("gable_west", "+v", "+w"),
         ("plan", "+u", "-v")]
# each building's close-ups and figure spot are its spec's "review" (detail, figure)
CLAY = {"yard": (62, 16), "water": (242, 16), "gable": (160, 12), "high": (25, 42)}


def _project(p, r, u, centre, w_px, h_px):
    lp = Vector(p) - Vector(tuple(centre))
    k = 1000.0 / MM_PER_PX
    return [round(w_px / 2 + lp.dot(r) * k, 1), round(h_px / 2 - lp.dot(u) * k, 1)]


def ortho_views(aid, built, out_dir):
    global MM_PER_PX
    rv = built["spec"]["review"]
    # a wide site (the garage with its fuel pad) can draw at another scale (review `mmPerPx`: a number, or per view with
    # "*" for the rest), and leave a part out of a view (`orthoHide`: view -> parts), so the sheets' text reads
    scale = rv.get("mmPerPx", 10.0)
    all_objs = lrr.lod_objects(built, 0)
    a0 = built["assets"][0]
    cam = lrr.camera()
    for view, rs, us in ORTHO:
        MM_PER_PX = lrr.MM_PER_PX = scale.get(view, scale.get("*", 10.0)) if isinstance(scale, dict) else scale
        hide = rv.get("orthoHide", {}).get(view, [])
        objs = [o for o in all_objs if not any(f"_{p}_LOD" in o.name for p in hide)]
        lrr.show_only(objs)
        lrr.use_palette_uv(objs)
        lo, hi = lrr.bounds_lift(objs)
        lo -= Vector((0.6, 0.6, 0.0))
        hi += Vector((0.6, 0.6, 0.6))
        # the stem wall's buried skirt (a hut's buried posts) is left out of the frame, below the review's ground
        lo.z = max(lo.z, rv.get("ground", 0.0) - 0.3)
        r, u = lrr.axis(rs), lrr.axis(us)
        corners = [Vector((x, y, z)) for x in (lo.x, hi.x) for y in (lo.y, hi.y) for z in (lo.z, hi.z)]
        rr, uu = [c.dot(r) for c in corners], [c.dot(u) for c in corners]
        width, height = max(rr) - min(rr), max(uu) - min(uu)
        centre = r * (max(rr) + min(rr)) / 2 + u * (max(uu) + min(uu)) / 2
        lrr.place_ortho(cam, tuple(r), tuple(u), tuple(centre), width, height)   # ortho_scale spans the longer side
        name = f"{aid}_{view}"
        lrr.render(os.path.join(out_dir, name + ".png"))
        sc = bpy.context.scene
        w_px, h_px = sc.render.resolution_x, sc.render.resolution_y
        meta = {"view": view, "mmPerPx": MM_PER_PX, "size": [w_px, h_px], "right": rs, "up": us,
                "origin": _project((0, 0, 0), r, u, centre, w_px, h_px),
                "sockets": {k: _project(p, r, u, centre, w_px, h_px) for k, p in a0.sockets.items()},
                "lights": [{**lt, "px": _project(lt["pos"], r, u, centre, w_px, h_px),
                            "aimPx": [round(Vector(lt["aim"]).dot(r), 3), round(-Vector(lt["aim"]).dot(u), 3)]}
                           for lt in a0.lights],
                "dims": a0.dims}
        with open(os.path.join(out_dir, name + "_meta.json"), "w") as f:
            json.dump(meta, f, indent=2)
        lrr.restore_uv(objs)


def clay_views(aid, built, out_dir):
    sc = _setup()
    sc.display.shading.show_shadows = True
    sc.render.film_transparent = False
    objs = lrr.lod_objects(built, 0)
    rv = built["spec"]["review"]
    g = lrr.ground(90, rv.get("ground", 0.0) - 0.002)   # a hut at a top station stands over lower ground
    fig = lrr.figure(tuple(rv["figure"]))   # beside the overhead door, clear of the doors
    extras = [g, fig] + [scale_machine(m, i) for i, m in enumerate(rv.get("machines", []))]
    extras += sign_letters(built["assets"][0])
    lrr.show_only(objs + extras)
    lrr.use_palette_uv(objs + extras)
    lo, hi = lrr.bounds_lift(objs)
    lo.z = rv.get("ground", 0.0)
    centre, radius = (lo + hi) / 2, (hi - lo).length / 2
    cam = lrr.camera("ClayCam")
    cam.data.type = "PERSP"
    cam.data.lens = 35
    cam.data.clip_end = 600
    sc.render.resolution_x, sc.render.resolution_y = 1600, 1000
    for name, (az, el) in CLAY.items():
        a, e = math.radians(az), math.radians(el)
        d = Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
        cam.location = frame.b(tuple(centre + d * radius * 2.2))
        cam.rotation_euler = (frame.b(tuple(centre)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        lrr.render(os.path.join(out_dir, f"{aid}_clay_{name}.png"))
    # close-ups of the details: each a (target, eye offset from it, lens) in the building frame; "light:<name>" aims
    # at that light's socket
    a0 = built["assets"][0]
    for name, (target, offset, lens) in rv["detail"].items():
        t = Vector(a0.sockets[target[6:]]) if isinstance(target, str) else Vector(target)
        cam.data.lens = lens
        cam.location = frame.b(tuple(t + Vector(offset)))
        cam.rotation_euler = (frame.b(tuple(t)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        lrr.render(os.path.join(out_dir, f"{aid}_detail_{name}.png"))
    cam.data.lens = 35
    clean = rv.get("cleanClay", [])
    if clean and rv.get("machines"):   # the same views without the scale boxes, to judge the building's look
        lrr.show_only(objs + [g, fig] + extras[2 + len(rv["machines"]):])
        for name in clean:
            az, el = CLAY[name]
            a, e = math.radians(az), math.radians(el)
            d = Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
            cam.location = frame.b(tuple(centre + d * radius * 2.2))
            cam.rotation_euler = (frame.b(tuple(centre)) - cam.location).to_track_quat("-Z", "Y").to_euler()
            lrr.render(os.path.join(out_dir, f"{aid}_clay_{name}_clean.png"))
    lrr.restore_uv(objs)


MACHINE = {   # review-only scale boxes, (name, x from, x to, half width, z from, z to) in the machine's frame (x forward)
    # a 400/600-class groomer (Gate 0): 9.1 m with blade and tiller, 4.2 m over the tracks, blade folded 4.6 m, 2.9 m
    # to the cab roof
    "groomer": ([("track_l", -2.4, 2.4, (1.475, 2.1), 0.0, 1.0), ("track_r", -2.4, 2.4, (-2.1, -1.475), 0.0, 1.0),
                 ("hull", -2.3, 2.3, (-1.2, 1.2), 0.85, 1.75), ("cab", -0.2, 1.8, (-1.0, 1.0), 1.75, 2.9),
                 ("blade", 4.2, 4.55, (-2.3, 2.3), 0.15, 1.45), ("arm", 2.3, 4.2, (-0.3, 0.3), 0.55, 0.85),
                 ("tiller", -4.55, -2.75, (-2.2, 2.2), 0.05, 0.75)], (0.78, 0.18, 0.12, 1)),
    # a crew-cab pickup: 5.9 x 2.0 x 1.9 m
    "pickup": ([("body", -2.95, 2.95, (-1.0, 1.0), 0.4, 1.1), ("cab", -0.4, 1.4, (-0.95, 0.95), 1.1, 1.9),
                ("wheels", -2.0, 2.0, (-1.0, 1.0), 0.0, 0.4)], (0.20, 0.28, 0.36, 1)),
}


def scale_machine(m, i):
    """A groomer or pickup as boxes (MACHINE) at `at` (u, v metres), heading `heading` degrees from +u."""
    import bmesh
    name = f"ScaleMachine{i}"
    o = bpy.data.objects.get(name)
    if o is None:
        parts, colour = MACHINE[m["kind"]]
        bm = bmesh.new()
        h = math.radians(m["heading"])
        cu, su = math.cos(h), math.sin(h)
        for _, x0, x1, (y0, y1), z0, z1 in parts:
            vs = []
            for x, y, z in [(x, y, z) for z in (z0, z1) for y in (y0, y1) for x in (x0, x1)]:
                p = (m["at"][0] + x * cu - y * su, m["at"][1] + x * su + y * cu, z)
                vs.append(bm.verts.new(frame.b(p)))
            for f in ((0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)):
                bm.faces.new([vs[k] for k in f])
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        me = bpy.data.meshes.new(name)
        bm.to_mesh(me)
        o = bpy.data.objects.new(name, me)
        bpy.context.scene.collection.objects.link(o)
        mat = bpy.data.materials.new(name + "Mat")
        mat.diffuse_color = colour
        me.materials.append(mat)
    return o


def sign_letters(a0):
    """Each sign's text (dims["signs"]) as review-only letters on its board, groomer amber, filling 85 % of its width."""
    made = []
    for name, sg in a0.dims.get("signs", {}).items():
        oname = f"SignText_{name}"
        o = bpy.data.objects.get(oname)
        if o is None:
            cu = bpy.data.curves.new(oname, "FONT")
            cu.body = sg["text"]
            cu.align_x, cu.align_y = "CENTER", "CENTER"
            cu.size = sg["size"][1] / 1000 * 0.55
            cu.extrude = 0.004
            o = bpy.data.objects.new(oname, cu)
            bpy.context.scene.collection.objects.link(o)
            mat = bpy.data.materials.new(oname + "Mat")
            mat.diffuse_color = (0.89, 0.64, 0.20, 1)
            cu.materials.append(mat)
            n = (Vector(frame.b(tuple(sg["facing"]))) - Vector(frame.b((0.0, 0.0, 0.0)))).normalized()
            z = Vector((0.0, 0.0, 1.0))
            rot = Matrix((z.cross(n), z, n)).transposed().to_4x4()
            o.matrix_world = Matrix.Translation(Vector(frame.b(a0.sockets[name])) + n * 0.01) @ rot
            bpy.context.view_layer.update()
            wide = max((Vector(c).x for c in o.bound_box)) - min((Vector(c).x for c in o.bound_box))
            k = min(1.0, sg["size"][0] / 1000 * 0.85 / max(wide, 1e-6))
            cu.size *= k
        made.append(o)
    return made


def lod_lineup(aid, built, out_dir):
    sc = _setup()
    sc.display.shading.show_shadows = True
    sc.render.film_transparent = False
    n = len(built["assets"])
    lo, hi = lrr.bounds_lift(lrr.lod_objects(built, 0))
    span = (hi.x - lo.x) + 4.0
    placed, objs = [], []
    for lod in range(n):
        group = [o for o in built["objects"] if o.type == "MESH" and o.name.endswith(f"_LOD{lod}")]
        for o in group:
            placed.append((o, o.matrix_world.copy()))
            o.matrix_world = Matrix.Translation(frame.b((span * lod, 0, 0))) @ o.matrix_world
        objs += group
    g = lrr.ground(400, -0.002)
    g.scale = (6.0, 6.0, 1.0)   # the clay views made it 90 m; the lineup is wider
    lrr.show_only(objs + [g])
    lrr.use_palette_uv(objs)
    blo, bhi = lrr.bounds_lift(objs)
    blo.z = 0.0
    centre = (blo + bhi) / 2
    cam = lrr.camera("LodCam")
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = (bhi.x - blo.x) * 1.06
    cam.data.clip_end = 1000
    sc.render.resolution_x, sc.render.resolution_y = 2400, 700
    # the camera looks across the row (from +v, a little along u) so the LODs stand side by side
    d = Vector((0.25, 1.0, 0.42)).normalized()
    cam.location = frame.b(tuple(centre + d * 150))
    cam.rotation_euler = (frame.b(tuple(centre)) - cam.location).to_track_quat("-Z", "Y").to_euler()
    lrr.render(os.path.join(out_dir, f"{aid}_lods.png"))
    for o, mw in placed:
        o.matrix_world = mw
    lrr.restore_uv(objs)


_LIFT_SETUP = lrr.scene_setup


def _setup():
    """Workbench as the game draws the buildings: back faces culled (a hole shows as one)."""
    sc = _LIFT_SETUP()
    sc.display.shading.show_backface_culling = True
    return sc


def render_all(built_all, out_dir, mats):
    os.makedirs(out_dir, exist_ok=True)
    # Workbench reads the palette's alpha (packed metallic and smoothness) as transparency: an opaque copy
    pal = next(n.image for n in mats[0].node_tree.nodes if n.type == "TEX_IMAGE" and n.name == "PaletteTex")
    materials.use_palette(mats, materials.opaque_copy(pal))
    for key, built in built_all.items():
        aid = built["assets"][0].id
        _setup()
        ortho_views(aid, built, out_dir)
        clay_views(aid, built, out_dir)
        lod_lineup(aid, built, out_dir)
