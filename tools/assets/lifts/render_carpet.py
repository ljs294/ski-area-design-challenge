"""Review renders for the Glidepath magic carpet (build_lifts.py --render DIR --shots carpet_gate1,carpet_audit).

  carpet_gate1  Gate 1 (about ten images): orthographic end views of a module under its gallery bay (single, double) and
                side and plan views of the stations (<name>_meta.json beside each, as render_review's gate1, for the
                local dims sheet); Cycles photos of three carpets side by side up a hill with a crest and a dip (an
                uncovered single with side rails on part of it, a covered single and a covered double; modules hinged
                at their joints at up to 2 deg, galleries with exit bays and a portal at each end, the control posts,
                the hut): the hero, the bottom and top stations, a crest joint, a gallery from outside and from inside
                (single, double), the post and the hut; and two LOD rows (a station, a module with its bay and rails).
  carpet_audit  no images: the holes the game would show where it culls back faces (carpet_audit.json): every asset at
                every LOD as the game puts it (a module between its neighbours, a bay on its module, a station with
                its first module), counting only the asset's own faces.

The scene copies the built objects (LOD0, or the LOD asked for) and hides everything else; render_review's helpers do
the cameras and materials. The livery shows as a blue, as in the owner's photos (the player picks it in the game).
"""
import json
import math
import os

import bpy
from mathutils import Matrix, Vector

import render_review as rr
import render_surface as rs
from liftkit import frame, materials

C1, C2 = "glidepath_carpet_", "glidepath_carpet2_"
LIVERY_HEX = "#2457B0"           # the review's livery (a blue, as photographed)
LIVERY = tuple(int(LIVERY_HEX[k:k + 2], 16) / 255 for k in (1, 3, 5))   # sRGB, for materials.set_livery
ANGLES = (0, 0, 2, 4, 6, 8, 8, 8, 8, 6, 4, 2, 2, 4, 6, 6, 6, 4, 2, 0, 0)   # each module's grade (deg): up, crest, dip, top
LINES = ((C1, -7.0, False), (C1, 0.0, True), (C2, 7.5, True))   # (ids, v, covered)
RAILS = range(2, 9)              # the uncovered single's modules with side rails
EXITS = (7, 15)                  # the bays with exit doors
B = Matrix(((0.0, -1.0, 0.0), (-1.0, 0.0, 0.0), (0.0, 0.0, 1.0)))   # lift frame <-> Blender (its own inverse)
TILED = ("segment", "segment_1m", "rail", "gallery", "gallery_exit")


def rot(tilt=0.0, yaw=0.0):
    """A turn in the lift frame: tilt raises +u toward +w (a module climbing), then yaw turns +u toward +v."""
    t, y = math.radians(tilt), math.radians(yaw)
    rt = Matrix(((math.cos(t), 0.0, -math.sin(t)), (0.0, 1.0, 0.0), (math.sin(t), 0.0, math.cos(t))))
    ry = Matrix(((math.cos(y), -math.sin(y), 0.0), (math.sin(y), math.cos(y), 0.0), (0.0, 0.0, 1.0)))
    return ry @ rt


def place(built, R, t, lod=0, tag=None):
    """Copies of one built asset's meshes at a LOD, turned by R (lift frame) and moved to t; tag prefixes their names."""
    m = (B @ R @ B).to_4x4()
    m.translation = frame.b(tuple(t))
    bpy.context.view_layer.update()
    made = []
    for src in rr.lod_objects(built, lod):
        o = src.copy()
        o.parent = None
        o.matrix_world = m @ src.matrix_world
        if tag:
            o.name = f"{tag}{src.name}"
        bpy.context.scene.collection.objects.link(o)
        made.append(o)
    return made


def sock(built, name):
    return Vector(built["assets"][0].sockets[name])


def carpet(built, prefix, v, covered, lods=None):
    """One carpet up the scene's profile: the bottom station at u = 0, the modules hinged at their joints (each at its
    ANGLES grade), the top station turned round at the last joint; covered: a bay on every module (exit bays at EXITS)
    and the portals; the uncovered single gets rails on RAILS. Returns (objects, joint points)."""
    pick = lods or (lambda key, at: 0)
    objs = []
    bot, top, seg = built[prefix + "bottom"], built[prefix + "top"], built[prefix + "segment"]
    t_bot = Vector((0.0, v, 0.0))
    objs += place(bot, rot(), t_bot, pick("bottom", t_bot))
    p = t_bot + sock(bot, "belt_out")
    joints = [p.copy()]
    back, front = sock(seg, "back"), sock(seg, "front")
    for k, ang in enumerate(ANGLES):
        R = rot(tilt=ang)
        t = p - R @ back
        objs += place(seg, R, t, pick("segment", p))
        if covered:
            objs += place(built[prefix + ("gallery_exit" if k in EXITS else "gallery")], R, t, pick("gallery", p))
        elif k in RAILS and prefix + "rail" in built:
            objs += place(built[prefix + "rail"], R, t, pick("rail", p))
        p = p + R @ (front - back)
        joints.append(p.copy())
    R_top = rot(yaw=180.0)
    t_top = p - R_top @ sock(top, "belt_in")
    objs += place(top, R_top, t_top, pick("top", t_top))
    if covered:   # the portals: at the first module's back joint, and turned round at the last one's front
        g = sock(built[prefix + "portal"], "gallery")
        objs += place(built[prefix + "portal"], rot(), joints[0] - g, pick("portal", joints[0]))
        objs += place(built[prefix + "portal"], R_top, joints[-1] - R_top @ g, pick("portal", joints[-1]))
    post = built.get(C1 + "post")
    if post:   # the control posts where the stations ask for them
        objs += place(post, rot(), t_bot + sock(bot, "post"), pick("post", t_bot))
        objs += place(post, R_top, t_top + R_top @ sock(top, "post"), pick("post", t_top))
    return objs, joints


def profile(joints, top_flat=60.0):
    """The ground under a carpet, (u, w) points: level at the bottom station, under each module's feet (the joints'
    points shifted down to grade, as the game cuts and fills the bed), level again at the top."""
    pts = [(-60.0, 0.0), (joints[0].x, 0.0)]
    beltTop = joints[0].z
    for k in range(1, len(joints) - 1):
        a = math.radians(ANGLES[k - 1])
        b = math.radians(ANGLES[k])
        s = (math.sin(a) + math.sin(b)) / 2
        c = (math.cos(a) + math.cos(b)) / 2
        pts.append((joints[k].x + beltTop * s, joints[k].z - beltTop * c))
    end = joints[-1]
    pts += [(end.x, end.z - beltTop), (end.x + top_flat, end.z - beltTop)]
    return pts


def ground(pts, half=60.0, name="CarpetHill", mat=None):
    verts = [tuple(frame.b((u, v, w))) for u, w in pts for v in (-half, half)]
    faces = [(2 * i, 2 * i + 1, 2 * i + 3, 2 * i + 2) for i in range(len(pts) - 1)]
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    for poly in me.polygons:
        if poly.normal.z < 0:
            poly.flip()
    me.update()
    o = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(o)
    if mat:
        o.data.materials.append(mat)
    return o


def grade_at(pts, u):
    for (u0, w0), (u1, w1) in zip(pts, pts[1:]):
        if u0 <= u <= u1:
            return w0 + (w1 - w0) * (u - u0) / (u1 - u0)
    return pts[-1][1]


def scene(built, lods=None):
    """The three carpets, the hut beside the uncovered single's bottom station; (objects, ground profile, joints by
    line)."""
    objs, joints = [], {}
    for prefix, v, covered in LINES:
        if prefix + "bottom" not in built:
            continue
        o, j = carpet(built, prefix, v, covered, lods)
        objs += o
        joints[(prefix, covered)] = j
    hut = built.get(C1 + "hut")
    if hut:   # its door toward the carpet
        objs += place(hut, rot(yaw=-90.0), Vector((-1.6, LINES[0][1] - 3.4, 0.0)))
    first = next(iter(joints.values()))
    return objs, profile(first), joints


# -- Gate 1 ---------------------------------------------------------------------------------------------------------
def game_materials(mats):
    """As the game draws them (LiftStructure.shader): back faces culled, and the glass (_LIFT_GLASS) casting no shadow,
    so a gallery's inside is lit as in the game (Cycles let the 75 % glass shade it nearly black, Gate 1 round 1)."""
    materials.cull_backfaces(mats)
    for m in mats:
        if not m.name.startswith("LiftGlass"):
            continue
        nt = m.node_tree
        out = next(n for n in nt.nodes if n.type == "OUTPUT_MATERIAL")
        link = out.inputs["Surface"].links[0]
        if link.from_node.name == "NoShadow":
            continue
        lp = nt.nodes.new("ShaderNodeLightPath")
        hole = nt.nodes.new("ShaderNodeBsdfTransparent")
        mix = nt.nodes.new("ShaderNodeMixShader")
        mix.name = "NoShadow"
        src = link.from_socket
        nt.links.remove(link)
        nt.links.new(lp.outputs["Is Shadow Ray"], mix.inputs["Fac"])
        nt.links.new(src, mix.inputs[1])
        nt.links.new(hole.outputs[0], mix.inputs[2])
        nt.links.new(mix.outputs[0], out.inputs["Surface"])


def section(objs, u, name="CarpetSection", half=0.003):
    """The cut through objs at u (lift frame) drawn as dark lines in that plane: a module's section for its end view
    (seen end on, its open ends show almost nothing)."""
    import bmesh
    quads = []
    for o in objs:
        bm = bmesh.new()
        bm.from_mesh(o.data)
        bm.transform(o.matrix_world)
        res = bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], dist=1e-6,
                                     plane_co=frame.b((u, 0.0, 0.0)), plane_no=frame.b((1.0, 0.0, 0.0)))
        for e in res["geom_cut"]:
            if not isinstance(e, bmesh.types.BMEdge):
                continue
            p0, p1 = (Vector(frame.lift(v.co)) for v in e.verts)
            d = p1 - p0
            if d.length < 1e-6:
                continue
            n = Vector((0.0, -d.z, d.y)).normalized() * half
            quads.append([p0 + n, p1 + n, p1 - n, p0 - n])
        bm.free()
    me = bpy.data.meshes.new(name)
    me.from_pydata([tuple(frame.b(tuple(p))) for q in quads for p in q], [],
                   [tuple(range(4 * i, 4 * i + 4)) for i in range(len(quads))])
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.diffuse_color = (0.04, 0.05, 0.09, 1.0)
    me.materials.append(mat)
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def drawings(built, out_dir):
    """Orthographic views for the dims sheet, at 3 mm a pixel: a module's end under its bay (single, double), with the
    module's section at mid-length, and the stations' side and plan; each with <name>_meta.json and _lines.json
    (render_review.ortho_views)."""
    scale = rr.MM_PER_PX
    rr.MM_PER_PX = 3.0
    for prefix in (C1, C2):
        if prefix + "segment" not in built:
            continue
        key = prefix + "segment"
        rr.ORTHO[key] = [("end", "+v", "+w")]
        rr.scene_setup()
        extras = place(built[prefix + "gallery"], rot(), Vector())
        extras.append(section(rr.lod_objects(built[key], 0), sock(built[key], "front").x / 2))
        rr.ortho_views(key, built[key], out_dir, extras)
        for o in extras:
            bpy.data.objects.remove(o)
        for end in ("top", "bottom"):
            key = prefix + end
            rr.ORTHO[key] = [("side", "+u", "+w"), ("plan", "+u", "-v")]
            rr.scene_setup()
            rr.ortho_views(key, built[key], out_dir)
    rr.MM_PER_PX = scale


def photos(built, out_dir, mats):
    sc, sun = rr._photo_scene()
    game_materials(mats)
    materials.set_livery(mats, LIVERY)
    objs, pts, joints = scene(built)
    snow = ground(pts, mat=rr._photo_material("PhotoSnowMat", (0.88, 0.91, 0.95), 0.55))
    figs = []
    for (prefix, covered), j in joints.items():   # riders: one boarding, one on the belt, one stepping off
        v = j[0].y
        for at in ((-0.3, v, 0.37), (j[6].x, v, j[6].z), (j[-1].x + 2.4, v, j[-1].z + 0.02)):
            f = rr.figure(at).copy()   # render_review moves one figure about: a copy each
            bpy.context.scene.collection.objects.link(f)
            f.data = f.data.copy()
            f.data.materials.clear()
            f.data.materials.append(rr._photo_material("PhotoFigureMat", (0.62, 0.16, 0.12), 0.6))
            f.hide_render = False
            figs.append(f)
    rr.show_only(objs + figs + [snow, sun])
    rr.restore_uv(objs)
    cam = rr.camera("PhotoCam")
    cam.data.type = "PERSP"
    cam.data.sensor_width = 36
    cam.data.clip_start, cam.data.clip_end = 0.05, 800
    only = [s for s in os.environ.get("LIFT_PHOTOS", "").split(",") if s]

    def shot(name, eye, target, lens=35):
        if only and name not in only:
            return
        cam.data.lens = lens
        cam.location = frame.b(tuple(eye))
        cam.rotation_euler = (frame.b(tuple(target)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        rr.render(os.path.join(out_dir, f"carpet_photo_{name}.png"))

    def over(u, v, h):   # a point h above the ground at u
        return Vector((u, v, grade_at(pts, u) + h))
    js = joints[(C1, True)]
    j2 = joints.get((C2, True), js)
    end = js[-1].x
    shot("hero", over(-14.0, -24.0, 9.0), over(end * 0.45, 0.5, 1.0), lens=30)
    shot("bottom", over(-7.5, -6.0, 3.2), over(0.6, 1.5, 0.5), lens=30)
    shot("top", over(end + 9.0, 7.0, 3.6), over(end + 1.0, 0.0, 0.4), lens=30)
    k = 9   # the crest joint between the 8 deg and 6 deg modules, uncovered single
    jc = joints[(C1, False)][k]
    shot("crest", jc + Vector((1.6, -2.6, 1.7)), jc + Vector((0.0, 0.0, 0.25)), lens=35)
    jg = js[EXITS[0]] + Vector((1.0, 0.0, 0.0))
    shot("gallery_out", jg + Vector((-2.5, -6.0, 1.8)), jg + Vector((1.0, 0.0, 0.6)), lens=35)
    a, b = js[3], js[8]
    shot("gallery_in", a + Vector((0.0, 0.0, 1.05)), b + Vector((0.0, 0.0, 0.7)), lens=24)
    a2, b2 = j2[3], j2[8]
    shot("double_in", a2 + Vector((0.0, -0.1, 1.15)), b2 + Vector((0.0, 0.3, 0.8)), lens=22)
    shot("double_portal", over(-5.0, j2[0].y + 4.5, 2.0), over(1.6, j2[0].y, 1.4), lens=32)
    shot("post_hut", over(-6.5, LINES[0][1] - 6.5, 1.9), over(-1.5, LINES[0][1] - 2.0, 1.0), lens=35)
    rr._photo_end(sc, [snow] + figs)
    for o in objs + figs:
        bpy.data.objects.remove(o)


def lod_rows(built, out_dir, mats):
    """Each LOD side by side (LOD0 on the left): the single's top station; the covered single's module with its exit
    bay beside the uncovered one with its rails."""
    materials.set_livery(mats, LIVERY)
    sc, sun = rr._photo_scene()
    snow = rr._snow_plane(0.0)
    keep = [snow, sun]

    def row(name, at_lod, el=22.0):
        objs, u = [], -150.0
        for lod in (3, 2, 1, 0):
            made = at_lod(lod)
            bpy.context.view_layer.update()
            lo, hi = rr.bounds_lift(made)
            for o in made:
                o.matrix_world = Matrix.Translation(frame.b((u - lo.x, 0.0, 0.0))) @ o.matrix_world
            objs += made
            u += hi.x - lo.x + 1.5
        rr.show_only(objs + keep)
        rr.restore_uv(objs)
        bpy.context.view_layer.update()
        lo, hi = rr.bounds_lift(objs)
        lo.z = max(lo.z, -0.3)
        centre = (lo + hi) / 2
        d = rs._eye((0.0, 0.0, 0.0), 235.0, el, 1.0)
        right = Vector((-d.y, d.x, 0.0)).normalized()
        up = d.cross(right)
        corners = [Vector((x, y, z)) for x in (lo.x, hi.x) for y in (lo.y, hi.y) for z in (lo.z, hi.z)]
        wx = max(abs((p - centre).dot(right)) for p in corners) * 2
        wy = max(abs((p - centre).dot(up)) for p in corners) * 2
        rx, ry = 2000, 700
        sc.render.resolution_x, sc.render.resolution_y = rx, ry
        cam = rr.camera("LodRowCam")
        cam.data.type = "ORTHO"
        cam.data.ortho_scale = max(wx, wy * rx / ry) * 1.08
        cam.data.clip_start, cam.data.clip_end = 0.1, 400
        cam.location = frame.b(tuple(centre + d * 100.0))
        cam.rotation_euler = (frame.b(tuple(centre)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        rr.render(os.path.join(out_dir, f"carpet_lods_{name}.png"))
        sc.render.resolution_x, sc.render.resolution_y = 1600, 1066
        for o in objs:
            bpy.data.objects.remove(o)
    row("station", lambda lod: place(built[C1 + "top"], rot(), Vector(), lod))

    def module(lod):
        made = place(built[C1 + "segment"], rot(), Vector(), lod) + place(built[C1 + "gallery_exit"], rot(), Vector(), lod)
        made += place(built[C1 + "segment"], rot(), Vector((0.0, -2.2, 0.0)), lod)
        made += place(built[C1 + "rail"], rot(), Vector((0.0, -2.2, 0.0)), lod)
        return made
    row("module", module, el=18.0)
    rr._photo_end(sc, [snow])


# -- the audit ------------------------------------------------------------------------------------------------------
def audit(built, out_dir):
    """Back-face holes (render_surface.holes, its view directions) per asset and LOD, as the game shows it: a module
    between its neighbours, a bay or rails on their module (and its neighbours), a station with its first module;
    only the asset's own faces counted."""
    report = {}

    def record(name, own, others, graded, lod):
        step, rays, back = rs.holes(own + others, graded, lod)
        mine = {o.name for o in own}
        cells = sorted(((k, n) for k, n in back.items() if k[0] in mine and n[0] >= 3), key=lambda kn: -kn[1][0])
        total = sum(n[0] for k, n in back.items() if k[0] in mine)
        report[name] = {"stepMm": round(step * 1000), "raysHit": rays, "backHits": total,
                        "cells": [{"part": k[0], "uvw": k[1:], "hits": n[0], "face": n[1], "normal": n[2]} for k, n in cells[:40]]}
        print(f"Audit {name}: {total} back-face hits" + (f", worst {cells[0][0][0]} at {cells[0][0][1:]} ({cells[0][1][0]})"
                                                          if cells else ""), flush=True)
        for o in own + others:
            bpy.data.objects.remove(o)

    for key in sorted(built):
        if not key.startswith((C1, C2)):
            continue
        name = key[len(C2) if key.startswith(C2) else len(C1):]
        prefix = C2 if key.startswith(C2) else C1
        b = built[key]
        for lod in range(len(b["assets"])):
            if name in TILED:
                L = sock(b, "front").x
                seg = built[prefix + ("segment_1m" if name == "segment_1m" else "segment")]
                own = place(b, rot(), Vector(), lod, tag="own_")
                others = []
                n = math.ceil(6.0 / L)   # neighbours 6 m each way: no ray gets in under a deck through an open end
                for du in [k * L for k in range(-n, n + 1) if k]:
                    others += place(b, rot(), Vector((du, 0.0, 0.0)), lod, tag="nb_")
                    if name not in ("segment", "segment_1m"):
                        others += place(seg, rot(), Vector((du, 0.0, 0.0)), lod, tag="nb_")
                if name not in ("segment", "segment_1m"):
                    others += place(seg, rot(), Vector(), lod, tag="nb_")
                record(f"{key} LOD{lod}", own, others, True, lod)
            elif name in ("top", "bottom"):
                own = place(b, rot(), Vector(), lod, tag="own_")
                seg = built[prefix + "segment"]
                j = sock(b, "belt_in" if name == "top" else "belt_out")
                others = []
                for k in range(4):   # its first modules, 8 m: no ray reaches it under the deck from the last one's end
                    others += place(seg, rot(), j - sock(seg, "back") + Vector((2.0 * k, 0.0, 0.0)), lod, tag="nb_")
                record(f"{key} LOD{lod}", own, others, True, lod)
            else:   # the portal, the post, the hut: on the snow
                own = place(b, rot(), Vector(), lod, tag="own_")
                record(f"{key} LOD{lod}", own, [], True, lod)
    with open(os.path.join(out_dir, "carpet_audit.json"), "w") as f:
        json.dump(report, f, indent=1)


def render_all(built, out_dir, shots, mats):
    out_dir = os.path.abspath(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    shots = (shots or "").split(",")
    if not any(k.startswith((C1, C2)) for k in built):
        return
    packed = bpy.data.images["lift_palette"]
    if "carpet_gate1" in shots:
        materials.use_palette(mats, materials.opaque_copy(packed, LIVERY_HEX))
        drawings(built, out_dir)
        materials.use_palette(mats, packed)
        photos(built, out_dir, mats)
        lod_rows(built, out_dir, mats)
    if "carpet_audit" in shots:
        audit(built, out_dir)
    print(f"Carpet renders in {out_dir}", flush=True)
