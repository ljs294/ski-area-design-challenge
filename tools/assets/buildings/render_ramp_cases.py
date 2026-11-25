"""Gate 1 case renders for the unload ramps (loaded by build_buildings.py --cases DIR), Workbench only.

Each case in unloadramp.json `review.cases` stands its chair width's one ramp model (LOD0) on a solid block of ground
shaped to the case's site (raise, ground fall each way), so the terrain clips the ramp and its posts as the game
will. Beside it: the lift's own top return terminal (LOD0, its top head) and chairs from the committed lift models
(`--terminals DIR`; a grey stand-in from the spec's table if they are not on disk), the mini hut in its opening with
its legs part hidden, the operator's pedestal, the stop gate (a post and a bar past the wheel),
and a 1.8 m figure; the deck's boards are painted from the planks detail tile (plank_preview). Views per case:

  ramp_<case>_high.png  a 3/4 view from the hut side, a little above
  ramp_<case>_approach.png  the riders' approach, from down the lift line beside the up rope
  ramp_<case>_front.png  from down the ski-off, looking back up at the deck across both ropes
  ramp_<case>_column.png  from behind the down side: the terminal standing through the deck
  ramp_<case>_gate.png  the stop gate's post and bar past the wheel, from down the ski-off on the down side
  ramp_<case>_gateclose.png  the stop gate's post and pivot, close
  ramp_<case>_hut.png   the hut in its opening and the operator's pedestal
  ramp_<case>_walkway.png  the operator's cleated walkway, the ramp's outer strip, from out beside it
  ramp_<case>_pedestal.png  the operator's pedestal, close
  ramp_<case>_plan.png, _planbare.png  from above (the ski-off to the left, the hut side up), with the lift and
                        without it (the openings)
  ramp_<case>_side.png  an orthographic side view from the lift's centreline side (the ski-off to the left): the
                        block's face is the ground's section, so what is buried is hidden
  ramp_lods.png         the quad at its default site, LOD0 to LOD3 left to right, on ground at its level
  ramp_cases.json       each case's dims (slopes, runs, widths, deck area, cost)
"""
import json
import math
import os
import re
from types import SimpleNamespace

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

import render_review as rr
from bldgkit import lift_hut, op_pedestal, stop_gate, unload_ramp
from liftkit import export, frame, textures

lrr = rr.lrr
GREY, CLAY = (0.52, 0.53, 0.55, 1.0), (0.80, 0.78, 0.72, 1.0)


def _mat(name, rgba):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.diffuse_color = rgba
    return m


def _mesh_obj(name, verts_lift, faces, mat, coll):
    me = bpy.data.meshes.new(name)
    me.from_pydata([frame.b(p) for p in verts_lift], [], faces)
    me.update()
    o = bpy.data.objects.new(name, me)
    o.data.materials.append(mat)
    coll.objects.link(o)
    return o


def ground_block(name, R, fall, A, B, coll, u_lim=46.0, v0=-24.0, v1=24.0, step=1.0):
    """The site's ground as a solid: level at -R under the deck (u -A..B), falling `fall` per metre beyond each end;
    its top a grid, its face toward -v (the side view's section) and a floor."""
    def g(u):
        if u > B:
            return -R - fall[0] * (u - B)
        if u < -A:
            return -R - fall[1] * (-A - u)
        return -R
    us = sorted(set([-A, B] + [round(-u_lim + k * step, 3) for k in range(int(2 * u_lim / step) + 1)]))
    base = min(g(u) for u in us) - 7.0   # deeper than the posts' feet
    verts, faces = [], []
    for u in us:
        verts += [(u, v0, g(u)), (u, v1, g(u)), (u, v0, base)]
    for i in range(len(us) - 1):
        a, b = 3 * i, 3 * (i + 1)
        faces.append((a, b, b + 1, a + 1))           # top
        faces.append((a + 2, b + 2, b, a))           # the section face at v0
    return _mesh_obj(name, verts, faces, _mat("RampClay", CLAY), coll)


def mound_block(name, R, spec, coll, u_lim=30.0, v0=-24.0, v1=24.0, step=0.5):
    """Kind A, an earth mound under 5 ft (graded by the game in phase 6): the timber ramp's plan in earth, its top
    level at the unload over the deck and hut bay's footprint (both sides of the lift), one ski-off end as wide as the
    top falling at the ramp's slope with the same flare, its back and sides 1 in 2, down to the natural ground at -R;
    a solid with its section face toward -v."""
    A, B = spec["level"]["before"] / 1000, spec["level"]["after"] / 1000
    g2 = spec["gauge"] / 2000
    v_out = -(g2 - spec["pierClear"] / 1000) + spec["width"] / 1000
    v_far = -2 * g2 - v_out
    v_top = v_out + spec["hutBay"]["width"] / 1000
    s, fl = spec["slope"], spec["flare"]

    def h(u, v):
        x = max(0.0, u - B)                          # out along the ski-off
        back = max(0.0, -A - u)                      # behind the deck: a 1 in 2 face
        lo, hi = v_far - fl * x, v_top + fl * x       # on earth the ski-off spans the hut bay too
        dv = max(0.0, lo - v, v - hi)
        return max(-R, -s * x - 0.5 * (dv + back))
    us = [round(-u_lim + k * step, 3) for k in range(int(2 * u_lim / step) + 1)]
    vs = [round(v0 + k * step, 3) for k in range(int((v1 - v0) / step) + 1)]
    nv = len(vs)
    verts = [(u, v, h(u, v)) for u in us for v in vs]
    faces = [(i * nv + j, (i + 1) * nv + j, (i + 1) * nv + j + 1, i * nv + j + 1)
             for i in range(len(us) - 1) for j in range(nv - 1)]
    base = -R - 7.0   # deeper than the ramp's posts
    k0 = len(verts)
    verts += [(u, v0, base) for u in us]
    faces += [(i * nv, k0 + i, k0 + i + 1, (i + 1) * nv) for i in range(len(us) - 1)]
    return _mesh_obj(name, verts, faces, _mat("RampClay", CLAY), coll)


# each terminal style's lift models (tools/assets/lifts; the committed FBXs): its return per variant, the chair that
# rides it, and the parts of the heads it does not wear at the top (liftkit/swap.py: one head shows at a time)
MODELS = {
    "sessellift": {"return": {"quad": "sessellift_fgq4_return", "triple": "sessellift_fgt3_return",
                              "double": "sessellift_fgd2_return"},
                   "hide": r"(bottom_head|top6_|top8_|eq_|rocker_|yoke_|sheave_[lr][2-9])"},
    "stern": {"return": {"quad": "sessellift_stern4_return", "triple": "sessellift_stern3_return",
                         "double": "sessellift_stern2_return"}, "hide": r"(top6_|top8_)"},
    "monta": {"return": {"quad": "monta_fg4_return", "triple": "monta_fg3_return", "double": "monta_fg2_return"},
              "hide": r"$^"},
}
CHAIRS = {"quad": "sessellift_fgq4_chair", "triple": "sessellift_fgt3_chair", "double": "sessellift_fgd2_chair"}
MODELS_DIR = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..",
                                           "Assets", "MountainPlanner", "Art", "Lifts", "Models"))


def _import_fbx(path, coll, mat):
    """An FBX's objects, moved into `coll`, their meshes on the review's palette material; None if it is not there
    (or is still a Git LFS pointer)."""
    if not os.path.isfile(path) or os.path.getsize(path) < 4096:
        return None
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path)
    objs = [o for o in bpy.data.objects if o not in before]
    for o in objs:
        for c in list(o.users_collection):
            c.objects.unlink(o)
        coll.objects.link(o)
        if o.type == "MESH":
            o.data.materials.clear()
            o.data.materials.append(mat)
    return objs


def _base(o):
    """An imported object's own name (a second import of the same model gets a .001 suffix)."""
    return re.sub(r"\.\d+$", "", o.name)


def _move(objs, m):
    bpy.context.view_layer.update()
    for o in objs:
        if o.parent is None:
            o.matrix_world = m @ o.matrix_world


def real_terminal(spec, terminal, variant, coll, mat, models_dir):
    """The lift's own top return terminal (LOD0, its top head) and chairs from the lift models, placed in the ramp's
    frame by the terminal's `chair_unload` socket: one chair at the unload point, one arriving, one crossing the line
    at the far end of the wheel and one leaving. None if the models are not on disk."""
    mdl = MODELS[terminal]
    aid = mdl["return"][variant]
    objs = _import_fbx(os.path.join(models_dir, aid + ".fbx"), coll, mat)
    if objs is None:
        return None
    bpy.context.view_layer.update()
    socks = {_base(o)[len(aid) + 1:]: o.matrix_world.translation.copy() for o in objs if o.type == "EMPTY"}
    unload = next(socks[k] for k in ("socket_top_chair_unload", "socket_chair_unload") if k in socks)
    g2, rope = spec["gauge"] / 2000, spec["ropeOverDeck"] / 1000
    # the terminal's line runs toward its +u; the ramp's toward -u: half a turn about the unload point
    place = (Matrix.Translation(frame.b((0.0, 0.0, rope))) @ Matrix.Rotation(math.pi, 4, "Z")
             @ Matrix.Translation(-unload))
    _move(objs, place)
    hide = re.compile(mdl["hide"])
    shown = [o for o in objs if o.type == "MESH" and _base(o).endswith("_LOD0")
             and not hide.match(_base(o)[len(aid) + 1:])]
    wheel_u = spec["terminals"][terminal]["wheelU"] / 1000
    # (u, v, heading about w): travelling +u up the line, crossing the line toward -v, travelling -u back down
    for u, v, turn in ((-0.4, 0.0, 0.0), (-9.0, 0.0, 0.0), (wheel_u + g2, -g2, math.pi / 2), (-6.0, -2 * g2, math.pi)):
        ch = _import_fbx(os.path.join(models_dir, CHAIRS[variant] + ".fbx"), coll, mat)
        if ch is None:
            break
        _move(ch, Matrix.Translation(frame.b((u, v, rope))) @ Matrix.Rotation(turn, 4, "Z"))
        shown += [o for o in ch if o.type == "MESH" and _base(o).endswith("_LOD0")]
    box = _mesh_obj  # the ropes, back down the line (the terminal model ends at its rope sockets)
    m = _mat("StandIn", GREY)
    for v in (0.0, -2 * g2):
        lo, hi = Vector((-45.0, v - 0.022, rope - 0.022)), Vector((-7.5, v + 0.022, rope + 0.022))
        vs = [(x, y, z) for x in (lo.x, hi.x) for y in (lo.y, hi.y) for z in (lo.z, hi.z)]
        fs = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
        shown.append(box(f"Rope_{aid}_{v:.1f}", vs, fs, m, coll))
    return shown


def stand_in(spec, R, coll, terminal="sessellift"):
    """A grey top terminal where the lift models are not on disk: the terminal's columns from the spec's table, the
    bullwheel on its axle past the unload point at rope height, the two ropes running back down the line (-u), and
    chairs: one at the unload, one arriving, one leaving."""
    g2, rope = spec["gauge"] / 2000, spec["ropeOverDeck"] / 1000
    term = spec["terminals"][terminal]
    wu = term["wheelU"] / 1000
    m = _mat("StandIn", GREY)
    objs = []

    def box(name, lo, hi):
        lo, hi = Vector(lo), Vector(hi)
        vs = [(x, y, z) for x in (lo.x, hi.x) for y in (lo.y, hi.y) for z in (lo.z, hi.z)]
        fs = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
        objs.append(_mesh_obj(name, vs, fs, m, coll))
    for i, c in enumerate(term["through"]):
        box(f"SI_col{i}", (c[0] / 1000, -g2 - c[2] / 1000, -R - 0.5), (c[1] / 1000, -g2 + c[2] / 1000, rope + 0.6))
    box("SI_arm", (term["through"][0][0] / 1000, -g2 - 0.25, rope + 0.25), (wu + 0.3, -g2 + 0.25, rope + 0.6))
    n = 32                                           # the bullwheel: a thin disc
    vs = [(wu + g2 * math.cos(2 * math.pi * k / n), -g2 + g2 * math.sin(2 * math.pi * k / n), rope + dz)
          for dz in (0.0, 0.12) for k in range(n)]
    fs = [tuple(range(n)), tuple(range(2 * n - 1, n - 1, -1))] + [(k, (k + 1) % n, n + (k + 1) % n, n + k) for k in range(n)]
    objs.append(_mesh_obj("SI_bullwheel", vs, fs, m, coll))
    for v in (0.0, -2 * g2):                         # the ropes, back down the line
        box(f"SI_rope{v:.1f}", (-45.0, v - 0.02, rope - 0.02), (wu, v + 0.02, rope + 0.02))
    seat = rope - spec["chairBelowRope"] / 1000
    half = spec["chairWidth"] / 2000
    for i, (u, v) in enumerate(((-0.4, 0.0), (-9.0, 0.0), (-6.0, -2 * g2))):
        box(f"SI_hanger{i}", (u - 0.04, v - 0.04, seat + 0.45), (u + 0.04, v + 0.04, rope))
        box(f"SI_seat{i}", (u - 0.25, v - half, seat), (u + 0.25, v + half, seat + 0.1))
        box(f"SI_back{i}", (u - 0.32, v - half, seat + 0.1), (u - 0.25, v + half, seat + 0.65))
    return objs


def _srgb_to_lin(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


_PLANKS = {}


def _plank_material():
    """The planks detail tile as a colour picture for Workbench: the deck's palette colour x the tile's brightness and
    cavity x its normal under a fixed light from above (the game's shader does this from the atlas; review only)."""
    if "mat" in _PLANKS:
        return _PLANKS["mat"]
    from bldgkit import palette as bpal
    h, bright, cavity, strength = textures._tile("planks")
    rgba = textures._pack(h, bright, cavity, strength)
    n = np.stack([rgba[..., 0] * 2 - 1, rgba[..., 1] * 2 - 1, np.zeros(h.shape)], axis=-1)
    n[..., 2] = np.sqrt(np.clip(1 - n[..., 0] ** 2 - n[..., 1] ** 2, 0, 1))
    light = np.array([0.35, 0.45, 0.82])
    light /= np.linalg.norm(light)
    shade = 0.7 + 0.3 * np.clip(n @ light, 0, 1) / light[2]
    hexs = dict((c[0], c[1]) for c in bpal.CLASSES)["timber_deck"]
    base = np.array([_srgb_to_lin(int(hexs[i:i + 2], 16) / 255) for i in (1, 3, 5)])
    col = base[None, None, :] * (rgba[..., 3:4] * 2) * (rgba[..., 2:3] * 2) * shade[..., None]
    img = bpy.data.images.new("PlankPreview", textures.T, textures.T, alpha=False, float_buffer=True)
    img.colorspace_settings.name = "Linear Rec.709"
    px = np.ones((textures.T, textures.T, 4), np.float32)
    px[..., :3] = np.clip(col, 0, 1)
    img.pixels.foreach_set(px.ravel())
    m = bpy.data.materials.new("PlankPreview")
    m.use_nodes = True
    node = m.node_tree.nodes.new("ShaderNodeTexImage")
    node.image, node.extension, node.interpolation = img, "REPEAT", "Closest"
    m.node_tree.nodes.active = node
    m.node_tree.links.new(node.outputs["Color"], m.node_tree.nodes.get("Principled BSDF").inputs["Base Color"])
    _PLANKS["mat"] = m
    return m


def plank_preview(objs):
    """The faces drawn with the planks detail tile (its slot in UVMap's integer part), moved from each object into one
    of their own with the painted boards on them, so Workbench clay shows the planks as the game will (review only)."""
    tile, out = textures.TILE["planks"], []
    for o in objs:
        if "UVMap" not in o.data.uv_layers:
            continue
        dup = o.copy()
        dup.data = o.data.copy()
        dup.name = o.name + "_planks"
        for c in o.users_collection:
            c.objects.link(dup)
        for ob, keep in ((dup, True), (o, False)):
            bm = bmesh.new()
            bm.from_mesh(ob.data)
            uv = bm.loops.layers.uv["UVMap"]
            kill = [f for f in bm.faces if (math.floor(f.loops[0][uv].uv.x / 256.0) == tile) != keep]
            bmesh.ops.delete(bm, geom=kill, context="FACES")
            bm.to_mesh(ob.data)
            bm.free()
        dup.data.materials.clear()
        dup.data.materials.append(_plank_material())
        dup.data.uv_layers["UVMap"].active = True
        dup.data.uv_layers["UVMap"].active_render = True
        out.append(dup)
    return out


def pedestal_on_bay(socket, coll, mats, aid, kit=op_pedestal, spec_file="oppedestal.json", terminal=None):
    """The operator's pedestal at the ramp's `pedestal` socket (its head's face toward the hut side); with `kit`
    stop_gate, stopgate.json and the lift's `terminal`, its stop gate where unload_ramp.gate_at() puts it."""
    here = os.path.dirname(os.path.abspath(__file__))
    spec = json.load(open(os.path.join(here, spec_file), encoding="utf-8"))
    a = kit.build(kit.pick(spec, terminal) if terminal else spec, 0, asset_id=aid)
    objs, _, _ = export.make_objects([a], mats, coll)
    for o in objs:
        if o.parent is None:
            o.matrix_world = Matrix.Translation(frame.b(socket)) @ o.matrix_world
    return [o for o in objs if o.type == "MESH"], objs


def hut_on_bay(socket, coll, mats, aid):
    """The mini hut, its legs part hidden, turned to face -v (toward the deck) and stood on the hut bay."""
    here = os.path.dirname(os.path.abspath(__file__))
    hs = lift_hut.pick(json.load(open(os.path.join(here, "lifthut.json"), encoding="utf-8")), "mini")
    a = lift_hut.build(hs, 0, asset_id=aid)
    objs, _, _ = export.make_objects([a], mats, coll)
    body = [o for o in objs if o.type == "MESH" and o.name == f"{aid}_LOD0"]
    place = Matrix.Translation(frame.b(socket)) @ Matrix.Rotation(math.pi, 4, "Z")
    legs = [o for o in objs if "legs" in o.name]
    for o in body + [o for o in legs if o.type == "MESH" and o.name.endswith("_LOD0")]:
        o.matrix_world = place @ o.matrix_world
    return body, legs, objs


VIEWS = ("high", "approach", "front", "column", "gate", "gateclose", "hut", "walkway", "pedestal")


def case_scene(case, full, mats, models_dir, lift_mat=None, photo=False):
    """One review case's scene, in its own collection: the ramp (LOD0) for the case's chair width, its ground (Gate 1:
    the clay block, shaped to the site; photo: none, the caller lays the meadow and the snow), the lift's terminal and
    chairs (on `lift_mat`, else the buildings' material), the hut in its opening, the pedestal, the stop gate and
    figures (photo: in the photo figure's colour)."""
    spec = unload_ramp.pick(full, case["variant"])
    if case.get("slope"):                            # a case may try another slope (a slope comparison)
        spec["slope"] = case["slope"]
    site = {"raise": case["raise"], "fall": case.get("fall", [0.0, 0.0]),
            "terminal": case.get("terminal", spec["site"]["terminal"])}
    name = case["name"]
    coll = bpy.data.collections.new(("photo_" if photo else "case_") + name)
    bpy.context.scene.collection.children.link(coll)
    a = unload_ramp.build(spec, 0, asset_id="case_" + name)   # the same model on every site
    rep = unload_ramp.site_report(spec, site)
    R = site["raise"] / 1000
    A, B = spec["level"]["before"] / 1000, spec["level"]["after"] / 1000
    gnd = None
    if case.get("mound"):   # kind A: no ramp model, the ground graded to the same shape
        objs, ramp = [], []
        gnd = mound_block("Ground_" + name, R, spec, coll)
    else:
        objs, _, _ = export.make_objects([a], mats, coll)
        ramp = [o for o in objs if o.type == "MESH"]
        if not photo:
            gnd = ground_block("Ground_" + name, R, site["fall"], A, B, coll)
    si = real_terminal(spec, site["terminal"], case["variant"], coll, lift_mat or mats[0], models_dir)
    real = si is not None
    if not real:
        si = stand_in(spec, R, coll, site["terminal"])
    if photo:
        for o in si:
            if o.name.startswith("Rope_"):
                o.data.materials.clear()
                o.data.materials.append(lrr._rope_material())
    hs = a.sockets["hut"]   # on a mound the hut stands on the ground on its own skids
    hut, legs, hut_objs = hut_on_bay((hs[0], hs[1], 0.0) if case.get("mound") else hs, coll, mats,
                                     "case_hut_" + name)
    if case.get("mound"):
        hut = hut + [o for o in legs if o.type == "MESH" and o.name.endswith("_LOD0")]
    ped, ped_objs = pedestal_on_bay(a.sockets["pedestal"], coll, mats, "case_ped_" + name)
    fig = lrr._photo_figure if photo else lrr.figure
    figs = [fig((-0.6, 0.7, 0.0))]
    if "walkway" in a.sockets and not case.get("mound"):   # the operator on the walkway, 1.5 m out
        ws_ = a.sockets["walkway"]
        figs.append(fig((ws_[0] + 1.5, ws_[1] + 0.15, -spec["slope"] * 1.5)))
    gate, gate_objs = pedestal_on_bay(tuple(rep["gate"]), coll, mats, "case_gate_" + name, stop_gate,
                                      "stopgate.json", site["terminal"])
    return SimpleNamespace(spec=spec, site=site, name=name, a=a, rep=rep, R=R, A=A, B=B, objs=objs, ramp=ramp,
                           gnd=gnd, si=si, real=real, hut=hut, legs=legs, hut_objs=hut_objs, ped=ped,
                           ped_objs=ped_objs, gate=gate, gate_objs=gate_objs, figs=figs)


def view_of(view, c):
    """A named view of a case (eye, target, lens), in the ramp's frame; None where the case has no such part.
      high      a 3/4 view from the hut side, a little above
      approach  the riders' approach, from down the lift line beside the up rope
      front     from down the ski-off, a little off its middle, looking back up at the deck across both ropes
      column    from behind the down side, low: the terminal standing through the deck
      gate      the stop gate's post and bar past the wheel, from down the ski-off on the down side
      gateclose the stop gate's post and pivot, close
      hut       the hut in its opening and the pedestal, from over the ski-off on the hut side
      walkway   the operator's cleated walkway, from out beside it, a little ahead
      pedestal  the pedestal's control face and phone box, from where the operator stands
      hero      (Gate 2) a 3/4 view from over the ski-off on the down side, a little above: the deck, the terminal
                through it and the hut behind
      gateside  (Gate 2) the stop gate from the down side, back a little: its post, bar and the turning chair"""
    spec, rep, a, R, B = c.spec, c.rep, c.a, c.R, c.B
    reach = rep["run"] / 1000 + 1.5
    vc = -spec["gauge"] / 2000
    if view == "high":
        centre = Vector((0.0, vc + 1.0, -R * 0.5))
        d = Vector((math.cos(math.radians(62)), math.sin(math.radians(62)), 0.0)) * math.cos(math.radians(22))
        d.z = math.sin(math.radians(22))
        return centre + d * (reach * 1.45 + 5), centre, 30
    if view == "hero":
        centre = Vector((0.5, vc + 1.5, -R * 0.4))
        d = Vector((math.cos(math.radians(-38)), math.sin(math.radians(-38)), 0.0)) * math.cos(math.radians(24))
        d.z = math.sin(math.radians(24))
        return centre + d * (reach * 1.2 + 9), centre, 32
    if view == "approach":
        return Vector((-(reach * 0.9 + 9), -3.0, 2.2 + max(0.0, R * 0.4))), Vector((1.5, 0.6, -R * 0.3)), 30
    if view == "front":
        return Vector((B + reach + 7.0, vc - 4.0, 3.0 + R * 0.3)), Vector((0.0, vc + 0.5, -R * 0.35)), 30
    if view == "column":
        return Vector((-6.5, vc - 6.0, 2.4)), Vector((0.2, vc + 0.3, -0.2)), 35
    gs, w1 = Vector(rep["gate"]), rep["gateWindow"][1]
    if view == "gate":
        return Vector((w1 + 3.5, vc - 5.0, 1.8)), Vector(((gs.x + w1) / 2 + 0.3, vc, 0.3)), 35
    if view == "gateside":
        return gs + Vector((3.0, -9.5, 1.5)), Vector(((gs.x + w1) / 2, vc, 0.35)), 40
    if view == "gateclose":
        return gs + Vector((1.3, -1.1, 0.75)), gs + Vector((0.1, 0.0, 0.2)), 40
    if view == "hut":
        hs_ = Vector(a.sockets["hut"])
        return Vector((5.5, hs_.y + 5.5, 3.0)), Vector((-0.6, hs_.y - 0.9, 0.7)), 30
    if view == "walkway":
        if "walkway" not in a.sockets:
            return None
        wk_ = Vector(a.sockets["walkway"])
        return Vector((wk_.x + 6.5, wk_.y + 7.0, 2.0)), Vector((wk_.x + 1.0, wk_.y - 0.5, -R * 0.35)), 30
    if view == "pedestal":
        pd_ = Vector(a.sockets["pedestal"])
        return pd_ + Vector((0.35, 2.5, 1.55)), pd_ + Vector((0.0, 0.0, 0.85)), 50
    raise ValueError(f"no view named {view}")


def render_cases(built_all, out_dir, mats, models_dir=None):
    models_dir = models_dir or MODELS_DIR
    os.makedirs(out_dir, exist_ok=True)
    first = next(b for k, b in built_all.items() if k.startswith("ramp_"))
    cases = first["spec"]["review"]["cases"]
    here = os.path.dirname(os.path.abspath(__file__))
    full = json.load(open(os.path.join(here, "unloadramp.json"), encoding="utf-8"))
    pal = next(n.image for n in mats[0].node_tree.nodes if n.type == "TEX_IMAGE" and n.name == "PaletteTex")
    from liftkit import materials
    materials.use_palette(mats, materials.opaque_copy(pal))
    report = {}
    for case in cases:
        c = case_scene(case, full, mats, models_dir)
        spec, site, name, a, rep, R, A, B = c.spec, c.site, c.name, c.a, c.rep, c.R, c.A, c.B
        objs, ramp, gnd, si, real = c.objs, c.ramp, c.gnd, c.si, c.real
        hut, legs, hut_objs, ped, ped_objs, gate, gate_objs = c.hut, c.legs, c.hut_objs, c.ped, c.ped_objs, \
            c.gate, c.gate_objs
        shown = ramp + [gnd] + si + hut + ped + gate + c.figs
        pal_objs = ramp + hut + ped + gate + (si if real else [])
        report[name] = {"variant": case["variant"], "site": site, "mound": bool(case.get("mound")),
                        "terminalModel": real,
                        "dims": {**a.dims, **{k: v for k, v in rep.items() if k != "problems"}},
                        "problems": [] if case.get("mound") else a.problems + rep["problems"]}

        sc = rr._setup()
        sc.display.shading.show_shadows = True
        sc.render.film_transparent = False
        sc.display.shading.show_backface_culling = False   # the ground block is open-faced
        lrr.use_palette_uv(pal_objs)
        boards = plank_preview(ramp)
        shown += boards
        lrr.show_only(shown)
        cam = lrr.camera("CaseCam")
        cam.data.type = "PERSP"
        cam.data.lens = 30
        cam.data.clip_end = 800
        sc.render.resolution_x, sc.render.resolution_y = 1600, 1000
        reach = rep["run"] / 1000 + 1.5
        vc = -spec["gauge"] / 2000
        for view in VIEWS:
            v = view_of(view, c)
            if v is None:
                continue
            eye, look, cam.data.lens = v
            cam.location = frame.b(tuple(eye))
            cam.rotation_euler = (frame.b(tuple(look)) - cam.location).to_track_quat("-Z", "Y").to_euler()
            lrr.render(os.path.join(out_dir, f"ramp_{name}_{view}.png"))
        cam.data.lens = 30
        # the side: orthographic from -v, the block's face the ground's section
        span = 2 * reach + A + B + 6
        lrr.MM_PER_PX = 25.0
        lrr.place_ortho(cam, (-1.0, 0.0, 0.0), (0.0, 0.0, 1.0), ((B - A) / 2, 0.0, 0.8 - R / 2), span, R + 8.5)
        lrr.render(os.path.join(out_dir, f"ramp_{name}_side.png"))
        # the plan: orthographic from above (the ski-off to the left, the hut side up), with the lift and without
        lrr.MM_PER_PX = 20.0
        pc = ((B + reach - A - 3.0) / 2, vc + 3.0, 0.0)
        pw, ph_ = reach + A + B + 6.0, 26.0
        lrr.place_ortho(cam, (-1.0, 0.0, 0.0), (0.0, 1.0, 0.0), pc, pw, ph_)
        lrr.render(os.path.join(out_dir, f"ramp_{name}_plan.png"))
        lrr.show_only(ramp + boards + [gnd] + ped + gate)
        lrr.render(os.path.join(out_dir, f"ramp_{name}_planbare.png"))
        lrr.show_only(shown)
        lrr.MM_PER_PX = rr.MM_PER_PX
        lrr.restore_uv(pal_objs)
        for o in shown + legs + hut_objs + ped_objs + gate_objs + objs:
            o.hide_render = o.hide_viewport = True
    lod_lineup(full, out_dir, mats)
    with open(os.path.join(out_dir, "ramp_cases.json"), "w") as f:
        json.dump(report, f, indent=2)
    print(f"ramp cases: {len(cases)} rendered", flush=True)


def lod_lineup(full, out_dir, mats, variant="quad", gap=26.0):
    """ramp_lods.png: the quad at its default site, LOD0 to LOD3 side by side (left to right) on ground at its level,
    so the ramps show where they clip (the standard lineup stands models on z = 0, which buries them)."""
    spec = unload_ramp.pick(full, variant)
    site = spec["site"]
    R = site["raise"] / 1000
    A, B = spec["level"]["before"] / 1000, spec["level"]["after"] / 1000
    coll = bpy.data.collections.new("case_lods")
    bpy.context.scene.collection.children.link(coll)
    shown, meshes = [], []
    for lod in range(4):
        a = unload_ramp.build(spec, lod, asset_id=f"case_lods_{lod}")
        objs, _, _ = export.make_objects([a], mats, coll)
        for o in objs:
            if o.parent is None:
                o.location = frame.b((0.0, -lod * gap, 0.0))
        meshes += [o for o in objs if o.type == "MESH"]
    gnd = ground_block("Ground_lods", R, site["fall"], A, B, coll, u_lim=30.0, v0=-3 * gap - 12.0, v1=12.0)
    shown = meshes + [gnd]
    sc = rr._setup()
    sc.display.shading.show_shadows = True
    sc.render.film_transparent = False
    sc.display.shading.show_backface_culling = False
    lrr.use_palette_uv(meshes)
    shown += plank_preview([o for o in meshes if o.name.startswith("bldg_ramp") or "ramp" in o.name])
    lrr.show_only(shown)
    cam = lrr.camera("CaseCam")
    cam.data.type = "PERSP"
    cam.data.lens = 30
    cam.data.clip_end = 800
    sc.render.resolution_x, sc.render.resolution_y = 1600, 900
    centre = Vector((0.0, -1.5 * gap - 1.0, -R * 0.6))
    d = Vector((math.cos(math.radians(-20)), math.sin(math.radians(-20)), 0.0)) * math.cos(math.radians(32))
    d.z = math.sin(math.radians(32))
    cam.location = frame.b(tuple(centre + d * 115.0))
    cam.rotation_euler = (frame.b(tuple(centre)) - cam.location).to_track_quat("-Z", "Y").to_euler()
    lrr.render(os.path.join(out_dir, "ramp_lods.png"))
    lrr.restore_uv(meshes)
    for o in shown:
        o.hide_render = o.hide_viewport = True


def render_photos(built_all, out_dir, mats, models_dir=None, preview=False):
    """Gate 2 stills (Cycles, as the game draws them: back faces hidden, the real palette, the detail atlas's planks
    on the deck). unloadramp.json `review.photos.sets`: each a case (the chair width, its site and the lift's
    terminal) and its shots [shot, view (view_of), mode (day or snow)], saved as ramp_photo_<case>_<shot>.png. The
    ground is a meadow at the site's level (the ramp clips into it), snow in the snow shots. `preview` renders the
    same shots in Workbench, to check the framing without Cycles."""
    import render_photos as rp
    from liftkit import materials
    models_dir = models_dir or MODELS_DIR
    os.makedirs(out_dir, exist_ok=True)
    here = os.path.dirname(os.path.abspath(__file__))
    full = json.load(open(os.path.join(here, "unloadramp.json"), encoding="utf-8"))
    pal = bpy.data.images["lift_palette"]
    trim = bpy.data.images["lift_trim"]
    lift_mat = materials.make("RampLiftStructure", pal, detail_img=trim)   # the lift in its red livery
    all_mats = list(mats) + [lift_mat]
    materials.use_palette(all_mats, pal)
    rp._glow_nodes(all_mats)
    materials.cull_backfaces(all_mats)
    grass = lrr._photo_material("PhotoGrassMat", (0.16, 0.21, 0.10), 0.95)
    sets = full["common"]["review"]["photos"]["sets"]
    n = 0
    for st in sets:
        case = st["case"]
        c = case_scene(case, full, mats, models_dir, lift_mat=lift_mat, photo=True)
        far, g = 300.0, -c.R
        ground = [rp._plane("PhotoGrass_" + c.name, [(-far, -far, g - 0.01), (far, -far, g - 0.01),
                                                     (far, far, g - 0.01), (-far, far, g - 0.01)], grass)]
        snow = [lrr._snow_plane(g)]
        meshes = c.ramp + c.si + c.hut + c.ped + c.gate
        sc, sun = lrr._photo_scene()
        print(f"ramp photos {c.name}: Cycles on {rp._gpu(sc)}", flush=True)
        lrr.restore_uv(meshes)
        extras = ground + snow + [sun] + c.figs
        lrr.show_only(meshes + extras)
        if preview:
            wsc = rp.workbench_setup()
            wsc.render.resolution_x, wsc.render.resolution_y = 800, 533
            materials.use_palette(all_mats, materials.opaque_copy(pal))
            lrr.use_palette_uv(meshes)
        shoot = lrr._shooter("ramp", out_dir)
        cam = bpy.data.objects["PhotoCam"]
        for shot, view, mode in st["shots"]:
            rp._mode(sc, sun, mode, all_mats, ground, snow)
            eye, look, lens = view_of(view, c)
            d = Vector(eye) - Vector(look)   # the shooter's azimuth, elevation and distance from the view
            az = math.degrees(math.atan2(d.y, d.x))
            el = math.degrees(math.asin(d.z / d.length))
            shoot(f"{c.name}_{shot}", tuple(look), az, el, d.length, lens=lens)
            n += 1
        materials.set_snow(all_mats, 0.0)
        rp._set_glow(all_mats, 0.0, 0.0)
        lrr._photo_end(sc, extras)
        if preview:
            lrr.restore_uv(meshes)
            materials.use_palette(all_mats, pal)
        for o in meshes + extras + c.legs + c.hut_objs + c.ped_objs + c.gate_objs + c.objs:
            if o is not sun:
                o.hide_render = o.hide_viewport = True
    print(f"ramp photos: {n} rendered", flush=True)
