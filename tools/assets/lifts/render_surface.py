"""Review renders for the surface lifts (loaded by build_lifts.py --render DIR --shots surface_gate1,surface_photos).

  surface_gate1   Workbench orthographic views of every Monta T-bar asset (side, end, plan; <name>_meta.json beside
                  each, as render_review's gate1), and clay views of a short line: the return, two towers and the
                  drive, the rope round both wheels, recoil boxes every carrier spacing with bars retracted on the
                  downhill rope and ridden on the uphill one (1.8 m figures), plus the four heads side by side.
  surface_photos  the same line in Cycles on snow: hero, each terminal, a tower head, the carriers close up.

The scene copies the built LOD0 objects (render_review.hang_chairs) and hides everything else; render_review's
helpers do the cameras, materials and rope curves.
"""
import math
import os

import bpy
from mathutils import Matrix, Vector

import render_review as rr
from liftkit import frame, materials

T = "monta_tbar_"
VIEWS = {   # (view, image right, image up), as render_review.ORTHO
    "drive": [("side", "-u", "+w"), ("end", "+v", "+w"), ("plan", "-u", "+v")],
    "return": [("side", "+u", "+w"), ("end", "+v", "+w"), ("plan", "+u", "-v")],
    "tower": [("side", "+u", "+w"), ("end", "+v", "+w")],
    "carrier": [("front", "+v", "+w"), ("side", "+u", "+w")],
}
SECTIONS = 1.0     # a mast section is 1 m


def _yaw(deg):
    """A turn about w in the lift frame as a Blender matrix (the frame is a reflection, so the angle flips)."""
    return Matrix.Rotation(-math.radians(deg), 4, "Z")


def _tilt(deg):
    """A turn about the lift frame's v axis (positive leans +w toward +u), as a Blender matrix (+u is Blender -y)."""
    return Matrix.Rotation(math.radians(deg), 4, "X")


def place(built, at, yaw=0.0, extra=None):
    """LOD0 copies of one built asset at lift point `at`, turned `yaw` degrees about w."""
    m = Matrix.Translation(frame.b(at)) @ _yaw(yaw)
    if extra is not None:
        m = m @ extra
    made = []
    bpy.context.view_layer.update()
    for src in rr.lod_objects(built, 0):
        o = src.copy()
        o.parent = None
        o.matrix_world = m @ src.matrix_world
        bpy.context.scene.collection.objects.link(o)
        made.append(o)
    return made


def _socket(built, name):
    return Vector(built["assets"][0].sockets[name])


def _world(at, yaw, p):
    """A lift point p of an asset placed at `at` turned `yaw` degrees, in the scene's lift frame."""
    a = math.radians(yaw)
    return Vector((at[0] + p[0] * math.cos(a) - p[1] * math.sin(a), at[1] + p[0] * math.sin(a) + p[1] * math.cos(a), at[2] + p[2]))


def tower(built, head, at, rope_target):
    """A tower at `at`: base, mast sections, head, the mast stacked so the rope passes near rope_target. Returns
    (objects, rope height)."""
    base, mast, hd = built[T + "base"], built[T + "mast"], built[head]
    foot = _socket(base, "mast_foot").z
    rope_rel = _socket(hd, "rope_right").z
    n = max(1, round((rope_target - rope_rel - foot) / SECTIONS))
    objs = place(base, at)
    for k in range(n):
        objs += place(mast, (at[0], at[1], at[2] + foot + k * SECTIONS))
    top = at[2] + foot + n * SECTIONS
    objs += place(hd, (at[0], at[1], top))
    return objs, top + rope_rel


def line_scene(built):
    """The short line: return at u 0, towers at 30 m (s6) and 60 m (s4), the drive at 90 m facing back; the rope
    round both wheels; carriers every spacing. Returns (objects, figures, the drive's u)."""
    spec = built[T + "return"]["spec"]
    c = spec["common"]
    hg, rope0 = c["lineGauge"] / 2000, c["ropeElevation"] / 1000
    gap = c["carrierSpacing"] / 1000
    u_drive = 90.0
    objs = place(built[T + "return"], (0.0, 0.0, 0.0))
    objs += place(built[T + "drive"], (u_drive, 0.0, 0.0), 180.0)
    stations = [(0.0, rope0)]
    for u, head in ((30.0, T + "tower_s6"), (60.0, T + "tower_s4")):
        o, rw = tower(built, head, (u, 0.0, 0.0), 5.9)
        objs += o
        stations.append((u, rw))
    stations.append((u_drive, rope0))

    def rope_w(u):
        for (u0, w0), (u1, w1) in zip(stations, stations[1:]):
            if u0 <= u <= u1:
                return w0 + (w1 - w0) * (u - u0) / (u1 - u0)
        return rope0

    # the rope: up the right side, round the drive, down the left, round the return
    pts = [(u, hg, w) for u, w in stations]
    pts += [(u_drive + hg * math.sin(math.pi * k / 12), hg * math.cos(math.pi * k / 12), rope0) for k in range(1, 12)]
    pts += [(u, -hg, w) for u, w in reversed(stations)]
    pts += [(-hg * math.sin(math.pi * k / 12), -hg * math.cos(math.pi * k / 12), rope0) for k in range(1, 13)]
    objs.append(rr._rope_curve("SurfaceRope", pts, c["ropeDiameter"]))

    # carriers: boxes every spacing on both ropes; ridden bars up the right rope from the load point, retracted ones
    # coming down the left
    box, bar_t, bar_j = built[T + "springbox"], built[T + "bar_t"], built[T + "bar_j"]
    cord = _socket(box, "cord")
    seat_dz = _socket(bar_t, "seat_1").z
    figures = []
    k = 0
    u = 4.0
    while u < u_drive - 6.0:
        w = rope_w(u)
        # uphill: travel +u, outboard +v
        objs += place(box, (u, hg, w))
        bar = bar_j if k % 3 == 2 else bar_t
        top = Vector((u, hg + cord.y, w + cord.z))
        seat = Vector((u - 2.4, hg + cord.y, 0.70))             # the rider sits 0.7 m over the snow, trailing the box
        lean = math.atan2(top.x - seat.x, top.z - seat.z)        # the bar lines up with its cord
        eye = seat + Vector((math.sin(lean), 0.0, math.cos(lean))) * -seat_dz
        objs += place(bar, tuple(eye), 0.0, _tilt(math.degrees(lean)))
        objs.append(rr._rope_curve(f"SurfaceCord{k}", [tuple(top), tuple(eye)], 8))
        fig = rr.figure((0.0, 0.0, 0.0)).copy()
        fig.location = frame.b((seat.x - 0.3, seat.y + _socket(bar, "seat_1").y, 0.0)) + Vector((0, 0, (rr.FIGURE_H - 0.3) / 2))
        bpy.context.scene.collection.objects.link(fig)
        figures.append(fig)
        # downhill: travel -u, outboard -v; the bar retracted under its box
        ud = u + gap / 2
        if ud < u_drive - 3.0:
            wd = rope_w(ud)
            objs += place(box, (ud, -hg, wd), 180.0)
            objs += place(bar_t if k % 3 != 2 else bar_j, tuple(_world((ud, -hg, wd), 180.0, cord)), 180.0)
        u += gap
        k += 1
    return objs, figures, u_drive


def head_lineup(built):
    """The four heads side by side on short masts, rope stubs through them."""
    objs = []
    c = built[T + "return"]["spec"]["common"]
    hg = c["lineGauge"] / 2000
    for i, head in enumerate(("tower_s4", "tower_s6", "tower_d4", "tower_c4")):
        at = (0.0, 6.0 * i, 0.0)
        o, rw = tower(built, T + head, at, 5.5)
        objs += o
        for s in (-1, 1):
            objs.append(rr._rope_curve(f"HeadRope{i}{s}", [(-2.0, 6.0 * i + s * hg, rw), (2.0, 6.0 * i + s * hg, rw)], c["ropeDiameter"]))
    return objs


def gate1(built, out_dir):
    for key, b in built.items():
        if not key.startswith(T):
            continue
        name = key[len(T):]
        views = VIEWS["drive"] if name == "drive" else VIEWS["return"] if name == "return" else \
            VIEWS["carrier"] if name in ("springbox", "bar_t", "bar_j") else VIEWS["tower"]
        rr.ORTHO[key] = views
        rr.scene_setup()
        rr.ortho_views(key, b, out_dir)
    # clay: the line, from the side and three-quarter; the heads
    sc = rr.scene_setup()
    sc.display.shading.show_shadows = True
    sc.render.film_transparent = False
    objs, figs, u_drive = line_scene(built)
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
    look("line_clay", (-14.0, 24.0, 9.0), (40.0, 0.0, 3.0))
    look("line_clay_side", (45.0, 70.0, 4.0), (45.0, 0.0, 3.5))
    look("return_clay", (9.0, 9.0, 5.0), (0.0, 0.0, 2.8))
    look("drive_clay", (u_drive - 9.0, -9.0, 5.5), (u_drive, 0.0, 3.0))
    look("carriers_clay", (14.0, 6.0, 3.0), (20.0, 1.6, 2.6))
    for o in objs + figs:
        bpy.data.objects.remove(o)
    heads = head_lineup(built)
    rr.show_only(heads + [gnd])
    rr.use_palette_uv(heads)
    look("heads_clay", (9.0, -5.0, 8.5), (0.0, 9.0, 5.6))
    look("heads_clay_front", (12.0, 9.0, 5.8), (0.0, 9.0, 5.6))
    for o in heads:
        bpy.data.objects.remove(o)


def photos(built, out_dir, mats):
    sc, sun = rr._photo_scene()
    objs, figs, u_drive = line_scene(built)
    for f in figs:
        f.data.materials.clear()
        f.data.materials.append(rr._photo_material("PhotoFigureMat", (0.12, 0.28, 0.62), 0.6))
    snow = rr._snow_plane(0.0)
    rr.show_only(objs + figs + [snow, sun])
    rr.restore_uv(objs)
    shoot = rr._shooter("tbar", out_dir)
    shoot("hero", (35.0, 0.0, 3.0), 205, 10, 32.0, lens=35)
    shoot("line_up", (60.0, 0.0, 4.0), 182, 4, 70.0, lens=50)
    shoot("return", (0.0, 0.0, 2.8), 145, 16, 11.0)
    shoot("return_back", (-1.5, 0.0, 2.8), 215, 18, 10.0)
    shoot("drive", (u_drive, 0.0, 3.0), -40, 16, 11.0)
    shoot("drive_back", (u_drive + 1.0, 0.0, 3.0), 30, 20, 10.0)
    shoot("tower", (30.0, 0.0, 5.8), 120, 10, 7.0)
    shoot("carriers", (19.0, 1.7, 2.8), 60, 8, 6.5)
    shoot("springbox", (19.0, 1.6, 5.4), 70, -12, 2.4, lens=50)
    shoot("rider", (17.4, 1.7, 1.2), 160, 6, 4.5, lens=40)
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
