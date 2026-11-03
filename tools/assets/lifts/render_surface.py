"""Review renders for the surface lifts (loaded by build_lifts.py --render DIR --shots surface_gate1,surface_photos).

  surface_gate1   Workbench orthographic views of every Monta T-bar asset (side, end, plan; <name>_meta.json beside
                  each, as render_review's gate1), the terminals with the rope at rest through them; the return's
                  breakover rows posed for other rope angles (tbar_return_pose_NN.png, tbar_return_pose.json with
                  the band of exits they hold); and clay views of a short line up a hill (level loading and unloading
                  areas, scene_grade): the drive at the bottom, two leaning towers and the return at the top, the
                  rope round both wheels, T-bars every carrier spacing (ridden up the loaded rope by 1.8 m figures,
                  retracted coming down and round both wheels), the operator hut; plus the three heads side by side.
  surface_photos  the same line in Cycles on snow: hero, each terminal, a tower head, the carriers close up, and a short
                  J-bar line beside it (one carrier kind per line, D7).
  surface_audit   no images: the holes the game would show where it culls back faces (tbar_audit.json, Gate 2).
  surface_gate2   Gate 2, as the game draws the models (back faces culled): hero, each terminal, a tower, the J bars,
                  the line after snowfall; the game's camera far and mid with each model at the LOD the game picks at
                  its distance (Low quality, lodBias 1); each model's LODs side by side (tbar_lods_<row>.png).

The scene copies the built objects (LOD0, or the game's LOD for Gate 2's game views) and hides everything else;
render_review's helpers do the cameras, materials and rope curves.
"""
import json
import math
import os

import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

import render_review as rr
import surface_fit
from liftkit import frame, materials
from liftkit import pose as lk_pose

T = "monta_tbar_"
VIEWS = {   # (view, image right, image up), as render_review.ORTHO
    "terminal": [("side", "+u", "+w"), ("end", "+v", "+w"), ("plan", "+u", "-v")],
    "tower": [("side", "+u", "+w"), ("end", "+v", "+w")],
    "carrier": [("front", "+v", "+w"), ("side", "+u", "+w")],
}
U_TOP = 90.0       # the return's u
TOWERS = ((30.0, "tower_s6", 5.4), (60.0, "tower_s4", 5.3))
FLAT_BOTTOM = 12.0  # the level loading area, from the drive's axle (v3.1: the line climbs a hill between)
FLAT_TOP = 8.0      # the level unloading area below the return's axle
POSE_EXITS = (-6.0, -12.0, -18.0)   # the return rows' posed exits (degrees along its +u; rest: dims ropeExitDeg)
KINDS = {"drive": "surface_terminal", "return": "surface_terminal", "hut": "surface_hut", "tower_s4": "surface_tower_head",
         "tower_s6": "surface_tower_head", "tower_d4": "surface_tower_head", "mast": "surface_mast", "base": "surface_base",
         "springbox": "surface_carrier", "bar_t": "surface_carrier", "bar_j": "surface_carrier"}   # budgets.json kinds
GRADED = ("drive", "return", "hut", "base")   # standing on grade: nothing under it is ever seen
AUDIT_DIRS = [(az, el) for el in (60, 30, 10, -15) for az in range(0, 360, 30)] + [(0, 90)]   # views (deg), -15 looks up
GAME_FOV = 60.0     # the game's default vertical field of view, also LiftImport's ReferenceFov
_LOD = None         # Gate 2's game views: f(asset id, lift point) -> the LOD to place there (None: LOD0)


def _yaw(deg):
    """A turn about w in the lift frame as a Blender matrix (the frame is a reflection, so the angle flips)."""
    return Matrix.Rotation(-math.radians(deg), 4, "Z")


def _tilt(deg):
    """A turn about the lift frame's v axis (positive leans +w toward +u), as a Blender matrix (+u is Blender -y)."""
    return Matrix.Rotation(math.radians(deg), 4, "X")


def _lean(rad):
    """A turn about the lift frame's u axis tipping +w toward +v (v is Blender -x)."""
    return Matrix.Rotation(-rad, 4, "Y")


def _aid(built):
    """A built asset's id (its body object is <id>_LOD0)."""
    return min((o.name for o in rr.lod_objects(built, 0)), key=len)[:-len("_LOD0")]


def place(built, at, yaw=0.0, extra=None, poses=None):
    """Copies of one built asset (LOD0, or the LOD _LOD picks) at lift point `at`, turned `yaw` degrees about w; poses:
    {part: matrix} turns a part about its pivot (a part object's origin is its pivot)."""
    lod = _LOD(_aid(built), at) if _LOD else 0
    m = Matrix.Translation(frame.b(at)) @ _yaw(yaw)
    if extra is not None:
        m = m @ extra
    made = []
    bpy.context.view_layer.update()
    for src in rr.lod_objects(built, lod):
        o = src.copy()
        o.parent = None
        mw = m @ src.matrix_world
        for part, rot in (poses or {}).items():
            if src.name.endswith(f"_{part}_LOD{lod}"):
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


def _tower_geometry(built, head, rope_target):
    """A leaning tower aimed at rope_target over its grade: (lean, mast foot, one section along the axis, sections,
    head top from the pier, rope height over grade)."""
    base, mast, hd = built[T + "base"], built[T + "mast"], built[head]
    lean = math.radians(base["assets"][0].dims["leanDeg"])
    sec = _socket(mast, "top").z
    foot = _socket(base, "mast_foot")
    axis = Vector((0.0, math.sin(lean), math.cos(lean))) * sec
    rope_rel = _socket(hd, "rope_right").z
    n = max(1, round((rope_target - rope_rel - foot.z) / axis.z))
    top = foot + axis * n
    return lean, foot, axis, n, top, top.z + rope_rel


def tower(built, head, u, rope_target, grade=0.0):
    """A leaning tower whose head stands on the line centre at u, on ground at `grade`: the base off to the return side
    (-v), mast sections up the leaned axis, the head. Returns (objects, rope height)."""
    lean, foot, axis, n, top, above = _tower_geometry(built, head, rope_target)
    at = Vector((u, -top.y, grade))    # the pier, so the head lands on v = 0
    objs = place(built[T + "base"], tuple(at))
    for k in range(n):
        objs += place(built[T + "mast"], tuple(at + foot + axis * k), 0.0, _lean(lean))
    objs += place(built[head], tuple(at + top))
    return objs, grade + above


def scene_grade(built):
    """The line scene's ground (v3.1): level loading and unloading areas (FLAT_BOTTOM from the drive, FLAT_TOP below
    the return) and a straight hill between, steep enough that the rope comes up from the last tower into the return
    at its breakover rows' rest exit angle, as on a real top station. Returns grade(u)."""
    rt = built[T + "return"]["assets"][0]
    out = rt.sockets["rope_left_out"]
    e = math.tan(math.radians(-rt.dims.get("ropeExitDeg", 0.0)))
    u_t, head, target = TOWERS[-1]
    above = _tower_geometry(built, T + head, target)[-1]
    u0, u1 = FLAT_BOTTOM, U_TOP - FLAT_TOP
    g = (e * (U_TOP - out[0] - u_t) + above - out[2]) / (u1 - u_t)   # top + out.z = g (u_t - u0) + above + e (u_o - u_t)

    def grade(u):
        return g * (min(max(u, u0), u1) - u0)
    grade.slope = g
    return grade


def hill(grade, name, mat, half=160.0):
    """The scene's ground as a mesh: level, the hill, level again (grade from scene_grade)."""
    us = [-half, FLAT_BOTTOM, U_TOP - FLAT_TOP, U_TOP + half]
    verts = [tuple(frame.b((u, v, grade(u)))) for u in us for v in (-half, half)]
    faces = [(2 * i, 2 * i + 1, 2 * i + 3, 2 * i + 2) for i in range(len(us) - 1)]
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    o = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(o)
    o.data.materials.append(mat)
    return o


def terminal_rope(b, past=1.0):
    """A rope stub through a terminal at rest for its orthographic views (v3.1): in along -v over or under its rows,
    round the back of the wheel, out along +v, `past` metres beyond the out sockets (surface_fit.rope_profile)."""
    a0 = b["assets"][0]
    c = b["spec"]["common"]
    hg, R = c["lineGauge"] / 2000, c["bullwheel"]["pitch"] / 2000
    rope = a0.sockets["rope_right_bw"][2]
    u_far = max(a0.sockets["rope_left_out"][0], a0.sockets["rope_right_out"][0]) + past
    wl, wr = surface_fit.rope_profile(a0, "left"), surface_fit.rope_profile(a0, "right")
    us = [u_far * k / 80 for k in range(81)]
    pts = [(u, hg, wr(u)) for u in reversed(us)]
    pts += [(R * math.cos(math.radians(th)), R * math.sin(math.radians(th)), rope) for th in range(95, 270, 5)]
    pts += [(u, -hg, wl(u)) for u in us]
    o = rr._rope_curve(f"TerminalRope{len(bpy.data.curves)}", pts, c["ropeDiameter"])
    o.data.materials[0].diffuse_color = (0.05, 0.05, 0.05, 1.0)
    return o


def return_poses(built, out_dir):
    """The return's breakover rows posed as the game poses them (liftkit/pose.py, v3.1): the rope in level from the
    wheel, the first sheave held at its rest height, out at each of POSE_EXITS; a side view per pose with the rope over
    the posed sheaves, and tbar_return_pose.json with the band of exits that keeps every sheave on the rope inside the
    pivots' clash ranges."""
    b = built[T + "return"]
    a0 = b["assets"][0]
    c = b["spec"]["common"]
    rho = c["sheave"]["grooveBottom"] / 2000 + c["ropeDiameter"] / 2000   # axle to the rope's centre
    hg, R = c["lineGauge"] / 2000, c["bullwheel"]["pitch"] / 2000
    rope = a0.sockets["rope_right_bw"][2]
    empties = rr._nest(b)
    objs = rr.lod_objects(b, 0)
    rows = {}
    log = {"restExitDeg": a0.dims["ropeExitDeg"], "band": {}, "poses": {}}
    for side in "lr":
        top = f"eq_{side}"
        row = sorted((k for k in lk_pose.subtree(a0.pivots, top) if k.startswith("sheave_")), key=lambda k: a0.pivots[k]["pos"][0])
        anchor = (row[0], a0.pivots[row[0]]["pos"][2])
        rows[side] = (top, row, anchor)
        log["band"][side] = lk_pose.band(a0.pivots, top, row, 0.0, anchor, outs=range(0, -31, -1), ranges=b.get("ranges"))
    cam = rr.camera()
    for deg in POSE_EXITS:
        s_out = math.radians(deg)
        angles, sides, errs = {}, {}, []
        for side, (top, row, anchor) in rows.items():
            ang, err = lk_pose.solve(a0.pivots, top, [row], 0.0, s_out, anchor=anchor)
            angles.update(ang)
            errs.append(err)
            path = [(float(u), float(w)) for u, w in lk_pose.rope_path(a0.pivots, ang, row, rho, 0.0, s_out, 1.0, far=1.0)]
            sides[side] = [(0.0, path[1][1])] + path[1:]   # level back to the wheel's tangent point
        log["poses"][f"{deg:+.0f}"] = {"anchorErrorMm": round(max(errs) * 1000, 3),
                                       "unityDeg": {k: round(-math.degrees(x), 2) for k, x in sorted(angles.items()) if abs(x) > 1e-6}}
        rr._turn(empties, angles)
        pts = [(u, hg, w) for u, w in reversed(sides["r"])]
        pts += [(R * math.cos(math.radians(th)), R * math.sin(math.radians(th)), rope) for th in range(95, 270, 5)]
        pts += [(u, -hg, w) for u, w in sides["l"]]
        line = rr._rope_curve(f"PoseRope{len(bpy.data.curves)}", pts, c["ropeDiameter"] * 2)
        line.data.materials[0].diffuse_color = (0.05, 0.05, 0.05, 1.0)
        rr.show_only(objs + [line])
        rr.use_palette_uv(objs)
        lo, hi = rr.bounds_lift(objs + [line])
        lo -= Vector((0.3, 0.3, 0.3))
        hi += Vector((0.3, 0.3, 0.3))
        rr.place_ortho(cam, (1.0, 0.0, 0.0), (0.0, 0.0, 1.0), ((lo.x + hi.x) / 2, 0.0, (lo.z + hi.z) / 2), hi.x - lo.x, hi.z - lo.z)
        rr.render(os.path.join(out_dir, f"tbar_return_pose_{abs(round(deg)):02d}.png"))
        rr._turn(empties, {})
        bpy.data.objects.remove(line)
    rr.restore_uv(objs)
    with open(os.path.join(out_dir, "tbar_return_pose.json"), "w") as f:
        json.dump(log, f, indent=2)
    print(f"Return breakover band: {log['band']}", flush=True)


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
    """The short line up the scene's hill (scene_grade): the drive (bottom) at u 0, towers at 30 m (s6) and 60 m (s4),
    the return (top) at U_TOP facing back, the rope over its breakover rows; the rope round both wheels; carriers
    every spacing and round both wheels; the hut. Returns (objects, figures)."""
    spec = built[T + "return"]["spec"]
    c = spec["common"]
    hg = c["lineGauge"] / 2000
    gap = c["carrierSpacing"] / 1000
    grade = scene_grade(built)
    top = grade(U_TOP)
    dv, rt = built[T + "drive"], built[T + "return"]
    r0, r1 = dv["assets"][0].dims["rope"] / 1000, top + rt["assets"][0].dims["rope"] / 1000
    out0 = _socket(dv, "rope_right_out").x
    out1 = _socket(rt, "rope_left_out").x
    w_ret = surface_fit.rope_profile(rt["assets"][0], "left")   # the arriving loaded rope over the return's rows
    objs = place(dv, (0.0, 0.0, 0.0))
    objs += place(rt, (U_TOP, 0.0, top), 180.0)
    objs += place(built[T + "hut"], (2.0, 4.6, grade(2.0)), 90.0)
    stations = [(0.0, r0), (out0, r0)]
    for u, head, target in TOWERS:
        o, rw = tower(built, T + head, u, target, grade(u))
        objs += o
        stations.append((u, rw))
    stations += [(U_TOP - x, top + w_ret(x)) for x in [out1 * k / 24 for k in range(24, 0, -1)]] + [(U_TOP, r1)]

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
        seat = Vector((u - 2.3, hg + _socket(built[T + "springbox"], "cord").y, grade(u - 2.3) + 0.70))
        o, _ = carrier(built, bar, at, 0.0, retracted=False, seat=seat)
        objs += o
        fig = rr.figure((0.0, 0.0, 0.0)).copy()
        sy = _socket(bar, "seat_1").y
        fig.location = frame.b((seat.x - 0.3, seat.y + sy, grade(seat.x - 0.3))) + Vector((0, 0, (rr.FIGURE_H - 0.3) / 2))
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
    """A rope stub beside the line with J bars, 5 m over the hill: retracted, and one ridden, for the J-bar's photo
    (one kind per line)."""
    c = built[T + "return"]["spec"]["common"]
    grade = scene_grade(built)
    objs = [rr._rope_curve("JRope", [(u, v0, 5.0 + grade(u)) for u in (0.0, FLAT_BOTTOM, 40.0)], c["ropeDiameter"])]
    figs = []
    bar = built[T + "bar_j"]
    for u in (8.0, 23.0):
        o, _ = carrier(built, bar, (u, v0, 5.0 + grade(u)), 0.0)
        objs += o
    seat = Vector((15.5 - 2.3, v0 + _socket(built[T + "springbox"], "cord").y, grade(15.5 - 2.3) + 0.70))
    o, _ = carrier(built, bar, (15.5, v0, 5.0 + grade(15.5)), 0.0, retracted=False, seat=seat)
    objs += o
    fig = rr.figure((0.0, 0.0, 0.0)).copy()
    fig.location = frame.b((seat.x - 0.3, seat.y + _socket(bar, "seat_1").y, grade(seat.x - 0.3))) + Vector((0, 0, (rr.FIGURE_H - 0.3) / 2))
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
        extras = [terminal_rope(b)] if name in ("drive", "return") else []   # the rope at rest through the station
        rr.ortho_views(key, b, out_dir, extras)
        for o in extras:
            bpy.data.objects.remove(o)
    if T + "return" in built:   # the return's breakover rows posed for other rope angles (v3.1)
        rr.scene_setup()
        return_poses(built, out_dir)
    # clay: the line up its hill, from the side and three-quarter; the heads on level ground
    sc = rr.scene_setup()
    sc.display.shading.show_shadows = True
    sc.render.film_transparent = False
    objs, figs = line_scene(built)
    gnd = rr.ground(size=300.0)
    grade = scene_grade(built)
    slope = hill(grade, "SurfaceHill", gnd.data.materials[0])
    rr.show_only(objs + figs + [slope])
    rr.use_palette_uv(objs)
    cam = rr.camera()
    cam.data.type = "PERSP"
    cam.data.lens = 35
    cam.data.clip_start, cam.data.clip_end = 0.1, 600
    sc.render.resolution_x, sc.render.resolution_y = 1800, 900

    def look(name, eye, target, g=grade):   # eye and target stand over the hill
        cam.location = frame.b((eye[0], eye[1], eye[2] + g(eye[0])))
        tgt = frame.b((target[0], target[1], target[2] + g(target[0])))
        cam.rotation_euler = (tgt - cam.location).to_track_quat("-Z", "Y").to_euler()
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
    for o in objs + figs + [slope]:
        bpy.data.objects.remove(o)
    heads = head_lineup(built)
    rr.show_only(heads + [gnd])
    rr.use_palette_uv(heads)
    level = lambda u: 0.0
    look("heads_clay", (-5.0, -9.0, 8.5), (6.0, 0.0, 5.6), level)
    look("heads_clay_front", (-12.0, 0.5, 5.8), (6.0, 0.0, 5.0), level)
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
    grade = scene_grade(built)
    snow = hill(grade, "PhotoHill", rr._photo_material("PhotoSnowMat", (0.88, 0.91, 0.95), 0.55))
    rr.show_only(objs + figs + [snow, sun])
    rr.restore_uv(objs)
    _shoot = rr._shooter("tbar", out_dir)

    def shoot(name, target, az, el, dist, lens=50):   # targets stand over the hill
        _shoot(name, (target[0], target[1], target[2] + grade(target[0])), az, el, dist, lens=lens)
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


def holes(objs, graded, lod):
    """Rays on a grid over `objs` from every AUDIT_DIRS view (the step from their size and LOD): where the first surface
    a ray meets faces away from it, the game shows a hole. A graded set gets the snow at grade as an occluder, so nothing
    counts that only shows from under the ground (no ray starts below grade). Returns (step, rays that hit,
    {(part, u, v, w) 0.25 m cell: [hits, a face there (centre, normal, lift frame)]})."""
    verts, polys, owner = [], [], []
    for o in objs:
        if o.type != "MESH":
            continue
        base = len(verts)
        verts += [o.matrix_world @ v.co for v in o.data.vertices]
        for f in o.data.polygons:
            polys.append([base + i for i in f.vertices])
            owner.append(o.name)
    c = sum(verts, Vector()) / len(verts)
    r = max((v - c).length for v in verts)
    own = list(verts)   # the model, without the snow
    if graded:   # the snow, facing up
        g = len(verts)
        verts += [Vector((c.x + sx * (r + 2.0), c.y + sy * (r + 2.0), 0.0)) for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
        polys.append([g, g + 1, g + 2, g + 3])
        owner.append(None)
    tree = BVHTree.FromPolygons(verts, polys)
    step = max(0.01, r / 250.0) * 2 ** lod
    rays, back = 0, {}
    for az, el in AUDIT_DIRS:
        a, e = math.radians(az), math.radians(el)
        d = frame.b((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))   # toward the eye
        e1 = d.orthogonal().normalized()
        e2 = d.cross(e1)
        xs, ys = [(v - c).dot(e1) for v in own], [(v - c).dot(e2) for v in own]
        for i in range(int(min(xs) / step) - 1, int(max(xs) / step) + 2):
            for j in range(int(min(ys) / step) - 1, int(max(ys) / step) + 2):
                o = c + d * (r + 1.0) + e1 * (i * step) + e2 * (j * step)
                if graded and o.z < 0.02:   # no eye under the snow
                    continue
                hit, n, idx, _ = tree.ray_cast(o, -d, 2 * r + 2.0)
                if hit is None or owner[idx] is None:
                    continue
                rays += 1
                if n.dot(d) < -0.05:
                    u, v, w = frame.lift(hit)
                    cell = (owner[idx], round(u * 4) / 4, round(v * 4) / 4, round(w * 4) / 4)
                    if cell not in back:
                        f = polys[idx]
                        fc = sum((verts[k] for k in f), Vector()) / len(f)
                        back[cell] = [0, [round(x, 3) for x in frame.lift(fc)], [round(x, 2) for x in frame.lift(n)]]
                    back[cell][0] += 1
    return step, rays, back


def audit(built, out_dir):
    """The holes the game would show (Gate 2): Unity culls back faces, so wherever the first surface a view ray meets
    faces away from it the game draws what is behind it (an open box, a skipped face in view, a flipped face). Every
    asset's LODs, then each tower head on its mast and base as the game stacks them (a mast section's open ends meet
    its neighbours there); hits grouped by part and 0.25 m cell (lift frame) into tbar_audit.json."""
    global _LOD
    report = {}

    def record(name, objs, graded, lod):
        step, rays, back = holes(objs, graded, lod)
        cells = sorted(((k, n) for k, n in back.items() if n[0] >= 3), key=lambda kn: -kn[1][0])
        total = sum(n[0] for n in back.values())
        report[name] = {"stepMm": round(step * 1000), "raysHit": rays, "backHits": total,
                        "cells": [{"part": k[0], "uvw": k[1:], "hits": n[0], "face": n[1], "normal": n[2]} for k, n in cells[:40]]}
        print(f"Audit {name}: {total} back-face hits of {rays}"
              + (f", worst {cells[0][0][0]} at {cells[0][0][1:]} ({cells[0][1][0]})" if cells else ""), flush=True)

    for key, b in sorted(built.items()):
        if key.startswith(T):
            for lod in range(len(b["assets"])):
                record(f"{key} LOD{lod}", rr.lod_objects(b, lod), key[len(T):] in GRADED, lod)
    for head in ("tower_s4", "tower_s6", "tower_d4"):
        for lod in range(4):
            _LOD = lambda aid, at, k=lod: k
            objs, _ = tower(built, T + head, 0.0, 5.4)
            _LOD = None
            record(f"{T}{head} on its mast LOD{lod}", objs, True, lod)
            for o in objs:
                bpy.data.objects.remove(o)
    with open(os.path.join(out_dir, "tbar_audit.json"), "w") as f:
        json.dump(report, f, indent=1)


def game_lods(eye, bias=1.0):
    """An _LOD for a game camera at lift point `eye` (a GAME_FOV view) with QualitySettings.lodBias `bias`: LiftImport
    puts each switch at its budgets.json distance for that view at bias 1."""
    budgets = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "budgets.json"), encoding="utf-8"))
    until = {T + n: [l["untilM"] for l in budgets[k]["lods"]] for n, k in KINDS.items()}

    def pick(aid, at):
        d = (Vector(at) - eye).length / bias
        return next((k for k, m in enumerate(until[aid]) if d <= m), len(until[aid]) - 1)
    return pick


def _eye(target, az, el, dist):
    a, e = math.radians(az), math.radians(el)
    return Vector(target) + Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e))) * dist


def lod_row(name, at_lod, out_dir, keep, az=235.0, el=22.0):
    """One model's LODs side by side (LOD0 on the left of the image, so laid out from LOD3 up along u), with `keep` (the
    snow and the sun), in an orthographic view framing what stands above the snow: at_lod(lod, u) places one copy and
    returns its objects."""
    global _LOD
    objs, u = [], -150.0
    for lod in (3, 2, 1, 0):
        _LOD = lambda aid, at, k=lod: k
        made = at_lod(lod, u)
        _LOD = None
        bpy.context.view_layer.update()
        lo, hi = rr.bounds_lift(made)
        for o in made:   # each copy starts 2 m past the last
            o.matrix_world = Matrix.Translation(frame.b((u - lo.x, 0.0, 0.0))) @ o.matrix_world
        objs += made
        u += hi.x - lo.x + 2.0
    rr.show_only(objs + keep)
    rr.restore_uv(objs)
    bpy.context.view_layer.update()
    lo, hi = rr.bounds_lift(objs)
    lo.z = max(lo.z, -0.3)   # not the terminals' piers under the snow
    centre = (lo + hi) / 2
    d = _eye((0.0, 0.0, 0.0), az, el, 1.0)
    right = Vector((-d.y, d.x, 0.0)).normalized()
    up = d.cross(right)
    pts = [Vector((x, y, z)) for x in (lo.x, hi.x) for y in (lo.y, hi.y) for z in (lo.z, hi.z)]
    wx = max(abs((p - centre).dot(right)) for p in pts) * 2
    wy = max(abs((p - centre).dot(up)) for p in pts) * 2
    sc = bpy.context.scene
    rx, ry = 2000, 700
    sc.render.resolution_x, sc.render.resolution_y = rx, ry
    cam = rr.camera("LodRowCam")
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = max(wx, wy * rx / ry) * 1.08
    cam.data.clip_start, cam.data.clip_end = 0.1, 400
    cam.location = frame.b(tuple(centre + d * 100.0))
    cam.rotation_euler = (frame.b(tuple(centre)) - cam.location).to_track_quat("-Z", "Y").to_euler()
    rr.render(os.path.join(out_dir, f"tbar_lods_{name}.png"))
    sc.render.resolution_x, sc.render.resolution_y = 1600, 1066
    for o in objs:
        bpy.data.objects.remove(o)


def gate2(built, out_dir, mats):
    """Gate 2: the line as the game draws it (back faces culled, the game's LODs at its distances), after snowfall,
    and each model's LODs side by side."""
    materials.cull_backfaces(mats)
    sc, sun = rr._photo_scene()
    grade = scene_grade(built)
    snow = hill(grade, "PhotoHill", rr._photo_material("PhotoSnowMat", (0.88, 0.91, 0.95), 0.55), half=1500.0)
    fig_mat = rr._photo_material("PhotoFigureMat", (0.12, 0.28, 0.62), 0.6)
    _shoot = rr._shooter("tbar", out_dir)
    cam = bpy.data.objects["PhotoCam"]

    def scene(pick=None):
        global _LOD
        _LOD = pick
        objs, figs = line_scene(built)
        jo, jf = jbar_line(built)
        _LOD = None
        for f in figs + jf:
            f.data.materials.clear()
            f.data.materials.append(fig_mat)
        made = objs + jo + figs + jf
        rr.show_only(made + [snow, sun])
        rr.restore_uv(made)
        return made

    def over(target):   # targets stand over the hill
        return (target[0], target[1], target[2] + grade(target[0]))

    made = scene()
    for name, target, az, el, dist, lens in (
            ("hero", (45.0, 0.0, 3.0), 205, 10, 40.0, 35), ("drive", (1.5, 0.0, 3.0), 35, 16, 12.0, 50),
            ("return", (U_TOP - 1.5, 0.0, 4.0), 215, 16, 12.0, 50), ("tower", (30.0, 0.0, 5.6), 130, 10, 6.0, 50),
            ("jbar", (15.5, 16.0, 2.5), 140, 8, 7.0, 40)):
        _shoot(name, over(target), az, el, dist, lens=lens)
    materials.set_snow(mats, 1.0)   # framed as "drive", to compare
    _shoot("snowfall", over((1.5, 0.0, 3.0)), 35, 16, 12.0)
    materials.set_snow(mats, 0.0)
    for o in made:
        bpy.data.objects.remove(o)

    # the game's camera: a GAME_FOV tall view, each model at the LOD Low quality shows at its distance
    cam.data.sensor_fit, cam.data.sensor_height = "VERTICAL", 24.0
    lens = 12.0 / math.tan(math.radians(GAME_FOV / 2))
    for name, target, az, el, dist in (("game_far", (45.0, 0.0, 3.0), 232, 35, 150.0),
                                       ("game_mid", (12.0, 0.0, 3.0), 215, 30, 50.0)):
        made = scene(game_lods(_eye(over(target), az, el, dist)))
        _shoot(name, over(target), az, el, dist, lens=lens)
        for o in made:
            bpy.data.objects.remove(o)
    cam.data.sensor_fit = "AUTO"

    # each model's LODs side by side on level snow (rope stubs through the towers and the carriers' grips)
    snow.hide_render = True
    snow = rr._snow_plane(0.0)
    c = built[T + "return"]["spec"]["common"]
    hg, rope_d = c["lineGauge"] / 2000, c["ropeDiameter"]

    def stub(u, v, w, half=1.0):
        return rr._rope_curve(f"LodRope{len(bpy.data.curves)}", [(u - half, v, w), (u + half, v, w)], rope_d)

    lod_row("drive", lambda lod, u: place(built[T + "drive"], (u, 0.0, 0.0)), out_dir, [snow, sun])
    lod_row("return", lambda lod, u: place(built[T + "return"], (u, 0.0, 0.0)), out_dir, [snow, sun])

    def tower_at(lod, u):
        o, rw = tower(built, T + "tower_s6", u, 5.4)
        return o + [stub(u, s * hg, rw) for s in (-1, 1)]
    lod_row("tower", tower_at, out_dir, [snow, sun])

    def carriers_at(lod, u):
        o, _ = carrier(built, built[T + "bar_t"], (u, 0.0, 4.5), 0.0)
        oj, _ = carrier(built, built[T + "bar_j"], (u + 1.2, 0.0, 4.5), 0.0)
        return o + oj + [stub(u + 0.6, 0.0, 4.5, 1.2)]
    lod_row("carriers", carriers_at, out_dir, [snow, sun], el=12.0)
    rr._photo_end(sc, [snow])


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
    if "surface_audit" in shots:
        audit(built, out_dir)
    if "surface_gate2" in shots:
        gate2(built, out_dir, mats)
    print(f"Surface lift renders in {out_dir}", flush=True)
