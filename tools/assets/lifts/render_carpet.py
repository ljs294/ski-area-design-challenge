"""Review renders for the Glidepath magic carpet (build_lifts.py --render DIR --shots carpet_gate1,carpet_audit).

  carpet_gate1  Gate 1 (about ten images): orthographic end views of a module under its gallery bay (single, double) and
                side and plan views of the stations (<name>_meta.json beside each, as render_review's gate1, for the
                local dims sheet); Cycles photos of three carpets side by side up a hill (an uncovered single with side
                rails on part of it, a covered single and a covered double; modules hinged at their joints, straight
                runs joined by 1 deg curves, galleries with exit bays and a portal at each end, the control posts, the
                hut, a snow approach at each end): the hero, the bottom and top stations, a crest joint, a gallery
                from outside and from inside (single, double), the post and the hut, the bottom in a melt-out; and
                LOD rows (a station, a module with its bay and rails, the double's module and bay).
  carpet_audit  no images: the holes the game would show where it culls back faces (carpet_audit.json): every asset at
                every LOD as the game puts it (a module between its neighbours, a bay on its module, a station with
                its first module), counting only the asset's own faces.
  carpet_joints no images: every joint bent to the hardware's 2 deg, as a crest and a dip (carpet_joints.json, Gate 2):
                four modules hinged at the middle joint, with rails (the uncovered single) or bays (the covered single
                and double), at LOD0-2; rays from the back-face audit's views at the joint's band, a leak where a ray
                that meets the straight line there passes 0.15 m further through the bent one (and so do its four
                neighbours half a game pixel away, the pixel where that LOD shows), into the inside for a gallery.
  carpet_pops   no images: each LOD switch as the game's camera sees it (carpet_pops.json, Gate 2): every asset at each
                switch distance from eight views, one ray per pixel of a 1080-line 60 deg view, the share of the model's
                pixels whose silhouette changes (and that count per pixel of its outline: how far the outline moves),
                and of those whose surface turns by more than 30 deg.
  carpet_gate2  Gate 2 (Cycles, back faces culled as the game draws them): the hero after snowfall, a rail's sleeve
                coupler, the galleries in three accent colours (C16) and in a clearer glass, the game's camera far
                and mid with each model at the LOD it picks there, joints bent 2 deg (crest and dip; rails and the
                double's gallery), and the LOD rows.

The scene copies the built objects (LOD0, or the LOD asked for) and hides everything else; render_review's helpers do
the cameras and materials. The livery shows as a blue, as in the owner's photos (the player picks it in the game).
"""
import json
import math
import os

import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

import render_review as rr
import render_surface as rs
from liftkit import frame, materials

C1, C2 = "glidepath_carpet_", "glidepath_carpet2_"
LIVERY_HEX = "#2457B0"           # the review's livery (a blue, as photographed)
LIVERY = tuple(int(LIVERY_HEX[k:k + 2], 16) / 255 for k in (1, 3, 5))   # sRGB, for materials.set_livery
# each module's grade (deg), as the profile rule asks (owner C12): level ends, straight runs of 6 modules (40 ft) or
# more at one grade, joined by curves of 1 deg a joint; a sag to 6 deg, a crest to 4 deg, a curve to level
ANGLES = (0, 0, 1, 2, 3, 4, 5) + (6,) * 6 + (5,) + (4,) * 6 + (3, 2, 1, 0, 0)
LINES = ((C1, -7.0, False), (C1, 0.0, True), (C2, 7.5, True))   # (ids, v, covered)
RAILS = range(2, 9)              # the uncovered single's modules with side rails
EXITS = (8, 17)                  # the bays with exit doors
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


def approaches(built, pts, joints, step=0.25):
    """The earth the game grades at each end (the stations' dims "approach", owner C13), one heightfield per end
    under all the lines: level with each plate's top past its front edge (the top's run-off falling first), then
    easePct down to the hill, wider than the deck by margin each side, banked 1:1 back along the station. Heights
    are max(hill, pad), 2 mm proud of the hill where they meet it; faces only where a pad stands above the hill.
    Returns (u, v, w) grids, one per end."""
    ends = {"bottom": [], "top": []}
    for (prefix, covered), j in joints.items():
        for end in ends:
            st = built[prefix + end]["assets"][0]
            dm = st.dims
            ap, half = dm["approach"], st.dims["deckWidth"] / 2000
            beltTop = dm["beltTop"] / 1000
            ptop = dm["plateTop"] / 1000
            if end == "bottom":   # the station at u = 0, its plate's front edge at plateFrom, away toward -u
                edge, d, base = dm["plateFrom"] / 1000, -1.0, j[0].z - beltTop
            else:                 # turned round at the last joint: its plate's edge past it, away toward +u
                edge, d, base = j[-1].x + (dm["joint"] - dm["plateFrom"]) / 1000, 1.0, j[-1].z - beltTop
            ends[end].append((edge, d, base + ptop, j[0].y, half + ap["margin"] / 1000, ap))
    grids = []
    for end, pads in ends.items():
        def pad_at(u, v):
            best = -1e9
            for edge, d, w0, vc, hw, ap in pads:
                s_ = (u - edge) * d
                r, pct = ap.get("runOff", 0) / 1000, ap.get("runOffPct", 0) / 100
                L = r + ap["flat"] / 1000
                level = w0 - min(max(s_, 0.0), r) * pct
                side = max(0.0, abs(v - vc) - hw) * ap["easePct"] / 100
                if s_ < 0:   # beside the station: a 1:1 bank against its sides, the snow banked at the end only
                    drop = -s_ + side
                else:
                    drop = math.hypot(max(0.0, s_ - L), max(0.0, abs(v - vc) - hw)) * ap["easePct"] / 100
                best = max(best, level - drop)
            return best
        reach = max(1.0 + (w0 - grade_at(pts, edge)) / (ap["easePct"] / 100) for edge, d, w0, vc, hw, ap in pads)
        u_lo = min(min(e, e + d * (ap.get("runOff", 0) + ap["flat"]) / 1000) for e, d, w0, vc, hw, ap in pads) - reach
        u_hi = max(max(e, e + d * (ap.get("runOff", 0) + ap["flat"]) / 1000) for e, d, w0, vc, hw, ap in pads) + reach
        v_lo = min(vc - hw for e, d, w0, vc, hw, ap in pads) - reach
        v_hi = max(vc + hw for e, d, w0, vc, hw, ap in pads) + reach
        nu, nv = int((u_hi - u_lo) / step) + 1, int((v_hi - v_lo) / step) + 1
        us = [u_lo + (u_hi - u_lo) * i / (nu - 1) for i in range(nu)]
        vs = [v_lo + (v_hi - v_lo) * k / (nv - 1) for k in range(nv)]
        grid = [[(u, v, grade_at(pts, u), pad_at(u, v)) for v in vs] for u in us]
        grids.append((end, grid))
    return grids


def approach_mesh(grid, name, mat=None):
    verts, faces, index = [], [], {}
    rows = len(grid)
    cols = len(grid[0])
    for i in range(rows - 1):
        for k in range(cols - 1):
            q = [(i, k), (i + 1, k), (i + 1, k + 1), (i, k + 1)]
            if all(grid[a][b][3] <= grid[a][b][2] for a, b in q):
                continue
            ids = []
            for a, b in q:
                if (a, b) not in index:
                    u, v, nat, pad = grid[a][b]
                    index[(a, b)] = len(verts)
                    verts.append(tuple(frame.b((u, v, max(nat + 0.002, pad)))))
                ids.append(index[(a, b)])
            faces.append(ids)
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


def scene(built, lods=None):
    """The three carpets, the hut beside the uncovered single's bottom station (clear of its approach); (objects,
    ground profile, joints by line)."""
    objs, joints = [], {}
    for prefix, v, covered in LINES:
        if prefix + "bottom" not in built:
            continue
        o, j = carpet(built, prefix, v, covered, lods)
        objs += o
        joints[(prefix, covered)] = j
    hut = built.get(C1 + "hut")
    if hut:   # its door toward the carpet
        at = Vector((-1.6, LINES[0][1] - 6.6, 0.0))
        objs += place(hut, rot(yaw=-90.0), at, (lods or (lambda key, a: 0))("hut", at))
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


def riders(joints):
    """Riders on each line: one boarding, one on the belt, one stepping off."""
    figs = []
    for (prefix, covered), j in joints.items():
        v = j[0].y
        for at in ((-0.3, v, 0.37), (j[6].x, v, j[6].z), (j[-1].x + 2.4, v, j[-1].z + 0.02)):
            f = rr.figure(at).copy()   # render_review moves one figure about: a copy each
            bpy.context.scene.collection.objects.link(f)
            f.data = f.data.copy()
            f.data.materials.clear()
            f.data.materials.append(rr._photo_material("PhotoFigureMat", (0.62, 0.16, 0.12), 0.6))
            f.hide_render = False
            figs.append(f)
    return figs


def photos(built, out_dir, mats):
    sc, sun = rr._photo_scene()
    game_materials(mats)
    materials.set_livery(mats, LIVERY)
    objs, pts, joints = scene(built)
    snow_mat = rr._photo_material("PhotoSnowMat", (0.88, 0.91, 0.95), 0.55)
    snow = ground(pts, mat=snow_mat)
    pads = [approach_mesh(g, f"CarpetApproach_{end}", snow_mat) for end, g in approaches(built, pts, joints)]
    figs = riders(joints)
    rr.show_only(objs + figs + pads + [snow, sun])
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
    k = 13   # the crest joint between the 6 deg and 5 deg modules, uncovered single
    jc = joints[(C1, False)][k]
    shot("crest", jc + Vector((1.6, -2.6, 1.7)), jc + Vector((0.0, 0.0, 0.25)), lens=35)
    jg = js[EXITS[0]] + Vector((1.0, 0.0, 0.0))
    shot("gallery_out", jg + Vector((-2.5, -6.0, 1.8)), jg + Vector((1.0, 0.0, 0.6)), lens=35)
    a, b = js[3], js[8]
    shot("gallery_in", a + Vector((0.0, 0.0, 1.05)), b + Vector((0.0, 0.0, 0.7)), lens=24)
    a2, b2 = j2[3], j2[8]
    shot("double_in", a2 + Vector((0.0, -0.1, 1.15)), b2 + Vector((0.0, 0.3, 0.8)), lens=22)
    shot("double_portal", over(-5.0, j2[0].y + 4.5, 2.0), over(1.6, j2[0].y, 1.4), lens=32)
    shot("post_hut", over(-8.5, LINES[0][1] - 9.0, 2.2), over(-1.2, LINES[0][1] - 4.0, 0.9), lens=35)
    # the melt-out (C13): the same bottom view with the hill and the approaches bare, the earth ramps showing
    dirt = rr._photo_material("PhotoDirtMat", (0.30, 0.24, 0.17), 0.9)
    for o in [snow] + pads:
        o.data.materials[0] = dirt
    shot("meltout", over(-7.5, -6.0, 3.2), over(0.6, 1.5, 0.5), lens=30)
    for o in [snow] + pads:
        o.data.materials[0] = snow_mat
    rr._photo_end(sc, [snow] + pads + figs)
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
    row("double", lambda lod: place(built[C2 + "segment"], rot(), Vector(), lod) +
        place(built[C2 + "gallery_exit"], rot(), Vector(), lod), el=18.0)
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


# -- Gate 2 ---------------------------------------------------------------------------------------------------------
BEND = 2.0          # the hardware's limit per joint (deg); the game's profile rule holds curves to 1 (owner C12)
BAND = 0.06         # the joint's band (m either side of it): the sleeves, the panes' laps and the arches cover it
JOINT_LINES = (("rails", C1, False), ("single", C1, True), ("double", C2, True))   # (name, ids, covered)
KIND = {"top": "carpet_station", "bottom": "carpet_station", "gallery": "carpet_gallery",
        "gallery_exit": "carpet_gallery", "portal": "carpet_gallery", "post": "surface_hut", "hut": "surface_hut"}
PIXEL = 2.0 * math.tan(math.radians(rs.GAME_FOV / 2)) / 1080   # a 1080-line game view's pixel, per metre away
ACCENT_DARK = (0.13, 0.14, 0.15)   # LiftLivery.DarkAccent, the game's default accent
ACCENTS = (("dark", ACCENT_DARK, 0.75), ("light_blue", (0.61, 0.78, 0.91), 0.75), ("red", (0.72, 0.12, 0.09), 0.75),
           ("light_blue_clear", (0.61, 0.78, 0.91), 0.45))   # (name, accent sRGB, face-on glass opacity)


def _until():
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "budgets.json"), encoding="utf-8") as f:
        budgets = json.load(f)
    return {k: [l["untilM"] for l in v["lods"]] for k, v in budgets.items() if isinstance(v, dict) and "lods" in v}


def game_pick(eye, bias=1.0):
    """A scene() lods for a game camera at lift point eye (Low quality, lodBias bias), as render_surface.game_lods: each
    model at the LOD whose budgets.json distance holds it there."""
    until = _until()

    def pick(name, at):
        ds = until[KIND.get(name, "carpet_segment")]
        d = (Vector(at) - Vector(eye)).length / bias
        return next((k for k, m in enumerate(ds) if d <= m), len(ds) - 1)
    return pick


def hinged(built, prefix, covered, bend, lod=0):
    """Four modules hinged about the joint at u = 0 on the belt top (A0, A | B, B0), A's pair climbing at bend/2 and B's
    at -bend/2 (a crest for bend > 0, a dip below 0, straight at 0), each with its bay (covered) or its rails. Returns
    (objects, the joints' points from A0's back to B0's front)."""
    seg = built[prefix + "segment"]
    extra = built[prefix + ("gallery" if covered else "rail")]
    back, front = sock(seg, "back"), sock(seg, "front")
    J = Vector((0.0, 0.0, back.z))
    objs, pts = [], {0: J}
    for side, tilt in ((-1, bend / 2), (1, -bend / 2)):
        R = rot(tilt=tilt)
        step = R @ (front - back)
        for k in range(2):
            t = J - R @ front - step * k if side < 0 else J - R @ back + step * k
            for b in (seg, extra):
                objs += place(b, R, t, lod)
            pts[side * (k + 1)] = t + R @ (back if side < 0 else front)
    return objs, [pts[k] for k in sorted(pts)]


def _tree(objs, ground=None):
    """A BVH over objs (world space), its vertices and the owner of each face; ground: a square of snow at grade this
    far round the origin each way (owner None, its four vertices last)."""
    verts, polys, owner = [], [], []
    for o in objs:
        if o.type != "MESH":
            continue
        base = len(verts)
        verts += [o.matrix_world @ v.co for v in o.data.vertices]
        for f in o.data.polygons:
            polys.append([base + i for i in f.vertices])
            owner.append(o.name)
    if ground:
        g = len(verts)
        verts += [Vector((sx * ground, sy * ground, 0.0)) for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
        polys.append([g, g + 1, g + 2, g + 3])
        owner.append(None)
    return BVHTree.FromPolygons(verts, polys), verts, owner


def joint_audit(built, out_dir):
    """Every joint at the hardware's 2 deg (Gate 2): see carpet_joints in the module's docstring."""
    until = _until()
    report, leaks_all = {}, 0
    for name, prefix, covered in JOINT_LINES:
        if prefix + "segment" not in built or prefix + ("gallery" if covered else "rail") not in built:
            continue
        kind = "carpet_gallery" if covered else "carpet_segment"
        for lod in range(3):
            step = max(0.01, PIXEL * (until[kind][lod - 1] if lod else 0.0))
            straight, _ = hinged(built, prefix, covered, 0.0, lod)
            ts, verts, owner = _tree(straight, ground=30.0)
            lift = [frame.lift(v) for v in verts[:-4]]
            vs, ws = [p[1] for p in lift], [p[2] for p in lift]
            box = [frame.b((u, v, w)) for u in (-BAND, BAND) for v in (min(vs), max(vs)) for w in (0.0, max(ws))]
            c = sum(box, Vector()) / 8
            far = max((p - c).length for p in box) + 9.0
            bent = {}
            for label, b in (("crest", BEND), ("dip", -BEND)):
                objs, _ = hinged(built, prefix, covered, b, lod)
                bent[label] = _tree(objs, ground=30.0)[0]
                for o in objs:
                    bpy.data.objects.remove(o)
            for o in straight:
                bpy.data.objects.remove(o)

            def through(tree, o, d):   # the ray passes 0.15 m past where the straight line stops it
                hs, _, _, ds = ts.ray_cast(o, d, 2 * far)
                if hs is None:
                    return False
                hb, _, _, db = tree.ray_cast(o, d, 2 * far)
                return hb is None or db > ds + 0.15

            def inside(tree, p):   # p is closed in by the line: four of five rays (up, to each side, up to each side)
                return sum(tree.ray_cast(p, q, 6.0)[0] is not None
                           for q in (frame.b(x) for x in ((0, 0, 1), (0, 1, 0), (0, -1, 0), (0, 0.7, 0.7), (0, -0.7, 0.7)))) >= 4

            tested, res = 0, {k: {} for k in bent}
            for az, el in rs.AUDIT_DIRS:
                a, e = math.radians(az), math.radians(el)
                to_eye = frame.b((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
                e1 = to_eye.orthogonal().normalized()
                e2 = to_eye.cross(e1)
                xs, ys = [(p - c).dot(e1) for p in box], [(p - c).dot(e2) for p in box]
                for i in range(int(min(xs) / step) - 1, int(max(xs) / step) + 2):
                    for j in range(int(min(ys) / step) - 1, int(max(ys) / step) + 2):
                        o = c + to_eye * far + e1 * (i * step) + e2 * (j * step)
                        if o.z < 0.02:   # no eye under the snow
                            continue
                        hit, _, idx, ds = ts.ray_cast(o, -to_eye, 2 * far)
                        if hit is None or owner[idx] is None or abs(frame.lift(hit)[0]) > BAND:
                            continue
                        tested += 1
                        past = o - to_eye * (ds + 0.15)
                        for label, tb in bent.items():
                            if not through(tb, o, -to_eye):
                                continue
                            # a gallery leaks only where the ray gets inside the bent line (not a grazing ray sliding
                            # over the glass where an arch moved off it); open rails, wherever a ray gets through
                            if covered and not inside(tb, past):
                                continue
                            if all(through(tb, o + q * (step / 2), -to_eye) for q in (e1, -e1, e2, -e2)):
                                u, v, w = frame.lift(hit)
                                cell = (owner[idx].split(".")[0], round(v * 20) / 20, round(w * 20) / 20)
                                res[label][cell] = res[label].get(cell, 0) + 1
            for label, cells in res.items():
                n = sum(cells.values())
                leaks_all += n
                worst = sorted(cells.items(), key=lambda kv: -kv[1])
                report[f"{name} LOD{lod} {label}"] = {
                    "stepMm": round(step * 1000, 1), "raysAtJoint": tested, "leaks": n,
                    "cells": [{"part": k[0], "vw": k[1:], "rays": c} for k, c in worst[:20]]}
                print(f"Joints {name} LOD{lod} {label} {BEND:g} deg: {n} leaks of {tested} rays at the joint"
                      + (f", worst {worst[0][0]} ({worst[0][1]})" if worst else ""), flush=True)
    with open(os.path.join(out_dir, "carpet_joints.json"), "w") as f:
        json.dump(report, f, indent=1)
    print(f"Joints: {leaks_all} leaks in all", flush=True)


def pop_audit(built, out_dir, views=8, el=20.0):
    """Each LOD switch as the game's camera sees it (Gate 2): see carpet_pops in the module's docstring."""
    until = _until()
    report = {}
    for key in sorted(built):
        if not key.startswith((C1, C2)):
            continue
        name = key[len(C2) if key.startswith(C2) else len(C1):]
        b = built[key]
        ds = until[KIND.get(name, "carpet_segment")]
        trees = []
        for lod in range(len(b["assets"])):
            objs = place(b, rot(), Vector(), lod)
            trees.append(_tree(objs, ground=60.0))
            for o in objs:
                bpy.data.objects.remove(o)
        pts = trees[0][1][:-4]
        lo = Vector([min(p[i] for p in pts) for i in range(3)])
        hi = Vector([max(p[i] for p in pts) for i in range(3)])
        lo.z = max(lo.z, 0.0)
        c = (lo + hi) / 2
        corners = [Vector((x, y, z)) for x in (lo.x, hi.x) for y in (lo.y, hi.y) for z in (lo.z, hi.z)]
        rows = []
        for k in range(len(trees) - 1):
            dist = ds[k]
            flips, shade, shown, shifts = [], [], [], []
            for vi in range(views):
                a, e = math.radians(360.0 * vi / views + 15.0), math.radians(el)
                eye = c + frame.b((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e))) * dist
                fwd = (c - eye).normalized()
                right = fwd.cross(Vector((0.0, 0.0, 1.0))).normalized()
                up = right.cross(fwd)
                xs = [(p - eye).dot(right) / (p - eye).dot(fwd) / PIXEL for p in corners]
                ys = [(p - eye).dot(up) / (p - eye).dot(fwd) / PIXEL for p in corners]
                n_flip = n_shade = 0
                mask = set()
                for i in range(math.floor(min(xs)) - 1, math.ceil(max(xs)) + 2):
                    for j in range(math.floor(min(ys)) - 1, math.ceil(max(ys)) + 2):
                        d = (fwd + right * (i * PIXEL) + up * (j * PIXEL)).normalized()
                        hits = []
                        for tree, _, owner in (trees[k], trees[k + 1]):
                            h, n, idx, _ = tree.ray_cast(eye, d, dist * 3)
                            hits.append(n if h is not None and owner[idx] is not None else None)
                        if hits[0] is not None:
                            mask.add((i, j))
                        if (hits[0] is None) != (hits[1] is None):
                            n_flip += 1
                        elif hits[0] is not None and hits[0].dot(hits[1]) < math.cos(math.radians(30.0)):
                            n_shade += 1
                edge = sum(1 for i, j in mask if any(q not in mask for q in ((i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1))))
                shown.append(len(mask))
                flips.append(100.0 * n_flip / max(1, len(mask)))
                shade.append(100.0 * n_shade / max(1, len(mask)))
                shifts.append(n_flip / max(1, edge))
            rows.append({"from": k, "to": k + 1, "atM": dist, "pixels": round(sum(shown) / views),
                         "silhouettePct": round(max(flips), 1), "silhouetteMeanPct": round(sum(flips) / views, 1),
                         "surfacePct": round(max(shade), 1), "edgeShiftPx": round(max(shifts), 2)})
        report[key] = rows
        print(f"Pops {key}: " + ", ".join(f"{r['from']}->{r['to']} at {r['atM']:g} m ({r['pixels']} px) "
                                          f"silhouette {r['silhouettePct']} % ({r['edgeShiftPx']} px at the edge), "
                                          f"surface {r['surfacePct']} %" for r in rows), flush=True)
    with open(os.path.join(out_dir, "carpet_pops.json"), "w") as f:
        json.dump(report, f, indent=1)


def glass_opacity(mats, face_on=None):
    """The preview glasses' face-on opacity (materials.make: face_on + f^4 (0.97 - face_on)); None puts each glass back
    to its own (LiftGlass 0.75, LiftGlassClear 0.45: the game's)."""
    for m in mats:
        if "glassAlpha" not in m or m.node_tree.nodes.get("GlassFaceOn") is None:
            continue
        a = m["glassAlpha"] if face_on is None else face_on
        m.node_tree.nodes["GlassFaceOn"].inputs[0].default_value = a
        m.node_tree.nodes["GlassGrazing"].inputs[1].default_value = 0.97 - a


def gate2(built, out_dir, mats):
    """Gate 2's review renders (carpet_gate2 in the module's docstring)."""
    sc, sun = rr._photo_scene()
    game_materials(mats)
    materials.set_livery(mats, LIVERY)
    materials.set_accent(mats, ACCENT_DARK)
    snow_mat = rr._photo_material("PhotoSnowMat", (0.88, 0.91, 0.95), 0.55)
    cam = rr.camera("PhotoCam")
    cam.data.type = "PERSP"
    cam.data.sensor_fit, cam.data.sensor_width = "AUTO", 36
    cam.data.clip_start, cam.data.clip_end = 0.05, 900
    only = [s for s in os.environ.get("LIFT_PHOTOS", "").split(",") if s]

    def shot(name, eye, target, lens=35):
        if only and name not in only:
            return
        cam.data.lens = lens
        cam.location = frame.b(tuple(eye))
        cam.rotation_euler = (frame.b(tuple(target)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        rr.render(os.path.join(out_dir, f"carpet_gate2_{name}.png"))

    def dressed(lods=None):
        objs, pts, joints = scene(built, lods)
        snow = ground(pts, mat=snow_mat, half=400.0)
        pads = [approach_mesh(g, f"CarpetApproach_{end}", snow_mat) for end, g in approaches(built, pts, joints)]
        figs = riders(joints)
        rr.show_only(objs + figs + pads + [snow, sun])
        rr.restore_uv(objs)
        return objs + figs + pads + [snow], pts, joints

    made, pts, joints = dressed()

    def over(u, v, h):
        return Vector((u, v, grade_at(pts, u) + h))
    js = joints[(C1, True)]
    end = js[-1].x
    materials.set_snow(mats, 1.0)   # after snowfall, framed as Gate 1's hero
    shot("snowfall", over(-14.0, -24.0, 9.0), over(end * 0.45, 0.5, 1.0), lens=30)
    materials.set_snow(mats, 0.0)
    jr = joints[(C1, False)][5]     # a rail joint on the uncovered single (3 deg to 4 deg)
    rail = jr + Vector((0.0, -0.76, 0.75))
    shot("sleeve", rail + Vector((-0.9, -1.35, 0.35)), rail, lens=40)
    jg = js[6]
    for name, rgb, face_on in ACCENTS:   # the galleries from outside, riders inside, in each accent (C16)
        materials.set_accent(mats, rgb)
        glass_opacity(mats, face_on)
        shot(f"glass_{name}", jg + Vector((-1.5, -6.0, 1.3)), jg + Vector((2.5, 3.0, 0.6)), lens=30)
    materials.set_accent(mats, ACCENT_DARK)
    glass_opacity(mats)
    for o in made:
        bpy.data.objects.remove(o)

    # the game's camera (a GAME_FOV tall view at 1920 x 1080), each model at the LOD the game picks at its distance
    cam.data.sensor_fit, cam.data.sensor_height = "VERTICAL", 24.0
    sc.render.resolution_x, sc.render.resolution_y = 1920, 1080
    aim = Vector((end * 0.45, 0.5, grade_at(pts, end * 0.45) + 1.0))
    for name, az, el, dist in (("game_far", 232.0, 35.0, 150.0), ("game_mid", 215.0, 30.0, 50.0)):
        if only and name not in only:
            continue
        eye = rs._eye(aim, az, el, dist)
        made, _, _ = dressed(game_pick(eye))
        shot(name, eye, aim, lens=12.0 / math.tan(math.radians(rs.GAME_FOV / 2)))
        for o in made:
            bpy.data.objects.remove(o)
    cam.data.sensor_fit = "AUTO"
    sc.render.resolution_x, sc.render.resolution_y = 1600, 1066

    # joints bent to the hardware's 2 deg: the rails (their sleeves) and the double's gallery, crest and dip
    for name, prefix, covered, eye, aim in (
            ("rails", C1, False, Vector((-1.7, -2.5, 1.35)), Vector((0.0, -0.76, 0.62))),
            ("double", C2, True, Vector((-4.2, -5.0, 3.4)), Vector((0.0, 0.0, 1.5)))):
        for label, bend in (("crest", BEND), ("dip", -BEND)):
            if only and f"joint_{name}_{label}" not in only:
                continue
            objs, jp = hinged(built, prefix, covered, bend)
            back = sock(built[prefix + "segment"], "back").z
            prof = [(-40.0, jp[0].z - back)] + [(p.x, p.z - back) for p in jp] + [(40.0, jp[-1].z - back)]
            snow = ground(prof, half=40.0, name="JointSnow", mat=snow_mat)
            rr.show_only(objs + [snow, sun])
            rr.restore_uv(objs)
            shot(f"joint_{name}_{label}", jp[2] + eye, jp[2] + aim, lens=35)
            for o in objs + [snow]:
                bpy.data.objects.remove(o)
    rr._photo_end(sc, [])
    if not only:
        lod_rows(built, out_dir, mats)


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
    if "carpet_joints" in shots:
        joint_audit(built, out_dir)
    if "carpet_pops" in shots:
        pop_audit(built, out_dir)
    if "carpet_gate2" in shots:
        gate2(built, out_dir, mats)
    print(f"Carpet renders in {out_dir}", flush=True)
