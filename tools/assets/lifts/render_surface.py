"""Review renders for the surface lifts (loaded by build_lifts.py --render DIR --shots surface_gate1,surface_photos).

  surface_gate1   Workbench orthographic views of every Monta T-bar asset (side, end, plan; <name>_meta.json beside
                  each, as render_review's gate1), and clay views of a short line: the drive at the bottom, two
                  leaning towers and the return at the top, the rope round both wheels, T-bars every carrier spacing
                  (ridden up the loaded rope by 1.8 m figures, retracted coming down and round both wheels), the
                  operator hut; plus the three heads side by side.
  surface_photos  the same line in Cycles on snow: hero, each terminal, a tower head, the carriers close up, and a short
                  J-bar line beside it (one carrier kind per line, D7).

The scene copies the built LOD0 objects and hides everything else; render_review's helpers do the cameras, materials
and rope curves.
"""
import math
import os

import bpy
from mathutils import Matrix, Vector

import render_review as rr
from liftkit import frame, materials

T = "monta_tbar_"
VIEWS = {   # (view, image right, image up), as render_review.ORTHO
    "terminal": [("side", "+u", "+w"), ("end", "+v", "+w"), ("plan", "+u", "-v")],
    "tower": [("side", "+u", "+w"), ("end", "+v", "+w")],
    "carrier": [("front", "+v", "+w"), ("side", "+u", "+w")],
}
U_TOP = 90.0       # the return's u
TOWERS = ((30.0, "tower_s6", 5.4), (60.0, "tower_s4", 5.3))


def _yaw(deg):
    """A turn about w in the lift frame as a Blender matrix (the frame is a reflection, so the angle flips)."""
    return Matrix.Rotation(-math.radians(deg), 4, "Z")


def _tilt(deg):
    """A turn about the lift frame's v axis (positive leans +w toward +u), as a Blender matrix (+u is Blender -y)."""
    return Matrix.Rotation(math.radians(deg), 4, "X")


def _lean(rad):
    """A turn about the lift frame's u axis tipping +w toward +v (v is Blender -x)."""
    return Matrix.Rotation(-rad, 4, "Y")


def place(built, at, yaw=0.0, extra=None, poses=None):
    """LOD0 copies of one built asset at lift point `at`, turned `yaw` degrees about w; poses: {part: matrix} turns a
    part about its pivot (a part object's origin is its pivot)."""
    m = Matrix.Translation(frame.b(at)) @ _yaw(yaw)
    if extra is not None:
        m = m @ extra
    made = []
    bpy.context.view_layer.update()
    for src in rr.lod_objects(built, 0):
        o = src.copy()
        o.parent = None
        mw = m @ src.matrix_world
        for part, rot in (poses or {}).items():
            if src.name.endswith(f"_{part}_LOD0"):
                mw = mw @ rot
        o.matrix_world = mw
        bpy.context.scene.collection.objects.link(o)
        made.append(o)
    return made


def _socket(built, name):
    return Vector(built["assets"][0].sockets[name])


def _world(at, yaw, p):
    """A lift point p of an asset placed at `at` turned `yaw` degrees, in the scene's lift frame."""
    a = math.radians(yaw)
    return Vector((at[0] + p[0] * math.cos(a) - p[1] * math.sin(a), at[1] + p[0] * math.sin(a) + p[1] * math.cos(a), at[2] + p[2]))


def tower(built, head, u, rope_target):
    """A leaning tower whose head stands on the line centre at u: the base off to the return side (-v), mast sections
    up the leaned axis, the head. Returns (objects, rope height)."""
    base, mast, hd = built[T + "base"], built[T + "mast"], built[head]
    lean = math.radians(base["assets"][0].dims["leanDeg"])
    sec = _socket(mast, "top").z
    foot = _socket(base, "mast_foot")
    axis = Vector((0.0, math.sin(lean), math.cos(lean))) * sec
    rope_rel = _socket(hd, "rope_right").z
    n = max(1, round((rope_target - rope_rel - foot.z) / axis.z))
    top = foot + axis * n
    at = Vector((u, -top.y, 0.0))      # the pier, so the head lands on v = 0
    objs = place(base, tuple(at))
    for k in range(n):
        objs += place(mast, tuple(at + foot + axis * k), 0.0, _lean(lean))
    objs += place(hd, tuple(at + top))
    return objs, at.z + top.z + rope_rel


def carrier(built, bar, at, yaw, retracted=True, seat=None):
    """A recoil box with its bar: retracted (the bar hanging under the box), or ridden from the seat point `seat`
    (scene lift frame), the box swung toward the bar and the bar lined up with its cord. Returns (objects, eye)."""
    box = built[T + "springbox"]
    cord = _socket(box, "cord")
    pin = Vector(box["assets"][0].pivots["box"]["pos"])
    gap = built[T + "return"]["spec"]["carrier"]["cordGap"] / 1000
    if retracted:
        objs = place(box, at, yaw)
        top = _world(at, yaw, cord)
        eye = top - Vector((0.0, 0.0, gap))
        objs += place(bar, tuple(eye), yaw)
        return objs, None
    # ridden: travel +u (yaw 0); the cord runs from the box's outlet back down to the eye on the stem's line
    seat_dz = _socket(bar, "seat_1").z
    p0 = _world(at, yaw, pin)
    sw = math.atan2(p0.x - seat.x, p0.z - seat.z)          # the box's swing and the bar's lean, nearly the same line
    objs = place(box, at, yaw, poses={"box": _tilt(math.degrees(sw))})
    rel = cord - pin                                         # the outlet below the pin, swung with the box
    top = Vector((p0.x - math.sin(sw) * (-rel.z), p0.y + rel.y, p0.z - math.cos(sw) * (-rel.z)))
    lean = math.atan2(top.x - seat.x, top.z - seat.z)
    eye = seat + Vector((math.sin(lean), 0.0, math.cos(lean))) * -seat_dz
    objs += place(bar, tuple(eye), yaw, _tilt(math.degrees(lean)))
    objs.append(rr._rope_curve(f"SurfaceCord{len(bpy.data.curves)}", [tuple(top), tuple(eye)], 8))
    return objs, eye


def line_scene(built, kind="bar_t"):
    """The short line: the drive (bottom) at u 0, towers at 30 m (s6) and 60 m (s4), the return (top) at U_TOP facing
    back; the rope round both wheels; carriers every spacing and round both wheels; the hut. Returns (objects,
    figures)."""
    spec = built[T + "return"]["spec"]
    c = spec["common"]
    hg = c["lineGauge"] / 2000
    gap = c["carrierSpacing"] / 1000
    dv, rt = built[T + "drive"], built[T + "return"]
    r0, r1 = dv["assets"][0].dims["rope"] / 1000, rt["assets"][0].dims["rope"] / 1000
    out0 = _socket(dv, "rope_right_out").x
    out1 = _socket(rt, "rope_left_out").x
    objs = place(dv, (0.0, 0.0, 0.0))
    objs += place(rt, (U_TOP, 0.0, 0.0), 180.0)
    objs += place(built[T + "hut"], (2.0, 4.6, 0.0), 90.0)
    stations = [(0.0, r0), (out0, r0)]
    for u, head, target in TOWERS:
        o, rw = tower(built, T + head, u, target)
        objs += o
        stations.append((u, rw))
    stations += [(U_TOP - out1, r1), (U_TOP, r1)]

    def rope_w(u):
        for (u0, w0), (u1, w1) in zip(stations, stations[1:]):
            if u0 <= u <= u1:
                return w0 + (w1 - w0) * (u - u0) / (u1 - u0)
        return r0 if u < U_TOP / 2 else r1

    # the rope: up the right side, round the return, down the left, round the drive
    pts = [(u, hg, w) for u, w in stations]
    pts += [(U_TOP + hg * math.sin(math.pi * k / 12), hg * math.cos(math.pi * k / 12), r1) for k in range(1, 12)]
    pts += [(u, -hg, w) for u, w in reversed(stations)]
    pts += [(-hg * math.sin(math.pi * k / 12), -hg * math.cos(math.pi * k / 12), r0) for k in range(1, 13)]
    objs.append(rr._rope_curve("SurfaceRope", pts, c["ropeDiameter"]))

    bar = built[T + kind]
    figures = []
    load = _socket(dv, "carrier_load").x
    unload = U_TOP - _socket(rt, "carrier_unload").x
    k, u = 0, load
    while u < unload:
        w = rope_w(u)
        # uphill: ridden, travel +u, outboard +v; the rider 0.7 m over the snow, trailing the box
        at = (u, hg, w)
        seat = Vector((u - 2.3, hg + _socket(built[T + "springbox"], "cord").y, 0.70))
        o, _ = carrier(built, bar, at, 0.0, retracted=False, seat=seat)
        objs += o
        fig = rr.figure((0.0, 0.0, 0.0)).copy()
        sy = _socket(bar, "seat_1").y
        fig.location = frame.b((seat.x - 0.3, seat.y + sy, 0.0)) + Vector((0, 0, (rr.FIGURE_H - 0.3) / 2))
        bpy.context.scene.collection.objects.link(fig)
        figures.append(fig)
        u += gap
        k += 1
    u = U_TOP - 8.0
    while u > 6.0:   # downhill: retracted, travel -u, outboard -v
        o, _ = carrier(built, bar, (u, -hg, rope_w(u)), 180.0)
        objs += o
        u -= gap
    R = c["bullwheel"]["pitch"] / 2000
    for phi in (150.0, 210.0, 270.0):      # round the drive's wheel, -v round the back to +v
        p = math.radians(phi)
        o, _ = carrier(built, bar, (R * math.cos(p), R * math.sin(p), r0), phi - 90.0)
        objs += o
    for phi in (45.0, 0.0, -45.0):          # round the return's, +v round the back to -v
        p = math.radians(phi)
        o, _ = carrier(built, bar, (U_TOP + R * math.cos(p), R * math.sin(p), r1), phi - 90.0)
        objs += o
    return objs, figures


def jbar_line(built, v0=16.0):
    """A rope stub beside the line with J bars: retracted, and one ridden, for the J-bar's photo (one kind per line)."""
    c = built[T + "return"]["spec"]["common"]
    objs = [rr._rope_curve("JRope", [(0.0, v0, 5.0), (40.0, v0, 5.0)], c["ropeDiameter"])]
    figs = []
    bar = built[T + "bar_j"]
    for u in (8.0, 23.0):
        o, _ = carrier(built, bar, (u, v0, 5.0), 0.0)
        objs += o
    seat = Vector((15.5 - 2.3, v0 + _socket(built[T + "springbox"], "cord").y, 0.70))
    o, _ = carrier(built, bar, (15.5, v0, 5.0), 0.0, retracted=False, seat=seat)
    objs += o
    fig = rr.figure((0.0, 0.0, 0.0)).copy()
    fig.location = frame.b((seat.x - 0.3, seat.y + _socket(bar, "seat_1").y, 0.0)) + Vector((0, 0, (rr.FIGURE_H - 0.3) / 2))
    bpy.context.scene.collection.objects.link(fig)
    figs.append(fig)
    return objs, figs


def head_lineup(built):
    """The three heads side by side on their leaning towers, rope stubs through them."""
    objs = []
    c = built[T + "return"]["spec"]["common"]
    hg = c["lineGauge"] / 2000
    for i, head in enumerate(("tower_s4", "tower_s6", "tower_d4")):
        o, rw = tower(built, T + head, 6.0 * i, 5.5)
        objs += o
        for s in (-1, 1):
            objs.append(rr._rope_curve(f"HeadRope{i}{s}", [(6.0 * i - 2.0, s * hg, rw), (6.0 * i + 2.0, s * hg, rw)], c["ropeDiameter"]))
    return objs


def gate1(built, out_dir):
    for key, b in built.items():
        if not key.startswith(T):
            continue
        name = key[len(T):]
        views = VIEWS["terminal"] if name in ("drive", "return", "hut") else \
            VIEWS["carrier"] if name in ("springbox", "bar_t", "bar_j") else VIEWS["tower"]
        rr.ORTHO[key] = views
        rr.scene_setup()
        rr.ortho_views(key, b, out_dir)
    # clay: the line, from the side and three-quarter; the heads
    sc = rr.scene_setup()
    sc.display.shading.show_shadows = True
    sc.render.film_transparent = False
    objs, figs = line_scene(built)
    gnd = rr.ground(size=300.0)
    rr.show_only(objs + figs + [gnd])
    rr.use_palette_uv(objs)
    cam = rr.camera()
    cam.data.type = "PERSP"
    cam.data.lens = 35
    cam.data.clip_start, cam.data.clip_end = 0.1, 600
    sc.render.resolution_x, sc.render.resolution_y = 1800, 900

    def look(name, eye, target):
        cam.location = frame.b(eye)
        cam.rotation_euler = (frame.b(target) - cam.location).to_track_quat("-Z", "Y").to_euler()
        rr.render(os.path.join(out_dir, f"tbar_{name}.png"))
    look("line_clay", (-16.0, 22.0, 9.0), (40.0, 0.0, 3.0))
    look("line_clay_up", (-14.0, 2.0, 2.2), (60.0, 0.0, 4.5))
    look("line_clay_side", (45.0, 70.0, 4.0), (45.0, 0.0, 3.5))
    look("drive_clay", (10.0, 9.0, 5.0), (1.5, 0.0, 3.0))
    look("drive_clay_back", (-9.0, -8.0, 5.5), (1.0, 0.0, 3.0))
    look("return_clay", (U_TOP - 10.0, -9.0, 6.0), (U_TOP - 1.5, 0.0, 4.0))
    look("return_clay_back", (U_TOP + 9.0, 8.0, 6.5), (U_TOP - 1.0, 0.0, 4.0))
    look("carriers_clay", (14.0, 7.0, 3.0), (20.0, 1.6, 2.6))
    look("tower_clay", (34.0, 8.0, 3.0), (30.0, 0.0, 4.0))
    for o in objs + figs:
        bpy.data.objects.remove(o)
    heads = head_lineup(built)
    rr.show_only(heads + [gnd])
    rr.use_palette_uv(heads)
    look("heads_clay", (-5.0, -9.0, 8.5), (6.0, 0.0, 5.6))
    look("heads_clay_front", (-12.0, 0.5, 5.8), (6.0, 0.0, 5.0))
    for o in heads:
        bpy.data.objects.remove(o)


def photos(built, out_dir, mats):
    sc, sun = rr._photo_scene()
    objs, figs = line_scene(built)
    jo, jf = jbar_line(built)
    objs += jo
    figs += jf
    for f in figs:
        f.data.materials.clear()
        f.data.materials.append(rr._photo_material("PhotoFigureMat", (0.12, 0.28, 0.62), 0.6))
    snow = rr._snow_plane(0.0)
    rr.show_only(objs + figs + [snow, sun])
    rr.restore_uv(objs)
    shoot = rr._shooter("tbar", out_dir)
    shoot("hero", (45.0, 0.0, 3.0), 205, 10, 40.0, lens=35)
    shoot("line_up", (30.0, 0.0, 4.0), 182, 4, 45.0, lens=40)
    shoot("drive", (1.5, 0.0, 3.0), 35, 16, 12.0)
    shoot("drive_back", (0.0, 0.0, 3.0), 210, 20, 11.0)
    shoot("return", (U_TOP - 1.5, 0.0, 4.0), 215, 16, 12.0)
    shoot("return_back", (U_TOP, 0.0, 4.0), 20, 20, 11.0)
    shoot("tower", (30.0, 0.0, 5.6), 130, 10, 6.0)
    shoot("carriers", (21.8, 0.2, 3.6), 110, 5, 8.0, lens=40)     # ridden (+v) and retracted (-v) side by side
    shoot("springbox", (36.8, 1.75, 4.75), 55, 4, 3.4, lens=50)
    shoot("rider", (21.0, 1.7, 1.2), 160, 6, 4.5, lens=40)
    shoot("jbar", (15.5, 16.0, 2.5), 140, 8, 7.0, lens=40)
    rr._photo_end(sc, [snow] + figs)
    for o in objs + figs:
        bpy.data.objects.remove(o)


def render_all(built, out_dir, shots, mats):
    out_dir = os.path.abspath(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    shots = (shots or "").split(",")
    if not any(k.startswith(T) for k in built):
        return
    packed = bpy.data.images["lift_palette"]
    if "surface_gate1" in shots:
        materials.use_palette(mats, materials.opaque_copy(packed, os.environ.get("LIFT_LIVERY")))
        gate1(built, out_dir)
        materials.use_palette(mats, packed)
    if "surface_photos" in shots:
        photos(built, out_dir, mats)
    print(f"Surface lift renders in {out_dir}", flush=True)
